using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// GRAPPLE ARM (built by SwordController; nothing to add by hand).
///
///   Tap   a quick swipe in front of the player that hurts foes (and breaks crates, like the sword did).
///   Hold  charge the grapple: the longer the button is held the FURTHER the hook goes (a faint line shows how far). Let go to fire it.
///           Wall          the hook grabs on and PULLS THE PLAYER to it.
///           Interactable  (a ToolInteractable, or anything on the Interactable Mask layers) it is REELED IN to the player.
///           Foe           a little damage, and the foe is yanked toward the player (turn this off with Grab Foes).
///           Nothing       the hook flies out to its range and comes back.
/// </summary>
public class GrappleArmTool : PlayerTool
{
    [System.Serializable]
    public class Settings : ToolSettings
    {
        public Settings()
        {
            toolName = "Grapple Arm";
            shortLabel = "GRAP";
            color = new Color(1f, 0.65f, 0.25f);
        }

        [Header("Tap: swipe")]
        [Tooltip("Pressing for less than this many seconds is a tap (the swipe). Holding longer charges the grapple.")]
        [Min(0.05f)] public float tapMaxTime = 0.22f;
        [Min(0.3f)] public float swipeReach = 1.4f;
        [Range(30f, 180f)] public float swipeArc = 120f;
        [Min(0.05f)] public float swipeTime = 0.2f;
        [Min(0f)] public float swipeDamage = 14f;
        [Min(0f)] public float swipeKnockback = 0.8f;
        [Tooltip("Seconds before the next tap or grapple after a swipe.")]
        [Min(0f)] public float swipeCooldown = 0.3f;

        [Header("Hold: grapple")]
        [Tooltip("Seconds before the grapple can be used again after it finishes.")]
        [Min(0f)] public float cooldown = 0.8f;
        [Tooltip("Seconds of holding (after the tap time) until the hook reaches its longest range.")]
        [Min(0.1f)] public float chargeTime = 1.2f;
        [Min(0.5f)] public float minRange = 2.5f;
        [Min(1f)] public float maxRange = 9f;
        [Tooltip("How fast the hook flies, in Unity units per second.")]
        [Min(5f)] public float hookSpeed = 32f;

        [Header("Pulling the player to a wall")]
        [Min(2f)] public float pullSpeed = 18f;
        [Tooltip("The pull stops this far (Unity units) from the spot the hook grabbed.")]
        [Min(0.2f)] public float stopDistance = 0.8f;
        [Tooltip("The pull gives up after this many seconds.")]
        [Min(0.2f)] public float maxPullTime = 1.5f;

        [Header("Reeling in an object")]
        [Min(2f)] public float reelSpeed = 14f;
        [Tooltip("Gap left between the player and the reeled-in object.")]
        [Min(0f)] public float reelGap = 0.2f;

        [Header("Foes")]
        public bool grabFoes = true;
        [Min(0f)] public float hookDamage = 6f;
        [Tooltip("How far a hooked foe is yanked toward the player, in Unity units.")]
        [Min(0f)] public float foePullDistance = 1.8f;

        [Header("Look")]
        public Color ropeColor = new Color(0.9f, 0.8f, 0.6f);
        public Color swipeColor = new Color(1f, 0.75f, 0.3f);
    }

    private enum Mode { Idle, Charging, Swiping, HookOut, Retract, Pulling, Reeling }

    private readonly Settings s;
    private Mode mode = Mode.Idle;

    private float holdTime;
    private float cooldownLeft;
    private float cooldownTotal;

    // swipe
    private float swipeTimer;
    private float swipeFaceAngle;
    private float swipeSign;
    private float swipePrevious;
    private float sweepFrom;
    private float sweepSpan;
    private Vector2 sweepOrigin;
    private float swipeCurrentAngle;
    private readonly System.Func<Vector2, bool> sweepTest;
    private readonly HashSet<FoeController> swipeFoes = new HashSet<FoeController>();
    private readonly HashSet<CoopHealth> swipePlayers = new HashSet<CoopHealth>();

