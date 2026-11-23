using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Ilumisoft.HealthSystem;
using Cainos.PixelArtTopDown_Basic;

/// <summary>
/// The foe side of the floor system. FloorController owns the floor NUMBER (doors, Shop trip, black screen,
/// teleport); this script FOLLOWS it:
///
///   FloorController.FloorChanged fires (players already moved, screen still black)
///        ->  FoeSpawner.StartLevel(floor - 1)  ->  foes of the new floor start spawning once the screen fades out.
///
/// So a door AND the Shop both end up here, and the spawner always matches the floor. After a trip to
/// the Shop the scene reloads; Start() then begins the spawner on the floor FloorController remembers (not on floor 1).
///
///   GoDownFloor()  ->  asks FloorController to run the full "go down" (black screen, teleport, new foes), so any script
///                      can send the players down without a door.
///
/// It also decides where foes are allowed to be, using ONE rule (IsValidFoePosition) in two places:
///
///   1. BEFORE a foe is created: the spawner asks this rule about every candidate spawn point, so a foe is never
///      created somewhere it would be removed from later. No pop-in then pop-out.
///   2. While foes are alive: a foe that stays outside the rule for "graceSeconds" is removed (safety net).
///
/// A point is VALID (a foe is never removed there) if ANY of these is true:
///   - it overlaps a collider on the "Protected Mask" layer(s)   <- never removed, under any conditions
///   - it is on a tile of one of the "Floor Tilemaps" (and over the "Floor Mask" layer, if you set one)
/// If nothing at all is set up (no tilemaps, no masks) the rule has no opinion and nothing is ever removed.
///
/// Read FloorManager.CurrentFloor (or FloorController.CurrentFloor, same number) from anywhere: difficulty, spawn rates,
/// shop prices, score bonuses... CurrentFloor is 1-based. The spawner's level index is CurrentFloor - 1, so floor 1 = the
/// first entry in FoeSpawner's "levels" list. Past the last entry the spawner reuses the last list.
/// The floor a run STARTS on is FloorController's "Starting Floor" field.
///
/// Going down also HEALS every player that counts (Heal On Descent, 10% of max health by default) and the run has an END: when the floor
/// number goes past "Last Floor", no new floor is built and EndScreens loads the Ending scene instead.
///
/// HAZARD TILES: it also reads the tile under each player, by player NUMBER:
///   lava      does NO damage. Standing on a tile with "lava" in its name turns the player's sprite red, a little more every second.
///             When the sprite is fully red the player DIES. Off the lava the red fades again, and it keeps fading while the player is
///             jumping (a hop over lava, or off it, is a way to cool down). Hurts the players listed in "Lava Hurts" (Player 1, blue, by default).
///   water     hurts the players listed in "Water Hurts" (Player 2, orange, by default) MORE than electricity does (10 per tick against 5) and
///             SLOWS them down while they are in it (and for a moment after). Any tile with "water" in its name.
///   electric  any tile with "electric" in its name hurts the players listed in "Electric Hurts" (every player by default): 5 damage every
///             half second while they stand on it. The first tick is the moment they step on it.
///   Spikes are gone: they no longer exist as a hazard here.
///
///   Water and electricity hurt through CoopHealth.ApplyDamage, so Invincible and the CoopRules (Shared Damage, Single Player Mode) still apply.
///   Their damage numbers are the MEDIUM values: every hit is multiplied by DifficultyRules.TrapDamageScale (Easy x0.5, Medium x1, Hard x2; PVP
///   always counts as Easy). Lava has no damage number; the same scale changes how FAST the red builds instead.
///
/// A player who is jumping (CoopHealth.Airborne) is skipped by water and electricity: they fly over them. Lava is the exception on purpose:
/// it is still read every frame, and since an airborne player is off the lava, the red fades. The moment they land, the tile under them is read again.
///
/// HazardTileFx (a separate script on its own object) gives those same tiles their effects (glow, sparks, hopping pieces, sounds). It reads the tile
/// names, the Tilemaps and the foot point from here (see "hooks for HazardTileFx" below), so what glows is exactly what burns. This script does not
/// need it, and it does not need this script's "Hazards Enabled" box ticked.
///
/// Put this on an empty object and drag the FoeSpawner into "spawner". The spawner's "Auto Start" box is now ignored
/// (this script starts the right level itself), so it no longer matters whether it is ticked.
/// </summary>
public class FloorManager : MonoBehaviour
{
    // ------------------------------------------------------------ shared

    /// <summary>The floor the player is on. This is FloorController's number: there is only ONE counter.</summary>
    public static int CurrentFloor => FloorController.CurrentFloor;

    /// <summary>Fires when the player arrives on a new floor (behind the black screen), with the NEW floor number. Also fires once at the start of the scene.</summary>
    public static event System.Action<int> OnFloorChanged;

