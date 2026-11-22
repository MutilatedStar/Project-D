using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
using Ilumisoft.HealthSystem;

/// <summary>
/// Takes the floor away. When it goes off, the floor tiles in an area around it crack, then disappear. It reads the floor from
/// FloorManager's "Floor Tilemaps" list (the same tiles the foe spawner treats as floor), so what it removes is exactly what counts
/// as floor. Give it its own "Tilemaps" list only if you want it to remove from different maps.
///
///   Area            a Rectangle (width x height in cells), a Circle (radius in cells) or a Plus (arm length). It is centred on the tile the
///                   trap sits on; Area Offset (in cells) moves it, so the hole can be ON the trap or a few tiles away from it.
///   Keep Intact     drag doors or spawn points in here: floor within Keep Intact Radius of them is never removed.
///   Warning         the tiles in the area tint towards Crack Color for Warn Seconds before they go.
///   Ripple          Ripple Seconds: the tiles go one after another, nearest to the trap first (0 = all at once).
///   Stay Gone       Stay Gone Seconds: the floor comes back after this long, then the trap rests for Interval and can go off again.
///                   0 = the floor stays gone until the floor changes or is restarted, and the trap goes off only once.
///
/// Falling: a player standing over a removed tile (not jumping) for "Fall Delay" seconds is killed with CoopHealth.ForceKill, the same call
/// CoopDowned uses for a pit fall (it skips Invincible and the Hard-difficulty linked death, and is not passed to the other player).
/// Untick Fall Kills if your pit system already notices that a tile is missing.
///
/// Foes: FloorManager's stray foe check treats a removed tile as "not floor", so a foe left standing over the hole is removed after its
/// grace time and the spawner replaces it. No spawn point is ever picked over a removed tile.
/// </summary>
[AddComponentMenu("Traps/Tile Remove Trap")]
public class TileRemoveTrap : TrapBase
{
    public enum AreaShape { Rectangle, Circle, Plus }

    [Header("What it removes")]
    [SerializeField] private AreaShape shape = AreaShape.Rectangle;
    [Tooltip("Rectangle: width and height in cells.")]
    [SerializeField] private Vector2Int rectangleSize = new Vector2Int(3, 3);
    [Tooltip("Circle: radius in cells.")]
    [SerializeField, Min(0f)] private float circleRadius = 2f;
    [Tooltip("Plus: how many cells the arms reach out from the middle.")]
    [SerializeField, Min(0)] private int plusArm = 2;
    [Tooltip("Moves the area away from the tile the trap sits on, in cells (x right, y up). 0,0 = the hole is centred ON the trap.")]
    [SerializeField] private Vector2Int areaOffset = Vector2Int.zero;
    [Tooltip("Optional. The Tilemaps to remove tiles from. Leave EMPTY to use FloorManager's Floor Tilemaps.")]
    [SerializeField] private Tilemap[] tilemaps;
    [Tooltip("Optional. Doors, spawn points, anything whose surroundings must keep their floor.")]
    [SerializeField] private Transform[] keepIntact;
    [Tooltip("Floor closer than this (world units) to a Keep Intact object is never removed.")]
    [SerializeField, Min(0f)] private float keepIntactRadius = 1.5f;

    [Header("How it happens")]
    [Tooltip("The tiles tint towards this colour while the trap warns.")]
    [SerializeField] private Color crackColor = new Color(1f, 0.4f, 0.25f, 1f);
    [Tooltip("The tiles vanish one after another, nearest to the trap first, over this many seconds. 0 = all at once.")]
    [SerializeField, Min(0f)] private float rippleSeconds = 0.4f;
    [Tooltip("The floor comes back after this many seconds. 0 = it stays gone until the floor changes or restarts (the trap then goes off only once).")]
    [SerializeField, Min(0f)] private float stayGoneSeconds = 4f;

    [Header("Falling")]
    [Tooltip("A player standing over a removed tile falls (CoopHealth.ForceKill, like a pit). Untick if your pit system already does this.")]
    [SerializeField] private bool fallKills = true;
    [Tooltip("How long a player can stand over the hole before they fall, so they can step off in time.")]
    [SerializeField, Min(0f)] private float fallDelay = 0.25f;

    // One tile of the area, with everything needed to put it back exactly as it was.
    private struct Target
    {
        public Tilemap map;
        public Vector3Int cell;
        public TileBase tile;
        public Color color;
        public TileFlags flags;
        public Matrix4x4 matrix;
        public float distance;
    }

    private readonly List<Target> targets = new List<Target>();   // the area of the shot in progress
    private readonly List<Target> gone = new List<Target>();      // tiles that are removed right now
    private readonly List<Vector2Int> offsets = new List<Vector2Int>();
    private readonly float[] voidTime = new float[TrapPlayers.MaxPlayers + 1];
    private int removedCount;
    private float rippleElapsed;
    private float goneTimer;
    private bool warnedNoFloor;

