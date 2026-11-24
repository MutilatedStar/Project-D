using System.Collections.Generic;
using Ilumisoft.HealthSystem;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// MAGIC BOOTS (built by SwordController; nothing to add by hand).
///
///   Hover   while the boots are in hand the player floats a little (a glow under the feet). SwordController.IsHovering tells a floor trap
///           to skip a hovering player if you want that.
///   Dash    tap the attack button: a quick dash the way the player is moving (or facing). It hurts every foe it passes through.
///           The boots hold 2 dashes; each one comes back after the Cooldown, one at a time.
///   Super   hold the attack button: one super long dash that hits harder. It uses all the dash charges.
///   Buff    every dash gives a short speed buff.
///   Climb   with the boots in hand, walk into a wall tile whose NAME is in Climbable Tile Names and the player climbs up it:
///           it stops being solid for the player for as long as they are on those tiles (every other wall still blocks).
/// </summary>
public class MagicBootsTool : PlayerTool
{
    [System.Serializable]
    public class Settings : ToolSettings
    {
        public Settings()
        {
            toolName = "Magic Boots";
            shortLabel = "BOOT";
            color = new Color(0.45f, 1f, 0.7f);
        }

        [Header("Dash (tap)")]
        [Tooltip("Seconds for ONE used dash to come back. Both dashes used = twice this to get both back.")]
        [Min(0.1f)] public float cooldown = 3f;
        [Tooltip("How many dashes the boots hold.")]
        [Range(1, 4)] public int dashCharges = 2;
        [Tooltip("How far a dash goes, in Unity units.")]
        [Min(0.5f)] public float dashDistance = 3.2f;
        [Tooltip("Seconds a dash takes.")]
        [Min(0.05f)] public float dashTime = 0.16f;
        [Tooltip("Damage to every foe the dash passes through (once per foe).")]
        [Min(0f)] public float dashDamage = 10f;
        [Tooltip("How far a dash shoves a foe, in Unity units.")]
        [Min(0f)] public float dashKnockback = 1.2f;
        [Tooltip("Seconds after a dash before the next one can start.")]
        [Min(0f)] public float dashGap = 0.12f;
        [Tooltip("How close (Unity units) a foe has to be to the player to be hit by the dash.")]
        [Min(0.2f)] public float hitRadius = 0.7f;

        [Header("Super dash (hold)")]
        [Tooltip("Seconds the attack button must be held for the super dash.")]
        [Min(0.1f)] public float superHoldTime = 0.6f;
        [Min(0.5f)] public float superDashDistance = 8f;
        [Min(0.05f)] public float superDashTime = 0.35f;
        [Min(0f)] public float superDashDamage = 25f;
        [Min(0f)] public float superDashKnockback = 2.5f;
        [Tooltip("Dash charges the super dash uses. If it is as many as the boots hold, it uses them all.")]
        [Range(1, 4)] public int superDashCost = 2;

        [Header("Speed buff (after a dash)")]
        [Tooltip("1.35 = 35% faster walking.")]
        [Min(1f)] public float speedBuffMultiplier = 1.35f;
        [Tooltip("Seconds the buff lasts after a normal dash.")]
        [Min(0f)] public float speedBuffSeconds = 2.5f;
        [Tooltip("Seconds the buff lasts after a super dash.")]
        [Min(0f)] public float superSpeedBuffSeconds = 4f;

        [Header("Hover")]
        public bool hover = true;
        [Tooltip("Where the glow sits, relative to the player's position (the feet).")]
        public Vector2 hoverGlowOffset = new Vector2(0f, 0f);
        [Min(0.3f)] public float hoverGlowSize = 1.1f;

        [Header("Climbing")]
        public bool canClimb = true;
        [Tooltip("Names of the wall tiles the player can climb (the name of the Tile asset). Not case sensitive.")]
        public string[] climbableTileNames = { "Climbable" };
        [Tooltip("ON: a tile counts when its name CONTAINS one of the names above. OFF: the name must match exactly.")]
        public bool partialNameMatch = true;
        [Tooltip("The player walks at this fraction of their speed while climbing.")]
        [Range(0.2f, 1f)] public float climbSpeedFactor = 0.7f;
        [Tooltip("How far in front of the player (Unity units) to look for a wall to climb.")]
        [Min(0.2f)] public float climbProbeDistance = 0.6f;
    }

