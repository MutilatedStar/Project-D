using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Puts traps into the floor by RULE, so you don't hand-place every one. Add ONE of these to the scene (next to the TrapRoomCalculator).
/// You can set traps at three levels:
///
///   THE FLOOR   a rule with Scope = Whole Floor: "put 6 arrow traps somewhere on floor 2".
///   A ROOM      a rule with Scope = Each Room ("2 laser traps in every room, rooms of 20+ cells only") or Room At Marker
///               ("3 hole traps in THE room this empty object sits in").
///   A TRAP      no rule needed: drag an ArrowTrap / LaserTrap / TileRemoveTrap into the scene and set it up by hand.
///               Hand-placed traps and placed traps work side by side. Every trap keeps its own settings (timing, damage, area...).
///
/// A rule's "Trap Prefab" is a prefab with ONE trap script on it (ArrowTrap, LaserTrap or TileRemoveTrap), set up the way you want that
/// trap to behave. The placer decides WHERE:
///   Arrow and Laser traps sit in the wall next to the floor and are aimed into the room.
///   Tile Remove traps sit on the floor, a few cells away from any wall (Min Wall Distance).
///
/// It places the traps of the floor the team is on, when the team arrives on it (behind the black screen), and takes them away again when
/// they leave. Traps are never put near a player spawn point (Keep Clear Of Spawns) and never closer together than the rule's Min Spacing.
/// With Use Fixed Seed on, a floor gets the same traps every time; off, every visit is different.
///
/// Level Select puzzle stages are skipped (Skip In Puzzle Stages), because there the floor number never changes.
/// </summary>
[AddComponentMenu("Traps/Trap Placer")]
public class TrapPlacer : MonoBehaviour
{
    public enum Scope { WholeFloor, EachRoom, RoomAtMarker }

    [System.Serializable]
    public class Rule
    {
        [Tooltip("Just a name for this row in the Inspector. The game doesn't read it.")]
        public string label = "New rule";
        public bool enabled = true;
        [Tooltip("A prefab with ONE trap on it: ArrowTrap, LaserTrap or TileRemoveTrap.")]
        public GameObject trapPrefab;
        [Tooltip("Whole Floor: Count traps spread over the whole floor. Each Room: Count traps in EVERY room. Room At Marker: Count traps in the one room the Marker is in.")]
        public Scope scope = Scope.EachRoom;
        [Tooltip("Room At Marker only: an empty object standing inside the room you mean.")]
        public Transform marker;
        [Tooltip("Rooms only, corridors only, or both.")]
        public TrapAreaKind where = TrapAreaKind.RoomsOnly;
        [Tooltip("The floor numbers this rule works on. Empty = every floor.")]
        public int[] floors;
        [Tooltip("How many traps (in total for Whole Floor, per room for the others).")]
        [Min(1)] public int count = 1;
        [Tooltip("Each Room only: the chance (0 to 1) that a given room gets its traps. 1 = every room.")]
        [Range(0f, 1f)] public float chance = 1f;
        [Tooltip("Rooms with fewer cells than this are skipped.")]
        [Min(0)] public int minRoomCells = 12;
        [Tooltip("Traps (of any rule) are never closer than this many cells to each other.")]
        [Min(1)] public int minSpacing = 3;
        [Tooltip("Tile Remove traps only: how many cells away from every wall the trap must sit.")]
        [Min(1)] public int minWallDistance = 2;
    }

    [Tooltip("Leave empty to find the TrapRoomCalculator in the scene.")]
    [SerializeField] private TrapRoomCalculator calculator;
    [Tooltip("The rules, applied top to bottom.")]
    [SerializeField] private Rule[] rules;