    /// <summary>Fires when the CURRENT floor is started over (RestartFloor, a Retry after the team was wiped). The floor number does not change,
    /// so FloorChanged does not fire. The traps listen to this to put their tiles and timers back.</summary>
    public static event System.Action OnFloorRestarted;

    /// <summary>Fires when the player has killed every foe on the floor (passes the floor number).</summary>
    public event System.Action<int> OnFloorCleared;

    // ------------------------------------------------------------ inspector

    [Tooltip("Leave empty to find the FoeSpawner in the scene.")]
    [SerializeField] private FoeSpawner spawner;

    [Header("Where foes may be")]
    [Tooltip("PROTECTED LAYER. A foe whose position overlaps a collider on these layers is NEVER removed, and foes may spawn there. " +
             "Make an empty object with a Box Collider 2D (Is Trigger ticked) covering the whole playable stage, put it on its own " +
             "layer (e.g. 'FoeSafe') and pick that layer here. Nothing = off.")]
    [SerializeField] private LayerMask protectedMask = 0;
    [Tooltip("The floor Tilemaps: a foe standing on a cell with a tile in one of these is fine. Add EVERY Tilemap foes can legally walk on.")]
    [SerializeField] private Tilemap[] floorTilemaps;
    [Tooltip("Optional, for floors built from prefabs: floor pieces with a Collider2D (trigger is fine) on this layer. Nothing = off.")]
    [SerializeField] private LayerMask floorMask = 0;

    [Header("Going down: healing and the last floor")]
    [Tooltip("The final floor of the run. Going down from it (a door) ends the run (Ending scene) instead of building another floor.")]
    [SerializeField, Min(1)] private int lastFloor = 4;
    [Tooltip("Fraction of each player's MAX health restored each time the floor number goes up. 0.1 = 10%. 0 = no healing.")]
    [SerializeField, Range(0f, 1f)] private float healOnDescent = 0.1f;
    [Tooltip("Leave empty to find the EndScreens in the scene. It loads the Ending scene on a win (and the Failure Ending scene when health hits zero).")]
    [SerializeField] private EndScreens endScreens;

    [Header("Hazard tiles (lava, water, electric)")]
    [Tooltip("Master switch for lava, water and electricity.")]
    [SerializeField] private bool hazardsEnabled = true;
    [Tooltip("Tilemaps to check for hazard tiles. Leave EMPTY to check every Tilemap in the scene (re-checked once a second).")]
    [SerializeField] private Tilemap[] hazardTilemaps;
    [Tooltip("Where on each player the tile is read, as an offset from the middle of that player's collider (world units). Nudge this until it sits at their feet.")]
    [SerializeField] private Vector2 hazardCheckOffset = Vector2.zero;
    [Tooltip("Seconds between damage ticks while a player stands in water or on electricity they are hurt by. The first tick is the moment they step on it. Lava has no ticks.")]
    [SerializeField, Min(0.05f)] private float hazardTickSeconds = 0.5f;
    [Tooltip("Prints a Console line every time a hazard hurts or kills a player, with the tile and sprite names, so a wrong hit can be traced.")]
    [SerializeField] private bool logHazards = true;

    [Header("Lava (no damage: the sprite turns red, and a fully red player dies)")]
    [Tooltip("A tile counts as lava when its name (tile asset OR sprite) contains this. Capitals, spaces, dashes and underscores don't matter, so \"lava\" covers lava-16px_strip12_0 to _11.")]
    [SerializeField] private string lavaName = "lava";
    [Tooltip("Which players lava affects. Player 1 (blue) by default. Add 2, 3 or 4 if you want them affected too.")]
    [SerializeField] private int[] lavaHurts = { 1 };
    [Tooltip("Seconds standing on lava for the player to go from their normal colour to fully red, which kills them (Medium difficulty; Easy takes twice as long, Hard half as long).")]
    [SerializeField, Min(0.1f)] private float lavaSecondsToDie = 2.5f;
    [Tooltip("Seconds for a fully red player to cool back to their normal colour once they are OFF the lava. It also cools while they are jumping.")]
    [SerializeField, Min(0.1f)] private float lavaCoolSeconds = 2f;
    [Tooltip("The colour the sprite turns toward. It is blended over the player's own colour, so a fully heated player is exactly this colour.")]
    [SerializeField] private Color lavaTint = new Color(1f, 0.1f, 0.1f, 1f);

