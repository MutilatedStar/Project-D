using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// BUBBLE SHIELD (built by SwordController; nothing to add by hand).
///
///   Tap the attack button: a bubble pops up around the player for a few seconds.
///     - It STOPS trap arrows and lasers (see below for how it finds them).
///     - While it is up, and for a short while after it pops, the player takes less damage from EVERYTHING, tick damage included.
///       That is a timed buff, like the Magic Boots speed buff: it keeps running if the player switches to another tool.
///   Then it needs its Cooldown.
///
/// Arrows and lasers: add a ShieldBlockable to the arrow or laser for a sure result. Without it the shield looks for things on the Projectile /
/// Laser Layers, or whose NAME contains one of the words in the lists, and only touches moving projectiles and trigger zones (it never
/// destroys or walks the player through a static wall, turret or trap). A laser that hurts with a raycast should ask SegmentBlocked below.
/// For the damage reduction to be exact, add the one line described at the top of PlayerBuffs.cs to CoopHealth.
/// </summary>
public class BubbleShieldTool : PlayerTool
{
    [System.Serializable]
    public class Settings : ToolSettings
    {
        public Settings()
        {
            toolName = "Bubble Shield";
            shortLabel = "BUBL";
            color = new Color(0.4f, 0.8f, 1f);
        }

        [Header("Bubble")]
        [Tooltip("Seconds before the shield can be used again, counted from the moment it pops up.")]
        [Min(0f)] public float cooldown = 9f;
        [Tooltip("Seconds the bubble stays up.")]
        [Min(0.5f)] public float bubbleSeconds = 4f;
        [Tooltip("Size of the bubble, in Unity units (radius).")]
        [Min(0.3f)] public float radius = 1.1f;

        [Header("Damage reduction (the timed buff)")]
        [Tooltip("The share of normal damage the player takes while the buff runs. 0.5 = half damage, 0.25 = a quarter.")]
        [Range(0f, 1f)] public float damageTakenMultiplier = 0.5f;
        [Tooltip("The buff keeps running this many seconds after the bubble pops.")]
        [Min(0f)] public float lingerSeconds = 2f;

        [Header("What the bubble stops")]
        public bool blockProjectiles = true;
        public bool blockLasers = true;
        [Tooltip("Colliders on these layers count as trap arrows.")]
        public LayerMask projectileLayers = 0;
        [Tooltip("A collider whose name contains one of these words counts as a trap arrow. Not case sensitive.")]
        public string[] projectileNames = { "arrow", "bolt", "projectile" };
        [Tooltip("Colliders on these layers count as lasers.")]
        public LayerMask laserLayers = 0;
        [Tooltip("A collider whose name contains one of these words counts as a laser. Not case sensitive.")]
        public string[] laserNames = { "laser", "beam" };
        [Tooltip("ON: a moving arrow that touches the bubble is destroyed. OFF: it is only made harmless (collisions ignored).")]
        public bool popMovingProjectiles = true;
    }

    // ------------------------------------------------------------ for trap scripts

    /// <summary>Every bubble that is up right now (one per player at most).</summary>
    public static readonly List<BubbleShieldTool> Active = new List<BubbleShieldTool>();

    /// <summary>The middle of the bubble, in the world.</summary>
    public Vector2 Center { get { return owner.HandGround; } }
    public float BubbleRadius { get { return s.radius; } }

    /// <summary>True if this point is inside any bubble that is up. For a trap that damages with its own overlap check.</summary>
    public static bool IsShielded(Vector2 point)
    {
        for (int i = 0; i < Active.Count; i++)
        {
            BubbleShieldTool shield = Active[i];
            if (shield == null) continue;
            if ((point - shield.Center).sqrMagnitude <= shield.s.radius * shield.s.radius) return true;
        }
        return false;
    }

    /// <summary>
    /// For a laser that damages with a raycast: does the line from "from" to "to" run into a bubble? If so stopPoint is where it first
    /// touches the bubble, and the laser should end there instead of hurting the player.
    /// </summary>
    public static bool SegmentBlocked(Vector2 from, Vector2 to, out Vector2 stopPoint)
    {
        stopPoint = to;
        float bestT = float.MaxValue;

        Vector2 d = to - from;
        float a = Vector2.Dot(d, d);
        if (a < 0.000001f) return false;

        for (int i = 0; i < Active.Count; i++)
        {
            BubbleShieldTool shield = Active[i];
            if (shield == null) continue;

            Vector2 f = from - shield.Center;
            float r = shield.s.radius;
            float c = Vector2.Dot(f, f) - r * r;
            if (c <= 0f)
            {
                bestT = 0f;   // the line starts inside the bubble
                break;
            }

            float b = 2f * Vector2.Dot(f, d);
            float disc = b * b - 4f * a * c;
            if (disc < 0f) continue;

            float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
            if (t >= 0f && t <= 1f && t < bestT) bestT = t;
        }

        if (bestT == float.MaxValue) return false;
        stopPoint = from + d * bestT;
        return true;
    }

