using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// PULSE BEAM (built by SwordController; nothing to add by hand).
///
///   A laser with two strengths. Neither one ever grows: each has its own fixed size.
///
///     TAP the attack button   a short, thin, weak zap. One quick hit, and you can fire it again fast.
///     HOLD the attack button  once the button has been down for Hold Threshold seconds, the zap becomes the STRONG beam:
///                             longer, wider, and it keeps burning every foe inside it for as long as you hold.
///
///   Both beams hurt every foe inside them and stop at walls.
///
///   The strong beam can also pick things up. If the first thing it touches is an interactable object (a ToolInteractable,
///   or anything on the Interactable Mask layers) it latches on and PICKS IT UP instead. While it holds the object:
///       Next weapon key  pushes the object further away from the player
///       Previous weapon key  pulls it closer
///   Let go of the attack button to drop it where it is. (Turning round carries it round with you.)
///   (The weak tap beam never picks anything up, so tapping can't nudge crates around by accident.)
///
/// The strong beam shuts off by itself after Max Hold Time. Each beam then needs its own cooldown before the next shot.
/// </summary>
public class PulseBeamTool : PlayerTool
{
    [System.Serializable]
    public class Settings : ToolSettings
    {
        public Settings()
        {
            toolName = "Pulse Beam";
            shortLabel = "BEAM";
            color = new Color(0.85f, 0.5f, 1f);
        }

        [Header("Tap / Hold")]
        [Tooltip("Hold the attack button this many seconds and the weak tap beam turns into the strong beam. Let go sooner and it stays a tap.")]
        [Min(0.05f)] public float holdThreshold = 0.3f;
        [Tooltip("The shortest the weak beam stays on screen, so even a very quick tap shows as a flash.")]
        [Min(0.02f)] public float tapMinTime = 0.12f;
        [Tooltip("The strong beam shuts off after the attack button has been held this long.")]
        [Min(0.5f)] public float maxHoldTime = 5f;
        [Tooltip("Seconds before you can fire again after a weak tap.")]
        [Min(0f)] public float tapCooldown = 0.2f;
        [Tooltip("Seconds before you can fire again after the strong beam stops.")]
        [Min(0f)] public float cooldown = 0.5f;

        [Header("Tap beam (weak)")]
        [Min(0.5f)] public float tapLength = 4f;
        [Min(0.05f)] public float tapWidth = 0.3f;
        [Tooltip("Damage of the single hit a tap does to every foe in the beam.")]
        [Min(0f)] public float tapDamage = 6f;
        [Tooltip("How far the tap shoves a foe, in Unity units.")]
        [Min(0f)] public float tapKnockback = 0.3f;

        [Header("Hold beam (strong)")]
        [Min(0.5f)] public float holdLength = 7f;
        [Min(0.1f)] public float holdWidth = 1f;
        [Tooltip("Damage per second to a foe in the strong beam. It is the same the whole time the beam is on.")]
        [Min(0f)] public float holdDamagePerSecond = 35f;
        [Tooltip("Seconds between damage ticks of the strong beam.")]
        [Min(0.05f)] public float tickTime = 0.15f;
        [Tooltip("How far each tick of the strong beam shoves a foe, in Unity units.")]
        [Min(0f)] public float tickKnockback = 0.12f;

        [Header("Holding an object")]
        [Tooltip("The closest the held object can be pulled to the player, in Unity units.")]
        [Min(0.3f)] public float minHoldDistance = 0.9f;
        [Min(1f)] public float maxHoldDistance = 4.5f;
        [Tooltip("How far one press of the Next / Previous weapon key moves the object.")]
        [Min(0.1f)] public float holdStep = 0.5f;
        [Tooltip("How fast the object follows (Unity units per second).")]
        [Min(1f)] public float carrySpeed = 14f;

        [Header("Look")]
        public Color beamColor = new Color(0.85f, 0.5f, 1f);
        public Color coreColor = new Color(1f, 0.95f, 1f);
    }

    private readonly Settings s;

    private bool active;
    private bool strong;      // false = weak tap beam, true = strong hold beam
    private bool letGo;       // the button was released during the weak phase, so this shot stays a tap
    private bool tapHit;      // the tap's single hit has been dealt
    private float holdTime;
    private float tickTimer;
    private float cooldownLeft;
    private float cooldownTotal;
    private Vector2 dir = Vector2.down;
    private float beamLength;

    private CarriedObject carried;
    private float holdDistance;

    private GameObject visualRoot;
    private SpriteRenderer outer;
    private SpriteRenderer core;