    [Header("Water (hurts more than electricity, and slows)")]
    [Tooltip("A tile counts as water when its name (tile asset OR sprite) contains this, so \"water\" covers water-16px_strip8_0 to _7.")]
    [SerializeField] private string waterName = "water";
    [Tooltip("Which players water hurts and slows. Player 2 (orange) by default. Add 1, 3 or 4 if you want them affected too.")]
    [SerializeField] private int[] waterHurts = { 2 };
    [Tooltip("Damage per tick. Keep it ABOVE Electric Damage: water is meant to hurt more than electricity.")]
    [SerializeField, Min(0f)] private float waterDamagePerTick = 10f;
    [Tooltip("Walking speed while in water, as a fraction of normal (0.5 = half speed).")]
    [SerializeField, Range(0.1f, 1f)] private float waterSlowFactor = 0.5f;
    [Tooltip("How long the slow lasts after the last frame in water, so stepping out doesn't snap back to full speed. Kept short so the slow ends soon after they leave.")]
    [SerializeField, Min(0.05f)] private float waterSlowSeconds = 0.25f;

    [Header("Electric")]
    [Tooltip("A tile counts as electric when its name (tile asset OR sprite) contains this. Capitals, spaces, dashes and underscores don't matter, so \"electric\" covers electric-16px_strip8_0 to _7.")]
    [SerializeField] private string electricName = "electric";
    [Tooltip("Which players electric tiles hurt. Everybody (1 to 4) by default. Take a number out to let that player walk on them safely.")]
    [SerializeField] private int[] electricHurts = { 1, 2, 3, 4 };
    [Tooltip("Damage per tick. 5 every 0.5 seconds (Hazard Tick Seconds).")]
    [SerializeField, Min(0f)] private float electricDamage = 5f;

    [Header("Stray foe check")]
    [Tooltip("Turn the removal of foes that are somewhere invalid on or off. (The spawn-time check stays active either way.)")]
    [SerializeField] private bool removeStrayFoes = true;
    [Tooltip("How often the check runs, in seconds.")]
    [SerializeField, Min(0.05f)] private float checkInterval = 0.25f;
    [Tooltip("A foe must stay invalid this long before it is removed, so a foe scraping a wall edge for a moment is not killed.")]
    [SerializeField, Min(0f)] private float graceSeconds = 1f;
    [Tooltip("Prints one Console line every time a foe is removed, with its name and position, so a wrong removal can be traced.")]
    [SerializeField] private bool logRemovals = true;

    // ------------------------------------------------------------ state

    private readonly Dictionary<FoeController, float> invalidSince = new Dictionary<FoeController, float>();
    private readonly List<FoeController> pruneList = new List<FoeController>();
    private readonly Collider2D[] overlapBuffer = new Collider2D[1];
    private float nextCheckTime;
    private bool warnedNoFloor;
    private FloorController floorController;
    private int handledFloor;   // the floor the spawner was last started for (0 = none yet), so one floor never starts twice

    private const int MaxPlayers = 4;
    private readonly float[] nextTickTime = new float[MaxPlayers + 1];    // per player number: when water / electricity may hurt them next
    private readonly Collider2D[] playerColliders = new Collider2D[MaxPlayers + 1];
    private readonly CoopHealth[] playerColliderOwners = new CoopHealth[MaxPlayers + 1];
    private Tilemap[] autoHazardMaps;
    private float nextHazardMapRefresh;

    // Lava heat, per player number: 0 = normal colour, 1 = fully red = dead.
    private readonly float[] lavaHeat = new float[MaxPlayers + 1];
    private readonly SpriteRenderer[] lavaRenderer = new SpriteRenderer[MaxPlayers + 1];   // the renderer that is currently tinted
    private readonly Color[] lavaBaseColor = new Color[MaxPlayers + 1];                     // its own colour, put back when the heat is gone
    private readonly bool[] lavaTinted = new bool[MaxPlayers + 1];                          // true while we have changed that renderer's colour
    private bool warnedNoLavaRenderer;

    // Water slows through the character's own ApplySlow; the controller is looked up once per player.
    private readonly TopDownCharacterController[] playerWalkers = new TopDownCharacterController[MaxPlayers + 1];
    private readonly CoopHealth[] playerWalkerOwners = new CoopHealth[MaxPlayers + 1];

    // ------------------------------------------------------------ setup

    private void Awake()
    {
        if (spawner == null) spawner = FindAnyObjectByType<FoeSpawner>();
        if (endScreens == null) endScreens = FindAnyObjectByType<EndScreens>();

        // Awake runs before every Start, so the spawner's own Auto Start can never start the wrong level over ours.
        if (spawner != null) spawner.ControlledExternally = true;
    }

    private void Start()
    {
        floorController = FindAnyObjectByType<FloorController>();
        if (floorController == null)
        {
            Debug.LogWarning("FloorManager: no FloorController in the scene, so nothing can change the floor and the " +
                             "spawner will stay on floor " + CurrentFloor + ".", this);
        }

        if (waterDamagePerTick <= electricDamage)
        {
            Debug.LogWarning("FloorManager: Water Damage Per Tick (" + waterDamagePerTick + ") is not above Electric Damage (" + electricDamage +
                             "), but water is meant to hurt more than electricity.", this);
        }

        if (spawner != null)
        {
            spawner.OnLevelCleared += HandleLevelCleared;
            spawner.SpawnPointFilter = IsValidFoePosition;   // the spawn-time check: no foe is created in an invalid spot
        }

        // Begin on the floor FloorController says we are on. Fresh run = Starting Floor; back from the Shop = the floor after it.
        ApplyFloor(CurrentFloor);

        // From now on, every floor change (a door, Shop return, GoDownFloor()) comes through here.
        FloorController.FloorChanged += ApplyFloor;
    }