    private readonly Settings s;

    private int charges;
    private float rechargeTimer;
    private float gapTimer;
    private bool equipped;

    private bool holding;
    private bool superFired;
    private float holdTime;

    private bool dashing;
    private Vector2 dashDir;
    private float dashLeft;
    private float dashSpeed;
    private float dashDmg;
    private float dashKnock;
    private readonly HashSet<FoeController> hitFoes = new HashSet<FoeController>();
    private readonly HashSet<CoopHealth> hitPlayers = new HashSet<CoopHealth>();
    private readonly List<Collider2D> ignoredFoeColliders = new List<Collider2D>();
    private readonly RaycastHit2D[] castBuffer = new RaycastHit2D[16];
    private readonly Collider2D[] overlapBuffer = new Collider2D[24];
    private readonly System.Func<Collider2D, bool> ignoreFoes;

    private float buffTimer;
    private float buffTotal;

    private SpriteRenderer aura;

    private readonly List<Tilemap> maps = new List<Tilemap>();
    private readonly Dictionary<Tilemap, bool> mapSolid = new Dictionary<Tilemap, bool>();
    private readonly List<Collider2D> climbIgnored = new List<Collider2D>();
    private readonly Vector2[] samples = new Vector2[5];
    private readonly object climbKey = new object();
    private bool climbing;
    private Vector2 lastSafe;
    private float exitTimer;
    private float mapTimer;

    public MagicBootsTool(SwordController owner, Settings settings) : base(owner, settings)
    {
        s = settings;
        charges = s.dashCharges;
        ignoreFoes = IsFoeCollider;
    }

    public override ToolKind Kind { get { return ToolKind.MagicBoots; } }

    /// <summary>True while the boots make the player hover (in hand, or a dash / speed buff still running).</summary>
    public bool Hovering { get { return s.hover && (equipped || dashing || buffTimer > 0f); } }

    public override bool IsBusy { get { return dashing; } }

    public override int PipCount { get { return charges; } }
    public override int PipMax { get { return s.dashCharges; } }

    public override float CooldownFraction
    {
        get
        {
            if (charges > 0) return 0f;
            float need = Mathf.Max(0.05f, Cooldown(s.cooldown));
            return Mathf.Clamp01(1f - rechargeTimer / need);
        }
    }

    public override float ChargeFraction
    {
        get { return (holding && !superFired) ? Mathf.Clamp01(holdTime / Mathf.Max(0.01f, s.superHoldTime)) : 0f; }
    }

    public override float BuffFraction { get { return buffTotal > 0f ? Mathf.Clamp01(buffTimer / buffTotal) : 0f; } }
    public override string BuffLabel { get { return "SPD"; } }

    // ------------------------------------------------------------ every frame

    public override void TickAlways(float dt)
    {
        if (charges > s.dashCharges) charges = s.dashCharges;

        if (charges < s.dashCharges)
        {
            rechargeTimer += dt;
            if (rechargeTimer >= Mathf.Max(0.05f, Cooldown(s.cooldown)))
            {
                charges++;
                rechargeTimer = 0f;
            }
        }
        else
        {
            rechargeTimer = 0f;
        }

        if (gapTimer > 0f) gapTimer -= dt;

        if (buffTimer > 0f)
        {
            buffTimer -= dt;
            if (buffTimer <= 0f) EndBuff();
        }
    }

    public override void OnEquipped()
    {
        equipped = true;
        RefreshMaps();
        mapTimer = 3f;
    }

    public override void OnUnequipped()
    {
        equipped = false;
        holding = false;
    }

    public override void TickEquipped(float dt)
    {
        if (dashing)
        {
            holding = false;
            return;
        }

        if (owner.AttackPressedNow && gapTimer <= 0f && charges > 0)
        {
            holding = true;
            holdTime = 0f;
            superFired = false;
        }

        if (!holding) return;

        if (owner.AttackHeldNow)
        {
            holdTime += dt;
            if (!superFired && holdTime >= s.superHoldTime && charges >= SuperCost)
            {
                superFired = true;
                StartDash(true);
            }
            return;
        }

        // Let go before the super dash was ready: a normal dash.
        if (!superFired) StartDash(false);
        holding = false;
    }

