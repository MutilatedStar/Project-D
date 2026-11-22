using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>Which kind of area to look at.</summary>
public enum TrapAreaKind { RoomsOnly, CorridorsOnly, Both }

/// <summary>One room (or corridor) the calculator found: a group of floor cells.</summary>
public class TrapRoom
{
    public int Id;
    public bool IsCorridor;
    public readonly List<Vector3Int> Cells = new List<Vector3Int>();

    /// <summary>The floors (FloorController's floor numbers) this room belongs to. Empty = no floor claims it, so it counts for every floor.</summary>
    public readonly HashSet<int> Floors = new HashSet<int>();

    /// <summary>A box round the room, in world units.</summary>
    public Bounds WorldBounds;

    /// <summary>The middle of the room: the floor cell nearest its centre of mass, so it is always on the floor.</summary>
    public Vector2 Center;

    public int CellCount { get { return Cells.Count; } }
    public string Label { get { return (IsCorridor ? "Corridor " : "Room ") + Id; } }
}

/// <summary>A spot on a wall where a wall-mounted trap (arrows, laser) can sit, and the way it should shoot (into the room).</summary>
public struct TrapWallSlot
{
    public Vector3Int floorCell;   // the floor cell that touches the wall
    public Vector3Int wallCell;    // the cell with no floor: where the trap goes
    public Vector2 wallWorld;      // the middle of wallCell, in the world
    public Vector2 inward;         // the direction from the wall into the room
}

/// <summary>
/// THE SUB ROOM CALCULATOR. Add ONE of these anywhere in the scene (next to the FloorManager is a good home).
///
/// It reads the floor from FloorManager's "Floor Tilemaps" and works out the rooms by itself:
///   1. every cell with a floor tile is a floor cell;
///   2. it measures how far each cell is from the nearest wall / empty cell;
///   3. cells deeper inside than a corridor can be are the CORES of rooms (a corridor is never that deep, so it falls out);
///   4. every floor cell is handed to the nearest core, and the border band round a core is part of that room;
///   5. what is left (the thin passages between rooms) is grouped into corridors.
///
/// "Corridor Width" is the one setting to tune: passages up to this many cells wide are corridors, anything wider becomes a room of its own.
/// Select this object in the Scene view to SEE the result: each room has its own colour and number, corridors are grey.
///
/// It also finds out which FLOOR (FloorController's floor number) each room is on. All the floors of the game are in the same scene, so it takes
/// each floor's player spawn points from FloorController and marks everything connected to them as that floor. A room no floor claims counts for
/// every floor.
///
/// Other scripts (TrapPlacer) ask it:  RoomsOnFloor(...),  RoomAt(position),  GetWallSlots(room, list),  GetInteriorCells(room, depth, list).
/// Call Recalculate() if the floor tiles change by script (for example after FloorManager.SetFloorTilemaps).
/// </summary>
[AddComponentMenu("Traps/Trap Room Calculator")]
public class TrapRoomCalculator : MonoBehaviour
{
    [Header("The floor")]
    [Tooltip("Optional. Leave EMPTY to use FloorManager's Floor Tilemaps (recommended: then the rooms are exactly where foes may stand).")]
    [SerializeField] private Tilemap[] tilemapsOverride;

    [Header("Splitting the floor into rooms")]
    [Tooltip("Passages up to this many cells wide are corridors. A bigger opening counts as part of a room. Raise it if two rooms are being joined into one, " +
             "lower it if a thick corridor is being called a room.")]
    [SerializeField, Min(1)] private int corridorWidth = 2;

    [Header("Debug")]
    [Tooltip("Draws the rooms when this object is selected, even outside Play mode.")]
    [SerializeField] private bool previewInEditor = true;
    [Tooltip("Prints one Console line with what was found.")]
    [SerializeField] private bool logResult = true;

    // ------------------------------------------------------------ results

    private readonly List<TrapRoom> rooms = new List<TrapRoom>();
    private readonly Dictionary<Vector3Int, int> roomOf = new Dictionary<Vector3Int, int>();
    private readonly Dictionary<Vector3Int, int> wallDistance = new Dictionary<Vector3Int, int>();
    private readonly HashSet<Vector3Int> floorCells = new HashSet<Vector3Int>();
    private Grid grid;
    private int floorRows;
    private bool dirty = true;
    private bool warnedNoFloor;