    // ------------------------------------------------------------ the cycle

    protected override bool CanStartCycle { get { return gone.Count == 0; } }

    protected override float FireSeconds { get { return rippleSeconds; } }

    protected override void Update()
    {
        if (!Paused && gone.Count > 0)
        {
            // The floor is gone: count down to bringing it back (only once the shot itself is over).
            if (phase == TrapPhase.Idle && stayGoneSeconds > 0f)
            {
                goneTimer -= Time.deltaTime;
                if (goneTimer <= 0f)
                {
                    RestoreGone();
                    waitTimer = interval;
                }
            }

            if (gone.Count > 0 && fallKills) CheckFalls(Time.deltaTime);
        }

        base.Update();
    }

    protected override void OnWarnBegin()
    {
        BuildTargets();
        Tint(0.25f);
    }

    protected override void OnWarnTick(float progress)
    {
        Tint(Mathf.Clamp01(progress * 0.7f + Mathf.PingPong(Time.time * 8f, 1f) * 0.3f));
    }

    protected override void OnFireBegin()
    {
        if (phase == TrapPhase.Firing && targets.Count == 0) BuildTargets();   // no warning: the area is worked out now

        removedCount = 0;
        rippleElapsed = 0f;
        goneTimer = stayGoneSeconds;
        RemoveUpTo(rippleSeconds > 0f ? 1 : targets.Count);
    }

    protected override void OnFireTick(float deltaTime)
    {
        if (rippleSeconds <= 0f) return;

        rippleElapsed += deltaTime;
        RemoveUpTo(Mathf.CeilToInt(rippleElapsed / rippleSeconds * targets.Count));
    }

    protected override void OnFireEnd()
    {
        RemoveUpTo(targets.Count);
        Untint();           // any tile that could not be removed gets its colour back
        targets.Clear();
        goneTimer = stayGoneSeconds;

        if (logEvents) Debug.Log("TileRemoveTrap '" + name + "': " + gone.Count + " tile(s) removed.", this);
    }

    protected override void ClearEffects()
    {
        Untint();
        targets.Clear();
        RestoreGone();
    }

    // ------------------------------------------------------------ working out the area

    private void BuildTargets()
    {
        targets.Clear();

        Tilemap[] maps = TrapFloor.MapsOrFloor(tilemaps);
        Grid grid = TrapFloor.GridFor(tilemaps);
        if (grid == null || maps.Length == 0)
        {
            if (!warnedNoFloor)
            {
                warnedNoFloor = true;
                Debug.LogWarning("TileRemoveTrap '" + name + "': no floor Tilemaps to remove from. Fill in \"Floor Tilemaps\" on the FloorManager " +
                                 "(or this trap's own Tilemaps list).", this);
            }
            return;
        }

        Vector3Int centre = grid.WorldToCell(transform.position);
        centre.z = 0;
        centre += new Vector3Int(areaOffset.x, areaOffset.y, 0);

        BuildOffsets();
        Vector2 here = transform.position;

        for (int i = 0; i < offsets.Count; i++)
        {
            Vector3 world = grid.GetCellCenterWorld(centre + new Vector3Int(offsets[i].x, offsets[i].y, 0));
            if (IsKept(world)) continue;

            for (int m = 0; m < maps.Length; m++)
            {
                Tilemap map = maps[m];
                if (map == null) continue;

                Vector3Int cell = map.WorldToCell(world);
                cell.z = 0;   // a tilemap that sits at a different Z would otherwise look one layer off and find nothing

                TileBase tile = map.GetTile(cell);
                if (tile == null) continue;

                Target t = new Target();
                t.map = map;
                t.cell = cell;
                t.tile = tile;
                t.color = map.GetColor(cell);
                t.flags = map.GetTileFlags(cell);
                t.matrix = map.GetTransformMatrix(cell);
                t.distance = ((Vector2)world - here).sqrMagnitude;
                targets.Add(t);
            }
        }

        targets.Sort((a, b) => a.distance.CompareTo(b.distance));
    }

    // The cells of the area as offsets from its middle cell.
    private void BuildOffsets()
    {
        offsets.Clear();

        switch (shape)
        {
            case AreaShape.Rectangle:
            {
                int w = Mathf.Max(1, rectangleSize.x);
                int h = Mathf.Max(1, rectangleSize.y);
                int x0 = -(w - 1) / 2;
                int y0 = -(h - 1) / 2;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++) offsets.Add(new Vector2Int(x0 + x, y0 + y));
                }
                break;
            }

            case AreaShape.Circle:
            {
                int r = Mathf.CeilToInt(circleRadius);
                float limit = circleRadius * circleRadius + 0.25f;
                for (int y = -r; y <= r; y++)
                {
                    for (int x = -r; x <= r; x++)
                    {
                        if (x * x + y * y <= limit) offsets.Add(new Vector2Int(x, y));
                    }
                }
                break;
            }