    // Switched off (or the scene is closing): nobody stays red. Destroyed players are skipped, their renderer is already gone.
    private void OnDisable()
    {
        ResetAllLava();
        for (int n = 1; n <= MaxPlayers; n++) ShowLavaHeat(n);
    }

    private void OnDestroy()
    {
        // FloorChanged is static and outlives this scene, so a leftover subscription would call a destroyed object.
        FloorController.FloorChanged -= ApplyFloor;

        if (spawner != null)
        {
            spawner.OnLevelCleared -= HandleLevelCleared;
            spawner.SpawnPointFilter = null;
            spawner.ControlledExternally = false;
        }
    }

    private void HandleLevelCleared(int levelIndex)
    {
        OnFloorCleared?.Invoke(CurrentFloor);
    }

    // ------------------------------------------------------------ the floor changes

    // Runs when the player has just arrived on a floor. FloorController has already removed the old foes and moved the
    // player, and the foes are still frozen behind the black screen, so the new level's first spawn waits for the fade-out.
    private void ApplyFloor(int floor)
    {
        if (floor == handledFloor) return;
        int previousFloor = handledFloor;
        handledFloor = floor;

        invalidSince.Clear();
        ResetAllLava();   // everybody arrives on a new floor cool

        // Went down from the last floor: the run is won. No new level starts, and nothing listening to OnFloorChanged
        // gets a floor number that has no data behind it.
        if (floor > lastFloor)
        {
            WinRun();
            return;
        }

        // Healing only on a real step down. The first call of a scene (a fresh start, or back from the Shop) has no
        // previous floor, so it never heals.
        if (previousFloor > 0 && floor > previousFloor) HealPlayers(healOnDescent);

        if (spawner != null) spawner.StartLevel(floor - 1);   // StartLevel also clears any foe still left from the last floor

        OnFloorChanged?.Invoke(floor);
    }

    /// <summary>True while the player is on the final floor (going down from here is the victory).</summary>
    public bool IsOnLastFloor => CurrentFloor >= lastFloor;

    /// <summary>
    /// Ends the run by loading the Ending scene (through EndScreens). Called automatically when the floor number goes past "Last Floor"; any script
    /// can also call it directly (for example from a script on the last floor, to skip the extra black screen).
    /// </summary>
    public void WinRun()
    {
        // Ending the run from a script on the last floor skips the door's results screen, so that floor is saved as cleared here (its Level Select
        // card too). When a door sent the team past the last floor the floor change is running and the room was already saved on the way down.
        if (!FloorController.IsChangingFloor && CurrentFloor <= lastFloor) LevelResults.RecordRoomIfUnsaved(CurrentFloor);

        ClearAllFoes();   // no score for these

        if (endScreens == null) endScreens = FindAnyObjectByType<EndScreens>();
        if (endScreens != null) endScreens.ShowVictory();
        else Debug.LogWarning("FloorManager: the run is won but there is no EndScreens in the scene to load the Ending scene.", this);
    }

    // Restores a fraction of each living player's MAX health (so health upgrades from the Shop count). AddHealth clamps at max.
    private void HealPlayers(float fraction)
    {
        if (fraction <= 0f) return;

        CoopHealth[] all = CoopHealth.Players.ToArray();
        for (int i = 0; i < all.Length; i++)
        {
            CoopHealth hp = all[i];
            if (hp == null || !hp.IsAlive || !CoopRules.Counts(hp.PlayerNumber)) continue;
            hp.AddHealth(hp.MaxHealth * fraction);
        }
    }

    /// <summary>
    /// Sends the players down one floor with the full change: black "Floor Level N" screen, teleport to that floor's spawn
    /// points, old foes removed, new floor's foes started. Works without a door. (If no black screen is set up,
    /// the players still go down, just without the fade.)
    /// </summary>
    public void GoDownFloor()
    {
        if (floorController == null) floorController = FindAnyObjectByType<FloorController>();
        if (floorController == null)
        {
            Debug.LogWarning("FloorManager: GoDownFloor() needs a FloorController in the scene.", this);
            return;
        }

        floorController.GoDown();
    }

