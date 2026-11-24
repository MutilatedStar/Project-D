using System.Collections.Generic;
using UnityEngine;

/// <summary>Which gadget a slot holds. Sword = the Frost Sword (the one sword that stays).</summary>
public enum ToolKind { Sword, ScannerLens, MagicBoots, PulseBeam, GrappleArm, BubbleShield }

/// <summary>The numbers every tool has in common. Each tool's own Settings class builds on this one.</summary>
[System.Serializable]
public class ToolSettings
{
    [Header("Identity")]
    [Tooltip("Must be unique. Pickups, unlocks and the shop find a tool by this name.")]
    public string toolName = "Tool";
    [Tooltip("ON: every player owns this tool from the start of a run. OFF: it stays locked until a pickup / the shop / Debug Unlock All gives it.")]
    public bool unlockedAtStart = true;
    [Tooltip("Optional picture for the inventory slot. Without one the slot shows the short label on a coloured square.")]
    public Sprite icon;
    [Tooltip("Colour of the slot and of the tool's effects.")]
    public Color color = Color.white;
    [Tooltip("Up to 4 letters, shown on the slot while there is no icon.")]
    public string shortLabel = "TOOL";
}

/// <summary>
/// One gadget on the player. SwordController builds the tools itself and calls them, so there is nothing to add by hand.
/// A tool only reads the Attack button (tap / hold) and, for the Pulse Beam, the previous / next buttons.
/// </summary>
public abstract class PlayerTool
{
    protected readonly SwordController owner;
    protected readonly ToolSettings baseSettings;

    protected PlayerTool(SwordController owner, ToolSettings settings)
    {
        this.owner = owner;
        baseSettings = settings;
    }

    public abstract ToolKind Kind { get; }

    public string Name { get { return baseSettings.toolName; } }
    public string ShortLabel { get { return baseSettings.shortLabel; } }
    public Color TintColor { get { return baseSettings.color; } }

    // ---- for the inventory bar -------------------------------------------------------------------------

    /// <summary>1 = just used, 0 = ready. Drawn as a dark cover that drains away on the slot.</summary>
    public virtual float CooldownFraction { get { return 0f; } }
    /// <summary>0 to 1 while the player is charging something (hold time). Drawn as a bar under the slot.</summary>
    public virtual float ChargeFraction { get { return 0f; } }
    /// <summary>Charges left / charges in total (Magic Boots dashes). 0 = this tool has no charges.</summary>
    public virtual int PipCount { get { return 0; } }
    public virtual int PipMax { get { return 0; } }
    /// <summary>1 to 0 while a timed buff from this tool runs (speed buff, shield). 0 = none. Drawn as a timer square above the bar.</summary>
    public virtual float BuffFraction { get { return 0f; } }
    public virtual string BuffLabel { get { return ""; } }
    public virtual Color BuffColor { get { return TintColor; } }

    // ---- for the controller ----------------------------------------------------------------------------

    /// <summary>True while the player must not switch to another tool (a dash, a held beam, a frozen scan...).</summary>
    public virtual bool IsBusy { get { return false; } }
    /// <summary>True while the previous / next buttons mean something else for this tool (moving a held object).</summary>
    public virtual bool WantsSwitchKeys { get { return false; } }

    public virtual void OnEquipped() { }
    public virtual void OnUnequipped() { }

    /// <summary>Every frame the game is running, equipped or not: cooldowns, buff timers.</summary>
    public virtual void TickAlways(float dt) { }
    /// <summary>Every frame while this tool is in the player's hand: reads the buttons.</summary>
    public virtual void TickEquipped(float dt) { }
    /// <summary>Every physics step the game is running, equipped or not: dashes, pulls, carrying.</summary>
    public virtual void FixedTickAlways(float dt) { }
    /// <summary>Every frame, even while paused (after the camera has moved): visuals.</summary>
    public virtual void LateTickAlways() { }
    /// <summary>Stops everything right now (player died, disabled, new run). Must be safe to call every frame.</summary>
    public virtual void Cancel() { }
    /// <summary>The player is being destroyed: throw away anything this tool made.</summary>
    public virtual void DestroyVisuals() { }