    // ------------------------------------------------------------ the tool

    private readonly Settings s;

    private bool up;
    private float bubbleTimer;
    private float bubbleTotal;
    private float lingerTimer;
    private float cooldownLeft;
    private float cooldownTotal;

    private ContactFilter2D allFilter;
    private readonly Collider2D[] overlap = new Collider2D[40];
    private readonly List<Collider2D> ignored = new List<Collider2D>();

    private SpriteRenderer bubble;
    private static Sprite ringSprite;

    public BubbleShieldTool(SwordController owner, Settings settings) : base(owner, settings)
    {
        s = settings;
        allFilter = new ContactFilter2D();
        allFilter.NoFilter();
        allFilter.useTriggers = true;
    }

    public override ToolKind Kind { get { return ToolKind.BubbleShield; } }

    public override float CooldownFraction
    {
        get { return cooldownTotal > 0f ? Mathf.Clamp01(cooldownLeft / cooldownTotal) : 0f; }
    }

    public override float BuffFraction
    {
        get
        {
            if (up) return bubbleTotal > 0f ? Mathf.Clamp01(bubbleTimer / bubbleTotal) : 0f;
            if (lingerTimer > 0f && s.lingerSeconds > 0f) return Mathf.Clamp01(lingerTimer / s.lingerSeconds);
            return 0f;
        }
    }

    public override string BuffLabel { get { return "SHLD"; } }

    // ------------------------------------------------------------ every frame

    public override void TickAlways(float dt)
    {
        if (cooldownLeft > 0f) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);

