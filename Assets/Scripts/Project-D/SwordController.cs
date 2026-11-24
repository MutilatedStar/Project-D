using System.Collections.Generic;
using UnityEngine;
using Ilumisoft.HealthSystem;
using Cainos.PixelArtTopDown_Basic;

// NOTE: no "using System;" on purpose. It makes "Random" ambiguous with UnityEngine.Random (same reason as FoeController).

/// <summary>
/// Put this on the PLAYER (the same object as TopDownCharacterController; the PlayerInputHub in the scene supplies the input).
///
/// THE LOADOUT: every player has the FROST SWORD and five tools. Slot 1 is the sword, then, in order:
///   2 Scanner Lens   3 Magic Boots   4 Pulse Beam   5 Grapple Arm   6 Bubble Shield
/// The tools are built by this script (ScannerLensTool.cs, MagicBootsTool.cs, PulseBeamTool.cs, GrappleArmTool.cs, BubbleShieldTool.cs),
/// each with its own settings block in the Inspector, so there is nothing to add by hand. All of them use the Attack button (tap / hold);
/// the Pulse Beam also uses previous / next weapon while it holds an object. Their explanations are at the top of each file.
///
/// The old class name stays (SwordController) so PlayerEffects, the shop, GameReset and the HUD keep working. Slots are still found by
/// index and by name exactly like the swords were, so "unlock a sword" now means "unlock a slot".
///
/// TWO PLAYERS: every player has their own SwordController, their own unlocks, upgrades and timed power-ups. It reads its buttons from this
/// character's player slot in the PlayerInputHub (defaults: attack F / Right Ctrl, previous / next Q, R / Comma, Period).
/// SHARED WEAPONS (CoopRules.SharedWeapons, OFF by default): ON = a slot unlocked or upgraded by one player is unlocked for everybody.
///
/// THE FROST SWORD:
///   Attack (tap)       swing the sword the way the player is facing. The blade always chops DOWNWARD on screen.
///   Attack (keep held) the swing happens straight away, then a charge starts. When the player GLOWS the spin is ready:
///                      let go for a full 360-degree spin that hits every foe around you.
///   It knocks foes back, slows them, and has a chance to freeze them solid (they turn blue and can't move or attack). The blade throws
///   ice sparks (SwordElementFx.cs).
///
/// Notebook open (or FoeController.Frozen) = nothing works, same as the foes. The one exception is the Scanner Lens, which freezes the
/// stage itself and keeps running while it does.
///
/// Collecting: a slot with "Unlocked At Start" OFF can't be equipped (previous / next and the number keys skip it) until UnlockSword,
/// UnlockAllSwords or UnlockByName is called (a pickup, the shop...). Tick Debug Unlock All to own every slot.
///
/// Upgrades: SwordShop.cs (per slot name) and PlayerUpgrades.cs (the player) hand their bonuses in through SetUpgradeBonus / SetPlayerBonus.
/// The Frost Sword uses all of a SwordBonus. The tools use Damage Percent (damage) and Attack Speed Percent (shorter cooldowns).
///
/// PVP (PvpMode.Enabled; with it off none of this ever happens): the sword, the Magic Boots dash, the Pulse Beam and the Grapple swipe also
/// hurt the OTHER player, scaled by PvpMode.DamageScale, through CoopHealth.ApplyDamage so Invincible, Grace and a jump still protect.
///
/// Jumping: TopDownJump.Height is the one shared number. Every frame this script lifts ONLY the blade (and its sparks) by that much.
/// </summary>
[DefaultExecutionOrder(100)]   // after TopDownJump's own updates, so the blade is lifted by this frame's jump height
[DisallowMultipleComponent]
public class SwordController : MonoBehaviour
{
    // ------------------------------------------------------------ the sword's numbers

    /// <summary>All the numbers for the sword (and the small stand-in record every tool slot hands to HUDs). One foldout in the Inspector.</summary>
    [System.Serializable]
    public class SwordStats
    {
        [Tooltip("Must be unique. The shop and the unlock list find a slot by this name.")]
        public string swordName = "Sword";
        [Tooltip("ON: the player owns this from the start of a run. OFF: it stays locked until it is unlocked (a pickup, the shop, or the Debug Unlock All box).")]
        public bool unlockedAtStart = true;

        [Header("Damage")]
        [Tooltip("Damage per hit, taken off the foe's Health.")]
        [Min(0f)] public float damage = 15f;
        [Tooltip("Chance (0 to 1) that a hit is a critical hit. 0 = never. A crit makes the foe flash white and plays the crit sound.")]
        [Range(0f, 1f)] public float critChance = 0f;
        [Tooltip("A critical hit deals damage x this.")]
        [Min(1f)] public float critMultiplier = 2f;
        [Tooltip("Most foes ONE normal swing can hit with the blade. 0 = no limit. (The spin always hits everything around you.)")]
        [Min(0)] public int maxTargets = 2;
        [Tooltip("Fraction of the damage a hit deals that heals the player. 0 = off, 0.05 = 5%. The shop can raise it.")]
        [Range(0f, 1f)] public float lifeSteal = 0f;

        [Header("Swing")]
        [Tooltip("Seconds the swing takes from raised to lowered. Smaller = faster.")]
        [Min(0.05f)] public float swingTime = 0.25f;
        [Tooltip("Seconds you must wait after a swing finishes before the next one.")]
        [Min(0f)] public float cooldown = 0.15f;
        [Tooltip("How far the sword reaches, in Unity units. Ignored while a Sprite is assigned and Fit Hitbox To Sprite is ON: then the sprite's size decides.")]
        [Min(0.1f)] public float reach = 1.2f;
        [Tooltip("Width of the sweep in degrees. Wider hits more foes around you.")]
        [Range(30f, 180f)] public float arc = 110f;

        [Header("Knockback")]
        [Tooltip("How far a hit pushes a foe back, in Unity units.")]
        [Min(0f)] public float knockback = 0.5f;

        [Header("Spin attack (hold the attack key)")]
        [Tooltip("Seconds the attack key must be held before the spin is ready (the player glows).")]
        [Min(0.1f)] public float spinChargeTime = 1.5f;
        [Tooltip("Seconds the full 360 degree spin takes.")]
        [Min(0.1f)] public float spinTime = 0.5f;
        [Tooltip("Spin damage = damage x this.")]
        [Min(0f)] public float spinDamageMultiplier = 1.5f;
        [Tooltip("Colour of the glow around the player while the spin charges.")]
        public Color glowColor = new Color(1f, 0.95f, 0.6f);

        // The lightning chain and the fire trail were scrapped with the other swords. The numbers stay (hidden, all "off") so other
        // scripts that read them, like SwordElementFx, still compile.
        [HideInInspector] public int chainTargets = 0;
        [HideInInspector] public float chainRange = 3f;
        [HideInInspector] public float chainFalloff = 0.7f;
        [HideInInspector] public Color boltColor = new Color(1f, 1f, 0.5f);
        [HideInInspector] public float chainDamageMultiplier = 1f;
        [HideInInspector] public float shockSwingFactor = 0.6f;
        [HideInInspector] public float shockTime = 3f;
        [HideInInspector] public float trailDuration = 0f;
        [HideInInspector] public float trailLength = 2f;
        [HideInInspector] public float trailWidth = 0.6f;
        [HideInInspector] public float trailDamage = 3f;
        [HideInInspector] public float trailTickTime = 0.5f;

        [Header("Ice")]
        [Tooltip("Foes hit move at this fraction of their speed. 1 = off. 0.5 = half speed.")]
        [Range(0.1f, 1f)] public float slowFactor = 1f;
        [Tooltip("Seconds the slow lasts (a new hit refreshes it).")]
        [Min(0f)] public float slowTime = 3f;
        [Tooltip("Chance (0 to 1) that a hit freezes the foe solid. 0 = off.")]
        [Range(0f, 1f)] public float freezeChance = 0f;
        [Tooltip("Seconds a frozen foe stays frozen.")]
        [Min(0f)] public float freezeTime = 2.5f;

        [Header("Look")]
        [Tooltip("Optional. Leave empty to draw a plain coloured bar the size of the sword's reach.")]
        public Sprite sprite;
        [Tooltip("Overall size of the Sprite (width AND height together). 1 = the sprite's normal size.")]
        [Min(0.01f)] public float spriteScale = 1f;
        [Tooltip("Stretches the Sprite across, as it is drawn in the picture. Multiplies with Sprite Scale. 1 = normal.")]
        [Min(0.01f)] public float spriteWidth = 1f;
        [Tooltip("Stretches the Sprite along, as it is drawn in the picture. Multiplies with Sprite Scale. 1 = normal.")]
        [Min(0.01f)] public float spriteHeight = 1f;
        [Tooltip("ON: the hit range comes from the Sprite's real size after the scale, width and height above. OFF: the Reach number is used.")]
        public bool fitHitboxToSprite = true;
        [Tooltip("Extra hit range past the sprite's tip. Can be negative to shorten it. Only used with Fit Hitbox To Sprite ON.")]
        public float hitReachPadding = 0f;
        [Tooltip("Degrees to turn the Sprite so it points along the swing. A sword drawn pointing UP needs -90, one pointing RIGHT needs 0.")]
        public float spriteRotationOffset = -90f;
        [Tooltip("Colour of the placeholder bar (only used while no Sprite is assigned). Also the colour of a tool's slot.")]
        public Color placeholderColor = new Color(0.85f, 0.85f, 0.9f);
        [Tooltip("Thickness of the placeholder bar in Unity units.")]
        public float placeholderThickness = 0.2f;
        [Tooltip("Ice sparks along the blade. OFF = none.")]
        public bool showBladeParticles = true;
        [Tooltip("How many sparks. 1 = normal, 0.5 = half as many, 2 = twice as many.")]
        [Min(0f)] public float particleAmount = 1f;