    public override void Cancel()
    {
        if (dashing) EndDash();
        holding = false;
        if (buffTimer > 0f || buffTotal > 0f) EndBuff();
        if (climbing) EndClimb();
        if (aura != null && aura.enabled) aura.enabled = false;
    }

    public override void DestroyVisuals()
    {
        Cancel();
        if (aura != null) Object.Destroy(aura.gameObject);
    }

    private int SuperCost { get { return Mathf.Clamp(s.superDashCost, 1, Mathf.Max(1, s.dashCharges)); } }

    // ------------------------------------------------------------ dashing

    private void StartDash(bool super)
    {
        int cost = super ? SuperCost : 1;
        if (charges < cost || dashing) return;

        charges -= cost;
        rechargeTimer = 0f;

        Vector2 dir = owner.LastMoveDirection;
        if (dir.sqrMagnitude < 0.01f) dir = owner.FacingVector;
        dashDir = dir.normalized;

        float distance = super ? s.superDashDistance : s.dashDistance;
        float time = Mathf.Max(0.05f, super ? s.superDashTime : s.dashTime);
        dashLeft = distance;
        dashSpeed = distance / time;
        dashDmg = Damage(super ? s.superDashDamage : s.dashDamage);
        dashKnock = super ? s.superDashKnockback : s.dashKnockback;

        hitFoes.Clear();
        hitPlayers.Clear();
        dashing = true;
        owner.LockMovement(this);   // the walking script is switched off for the dash, so it can't fight it
        StartBuff(super);
    }

    private void EndDash()
    {
        dashing = false;
        gapTimer = s.dashGap;
        owner.UnlockMovement(this);
        RestoreFoeCollisions();
        ToolPhysics.SetVelocity(owner.Rb, Vector2.zero);
    }

    public override void FixedTickAlways(float dt)
    {
        if (dashing) DashStep(dt);
        ClimbStep(dt);
    }

    private void DashStep(float dt)
    {
        Rigidbody2D rb = owner.Rb;
        float step = Mathf.Min(dashSpeed * dt, dashLeft);
        Vector2 pos = rb != null ? rb.position : (Vector2)owner.transform.position;

        IgnoreFoeColliders(pos);

        float free = step;
        if (rb != null) free = ToolPhysics.FreeDistance(rb, dashDir, step, owner.SolidFilter, castBuffer, ignoreFoes);

        Vector2 next = pos + dashDir * free;
        if (free > 0.0005f)
        {
            if (rb != null) rb.MovePosition(next);
            else owner.transform.position = new Vector3(next.x, next.y, owner.transform.position.z);
        }

        dashLeft -= free;
        HitAlongDash(next);
        SpawnAfterImage(pos);

        if (free < step - 0.001f || dashLeft <= 0.001f) EndDash();
    }

    private void HitAlongDash(Vector2 center)
    {
        int count = Physics2D.OverlapCircle(center, s.hitRadius, owner.FoeFilter, overlapBuffer);
        for (int i = 0; i < count; i++)
        {
            FoeController foe = overlapBuffer[i].GetComponentInParent<FoeController>();
            if (foe == null || foe.IsDead || !hitFoes.Add(foe)) continue;
            owner.DamageFoe(foe, dashDmg, dashDir, dashKnock);
        }

        owner.PvpHitCircle(center, s.hitRadius, hitPlayers, dashDmg, dashKnock, dashDir);
    }

    // A dash goes THROUGH foes (it hurts them on the way), so their solid colliders are switched off against the player until the dash ends.
    private void IgnoreFoeColliders(Vector2 pos)
    {
        int count = Physics2D.OverlapCircle(pos, s.hitRadius + 0.6f, owner.FoeFilter, overlapBuffer);
        for (int i = 0; i < count; i++)
        {
            Collider2D c = overlapBuffer[i];
            if (c == null || ignoredFoeColliders.Contains(c)) continue;
            if (!IsFoeCollider(c)) continue;

            SetIgnoreWithPlayer(c, true);
            ignoredFoeColliders.Add(c);
        }
    }

