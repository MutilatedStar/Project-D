using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Shoots arrows out of one or more tiles. Put it on an empty object that sits on the tile the arrows come out of (usually a wall
/// tile), or on a parent with several child "shooter" objects, one per tile, and drag those into Shooters.
///
///   Aim            Right / Up / Left / Down, or Custom with an angle.
///   Volley         All At Once (every shooter fires together), One After Another (a sweep along the wall, Sequence Delay apart),
///                  or Random One (one shooter, picked at random, each shot).
///   Arrow          pick an Arrow Prefab if you have one; with none, a plain bar is made so it works straight away.
///                  Arrow Speed, Max Range and Damage are the MEDIUM values (hard hits twice as hard, easy half).
///   Obstacle Mask  the layer(s) of your WALL colliders. An arrow stops at them. Empty = it flies its whole Max Range.
///   Warning        the shooter's sprites flash red. Optional: a Warning Tile on a Tilemap swaps the tile under each shooter while it
///                  is about to fire (a loaded arrow slit), then puts the normal tile back.
///
/// Timing (Timed / Proximity / Manual, Interval, Warn Seconds, Active On Floors, events) is explained on TrapBase.
/// </summary>
[AddComponentMenu("Traps/Arrow Trap")]
public class ArrowTrap : TrapBase
{
    public enum Volley { AllAtOnce, OneAfterAnother, RandomOne }

    [Header("Where it shoots from")]
    [Tooltip("The tiles the arrows come out of: one empty object per tile. Leave EMPTY to shoot from this object itself.")]
    [SerializeField] private Transform[] shooters;
    [Tooltip("Moves each shooter to the middle of the tile cell it is on when the floor starts, so the arrows leave the tile dead centre.")]
    [SerializeField] private bool snapToCell = true;

    [Header("Aim")]
    [SerializeField] private TrapDirection direction = TrapDirection.Right;
    [Tooltip("Only used when Direction is Custom. Degrees: 0 = right, 90 = up, 180 = left, 270 = down.")]
    [SerializeField] private float customAngle = 0f;

    [Header("Volley")]
    [SerializeField] private Volley volley = Volley.AllAtOnce;
    [Tooltip("One After Another only: seconds between one shooter and the next.")]
    [SerializeField, Min(0.01f)] private float sequenceDelay = 0.2f;

    [Header("Arrow")]
    [Tooltip("Optional. Your own arrow (a sprite, facing RIGHT). It does not need any script. Empty = a plain bar is made.")]
    [SerializeField] private GameObject arrowPrefab;
    [Tooltip("Optional picture for the plain arrow when there is no prefab.")]
    [SerializeField] private Sprite arrowSprite;
    [Tooltip("Size of the plain bar (width along the flight, height across it) when there is no sprite.")]
    [SerializeField] private Vector2 arrowSize = new Vector2(0.6f, 0.14f);
    [SerializeField] private Color arrowColor = new Color(0.9f, 0.85f, 0.7f, 1f);
    [Tooltip("Sorting Order of the plain arrow, so it draws above the floor.")]
    [SerializeField] private int sortingOrder = 5;
    [Tooltip("World units per second.")]
    [SerializeField, Min(0.1f)] private float arrowSpeed = 8f;
    [Tooltip("How fat the arrow is for hitting a player (added to the player's own collider box).")]
    [SerializeField, Min(0.01f)] private float arrowRadius = 0.15f;
    [Tooltip("The arrow disappears after flying this far.")]
    [SerializeField, Min(0.5f)] private float maxRange = 12f;
    [Tooltip("Where the arrow starts, in front of the shooter. Keep it a bit more than half a tile so the arrow starts clear of the wall tile.")]
    [SerializeField, Min(0f)] private float spawnOffset = 0.6f;
    [Tooltip("The layer(s) of your WALL colliders. An arrow stops when it hits one. Empty = it flies its whole Max Range.")]
    [SerializeField] private LayerMask obstacleMask = 0;

    [Header("Damage (Medium value)")]
    [SerializeField, Min(0f)] private float damage = 15f;

    [Header("Warning")]
    [Tooltip("The shooter's sprites blink towards this colour while the trap warns.")]
    [SerializeField] private Color warnColor = new Color(1f, 0.25f, 0.2f, 1f);
    [Tooltip("Optional: a Tilemap and a tile. While the trap warns and fires, the tile under each shooter on this Tilemap becomes the Warning Tile.")]
    [SerializeField] private Tilemap warningMap;
    [SerializeField] private TileBase warningTile;

    private Transform[] active;   // the shooters in use (this object if the list is empty)
    private int nextInSequence;
    private float sequenceTimer;
    private readonly List<SpriteRenderer> flashers = new List<SpriteRenderer>();
    private readonly List<Color> flasherColors = new List<Color>();
    private readonly List<Vector3Int> swappedCells = new List<Vector3Int>();
    private readonly List<TileBase> swappedTiles = new List<TileBase>();

    /// <summary>The way the arrows fly.</summary>
    public Vector2 AimDirection { get { return TrapGeometry.DirectionOf(direction, customAngle); } }

    /// <summary>Aims the trap at any direction (TrapPlacer uses this to point a trap into the room it was put in).</summary>
    public void Aim(Vector2 aim)
    {
        if (aim.sqrMagnitude < 0.0001f) return;
        direction = TrapDirection.Custom;
        customAngle = TrapGeometry.AngleOf(aim);
    }

    // ------------------------------------------------------------ setup

    protected override void OnEnable()
    {
        base.OnEnable();
        BuildShooters();
    }

    private void BuildShooters()
    {
        List<Transform> list = new List<Transform>();
        if (shooters != null)
        {
            for (int i = 0; i < shooters.Length; i++)
            {
                if (shooters[i] != null) list.Add(shooters[i]);
            }
        }
        if (list.Count == 0) list.Add(transform);
        active = list.ToArray();

        flashers.Clear();
        flasherColors.Clear();
        for (int i = 0; i < active.Length; i++)
        {
            if (snapToCell) SnapToCell(active[i]);

            SpriteRenderer[] found = active[i].GetComponentsInChildren<SpriteRenderer>();
            for (int s = 0; s < found.Length; s++)
            {
                if (flashers.Contains(found[s])) continue;
                flashers.Add(found[s]);
                flasherColors.Add(found[s].color);
            }
        }
    }

    // ------------------------------------------------------------ the cycle

    protected override float FireSeconds
    {
        get
        {
            if (volley != Volley.OneAfterAnother || active == null) return 0f;
            return Mathf.Max(0, active.Length - 1) * sequenceDelay;
        }
    }

    protected override void OnWarnBegin()
    {
        SwapTiles(true);
    }

    protected override void OnWarnTick(float progress)
    {
        Flash(Mathf.PingPong(Time.time * 6f, 1f));
    }

    protected override void OnFireBegin()
    {
        RestoreFlash();
        if (active == null) return;

        nextInSequence = 0;
        sequenceTimer = 0f;

        switch (volley)
        {
            case Volley.AllAtOnce:
                for (int i = 0; i < active.Length; i++) Shoot(active[i]);
                break;

            case Volley.RandomOne:
                Shoot(active[Random.Range(0, active.Length)]);
                break;

            case Volley.OneAfterAnother:
                Shoot(active[0]);
                nextInSequence = 1;
                break;
        }
    }

    protected override void OnFireTick(float deltaTime)
    {
        if (volley != Volley.OneAfterAnother || active == null) return;

        sequenceTimer += deltaTime;
        while (nextInSequence < active.Length && sequenceTimer >= nextInSequence * sequenceDelay)
        {
            Shoot(active[nextInSequence]);
            nextInSequence++;
        }
    }

    protected override void OnFireEnd()
    {
        // The last arrows of a sweep, in case the final frame landed a hair early.
        if (volley == Volley.OneAfterAnother && active != null)
        {
            while (nextInSequence < active.Length)
            {
                Shoot(active[nextInSequence]);
                nextInSequence++;
            }
        }
        SwapTiles(false);
    }

    protected override void ClearEffects()
    {
        RestoreFlash();
        SwapTiles(false);
    }

    private void Shoot(Transform from)
    {
        if (from == null) return;

        Vector2 aim = AimDirection;
        Vector3 start = from.position + (Vector3)(aim * spawnOffset);

        GameObject go;
        if (arrowPrefab != null)
        {
            go = Instantiate(arrowPrefab, start, Quaternion.identity);
        }
        else
        {
            go = new GameObject("Trap Arrow");
            go.transform.position = start;
        }

        TrapArrow arrow = go.GetComponent<TrapArrow>();
        if (arrow == null) arrow = go.AddComponent<TrapArrow>();
        arrow.Launch(aim, arrowSpeed, arrowRadius, damage, maxRange, obstacleMask, arrowSprite, arrowSize, arrowColor, sortingOrder, logEvents);
    }

    // ------------------------------------------------------------ warning looks

    private void Flash(float amount)
    {
        for (int i = 0; i < flashers.Count; i++)
        {
            if (flashers[i] != null) flashers[i].color = Color.Lerp(flasherColors[i], warnColor, amount);
        }
    }

    private void RestoreFlash()
    {
        for (int i = 0; i < flashers.Count; i++)
        {
            if (flashers[i] != null) flashers[i].color = flasherColors[i];
        }
    }

    // on = true: put the Warning Tile under each shooter and remember what was there. on = false: put the old tiles back.
    private void SwapTiles(bool on)
    {
        if (warningMap == null) return;

        if (on)
        {
            if (warningTile == null || active == null || swappedCells.Count > 0) return;

            for (int i = 0; i < active.Length; i++)
            {
                Vector3Int cell = warningMap.WorldToCell(active[i].position);
                cell.z = 0;
                swappedCells.Add(cell);
                swappedTiles.Add(warningMap.GetTile(cell));
                warningMap.SetTile(cell, warningTile);
            }
            return;
        }

        for (int i = 0; i < swappedCells.Count; i++) warningMap.SetTile(swappedCells[i], swappedTiles[i]);
        swappedCells.Clear();
        swappedTiles.Clear();
    }

    // ------------------------------------------------------------ editor helper

    protected override void OnDrawGizmosSelected()
    {
        base.OnDrawGizmosSelected();

        Gizmos.color = Color.red;
        Vector2 aim = AimDirection;
        float length = Mathf.Min(maxRange, 4f);

        Transform[] from = shooters != null && shooters.Length > 0 ? shooters : new Transform[] { transform };
        for (int i = 0; i < from.Length; i++)
        {
            if (from[i] == null) continue;
            Vector3 a = from[i].position + (Vector3)(aim * spawnOffset);
            Gizmos.DrawLine(a, a + (Vector3)(aim * length));
            Gizmos.DrawWireSphere(a, 0.08f);
        }
    }
}
