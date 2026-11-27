using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Tilemaps;
using Ilumisoft.HealthSystem;

/// <summary>
/// Something the tools break. Three TYPES decide how many hits it takes (the numbers are under "Types" in the Inspector):
///
///       Crate  1 hit          Metal Box  3 hits          Iron Wall  5 hits
///
/// What counts as ONE hit:
///   Sword and Grapple Arm swipe   one swing / spin that touches it (the same swing never counts twice). SwordController calls HitByBlade().
///   Pulse Beam                    one hit every Object Hit Interval seconds while the beam rests on it (faster the longer it is held).
///   Magic Boots                   a dash that runs into it (a super dash is worth more hits). Not the Bubble Shield or the Scanner Lens.
///   Grapple Arm hook              the hook landing on it (instead of pulling the player to it). Tools call HitByTool().
/// A hit that does not break it fires On Hit and flashes its sprite white for a moment.
///
/// Which type a thing is: a crate (or tile) named in Breakable Tiles has the Kind written on its row. Everything else (a whole
/// object this script sits on, a crate that is not in the list, a tile broken through Break Any Tile) is the Default Kind.
///
/// The Scanner Lens paints breakable objects RED all by itself: no ScannerMarker is needed (see IsRedUnderLens).
///
/// It works in four ways, and picks the right one by itself:
///
///   A normal object (a crate, barrel or pot that is its own sprite with a normal, non-trigger Collider2D):
///       put this on the object itself. The whole object breaks and gives Default Score.
///
///   A parent that holds SEVERAL separate crates (each crate a child with its own solid collider):
///       put this on the parent if you like. A hit breaks ONLY the crate it touched, never the rest
///       (Break Children Separately, ON by default).
///
///   A Tilemap (crates drawn as tiles on a Tilemap that has a TilemapCollider2D):
///       put this on the Tilemap's object. ONLY THE TILE THE BLADE TOUCHES breaks, never the whole Tilemap.
///       Which tiles may break:
///         - the ones listed under Breakable Tiles. A tile matches on the name of its Tile asset OR the name of the
///           Sprite it shows (the same way the stairs are matched), and each entry has its own score.
///         - every tile on the Tilemap, if Break Any Tile is ticked (they give Default Score).
///
///   A Tilemap that ALSO carries crate OBJECTS (separate sprites, scaled down and laid over the tiles, each with its own solid
///   non-trigger Collider2D, as DIRECT children of the Tilemap's object):
///       a hit on a crate breaks ONLY that crate, never the Tilemap and never the other crates. A crate scores like the listed tile
///       with the same name (its object name or the sprite it shows), otherwise Default Score. A hit on the Tilemap's own collider
///       still breaks tiles as above, so leave Break Any Tile OFF (and empty Breakable Tiles) if only the crate objects should break.
///
/// SCORE: the points go into CoopScore, the same per-player score the score buttons (CoopScoreBinder / CoopScorePanel) read:
///   - the player who made the hit gets them (CoopScore.CreditedPlayer when the weapon sets it, otherwise the player
///     nearest to the sword's hand). With Shared Score ON both players see the team total go up.
///   - Award To Every Player ON gives the FULL points to each player that counts (Shared Score OFF only: with Shared Score ON
///     the team total is shown to everybody, so it is added once and never doubled).
///   DestructibleObject.OnScored also fires with the points, if you would rather listen to that.
///   The old FoeController score event (the old single-number ScoreController) is only fired if Also Fire Foe Score Event is ticked.
///
/// Solid: at the start of the game the object (and its children) is put on the layer named below, so the player can't
/// walk through it. Make sure that layer collides with the player's layer (Edit > Project Settings > Physics 2D >
/// Layer Collision Matrix, where every box is ticked by default) and that SwordController's Foe Mask includes it
/// (its default, Everything, does). NOTE: on a Tilemap this moves the WHOLE Tilemap to that layer. If the Tilemap also
/// holds walls you do not want moved, empty Solid Layer Name and set the layer by hand.
///
/// When something breaks it vanishes and stops blocking straight away, fires On Broken, and (a whole object or one crate of
/// a group) is destroyed.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class DestructibleObject : MonoBehaviour
{
    /// <summary>One kind of tile that may break, and what it is worth.</summary>
    [System.Serializable]
    public class BreakableTile
    {
        [Tooltip("The tile's name: the name of its Tile asset OR of the Sprite it shows (capitals, spaces and underscores are ignored).")]
        public string tileName = "0x72_16x16DungeonTileset.v5 1_173";
        [Tooltip("Score for breaking one of these tiles.")]
        [Min(0)] public int score = 30;
        [Tooltip("Crate, Metal Box or Iron Wall: how many hits this one takes (the numbers are set under Types below). A crate OBJECT with this name takes the same.")]
        public ObjectKind kind = ObjectKind.Crate;
    }

    /// <summary>How tough a breakable is. The number of hits each one takes is set in the Inspector (1 / 3 / 5 by default).</summary>
    public enum ObjectKind { Crate, MetalBox, IronWall }

    // ------------------------------------------------------------ inspector

    [Header("Breaking")]
    [Tooltip("The layer this object is moved to when the game starts, so it blocks the player. Must match the layer's name in the Layers list exactly (it is case-sensitive). Leave EMPTY to keep the layer you set by hand.")]
    [SerializeField] private string solidLayerName = "objects";

    [Tooltip("Seconds before a broken whole object is destroyed. It is hidden and stops blocking immediately; a delay is only useful if a sound or effect that is on this object still needs to finish. (Tiles just disappear.)")]
    [SerializeField, Min(0f)] private float destroyDelay = 0f;

    [Tooltip("ON: if this object holds several separate solid child objects (crates under one parent), a hit breaks ONLY the one that was touched. " +
             "OFF: a hit on any of them breaks ALL of them. Has no effect on a Tilemap (its crate objects always break one at a time) or on a single crate.")]
    [SerializeField] private bool breakChildrenSeparately = true;

    [Tooltip("Runs when something breaks: once for a whole object, once for EACH crate of a group, once for EACH tile on a Tilemap. Hook up a sound, a particle effect, a drop...")]
    public UnityEvent onBroken;

    [Tooltip("Runs when a hit lands but it does NOT break yet (a metal box or an iron wall that took a hit). Hook up a clink sound or a spark.")]
    public UnityEvent onHit;

    [Header("Types (hits to break)")]
    [Tooltip("The type of a whole object this script sits on, of a crate object that is not named in Breakable Tiles, and of every tile broken through Break Any Tile.")]
    [SerializeField] private ObjectKind defaultKind = ObjectKind.Crate;
    [Tooltip("Hits a Crate takes to break.")]
    [SerializeField, Min(1)] private int crateHits = 1;
    [Tooltip("Hits a Metal Box takes to break.")]
    [SerializeField, Min(1)] private int metalBoxHits = 3;
    [Tooltip("Hits an Iron Wall takes to break.")]
    [SerializeField, Min(1)] private int ironWallHits = 5;
    [Tooltip("ON: a hit that does not break it makes its sprite flash white for a moment.")]
    [SerializeField] private bool flashOnHit = true;
    [SerializeField, Min(0.01f)] private float flashSeconds = 0.08f;

    [Header("Score")]
    [Tooltip("Score for breaking a whole object (or one crate of a group), and for any tile when Break Any Tile is ticked. 0 = no score.")]
    [SerializeField, Min(0)] private int defaultScore = 30;
    [Tooltip("OFF (default): the player who made the hit gets the points (with Shared Score ON both players see the team total). " +
             "ON: EVERY player that counts gets the full points. Ignored while Shared Score is ON, so the team total is never doubled.")]
    [SerializeField] private bool awardToEveryPlayer = false;
    [Tooltip("ON: ALSO fire FoeController's score event (the one the old single-number ScoreController listens to). " +
             "Leave OFF unless something still depends on that event: the per-player score does not need it.")]
    [SerializeField] private bool alsoFireFoeScoreEvent = false;

    [Header("Tilemap mode (only when this object is a Tilemap)")]
    [Tooltip("Tiles that may break, each with its own score. A tile matches on the name of its Tile asset OR the name of the Sprite it shows.")]
    [SerializeField] private BreakableTile[] breakableTiles = { new BreakableTile() };
    [Tooltip("ON: EVERY tile on this Tilemap can break, one at a time, for Default Score (use it for a Tilemap that holds only crates). Tiles named in the list above keep their own score. OFF: only the listed tiles break.")]
    [SerializeField] private bool breakAnyTile = false;
    [Tooltip("ON: the whole name must match (capitals, spaces and underscores ignored). OFF: a tile whose name merely CONTAINS the listed name also matches. Leave ON for numbered names, or '..._17' would also catch '..._173'.")]
    [SerializeField] private bool exactNameMatch = true;

    // ------------------------------------------------------------ public

    /// <summary>True once a whole object (or the last crate of a group) has been broken. (Stays false on a Tilemap: its tiles break one by one.)</summary>
    public bool IsBroken { get; private set; }

    /// <summary>Fires with the points every time something here breaks. Subscribe a score display or counter to it.</summary>
    public static event System.Action<int> OnScored;

    // Static values survive between Play sessions when "Reload Domain" is switched off, so they are reset by hand.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        OnScored = null;
    }

    // ------------------------------------------------------------ state

    // What a tile is worth and how tough it is. score -1 = this tile may not break.
    private struct TileInfo
    {
        public int score;
        public ObjectKind kind;
    }

    // (tile asset, sprite it shows) -> its info. Saves re-reading names every frame of a swing.
    private readonly Dictionary<(TileBase, Sprite), TileInfo> scoreCache = new Dictionary<(TileBase, Sprite), TileInfo>();

    // The separate crates this object holds (each direct child with a solid collider), found at the start.
    private readonly HashSet<Transform> crateParts = new HashSet<Transform>();

    // Hits taken so far, for whatever has not broken yet.
    private int wholeHits;
    private readonly Dictionary<Transform, int> partHits = new Dictionary<Transform, int>();
    private readonly Dictionary<(Tilemap, Vector3Int), int> tileHits = new Dictionary<(Tilemap, Vector3Int), int>();

    // The last hitKey that touched a whole object / a crate / a tile, so one swing or dash never counts twice.
    private readonly Dictionary<object, object> lastHitKey = new Dictionary<object, object>();

    // Sprites that are flashing right now, with the colour to give back.
    private readonly Dictionary<SpriteRenderer, Color> flashing = new Dictionary<SpriteRenderer, Color>();

    private bool holdsTilemap;     // this object is a Tilemap, or has one under it: it is never broken as a whole
    private bool splitIntoParts;   // this object holds several separate crates, and each breaks on its own
    private int partsLeft;         // how many of those crates are still standing
    private bool warnedOldHit;

    private void OnValidate()
    {
        scoreCache.Clear();   // the lists were edited: forget what was worked out
    }

    private void Awake()
    {
        if (!string.IsNullOrEmpty(solidLayerName))
        {
            int layer = LayerMask.NameToLayer(solidLayerName);
            if (layer >= 0)
            {
                foreach (Transform t in GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            }
            else
            {
                Debug.LogWarning("DestructibleObject on '" + name + "': there is no layer called '" + solidLayerName +
                                 "'. Add it under Layers, or fix the spelling in Solid Layer Name. The object stays on its current layer.", this);
            }
        }

        bool anySolid = false;
        foreach (Collider2D c in GetComponentsInChildren<Collider2D>())
        {
            if (!c.isTrigger) anySolid = true;
        }
        if (!anySolid)
        {
            Debug.LogWarning("DestructibleObject on '" + name + "': every collider is a trigger, so the player can walk straight through " +
                             "it and the sword can't see it. Untick Is Trigger.", this);
        }

        // Several separate crates under one parent: count them (each direct child that holds a solid collider is one crate).
        // On a Tilemap the crates are the child OBJECTS laid over the tiles; the Tilemap's own collider is not a crate.
        holdsTilemap = GetComponentInChildren<Tilemap>(true) != null;
        splitIntoParts = false;
        partsLeft = 0;

        crateParts.Clear();
        HashSet<Transform> crates = crateParts;
        foreach (Collider2D c in GetComponentsInChildren<Collider2D>())
        {
            if (c.isTrigger || c.transform == transform || IsTileCollider(c)) continue;
            crates.Add(DirectChildOf(c.transform));
        }

        partsLeft = crates.Count;
        splitIntoParts = breakChildrenSeparately && crates.Count >= 2;

        if (!holdsTilemap && !breakChildrenSeparately && crates.Count >= 2)
        {
            Debug.LogWarning("DestructibleObject on '" + name + "': this object holds " + crates.Count + " separate solid child objects, and " +
                             "Break Children Separately is OFF, so hitting any one of them breaks ALL of them. Tick it to break only the one that is hit.", this);
        }
    }

    // True for the collider that belongs to a Tilemap itself (its TilemapCollider2D, or the CompositeCollider2D on the Tilemap's object).
    // A collider on an OBJECT that merely sits under a Tilemap is NOT a tile collider: it is a crate.
    private static bool IsTileCollider(Collider2D c)
    {
        return c is TilemapCollider2D || c.GetComponent<Tilemap>() != null;
    }

    // The child of this object that holds t (or t itself if it is a direct child): the "crate" a collider belongs to.
    // Climbing also stops under a Tilemap, so crates sitting inside a Tilemap are found even when this script is on a parent of it.
    private Transform DirectChildOf(Transform t)
    {
        while (t.parent != null && t.parent != transform && t.parent.GetComponent<Tilemap>() == null) t = t.parent;
        return t;
    }

    // ------------------------------------------------------------ hit by the sword

    /// <summary>
    /// Called by SwordController for every collider of this object that is inside the sword's reach.
    /// "insideSweep" says whether a world point lies inside the part of the arc the blade is passing over right now.
    /// "playerNumber" is the player who swung (1, 2...), if the caller knows it; leave it 0 and the points go to the player
    /// credited by the weapon (CoopScore.CreditedPlayer) or, failing that, the one nearest to "origin" (the sword's hand).
    /// "hitKey" is ONE object per swing / spin: while it stays the same, a crate, tile or whole object takes only ONE hit from it
    /// (a sweep looks at the same thing on many frames). Leave it null and every call counts as a hit.
    /// Returns how many things BROKE (a whole object or one crate counts as 1). A hit that only dented it returns 0.
    /// </summary>
    public int HitByBlade(Collider2D touched, Vector2 origin, float reach, System.Func<Vector2, bool> insideSweep, int playerNumber = 0, object hitKey = null)
    {
        if (IsBroken || touched == null || insideSweep == null) return 0;
        if (!touched.enabled) return 0;   // a crate of this object that was broken a moment ago in the same swing

        // The Tilemap's OWN collider (its TilemapCollider2D / CompositeCollider2D sits on the same object as the Tilemap): break only the
        // tiles the blade reaches. A Tilemap is NEVER broken as a whole.
        if (IsTileCollider(touched))
        {
            Tilemap map = touched.GetComponentInParent<Tilemap>();
            if (map != null) return BreakTilesInSweep(map, origin, reach, insideSweep, playerNumber, hitKey);
        }

        // Anything else is an OBJECT, even when it sits inside a Tilemap's hierarchy: crate sprites laid over the tiles are objects, not tiles.
        // Some other collider on the Tilemap's own object would take the whole Tilemap with it, so it is ignored.
        if (holdsTilemap && touched.transform == transform) return 0;

        // It breaks when the blade passes over the closest point of the collider that was touched.
        if (!insideSweep(touched.ClosestPoint(origin))) return 0;

        // A Tilemap, or a parent with several crates: only the crate that was touched goes.
        if ((splitIntoParts || holdsTilemap) && touched.transform != transform)
        {
            return HitPart(DirectChildOf(touched.transform), origin, playerNumber, hitKey, 1);
        }

        return HitWhole(origin, playerNumber, hitKey, 1);
    }

    /// <summary>
    /// For tools that are not a sweeping blade (Pulse Beam, Magic Boots dash, Grapple hook): hits the thing that "touched" belongs to.
    /// "contactPoint" is where the tool touched it (RaycastHit2D.point) and "direction" the way the tool was travelling: on a Tilemap
    /// the two pick the tile. "hits" is how many hits this touch is worth; "hitKey" works as in HitByBlade.
    /// Returns how many things BROKE (0 if it only took a hit).
    /// </summary>
    public int HitByTool(Collider2D touched, Vector2 contactPoint, Vector2 direction, int playerNumber = 0, object hitKey = null, int hits = 1)
    {
        if (IsBroken || touched == null || hits <= 0) return 0;
        if (!touched.enabled) return 0;   // a crate of this object that was broken a moment ago

        if (IsTileCollider(touched))
        {
            Tilemap map = touched.GetComponentInParent<Tilemap>();
            return map != null ? HitTileAt(map, contactPoint, direction, playerNumber, hitKey, hits) : 0;
        }

        // Same rules as the blade: an object on a Tilemap is a crate, and the Tilemap's own object is never broken as a whole.
        if (holdsTilemap && touched.transform == transform) return 0;

        if ((splitIntoParts || holdsTilemap) && touched.transform != transform)
        {
            return HitPart(DirectChildOf(touched.transform), contactPoint, playerNumber, hitKey, hits);
        }

        return HitWhole(contactPoint, playerNumber, hitKey, hits);
    }

    /// <summary>
    /// For a tool that must tell a breakable from a wall BEFORE it hits (the Pulse Beam, the Grapple hook): would HitByTool() with these
    /// arguments hit something that can break? False for a wall tile on a Tilemap that also holds crates, so the hook can still pull the player to it.
    /// </summary>
    public bool IsBreakableAt(Collider2D touched, Vector2 contactPoint, Vector2 direction)
    {
        if (IsBroken || touched == null || !touched.enabled) return false;

        if (IsTileCollider(touched))
        {
            Tilemap map = touched.GetComponentInParent<Tilemap>();
            if (map == null) return false;

            Vector3Int cell;
            TileInfo info;
            TileBase tile;
            Sprite sprite;
            return FindTileAt(map, contactPoint, direction, out cell, out info, out tile, out sprite);
        }

        return !(holdsTilemap && touched.transform == transform);
    }

    /// <summary>
    /// For the Scanner Lens: should the renderer on "t" be painted red? True for a whole breakable object and for each separate
    /// crate of a group or of a Tilemap. A Tilemap itself is only painted when Break Any Tile is ON (every tile of it can break):
    /// the lens swaps a whole renderer's look, so single tiles of a mixed Tilemap can't be coloured one by one.
    /// </summary>
    public bool IsRedUnderLens(Transform t)
    {
        if (IsBroken || t == null) return false;
        if (t.GetComponent<Tilemap>() != null) return breakAnyTile;
        if (!holdsTilemap && !splitIntoParts) return true;   // one breakable object: all of it
        if (t == transform) return false;
        return crateParts.Contains(DirectChildOf(t));
    }

    /// <summary>
    /// Older call, with no way to know WHICH crate or tile was touched. It still works on a single object (as one hit), but on a Tilemap or a parent
    /// with several crates it would remove everything at once, so there it does nothing and says so. Update SwordController so it calls HitByBlade().
    /// </summary>
    public void Hit()
    {
        if (holdsTilemap || splitIntoParts)
        {
            if (!warnedOldHit)
            {
                warnedOldHit = true;
                Debug.LogWarning("DestructibleObject on '" + name + "': something called Hit(), which can't tell which crate or tile was touched, so nothing was " +
                                 "broken (it used to delete everything under this object). SwordController needs to call HitByBlade() instead.", this);
            }
            return;
        }

        HitWhole(transform.position, 0, null, 1);   // one hit: a metal box or iron wall needs more of them
    }

    /// <summary>Breaks the whole object right now (also usable from other scripts or a UnityEvent). On a Tilemap or a group this removes EVERYTHING under it.</summary>
    public void Break()
    {
        BreakWhole(transform.position, 0);
    }

    // ------------------------------------------------------------ hits

    private int HitsNeeded(ObjectKind kind)
    {
        switch (kind)
        {
            case ObjectKind.MetalBox: return Mathf.Max(1, metalBoxHits);
            case ObjectKind.IronWall: return Mathf.Max(1, ironWallHits);
            default: return Mathf.Max(1, crateHits);
        }
    }

    // True if this hitKey already hit "thing" (a whole object, a crate Transform or a (map, cell) pair). Otherwise remembers it.
    private bool AlreadyHitBy(object thing, object hitKey)
    {
        if (hitKey == null) return false;

        object last;
        if (lastHitKey.TryGetValue(thing, out last) && ReferenceEquals(last, hitKey)) return true;

        lastHitKey[thing] = hitKey;
        return false;
    }

    private int HitWhole(Vector2 creditAt, int playerNumber, object hitKey, int hits)
    {
        if (IsBroken || AlreadyHitBy(this, hitKey)) return 0;

        wholeHits += hits;
        if (wholeHits >= HitsNeeded(defaultKind))
        {
            BreakWhole(creditAt, playerNumber);
            return 1;
        }

        GotHit(transform);
        return 0;
    }

    private int HitPart(Transform part, Vector2 creditAt, int playerNumber, object hitKey, int hits)
    {
        if (part == null || AlreadyHitBy(part, hitKey)) return 0;

        int taken;
        partHits.TryGetValue(part, out taken);
        taken += hits;

        if (taken >= HitsNeeded(KindForCrate(part)))
        {
            partHits.Remove(part);
            lastHitKey.Remove(part);
            BreakPart(part, creditAt, playerNumber);
            return 1;
        }

        partHits[part] = taken;
        GotHit(part);
        return 0;
    }

    // A hit that did not break it: the event, and a short white flash.
    private void GotHit(Transform visual)
    {
        onHit.Invoke();
        if (flashOnHit && visual != null && isActiveAndEnabled) StartCoroutine(Flash(visual));
    }

    private IEnumerator Flash(Transform root)
    {
        List<SpriteRenderer> mine = new List<SpriteRenderer>();
        foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>())
        {
            if (sr == null || flashing.ContainsKey(sr)) continue;   // already flashing from an earlier hit

            Color original = sr.color;
            flashing[sr] = original;
            Color bright = Color.Lerp(original, Color.white, 0.75f);
            bright.a = original.a;
            sr.color = bright;
            mine.Add(sr);
        }

        yield return new WaitForSecondsRealtime(Mathf.Max(0.01f, flashSeconds));

        for (int i = 0; i < mine.Count; i++)
        {
            SpriteRenderer sr = mine[i];
            Color original;
            if (flashing.TryGetValue(sr, out original))
            {
                flashing.Remove(sr);
                if (sr != null) sr.color = original;
            }
        }
    }

    private void BreakWhole(Vector2 creditAt, int playerNumber)
    {
        if (IsBroken) return;
        IsBroken = true;

        foreach (Collider2D c in GetComponentsInChildren<Collider2D>()) c.enabled = false;
        foreach (Renderer r in GetComponentsInChildren<Renderer>()) r.enabled = false;

        Award(defaultScore, creditAt, playerNumber);
        onBroken.Invoke();
        Destroy(gameObject, destroyDelay);
    }

    // One crate of a group: only that child (and what is under it) vanishes. The parent and the other crates stay.
    private void BreakPart(Transform part, Vector2 creditAt, int playerNumber)
    {
        foreach (Collider2D c in part.GetComponentsInChildren<Collider2D>()) c.enabled = false;
        foreach (Renderer r in part.GetComponentsInChildren<Renderer>()) r.enabled = false;

        partsLeft--;
        if (partsLeft <= 0 && !holdsTilemap) IsBroken = true;   // a Tilemap keeps breaking its tiles after its last crate object is gone

        Award(ScoreForCrate(part), creditAt, playerNumber);
        onBroken.Invoke();
        Destroy(part.gameObject, destroyDelay);
    }

    // A crate object scores like the listed tile that has the same name (its object name or the name of the sprite it shows);
    // anything not in the list gives Default Score.
    private int ScoreForCrate(Transform crate)
    {
        BreakableTile entry = FindCrateEntry(crate);
        return entry != null ? entry.score : defaultScore;
    }

    // A crate object has the type of the listed tile with the same name; anything not in the list is the Default Kind.
    private ObjectKind KindForCrate(Transform crate)
    {
        BreakableTile entry = FindCrateEntry(crate);
        return entry != null ? entry.kind : defaultKind;
    }

    private BreakableTile FindCrateEntry(Transform crate)
    {
        if (breakableTiles == null || crate == null) return null;

        SpriteRenderer sr = crate.GetComponentInChildren<SpriteRenderer>(true);
        string spriteName = (sr != null && sr.sprite != null) ? sr.sprite.name : null;

        for (int i = 0; i < breakableTiles.Length; i++)
        {
            BreakableTile entry = breakableTiles[i];
            if (entry == null) continue;
            if (NameMatches(crate.name, entry.tileName) || NameMatches(spriteName, entry.tileName)) return entry;
        }
        return null;
    }

    // ------------------------------------------------------------ tiles

    // Breaks every allowed tile whose nearest point to the sword's hand is within reach AND inside the arc being swept.
    private int BreakTilesInSweep(Tilemap map, Vector2 origin, float reach, System.Func<Vector2, bool> insideSweep, int playerNumber, object hitKey)
    {
        BoundsInt used = map.cellBounds;
        if (used.size.x <= 0 || used.size.y <= 0) return 0;

        // Only the cells around the sword are looked at.
        Vector3Int a = map.WorldToCell(origin - Vector2.one * reach);
        Vector3Int b = map.WorldToCell(origin + Vector2.one * reach);
        int xMin = Mathf.Max(Mathf.Min(a.x, b.x), used.xMin);
        int xMax = Mathf.Min(Mathf.Max(a.x, b.x), used.xMax - 1);
        int yMin = Mathf.Max(Mathf.Min(a.y, b.y), used.yMin);
        int yMax = Mathf.Min(Mathf.Max(a.y, b.y), used.yMax - 1);

        float reachSq = reach * reach;
        int broken = 0;

        for (int x = xMin; x <= xMax; x++)
        {
            for (int y = yMin; y <= yMax; y++)
            {
                Vector3Int cell = new Vector3Int(x, y, 0);
                TileBase tile = map.GetTile(cell);
                if (tile == null) continue;

                // The corner-to-corner box of this cell, and the point of it nearest the sword's hand.
                Vector2 c1 = map.CellToWorld(cell);
                Vector2 c2 = map.CellToWorld(new Vector3Int(x + 1, y + 1, 0));
                Vector2 closest = new Vector2(
                    Mathf.Clamp(origin.x, Mathf.Min(c1.x, c2.x), Mathf.Max(c1.x, c2.x)),
                    Mathf.Clamp(origin.y, Mathf.Min(c1.y, c2.y), Mathf.Max(c1.y, c2.y)));

                float distanceSq = (closest - origin).sqrMagnitude;
                if (distanceSq > reachSq) continue;      // out of reach
                if (distanceSq < 0.0025f) continue;      // the hand is inside this cell: it is under the player, not in front of the blade
                if (!insideSweep(closest)) continue;     // not in the part of the arc the blade is passing over

                // Only a tile that is really under the blade is checked against the list.
                Sprite sprite = map.GetSprite(cell);
                if (!TryGetTileInfo(tile, sprite, out TileInfo info))
                {
                    ReportUnlistedTile(tile, sprite);
                    continue;
                }

                if (HitTile(map, cell, info, origin, playerNumber, hitKey, 1)) broken++;
            }
        }
        return broken;
    }

    // One hit on one tile. It goes when it has taken as many hits as its type needs (only THIS tile goes; the Tilemap's collider updates by itself).
    private bool HitTile(Tilemap map, Vector3Int cell, TileInfo info, Vector2 creditAt, int playerNumber, object hitKey, int hits)
    {
        (Tilemap, Vector3Int) key = (map, cell);
        if (AlreadyHitBy(key, hitKey)) return false;

        int taken;
        tileHits.TryGetValue(key, out taken);
        taken += hits;

        if (taken >= HitsNeeded(info.kind))
        {
            tileHits.Remove(key);
            lastHitKey.Remove(key);
            map.SetTile(cell, null);
            Award(info.score, creditAt, playerNumber);
            onBroken.Invoke();
            return true;
        }

        tileHits[key] = taken;
        onHit.Invoke();
        return false;
    }

    private static readonly float[] TileProbes = { 0.05f, 0.2f, -0.05f };

    // A tool touched the Tilemap's collider at "point" travelling along "direction": the tile is just inside the surface it touched.
    // Finds that tile. True if it is one that may break; false if there is no tile or it is a wall (tile and sprite say which, when there is one).
    private bool FindTileAt(Tilemap map, Vector2 point, Vector2 direction, out Vector3Int cell, out TileInfo info, out TileBase tile, out Sprite sprite)
    {
        Vector2 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector2.zero;
        cell = Vector3Int.zero;
        info = new TileInfo();
        sprite = null;

        for (int i = 0; i < TileProbes.Length; i++)
        {
            Vector2 p = point + dir * TileProbes[i];
            cell = map.WorldToCell(new Vector3(p.x, p.y, map.transform.position.z));
            tile = map.GetTile(cell);
            if (tile == null) continue;

            sprite = map.GetSprite(cell);
            return TryGetTileInfo(tile, sprite, out info);
        }

        tile = null;
        return false;
    }

    private int HitTileAt(Tilemap map, Vector2 point, Vector2 direction, int playerNumber, object hitKey, int hits)
    {
        Vector3Int cell;
        TileInfo info;
        TileBase tile;
        Sprite sprite;
        if (!FindTileAt(map, point, direction, out cell, out info, out tile, out sprite))
        {
            if (tile != null) ReportUnlistedTile(tile, sprite);   // a wall: nothing happens
            return 0;
        }
        return HitTile(map, cell, info, point, playerNumber, hitKey, hits) ? 1 : 0;
    }

    // Tiles already reported, so each kind of tile under the blade is mentioned only once.
    private readonly System.Collections.Generic.HashSet<TileBase> reportedTiles = new System.Collections.Generic.HashSet<TileBase>();

    // Tells you the REAL names of a tile the blade passed over but may not break, so a name that doesn't match is easy to spot.
    // Walls and floor on the same Tilemap show up here too; that is normal, just ignore those.
    private void ReportUnlistedTile(TileBase tile, Sprite sprite)
    {
        if (!reportedTiles.Add(tile)) return;

        Debug.Log("DestructibleObject on '" + name + "': the blade passed over a tile that may not break. Tile asset: '" + tile.name +
                  "', Sprite: '" + (sprite != null ? sprite.name : "none") + "'. If this is a crate, put one of those names in Breakable Tiles " +
                  "(or tick Break Any Tile).", this);
    }

    // Looks the tile up in the list (by Tile asset name or Sprite name); falls back to Break Any Tile. Cached per tile + sprite.
    private bool TryGetTileInfo(TileBase tile, Sprite sprite, out TileInfo info)
    {
        var key = (tile, sprite);
        if (!scoreCache.TryGetValue(key, out info))
        {
            info = WorkOutTileInfo(tile, sprite);
            scoreCache[key] = info;
        }
        return info.score >= 0;
    }

    private TileInfo WorkOutTileInfo(TileBase tile, Sprite sprite)
    {
        if (breakableTiles != null)
        {
            for (int i = 0; i < breakableTiles.Length; i++)
            {
                BreakableTile entry = breakableTiles[i];
                if (entry == null) continue;

                if (NameMatches(tile.name, entry.tileName) || (sprite != null && NameMatches(sprite.name, entry.tileName)))
                {
                    return new TileInfo { score = entry.score, kind = entry.kind };
                }
            }
        }

        if (breakAnyTile) return new TileInfo { score = defaultScore, kind = defaultKind };
        return new TileInfo { score = -1, kind = ObjectKind.Crate };
    }

    // Capitals, spaces, underscores and dashes are ignored: "0x72_16x16DungeonTileset.v5 1_173" and "0x72 16x16 dungeontileset.v5 1 173" are the same.
    // A trailing copy number like " (4)" (what Unity adds to duplicates) is ignored too: "..._173 (4)" matches "..._173".
    private bool NameMatches(string candidate, string wanted)
    {
        string a = Normalize(candidate);
        string b = Normalize(wanted);
        if (a.Length == 0 || b.Length == 0) return false;
        return exactNameMatch ? a == b : a.Contains(b);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Drop a trailing " (4)" first. Without this the 4 would be glued onto the number before it ("..._173 (4)" would read as "..._1734").
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\(\d+\)\s*$", "");

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i])) sb.Append(char.ToLowerInvariant(text[i]));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ score

    // Points go into CoopScore (what the per-player score buttons show). "creditAt" is where the hit happened (the sword's hand for a swing):
    // with nobody credited by the weapon, the player nearest to it gets the points.
    private void Award(int points, Vector2 creditAt, int playerNumber)
    {
        if (points <= 0) return;

        OnScored?.Invoke(points);

        if (awardToEveryPlayer && !CoopRules.SharedScore)
        {
            // Everybody gets the full points. (With Shared Score ON the team total is already shown to everyone, so it is added once below.)
            for (int i = 0; i < CoopHealth.Players.Count; i++)
            {
                CoopHealth player = CoopHealth.Players[i];
                if (player != null) CoopScore.Add(points, player.PlayerNumber);
            }
        }
        else if (playerNumber >= 1)
        {
            CoopScore.Add(points, playerNumber);
        }
        else
        {
            CoopScore.AddCredited(points, creditAt);
        }

        if (alsoFireFoeScoreEvent) FoeController.AwardPoints(points);
    }
}