    [Header("Placing")]
    [Tooltip("Never place a trap closer than this (world units) to a player spawn point, so nobody is hit the moment they arrive.")]
    [SerializeField, Min(0f)] private float keepClearOfSpawns = 3f;
    [Tooltip("ON: a floor always gets the same traps. OFF: every visit to a floor is different.")]
    [SerializeField] private bool useFixedSeed = false;
    [SerializeField] private int seed = 12345;
    [Tooltip("Level Select puzzle stages never change the floor number, so the placer leaves them alone.")]
    [SerializeField] private bool skipInPuzzleStages = true;
    [Tooltip("Prints one Console line per floor with what was placed.")]
    [SerializeField] private bool logPlacement = true;

    private Transform container;
    private int placedFloor;   // the floor the current traps belong to (0 = none yet), so one floor is never placed twice
    private readonly List<Vector3Int> placedCells = new List<Vector3Int>();
    private readonly List<Vector2> spawnPoints = new List<Vector2>();

    private void Start()
    {
        if (calculator == null) calculator = FindAnyObjectByType<TrapRoomCalculator>();

        FloorController.FloorChanged += PlaceForFloor;
        PlaceForFloor(FloorController.CurrentFloor);
    }

    private void OnDestroy()
    {
        FloorController.FloorChanged -= PlaceForFloor;
        ClearPlaced();
    }

    /// <summary>Takes away the traps this placer made, and places the ones of this floor. Runs by itself on every floor change.</summary>
    public void PlaceForFloor(int floor)
    {
        if (floor == placedFloor) return;
        placedFloor = floor;
        ClearPlaced();

        if (skipInPuzzleStages && FloorController.PickedPuzzleStage() != null) return;
        if (calculator == null) calculator = FindAnyObjectByType<TrapRoomCalculator>();
        if (calculator == null)
        {
            Debug.LogWarning("TrapPlacer: no TrapRoomCalculator in the scene, so no traps were placed.", this);
            return;
        }
        if (rules == null || rules.Length == 0) return;

        container = new GameObject("Placed Traps (floor " + floor + ")").transform;
        placedCells.Clear();
        CollectSpawnPoints(floor);

        System.Random rng = useFixedSeed ? new System.Random(seed + floor * 7919) : new System.Random();
        int arrows = 0;
        int lasers = 0;
        int holes = 0;

        for (int r = 0; r < rules.Length; r++)
        {
            Rule rule = rules[r];
            if (rule == null || !rule.enabled || rule.trapPrefab == null) continue;
            if (rule.floors != null && rule.floors.Length > 0 && System.Array.IndexOf(rule.floors, floor) < 0) continue;

            bool wallMounted = rule.trapPrefab.GetComponent<ArrowTrap>() != null || rule.trapPrefab.GetComponent<LaserTrap>() != null;
            bool floorTrap = rule.trapPrefab.GetComponent<TileRemoveTrap>() != null;
            if (!wallMounted && !floorTrap)
            {
                Debug.LogWarning("TrapPlacer: rule '" + rule.label + "': the prefab '" + rule.trapPrefab.name +
                                 "' has no ArrowTrap, LaserTrap or TileRemoveTrap on it, so it was skipped.", this);
                continue;
            }

            List<TrapRoom> targets = new List<TrapRoom>();
            if (rule.scope == Scope.RoomAtMarker)
            {
                TrapRoom markerRoom = rule.marker != null ? calculator.RoomAt(rule.marker.position) : null;
                if (markerRoom != null) targets.Add(markerRoom);
                else Debug.LogWarning("TrapPlacer: rule '" + rule.label + "': the Marker is missing or not standing on the floor.", this);
            }
            else
            {
                calculator.RoomsOnFloor(floor, rule.where, targets);
            }
            targets.RemoveAll(room => room.CellCount < rule.minRoomCells);
            if (targets.Count == 0) continue;

            int placed = 0;
            if (rule.scope == Scope.EachRoom)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    if (rng.NextDouble() > rule.chance) continue;
                    placed += PlaceIn(rule, wallMounted, targets.GetRange(i, 1), rule.count, rng);
                }
            }
            else
            {
                placed = PlaceIn(rule, wallMounted, targets, rule.count, rng);
            }