    private readonly RaycastHit2D[] hits = new RaycastHit2D[24];
    private readonly Collider2D[] overlap = new Collider2D[32];
    private readonly HashSet<FoeController> tickFoes = new HashSet<FoeController>();
    private readonly HashSet<CoopHealth> tickPlayers = new HashSet<CoopHealth>();

    public PulseBeamTool(SwordController owner, Settings settings) : base(owner, settings)
    {
        s = settings;
    }

    public override ToolKind Kind { get { return ToolKind.PulseBeam; } }

    public override bool IsBusy { get { return active; } }
    public override bool WantsSwitchKeys { get { return carried != null; } }

    // Fills up while you hold toward the strong beam, then stays full while it is firing.
    public override float ChargeFraction
    {
        get
        {
            if (!active) return 0f;
            if (strong) return 1f;
            return letGo ? 0f : Mathf.Clamp01(holdTime / Mathf.Max(0.05f, s.holdThreshold));
        }
    }

    public override float CooldownFraction
    {
        get { return cooldownTotal > 0f ? Mathf.Clamp01(cooldownLeft / cooldownTotal) : 0f; }
    }

    // ------------------------------------------------------------ every frame

    public override void TickAlways(float dt)
    {
        if (cooldownLeft > 0f) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);
    }

    public override void OnUnequipped() { Cancel(); }

    public override void Cancel()
    {
        if (active || carried != null) StopBeam();
    }

    public override void DestroyVisuals()
    {
        Cancel();
        if (visualRoot != null) Object.Destroy(visualRoot);
    }

    public override void TickEquipped(float dt)
    {
        if (!active)
        {
            if (!owner.AttackPressedNow || cooldownLeft > 0f) return;
            StartBeam();   // the weak beam fires this same frame, so even a one-frame tap lands
        }

        holdTime += dt;
        bool holding = owner.AttackHeldNow;

        if (!strong)
        {
            // Weak phase: a tap, unless the button stays down until the threshold.
            if (!holding) letGo = true;

            if (letGo)
            {
                if (holdTime >= s.tapMinTime)
                {
                    StopBeam();
                    return;
                }
            }
            else if (holdTime >= s.holdThreshold)
            {
                strong = true;
                tickTimer = Mathf.Max(0.05f, s.tickTime);   // the strong beam bites right away
            }
        }
        else if (!holding || holdTime >= s.maxHoldTime)
        {
            StopBeam();
            return;
        }

        dir = owner.FacingVector;

        if (carried != null)
        {
            if (!carried.IsValid)
            {
                StopBeam();
                return;
            }

            if (owner.NextPressedNow) holdDistance += s.holdStep;
            else if (owner.PrevPressedNow) holdDistance -= s.holdStep;
            holdDistance = Mathf.Clamp(holdDistance, s.minHoldDistance, s.maxHoldDistance);
            return;
        }

        UpdateBeam(dt);
    }

    public override void FixedTickAlways(float dt)
    {
        if (carried == null) return;
        if (!carried.IsValid)
        {
            carried = null;
            return;
        }

        Vector2 target = (Vector2)owner.HandGround + dir * holdDistance;
        carried.MoveToward(target, s.carrySpeed * dt);
    }

    // ------------------------------------------------------------ the beam

    private void StartBeam()
    {
        active = true;
        strong = false;
        letGo = false;
        tapHit = false;
        holdTime = 0f;
        tickTimer = 0f;
        dir = owner.FacingVector;
        beamLength = s.tapLength;
    }

    private void StopBeam()
    {
        if (carried != null)
        {
            carried.End();
            carried = null;
        }

        cooldownTotal = Cooldown(strong ? s.cooldown : s.tapCooldown);
        cooldownLeft = cooldownTotal;

        active = false;
        strong = false;
        letGo = false;
        tapHit = false;
        holdTime = 0f;

        if (outer != null) outer.enabled = false;
        if (core != null) core.enabled = false;
    }

    // Works out how far the beam reaches this frame, lets the strong beam pick up an object it meets,
    // and hurts what is inside the beam (once for a tap, on every tick for the strong beam).
    private void UpdateBeam(float dt)
    {
        Vector2 origin = (Vector2)owner.HandGround + dir * 0.2f;
        float maxLength = strong ? s.holdLength : s.tapLength;
        float width = strong ? s.holdWidth : s.tapWidth;
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

        float wallDistance = maxLength;
        float foeDistance = float.MaxValue;
        float objectDistance = float.MaxValue;
        Transform objectRoot = null;
        ToolInteractable objectMarker = null;

        int count = Physics2D.BoxCast(origin, new Vector2(0.15f, width), angle, dir, owner.FoeFilter, hits, maxLength);
        for (int i = 0; i < count; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.transform.IsChildOf(owner.transform)) continue;
            float d = hits[i].distance;

            if (c.GetComponentInParent<FoeController>() != null)
            {
                foeDistance = Mathf.Min(foeDistance, d);
                continue;
            }
            if (c.GetComponentInParent<CoopHealth>() != null) continue;   // players don't block the beam (PVP hurts them below)

            ToolInteractable marker;
            Transform root = owner.FindInteractable(c, false, out marker);
            if (root != null)
            {
                if (d < objectDistance)
                {
                    objectDistance = d;
                    objectRoot = root;
                    objectMarker = marker;
                }
                continue;
            }

            if (owner.IsWall(c, false)) wallDistance = Mathf.Min(wallDistance, d);
        }

        // Only the strong beam picks things up. An object is the first thing in the way (no foe in front of it, no wall): latch on.
        if (strong && objectRoot != null && objectDistance < wallDistance && objectDistance < foeDistance)
        {
            CarriedObject grabbed = new CarriedObject();
            if (grabbed.Begin(objectRoot, owner, objectMarker))
            {
                carried = grabbed;
                holdDistance = Mathf.Clamp(Vector2.Distance(owner.HandGround, grabbed.Position), s.minHoldDistance, s.maxHoldDistance);
                return;
            }
        }

        beamLength = Mathf.Min(maxLength, wallDistance);

        if (strong)
        {
            tickTimer += dt;
            float tick = Mathf.Max(0.05f, s.tickTime);
            if (tickTimer >= tick)
            {
                tickTimer -= tick;
                HurtInBeam(origin, beamLength, width, angle, Damage(s.holdDamagePerSecond) * tick, s.tickKnockback);
            }
        }
        else if (!tapHit)
        {
            // A tap is one clean hit the moment it fires.
            tapHit = true;
            HurtInBeam(origin, beamLength, width, angle, Damage(s.tapDamage), s.tapKnockback);
        }
    }

    private void HurtInBeam(Vector2 origin, float length, float width, float angle, float damage, float knockback)
    {
        Vector2 center = origin + dir * (length * 0.5f);
        int count = Physics2D.OverlapBox(center, new Vector2(length, width), angle, owner.FoeFilter, overlap);

        tickFoes.Clear();
        tickPlayers.Clear();
        for (int i = 0; i < count; i++)
        {
            Collider2D c = overlap[i];
            if (c == null || c.transform.IsChildOf(owner.transform)) continue;

            FoeController foe = c.GetComponentInParent<FoeController>();
            if (foe != null)
            {
                if (!foe.IsDead && tickFoes.Add(foe)) owner.DamageFoe(foe, damage, dir, knockback);
                continue;
            }

            CoopHealth other = c.GetComponentInParent<CoopHealth>();
            if (other != null && tickPlayers.Add(other)) owner.PvpTryHurt(other, damage, dir, knockback);
        }
    }

    // ------------------------------------------------------------ look

    public override void LateTickAlways()
    {
        if (!active)
        {
            if (outer != null && outer.enabled) outer.enabled = false;
            if (core != null && core.enabled) core.enabled = false;
            return;
        }

        if (visualRoot == null)
        {
            visualRoot = new GameObject("PulseBeam");
            outer = ToolDraw.NewSquare("Outer", visualRoot.transform);
            core = ToolDraw.NewSquare("Core", visualRoot.transform);
        }

        Vector2 from = (Vector2)owner.HandVisual + dir * 0.2f;
        float z = owner.transform.position.z;
        float pulse = 1f + 0.1f * Mathf.Sin(Time.time * 35f);

        Color outerColor = s.beamColor;
        Color coreColor = s.coreColor;
        float width;
        Vector2 to;

        if (carried != null && carried.IsValid)
        {
            // A thin tether to the held object.
            to = carried.Position;
            width = 0.16f * pulse;
            outerColor.a = 0.55f;
        }
        else
        {
            // Each beam has a fixed size: the tap is thin, the hold beam is wide. Only the small shimmer changes.
            to = from + dir * beamLength;
            width = (strong ? s.holdWidth : s.tapWidth) * pulse;
            outerColor.a = strong ? 0.5f : 0.4f;
        }

        ToolDraw.Bar(outer, from, to, width, outerColor, z);
        ToolDraw.Bar(core, from, to, width * 0.35f, coreColor, z);
        ToolDraw.Sort(outer, owner.SortingLayerId, owner.SortingOrder + 3);
        ToolDraw.Sort(core, owner.SortingLayerId, owner.SortingOrder + 4);
    }
}