    // hook
    private Vector2 hookDir = Vector2.down;
    private float hookDist;
    private float hookRange;
    private Vector2 attachPoint;
    private float actionTimer;
    private float stuckTimer;
    private float retractTimer;
    private CarriedObject carried;

    private GameObject visualRoot;
    private SpriteRenderer rope;
    private SpriteRenderer head;
    private SpriteRenderer arm;
    private SpriteRenderer preview;

    private readonly RaycastHit2D[] hits = new RaycastHit2D[24];
    private readonly RaycastHit2D[] castBuffer = new RaycastHit2D[16];
    private readonly Collider2D[] overlap = new Collider2D[32];

    public GrappleArmTool(SwordController owner, Settings settings) : base(owner, settings)
    {
        s = settings;
        sweepTest = PointInSweep;
    }

    public override ToolKind Kind { get { return ToolKind.GrappleArm; } }

    public override bool IsBusy { get { return mode != Mode.Idle; } }

    public override float CooldownFraction
    {
        get { return cooldownTotal > 0f ? Mathf.Clamp01(cooldownLeft / cooldownTotal) : 0f; }
    }

    public override float ChargeFraction
    {
        get
        {
            if (mode != Mode.Charging || holdTime <= s.tapMaxTime) return 0f;
            return Mathf.Clamp01((holdTime - s.tapMaxTime) / Mathf.Max(0.1f, s.chargeTime));
        }
    }

    // ------------------------------------------------------------ every frame