            if (wallMounted && rule.trapPrefab.GetComponent<ArrowTrap>() != null) arrows += placed;
            else if (wallMounted) lasers += placed;
            else holes += placed;
        }

        if (logPlacement)
        {
            Debug.Log("TrapPlacer: floor " + floor + " -> " + arrows + " arrow, " + lasers + " laser and " + holes + " tile-remove trap(s) placed.", this);
        }
    }

    // ------------------------------------------------------------ placing

    // Puts up to "count" traps in these rooms (together). Returns how many it managed.
    private int PlaceIn(Rule rule, bool wallMounted, List<TrapRoom> rooms, int count, System.Random rng)
    {
        int placed = 0;

        if (wallMounted)
        {
            List<TrapWallSlot> slots = new List<TrapWallSlot>();
            for (int i = 0; i < rooms.Count; i++) calculator.GetWallSlots(rooms[i], slots);
            Shuffle(slots, rng);

            for (int i = 0; i < slots.Count && placed < count; i++)
            {
                TrapWallSlot slot = slots[i];
                if (TooClose(slot.floorCell, rule.minSpacing) || NearSpawn(slot.wallWorld)) continue;

                GameObject go = Spawn(rule.trapPrefab, slot.wallWorld);
                ArrowTrap arrow = go.GetComponent<ArrowTrap>();
                if (arrow != null) arrow.Aim(slot.inward);
                LaserTrap laser = go.GetComponent<LaserTrap>();
                if (laser != null) laser.Aim(slot.inward);

                placedCells.Add(slot.floorCell);
                placed++;
            }
        }
        else
        {
            List<Vector3Int> cells = new List<Vector3Int>();
            for (int i = 0; i < rooms.Count; i++) calculator.GetInteriorCells(rooms[i], rule.minWallDistance, cells);
            Shuffle(cells, rng);

            for (int i = 0; i < cells.Count && placed < count; i++)
            {
                Vector2 world = calculator.CellCenter(cells[i]);
                if (TooClose(cells[i], rule.minSpacing) || NearSpawn(world)) continue;

                Spawn(rule.trapPrefab, world);
                placedCells.Add(cells[i]);
                placed++;
            }
        }
        return placed;
    }

    private GameObject Spawn(GameObject prefab, Vector2 world)
    {
        Vector3 position = new Vector3(world.x, world.y, prefab.transform.position.z);
        return Instantiate(prefab, position, Quaternion.identity, container);
    }

    private bool TooClose(Vector3Int cell, int minSpacing)
    {
        for (int i = 0; i < placedCells.Count; i++)
        {
            int dx = Mathf.Abs(placedCells[i].x - cell.x);
            int dy = Mathf.Abs(placedCells[i].y - cell.y);
            if (Mathf.Max(dx, dy) < minSpacing) return true;
        }
        return false;
    }

    private bool NearSpawn(Vector2 world)
    {
        float limit = keepClearOfSpawns * keepClearOfSpawns;
        for (int i = 0; i < spawnPoints.Count; i++)
        {
            if ((spawnPoints[i] - world).sqrMagnitude < limit) return true;
        }
        return false;
    }

    // The spots the players land on when they arrive on this floor (FloorController's Floors list).
    private void CollectSpawnPoints(int floor)
    {
        spawnPoints.Clear();

        FloorController controller = FindAnyObjectByType<FloorController>();
        if (controller == null) return;

        for (int n = 1; n <= TrapPlayers.MaxPlayers; n++)
        {
            Vector2 spawn;
            if (controller.TryGetSpawnPoint(floor, n, out spawn)) spawnPoints.Add(spawn);
        }
    }

    private void ClearPlaced()
    {
        if (container != null) Destroy(container.gameObject);   // each trap puts its floor tiles back as it is switched off
        container = null;
    }

    private static void Shuffle<T>(List<T> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            T swap = list[i];
            list[i] = list[j];
            list[j] = swap;
        }
    }
}