        /// <summary>A copy of these numbers. SwordController keeps the Inspector ones untouched and works on copies.</summary>
        public SwordStats Clone() { return (SwordStats)MemberwiseClone(); }
    }

    /// <summary>
    /// Extra strength added on top of a slot's own numbers. Everything is "added": 0 = no change.
    /// One of these comes from the slot's shop upgrades, one from the player's upgrades; both are summed.
    /// </summary>
    [System.Serializable]
    public struct SwordBonus
    {
        [Tooltip("0.25 = +25% damage (sword hits, spins and every tool's damage).")]
        public float damagePercent;
        [Tooltip("0.05 = +5 percentage points of crit chance (Frost Sword).")]
        public float critChanceAdd;
        [Tooltip("Not used any more (the lightning sword was scrapped). Kept so the shop and power-ups still compile.")]
        public float chainDamagePercent;
        [Tooltip("Not used any more (the fire sword was scrapped). Kept so the shop and power-ups still compile.")]
        public float trailDamagePercent;
        [Tooltip("0.04 = +4 percentage points of freeze chance (Frost Sword).")]
        public float freezeChanceAdd;
        [Tooltip("0.04 = heal 4% of the damage dealt (Frost Sword).")]
        public float lifeStealAdd;
        [Tooltip("0.20 = 20% faster swings, spins and cooldowns (sword and every tool).")]
        public float attackSpeedPercent;

        public static SwordBonus operator +(SwordBonus a, SwordBonus b)
        {
            return new SwordBonus
            {
                damagePercent = a.damagePercent + b.damagePercent,
                critChanceAdd = a.critChanceAdd + b.critChanceAdd,
                chainDamagePercent = a.chainDamagePercent + b.chainDamagePercent,
                trailDamagePercent = a.trailDamagePercent + b.trailDamagePercent,
                freezeChanceAdd = a.freezeChanceAdd + b.freezeChanceAdd,
                lifeStealAdd = a.lifeStealAdd + b.lifeStealAdd,
                attackSpeedPercent = a.attackSpeedPercent + b.attackSpeedPercent
            };
        }
    }

    private class ToolSlot
    {
        public ToolKind kind;
        public string name;
        public SwordStats stats;      // what HUDs and the shop read (the sword's real numbers, or a small stand-in for a tool)
        public PlayerTool tool;       // null for the sword
        public ToolSettings settings; // null for the sword
    }

    // ------------------------------------------------------------ inspector

    [Header("Frost Sword (the one sword that stays)")]
    [Tooltip("The Frost Sword. (These numbers only fill in when the component is first added; use the component's Reset to get them back.)")]
    [SerializeField] private SwordStats frostSword = new SwordStats
    {
        swordName = "Ice Sword", damage = 12f, maxTargets = 2,
        swingTime = 0.3f, cooldown = 0.2f, reach = 1.3f, arc = 110f, knockback = 0.4f,
        spinChargeTime = 1.5f, spinTime = 0.5f, glowColor = new Color(0.4f, 0.9f, 1f),
        slowFactor = 0.5f, slowTime = 3f, freezeChance = 0.12f, freezeTime = 2.5f,
        placeholderColor = new Color(0.5f, 0.9f, 1f)
    };
    [Tooltip("Slot the player starts with (0 = the Frost Sword, 1 = Scanner Lens, 2 = Magic Boots, 3 = Pulse Beam, 4 = Grapple Arm, 5 = Bubble Shield).")]
    [SerializeField] private int startingSword = 0;

    [Header("Tools")]
    [SerializeField] private ScannerLensTool.Settings scannerLens = new ScannerLensTool.Settings();
    [SerializeField] private MagicBootsTool.Settings magicBoots = new MagicBootsTool.Settings();
    [SerializeField] private PulseBeamTool.Settings pulseBeam = new PulseBeamTool.Settings();
    [SerializeField] private GrappleArmTool.Settings grappleArm = new GrappleArmTool.Settings();
    [SerializeField] private BubbleShieldTool.Settings bubbleShield = new BubbleShieldTool.Settings();
    [Tooltip("Layers the Pulse Beam and the Grapple Arm treat as 'interactable' (crates, blocks...). Anything with a ToolInteractable component counts too.")]
    [SerializeField] private LayerMask interactableMask = 0;

    [Header("Controls (the keys and buttons themselves live in PlayerInputSource)")]
    [Tooltip("Number keys 1-9 pick the matching slot (Player 1's keyboard only). Turn off if those keys already do something else.")]
    [SerializeField] private bool numberKeysPickSword = true;

    [Header("Jump")]
    [Tooltip("Multiplies TopDownJump's jump height before the blade is lifted. 1 = the blade rises exactly as far as the jump height says.")]
    [Min(0f)] [SerializeField] private float jumpLiftScale = 1f;