    private static readonly Vector3Int[] Orthogonal =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0)
    };

    private static readonly Vector3Int[] Around =
    {
        new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0),
        new Vector3Int(1, 1, 0), new Vector3Int(1, -1, 0), new Vector3Int(-1, 1, 0), new Vector3Int(-1, -1, 0)
    };

    private void OnValidate()
    {
        dirty = true;
    }

    private void Start()
    {
        EnsureComputed();
    }

    // ------------------------------------------------------------ what other scripts use

    /// <summary>Every room and corridor found (worked out the first time it is asked for).</summary>
    public IReadOnlyList<TrapRoom> Rooms
    {
        get
        {
            EnsureComputed();
            return rooms;
        }
    }

    /// <summary>How many rows FloorController's Floors list has (0 if it could not be read).</summary>
    public int FloorRows { get { return floorRows; } }

    /// <summary>The Grid the cells belong to.</summary>
    public Grid CellGrid
    {
        get
        {
            EnsureComputed();
            return grid;
        }
    }

    /// <summary>Works everything out again (after the floor tiles changed).</summary>
    [ContextMenu("Recalculate now")]
    public void Recalculate()
    {
        dirty = true;
        EnsureComputed();
    }

    /// <summary>The room or corridor this world position is in, or null if it is not on the floor.</summary>
    public TrapRoom RoomAt(Vector2 world)
    {
        EnsureComputed();
        if (grid == null) return null;

        Vector3Int cell = grid.WorldToCell(world);
        cell.z = 0;
        int id;
        return roomOf.TryGetValue(cell, out id) ? rooms[id] : null;
    }

    /// <summary>Adds to "results" every room that is on this floor (past the last floor row, the last row is reused, like FloorController does).</summary>
    public void RoomsOnFloor(int floor, TrapAreaKind kind, List<TrapRoom> results)
    {
        EnsureComputed();

        int row = floorRows > 0 ? Mathf.Clamp(floor, 1, floorRows) : floor;
        for (int i = 0; i < rooms.Count; i++)
        {
            TrapRoom r = rooms[i];
            if (kind == TrapAreaKind.RoomsOnly && r.IsCorridor) continue;
            if (kind == TrapAreaKind.CorridorsOnly && !r.IsCorridor) continue;
            if (r.Floors.Count > 0 && !r.Floors.Contains(row)) continue;
            results.Add(r);
        }
    }

    public Vector2 CellCenter(Vector3Int cell)
    {
        EnsureComputed();
        return grid != null ? (Vector2)grid.GetCellCenterWorld(cell) : Vector2.zero;
    }

    /// <summary>How many cells this floor cell is from the nearest wall (1 = it touches one). 0 = not a floor cell.</summary>
    public int WallDistance(Vector3Int cell)
    {
        EnsureComputed();
        int d;
        return wallDistance.TryGetValue(cell, out d) ? d : 0;
    }

    /// <summary>Adds to "results" the cells of the room that are at least minWallDistance cells away from every wall (a hole trap wants the middle).</summary>
    public void GetInteriorCells(TrapRoom room, int minWallDistance, List<Vector3Int> results)
    {
        EnsureComputed();
        for (int i = 0; i < room.Cells.Count; i++)
        {
            if (WallDistance(room.Cells[i]) >= minWallDistance) results.Add(room.Cells[i]);
        }
    }

    /// <summary>Adds to "results" every wall spot of the room: one for each floor cell that touches a wall, pointing into the room.</summary>
    public void GetWallSlots(TrapRoom room, List<TrapWallSlot> results)
    {
        EnsureComputed();
        if (grid == null) return;

        for (int i = 0; i < room.Cells.Count; i++)
        {
            Vector3Int cell = room.Cells[i];
            for (int d = 0; d < Orthogonal.Length; d++)
            {
                Vector3Int wall = cell + Orthogonal[d];
                if (floorCells.Contains(wall)) continue;

                TrapWallSlot slot = new TrapWallSlot();
                slot.floorCell = cell;
                slot.wallCell = wall;
                slot.wallWorld = grid.GetCellCenterWorld(wall);
                slot.inward = new Vector2(-Orthogonal[d].x, -Orthogonal[d].y);
                results.Add(slot);
            }
        }
    }

    // ------------------------------------------------------------ the calculation

    private void EnsureComputed()
    {
        if (!dirty) return;
        dirty = false;
        Calculate();
    }

    private void Calculate()
    {
        rooms.Clear();
        roomOf.Clear();
        wallDistance.Clear();
        floorCells.Clear();
        grid = null;
        floorRows = 0;

        // 1. the floor cells
        Tilemap[] maps = TrapFloor.MapsOrFloor(tilemapsOverride);
        for (int m = 0; m < maps.Length; m++)
        {
            if (maps[m] != null && grid == null) grid = maps[m].layoutGrid;
        }
        if (grid == null)
        {
            if (!warnedNoFloor && Application.isPlaying)
            {
                warnedNoFloor = true;
                Debug.LogWarning("TrapRoomCalculator: no floor Tilemaps found. Fill in \"Floor Tilemaps\" on the FloorManager (or this component's own list).", this);
            }
            return;
        }

        for (int m = 0; m < maps.Length; m++)
        {
            Tilemap map = maps[m];
            if (map == null) continue;

            bool sameGrid = map.layoutGrid == grid;
            foreach (Vector3Int c in map.cellBounds.allPositionsWithin)
            {
                if (!map.HasTile(c)) continue;

                Vector3Int cell = c;
                if (!sameGrid) cell = grid.WorldToCell(map.GetCellCenterWorld(c));
                cell.z = 0;
                floorCells.Add(cell);
            }
        }
        if (floorCells.Count == 0) return;

        // 2. how deep inside each cell is (1 = touches a wall)
        MeasureWallDistance();

        // 3. which connected piece of floor each cell is in (so rooms can be matched to a floor)
        Dictionary<Vector3Int, int> pieceOf = new Dictionary<Vector3Int, int>();
        int pieces = LabelConnected(floorCells, pieceOf);

        // 4. the cores: cells deeper than any corridor, grouped
        int cutoff = (corridorWidth + 1) / 2 + 1;
        HashSet<Vector3Int> coreCells = new HashSet<Vector3Int>();
        foreach (KeyValuePair<Vector3Int, int> pair in wallDistance)
        {
            if (pair.Value >= cutoff) coreCells.Add(pair.Key);
        }
        Dictionary<Vector3Int, int> coreOf = new Dictionary<Vector3Int, int>();
        int coreCount = LabelConnected(coreCells, coreOf);

        // 5. every floor cell goes to the nearest core; the border band round a core belongs to that room
        Dictionary<Vector3Int, int> owner = new Dictionary<Vector3Int, int>();
        Dictionary<Vector3Int, int> distanceFromCore = new Dictionary<Vector3Int, int>();
        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        foreach (KeyValuePair<Vector3Int, int> pair in coreOf)
        {
            owner[pair.Key] = pair.Value;
            distanceFromCore[pair.Key] = 0;
            queue.Enqueue(pair.Key);
        }
        while (queue.Count > 0)
        {
            Vector3Int cell = queue.Dequeue();
            int d = distanceFromCore[cell];
            for (int i = 0; i < Around.Length; i++)
            {
                Vector3Int next = cell + Around[i];
                if (!floorCells.Contains(next) || owner.ContainsKey(next)) continue;

                // A diagonal step only counts when both cells beside it are floor too: it must not squeeze through a wall corner.
                if (i >= 4 && (!floorCells.Contains(cell + new Vector3Int(Around[i].x, 0, 0)) || !floorCells.Contains(cell + new Vector3Int(0, Around[i].y, 0)))) continue;

                owner[next] = owner[cell];
                distanceFromCore[next] = d + 1;
                queue.Enqueue(next);
            }
        }

        // 6. build the rooms
        for (int i = 0; i < coreCount; i++) rooms.Add(new TrapRoom { Id = i });

        HashSet<Vector3Int> corridorCells = new HashSet<Vector3Int>();
        foreach (Vector3Int cell in floorCells)
        {
            int own;
            if (owner.TryGetValue(cell, out own) && distanceFromCore[cell] <= cutoff - 1)
            {
                rooms[own].Cells.Add(cell);
                roomOf[cell] = own;
            }
            else
            {
                corridorCells.Add(cell);   // too far from any room core, or no core at all in its piece of floor
            }
        }

        // 7. the corridors: connected groups of what is left
        Dictionary<Vector3Int, int> corridorOf = new Dictionary<Vector3Int, int>();
        int corridorCount = LabelConnected(corridorCells, corridorOf);
        int firstCorridor = rooms.Count;
        for (int i = 0; i < corridorCount; i++) rooms.Add(new TrapRoom { Id = firstCorridor + i, IsCorridor = true });
        foreach (KeyValuePair<Vector3Int, int> pair in corridorOf)
        {
            TrapRoom r = rooms[firstCorridor + pair.Value];
            r.Cells.Add(pair.Key);
            roomOf[pair.Key] = r.Id;
        }

        // A core can be empty only if every cell of it was claimed by a neighbour's band, which cannot happen, but never keep an empty room.
        for (int i = rooms.Count - 1; i >= 0; i--)
        {
            if (rooms[i].Cells.Count == 0) rooms.RemoveAt(i);
        }
        for (int i = 0; i < rooms.Count; i++)
        {
            rooms[i].Id = i;
            for (int c = 0; c < rooms[i].Cells.Count; c++) roomOf[rooms[i].Cells[c]] = i;
        }

        // 8. which floor each room is on, and the room boxes / centres
        AssignFloors(pieceOf, pieces);
        for (int i = 0; i < rooms.Count; i++) FillBounds(rooms[i]);

        if (logResult && Application.isPlaying)
        {
            int roomCount = 0;
            for (int i = 0; i < rooms.Count; i++) if (!rooms[i].IsCorridor) roomCount++;
            Debug.Log("TrapRoomCalculator: " + floorCells.Count + " floor cells -> " + roomCount + " room(s) and " + (rooms.Count - roomCount) +
                      " corridor(s), in " + pieces + " connected piece(s) of floor. Floor rows in FloorController: " + floorRows + ".", this);
        }
    }

    // Chessboard distance from each floor cell to the nearest cell WITHOUT floor, by flooding inwards from the edges.
    private void MeasureWallDistance()
    {
        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        foreach (Vector3Int cell in floorCells)
        {
            for (int i = 0; i < Around.Length; i++)
            {
                if (floorCells.Contains(cell + Around[i])) continue;
                wallDistance[cell] = 1;
                queue.Enqueue(cell);
                break;
            }
        }

        while (queue.Count > 0)
        {
            Vector3Int cell = queue.Dequeue();
            int d = wallDistance[cell];
            for (int i = 0; i < Around.Length; i++)
            {
                Vector3Int next = cell + Around[i];
                if (!floorCells.Contains(next) || wallDistance.ContainsKey(next)) continue;
                wallDistance[next] = d + 1;
                queue.Enqueue(next);
            }
        }
    }

    // Numbers the connected groups (up, down, left, right) of a set of cells 0, 1, 2 ... and returns how many groups there are.
    private static int LabelConnected(HashSet<Vector3Int> cells, Dictionary<Vector3Int, int> labels)
    {
        int count = 0;
        Queue<Vector3Int> queue = new Queue<Vector3Int>();

        foreach (Vector3Int start in cells)
        {
            if (labels.ContainsKey(start)) continue;

            labels[start] = count;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Vector3Int cell = queue.Dequeue();
                for (int i = 0; i < Orthogonal.Length; i++)
                {
                    Vector3Int next = cell + Orthogonal[i];
                    if (!cells.Contains(next) || labels.ContainsKey(next)) continue;
                    labels[next] = count;
                    queue.Enqueue(next);
                }
            }
            count++;
        }
        return count;
    }

    // Marks each connected piece of floor with the floors whose spawn points sit on it, then copies that onto the rooms inside it.
    private void AssignFloors(Dictionary<Vector3Int, int> pieceOf, int pieces)
    {
        FloorController controller = UnityEngine.Object.FindAnyObjectByType<FloorController>();
        if (controller == null || controller.FloorCount == 0) return;

        floorRows = controller.FloorCount;
        HashSet<int>[] floorsOfPiece = new HashSet<int>[pieces];

        for (int floor = 1; floor <= floorRows; floor++)
        {
            for (int player = 1; player <= TrapPlayers.MaxPlayers; player++)
            {
                Vector2 spawn;
                if (!controller.TryGetSpawnPoint(floor, player, out spawn)) continue;

                Vector3Int cell;
                if (!NearestFloorCell(spawn, out cell)) continue;

                int piece = pieceOf[cell];
                if (floorsOfPiece[piece] == null) floorsOfPiece[piece] = new HashSet<int>();
                floorsOfPiece[piece].Add(floor);
            }
        }

        for (int i = 0; i < rooms.Count; i++)
        {
            if (rooms[i].Cells.Count == 0) continue;
            HashSet<int> floors = floorsOfPiece[pieceOf[rooms[i].Cells[0]]];
            if (floors == null) continue;
            foreach (int f in floors) rooms[i].Floors.Add(f);
        }
    }

    // The floor cell at this world point, or one up to 3 cells away (a spawn point can sit just off the floor).
    private bool NearestFloorCell(Vector2 world, out Vector3Int result)
    {
        Vector3Int centre = grid.WorldToCell(world);
        centre.z = 0;

        for (int ring = 0; ring <= 3; ring++)
        {
            for (int y = -ring; y <= ring; y++)
            {
                for (int x = -ring; x <= ring; x++)
                {
                    if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) != ring) continue;

                    Vector3Int cell = centre + new Vector3Int(x, y, 0);
                    if (!floorCells.Contains(cell)) continue;
                    result = cell;
                    return true;
                }
            }
        }

        result = centre;
        return false;
    }

    private void FillBounds(TrapRoom room)
    {
        Vector3 half = grid.cellSize * 0.5f;
        Vector3 sum = Vector3.zero;
        Bounds box = new Bounds(grid.GetCellCenterWorld(room.Cells[0]), Vector3.zero);

        for (int i = 0; i < room.Cells.Count; i++)
        {
            Vector3 c = grid.GetCellCenterWorld(room.Cells[i]);
            sum += c;
            box.Encapsulate(c - half);
            box.Encapsulate(c + half);
        }
        room.WorldBounds = box;

        // The cell nearest the average position, so the "centre" is always on the floor even in an L-shaped room.
        Vector3 average = sum / room.Cells.Count;
        float best = float.MaxValue;
        Vector2 centre = average;
        for (int i = 0; i < room.Cells.Count; i++)
        {
            Vector3 c = grid.GetCellCenterWorld(room.Cells[i]);
            float d = (c - average).sqrMagnitude;
            if (d < best)
            {
                best = d;
                centre = c;
            }
        }
        room.Center = centre;
    }

    // ------------------------------------------------------------ editor helper

    private const int MaxGizmoCells = 8000;

    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying && !previewInEditor) return;

        EnsureComputed();
        if (grid == null) return;

        bool heavy = floorCells.Count > MaxGizmoCells;   // thousands of squares make the Scene view crawl: draw only the boxes then
        Vector3 size = new Vector3(grid.cellSize.x * 0.9f, grid.cellSize.y * 0.9f, 0.01f);

        for (int i = 0; i < rooms.Count; i++)
        {
            TrapRoom room = rooms[i];
            Color colour = room.IsCorridor ? new Color(0.6f, 0.6f, 0.6f, 1f) : Color.HSVToRGB((i * 0.137f) % 1f, 0.7f, 1f);

            if (!heavy)
            {
                Gizmos.color = new Color(colour.r, colour.g, colour.b, 0.35f);
                for (int c = 0; c < room.Cells.Count; c++) Gizmos.DrawCube(grid.GetCellCenterWorld(room.Cells[c]), size);
            }

            Gizmos.color = colour;
            Gizmos.DrawWireCube(room.WorldBounds.center, room.WorldBounds.size);

#if UNITY_EDITOR
            string floors = room.Floors.Count == 0 ? "any floor" : "floor " + string.Join("/", room.Floors);
            Handles.Label(room.Center, room.Label + "  (" + room.CellCount + " cells, " + floors + ")");
#endif
        }
    }
}