    private void RestoreFoeCollisions()
    {
        for (int i = 0; i < ignoredFoeColliders.Count; i++)
        {
            if (ignoredFoeColliders[i] != null) SetIgnoreWithPlayer(ignoredFoeColliders[i], false);
        }
        ignoredFoeColliders.Clear();
    }

    private static bool IsFoeCollider(Collider2D c)
    {
        return c != null && c.GetComponentInParent<FoeController>() != null;
    }

    private void SetIgnoreWithPlayer(Collider2D other, bool ignore)
    {
        Collider2D[] mine = owner.PlayerColliders;
        for (int i = 0; i < mine.Length; i++)
        {
            if (mine[i] != null) Physics2D.IgnoreCollision(mine[i], other, ignore);
        }
    }

    // Little fading squares along the dash.
    private void SpawnAfterImage(Vector2 position)
    {
        GameObject go = new GameObject("BootsTrail");
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = SwordController.WhiteSquare;
        Color c = s.color;
        c.a = 0.55f;
        sr.color = c;
        sr.sortingLayerID = owner.SortingLayerId;
        sr.sortingOrder = owner.SortingOrder - 1;
        go.transform.position = new Vector3(position.x, position.y + 0.25f, owner.transform.position.z);
        go.transform.rotation = Quaternion.Euler(0f, 0f, 45f);
        go.transform.localScale = new Vector3(0.28f, 0.28f, 1f);
        Object.Destroy(go, 0.2f);
    }

    // ------------------------------------------------------------ the speed buff

    private void StartBuff(bool super)
    {
        buffTotal = super ? s.superSpeedBuffSeconds : s.speedBuffSeconds;
        buffTimer = buffTotal;
        if (buffTotal > 0f) owner.SetSpeedMultiplier(this, s.speedBuffMultiplier);
    }

    private void EndBuff()
    {
        buffTimer = 0f;
        buffTotal = 0f;
        owner.ClearSpeedMultiplier(this);
    }

    // ------------------------------------------------------------ hover glow

    public override void LateTickAlways()
    {
        bool show = Hovering && owner.isActiveAndEnabled;
        if (!show)
        {
            if (aura != null && aura.enabled) aura.enabled = false;
            return;
        }

        if (aura == null)
        {
            aura = ToolDraw.NewSquare("BootsHover", owner.transform);
            aura.sprite = SwordController.SoftCircle;
        }

        float bob = Mathf.Sin(Time.time * 4f);
        Color c = s.color;
        c.a = (dashing || climbing ? 0.55f : 0.3f) + 0.08f * bob;
        aura.color = c;
        aura.enabled = true;
        ToolDraw.Sort(aura, owner.SortingLayerId, owner.SortingOrder - 2);

        float size = s.hoverGlowSize * (1f + 0.06f * bob);
        aura.transform.localPosition = new Vector3(s.hoverGlowOffset.x, s.hoverGlowOffset.y + 0.03f * bob, 0f);
        aura.transform.localScale = new Vector3(size, size * 0.6f, 1f);
    }

    // ------------------------------------------------------------ climbing