    protected float Cooldown(float seconds) { return seconds * owner.CooldownFactor(baseSettings.toolName); }
    protected float Damage(float baseDamage) { return baseDamage * owner.DamageFactor(baseSettings.toolName); }
}

// =====================================================================================================================
// Small helpers shared by the tools
// =====================================================================================================================

/// <summary>Rigidbody2D helpers that compile on every Unity version.</summary>
internal static class ToolPhysics
{
    public const float Skin = 0.02f;

    public static Vector2 GetVelocity(Rigidbody2D rb)
    {
#if UNITY_6000_0_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    public static void SetVelocity(Rigidbody2D rb, Vector2 velocity)
    {
        if (rb == null) return;
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = velocity;
#else
        rb.velocity = velocity;
#endif
    }

    /// <summary>How far this body can move along dir (up to distance) before it touches something solid. Colliders for which ignore returns true don't count.</summary>
    public static float FreeDistance(Rigidbody2D rb, Vector2 dir, float distance, ContactFilter2D filter, RaycastHit2D[] buffer, System.Func<Collider2D, bool> ignore)
    {
        int count = rb.Cast(dir, filter, buffer, distance + Skin);
        float best = distance;
        for (int i = 0; i < count; i++)
        {
            Collider2D other = buffer[i].collider;
            if (other == null) continue;
            if (ignore != null && ignore(other)) continue;
            best = Mathf.Min(best, Mathf.Max(0f, buffer[i].distance - Skin));
        }
        return best;
    }
}

/// <summary>Draws the simple bars (beams, hooks, swipes) out of stretched white squares, like the old lightning bolts.</summary>
internal static class ToolDraw
{
    public static SpriteRenderer NewSquare(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        if (parent != null) go.transform.SetParent(parent, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = SwordController.WhiteSquare;
        sr.enabled = false;
        return sr;
    }

    /// <summary>Stretches the square into a bar from one point to another.</summary>
    public static void Bar(SpriteRenderer sr, Vector2 from, Vector2 to, float thickness, Color color, float z)
    {
        Vector2 d = to - from;
        float length = d.magnitude;
        if (length < 0.001f)
        {
            sr.enabled = false;
            return;
        }

        sr.enabled = true;
        sr.color = color;
        sr.transform.position = new Vector3((from.x + to.x) * 0.5f, (from.y + to.y) * 0.5f, z);
        sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        sr.transform.localScale = new Vector3(length, thickness, 1f);
    }

    public static void Sort(SpriteRenderer sr, int layerId, int order)
    {
        sr.sortingLayerID = layerId;
        sr.sortingOrder = order;
    }
}

/// <summary>
/// One object a tool is moving around (a crate the Pulse Beam holds, a block the Grapple Arm reels in). While it is carried
/// it can't push the player, and it stops at walls instead of passing through them.
/// </summary>
internal class CarriedObject
{
    public Transform Root { get; private set; }
    public bool IsValid { get { return Root != null; } }
    /// <summary>Roughly half the object's size, so a tool can stop it a little way from the player.</summary>
    public float Radius { get; private set; }

    private SwordController owner;
    private Rigidbody2D rb;
    private RigidbodyType2D oldType;
    private Collider2D[] cols;
    private ToolInteractable marker;
    private readonly RaycastHit2D[] hits = new RaycastHit2D[16];

    public Vector2 Position
    {
        get { return rb != null ? rb.position : (Vector2)Root.position; }
    }

    public bool Begin(Transform root, SwordController by, ToolInteractable tagged)
    {
        if (root == null || by == null) return false;

        Root = root;
        owner = by;
        marker = tagged;

        rb = root.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            oldType = rb.bodyType;
            rb.bodyType = RigidbodyType2D.Kinematic;
            ToolPhysics.SetVelocity(rb, Vector2.zero);
            rb.angularVelocity = 0f;
        }

