using UnityEngine;
using Ilumisoft.HealthSystem;

/// <summary>
/// Fires a laser beam out of a tile. Put it on an empty object that sits on the emitter tile (or drag the emitter into Emitter).
///
/// The cycle (see TrapBase): the trap warns with a thin pulsing line, then the real beam is ON for "Fire Seconds" and hurts every
/// player it touches once per "Tick Seconds", then it switches off and rests for Interval.
///
///   Aim            Right / Up / Left / Down, or Custom with an angle.
///   Beam           Max Length, Beam Width and colours. The beam is a LineRenderer made for you (Beam Material is optional).
///   Obstacle Mask  the layer(s) of your WALL colliders. The beam stops at the first one. Empty = it always goes its whole Max Length.
///   Sweep          Sweep Degrees Per Second above 0 makes the beam turn while it is on. Sweep Arc 0 = it keeps spinning round,
///                  an arc (say 90) = it swings back and forth across that arc, centred on the Aim direction.
///   Damage         Medium value PER TICK (hard hits twice as hard, easy half).
///
/// Players are found by their own collider box, so no layers are needed for them. A jumping player is never hit.
/// </summary>
[AddComponentMenu("Traps/Laser Trap")]
public class LaserTrap : TrapBase
{
    [Header("Where it shoots from")]
    [Tooltip("The emitter tile's object. Leave EMPTY to shoot from this object itself.")]
    [SerializeField] private Transform emitter;
    [Tooltip("Moves the emitter to the middle of its tile cell when the floor starts.")]
    [SerializeField] private bool snapToCell = true;

    [Header("Aim")]
    [SerializeField] private TrapDirection direction = TrapDirection.Right;
    [Tooltip("Only used when Direction is Custom. Degrees: 0 = right, 90 = up, 180 = left, 270 = down.")]
    [SerializeField] private float customAngle = 0f;

    [Header("Beam")]
    [Tooltip("How long the beam is when nothing stops it (world units).")]
    [SerializeField, Min(0.5f)] private float maxLength = 10f;
    [Tooltip("Where the beam starts, in front of the emitter. Keep it a bit more than half a tile so it starts clear of the emitter's wall tile.")]
    [SerializeField, Min(0f)] private float startOffset = 0.55f;
    [Tooltip("The layer(s) of your WALL colliders. The beam stops at the first one. Empty = the beam always goes its whole Max Length.")]
    [SerializeField] private LayerMask obstacleMask = 0;
    [SerializeField, Min(0.02f)] private float beamWidth = 0.25f;
    [SerializeField] private Color beamColor = new Color(1f, 0.15f, 0.15f, 1f);
    [Tooltip("The thin line shown during the warning.")]
    [SerializeField] private Color warnColor = new Color(1f, 0.4f, 0.4f, 0.8f);
    [Tooltip("Optional. Leave empty and the beam uses Unity's built-in Sprites/Default shader.")]
    [SerializeField] private Material beamMaterial;
    [SerializeField] private int sortingOrder = 6;

    [Header("Firing")]
    [Tooltip("Seconds the beam stays on.")]
    [SerializeField, Min(0.05f)] private float fireSeconds = 1.5f;
    [Tooltip("A player standing in the beam is hurt once per this many seconds.")]
    [SerializeField, Min(0.05f)] private float tickSeconds = 0.3f;
    [Tooltip("Medium damage of ONE tick.")]
    [SerializeField, Min(0f)] private float damage = 8f;

    [Header("Sweep (optional)")]
    [Tooltip("Degrees per second the beam turns while it is on. 0 = it stays pointed where it is aimed. Negative turns the other way.")]
    [SerializeField] private float sweepDegreesPerSecond = 0f;
    [Tooltip("0 = keeps spinning all the way round. Above 0 = swings back and forth across this many degrees, centred on the Aim direction.")]
    [SerializeField, Min(0f)] private float sweepArc = 0f;

    private Transform source;
    private LineRenderer line;
    private Collider2D ignoredWall;
    private float firingTime;
    private float sweepOffset;
    private readonly float[] nextHitTime = new float[TrapPlayers.MaxPlayers + 1];

    /// <summary>The way the beam points at the start of a shot.</summary>
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

