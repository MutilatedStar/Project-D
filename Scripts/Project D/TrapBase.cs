using UnityEngine;
using UnityEngine.Events;
using Ilumisoft.HealthSystem;

/// <summary>
/// The shared brain of ArrowTrap, LaserTrap and TileRemoveTrap. It runs ONE cycle and the three traps only fill in what happens:
///
///     wait (Interval)  ->  warning (Warn Seconds: flash / thin line / cracking tiles)  ->  firing  ->  wait again
///
/// How the cycle starts is the Mode:
///   Timed       goes off by itself, over and over. "Wake Radius" keeps it asleep until a player is near (0 = always awake).
///   Proximity   goes off when a player (that counts) comes within "Trigger Radius", then rests for Interval.
///   Manual      only goes off when something calls Trigger() (a UnityEvent, a pressure plate, a lever, your own script).
///
/// A trap pauses with the game (menus, the pause screen, floor changes, the Lights Out screen) and starts its cycle over whenever
/// the floor changes or the floor is restarted (FloorController.FloorChanged, FloorManager.OnFloorRestarted).
///
/// "Active On Floors": the floor numbers a trap works on (empty = every floor). It is how you give ONE trap to ONE floor.
///
/// Players are hurt through CoopHealth.ApplyDamage, scaled by DifficultyRules.TrapDamageScale, so Invincible and the CoopRules apply.
/// A jumping player (CoopHealth.Airborne) is never hit.
/// </summary>
public abstract class TrapBase : MonoBehaviour
{
    [Header("When it goes off")]
    [Tooltip("Timed: goes off by itself over and over. Proximity: goes off when a player comes close. Manual: only when Trigger() is called.")]
    [SerializeField] protected TrapMode mode = TrapMode.Timed;
    [Tooltip("Seconds before the FIRST shot after the floor starts.")]
    [SerializeField, Min(0f)] protected float startDelay = 1.5f;
    [Tooltip("A random extra 0 to this many seconds added to Start Delay, so traps placed together don't all go off in the same moment.")]
    [SerializeField, Min(0f)] protected float startDelayRandom = 1f;
    [Tooltip("Seconds of quiet after a shot before the next one can start. Timed: the gap between shots. Proximity: the rest after it went off.")]
    [SerializeField, Min(0.05f)] protected float interval = 3f;
    [Tooltip("Seconds of warning before the shot goes off (the emitter flashes, the laser draws a thin line, the tiles crack). 0 = no warning.")]
    [SerializeField, Min(0f)] protected float warnSeconds = 0.75f;
    [Tooltip("Timed only: the trap stays asleep until a player is this close (world units). 0 = always awake. Keeps far-away traps from wasting their shots.")]
    [SerializeField, Min(0f)] protected float wakeRadius = 12f;
    [Tooltip("Proximity only: how close a player has to come (world units) to set it off.")]
    [SerializeField, Min(0f)] protected float triggerRadius = 4f;
    [Tooltip("The floor numbers this trap works on. Empty = every floor.")]
    [SerializeField] protected int[] activeOnFloors;

    [Header("Events (optional: sounds, animations, particles)")]
    [Tooltip("Raised when the warning starts.")]
    public UnityEvent onWarn;
    [Tooltip("Raised the moment the trap goes off.")]
    public UnityEvent onFire;
    [Tooltip("Raised when the shot is over.")]
    public UnityEvent onFinish;

    [Header("Debug")]
    [Tooltip("Prints a Console line every time this trap hurts a player or does something worth knowing about.")]
    [SerializeField] protected bool logEvents = false;

    // ------------------------------------------------------------ shared state

    /// <summary>True while a menu, the pause screen, a floor change or the Lights Out screen is up. Everything the traps make waits.</summary>
    public static bool Paused
    {
        get { return FloorController.BlocksInput || MenuController.IsOpen || PauseController.IsPaused || LightsOutScreen.IsActive; }
    }

    protected TrapPhase phase = TrapPhase.Idle;
    protected float waitTimer;       // counts down while Idle
    private float phaseTimer;        // counts down while Warning / Firing

    public TrapPhase Phase { get { return phase; } }

    /// <summary>False while this trap's Active On Floors list leaves out the floor the team is on.</summary>
    public bool ActiveOnThisFloor
    {
        get
        {
            if (activeOnFloors == null || activeOnFloors.Length == 0) return true;
            return System.Array.IndexOf(activeOnFloors, FloorController.CurrentFloor) >= 0;
        }
    }

    // ------------------------------------------------------------ what the three traps fill in

    /// <summary>Seconds the "firing" part lasts. 0 = the shot is instant.</summary>
    protected virtual float FireSeconds { get { return 0f; } }

    /// <summary>A trap can say "not yet" (the tile trap while its tiles are still gone).</summary>
    protected virtual bool CanStartCycle { get { return true; } }