    /// <summary>
    /// Starts the CURRENT floor's foes over from the beginning (a Retry after the team was wiped). The floor number does not change, and
    /// nobody is healed. The spawner clears any foe still left. LightsOutScreen calls this behind its black screen.
    /// </summary>
    public void RestartFloor()
    {
        invalidSince.Clear();
        ResetAllLava();   // a retry starts everybody cool
        OnFloorRestarted?.Invoke();
        if (spawner != null) spawner.StartLevel(CurrentFloor - 1);
    }

    /// <summary>
    /// Destroys every foe in the scene, including ones the spawner didn't make (hand-placed foes).
    /// Destroying skips FoeController.Die(), so no score is awarded for cleared foes.
    /// </summary>
    public void ClearAllFoes()
    {
        FoeController[] foes = FindObjectsByType<FoeController>();
        for (int i = 0; i < foes.Length; i++)
        {
            Destroy(foes[i].Root);
        }
        invalidSince.Clear();
    }

    /// <summary>Call this if a new floor uses different floor Tilemaps than the last one.</summary>
    public void SetFloorTilemaps(Tilemap[] maps)
    {
        floorTilemaps = maps;
        invalidSince.Clear();
    }

    // ------------------------------------------------------------ the one rule

    /// <summary>
    /// True if a foe may stand at this point (it will never be removed there, and the spawner may use it).
    /// Used by the spawner BEFORE it creates a foe, and by the stray check on foes that already exist.
    /// </summary>
    public bool IsValidFoePosition(Vector2 pos)
    {
        bool haveProtected = protectedMask.value != 0;
        bool haveTilemaps = floorTilemaps != null && floorTilemaps.Length > 0;
        bool haveMask = floorMask.value != 0;

        if (!haveProtected && !haveTilemaps && !haveMask) return true;   // nothing set up: no opinion

        if (haveProtected && PointOnLayer(pos, protectedMask)) return true;   // protected: never invalid

        if (!haveTilemaps && !haveMask) return false;   // only the protected layer is set, and the point is outside it
        if (haveTilemaps && !OnAnyTile(pos)) return false;
        if (haveMask && !PointOnLayer(pos, floorMask)) return false;
        return true;
    }

    // Triggers are included on purpose, so a trigger collider works as the protected / floor layer.
    private bool PointOnLayer(Vector2 pos, LayerMask mask)
    {
        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(mask);
        filter.useTriggers = true;
        return Physics2D.OverlapPoint(pos, filter, overlapBuffer) > 0;
    }