        cols = root.GetComponentsInChildren<Collider2D>();
        Bounds b = new Bounds(root.position, Vector3.zero);
        bool any = false;
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] == null || cols[i].isTrigger) continue;
            if (!any) { b = cols[i].bounds; any = true; }
            else b.Encapsulate(cols[i].bounds);
        }
        Radius = any ? Mathf.Max(b.extents.x, b.extents.y) : 0.4f;

        SetIgnore(true);
        if (marker != null) marker.NotifyGrabbed(by);
        return true;
    }

    /// <summary>Moves the object up to maxStep toward target, stopping at anything solid. Returns how far it really moved.</summary>
    public float MoveToward(Vector2 target, float maxStep)
    {
        if (Root == null) return 0f;

        Vector2 from = Position;
        Vector2 delta = target - from;
        float dist = delta.magnitude;
        if (dist < 0.001f) return 0f;

        Vector2 dir = delta / dist;
        float step = Mathf.Min(dist, maxStep);
        float free = FreeDistance(dir, step);
        if (free <= 0.0005f) return 0f;

        Vector2 to = from + dir * free;
        if (rb != null) rb.MovePosition(to);
        else Root.position = new Vector3(to.x, to.y, Root.position.z);
        return free;
    }

    public void End()
    {
        if (Root != null)
        {
            if (rb != null)
            {
                rb.bodyType = oldType;
                ToolPhysics.SetVelocity(rb, Vector2.zero);
            }
            SetIgnore(false);
        }

        if (marker != null) marker.NotifyReleased(owner);
        Root = null;
        rb = null;
        cols = null;
        marker = null;
    }

    private float FreeDistance(Vector2 dir, float step)
    {
        float best = step;
        for (int i = 0; i < cols.Length; i++)
        {
            Collider2D c = cols[i];
            if (c == null || c.isTrigger || !c.enabled) continue;

            int count = c.Cast(dir, owner.SolidFilter, hits, step + ToolPhysics.Skin);
            for (int h = 0; h < count; h++)
            {
                Collider2D other = hits[h].collider;
                if (other == null) continue;
                if (other.transform.IsChildOf(Root) || other.transform.IsChildOf(owner.transform)) continue;
                best = Mathf.Min(best, Mathf.Max(0f, hits[h].distance - ToolPhysics.Skin));
            }
        }
        return best;
    }

    // While carried the object and the player don't collide, so a crate held right in front of the player can't shove them.
    private void SetIgnore(bool ignore)
    {
        Collider2D[] mine = owner.PlayerColliders;
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] == null) continue;
            for (int p = 0; p < mine.Length; p++)
            {
                if (mine[p] != null) Physics2D.IgnoreCollision(cols[i], mine[p], ignore);
            }
        }
    }
}

/// <summary>
/// Finds the character's movement-speed number so a tool can speed the player up for a moment (the Magic Boots buff, the climb slow-down).
/// It looks for a public float called speed / moveSpeed / walkSpeed on TopDownCharacterController. Several buffs multiply together, and
/// the change is made as a ratio, so anything else that also changes the speed is not overwritten.
/// </summary>
internal class MoveSpeedAccess
{
    private static readonly string[] Names = { "speed", "moveSpeed", "walkSpeed", "movementSpeed", "Speed", "MoveSpeed" };

    private readonly Component mover;
    private System.Reflection.FieldInfo field;
    private System.Reflection.PropertyInfo property;
    private bool searched;
    private bool warned;
    private float applied = 1f;

    public MoveSpeedAccess(Component mover) { this.mover = mover; }

    /// <summary>Makes the speed this many times the normal one (1 = normal).</summary>
    public void SetMultiplier(float target)
    {
        if (mover == null) return;
        if (!Resolve()) return;
        if (Mathf.Approximately(target, applied)) return;

        float current = Get();
        float ratio = target / Mathf.Max(0.0001f, applied);
        Set(current * ratio);
        applied = target;
    }