    public override void TickAlways(float dt)
    {
        if (cooldownLeft > 0f) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);
    }

    public override void OnUnequipped() { Cancel(); }

    public override void Cancel()
    {
        if (mode == Mode.Idle && carried == null) return;

        if (mode == Mode.Pulling) EndPullMovement();
        if (carried != null)
        {
            carried.End();
            carried = null;
        }
        mode = Mode.Idle;
        HideAll();
    }

    public override void DestroyVisuals()
    {
        Cancel();
        if (visualRoot != null) Object.Destroy(visualRoot);
    }

    public override void TickEquipped(float dt)
    {
        switch (mode)
        {
            case Mode.Idle:
                if (owner.AttackPressedNow && cooldownLeft <= 0f)
                {
                    mode = Mode.Charging;
                    holdTime = 0f;
                }
                break;

            case Mode.Charging:
                if (owner.AttackHeldNow)
                {
                    holdTime += dt;
                }
                else if (holdTime < s.tapMaxTime)
                {
                    StartSwipe();
                }
                else
                {
                    FireHook();
                }
                break;

            case Mode.Swiping:
                UpdateSwipe(dt);
                break;

            case Mode.HookOut:
                UpdateHook(dt);
                break;

            case Mode.Retract:
                retractTimer -= dt;
                if (retractTimer <= 0f) Finish(s.cooldown);
                break;
        }
    }

    public override void FixedTickAlways(float dt)
    {
        if (mode == Mode.Pulling) PullStep(dt);
        else if (mode == Mode.Reeling) ReelStep(dt);
    }

    private void Finish(float cooldownSeconds)
    {
        mode = Mode.Idle;
        cooldownTotal = Cooldown(cooldownSeconds);
        cooldownLeft = cooldownTotal;
        HideAll();
    }

    // ------------------------------------------------------------ tap: swipe

    private void StartSwipe()
    {
        swipeFaceAngle = owner.FacingAngle;
        swipeSign = owner.FacingSweepSign;
        swipeTimer = 0f;
        swipePrevious = swipeFaceAngle + s.swipeArc * 0.5f * swipeSign;
        swipeCurrentAngle = swipePrevious;
        swipeFoes.Clear();
        swipePlayers.Clear();
        mode = Mode.Swiping;
    }

    private void UpdateSwipe(float dt)
    {
        swipeTimer += dt;
        float t = Mathf.Clamp01(swipeTimer / Mathf.Max(0.05f, s.swipeTime));
        float eased = t * t * (3f - 2f * t);

        float startOffset = s.swipeArc * 0.5f * swipeSign;
        float angle = swipeFaceAngle + Mathf.Lerp(startOffset, -startOffset, eased);
        swipeCurrentAngle = angle;

        if (t >= 0.2f)
        {
            SweepHit(swipePrevious, angle);
            swipePrevious = angle;
        }

        if (t >= 1f) Finish(s.swipeCooldown);
    }

    private void SweepHit(float fromAngle, float toAngle)
    {
        Vector2 origin = owner.HandGround;
        float span = Mathf.DeltaAngle(fromAngle, toAngle);
        sweepOrigin = origin;
        sweepFrom = fromAngle;
        sweepSpan = span;

        float damage = Damage(s.swipeDamage);
        int count = Physics2D.OverlapCircle(origin, s.swipeReach, owner.FoeFilter, overlap);
        for (int i = 0; i < count; i++)
        {
            Collider2D c = overlap[i];
            if (c == null || c.transform.IsChildOf(owner.transform)) continue;

            DestructibleObject breakable = c.GetComponentInParent<DestructibleObject>();
            if (breakable != null)
            {
                breakable.HitByBlade(c, origin, s.swipeReach, sweepTest);
                continue;
            }

            FoeController foe = c.GetComponentInParent<FoeController>();
            if (foe != null)
            {
                if (foe.IsDead || swipeFoes.Contains(foe)) continue;
                Vector2 toFoe = foe.Center - origin;
                if (!InSweep(toFoe)) continue;

                swipeFoes.Add(foe);
                Vector2 push = toFoe.sqrMagnitude > 0.0001f ? toFoe.normalized : owner.FacingVector;
                owner.DamageFoe(foe, damage, push, s.swipeKnockback);
                continue;
            }

            CoopHealth other = c.GetComponentInParent<CoopHealth>();
            if (other != null && !swipePlayers.Contains(other))
            {
                Vector2 toPlayer = (Vector2)c.bounds.center - origin;
                if (!InSweep(toPlayer)) continue;

                swipePlayers.Add(other);
                Vector2 push = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : owner.FacingVector;
                owner.PvpTryHurt(other, damage, push, s.swipeKnockback);
            }
        }
    }

    private bool InSweep(Vector2 toTarget)
    {
        if (toTarget.sqrMagnitude < 0.0001f) return true;
        float angle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;
        return SwordController.AngleInSweep(angle, sweepFrom, sweepSpan, SwordController.AngleSlack);
    }

    // Lets a breakable ask "is this point under the swipe?".
    private bool PointInSweep(Vector2 point)
    {
        return InSweep(point - sweepOrigin);
    }

    // ------------------------------------------------------------ hold: grapple

    private void FireHook()
    {
        float t = Mathf.Clamp01((holdTime - s.tapMaxTime) / Mathf.Max(0.1f, s.chargeTime));
        hookRange = Mathf.Lerp(s.minRange, s.maxRange, t);
        hookDir = owner.FacingVector;
        hookDist = 0f;
        mode = Mode.HookOut;
    }

    private void UpdateHook(float dt)
    {
        Vector2 origin = owner.HandGround;
        float step = Mathf.Min(s.hookSpeed * dt, hookRange - hookDist);
        Vector2 from = origin + hookDir * hookDist;

        int count = Physics2D.CircleCast(from, 0.12f, hookDir, owner.FoeFilter, hits, step);

        int bestKind = 0;
        float bestDistance = float.MaxValue;
        int bestIndex = -1;
        FoeController bestFoe = null;
        Transform bestRoot = null;
        ToolInteractable bestMarker = null;

        for (int i = 0; i < count; i++)
        {
            Collider2D c = hits[i].collider;
            if (c == null || c.transform.IsChildOf(owner.transform)) continue;

            int kind;
            FoeController foe = c.GetComponentInParent<FoeController>();
            Transform root = null;
            ToolInteractable marker = null;

            if (foe != null)
            {
                if (!s.grabFoes || foe.IsDead) continue;
                kind = 1;
            }
            else if (c.GetComponentInParent<CoopHealth>() != null)
            {
                continue;
            }
            else
            {
                root = owner.FindInteractable(c, true, out marker);
                if (root != null) kind = 2;
                else if (owner.IsWall(c, true)) kind = 3;
                else continue;
            }

            if (hits[i].distance < bestDistance)
            {
                bestDistance = hits[i].distance;
                bestIndex = i;
                bestKind = kind;
                bestFoe = foe;
                bestRoot = root;
                bestMarker = marker;
            }
        }

        if (bestIndex < 0)
        {
            hookDist += step;
            if (hookDist >= hookRange - 0.001f) StartRetract();
            return;
        }

        hookDist += bestDistance;

        if (bestKind == 1)
        {
            // A foe: a little damage, and it is yanked toward the player.
            Vector2 toPlayer = origin - bestFoe.Center;
            Vector2 pull = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : -hookDir;
            owner.DamageFoe(bestFoe, Damage(s.hookDamage), pull, s.foePullDistance);
            StartRetract();
        }
        else if (bestKind == 2)
        {
            CarriedObject grabbed = new CarriedObject();
            if (grabbed.Begin(bestRoot, owner, bestMarker))
            {
                carried = grabbed;
                actionTimer = 0f;
                stuckTimer = 0f;
                mode = Mode.Reeling;
            }
            else
            {
                StartRetract();
            }
        }
        else
        {
            attachPoint = hits[bestIndex].point;
            actionTimer = 0f;
            stuckTimer = 0f;
            mode = Mode.Pulling;
            owner.LockMovement(this);   // the walking script is switched off while the arm pulls
        }
    }

    private void StartRetract()
    {
        mode = Mode.Retract;
        retractTimer = 0.12f;
    }

    // The player is pulled to the wall, stopping at anything solid in the way.
    private void PullStep(float dt)
    {
        Rigidbody2D rb = owner.Rb;
        Vector2 hand = owner.HandGround;
        Vector2 delta = attachPoint - hand;
        float dist = delta.magnitude;
        actionTimer += dt;

        if (dist <= s.stopDistance || actionTimer > s.maxPullTime)
        {
            EndPull();
            return;
        }

        Vector2 dir = delta / dist;
        float step = Mathf.Min(s.pullSpeed * dt, dist - s.stopDistance);
        float free = step;
        Vector2 pos = rb != null ? rb.position : (Vector2)owner.transform.position;
        if (rb != null) free = ToolPhysics.FreeDistance(rb, dir, step, owner.SolidFilter, castBuffer, null);

        if (free > 0.0005f)
        {
            Vector2 next = pos + dir * free;
            if (rb != null) rb.MovePosition(next);
            else owner.transform.position = new Vector3(next.x, next.y, owner.transform.position.z);
            stuckTimer = 0f;
        }
        else
        {
            stuckTimer += dt;
            if (stuckTimer > 0.08f) EndPull();
        }
    }

    private void EndPull()
    {
        EndPullMovement();
        Finish(s.cooldown);
    }

    private void EndPullMovement()
    {
        owner.UnlockMovement(this);
        ToolPhysics.SetVelocity(owner.Rb, Vector2.zero);
    }

    // The object is reeled in toward the player, stopping at walls.
    private void ReelStep(float dt)
    {
        if (carried == null || !carried.IsValid)
        {
            carried = null;
            Finish(s.cooldown);
            return;
        }

        Vector2 hand = owner.HandGround;
        Vector2 toObject = carried.Position - hand;
        float dist = toObject.magnitude;
        float stopAt = s.reelGap + carried.Radius + 0.35f;
        actionTimer += dt;

        if (dist <= stopAt + 0.05f || actionTimer > s.maxPullTime)
        {
            EndReel();
            return;
        }

        Vector2 target = hand + toObject.normalized * stopAt;
        float moved = carried.MoveToward(target, s.reelSpeed * dt);
        if (moved <= 0.0005f)
        {
            stuckTimer += dt;
            if (stuckTimer > 0.3f) EndReel();
        }
        else
        {
            stuckTimer = 0f;
        }
    }

    private void EndReel()
    {
        if (carried != null)
        {
            carried.End();
            carried = null;
        }
        Finish(s.cooldown);
    }

    // ------------------------------------------------------------ look

    private void EnsureVisuals()
    {
        if (visualRoot != null) return;

        visualRoot = new GameObject("GrappleArm");
        rope = ToolDraw.NewSquare("Rope", visualRoot.transform);
        head = ToolDraw.NewSquare("Hook", visualRoot.transform);
        arm = ToolDraw.NewSquare("Swipe", visualRoot.transform);
        preview = ToolDraw.NewSquare("Preview", visualRoot.transform);
    }

    private void HideAll()
    {
        if (rope != null) rope.enabled = false;
        if (head != null) head.enabled = false;
        if (arm != null) arm.enabled = false;
        if (preview != null) preview.enabled = false;
    }

    public override void LateTickAlways()
    {
        if (mode == Mode.Idle)
        {
            if (rope != null && (rope.enabled || arm.enabled || preview.enabled || head.enabled)) HideAll();
            return;
        }

        EnsureVisuals();
        Vector2 hand = owner.HandVisual;
        float z = owner.transform.position.z;
        int layer = owner.SortingLayerId;
        int order = owner.SortingOrder;

        rope.enabled = false;
        head.enabled = false;
        arm.enabled = false;
        preview.enabled = false;

        if (mode == Mode.Charging)
        {
            if (holdTime > s.tapMaxTime)
            {
                float t = Mathf.Clamp01((holdTime - s.tapMaxTime) / Mathf.Max(0.1f, s.chargeTime));
                float range = Mathf.Lerp(s.minRange, s.maxRange, t);
                Color faint = s.color;
                faint.a = 0.3f;
                ToolDraw.Bar(preview, hand, hand + owner.FacingVector * range, 0.05f, faint, z);
                ToolDraw.Sort(preview, layer, order - 1);
            }
            return;
        }

        if (mode == Mode.Swiping)
        {
            Vector2 tip = hand + new Vector2(Mathf.Cos(swipeCurrentAngle * Mathf.Deg2Rad), Mathf.Sin(swipeCurrentAngle * Mathf.Deg2Rad)) * s.swipeReach;
            ToolDraw.Bar(arm, hand, tip, 0.2f, s.swipeColor, z);
            ToolDraw.Sort(arm, layer, order + (Mathf.Approximately(swipeFaceAngle, 90f) ? -1 : 1));
            return;
        }

        // Hook, retract, pull, reel: the rope runs from the hand to the hook.
        Vector2 headPosition;
        if (mode == Mode.Pulling) headPosition = attachPoint;
        else if (mode == Mode.Reeling && carried != null && carried.IsValid) headPosition = carried.Position;
        else headPosition = hand + hookDir * hookDist;

        ToolDraw.Bar(rope, hand, headPosition, 0.08f, s.ropeColor, z);
        ToolDraw.Sort(rope, layer, order + 2);

        head.enabled = true;
        head.color = s.color;
        head.transform.position = new Vector3(headPosition.x, headPosition.y, z);
        head.transform.rotation = Quaternion.Euler(0f, 0f, 45f);
        head.transform.localScale = new Vector3(0.24f, 0.24f, 1f);
        ToolDraw.Sort(head, layer, order + 3);
    }
}
