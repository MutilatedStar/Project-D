using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The numbers a room is judged by: how fast, how many points, how many gems. One of these is the default for every room;
/// RankSettings can hold different ones for particular rooms or stages.
///
///   TIME     seconds from the moment the players take control of the room until they are all on the door.
///            at or under Fast       -> FAST      (the top time tier)
///            at or under Medium     -> GOOD
///            at or under Slow       -> SLOW
///            over Slow              -> TOO SLOW
///   SCORE    the combined score of every player that counts, earned in THIS room (it starts from 0 in every room).
///            at or over High        -> HIGH      (the top score tier)
///            at or over Medium      -> MEDIUM
///            at or over Low         -> LOW
///            under Low              -> NONE
///   GEMS     how many ScorePickup gems were taken, out of how many the room has.
/// </summary>
[Serializable]
public class RankTargets
{
    // ------------------------------------------------------------------------------------------------------------
    // PROJECT D DEFAULTS. This is what a new RankSettings asset starts with, and what
    // Tools > Stages > Reset Default Targets To Project D puts back.
    //   Times are the same as before (60 / 90 / 150 s).
    //   Scores are about a third of what they were (1500 / 800 / 300 -> 500 / 250 / 100) because rooms now have far fewer collectables.
    // Remember: P needs EVERY gem as well as the High score, so High should never be more than the room can really pay out.
    // ------------------------------------------------------------------------------------------------------------

    [Header("Time (seconds)")]
    [Tooltip("At or under this the time is FAST. P, S and A all need a FAST time.")]
    [Min(1f)] public float fastSeconds = 60f;
    [Tooltip("At or under this the time is GOOD.")]
    [Min(1f)] public float mediumSeconds = 90f;
    [Tooltip("At or under this the time is SLOW. Over it the time is TOO SLOW.")]
    [Min(1f)] public float slowSeconds = 150f;

    [Header("Score (all players added together, this room only)")]
    [Tooltip("At or over this the score is HIGH. P and S need a HIGH score.")]
    [Min(0)] public int highScore = 500;
    [Tooltip("At or over this the score is MEDIUM. A needs at least a MEDIUM score.")]
    [Min(0)] public int mediumScore = 250;
    [Tooltip("At or over this the score is LOW. Under it the score is NONE.")]
    [Min(0)] public int lowScore = 100;

    [Header("Gems")]
    [Tooltip("How many gems the room has. -1 = count them automatically when the room starts (every uncollected ScorePickup that is closer to this room than to any other). " +
             "Type a number if the automatic count is wrong for this room. 0 = the room has no gems, so P only needs a fast time and a high score.")]
    [Min(-1)] public int totalCollectables = -1;

    /// <summary>Keeps Fast &lt;= Medium &lt;= Slow and High &gt;= Medium &gt;= Low, so a typo can't make a tier impossible to reach.</summary>
    public void Sanitize()
    {
        if (mediumSeconds < fastSeconds) mediumSeconds = fastSeconds;
        if (slowSeconds < mediumSeconds) slowSeconds = mediumSeconds;
        if (mediumScore > highScore) mediumScore = highScore;
        if (lowScore > mediumScore) lowScore = mediumScore;
    }

    /// <summary>A separate copy, so changing it never touches the original.</summary>
    public RankTargets Copy()
    {
        return (RankTargets)MemberwiseClone();
    }
}

/// <summary>
/// Different targets for one room (Room) of one stage (Stage Id), or for every stage's room N, or for one stage's every room.
///
/// TWO WAYS TO FILL IN A ROW
///   QUICK (the default for new rows)   type just a Fast time and a High score (and Gems only if the automatic count is wrong).
///                                      The other tiers (Good, Slow, Medium, Low) are worked out in the same proportions as the default targets.
///                                      A box left at 0 (Gems: -1) simply keeps the default.
///   ALL NUMBERS (Quick turned off)     every time and score is typed by hand, in 'targets'. This is how rows worked before.
/// </summary>
[Serializable]
public class RankTargetOverride
{
    [Tooltip("Just a name for this row in the Inspector. The game doesn't read it.")]
    public string label = "";
    [Tooltip("The stage this applies to (its Id: the object name in lower case with dashes, e.g. stage-3). Empty = any stage / the normal dungeon.")]
    public string stageId = "";
    [Tooltip("The room or level number this applies to, counting from 1 (the first room is 1, the sixth is 6; it is the number on the Level Select card). 0 = every room.")]
    [Min(0)] public int room = 0;