    private bool Resolve()
    {
        if (searched) return field != null || property != null;
        searched = true;

        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        System.Type type = mover.GetType();
        for (int i = 0; i < Names.Length; i++)
        {
            System.Reflection.FieldInfo f = type.GetField(Names[i], flags);
            if (f != null && f.FieldType == typeof(float)) { field = f; return true; }

            System.Reflection.PropertyInfo p = type.GetProperty(Names[i], flags);
            if (p != null && p.PropertyType == typeof(float) && p.CanRead && p.CanWrite) { property = p; return true; }
        }

        if (!warned)
        {
            warned = true;
            Debug.LogWarning("Tools: could not find a speed number (speed / moveSpeed / walkSpeed) on " + type.Name +
                             ", so speed buffs do nothing. Tell Claude what the variable is called.");
        }
        return false;
    }

    private float Get()
    {
        return field != null ? (float)field.GetValue(mover) : (float)property.GetValue(mover, null);
    }

    private void Set(float value)
    {
        if (field != null) field.SetValue(mover, value);
        else property.SetValue(mover, value, null);
    }
}

/// <summary>
/// Freezes the whole stage for the Scanner Lens: foes, traps, cooldowns, everything except the pause screen.
/// It turns on the game's own "frozen by hand" switch (FoeController.Frozen) so every script that already stops for the notebook stops,
/// and also sets Time.timeScale to 0, so even things that don't check the switch (physics, animations, particles) stop.
/// </summary>
public static class GameFreeze
{
    private static System.Reflection.PropertyInfo frozenProperty;
    private static System.Reflection.FieldInfo frozenField;
    private static bool searched;
    private static bool flagWarned;

    private static int holders;
    private static bool usedTimeScale;
    private static float savedTimeScale = 1f;

    public static bool IsFrozen { get { return holders > 0; } }

    public static void Begin(bool alsoStopTimeScale)
    {
        holders++;
        if (holders > 1) return;

        bool flagWorked = SetFrozenFlag(true);
        if (alsoStopTimeScale || !flagWorked)
        {
            savedTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            usedTimeScale = true;
        }
    }

    public static void End()
    {
        if (holders <= 0) return;
        holders--;
        if (holders > 0) return;

        SetFrozenFlag(false);
        if (usedTimeScale)
        {
            usedTimeScale = false;
            Time.timeScale = savedTimeScale > 0f ? savedTimeScale : 1f;
        }
    }

    /// <summary>Call every frame while frozen: if something else put the time scale back, stop time again.</summary>
    public static void Reassert()
    {
        if (holders > 0 && usedTimeScale && Time.timeScale != 0f) Time.timeScale = 0f;
    }

    // FoeController.Frozen is looked up by name, so this file compiles whatever it looks like. If it is not found,
    // only the time scale is used.
    private static bool SetFrozenFlag(bool value)
    {
        if (!searched)
        {
            searched = true;
            System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            frozenProperty = typeof(FoeController).GetProperty("Frozen", flags);
            if (frozenProperty != null && frozenProperty.GetSetMethod(true) == null) frozenProperty = null;
            if (frozenProperty == null) frozenField = typeof(FoeController).GetField("Frozen", flags);
        }

        if (frozenProperty != null) { frozenProperty.SetValue(null, value, null); return true; }
        if (frozenField != null) { frozenField.SetValue(null, value); return true; }

        if (!flagWarned)
        {
            flagWarned = true;
            Debug.LogWarning("GameFreeze: FoeController.Frozen was not found, so the Scanner Lens freezes the stage with Time.timeScale only.");
        }
        return false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        holders = 0;
        usedTimeScale = false;
        searched = false;
        flagWarned = false;
        frozenProperty = null;
        frozenField = null;
    }
}
