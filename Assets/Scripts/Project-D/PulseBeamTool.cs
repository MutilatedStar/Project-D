using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// PULSE BEAM (built by SwordController; nothing to add by hand).
///
///   Hold the attack button and a beam shoots the way the player faces. The longer it is held the BIGGER it gets (longer and wider)
///   and the MORE damage per second it does. It hurts every foe inside it, and stops at walls.
///   If the first thing the beam touches is an interactable object (a ToolInteractable, or anything on the Interactable Mask layers) it
///   latches on and PICKS IT UP instead. While it holds the object:
///       Next weapon key  pushes the object further away from the player
///       Previous weapon key  pulls it closer
///   Let go of the attack button to drop it where it is. (Turning round carries it round with you.)
/// The beam shuts off by itself after Max Hold Time, then needs its Cooldown.
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

        [Header("Beam")]
        [Tooltip("Seconds of holding until the beam is at full size and full damage.")]
        [Min(0.1f)] public float chargeTime = 2.2f;
        [Tooltip("The beam shuts off after being held this long.")]
        [Min(0.5f)] public float maxHoldTime = 5f;
        [Tooltip("Seconds before the beam can be used again after it stops.")]
        [Min(0f)] public float cooldown = 0.5f;
        [Min(0.5f)] public float minLength = 3f;
        [Min(0.5f)] public float maxLength = 7f;
        [Min(0.1f)] public float minWidth = 0.35f;
        [Min(0.1f)] public float maxWidth = 1.4f;

        [Header("Damage")]
        [Tooltip("Damage per second to a foe in the beam, at the start of the hold.")]
        [Min(0f)] public float minDamagePerSecond = 12f;
        [Tooltip("Damage per second at full charge.")]
        [Min(0f)] public float maxDamagePerSecond = 45f;
        [Tooltip("Seconds between damage ticks.")]
        [Min(0.05f)] public float tickTime = 0.15f;
        [Tooltip("How far each tick shoves a foe, in Unity units.")]
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
    private float holdTime;
    private float charge;
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

    public override float ChargeFraction { get { return active ? charge : 0f; } }

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
            if (owner.AttackPressedNow && cooldownLeft <= 0f) StartBeam();
            return;
        }

        if (!owner.AttackHeldNow)
        {
            StopBeam();
            return;
        }

        holdTime += dt;
        charge = Mathf.Clamp01(holdTime / Mathf.Max(0.1f, s.chargeTime));
        if (holdTime >= s.maxHoldTime)
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
        holdTime = 0f;
        charge = 0f;
        tickTimer = 0f;
        dir = owner.FacingVector;
        beamLength = s.minLength;
    }

    private void StopBeam()
    {
        if (carried != null)
        {
            carried.End();
            carried = null;
        }

        active = false;
        charge = 0f;
        holdTime = 0f;
        cooldownTotal = Cooldown(s.cooldown);
        cooldownLeft = cooldownTotal;

        if (outer != null) outer.enabled = false;
        if (core != null) core.enabled = false;
    }

    // Works out how far the beam reaches this frame, picks up an object it meets, and hurts what is inside it on every tick.
    private void UpdateBeam(float dt)
    {
        Vector2 origin = (Vector2)owner.HandGround + dir * 0.2f;
        float maxLength = Mathf.Lerp(s.minLength, s.maxLength, charge);
        float width = Mathf.Lerp(s.minWidth, s.maxWidth, charge);
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

        // An object is the first thing in the way (no foe in front of it, no wall): latch on and pick it up.
        if (objectRoot != null && objectDistance < wallDistance && objectDistance < foeDistance)
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

        tickTimer += dt;
        float tick = Mathf.Max(0.05f, s.tickTime);
        if (tickTimer >= tick)
        {
            tickTimer -= tick;
            float perSecond = Mathf.Lerp(s.minDamagePerSecond, s.maxDamagePerSecond, charge);
            HurtInBeam(origin, beamLength, width, angle, Damage(perSecond) * tick);
        }
    }

    private void HurtInBeam(Vector2 origin, float length, float width, float angle, float damage)
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
                if (!foe.IsDead && tickFoes.Add(foe)) owner.DamageFoe(foe, damage, dir, s.tickKnockback);
                continue;
            }

            CoopHealth other = c.GetComponentInParent<CoopHealth>();
            if (other != null && tickPlayers.Add(other)) owner.PvpTryHurt(other, damage, dir, s.tickKnockback);
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
            to = from + dir * beamLength;
            width = Mathf.Lerp(s.minWidth, s.maxWidth, charge) * pulse;
            outerColor.a = 0.4f;
        }

        ToolDraw.Bar(outer, from, to, width, outerColor, z);
        ToolDraw.Bar(core, from, to, width * 0.35f, coreColor, z);
        ToolDraw.Sort(outer, owner.SortingLayerId, owner.SortingOrder + 3);
        ToolDraw.Sort(core, owner.SortingLayerId, owner.SortingOrder + 4);
    }
}