    private void RefreshMaps()
    {
        maps.Clear();
        mapSolid.Clear();
        Tilemap[] all = Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++) maps.Add(all[i]);
    }

    private bool NameIsClimbable(string tileName)
    {
        if (s.climbableTileNames == null || string.IsNullOrEmpty(tileName)) return false;
        for (int i = 0; i < s.climbableTileNames.Length; i++)
        {
            string wanted = s.climbableTileNames[i];
            if (string.IsNullOrEmpty(wanted)) continue;

            if (s.partialNameMatch)
            {
                if (tileName.IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            else if (string.Equals(tileName, wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool MapHasClimbableAt(Tilemap map, Vector2 point)
    {
        Vector3Int cell = map.WorldToCell(new Vector3(point.x, point.y, map.transform.position.z));
        TileBase tile = map.GetTile(cell);
        return tile != null && NameIsClimbable(tile.name);
    }

    private bool AnyClimbableAt(Vector2 point)
    {
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i] != null && MapHasClimbableAt(maps[i], point)) return true;
        }
        return false;
    }

    private bool MapIsSolid(Tilemap map)
    {
        bool solid;
        if (mapSolid.TryGetValue(map, out solid)) return solid;

        solid = false;
        Collider2D[] cols = map.GetComponents<Collider2D>();
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i] != null && cols[i].enabled && !cols[i].isTrigger) solid = true;
        }
        mapSolid[map] = solid;
        return solid;
    }

    // A solid wall tile that is NOT climbable at this point.
    private bool SolidWallAt(Vector2 point)
    {
        for (int i = 0; i < maps.Count; i++)
        {
            Tilemap map = maps[i];
            if (map == null || !MapIsSolid(map)) continue;

            Vector3Int cell = map.WorldToCell(new Vector3(point.x, point.y, map.transform.position.z));
            TileBase tile = map.GetTile(cell);
            if (tile == null) continue;
            if (map.GetColliderType(cell) == Tile.ColliderType.None) continue;
            if (!NameIsClimbable(tile.name)) return true;
        }
        return false;
    }

    private void FillSamples(Vector2 pos)
    {
        Collider2D[] mine = owner.PlayerColliders;
        Bounds b = mine.Length > 0 && mine[0] != null ? mine[0].bounds : new Bounds(pos, new Vector3(0.4f, 0.4f, 0f));
        Vector2 c = b.center;
        Vector2 e = new Vector2(b.extents.x * 0.8f, b.extents.y * 0.8f);
        samples[0] = c;
        samples[1] = c + new Vector2(e.x, e.y);
        samples[2] = c + new Vector2(-e.x, e.y);
        samples[3] = c + new Vector2(e.x, -e.y);
        samples[4] = c + new Vector2(-e.x, -e.y);
    }

    private void ClimbStep(float dt)
    {
        if (!equipped && !climbing) return;
        if (!s.canClimb && !climbing) return;

        Rigidbody2D rb = owner.Rb;
        if (rb == null) return;
        Vector2 pos = rb.position;

        mapTimer -= dt;
        if (mapTimer <= 0f)
        {
            mapTimer = 3f;
            RefreshMaps();
        }

        Vector2 ahead = pos + owner.FacingVector * s.climbProbeDistance;

        if (!climbing)
        {
            if (dashing || !owner.WantsToMove) return;
            if (AnyClimbableAt(ahead)) StartClimb(ahead, pos);
            return;
        }

        // Climbing: every other wall still blocks. If the body got into one, go back to the last good spot.
        FillSamples(pos);
        bool blocked = false;
        bool onWall = false;
        for (int i = 0; i < samples.Length; i++)
        {
            if (SolidWallAt(samples[i])) blocked = true;
            if (AnyClimbableAt(samples[i])) onWall = true;
        }

        if (blocked)
        {
            rb.position = lastSafe;
            ToolPhysics.SetVelocity(rb, Vector2.zero);
        }
        else
        {
            lastSafe = pos;
        }

        bool goingOn = equipped && owner.WantsToMove && AnyClimbableAt(ahead);
        if (onWall || goingOn)
        {
            exitTimer = 0f;
        }
        else
        {
            exitTimer += dt;
            if (exitTimer >= 0.12f) EndClimb();
        }
    }

    private void StartClimb(Vector2 probe, Vector2 pos)
    {
        climbIgnored.Clear();
        for (int i = 0; i < maps.Count; i++)
        {
            Tilemap map = maps[i];
            if (map == null || !MapHasClimbableAt(map, probe)) continue;

            Collider2D[] cols = map.GetComponents<Collider2D>();
            for (int c = 0; c < cols.Length; c++)
            {
                if (cols[c] == null) continue;
                SetIgnoreWithPlayer(cols[c], true);
                climbIgnored.Add(cols[c]);
            }
        }

        if (climbIgnored.Count == 0) return;

        climbing = true;
        lastSafe = pos;
        exitTimer = 0f;
        owner.SetSpeedMultiplier(climbKey, s.climbSpeedFactor);
    }

    private void EndClimb()
    {
        for (int i = 0; i < climbIgnored.Count; i++)
        {
            if (climbIgnored[i] != null) SetIgnoreWithPlayer(climbIgnored[i], false);
        }
        climbIgnored.Clear();
        climbing = false;
        owner.ClearSpeedMultiplier(climbKey);
    }
}