            default:   // Plus
            {
                for (int i = -plusArm; i <= plusArm; i++)
                {
                    offsets.Add(new Vector2Int(i, 0));
                    if (i != 0) offsets.Add(new Vector2Int(0, i));
                }
                break;
            }
        }
    }

    private bool IsKept(Vector3 world)
    {
        if (keepIntact == null) return false;

        float limit = keepIntactRadius * keepIntactRadius;
        for (int i = 0; i < keepIntact.Length; i++)
        {
            if (keepIntact[i] == null) continue;
            if (((Vector2)(keepIntact[i].position - world)).sqrMagnitude <= limit) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ the tiles

    // amount 0 = their own colour, 1 = Crack Color.
    private void Tint(float amount)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            Target t = targets[i];
            if (t.map == null) continue;

            t.map.SetTileFlags(t.cell, TileFlags.None);   // a locked colour would ignore SetColor
            t.map.SetColor(t.cell, Color.Lerp(t.color, crackColor, amount));
        }
    }

    // Gives every tile of the area that is STILL there its own colour back.
    private void Untint()
    {
        for (int i = 0; i < targets.Count; i++)
        {
            Target t = targets[i];
            if (t.map == null || t.map.GetTile(t.cell) != t.tile) continue;

            t.map.SetTileFlags(t.cell, TileFlags.None);
            t.map.SetColor(t.cell, t.color);
            t.map.SetTileFlags(t.cell, t.flags);
        }
    }

    // Removes the nearest tiles of the area until "count" of them have been dealt with.
    private void RemoveUpTo(int count)
    {
        count = Mathf.Min(count, targets.Count);
        while (removedCount < count)
        {
            Target t = targets[removedCount];
            removedCount++;

            if (t.map == null || t.map.GetTile(t.cell) != t.tile) continue;   // already gone (another trap) or repainted

            t.map.SetTile(t.cell, null);   // a Rule Tile next to it redraws its edge by itself
            gone.Add(t);
        }
    }

    // Puts every removed tile back exactly as it was (tile, rotation, colour).
    private void RestoreGone()
    {
        for (int i = 0; i < gone.Count; i++)
        {
            Target t = gone[i];
            if (t.map == null || t.map.GetTile(t.cell) != null) continue;   // the map is gone, or something else was put there

            t.map.SetTile(t.cell, t.tile);
            t.map.SetTileFlags(t.cell, TileFlags.None);
            t.map.SetTransformMatrix(t.cell, t.matrix);
            t.map.SetColor(t.cell, t.color);
            t.map.SetTileFlags(t.cell, t.flags);
        }

        gone.Clear();
        for (int n = 0; n < voidTime.Length; n++) voidTime[n] = 0f;
    }

    // ------------------------------------------------------------ falling

    private void CheckFalls(float deltaTime)
    {
        FloorManager manager = TrapFloor.Manager;

        for (int n = 1; n <= TrapPlayers.MaxPlayers; n++)
        {
            CoopHealth p = CoopHealth.Get(n);
            if (!TrapPlayers.CanBeHurt(p))
            {
                voidTime[n] = 0f;   // dead, not counting, or in the air: they are not falling right now
                continue;
            }

            // The same spot on the player that the hazard tiles are read at (FloorManager's Hazard Check Offset).
            Vector2 point = manager != null ? manager.HazardPointOf(p) : (Vector2)p.transform.position;

            if (!IsOverGone(point))
            {
                voidTime[n] = 0f;
                continue;
            }

            voidTime[n] += deltaTime;
            if (voidTime[n] < fallDelay) continue;

            voidTime[n] = 0f;
            if (logEvents) Debug.Log("TileRemoveTrap '" + name + "': Player " + n + " fell through the missing floor.", this);
            p.ForceKill();
        }
    }

    private bool IsOverGone(Vector2 point)
    {
        for (int i = 0; i < gone.Count; i++)
        {
            Target t = gone[i];
            if (t.map == null) continue;

            Vector3Int cell = t.map.WorldToCell(point);
            cell.z = 0;
            if (cell == t.cell && t.map.GetTile(cell) == null) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ editor helper

    protected override void OnDrawGizmosSelected()
    {
        base.OnDrawGizmosSelected();

        Grid grid = TrapFloor.GridFor(tilemaps);
        if (grid == null) return;

        Vector3Int centre = grid.WorldToCell(transform.position);
        centre.z = 0;
        centre += new Vector3Int(areaOffset.x, areaOffset.y, 0);

        BuildOffsets();
        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.8f);
        for (int i = 0; i < offsets.Count; i++)
        {
            Vector3 world = grid.GetCellCenterWorld(centre + new Vector3Int(offsets[i].x, offsets[i].y, 0));
            Gizmos.DrawWireCube(world, new Vector3(grid.cellSize.x * 0.9f, grid.cellSize.y * 0.9f, 0.01f));
        }
    }
}