    [Tooltip("ON: only the three boxes below are used and the other tiers follow the shape of the default targets. " +
             "OFF: every number in 'targets' is used (the old way). Rows made before this option existed stay OFF so they keep working.")]
    public bool quick = false;

    [Tooltip("QUICK: the FAST time for this room, in seconds. P, S and A need it. GOOD and SLOW follow from it in the same proportions as the defaults. 0 = use the default.")]
    [Min(0f)] public float parSeconds = 0f;
    [Tooltip("QUICK: the HIGH score for this room. P and S need it. MEDIUM and LOW follow from it in the same proportions as the defaults. 0 = use the default. " +
             "Keep it at or under what the room can really pay out, because P also needs every gem.")]
    [Min(0)] public int topScore = 0;
    [Tooltip("QUICK: how many gems this room has. -1 = same as the default (counted automatically unless the default says otherwise). 0 = no gems.")]
    [Min(-1)] public int gems = -1;

    [Tooltip("ALL NUMBERS: used when Quick is OFF.")]
    public RankTargets targets = new RankTargets();

    /// <summary>A new Quick row for room number N (counting from 1). It changes nothing until a number is typed into it.</summary>
    public static RankTargetOverride ForRoom(int roomNumber)
    {
        RankTargetOverride row = new RankTargetOverride();
        row.label = "Room " + roomNumber;
        row.room = roomNumber;
        row.quick = true;
        return row;
    }

    /// <summary>A Quick row with nothing typed in it: it would give exactly the defaults, so it is ignored.</summary>
    public bool IsBlank
    {
        get { return quick && parSeconds <= 0f && topScore <= 0 && gems < 0; }
    }

    /// <summary>The targets this row means, given the default targets it is measured against.</summary>
    public RankTargets Resolve(RankTargets defaultTargets)
    {
        if (defaultTargets == null) defaultTargets = new RankTargets();
        if (!quick) return targets != null ? targets : defaultTargets;

        RankTargets r = defaultTargets.Copy();

        if (parSeconds > 0f)
        {
            float fast = Mathf.Max(1f, defaultTargets.fastSeconds);
            r.fastSeconds = Round1(parSeconds);
            r.mediumSeconds = Round1(parSeconds * defaultTargets.mediumSeconds / fast);
            r.slowSeconds = Round1(parSeconds * defaultTargets.slowSeconds / fast);
        }

        if (topScore > 0)
        {
            float high = Mathf.Max(1f, defaultTargets.highScore);
            r.highScore = topScore;
            r.mediumScore = Mathf.RoundToInt(topScore * (defaultTargets.mediumScore / high));
            r.lowScore = Mathf.RoundToInt(topScore * (defaultTargets.lowScore / high));
        }

        if (gems >= 0) r.totalCollectables = gems;

        r.Sanitize();
        return r;
    }

    private static float Round1(float value)
    {
        return Mathf.Round(value * 10f) / 10f;
    }
}