        if (up)
        {
            bubbleTimer -= dt;
            if (bubbleTimer <= 0f) Pop();
        }
        else if (lingerTimer > 0f)
        {
            lingerTimer -= dt;
            if (lingerTimer <= 0f) PlayerBuffs.Clear(owner.PlayerNo, this);
        }
    }

    public override void TickEquipped(float dt)
    {
        if (owner.AttackPressedNow && !up && cooldownLeft <= 0f) Raise();
    }

    public override void FixedTickAlways(float dt)
    {
        if (up) BlockStep();
    }

    public override void Cancel()
    {
        if (!up && lingerTimer <= 0f) return;

        if (up) Pop();
        lingerTimer = 0f;
        PlayerBuffs.Clear(owner.PlayerNo, this);
    }

    public override void DestroyVisuals()
    {
        Cancel();
        if (bubble != null) Object.Destroy(bubble.gameObject);
    }

    private void Raise()
    {
        up = true;
        bubbleTotal = s.bubbleSeconds;
        bubbleTimer = bubbleTotal;
        lingerTimer = 0f;
        cooldownTotal = Cooldown(s.cooldown);
        cooldownLeft = cooldownTotal;

        PlayerBuffs.Set(owner.PlayerNo, this, s.damageTakenMultiplier);
        if (!Active.Contains(this)) Active.Add(this);
    }

    private void Pop()
    {
        up = false;
        Active.Remove(this);
        RestoreIgnored();

        if (s.lingerSeconds > 0f) lingerTimer = s.lingerSeconds;
        else PlayerBuffs.Clear(owner.PlayerNo, this);

        if (bubble != null) bubble.enabled = false;
    }

    // ------------------------------------------------------------ stopping arrows and lasers

    private void BlockStep()
    {
        int count = Physics2D.OverlapCircle(Center, s.radius, allFilter, overlap);
        for (int i = 0; i < count; i++)
        {
            Collider2D c = overlap[i];
            if (c == null || c.transform.IsChildOf(owner.transform)) continue;

            ShieldBlockable mark = c.GetComponentInParent<ShieldBlockable>();
            Rigidbody2D body = c.attachedRigidbody;
            bool moving = body != null && body.bodyType != RigidbodyType2D.Static;

            if (mark != null)
            {
                if (mark.kind == ShieldBlockable.Kind.Projectile)
                {
                    if (s.blockProjectiles) BlockProjectile(c, mark, true);
                }
                else if (s.blockLasers)
                {
                    IgnoreCollider(c);
                }
                continue;
            }

            // No marker: guess by layer / name, but only touch things that really move or are trigger zones.
            if (!moving && !c.isTrigger) continue;

            if (s.blockProjectiles && Matches(c, s.projectileLayers, s.projectileNames))
            {
                BlockProjectile(c, null, moving);
            }
            else if (s.blockLasers && Matches(c, s.laserLayers, s.laserNames))
            {
                IgnoreCollider(c);
            }
        }
    }

    private void BlockProjectile(Collider2D c, ShieldBlockable mark, bool canDestroy)
    {
        GameObject target = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject : (mark != null ? mark.gameObject : c.gameObject);
        target.SendMessage("OnShieldBlocked", SendMessageOptions.DontRequireReceiver);

        bool destroy = canDestroy && (mark != null ? mark.destroyOnBlock : s.popMovingProjectiles);
        if (destroy)
        {
            SpawnPop(c.bounds.center);
            Object.Destroy(target);
        }
        else
        {
            IgnoreCollider(c);
        }
    }

    // The collider can no longer touch the player (so it can't trigger damage) until the bubble pops.
    private void IgnoreCollider(Collider2D c)
    {
        if (ignored.Contains(c)) return;

        Collider2D[] mine = owner.PlayerColliders;
        for (int i = 0; i < mine.Length; i++)
        {
            if (mine[i] != null) Physics2D.IgnoreCollision(mine[i], c, true);
        }
        ignored.Add(c);
    }

    private void RestoreIgnored()
    {
        Collider2D[] mine = owner.PlayerColliders;
        for (int k = 0; k < ignored.Count; k++)
        {
            Collider2D c = ignored[k];
            if (c == null) continue;
            for (int i = 0; i < mine.Length; i++)
            {
                if (mine[i] != null) Physics2D.IgnoreCollision(mine[i], c, false);
            }
        }
        ignored.Clear();
    }

    private static bool Matches(Collider2D c, LayerMask layers, string[] words)
    {
        if ((layers.value & (1 << c.gameObject.layer)) != 0) return true;
        if (words == null) return false;

        string own = c.gameObject.name;
        string body = c.attachedRigidbody != null ? c.attachedRigidbody.gameObject.name : own;
        for (int i = 0; i < words.Length; i++)
        {
            if (string.IsNullOrEmpty(words[i])) continue;
            if (own.IndexOf(words[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (body.IndexOf(words[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // A little flash where an arrow was stopped.
    private void SpawnPop(Vector2 position)
    {
        GameObject go = new GameObject("ShieldPop");
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = SwordController.WhiteSquare;
        Color c = s.color;
        c.a = 0.85f;
        sr.color = c;
        sr.sortingLayerID = owner.SortingLayerId;
        sr.sortingOrder = owner.SortingOrder + 3;
        go.transform.position = new Vector3(position.x, position.y, owner.transform.position.z);
        go.transform.rotation = Quaternion.Euler(0f, 0f, 45f);
        go.transform.localScale = new Vector3(0.3f, 0.3f, 1f);
        Object.Destroy(go, 0.12f);
    }

    // ------------------------------------------------------------ look

    public override void LateTickAlways()
    {
        if (!up)
        {
            if (bubble != null && bubble.enabled) bubble.enabled = false;
            return;
        }

        if (bubble == null)
        {
            bubble = ToolDraw.NewSquare("ShieldBubble", owner.transform);
            bubble.sprite = RingSprite();
        }

        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 6f);
        float alpha = 0.55f + 0.25f * pulse;
        if (bubbleTimer < 1f) alpha *= 0.5f + 0.5f * Mathf.Sign(Mathf.Sin(Time.time * 24f));   // blinks when it is about to pop

        Color c = s.color;
        c.a = alpha;
        bubble.color = c;
        bubble.enabled = true;
        ToolDraw.Sort(bubble, owner.SortingLayerId, owner.SortingOrder + 2);

        Vector2 center = Center;
        float size = s.radius * 2f * (1f + 0.02f * pulse);
        bubble.transform.position = new Vector3(center.x, center.y, owner.transform.position.z);
        bubble.transform.localScale = new Vector3(size, size, 1f);
    }

    // A soft disc with a bright rim, 1 unit wide, made once.
    private static Sprite RingSprite()
    {
        if (ringSprite == null)
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            Vector2 middle = new Vector2(size * 0.5f, size * 0.5f);
            float radius = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), middle) / radius;
                    float a = 0f;
                    if (d <= 1f)
                    {
                        float rim = Mathf.Clamp01((d - 0.78f) / 0.22f);
                        a = 0.12f + 0.78f * rim * rim;
                        if (d > 0.96f) a *= Mathf.Clamp01((1f - d) / 0.04f);
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            ringSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }
        return ringSprite;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        Active.Clear();
        ringSprite = null;
    }
}