        source = emitter != null ? emitter : transform;
        if (snapToCell) SnapToCell(source);
        EnsureLine();
    }

    private void EnsureLine()
    {
        if (line != null) return;

        GameObject go = new GameObject("Laser Beam");
        go.transform.SetParent(transform, false);

        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.numCapVertices = 2;
        line.sortingOrder = sortingOrder;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        Material m = beamMaterial != null ? beamMaterial : TrapFloor.SpriteMaterial;
        if (m != null) line.sharedMaterial = m;
        line.enabled = false;
    }

    // ------------------------------------------------------------ the cycle

    protected override float FireSeconds { get { return fireSeconds; } }

    protected override void OnWarnBegin()
    {
        EnsureLine();
        ignoredWall = TrapGeometry.SolidAt(source.position, obstacleMask);
        firingTime = 0f;
        sweepOffset = 0f;
        line.enabled = true;
        DrawBeam(beamWidth * 0.25f, warnColor);
    }

    protected override void OnWarnTick(float progress)
    {
        Color c = warnColor;
        c.a = Mathf.Lerp(0.3f, 1f, Mathf.PingPong(Time.time * 6f, 1f));
        DrawBeam(beamWidth * 0.25f, c);
    }

    protected override void OnFireBegin()
    {
        EnsureLine();
        if (phase == TrapPhase.Firing && line != null && !line.enabled)
        {
            // No warning (Warn Seconds is 0): the beam starts from here.
            ignoredWall = TrapGeometry.SolidAt(source.position, obstacleMask);
            line.enabled = true;
        }

        firingTime = 0f;
        sweepOffset = 0f;
        for (int i = 0; i < nextHitTime.Length; i++) nextHitTime[i] = 0f;   // the first tick lands the moment it switches on

        Vector2 origin;
        Vector2 end;
        DrawBeam(beamWidth, beamColor, out origin, out end);
        HurtPlayersOn(origin, end);
    }

    protected override void OnFireTick(float deltaTime)
    {
        firingTime += deltaTime;

        if (sweepDegreesPerSecond != 0f)
        {
            if (sweepArc > 0f)
            {
                float half = sweepArc * 0.5f;
                sweepOffset = Mathf.Sin(firingTime * sweepDegreesPerSecond / half) * half;
            }
            else
            {
                sweepOffset = firingTime * sweepDegreesPerSecond;
            }
        }

        Vector2 origin;
        Vector2 end;
        DrawBeam(beamWidth, beamColor, out origin, out end);
        HurtPlayersOn(origin, end);
    }

    protected override void OnFireEnd()
    {
        if (line != null) line.enabled = false;
    }

    protected override void ClearEffects()
    {
        if (line != null) line.enabled = false;
    }

    // ------------------------------------------------------------ the beam

    private Vector2 CurrentDirection
    {
        get { return TrapGeometry.Rotate(AimDirection, sweepOffset); }
    }

    private void DrawBeam(float width, Color color)
    {
        Vector2 origin;
        Vector2 end;
        DrawBeam(width, color, out origin, out end);
    }

    // Works out where the beam starts and where it stops (a wall, or its full length), and draws it.
    private void DrawBeam(float width, Color color, out Vector2 origin, out Vector2 end)
    {
        Vector2 dir = CurrentDirection;
        origin = (Vector2)source.position + dir * startOffset;
        float length = TrapGeometry.FirstSolidDistance(origin, dir, maxLength, obstacleMask, ignoredWall);
        end = origin + dir * length;

        if (line == null) return;

        float z = source.position.z;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        line.SetPosition(0, new Vector3(origin.x, origin.y, z));
        line.SetPosition(1, new Vector3(end.x, end.y, z));
    }

    private void HurtPlayersOn(Vector2 origin, Vector2 end)
    {
        float pad = beamWidth * 0.5f;

        for (int n = 1; n <= TrapPlayers.MaxPlayers; n++)
        {
            CoopHealth p = CoopHealth.Get(n);
            if (!TrapPlayers.CanBeHurt(p)) continue;
            if (Time.time < nextHitTime[n]) continue;

            float along;
            if (!TrapGeometry.SegmentHitsBounds(origin, end, TrapPlayers.BoundsOf(p), pad, out along)) continue;

            nextHitTime[n] = Time.time + tickSeconds;
            TrapDamage.Hurt(p, damage, "a laser", logEvents);
        }
    }

    // ------------------------------------------------------------ editor helper

    protected override void OnDrawGizmosSelected()
    {
        base.OnDrawGizmosSelected();

        Transform from = emitter != null ? emitter : transform;
        Vector2 dir = AimDirection;
        Vector3 a = from.position + (Vector3)(dir * startOffset);

        Gizmos.color = Color.red;
        Gizmos.DrawLine(a, a + (Vector3)(dir * maxLength));

        if (sweepDegreesPerSecond != 0f && sweepArc > 0f)
        {
            Gizmos.color = new Color(1f, 0.5f, 0.5f, 0.6f);
            Gizmos.DrawLine(a, a + (Vector3)(TrapGeometry.Rotate(dir, sweepArc * 0.5f) * maxLength));
            Gizmos.DrawLine(a, a + (Vector3)(TrapGeometry.Rotate(dir, -sweepArc * 0.5f) * maxLength));
        }
    }
}