/// <summary>
/// ALL the numbers behind the ranks, in one asset you edit in the Inspector:  Assets/Resources/RankSettings.asset
/// (created for you; menu Tools > Stages > Rank Settings selects it).
///
/// SETTING UP A ROOM
///   Under "Targets per room" there is one row per room. Type two numbers in the row:
///     Fast s   the time for FAST (P, S and A all need it)       High   the score for HIGH (P and S need it)
///   Everything else follows from the default targets. Leave a box at 0 to keep the default. The grey text under the row shows what you got.
///   (If a row is missing, press "Add rows for rooms 1-N" under the table.)
///
/// THE RANKS (P is the best):
///   P   PERFECT      every gem + a FAST time + a HIGH score
///   S   WONDERFUL    a FAST time + a HIGH score, but some gems missed
///   A   GREAT        a FAST time + at least a MEDIUM score
///   B / C / D / F    everything else, by points:  time tier (0-3) + score tier (0-3)   ->   B at B Points or more, C at C Points, D at D Points, else F
///   So a slow clear can never be P, S or A, however many points it earned: time is part of every rank.
///
/// THE WIN SCREEN (after the last room) averages the room ranks into ONE overall rank for the team. P needs every room to be P. If ANY room
/// was never cleared, the overall rank is capped at Cap Rank When Missing (B by default), so P, S and A become impossible.
/// </summary>
[CreateAssetMenu(fileName = "RankSettings", menuName = "Stages/Rank Settings")]
public class RankSettings : ScriptableObject
{
    public const string ResourceName = "RankSettings";

    [Header("On / off")]
    [Tooltip("OFF: no results panel, no win screen, doors work as before.")]
    public bool enabled = true;

    [Header("The run")]
    [Tooltip("How many rooms the run has, COUNTING THE FIRST ROOM (room 0) AS ROOM ONE. 6 = rooms 0 to 5 = six rooms. Beating the last one shows the win screen. " +
             "For the dungeon this is the floor number of the last floor; for Level Select it is the stage number of the last stage.")]
    [Min(1)] public int roomCount = 6;

    [Header("Targets used by every room (unless a row below says otherwise)")]
    public RankTargets defaults = new RankTargets();

    [Header("Rank rules")]
    [Tooltip("Below a FAST time with a HIGH score you can still get B, C, D or F. A room scores time tier + score tier (each 0 to 3). " +
             "B needs at least this many points (of 6).")]
    [Range(1, 6)] public int bPoints = 4;
    [Tooltip("C needs at least this many points (of 6).")]
    [Range(1, 6)] public int cPoints = 3;
    [Tooltip("D needs at least this many points (of 6). Under it the rank is F.")]
    [Range(1, 6)] public int dPoints = 2;
    [Tooltip("S needs at least this share of the gems (0 = any, so S only needs a fast time and a high score). Under it a fast, high-scoring room is an A.")]
    [Range(0f, 1f)] public float sMinCollectFraction = 0f;

    [Header("Missing a room")]
    [Tooltip("The best overall rank possible when a room of the run was never cleared. B means P, S and A are impossible.")]
    public LevelRank capRankWhenMissing = LevelRank.B;

    [Header("Different targets for particular rooms or stages")]
    [Tooltip("The most specific row wins: a row with a stage AND a room beats a row with only a stage, which beats a row with only a room, which beats the defaults above. " +
             "Rows with nothing typed in them are ignored.")]
    public List<RankTargetOverride> overrides = new List<RankTargetOverride>();

    [Header("Level Select cards")]
    [Tooltip("ON: clearing floor N of the dungeon (or ending the run from it) also counts as clearing the Nth card on Level Select, with the same rank, score and time, " +
             "so playing through the dungeon fills in the cards instead of leaving them all 'not cleared'. Rooms cleared before are folded in when Level Select opens. " +
             "Needs dungeon floor N to be the same room as Level Select stage N; switch it OFF if your floors and stages are not in the same order.")]
    public bool dungeonCountsForLevelSelect = true;

    [Header("Screens")]
    [Tooltip("Exact name of the Level Select scene (the buttons on the screens use it). Capitals and spaces don't matter.")]
    public string levelSelectScene = "LevelSelect";
    [Tooltip("Exact name of the main menu scene.")]
    public string mainMenuScene = "Mainmenu";
    [Tooltip("Seconds the Continue / Retry / Level Select buttons can't be pressed after they appear, so a player still hammering a key at the door can't skip the results.")]
    [Min(0f)] public float buttonGraceSeconds = 0.6f;

    // ------------------------------------------------------------ lookup