    private bool OnAnyTile(Vector2 worldPos)
    {
        for (int i = 0; i < floorTilemaps.Length; i++)
        {
            Tilemap map = floorTilemaps[i];
            if (map != null && map.HasTile(map.WorldToCell(worldPos))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ hazard tiles (lava, water, electric)

    // Reads the tile under each player and acts on it, by player number:
    //   lava       only the players in Lava Hurts: the red builds while they stand on it and fades otherwise (also while jumping). Full red = dead.
    //   water      only the players in Water Hurts: slowed every frame, hurt once per Hazard Tick Seconds (more than electricity)
    //   electric   only the players in Electric Hurts: hurt once per Hazard Tick Seconds
    // A player on water AND electricity is hurt by the water only (the bigger hit), so one tick never hits twice.
    private void CheckHazards()
    {
        if (FloorController.BlocksInput || MenuController.IsOpen) return;   // nobody is hurt (or heated) behind a menu or a black screen

        float dt = Time.deltaTime;

        for (int n = 1; n <= MaxPlayers; n++)
        {
            CoopHealth p = CoopHealth.Get(n);
            if (p == null || !p.IsAlive || !CoopRules.Counts(n))
            {
                lavaHeat[n] = 0f;   // not playing, or dead: nothing stays red
                continue;
            }

            // The tile is only read on the ground: in the air there is nothing to stand on.
            bool airborne = p.Airborne;
            HazardRead read = default(HazardRead);
            if (!airborne) read = ReadHazardTiles(HazardPoint(n, p));

            // Lava runs EVERY frame, jumping or not. An airborne player is simply "not on lava", so the red fades instead of growing.
            bool onLava = read.lava && Listed(lavaHurts, n);
            if (UpdateLava(n, p, onLava, dt)) continue;   // the lava killed them

            if (airborne) continue;   // jumping: flies over water and electricity

            bool inWater = read.water && Listed(waterHurts, n);
            if (inWater) SlowDown(n, p);

            string source = null;
            float amount = 0f;
            TileBase tile = null;
            Sprite sprite = null;
            if (inWater)
            {
                source = "water";
                amount = waterDamagePerTick;
                tile = read.waterTile;
                sprite = read.waterSprite;
            }
            else if (read.electric && Listed(electricHurts, n))
            {
                source = "electricity";
                amount = electricDamage;
                tile = read.electricTile;
                sprite = read.electricSprite;
            }

            if (source != null && Time.time >= nextTickTime[n])
            {
                nextTickTime[n] = Time.time + hazardTickSeconds;
                HurtByHazard(p, amount, source, logHazards ? TileDetail(tile, sprite) : null);
            }
        }
    }

    private void HurtByHazard(CoopHealth player, float amount, string source, string detail)
    {
        amount *= DifficultyRules.TrapDamageScale;   // Easy 0.5x, Medium 1x, Hard 2x (PVP counts as Easy)
        if (amount <= 0f) return;

        if (logHazards)
        {
            Debug.Log("FloorManager: Player " + player.PlayerNumber + " hurt by " + source + " for " + amount + " (" + detail + ").", player);
        }
        player.ApplyDamage(amount);   // goes through CoopHealth: Invincible and the CoopRules still apply
    }

    // ------------------------------------------------------------ lava: the red meter

    // Builds or cools one player's lava heat (0 = their own colour, 1 = fully red = dead). Returns true when the lava just killed them.
    private bool UpdateLava(int n, CoopHealth p, bool onLava, float dt)
    {
        float heat = lavaHeat[n];

        if (onLava)
        {
            // The difficulty scale works like it does for every trap (Easy x0.5, Medium x1, Hard x2); here it is how fast the red builds.
            heat += dt * DifficultyRules.TrapDamageScale / Mathf.Max(lavaSecondsToDie, 0.05f);
        }
        else if (heat > 0f)
        {
            heat -= dt / Mathf.Max(lavaCoolSeconds, 0.05f);
        }

        heat = Mathf.Clamp01(heat);
        lavaHeat[n] = heat;
        if (heat < 1f) return false;

        // Fully red: dead. This is a kill, not damage: no hit points are counted down on the way, and the difficulty scale is not applied again.
        // It still goes through CoopHealth, so Invincible and the CoopRules apply.
        p.ApplyDamage(Mathf.Max(p.MaxHealth, 1f) * 100f);
        if (p.IsAlive) return false;   // protected (Invincible...): they stay fully red and it is tried again next frame

        if (logHazards) Debug.Log("FloorManager: Player " + n + " burned up in lava (the sprite reached full red).", p);
        lavaHeat[n] = 0f;
        return true;
    }

    private void ResetAllLava()
    {
        for (int n = 1; n <= MaxPlayers; n++) lavaHeat[n] = 0f;
    }

    // Paints the heat onto the player's own SpriteRenderer (the same one TopDownJump uses by default). While the player is in the air TopDownJump
    // draws a copy of that renderer and copies its colour every frame, so the red is visible and keeps fading during a jump too.
    // LateUpdate, after the Animator has picked this frame's sprite. At zero heat the player's own colour is put back exactly once.
    private void ShowLavaHeat(int n)
    {
        float heat = lavaHeat[n];
        SpriteRenderer sr = lavaRenderer[n];

        if (heat <= 0f)
        {
            if (lavaTinted[n])
            {
                if (sr != null) sr.color = lavaBaseColor[n];   // cooled all the way (or reset): their own colour back
                lavaTinted[n] = false;
                lavaRenderer[n] = null;
            }
            return;
        }

        if (!lavaTinted[n])
        {
            CoopHealth p = CoopHealth.Get(n);
            sr = p != null ? p.GetComponent<SpriteRenderer>() : null;
            if (sr == null)
            {
                if (!warnedNoLavaRenderer)
                {
                    warnedNoLavaRenderer = true;
                    Debug.LogWarning("FloorManager: Player " + n + " has no SpriteRenderer on its own object, so the lava heat can't turn the sprite red. " +
                                     "Lava still kills when the heat is full.", this);
                }
                return;
            }

            lavaRenderer[n] = sr;
            lavaBaseColor[n] = sr.color;   // remembered now, while it is still untouched
            lavaTinted[n] = true;
        }
        else if (sr == null)
        {
            lavaTinted[n] = false;   // that player object is gone (a new floor or a respawn): start fresh next time
            return;
        }

        Color tint = Color.Lerp(lavaBaseColor[n], lavaTint, heat);
        tint.a = sr.color.a;   // leave any fade the player has alone
        sr.color = tint;
    }

    // ------------------------------------------------------------ water: the slow

    // Holds the character at waterSlowFactor of its speed. Called every frame in water; the slow runs out waterSlowSeconds after the last call.
    private void SlowDown(int n, CoopHealth p)
    {
        if (playerWalkerOwners[n] != p)
        {
            playerWalkerOwners[n] = p;
            playerWalkers[n] = p.GetComponent<TopDownCharacterController>();
        }

        TopDownCharacterController walker = playerWalkers[n];
        if (walker != null) walker.ApplySlow(waterSlowFactor, waterSlowSeconds);
    }

    // ------------------------------------------------------------ reading the tiles

    // What ReadHazardTiles found under one point. The tile / sprite are the LAST water or electric tile found (null when none): the Console line
    // names them when a hit is printed, so no text is built while reading.
    private struct HazardRead
    {
        public bool lava;
        public bool water;
        public bool electric;
        public TileBase waterTile;
        public Sprite waterSprite;
        public TileBase electricTile;
        public Sprite electricSprite;
    }

    // Looks at every hazard tilemap under one point and says whether lava, water or electricity is there.
    private HazardRead ReadHazardTiles(Vector2 point)
    {
        HazardRead read = default(HazardRead);

        string lava = TileNameCache.NormalizeWanted(lavaName);
        string water = TileNameCache.NormalizeWanted(waterName);
        string electric = TileNameCache.NormalizeWanted(electricName);

        Tilemap[] maps = HazardMaps();
        for (int i = 0; i < maps.Length; i++)
        {
            Tilemap map = maps[i];
            if (map == null) continue;

            Vector3Int cell = map.WorldToCell(point);
            cell.z = 0;   // a tilemap that sits at a different Z would otherwise look one layer off and find nothing

            TileBase tile = map.GetTile(cell);
            if (tile == null) continue;

            Sprite shown = map.GetSprite(cell);

            if (TileOrSpriteHas(tile, shown, lava))
            {
                read.lava = true;
            }
            if (TileOrSpriteHas(tile, shown, water))
            {
                read.water = true;
                read.waterTile = tile;
                read.waterSprite = shown;
            }
            if (TileOrSpriteHas(tile, shown, electric))
            {
                read.electric = true;
                read.electricTile = tile;
                read.electricSprite = shown;
            }
        }
        return read;
    }

    // Console text for the hazard log lines. Only built when a line is really printed.
    private static string TileDetail(TileBase tile, Sprite sprite)
    {
        return "tile '" + tile.name + "', sprite '" + (sprite != null ? sprite.name : "") + "'";
    }

    private static bool Listed(int[] list, int value)
    {
        return list != null && System.Array.IndexOf(list, value) >= 0;
    }

    // Capitals, spaces, dashes and underscores are ignored, so "lava" is found in "Lava-16px_strip12_3". Both texts are already normalized
    // (TileNameCache), and an empty name never matches.
    private static bool Matches(string normalizedCandidate, string normalizedWanted)
    {
        return normalizedCandidate.Length > 0 && normalizedWanted.Length > 0 && normalizedCandidate.Contains(normalizedWanted);
    }

    // True when the tile asset's name OR the name of the sprite it shows contains the (normalized) wanted name. A null sprite never matches.
    private static bool TileOrSpriteHas(TileBase tile, Sprite sprite, string normalizedWanted)
    {
        return Matches(TileNameCache.NormalizedName(tile), normalizedWanted) || Matches(TileNameCache.NormalizedName(sprite), normalizedWanted);
    }

    private Tilemap[] HazardMaps()
    {
        if (hazardTilemaps != null && hazardTilemaps.Length > 0) return hazardTilemaps;
        return AllSceneMaps();
    }

    private Tilemap[] AllSceneMaps()
    {
        if (autoHazardMaps == null || Time.unscaledTime >= nextHazardMapRefresh)
        {
            autoHazardMaps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
            nextHazardMapRefresh = Time.unscaledTime + 1f;
        }
        return autoHazardMaps;
    }

    // The spot on a player where the tile is read: the middle of their solid collider, plus the offset.
    private Vector2 HazardPoint(int playerNumber, CoopHealth player)
    {
        if (playerColliderOwners[playerNumber] != player)
        {
            playerColliderOwners[playerNumber] = player;
            playerColliders[playerNumber] = PickPlayerCollider(player.gameObject);
        }

        Collider2D c = playerColliders[playerNumber];
        Vector2 centre = c != null ? (Vector2)c.bounds.center : (Vector2)player.transform.position;
        return centre + hazardCheckOffset;
    }

    private static Collider2D PickPlayerCollider(GameObject go)
    {
        Collider2D[] all = go.GetComponentsInChildren<Collider2D>();
        Collider2D fallback = null;
        for (int i = 0; i < all.Length; i++)
        {
            Collider2D c = all[i];
            if (!c.enabled) continue;
            if (!c.isTrigger) return c;
            if (fallback == null) fallback = c;
        }
        return fallback;
    }

    // ------------------------------------------------------------ hooks for HazardTileFx (read-only)

    /// <summary>The name a tile must contain to count as lava (the same rule that heats).</summary>
    public string LavaName { get { return lavaName; } }

    /// <summary>The name a tile must contain to count as water (the same rule that hurts and slows).</summary>
    public string WaterName { get { return waterName; } }

    /// <summary>The name a tile must contain to count as electric (the same rule that hurts).</summary>
    public string ElectricName { get { return electricName; } }

    /// <summary>The Tilemaps hazards are read from: the list you set, or every Tilemap in the scene (refreshed once a second).</summary>
    public Tilemap[] HazardTilemapList { get { return HazardMaps(); } }

    /// <summary>The spot on this player where the hazard tile is read (middle of their solid collider plus "Hazard Check Offset").</summary>
    public Vector2 HazardPointOf(CoopHealth player)
    {
        if (player == null) return Vector2.zero;

        int n = player.PlayerNumber;
        if (n < 1 || n > MaxPlayers) return player.transform.position;
        return HazardPoint(n, player);
    }

    /// <summary>The name rule used for lava, water and electricity: capitals, spaces, dashes and underscores don't matter.</summary>
    public static bool TileNameHas(string candidate, string wanted)
    {
        return Matches(TileNameCache.Normalize(candidate), TileNameCache.NormalizeWanted(wanted));
    }

    // ------------------------------------------------------------ hooks for the traps (read-only)

    /// <summary>The floor Tilemaps (the same list the stray foe check uses). Never null: an empty array if none are set.
    /// TileRemoveTrap and TrapRoomCalculator read the floor from here.</summary>
    public Tilemap[] FloorTilemapList { get { return floorTilemaps != null ? floorTilemaps : System.Array.Empty<Tilemap>(); } }

    /// <summary>True if there is a floor tile under this point. With no Floor Tilemaps set there is no opinion, so it says true.</summary>
    public bool IsFloorTile(Vector2 worldPos)
    {
        if (floorTilemaps == null || floorTilemaps.Length == 0) return true;
        return OnAnyTile(worldPos);
    }

    // ------------------------------------------------------------ update

    private void Update()
    {
        if (hazardsEnabled)
        {
            CheckHazards();
        }
        else
        {
            ResetAllLava();   // switched off while somebody was heating up: they cool at once
        }

        if (!removeStrayFoes || Time.time < nextCheckTime) return;
        nextCheckTime = Time.time + checkInterval;
        CheckFoes();
    }

    // After the Animator (and with TopDownJump's own LateUpdate, which copies the colour onto the jump sprite).
    private void LateUpdate()
    {
        for (int n = 1; n <= MaxPlayers; n++) ShowLavaHeat(n);
    }

    // ------------------------------------------------------------ stray foe check

    private void CheckFoes()
    {
        bool configured = protectedMask.value != 0 || floorMask.value != 0 ||
                          (floorTilemaps != null && floorTilemaps.Length > 0);
        if (!configured)
        {
            if (!warnedNoFloor)
            {
                warnedNoFloor = true;
                Debug.LogWarning("FloorManager: the stray foe check is on but no Floor Tilemaps, Floor Mask or Protected Mask " +
                                 "are set, so it is doing nothing.");
            }
            return;
        }

        FoeController[] foes = FindObjectsByType<FoeController>();
        for (int i = 0; i < foes.Length; i++)
        {
            FoeController foe = foes[i];
            if (foe == null || foe.IsDead)
            {
                invalidSince.Remove(foe);
                continue;
            }

            if (FoeIsSafe(foe))
            {
                invalidSince.Remove(foe);   // a valid spot: do nothing
                continue;
            }

            if (!invalidSince.TryGetValue(foe, out float since))
            {
                invalidSince[foe] = Time.time;   // just noticed it somewhere invalid: start the grace timer
                continue;
            }

            if (Time.time - since >= graceSeconds)
            {
                invalidSince.Remove(foe);
                RemoveStray(foe);
            }
        }

        PruneDestroyedFoes();
    }

    // A foe counts as safe if EITHER its pivot OR the centre of its collider is on a valid point. Foes pressed against a
    // wall often have a pivot sitting on the wall's cell, so testing only the pivot would remove foes that are fine.
    private bool FoeIsSafe(FoeController foe)
    {
        if (IsValidFoePosition(foe.transform.position)) return true;

        Collider2D c = foe.GetComponentInChildren<Collider2D>();
        if (c != null && IsValidFoePosition(c.bounds.center)) return true;

        return false;
    }

    private void RemoveStray(FoeController foe)
    {
        if (logRemovals)
        {
            Debug.Log("FloorManager: removed foe '" + foe.name + "' at " + (Vector2)foe.transform.position +
                      " (not on a floor tile or the protected layer for " + graceSeconds + "s).", foe);
        }

        if (spawner != null) spawner.RemoveStray(foe);   // destroys it AND hands it back so a replacement spawns
        else Destroy(foe.Root);
    }

    // Foes that were destroyed elsewhere (killed, cleared) leave dead keys behind; drop them.
    private void PruneDestroyedFoes()
    {
        if (invalidSince.Count == 0) return;

        pruneList.Clear();
        foreach (KeyValuePair<FoeController, float> pair in invalidSince)
        {
            if (pair.Key == null) pruneList.Add(pair.Key);
        }
        for (int i = 0; i < pruneList.Count; i++) invalidSince.Remove(pruneList[i]);
    }
}