    protected virtual void OnWarnBegin() { }
    protected virtual void OnWarnTick(float progress01) { }
    protected virtual void OnFireBegin() { }
    protected virtual void OnFireTick(float deltaTime) { }
    protected virtual void OnFireEnd() { }

    /// <summary>Put everything this trap changed back (colours, tiles, the beam). Runs on every reset and when the trap is switched off.</summary>
    protected virtual void ClearEffects() { }

    // ------------------------------------------------------------ setup

    protected virtual void OnEnable()
    {
        FloorController.FloorChanged += HandleFloorChanged;
        FloorManager.OnFloorRestarted += HandleFloorRestarted;
        ResetTrap();
    }

    protected virtual void OnDisable()
    {
        FloorController.FloorChanged -= HandleFloorChanged;
        FloorManager.OnFloorRestarted -= HandleFloorRestarted;
        ClearEffects();
        phase = TrapPhase.Idle;
    }

    private void HandleFloorChanged(int floor)
    {
        ResetTrap();
    }

    private void HandleFloorRestarted()
    {
        ResetTrap();
    }

    /// <summary>Stops whatever the trap is doing, puts everything back, and starts the wait again from Start Delay.</summary>
    public void ResetTrap()
    {
        ClearEffects();
        phase = TrapPhase.Idle;
        waitTimer = startDelay + (startDelayRandom > 0f ? Random.value * startDelayRandom : 0f);
    }

    /// <summary>Sets the trap off now (with its warning first). Does nothing while it is already going off, paused, or on another floor.</summary>
    public void Trigger()
    {
        if (phase != TrapPhase.Idle || Paused || !ActiveOnThisFloor || !CanStartCycle) return;
        BeginWarning();
    }

    // ------------------------------------------------------------ the cycle

    protected virtual void Update()
    {
        if (Paused || !ActiveOnThisFloor) return;

        switch (phase)
        {
            case TrapPhase.Idle:
                TickIdle();
                break;

            case TrapPhase.Warning:
                phaseTimer -= Time.deltaTime;
                OnWarnTick(warnSeconds > 0f ? Mathf.Clamp01(1f - phaseTimer / warnSeconds) : 1f);
                if (phaseTimer <= 0f) BeginFiring();
                break;

            case TrapPhase.Firing:
                OnFireTick(Time.deltaTime);
                phaseTimer -= Time.deltaTime;
                if (phaseTimer <= 0f) EndFiring();
                break;
        }
    }

    private void TickIdle()
    {
        if (mode == TrapMode.Manual || !CanStartCycle) return;
        if (mode == TrapMode.Timed && wakeRadius > 0f && !PlayerWithin(wakeRadius)) return;

        waitTimer -= Time.deltaTime;
        if (waitTimer > 0f) return;

        if (mode == TrapMode.Proximity && !PlayerWithin(triggerRadius))
        {
            waitTimer = 0f;   // rested, now it just waits for somebody to come close
            return;
        }
        BeginWarning();
    }

    private void BeginWarning()
    {
        if (warnSeconds <= 0f)
        {
            BeginFiring();
            return;
        }

        phase = TrapPhase.Warning;
        phaseTimer = warnSeconds;
        OnWarnBegin();
        onWarn?.Invoke();
    }

    private void BeginFiring()
    {
        phase = TrapPhase.Firing;
        phaseTimer = FireSeconds;
        OnFireBegin();
        onFire?.Invoke();

        if (phaseTimer <= 0f) EndFiring();
    }

    private void EndFiring()
    {
        phase = TrapPhase.Idle;
        waitTimer = interval;
        OnFireEnd();
        onFinish?.Invoke();
    }

    // ------------------------------------------------------------ helpers

    /// <summary>True if a player that counts and is alive is within this distance of the trap.</summary>
    protected bool PlayerWithin(float radius)
    {
        float limit = radius * radius;
        Vector2 here = transform.position;

        for (int n = 1; n <= TrapPlayers.MaxPlayers; n++)
        {
            CoopHealth p = CoopHealth.Get(n);
            if (!TrapPlayers.InPlay(p)) continue;
            if (((Vector2)p.transform.position - here).sqrMagnitude <= limit) return true;
        }
        return false;
    }

    /// <summary>Moves a point to the middle of the cell it is in (so a trap sits exactly on its tile).</summary>
    protected static void SnapToCell(Transform t)
    {
        Grid grid = TrapFloor.GridFor(null);
        if (grid == null || t == null) return;

        Vector3 centre = grid.GetCellCenterWorld(grid.WorldToCell(t.position));
        t.position = new Vector3(centre.x, centre.y, t.position.z);
    }

    // ------------------------------------------------------------ editor helper

    protected virtual void OnDrawGizmosSelected()
    {
        if (mode == TrapMode.Timed && wakeRadius > 0f)
        {
            Gizmos.color = new Color(1f, 1f, 0f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, wakeRadius);
        }
        else if (mode == TrapMode.Proximity)
        {
            Gizmos.color = new Color(1f, 0.4f, 0f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, triggerRadius);
        }
    }
}