    /// <summary>The targets for this stage's room: the most specific matching row, else the defaults. stageId may be empty (the dungeon).</summary>
    public RankTargets TargetsFor(string stageId, int room)
    {
        RankTargets baseTargets = defaults != null ? defaults : new RankTargets();
        RankTargetOverride best = null;
        int bestSpecificity = 0;

        for (int i = 0; overrides != null && i < overrides.Count; i++)
        {
            RankTargetOverride o = overrides[i];
            if (o == null || o.IsBlank) continue;
            if (!o.quick && o.targets == null) continue;

            bool stageRow = !string.IsNullOrEmpty(o.stageId);
            bool roomRow = o.room > 0;
            if (!stageRow && !roomRow) continue;   // matches everything: that is what 'defaults' is for
            if (stageRow && StageDefinition.MakeId(o.stageId) != StageDefinition.MakeId(stageId)) continue;
            if (roomRow && o.room != room) continue;

            int specificity = (stageRow ? 2 : 0) + (roomRow ? 1 : 0);
            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                best = o;
            }
        }

        return best == null ? baseTargets : best.Resolve(baseTargets);
    }

    // ------------------------------------------------------------ editing helpers (used by the Inspector and the Tools menu)

    /// <summary>How many of the rooms 1..Room Count have no row of their own (a row with no stage id).</summary>
    public int MissingRoomRows()
    {
        int missing = 0;
        for (int r = 1; r <= roomCount; r++)
        {
            if (!HasRoomRow(r)) missing++;
        }
        return missing;
    }

    /// <summary>Adds an empty Quick row for every room 1..Room Count that has none, and returns how many were added. Existing rows are never changed.</summary>
    public int EnsureRoomRows()
    {
        if (overrides == null) overrides = new List<RankTargetOverride>();

        int added = 0;
        for (int r = 1; r <= roomCount; r++)
        {
            if (HasRoomRow(r)) continue;
            overrides.Add(RankTargetOverride.ForRoom(r));
            added++;
        }
        return added;
    }

    /// <summary>Puts "Targets used by every room" back to the Project D numbers (see RankTargets). Rows for particular rooms are left alone.</summary>
    public void ResetDefaultTargets()
    {
        defaults = new RankTargets();
    }

    private bool HasRoomRow(int roomNumber)
    {
        for (int i = 0; overrides != null && i < overrides.Count; i++)
        {
            RankTargetOverride o = overrides[i];
            if (o != null && o.room == roomNumber && string.IsNullOrEmpty(o.stageId)) return true;
        }
        return false;
    }

    private void OnValidate()
    {
        if (defaults == null) defaults = new RankTargets();
        defaults.Sanitize();
        for (int i = 0; overrides != null && i < overrides.Count; i++)
        {
            if (overrides[i] == null) continue;
            if (overrides[i].targets == null) overrides[i].targets = new RankTargets();
            overrides[i].targets.Sanitize();
        }
        if (cPoints > bPoints) cPoints = bPoints;
        if (dPoints > cPoints) dPoints = cPoints;
    }

    // ------------------------------------------------------------ loading

    private static RankSettings current;
    private static bool warned;

    /// <summary>
    /// The settings asset (Assets/Resources/RankSettings.asset). If the project has none yet, a copy of the built-in defaults is used, so the
    /// ranks still work; Tools > Stages > Rank Settings creates the real asset.
    /// </summary>
    public static RankSettings Current
    {
        get
        {
            if (current != null) return current;

            current = Resources.Load<RankSettings>(ResourceName);
            if (current == null)
            {
                if (!warned)
                {
                    warned = true;
                    Debug.LogWarning("RankSettings: there is no Assets/Resources/RankSettings.asset, so the built-in default targets are used. " +
                                     "Open the menu  Tools > Stages > Rank Settings  to create it and set your own times and scores.");
                }
                current = CreateInstance<RankSettings>();
                current.hideFlags = HideFlags.HideAndDontSave;
            }
            current.OnValidate();
            return current;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        current = null;
        warned = false;
    }
}