    [Header("Swing")]
    [Tooltip("Where the swing (and the tools' beams, hooks and bubble) is centred, relative to the player's position. If the player's pivot is at its feet, raise this to about chest height.")]
    [SerializeField] private Vector2 pivotOffset = new Vector2(0f, 0.3f);
    [Tooltip("Fraction of the swing spent winding up before the blade can hit anything. 0 = hits straight away.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float windup = 0.15f;
    [Tooltip("Layers the sword and the tools look at. Everything is fine: only objects with a FoeController are ever hurt.")]
    [SerializeField] private LayerMask foeMask = ~0;

    [Header("Sound")]
    [Tooltip("The AudioSource that plays the sword sounds. Put it on its OWN empty child object (not on the player) with a SoundSwitch on it.")]
    [SerializeField] private AudioSource sfxSource;
    [Tooltip("Played when a hit is a critical hit.")]
    [SerializeField] private AudioClip critClip;

    [Header("Debug")]
    [Tooltip("Prints one Console line per sword hit so you can tune the numbers.")]
    [SerializeField] private bool debugLog = false;
    [Tooltip("ON: the player owns EVERY slot at once (also works live in Play mode). Untick it before a final build. Unticking does not lock them again.")]
    [SerializeField] private bool debugUnlockAllSwords = false;

    // ------------------------------------------------------------ public

    /// <summary>The equipped slot's numbers as they really are right now. For a tool this is a small stand-in (name, sprite, colour).</summary>
    public SwordStats CurrentSword { get { return slots[currentIndex].stats; } }
    public int CurrentIndex { get { return currentIndex; } }
    public bool IsSwinging { get { return swinging || spinning; } }
    /// <summary>How many slots there are (the Frost Sword plus the five tools).</summary>
    public int SwordCount { get { return slots.Count; } }

    /// <summary>What the equipped slot is.</summary>
    public ToolKind CurrentKind { get { return slots[currentIndex].kind; } }
    /// <summary>The equipped tool, or null while the Frost Sword is in hand.</summary>
    public PlayerTool CurrentTool { get { return slots[currentIndex].tool; } }

    /// <summary>True while the Magic Boots make the player hover. A floor trap can skip a hovering player.</summary>
    public bool IsHovering { get { return boots != null && boots.Hovering; } }

    /// <summary>Which player this is, from the PlayerInputSource on the same object.</summary>
    public int PlayerNumber { get { return input != null ? input.playerNumber : 1; } }

    /// <summary>Every SwordController that is switched on (one per player in the game).</summary>
    public static readonly List<SwordController> All = new List<SwordController>();

    /// <summary>The SwordController of this player number, or null if that player isn't in the game right now.</summary>
    public static SwordController Get(int playerNumber)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i].PlayerNumber == playerNumber) return All[i];
        }
        return null;
    }

    /// <summary>The numbers slot <paramref name="index"/> really uses right now. For HUDs.</summary>
    public SwordStats StatsAt(int index)
    {
        if (index < 0 || index >= slots.Count) return null;
        return slots[index].stats;
    }

    public ToolKind KindAt(int index)
    {
        return (index < 0 || index >= slots.Count) ? ToolKind.Sword : slots[index].kind;
    }

    /// <summary>The tool in this slot, or null for the sword. For HUDs (cooldowns, charges, timers).</summary>
    public PlayerTool ToolAt(int index)
    {
        return (index < 0 || index >= slots.Count) ? null : slots[index].tool;
    }

    /// <summary>Position of the slot with this name, or -1.</summary>
    public int IndexOf(string swordName)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (string.Equals(slots[i].name, swordName, System.StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>Position of the slot holding this kind of tool, or -1.</summary>
    public int IndexOfKind(ToolKind kind)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].kind == kind) return i;
        }
        return -1;
    }

    /// <summary>
    /// Extra strength from timed power-ups (PlayerEffects hands it in). Added on top of the shop and player upgrades,
    /// for THIS player only, and replaced every time it is set. All zeros = no power-up.
    /// </summary>
    public void SetTimedBonus(SwordBonus bonus)
    {
        timedBonus = bonus;
        RebuildEffective();
    }

    /// <summary>
    /// PVP shock: for <paramref name="seconds"/>, this player's swings run at <paramref name="factor"/> of normal speed.
    /// (No slot uses it any more, since the lightning sword was scrapped; kept so other scripts still compile.)
    /// </summary>
    public void ApplyShock(float factor, float seconds)
    {
        if (seconds <= 0f || factor >= 1f) return;

        factor = Mathf.Clamp(factor, 0.1f, 1f);
        shockFactor = shockTimeLeft > 0f ? Mathf.Min(shockFactor, factor) : factor;
        shockTimeLeft = Mathf.Max(shockTimeLeft, seconds);
    }

    /// <summary>Ends every PVP effect on this player right now: the swing shock, and the shove and walking slow on the character.</summary>
    public void ClearPvpEffects()
    {
        shockTimeLeft = 0f;
        shockFactor = 1f;

        if (mover != null) mover.ClearHitEffects();
    }

    /// <summary>Fires when a slot becomes owned. Hook a "new tool!" message to it.</summary>
    public event System.Action<SwordStats> SwordUnlocked;

    /// <summary>Fires whenever the equipped slot changes. Hook a HUD label to it.</summary>
    public event System.Action<SwordStats> SwordChanged;

    /// <summary>A 1x1-unit white square, made once. Used (tinted and stretched) for the stand-in blade and the tools' beams, hooks and flashes.</summary>
    public static Sprite WhiteSquare
    {
        get
        {
            if (whiteSquare == null)
            {
                Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                tex.filterMode = FilterMode.Point;
                whiteSquare = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            }
            return whiteSquare;
        }
    }

    /// <summary>A soft white circle, 1 unit wide, made once.</summary>
    public static Sprite SoftCircle { get { return GlowSprite(); } }

    // ------------------------------------------------------------ what the tools use (internal)

    internal bool AttackPressedNow;
    internal bool AttackHeldNow;
    internal bool AttackReleasedNow;
    internal bool NextPressedNow;
    internal bool PrevPressedNow;

    internal int PlayerNo { get { return PlayerNumber; } }
    internal Rigidbody2D Rb { get { return body; } }
    internal Collider2D[] PlayerColliders { get { return playerColliders; } }
    internal ContactFilter2D FoeFilter { get { return foeFilter; } }
    internal ContactFilter2D SolidFilter { get { return solidFilter; } }
    internal int SortingLayerId { get { return playerSprite != null ? playerSprite.sortingLayerID : 0; } }
    internal int SortingOrder { get { return playerSprite != null ? playerSprite.sortingOrder : 0; } }

    /// <summary>Degrees the player faces: 0 = right, 90 = up, 180 = left, 270 = down.</summary>
    internal float FacingAngle { get { return FacingToAngle(ReadFacing()); } }
    internal Vector2 FacingVector { get { return AngleToVector(FacingToAngle(ReadFacing())); } }
    /// <summary>-1 when facing left, so a swipe still travels downward on screen.</summary>
    internal float FacingSweepSign { get { return ReadFacing() == 3 ? -1f : 1f; } }

    /// <summary>The player's hand where the body really is (hit checks use this: a jump is only a picture).</summary>
    internal Vector3 HandGround { get { return HandOnGround; } }
    /// <summary>The player's hand as drawn (lifted by the jump).</summary>
    internal Vector3 HandVisual { get { return pivot != null ? pivot.position : HandOnGround; } }

    /// <summary>The way the player has been walking in the last moment, or zero if they have been standing still.</summary>
    internal Vector2 LastMoveDirection { get { return (Time.time - lastMoveTime) < 0.25f ? lastMoveDir : Vector2.zero; } }

    /// <summary>True while the player is pressing a direction (the walking animation flag), or always true if that flag does not exist.</summary>
    internal bool WantsToMove
    {
        get { return hasIsMoving && animator != null ? animator.GetBool("IsMoving") : true; }
    }

    /// <summary>The attack-speed bonus makes cooldowns shorter: multiply a cooldown by this.</summary>
    internal float CooldownFactor(string toolName)
    {
        float speed = 1f + Mathf.Max(-0.9f, TotalBonus(toolName).attackSpeedPercent);
        return 1f / speed;
    }

    /// <summary>The damage bonus: multiply a tool's damage by this.</summary>
    internal float DamageFactor(string toolName)
    {
        return Mathf.Max(0f, 1f + TotalBonus(toolName).damagePercent);
    }

    private SwordBonus TotalBonus(string toolName)
    {
        return GetUpgradeBonus(PlayerNumber, toolName) + playerBonuses[Slot(PlayerNumber)] + timedBonus;
    }

    /// <summary>Hurts a foe for this player (the score goes to them) and shoves it.</summary>
    internal void DamageFoe(FoeController foe, float damage, Vector2 pushDirection, float knockbackDistance)
    {
        if (foe == null) return;

        CoopScore.CreditedPlayer = PlayerNumber;
        foe.TakeDamage(damage);
        CoopScore.CreditedPlayer = 0;

        if (knockbackDistance > 0f) foe.Knockback(pushDirection, knockbackDistance);
    }

    /// <summary>
    /// PVP: hurts the OTHER player (if PVP is on and they can be hurt) through CoopHealth.ApplyDamage, scaled by PvpMode.DamageScale,
    /// and shoves them. True if the hit really landed (a jumping or invincible victim takes nothing and is not shoved).
    /// </summary>
    internal bool PvpTryHurt(CoopHealth victim, float damage, Vector2 pushDirection, float knockbackDistance)
    {
        if (!PvpMode.Enabled || victim == null || !victim.IsAlive) return false;
        if (!PvpMode.CanHurt(PlayerNumber, victim.PlayerNumber)) return false;

        HealthComponent victimHealth = FindOn<HealthComponent>(victim);
        float healthBefore = victimHealth != null ? victimHealth.CurrentHealth : 0f;

        victim.ApplyDamage(damage * PvpMode.DamageScale);

        bool landed = victimHealth == null || victimHealth.CurrentHealth < healthBefore - 0.0001f;
        if (landed && victim.IsAlive && knockbackDistance > 0f && PvpMode.KnockbackScale > 0f)
        {
            TopDownCharacterController victimMover = FindOn<TopDownCharacterController>(victim);
            if (victimMover != null) victimMover.Knockback(pushDirection, knockbackDistance * PvpMode.KnockbackScale);
        }
        return landed;
    }

    /// <summary>PVP: hurts every other player whose body is inside this circle, each at most once per "already" set. Returns how many were hurt.</summary>
    internal int PvpHitCircle(Vector2 center, float radius, HashSet<CoopHealth> already, float damage, float knockbackDistance, Vector2 pushDirection)
    {
        if (!PvpMode.Enabled) return 0;

        int hurt = 0;
        for (int i = 0; i < CoopHealth.Players.Count; i++)
        {
            CoopHealth other = CoopHealth.Players[i];
            if (other == null || !other.IsAlive || already.Contains(other)) continue;
            if (!PvpMode.CanHurt(PlayerNumber, other.PlayerNumber)) continue;

            Collider2D bodyCollider = PvpColliderOf(other);
            Vector2 nearest = bodyCollider != null ? bodyCollider.ClosestPoint(center) : (Vector2)other.transform.position;
            if (Vector2.Distance(center, nearest) > radius) continue;

            already.Add(other);
            if (PvpTryHurt(other, damage, pushDirection, knockbackDistance)) hurt++;
        }
        return hurt;
    }

    /// <summary>True if this collider is solid scenery for a tool: not a trigger, not this player, not a foe, not a player, not an object this tool may move.</summary>
    internal bool IsWall(Collider2D c, bool forGrapple)
    {
        if (c == null || c.isTrigger) return false;
        if (c.transform.IsChildOf(transform)) return false;
        if (c.GetComponentInParent<FoeController>() != null) return false;
        if (c.GetComponentInParent<CoopHealth>() != null) return false;

        ToolInteractable marker;
        if (FindInteractable(c, forGrapple, out marker) != null) return false;
        return true;
    }

    /// <summary>
    /// The object this collider belongs to if the Pulse Beam (or the Grapple Arm) may move it: it has a ToolInteractable that allows it,
    /// or it is on the Interactable Mask layers. Otherwise null.
    /// </summary>
    internal Transform FindInteractable(Collider2D c, bool forGrapple, out ToolInteractable marker)
    {
        marker = null;
        if (c == null || c.isTrigger) return null;

        marker = c.GetComponentInParent<ToolInteractable>();
        if (marker != null)
        {
            if (!marker.enabled) { marker = null; return null; }
            if (forGrapple ? !marker.allowGrapple : !marker.allowPulseBeam) { marker = null; return null; }
            if (marker.IsHeld && marker.HeldBy != this) { marker = null; return null; }
            return marker.transform;
        }

        if ((interactableMask.value & (1 << c.gameObject.layer)) != 0)
        {
            Rigidbody2D rb = c.attachedRigidbody;
            return rb != null ? rb.transform : c.transform;
        }
        return null;
    }

    /// <summary>Switches the walking script off while a dash or a pull moves the player. Several things can ask; it comes back when all let go.</summary>
    internal void LockMovement(object key)
    {
        moveLocks.Add(key);
        if (mover != null) mover.enabled = false;
    }

    internal void UnlockMovement(object key)
    {
        if (moveLocks.Remove(key) && moveLocks.Count == 0 && mover != null) mover.enabled = true;
    }

    /// <summary>Makes the player walk this many times faster (or slower) while the key's effect lasts. All the effects multiply together.</summary>
    internal void SetSpeedMultiplier(object key, float multiplier)
    {
        speedSources[key] = multiplier;
        ApplySpeed();
    }

    internal void ClearSpeedMultiplier(object key)
    {
        if (speedSources.Remove(key)) ApplySpeed();
    }

    private void ApplySpeed()
    {
        float product = 1f;
        foreach (KeyValuePair<object, float> kv in speedSources) product *= kv.Value;

        if (speedAccess == null) speedAccess = new MoveSpeedAccess(mover);
        speedAccess.SetMultiplier(product);
    }

    // ------------------------------------------------------------ state

    private PlayerInputSource input;    // this player's buttons (keyboard half + controller)
    private SwordBonus timedBonus;       // power-ups running right now (PlayerEffects)
    private float shockFactor = 1f;
    private float shockTimeLeft;
    private Animator animator;
    private SpriteRenderer playerSprite;
    private HealthComponent playerHealth;
    private TopDownCharacterController mover;
    private Rigidbody2D body;
    private Collider2D[] playerColliders = new Collider2D[0];
    private bool hasIsMoving;

    private readonly List<ToolSlot> slots = new List<ToolSlot>();
    private ScannerLensTool lens;
    private MagicBootsTool boots;

    private SwordElementFx fx;           // ice sparks on the blade
    private bool fxFrozen;               // true while the sparks are held still by a pause
    private bool debugUnlockApplied;

    private bool prevAttackHeld;
    private int numberKeyThisFrame = -1;
    private Vector2 lastPos;
    private Vector2 lastMoveDir = Vector2.down;
    private float lastMoveTime = -10f;
    private float lastHealth = -1f;

    private readonly HashSet<object> moveLocks = new HashSet<object>();
    private readonly Dictionary<object, float> speedSources = new Dictionary<object, float>();
    private MoveSpeedAccess speedAccess;

    private Transform pivot;             // the "hand": rotates, carrying the blade with it
    private Transform visualTransform;   // the blade, sitting out along the pivot's +X
    private SpriteRenderer visual;
    private SpriteRenderer glowRenderer; // halo behind the player while the spin charges
    private TopDownJump playerJump;       // optional: without it the blade simply stays at the hand
    private int airFrames;
    private bool liftWarned;
    private float activeReach;           // hit range of the sword (its Reach, or measured from its sprite)
    private float activeHalfWidth;       // half the blade's thickness, widens the swing a little (0 = off)

    private ContactFilter2D foeFilter;
    private ContactFilter2D solidFilter;
    private readonly Collider2D[] overlapBuffer = new Collider2D[32];
    private readonly HashSet<FoeController> hitFoes = new HashSet<FoeController>();
    // PVP: the players already hit by this swing / spin (each player is hit at most once per swing, like foes), and their solid colliders.
    private readonly HashSet<CoopHealth> hitPlayers = new HashSet<CoopHealth>();
    private readonly Dictionary<CoopHealth, Collider2D> pvpColliders = new Dictionary<CoopHealth, Collider2D>();

    // The arc being swept right now, so a breakable can ask "is this point under the blade?" (see PointInSweep).
    private Vector2 sweepOrigin;
    private float sweepFrom;
    private float sweepSpan;
    private System.Func<Vector2, bool> sweepTest;

    private int currentIndex;
    private bool swinging;
    private bool spinning;
    private bool charging;        // the attack key is being held after a swing started
    private float chargeTimer;
    private float swingTimer;
    private float cooldownLeft;
    private int bladeHitCount;    // foes the blade hit this swing

    // Locked in at the moment the swing / spin starts.
    private int swingFacing;
    private float facingAngle;     // degrees, 0 = right, 90 = up
    private float sweepSign;       // flips for left-facing swings so the blade always travels downward on screen
    private float previousAngle;   // where the blade was at the last hit check (absolute degrees)

    // Hit checks give a foe's centre this many extra degrees of leeway, since foes have width.
    internal const float AngleSlack = 10f;
    // A long frame (a hitch) is treated as at most this long, so the blade can't skip over foes.
    private const float MaxStep = 0.05f;

    private static Sprite whiteSquare;
    private static Sprite glowSprite;

    // Kept between scenes (the Shop scene and back), one set per player (index = player number 1..4, 0 is unused).
    private const int MaxPlayers = 4;
    private static readonly HashSet<string>[] owned = NewPools();
    private static readonly bool[] poolReady = new bool[MaxPlayers + 1];
    private static readonly string[] startingNames = new string[MaxPlayers + 1];
    private static readonly List<string>[] startUnlocked = NewNameLists();
    private static readonly Dictionary<string, SwordBonus>[] swordBonuses = NewBonusTables();
    private static readonly SwordBonus[] playerBonuses = new SwordBonus[MaxPlayers + 1];
    private static event System.Action UpgradesChanged;

    private static HashSet<string>[] NewPools()
    {
        HashSet<string>[] pools = new HashSet<string>[MaxPlayers + 1];
        for (int i = 0; i <= MaxPlayers; i++) pools[i] = new HashSet<string>();
        return pools;
    }

    private static List<string>[] NewNameLists()
    {
        List<string>[] lists = new List<string>[MaxPlayers + 1];
        for (int i = 0; i <= MaxPlayers; i++) lists[i] = new List<string>();
        return lists;
    }

    private static Dictionary<string, SwordBonus>[] NewBonusTables()
    {
        Dictionary<string, SwordBonus>[] tables = new Dictionary<string, SwordBonus>[MaxPlayers + 1];
        for (int i = 0; i <= MaxPlayers; i++) tables[i] = new Dictionary<string, SwordBonus>();
        return tables;
    }

    private static int Slot(int playerNumber) { return Mathf.Clamp(playerNumber, 1, MaxPlayers); }

    // ------------------------------------------------------------ setup

    private void Awake()
    {
        // The PlayerInputHub in the scene gives this character its player slot; no input component is needed on the prefab.
        input = PlayerInputSource.For(gameObject);

        animator = GetComponent<Animator>();
        playerSprite = GetComponent<SpriteRenderer>();
        if (playerSprite == null) playerSprite = GetComponentInChildren<SpriteRenderer>();   // looked up BEFORE the blade exists, so it can't find itself
        playerJump = GetComponent<TopDownJump>();
        mover = GetComponent<TopDownCharacterController>();
        body = GetComponent<Rigidbody2D>();
        playerHealth = GetComponentInParent<HealthComponent>();
        if (playerHealth == null) playerHealth = GetComponentInChildren<HealthComponent>();

        List<Collider2D> solidColliders = new List<Collider2D>();
        Collider2D[] found = GetComponentsInChildren<Collider2D>();
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && !found[i].isTrigger) solidColliders.Add(found[i]);
        }
        playerColliders = solidColliders.ToArray();

        if (animator != null && animator.runtimeAnimatorController != null)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name == "IsMoving" && parameters[i].type == AnimatorControllerParameterType.Bool) hasIsMoving = true;
            }
        }

        foeFilter = new ContactFilter2D();
        foeFilter.SetLayerMask(foeMask);
        foeFilter.useTriggers = false;

        solidFilter = new ContactFilter2D();
        solidFilter.SetLayerMask(Physics2D.GetLayerCollisionMask(gameObject.layer));
        solidFilter.useTriggers = false;

        if (sfxSource != null) sfxSource.playOnAwake = false;

        // The slots: the Frost Sword, then the five tools.
        slots.Clear();
        AddSlot(ToolKind.Sword, null, null);
        lens = new ScannerLensTool(this, scannerLens);
        boots = new MagicBootsTool(this, magicBoots);
        AddSlot(ToolKind.ScannerLens, lens, scannerLens);
        AddSlot(ToolKind.MagicBoots, boots, magicBoots);
        AddSlot(ToolKind.PulseBeam, new PulseBeamTool(this, pulseBeam), pulseBeam);
        AddSlot(ToolKind.GrappleArm, new GrappleArmTool(this, grappleArm), grappleArm);
        AddSlot(ToolKind.BubbleShield, new BubbleShieldTool(this, bubbleShield), bubbleShield);

        // Pivot (the hand) with the blade as its child. Both are created here, so there is nothing to set up by hand.
        pivot = new GameObject("SwordPivot").transform;
        pivot.SetParent(transform, false);
        pivot.localPosition = pivotOffset;

        visualTransform = new GameObject("SwordVisual").transform;
        visualTransform.SetParent(pivot, false);
        visual = visualTransform.gameObject.AddComponent<SpriteRenderer>();
        visual.enabled = false;   // only visible while swinging

        // Ice sparks, riding on the pivot so they follow the blade.
        fx = new SwordElementFx(pivot, visual.sharedMaterial);

        // The glow halo, behind the player.
        glowRenderer = new GameObject("SpinGlow").AddComponent<SpriteRenderer>();
        glowRenderer.transform.SetParent(transform, false);
        glowRenderer.transform.localPosition = pivotOffset;
        glowRenderer.sprite = GlowSprite();
        glowRenderer.enabled = false;
        StyleRenderer(glowRenderer, -1);

        currentIndex = Mathf.Clamp(startingSword, 0, slots.Count - 1);
        InitUnlocks();
        RebuildEffective();   // works out the numbers actually used and puts the blade on screen

        if (slots[currentIndex].tool != null) slots[currentIndex].tool.OnEquipped();
        lastPos = transform.position;
    }

    private void AddSlot(ToolKind kind, PlayerTool tool, ToolSettings settings)
    {
        ToolSlot slot = new ToolSlot();
        slot.kind = kind;
        slot.tool = tool;
        slot.settings = settings;
        slots.Add(slot);
    }

    private void OnEnable()
    {
        UpgradesChanged += RebuildEffective;
        if (!All.Contains(this)) All.Add(this);
    }

    private void OnDisable()
    {
        UpgradesChanged -= RebuildEffective;
        All.Remove(this);

        // Never leave the stage frozen, a dash half done or a shield buff behind.
        CancelTools();
        PlayerBuffs.ClearAll(PlayerNumber);
    }

    private void OnDestroy()
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].tool != null) slots[i].tool.DestroyVisuals();
        }
    }

    // Copies the player's sorting layer and puts this renderer a few orders above (or below) the player.
    private void StyleRenderer(SpriteRenderer sr, int orderOffset)
    {
        if (playerSprite == null) return;
        sr.sortingLayerID = playerSprite.sortingLayerID;
        sr.sortingOrder = playerSprite.sortingOrder + orderOffset;
    }

    // ------------------------------------------------------------ switching

    /// <summary>Equip the slot at this position. Ignored in the middle of a swing, a spin or a tool action.</summary>
    public void EquipSword(int index)
    {
        if (slots.Count == 0 || IsBusy) return;
        index = Mathf.Clamp(index, 0, slots.Count - 1);
        if (index == currentIndex) return;
        if (!IsUnlocked(index)) return;   // locked slots can't be equipped

        charging = false;   // a half-charged spin doesn't carry over
        ToolSlot old = slots[currentIndex];
        if (old.tool != null) old.tool.OnUnequipped();

        currentIndex = index;
        ToolSlot now = slots[currentIndex];
        if (now.tool != null) now.tool.OnEquipped();
        else ApplyVisual(slots[0].stats);

        SwordChanged?.Invoke(now.stats);
    }

    /// <summary>Equip the slot holding this kind of tool (if the player owns it).</summary>
    public void EquipTool(ToolKind kind)
    {
        int index = IndexOfKind(kind);
        if (index >= 0) EquipSword(index);
    }

    /// <summary>+1 = next slot, -1 = previous. Wraps around the list and skips slots that are still locked.</summary>
    public void EquipNext(int step)
    {
        if (slots.Count == 0 || step == 0) return;
        int direction = step > 0 ? 1 : -1;
        for (int i = 1; i <= slots.Count; i++)
        {
            int next = ((currentIndex + direction * i) % slots.Count + slots.Count) % slots.Count;
            if (IsUnlocked(next))
            {
                EquipSword(next);
                return;
            }
        }
    }

    private bool IsBusy
    {
        get
        {
            if (swinging || spinning) return true;
            PlayerTool tool = slots[currentIndex].tool;
            return tool != null && tool.IsBusy;
        }
    }

    private void HandleSwitchInput()
    {
        if (NextPressedNow) EquipNext(1);
        else if (PrevPressedNow) EquipNext(-1);
        else if (numberKeysPickSword)
        {
            if (numberKeyThisFrame >= 0 && numberKeyThisFrame < slots.Count) EquipSword(numberKeyThisFrame);
        }
    }

    // Puts the sword's look onto the blade object, and works out the hit range to match.
    private void ApplyVisual(SwordStats s)
    {
        activeReach = s.reach;
        activeHalfWidth = 0f;

        if (s.sprite != null)
        {
            visual.sprite = s.sprite;
            visual.color = Color.white;
            visualTransform.localScale = new Vector3(SpriteScaleX(s), SpriteScaleY(s), 1f);
            visualTransform.localRotation = Quaternion.Euler(0f, 0f, s.spriteRotationOffset);

            if (s.fitHitboxToSprite)
            {
                Blade blade = MeasureBlade(s);
                visualTransform.localPosition = new Vector3(-blade.start, 0f, 0f);
                activeReach = blade.reach;
                activeHalfWidth = blade.halfThickness;
            }
            else
            {
                visualTransform.localPosition = new Vector3(s.reach * 0.5f, 0f, 0f);
            }
        }
        else
        {
            visual.sprite = WhiteSquare;
            visual.color = s.placeholderColor;
            visualTransform.localScale = new Vector3(s.reach, s.placeholderThickness, 1f);   // spans the hit range exactly
            visualTransform.localRotation = Quaternion.identity;
            visualTransform.localPosition = new Vector3(s.reach * 0.5f, 0f, 0f);
        }

        if (fx != null) fx.Configure(s, activeReach);   // the sparks that fit this sword, along the blade just placed
    }

    private static float SpriteScaleX(SwordStats s) { return (s.spriteScale > 0f ? s.spriteScale : 1f) * (s.spriteWidth > 0f ? s.spriteWidth : 1f); }
    private static float SpriteScaleY(SwordStats s) { return (s.spriteScale > 0f ? s.spriteScale : 1f) * (s.spriteHeight > 0f ? s.spriteHeight : 1f); }

    // Where a sprite blade starts and ends along the swing direction (the pivot's +X), measured from the sprite's own size.
    private struct Blade
    {
        public float start;
        public float reach;
        public float halfThickness;
    }

    private static Blade MeasureBlade(SwordStats s)
    {
        Bounds b = s.sprite.bounds;
        float sx = SpriteScaleX(s);
        float sy = SpriteScaleY(s);
        Quaternion turn = Quaternion.Euler(0f, 0f, s.spriteRotationOffset);

        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            Vector3 corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, 0f);
            corner = turn * new Vector3(corner.x * sx, corner.y * sy, 0f);
            minX = Mathf.Min(minX, corner.x);
            maxX = Mathf.Max(maxX, corner.x);
            minY = Mathf.Min(minY, corner.y);
            maxY = Mathf.Max(maxY, corner.y);
        }

        // The thickness is only trustworthy when the sprite is turned by a multiple of 90 degrees.
        float quarter = Mathf.Abs(Mathf.DeltaAngle(0f, s.spriteRotationOffset)) % 90f;
        bool straight = quarter < 1f || quarter > 89f;

        return new Blade
        {
            start = minX,
            reach = Mathf.Max(0.1f, (maxX - minX) + s.hitReachPadding),
            halfThickness = straight ? (maxY - minY) * 0.5f : 0f
        };
    }

    private static float ReachOf(SwordStats s)
    {
        return (s.sprite != null && s.fitHitboxToSprite) ? MeasureBlade(s).reach : s.reach;
    }

    // Lets you change Sprite Scale / Width / Height / Padding in the Inspector during Play mode and see it at once.
    private void OnValidate()
    {
        if (!Application.isPlaying || visualTransform == null || slots.Count == 0) return;
        RebuildEffective();
    }

    // A soft white circle (bright in the middle, fading out), 1 unit wide. Tinted with the glow colour.
    internal static Sprite GlowSprite()
    {
        if (glowSprite == null)
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius)) / radius;
                    float a = Mathf.Clamp01(1f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            tex.Apply();
            glowSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
        }
        return glowSprite;
    }

    // ------------------------------------------------------------ main loop

    private void Update()
    {
        if (slots.Count == 0) return;

        // Notebook open, stairs menu, pause menu (or foes frozen by hand): everything freezes and nothing can be hit.
        bool paused = FoeController.IsPaused;

        // The Scanner Lens freezes the stage itself. While it does, this player's lens keeps running and nothing else does.
        bool lensFreeze = lens != null && lens.IsFrozen;

        // The sparks are separate particle systems that keep running on their own, so they are held still by hand.
        if (fx != null && paused != fxFrozen)
        {
            fxFrozen = paused;
            fx.SetPaused(paused);
        }

        if (paused && !lensFreeze)
        {
            prevAttackHeld = input.AttackHeld;
            return;
        }

        // The frame a menu closes on: the click or key that closed it must not also swing the sword.
        if (!lensFreeze && FloorController.BlocksInput)
        {
            prevAttackHeld = input.AttackHeld;
            return;
        }

        if (lensFreeze)
        {
            ReadButtons();
            lens.TickEquipped(Time.unscaledDeltaTime);
            return;
        }

        // A dead player can't swing. Neither can a player who doesn't count (single player mode ignores them).
        if ((playerHealth != null && playerHealth.CurrentHealth <= 0f) || !CoopRules.Counts(PlayerNumber))
        {
            CancelAttack();
            prevAttackHeld = input.AttackHeld;
            lastHealth = -1f;
            return;
        }

        float dt = Mathf.Min(Time.deltaTime, MaxStep);

        // Debug box ticked (also live in Play mode): own every slot.
        if (debugUnlockAllSwords)
        {
            if (!debugUnlockApplied)
            {
                debugUnlockApplied = true;
                UnlockAllSwords();
            }
        }
        else
        {
            debugUnlockApplied = false;
        }

        float attackDt = dt;
        if (shockTimeLeft > 0f)
        {
            if (PvpMode.Enabled)
            {
                shockTimeLeft -= dt;
                attackDt = dt * shockFactor;
            }
            else
            {
                shockTimeLeft = 0f;
            }
        }

        TrackMovement();
        ReadButtons();

        // Cooldowns, buff timers and the like run for every tool, in hand or not.
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].tool != null) slots[i].tool.TickAlways(dt);
        }
        WatchDamage();

        ToolSlot current = slots[currentIndex];
        if (current.tool != null)
        {
            UpdateTool(current, dt);
            return;
        }

        // ---- the Frost Sword ----
        if (spinning)
        {
            UpdateSpin(attackDt);
            return;
        }

        if (swinging) UpdateSwing(attackDt);

        UpdateCharge(dt);   // may start the spin

        if (!swinging && !spinning)
        {
            cooldownLeft -= attackDt;
            if (!charging) HandleSwitchInput();

            if (!charging && cooldownLeft <= 0f && AttackPressedNow)
            {
                StartSwing();
                charging = true;   // keep holding after this swing and the spin charges up
                chargeTimer = 0f;
            }
        }

        UpdateGlow();
    }

    private void UpdateTool(ToolSlot current, float dt)
    {
        if (glowRenderer.enabled) glowRenderer.enabled = false;

        // Previous / next switch tools, unless the tool is busy or wants those keys itself (the Pulse Beam moving a held object).
        if (!current.tool.IsBusy && !current.tool.WantsSwitchKeys) HandleSwitchInput();

        if (slots[currentIndex] == current) current.tool.TickEquipped(dt);
    }

    private void FixedUpdate()
    {
        if (slots.Count == 0 || FoeController.IsPaused) return;

        float dt = Time.fixedDeltaTime;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].tool != null) slots[i].tool.FixedTickAlways(dt);
        }
    }

    // Reads every button once a frame, so each tool sees the same presses.
    private void ReadButtons()
    {
        bool held = input.AttackHeld;
        AttackHeldNow = held;
        AttackPressedNow = input.AttackPressed || (held && !prevAttackHeld);
        AttackReleasedNow = !held && prevAttackHeld;
        prevAttackHeld = held;

        NextPressedNow = input.WeaponNextPressed;
        PrevPressedNow = input.WeaponPrevPressed;
        numberKeyThisFrame = numberKeysPickSword ? input.NumberKeyPressed() : -1;   // 0-based, or -1 (Player 1's keyboard only)
    }

    private void TrackMovement()
    {
        Vector2 position = transform.position;
        Vector2 moved = position - lastPos;
        if (moved.sqrMagnitude > 0.000025f)
        {
            lastMoveDir = moved.normalized;
            lastMoveTime = Time.time;
        }
        lastPos = position;
    }

    // While a "take less damage" buff runs and CoopHealth has not been given the one-line hook (see PlayerBuffs), hand back the share of
    // any health the player loses. Once the hook is installed this does nothing.
    private void WatchDamage()
    {
        if (playerHealth == null) return;

        float now = playerHealth.CurrentHealth;
        if (lastHealth < 0f)
        {
            lastHealth = now;
            return;
        }

        float taken = PlayerBuffs.DamageTaken(PlayerNumber);
        if (!PlayerBuffs.HookInstalled && taken < 0.999f && now > 0f && now < lastHealth - 0.0001f)
        {
            float refund = (lastHealth - now) * (1f - Mathf.Clamp01(taken));
            PlayerHealthAccess.Heal(playerHealth, refund);
            now = playerHealth.CurrentHealth;
        }
        lastHealth = now;
    }

    // ------------------------------------------------------------ jumping

    // The fake jump never moves the body: its height is just a number inside TopDownJump. Each frame, after everything else
    // has run, put the blade (with its sparks) that far up the screen. The glow is NOT moved: it stays on the ground.
    // Not gated by the pause: a player frozen mid-air keeps the blade with them.
    private void LateUpdate()
    {
        if (pivot == null) return;

        float height = playerJump != null ? Mathf.Max(0f, playerJump.Height) * jumpLiftScale : 0f;
        pivot.position = HandOnGround + new Vector3(0f, height, 0f);

        // In the air with a height of zero for a whole jump means TopDownJump.Height is not returning the jump's height. Said once.
        bool airborne = playerJump != null && playerJump.IsAirborne;
        if (airborne && !liftWarned)
        {
            if (height > 0.0001f) liftWarned = true;   // it works: stop checking
            else if (++airFrames == 15)
            {
                liftWarned = true;
                Debug.LogWarning("SwordController on '" + name + "': the player is jumping but TopDownJump.Height is 0, so the sword cannot follow. " +
                                 "Make Height return the variable that holds the jump's height (in Unity units).", this);
            }
        }
        if (!airborne) airFrames = 0;

        // The tools draw their beams, hooks, bubble and lens overlay here, after the camera and the player have moved.
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].tool != null) slots[i].tool.LateTickAlways();
        }
    }

    // The hand where the player's body really is. Hit checks use this one.
    private Vector3 HandOnGround
    {
        get { return transform.TransformPoint(pivotOffset); }
    }

    private void CancelAttack()
    {
        swinging = false;
        spinning = false;
        charging = false;
        visual.enabled = false;
        fx.Hide();
        glowRenderer.enabled = false;
        CancelTools();
    }

    private void CancelTools()
    {
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].tool != null) slots[i].tool.Cancel();
        }
    }

    // ------------------------------------------------------------ charging the spin

    // While the key stays held the timer runs. Letting go after the sword's Spin Charge Time starts the spin;
    // letting go earlier just cancels the charge (the normal swing already happened).
    private void UpdateCharge(float dt)
    {
        if (!charging) return;

        if (AttackHeldNow)
        {
            chargeTimer += dt;
            return;
        }

        SwordStats s = slots[0].stats;
        bool ready = !swinging && chargeTimer >= s.spinChargeTime;
        charging = false;
        if (ready) StartSpin();
    }

    // The glow grows softly while charging, then pulses brightly once the spin is ready.
    private void UpdateGlow()
    {
        if (!charging)
        {
            glowRenderer.enabled = false;
            return;
        }

        SwordStats s = slots[0].stats;
        float progress = Mathf.Clamp01(chargeTimer / Mathf.Max(0.05f, s.spinChargeTime));
        bool ready = progress >= 1f && !swinging;

        float alpha;
        float size;
        if (ready)
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 12f);
            alpha = Mathf.Lerp(0.55f, 0.95f, pulse);
            size = Mathf.Lerp(2.4f, 2.9f, pulse);
        }
        else
        {
            alpha = progress * 0.25f;
            size = Mathf.Lerp(1.2f, 2f, progress);
        }

        Color c = s.glowColor;
        c.a = alpha;
        glowRenderer.color = c;
        glowRenderer.transform.localScale = new Vector3(size, size, 1f);
        glowRenderer.enabled = true;
    }

    // ------------------------------------------------------------ normal swing

    private void StartSwing()
    {
        SwordStats s = slots[0].stats;

        swingFacing = ReadFacing();
        facingAngle = FacingToAngle(swingFacing);
        sweepSign = swingFacing == 3 ? -1f : 1f;   // left-facing: sweep the other way round so it still goes down

        swingTimer = 0f;
        previousAngle = facingAngle + s.arc * 0.5f * sweepSign;
        hitFoes.Clear();
        hitPlayers.Clear();
        bladeHitCount = 0;
        swinging = true;

        // Facing up the sword is drawn behind the player, any other way in front.
        StyleRenderer(visual, swingFacing == 1 ? -1 : 1);
        visual.enabled = true;
        fx.Show(visual.sortingLayerID, visual.sortingOrder + (swingFacing == 1 ? 0 : 1));
        pivot.rotation = Quaternion.Euler(0f, 0f, previousAngle);
    }

    private void UpdateSwing(float dt)
    {
        SwordStats s = slots[0].stats;

        swingTimer += dt;
        float t = Mathf.Clamp01(swingTimer / Mathf.Max(0.01f, s.swingTime));
        float eased = t * t * (3f - 2f * t);   // slow start, fast chop, soft landing

        float startOffset = s.arc * 0.5f * sweepSign;
        float angle = facingAngle + Mathf.Lerp(startOffset, -startOffset, eased);
        pivot.rotation = Quaternion.Euler(0f, 0f, angle);

        // After the wind-up, check everything the blade has passed over since the last check.
        if (t >= windup)
        {
            HitSweep(s, previousAngle, angle, false);
            previousAngle = angle;
        }

        if (t >= 1f)
        {
            swinging = false;
            visual.enabled = false;
            fx.Hide();
            cooldownLeft = s.cooldown;
        }
    }

    // ------------------------------------------------------------ spin attack

    private void StartSpin()
    {
        swingFacing = ReadFacing();
        facingAngle = FacingToAngle(swingFacing);

        swingTimer = 0f;
        previousAngle = facingAngle;   // starts where the player is facing and turns clockwise
        hitFoes.Clear();
        hitPlayers.Clear();
        bladeHitCount = 0;
        spinning = true;

        glowRenderer.enabled = false;
        StyleRenderer(visual, 1);
        visual.enabled = true;
        fx.Show(visual.sortingLayerID, visual.sortingOrder + 1);
        pivot.rotation = Quaternion.Euler(0f, 0f, previousAngle);
    }

    private void UpdateSpin(float dt)
    {
        SwordStats s = slots[0].stats;

        swingTimer += dt;
        float t = Mathf.Clamp01(swingTimer / Mathf.Max(0.05f, s.spinTime));
        float angle = facingAngle - 360f * t;
        pivot.rotation = Quaternion.Euler(0f, 0f, angle);

        HitSweep(s, previousAngle, angle, true);
        previousAngle = angle;

        if (t >= 1f)
        {
            spinning = false;
            visual.enabled = false;
            fx.Hide();
            cooldownLeft = s.cooldown;
        }
    }

    // ------------------------------------------------------------ facing

    // The player's Animator holds the facing: 0 = down, 1 = up, 2 = right, 3 = left.
    private int ReadFacing()
    {
        return animator != null ? animator.GetInteger("Direction") : 0;
    }

    private static float FacingToAngle(int facing)
    {
        switch (facing)
        {
            case 1: return 90f;
            case 2: return 0f;
            case 3: return 180f;
            default: return 270f;
        }
    }

    private static Vector2 AngleToVector(float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
    }

    // ------------------------------------------------------------ hitting foes with the sword

    // Hits every foe inside the sword's reach whose angle lies between where the blade was and where it is now
    // (plus a little leeway). Each foe is hit at most once per swing / spin.
    private void HitSweep(SwordStats s, float fromAngle, float toAngle, bool spin)
    {
        Vector2 origin = HandOnGround;   // not pivot.position: that rises with a jump, the hit range must not
        int count = Physics2D.OverlapCircle(origin, activeReach, foeFilter, overlapBuffer);
        float span = Mathf.DeltaAngle(fromAngle, toAngle);   // signed: negative = clockwise

        sweepOrigin = origin;
        sweepFrom = fromAngle;
        sweepSpan = span;
        if (sweepTest == null) sweepTest = PointInSweep;

        for (int i = 0; i < count; i++)
        {
            // Breakable objects (crates, rubble...): one touch breaks them, and they don't use up a Max Targets slot.
            DestructibleObject breakable = overlapBuffer[i].GetComponentInParent<DestructibleObject>();
            if (breakable != null)
            {
                breakable.HitByBlade(overlapBuffer[i], origin, activeReach, sweepTest);
                continue;
            }

            if (!spin && s.maxTargets > 0 && bladeHitCount >= s.maxTargets) continue;

            FoeController foe = overlapBuffer[i].GetComponentInParent<FoeController>();
            if (foe == null || foe.IsDead || hitFoes.Contains(foe)) continue;

            Vector2 toFoe = foe.Center - origin;
            if (toFoe.sqrMagnitude > 0.0001f)   // a foe standing exactly on the pivot counts as in the arc
            {
                float angle = Mathf.Atan2(toFoe.y, toFoe.x) * Mathf.Rad2Deg;
                if (!AngleInSweep(angle, fromAngle, span, AngleSlack + WidthSlack(toFoe.magnitude))) continue;
            }

            hitFoes.Add(foe);
            bladeHitCount++;
            BladeHit(foe, s, toFoe, spin);
        }

        // PVP: with PvpMode on, the same blade also hurts the OTHER player. Off = players can never hurt each other.
        if (PvpMode.Enabled) HitPlayersSweep(s, origin, fromAngle, span, spin);
    }

    // PVP. Same reach, same arc, same crit rules as for foes.
    private void HitPlayersSweep(SwordStats s, Vector2 origin, float fromAngle, float span, bool spin)
    {
        for (int i = 0; i < CoopHealth.Players.Count; i++)
        {
            CoopHealth other = CoopHealth.Players[i];
            if (other == null || !other.IsAlive || hitPlayers.Contains(other)) continue;
            if (!PvpMode.CanHurt(PlayerNumber, other.PlayerNumber)) continue;
            if (!spin && s.maxTargets > 0 && bladeHitCount >= s.maxTargets) continue;

            Collider2D bodyCollider = PvpColliderOf(other);
            Vector2 center = bodyCollider != null ? (Vector2)bodyCollider.bounds.center : (Vector2)other.transform.position;
            Vector2 nearest = bodyCollider != null ? bodyCollider.ClosestPoint(origin) : center;
            if (Vector2.Distance(origin, nearest) > activeReach) continue;

            Vector2 toPlayer = center - origin;
            if (toPlayer.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Atan2(toPlayer.y, toPlayer.x) * Mathf.Rad2Deg;
                if (!AngleInSweep(angle, fromAngle, span, AngleSlack + WidthSlack(toPlayer.magnitude))) continue;
            }

            hitPlayers.Add(other);
            bladeHitCount++;

            float baseDamage = s.damage * (spin ? s.spinDamageMultiplier : 1f);
            bool crit = Random.value < s.critChance;
            float damage = crit ? baseDamage * s.critMultiplier : baseDamage;

            Vector2 pushDirection = toPlayer.sqrMagnitude > 0.0001f ? toPlayer.normalized : AngleToVector(facingAngle);
            bool landed = PvpTryHurt(other, damage, pushDirection, s.knockback);
            if (crit) PlayCritSound();

            // Ice slows their walking (a hit that did no damage does none of this).
            if (landed && other.IsAlive && s.slowFactor < 1f)
            {
                TopDownCharacterController victimMover = FindOn<TopDownCharacterController>(other);
                if (victimMover != null) victimMover.ApplySlow(s.slowFactor, s.slowTime);
            }
        }
    }

    // The component on this object, or on a parent, or on a child (the same search Awake uses for the health).
    private static T FindOn<T>(Component source) where T : Component
    {
        T found = source.GetComponentInParent<T>();
        return found != null ? found : source.GetComponentInChildren<T>();
    }

    // The player's solid (non-trigger) collider, found once and remembered.
    internal Collider2D PvpColliderOf(CoopHealth player)
    {
        Collider2D found;
        if (pvpColliders.TryGetValue(player, out found) && found != null) return found;

        Collider2D[] all = player.GetComponentsInChildren<Collider2D>();
        found = null;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].enabled && !all[i].isTrigger) { found = all[i]; break; }
        }
        if (found == null && all.Length > 0) found = all[0];

        pvpColliders[player] = found;
        return found;
    }

    // Extra degrees of leeway for a thick blade. Capped, so a very close foe can't turn a thick blade into a 180-degree hit.
    private float WidthSlack(float distance)
    {
        if (activeHalfWidth <= 0f) return 0f;
        return Mathf.Min(45f, Mathf.Atan2(activeHalfWidth, Mathf.Max(distance, 0.1f)) * Mathf.Rad2Deg);
    }

    // True if this world point lies inside the part of the arc the blade is passing over right now (same leeway as foes get).
    private bool PointInSweep(Vector2 point)
    {
        Vector2 toPoint = point - sweepOrigin;
        if (toPoint.sqrMagnitude < 0.0001f) return true;   // exactly on the hand counts as in the arc

        float angle = Mathf.Atan2(toPoint.y, toPoint.x) * Mathf.Rad2Deg;
        return AngleInSweep(angle, sweepFrom, sweepSpan, AngleSlack + WidthSlack(toPoint.magnitude));
    }

    internal static bool AngleInSweep(float angle, float fromAngle, float span, float slack)
    {
        float relative = Mathf.DeltaAngle(fromAngle, angle);   // -180..180 from where the sweep started
        if (span >= 0f) return relative >= -slack && relative <= span + slack;
        return relative <= slack && relative >= span - slack;
    }

    // One foe touched by the blade: roll the crit, hit it, and apply the ice.
    private void BladeHit(FoeController foe, SwordStats s, Vector2 toFoe, bool spin)
    {
        float baseDamage = s.damage * (spin ? s.spinDamageMultiplier : 1f);
        bool crit = Random.value < s.critChance;
        float damage = crit ? baseDamage * s.critMultiplier : baseDamage;

        Vector2 pushDirection = toFoe.sqrMagnitude > 0.0001f ? toFoe.normalized : AngleToVector(facingAngle);
        ApplyHit(foe, s, damage, pushDirection, crit);
    }

    // Everything one hit does to a foe. The foe handles the pause, the death and the score itself.
    private void ApplyHit(FoeController foe, SwordStats s, float damage, Vector2 pushDirection, bool crit)
    {
        CoopScore.CreditedPlayer = PlayerNumber;   // a foe that dies from this hit gives its score to THIS player
        foe.TakeDamage(damage);
        CoopScore.CreditedPlayer = 0;

        if (s.lifeSteal > 0f) PlayerHealthAccess.Heal(playerHealth, damage * s.lifeSteal);

        if (crit)
        {
            foe.FlashWhite();
            PlayCritSound();
        }
        if (s.knockback > 0f) foe.Knockback(pushDirection, s.knockback);
        if (s.slowFactor < 1f) foe.ApplySlow(s.slowFactor, s.slowTime);
        if (s.freezeChance > 0f && Random.value < s.freezeChance) foe.Freeze(s.freezeTime);

        if (debugLog)
        {
            Debug.Log(s.swordName + " hit " + foe.name + " for " + damage.ToString("F1") + (crit ? " (CRIT)" : ""));
        }
    }

    private void PlayCritSound()
    {
        if (sfxSource == null || critClip == null) return;
        // Respects the Sound On/Off menu: the SoundSwitch on the AudioSource's object handles the source itself,
        // and the SoundOn check here keeps it silent even if that source is still switched on.
        if (!GameSettings.SoundOn) return;
        if (!sfxSource.isActiveAndEnabled) return;
        sfxSource.PlayOneShot(critClip);
    }

    // ------------------------------------------------------------ collecting (unlocks)

    // Does this player own this slot? With Shared Weapons ON, a slot anybody owns counts.
    private static bool Owns(int player, string slotName)
    {
        if (!CoopRules.SharedWeapons) return owned[Slot(player)].Contains(slotName);

        for (int p = 1; p <= MaxPlayers; p++)
        {
            if (owned[p].Contains(slotName)) return true;
        }
        return false;
    }

    // Gives the slot to this player (to EVERY player with Shared Weapons ON). True if it was new for them.
    private static bool Give(int player, string slotName)
    {
        bool isNew = !Owns(player, slotName);

        if (CoopRules.SharedWeapons)
        {
            for (int p = 1; p <= MaxPlayers; p++) owned[p].Add(slotName);
        }
        else
        {
            owned[Slot(player)].Add(slotName);
        }
        return isNew;
    }

    private static bool AnyPoolReady()
    {
        for (int p = 1; p <= MaxPlayers; p++)
        {
            if (poolReady[p]) return true;
        }
        return false;
    }

    private bool StartsUnlocked(ToolSlot slot)
    {
        return slot.kind == ToolKind.Sword ? frostSword.unlockedAtStart : slot.settings.unlockedAtStart;
    }

    // Fills in this player's starting unlocks the first time their SwordController wakes up, then keeps them between scenes.
    private void InitUnlocks()
    {
        int p = Slot(PlayerNumber);

        // Names first, so every slot has the name the unlock list uses.
        RefreshNames();

        startUnlocked[p].Clear();
        for (int i = 0; i < slots.Count; i++)
        {
            if (StartsUnlocked(slots[i])) startUnlocked[p].Add(slots[i].name);
        }

        if (!poolReady[p])
        {
            poolReady[p] = true;
            for (int i = 0; i < startUnlocked[p].Count; i++) owned[p].Add(startUnlocked[p][i]);
        }

        // The starting slot is always owned, so the player is never empty-handed.
        startingNames[p] = slots[Mathf.Clamp(startingSword, 0, slots.Count - 1)].name;
        owned[p].Add(startingNames[p]);

        if (debugUnlockAllSwords)
        {
            debugUnlockApplied = true;
            for (int i = 0; i < slots.Count; i++) Give(PlayerNumber, slots[i].name);
        }
    }

    private void RefreshNames()
    {
        for (int i = 0; i < slots.Count; i++)
        {
            slots[i].name = slots[i].kind == ToolKind.Sword ? frostSword.swordName : slots[i].settings.toolName;
        }
    }

    /// <summary>True if this player owns the slot at this position in the list.</summary>
    public bool IsUnlocked(int index)
    {
        return index >= 0 && index < slots.Count && Owns(PlayerNumber, slots[index].name);
    }

    /// <summary>Gives this player the slot at this position. Returns true if it was new. Call it from a pickup.</summary>
    public bool UnlockSword(int index)
    {
        if (index < 0 || index >= slots.Count) return false;
        if (!Give(PlayerNumber, slots[index].name)) return false;

        if (debugLog) Debug.Log("Player " + PlayerNumber + " unlocked " + slots[index].name);
        SwordUnlocked?.Invoke(slots[index].stats);
        return true;
    }

    /// <summary>Gives this player the slot with this name. Returns true if it was new.</summary>
    public bool UnlockSwordByName(string slotName)
    {
        int index = IndexOf(slotName);
        return index >= 0 && UnlockSword(index);
    }

    /// <summary>Gives this player every slot (this is what the debug box does).</summary>
    [ContextMenu("Debug: unlock all swords and tools")]
    public void UnlockAllSwords()
    {
        for (int i = 0; i < slots.Count; i++) UnlockSword(i);
    }

    /// <summary>
    /// True if ANY player owns the slot with this name. Also true while nothing has been set up yet (for example when the
    /// Shop scene is played on its own), so a shop never shows an empty list just because no gameplay scene ran first.
    /// </summary>
    public static bool IsUnlockedByName(string slotName)
    {
        if (!AnyPoolReady()) return true;
        for (int p = 1; p <= MaxPlayers; p++)
        {
            if (owned[p].Contains(slotName)) return true;
        }
        return false;
    }

    /// <summary>True if this player owns the slot with this name (with the same "nothing set up yet" rule).</summary>
    public static bool IsUnlockedByName(int playerNumber, string slotName)
    {
        return !AnyPoolReady() || Owns(playerNumber, slotName);
    }

    /// <summary>Unlocks a slot by name for EVERY player, from any script or scene (no SwordController needed).</summary>
    public static void UnlockByName(string slotName)
    {
        for (int p = 1; p <= MaxPlayers; p++) owned[p].Add(slotName);
    }

    /// <summary>Unlocks a slot by name for one player (everybody with Shared Weapons ON).</summary>
    public static void UnlockByName(int playerNumber, string slotName)
    {
        Give(playerNumber, slotName);
    }

    /// <summary>
    /// New run (Retry, Quit to Main Menu): every player owns the slots that are ticked "Unlocked At Start" plus their starting slot again,
    /// and is holding the starting slot. Everything else is removed until a pickup or the shop gives it back. Works from any scene (GameReset calls it).
    /// Upgrade bonuses are NOT touched: see ResetProgress / ClearUpgradeBonuses for those.
    /// </summary>
    public static void ResetToStartingSword()
    {
        bool anySetUp = false;
        for (int p = 1; p <= MaxPlayers; p++)
        {
            if (startingNames[p] != null) anySetUp = true;
        }
        if (!anySetUp) return;   // no SwordController has woken up yet this session: nothing to reset

        for (int p = 1; p <= MaxPlayers; p++)
        {
            owned[p].Clear();
            if (startingNames[p] != null)
            {
                owned[p].Add(startingNames[p]);
                for (int i = 0; i < startUnlocked[p].Count; i++) owned[p].Add(startUnlocked[p][i]);
                poolReady[p] = true;
            }
        }

        // A player that is kept between scenes still holds its old slot: put the starting slot back in its hand.
        SwordController[] players = FindObjectsByType<SwordController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++) players[i].EquipStartingSword();
    }

    // Puts the starting slot in hand and drops whatever was going on (a swing frozen by the pause menu, a half-charged spin, a tool action).
    private void EquipStartingSword()
    {
        if (slots.Count == 0 || visualTransform == null) return;

        // A pause holds the sparks still by hand. Let them go first, in the same order a normal resume would.
        if (fx != null && fxFrozen)
        {
            fxFrozen = false;
            fx.SetPaused(false);
        }

        CancelAttack();
        cooldownLeft = 0f;

        int previous = currentIndex;
        int target = Mathf.Clamp(startingSword, 0, slots.Count - 1);
        if (previous != target)
        {
            if (slots[previous].tool != null) slots[previous].tool.OnUnequipped();
            currentIndex = target;
            if (slots[currentIndex].tool != null) slots[currentIndex].tool.OnEquipped();
        }

        ApplyVisual(slots[0].stats);
        if (currentIndex != previous) SwordChanged?.Invoke(slots[currentIndex].stats);
    }

    // ------------------------------------------------------------ upgrades

    /// <summary>The bonus bought for one slot (from SwordShop), for EVERY player. Replaces that slot's earlier bonus.</summary>
    public static void SetUpgradeBonus(string slotName, SwordBonus bonus)
    {
        for (int p = 1; p <= MaxPlayers; p++) swordBonuses[p][slotName] = bonus;
        UpgradesChanged?.Invoke();
    }

    /// <summary>The bonus bought for one slot by ONE player (everybody with Shared Weapons ON).</summary>
    public static void SetUpgradeBonus(int playerNumber, string slotName, SwordBonus bonus)
    {
        if (CoopRules.SharedWeapons)
        {
            SetUpgradeBonus(slotName, bonus);
            return;
        }

        swordBonuses[Slot(playerNumber)][slotName] = bonus;
        UpgradesChanged?.Invoke();
    }

    /// <summary>Player 1's bonus for this slot (what the old single-player shop read).</summary>
    public static SwordBonus GetUpgradeBonus(string slotName)
    {
        return GetUpgradeBonus(1, slotName);
    }

    public static SwordBonus GetUpgradeBonus(int playerNumber, string slotName)
    {
        SwordBonus bonus;
        return swordBonuses[Slot(playerNumber)].TryGetValue(slotName, out bonus) ? bonus : default(SwordBonus);
    }

    /// <summary>Removes every slot's bonus, for every player (used when the upgrades are reset).</summary>
    public static void ClearUpgradeBonuses()
    {
        for (int p = 0; p <= MaxPlayers; p++) swordBonuses[p].Clear();
        UpgradesChanged?.Invoke();
    }

    /// <summary>The bonus that applies to EVERY slot of EVERY player (from PlayerUpgrades: damage and attack speed).</summary>
    public static void SetPlayerBonus(SwordBonus bonus)
    {
        for (int p = 1; p <= MaxPlayers; p++) playerBonuses[p] = bonus;
        UpgradesChanged?.Invoke();
    }

    /// <summary>The bonus that applies to every slot of ONE player (everybody with Shared Weapons ON).</summary>
    public static void SetPlayerBonus(int playerNumber, SwordBonus bonus)
    {
        if (CoopRules.SharedWeapons)
        {
            SetPlayerBonus(bonus);
            return;
        }

        playerBonuses[Slot(playerNumber)] = bonus;
        UpgradesChanged?.Invoke();
    }

    /// <summary>New game: forget unlocks (back to each slot's Unlocked At Start) and all upgrade bonuses, for every player.</summary>
    public static void ResetProgress()
    {
        for (int p = 0; p <= MaxPlayers; p++)
        {
            owned[p].Clear();
            poolReady[p] = false;
            swordBonuses[p].Clear();
            playerBonuses[p] = default(SwordBonus);
        }
        UpgradesChanged?.Invoke();
    }

    // Statics outlive Play mode when Domain Reload is switched off in the editor, so clear them on every start.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        for (int p = 0; p <= MaxPlayers; p++)
        {
            owned[p].Clear();
            poolReady[p] = false;
            startingNames[p] = null;
            startUnlocked[p].Clear();
            swordBonuses[p].Clear();
            playerBonuses[p] = default(SwordBonus);
        }
        All.Clear();
        UpgradesChanged = null;
        whiteSquare = null;
        glowSprite = null;
    }

    // Builds the numbers each slot really uses: a copy of the Inspector sword with this player's shop bonuses, player bonus and timed
    // power-ups on top, and a small stand-in record for each tool (name, picture, colour) for HUDs and the shop.
    private void RebuildEffective()
    {
        if (slots.Count == 0) return;

        RefreshNames();
        for (int i = 0; i < slots.Count; i++)
        {
            ToolSlot slot = slots[i];
            if (slot.kind == ToolKind.Sword)
            {
                slot.stats = BuildEffective(frostSword, PlayerNumber, timedBonus);
            }
            else
            {
                SwordStats stub = new SwordStats();
                stub.swordName = slot.settings.toolName;
                stub.unlockedAtStart = slot.settings.unlockedAtStart;
                stub.sprite = slot.settings.icon;
                stub.placeholderColor = slot.settings.color;
                stub.glowColor = slot.settings.color;
                stub.showBladeParticles = false;
                stub.particleAmount = 0f;
                slot.stats = stub;
            }
        }

        if (visualTransform != null)
        {
            currentIndex = Mathf.Clamp(currentIndex, 0, slots.Count - 1);
            ApplyVisual(slots[0].stats);
        }
    }

    private static SwordStats BuildEffective(SwordStats baseStats, int player, SwordBonus timed)
    {
        SwordStats e = baseStats.Clone();
        SwordBonus b = GetUpgradeBonus(player, baseStats.swordName) + playerBonuses[Slot(player)] + timed;

        e.damage = baseStats.damage * (1f + b.damagePercent);
        e.critChance = Mathf.Clamp01(baseStats.critChance + b.critChanceAdd);
        e.freezeChance = Mathf.Clamp01(baseStats.freezeChance + b.freezeChanceAdd);
        e.lifeSteal = Mathf.Clamp01(baseStats.lifeSteal + b.lifeStealAdd);

        // Faster attacking: swing, spin and the wait between swings all shrink. (The spin CHARGE time is left alone.)
        float speed = 1f + Mathf.Max(-0.9f, b.attackSpeedPercent);
        e.swingTime = Mathf.Max(0.05f, baseStats.swingTime / speed);
        e.spinTime = Mathf.Max(0.1f, baseStats.spinTime / speed);
        e.cooldown = baseStats.cooldown / speed;
        return e;
    }

    // ------------------------------------------------------------ editor helper

    // Select the player in the Scene view to see the Frost Sword's reach as a yellow circle.
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position + (Vector3)pivotOffset, ReachOf(frostSword));
    }
}
