using System;
using System.Collections.Generic;
using Ilumisoft.HealthSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cainos.PixelArtTopDown_Basic
{
    /// <summary>
    /// Makes ONE thing (a foe, a crate, an item) fall down the levels of the TopDownLevelWorld. It is the same fall the player's TopDownJump does, for everything else.
    /// You do not have to add it: TopDownLevelWorld adds it to whatever should fall (see the world's Mode), and a TopDownLevelToggle set to Yes does too.
    /// Adding it by hand works as well. It has no settings of its own: the numbers come from the world, the rule that matches, and a toggle on the object.
    ///
    /// LEVEL   Read once from the object's physics layer (then its sprite's sorting layer) against the world's levels. Something that is not on the ladder at all
    ///         counts as standing on the ground, and keeps the layers it started on. If something else moves it between levels (stairs, a script that sets
    ///         the layer) it follows. SetLevel moves it to a level at once, without a fall.
    ///
    /// WHEN IT FALLS
    ///         * On a raised level and not over what holds that level up (the world's Surface for that level). It drops to the highest level that DOES hold it
    ///           (the ground always does). A level with an empty Surface counts as solid everywhere, so nothing falls off it on its own.
    ///         * FallTo / Drop from code, a TopDownDropZone, or TopDownLevelWorld.DropAllTo.
    ///
    /// THE FALL  It drops level by level: the physics layer and the sorting layer step down with it, a shadow sits on the level it will land on and grows as the ground
    ///         gets closer, nothing can hurt it in the air (a CoopHealth is switched to Airborne; hit boxes on the toggle are switched off), and the scripts listed on
    ///         the toggle are paused. If the spot below is taken by something solid, the fall waits (Blocked Landing Seconds), then comes down anyway.
    ///         Landing can hurt (Fall Damage in the settings).
    ///         Unlike the player's jump, the sprite is not drawn above everything while it falls: it simply takes the sorting layers of the levels it passes.
    ///
    /// Needs the Unity physics only for its colliders: a Rigidbody2D is NOT required, so an item with just a collider (or nothing) works too.
    /// </summary>
    [DisallowMultipleComponent]
    public class TopDownFaller : MonoBehaviour, ILevelFaller
    {
        [Header("Debug")]
        [Tooltip("Prints a Console line when this object starts a fall and when it lands.")]
        [SerializeField] private bool logFalls = false;

        // ------------------------------------------------------------ what other scripts can use

        /// <summary>0 on the ground, 1 on the first raised level, 2 on the second...</summary>
        public int Level { get { return appliedLevel; } }

        /// <summary>True from the moment a fall begins until it has landed.</summary>
        public bool IsFalling { get { return falling; } }

        /// <summary>Same as IsFalling (a faller never jumps). Here so the player and everything else answer the same question.</summary>
        public bool IsInAir { get { return falling; } }

        /// <summary>How high the sprite is drawn above the ground right now (0 when Height Per Level is 0).</summary>
        public float Height { get { return visualBase; } }

        /// <summary>Raised when a fall begins, with how many levels it covers at least.</summary>
        public event Action<int> FallStarted;

        /// <summary>Raised when it lands from a fall, with how many levels it fell. Handy for a thud sound or dust.</summary>
        public event Action<int> Fell;

        /// <summary>Raised when the level changes (up or down), with the new level. A fall raises it once for every level it passes.</summary>
        public event Action<int> LevelChanged;

        /// <summary>
        /// Drops this object to <paramref name="level"/> (0 = the ground) as a fall: a trapdoor, a floor that gives way. It falls to exactly that level, whatever is below.
        /// Returns false when there is nothing to fall to (already at or below that level) or it is already falling.
        /// With "Fall Enabled" off the step down is instant.
        /// </summary>
        public bool FallTo(int level)
        {
            if (!ready || falling || !isActiveAndEnabled) return false;

            level = Mathf.Clamp(level, 0, appliedLevel);
            if (level >= appliedLevel) return false;

            if (cfg.fallEnabled) BeginFall(level, true);
            else ApplyLevel(level);
            return true;
        }

        /// <summary>Drops this object down <paramref name="levels"/> levels (at least one).</summary>
        public bool Drop(int levels)
        {
            return FallTo(appliedLevel - Mathf.Max(1, levels));
        }

        /// <summary>Moves this object to a level at once, with no fall: where a spawner or a lift puts it. Ignored while it falls.</summary>
        public void SetLevel(int level)
        {
            if (!ready || falling) return;

            ApplyLevel(Mathf.Clamp(level, 0, physicsByLevel.Length - 1));
            visualBase = appliedLevel * cfg.heightPerLevel;
            unsupportedFor = 0f;
        }

        // ------------------------------------------------------------ state

        private const float LiftEpsilon = 0.001f;   // below this the sprite counts as "on the ground"
        private const float LevelLiftSpeed = 6f;    // world units per second the sprite rises or sinks when the level changes

        private TopDownLevelWorld world;
        private TopDownLevelToggle toggle;
        private FallSettings cfg;                   // read-only: it can be the world's own set
        private CoopHealth health;                  // optional
        private SpriteRenderer body;                // optional: the sprite that is lifted and that sizes the shadow
        private Collider2D[] bodyColliders;         // the solid colliders, or all of them when none is solid (an item)
        private bool ready;

        // Levels: index 0 = the ground.
        private int[] physicsByLevel;
        private int[] sortingByLevel;
        private int currentPhysics;
        private int currentSorting;
        private int appliedLevel;

        private float unsupportedFor;
        private float visualBase;                   // height from the level (heightPerLevel), eased

        // The fall. fallDepth counts the levels dropped so far (fractional); the layers follow the body down one level at a time.
        private bool falling;
        private int fallFromLevel;
        private int fallForcedLevel = -1;           // >= 0: land on exactly this level whatever is below (FallTo). -1: land on the first level that holds it up.
        private float fallDepth;
        private float fallSpeed;                    // levels per second
        private float fallHigh;                     // 0..1: how far above the landing spot it still is (the shadow's size follows it)
        private float fallShadowLift;               // where the shadow sits while falling: on the level it will land on
        private float blockedFor;

        private ContactFilter2D pointFilter;        // triggers too: a floor can be a trigger
        private ContactFilter2D blockFilter;        // solid colliders only
        private readonly Collider2D[] pointResults = new Collider2D[16];
        private readonly List<Collider2D> overlaps = new List<Collider2D>(16);

        private readonly List<Behaviour> pausedByMe = new List<Behaviour>();

        // The sprite copy that is drawn raised (the Animator-driven sprite itself cannot move without moving the body). Only built when Height Per Level > 0.
        private SortingGroup proxyGroup;
        private SpriteRenderer proxy;
        private bool proxyShown;

        private Transform shadow;
        private SpriteRenderer shadowRenderer;
        private Vector3 shadowBaseScale;
        private Vector3 shadowBaseLocal;

        // ------------------------------------------------------------ setup

        private void Awake()
        {
            world = TopDownLevelWorld.Active;
            if (world == null)
            {
                enabled = false;
                return;
            }

            if (GetComponent<TopDownJump>() != null)
            {
                Debug.LogWarning("TopDownFaller on '" + name + "': this object has a TopDownJump, which already handles its levels and falls. " +
                                 "Tick \"Use World Settings\" on the TopDownJump instead. This TopDownFaller is switched off.", this);
                enabled = false;
                return;
            }

            toggle = GetComponent<TopDownLevelToggle>();
            if (!world.ShouldFall(gameObject, toggle))
            {
                enabled = false;   // the world (or a toggle set to No) says this one does not fall
                return;
            }

            cfg = world.SettingsFor(gameObject, toggle);
            health = GetComponent<CoopHealth>();

            body = GetComponent<SpriteRenderer>();
            if (body == null) body = GetComponentInChildren<SpriteRenderer>();

            CacheColliders();
            BuildLevels();
            if (cfg.heightPerLevel > 0f) BuildSpriteProxy();

            pointFilter = new ContactFilter2D();
            pointFilter.useTriggers = true;
            blockFilter = new ContactFilter2D();
            blockFilter.useTriggers = false;

            visualBase = appliedLevel * cfg.heightPerLevel;   // already up there when the scene starts: no slide
            ready = true;
        }

        private void OnEnable()
        {
            if (ready) TopDownLevelWorld.Register(this);
        }

        private void OnDisable()
        {
            TopDownLevelWorld.Unregister(this);
            if (!ready) return;

            // Switched off in the middle of a fall: give everything back, so nothing stays paused, hidden or untouchable.
            falling = false;
            fallForcedLevel = -1;
            fallDepth = 0f;
            fallSpeed = 0f;
            fallHigh = 0f;
            blockedFor = 0f;
            unsupportedFor = 0f;
            visualBase = appliedLevel * cfg.heightPerLevel;

            SetHittable(true);
            SetPaused(false);
            if (proxyShown) SetLifted(false);
            if (shadowRenderer != null) shadowRenderer.enabled = false;
        }

        private void CacheColliders()
        {
            Collider2D[] all = GetComponentsInChildren<Collider2D>();
            List<Collider2D> solid = new List<Collider2D>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                if (!all[i].isTrigger) solid.Add(all[i]);
            }
            bodyColliders = solid.Count > 0 ? solid.ToArray() : all;
        }

        // Level 0 = the ground. Every level takes its layers from the world. An object that is not on the ladder at all keeps the layers it started on as its ground.
        private void BuildLevels()
        {
            int count = world.LevelCount;
            physicsByLevel = new int[count];
            sortingByLevel = new int[count];
            for (int i = 0; i < count; i++)
            {
                physicsByLevel[i] = world.PhysicsLayerOf(i);
                sortingByLevel[i] = world.SortingLayerIdOf(i);
            }

            int startPhysics = gameObject.layer;
            int startSorting = body != null ? body.sortingLayerID : 0;

            int level = world.LevelOfPhysicsLayer(startPhysics);
            if (level < 0 && body != null) level = world.LevelOfSortingLayer(startSorting);
            if (level < 0)
            {
                level = 0;
                physicsByLevel[0] = startPhysics;
                sortingByLevel[0] = startSorting;
            }

            appliedLevel = level;
            currentPhysics = startPhysics;
            currentSorting = startSorting;
        }

        // ------------------------------------------------------------ every frame

        private void Update()
        {
            if (!ready) return;

            float dt = Time.deltaTime;
            if (falling)
            {
                FallStep(dt);
                return;
            }

            SyncExternalLevel();
            if (appliedLevel > 0) CheckSupport(dt);
            else unsupportedFor = 0f;

            visualBase = Mathf.MoveTowards(visualBase, appliedLevel * cfg.heightPerLevel, LevelLiftSpeed * dt);
        }

        // Something else moved this object to another physics layer (stairs, a script): that is its level now.
        private void SyncExternalLevel()
        {
            int layer = gameObject.layer;
            if (layer == currentPhysics) return;

            currentPhysics = layer;   // follow whatever moved it
            int level = world.LevelOfPhysicsLayer(layer);
            if (level < 0 || level == appliedLevel) return;

            appliedLevel = level;
            if (body != null) currentSorting = body.sortingLayerID;
            unsupportedFor = 0f;
            if (LevelChanged != null) LevelChanged(level);
        }

        // On a raised level: nothing holding it up any more starts a fall (after a short delay, so the edge of a platform does not flicker).
        private void CheckSupport(float dt)
        {
            if (!cfg.fallWhenUnsupported) return;

            int landing = LandingLevel(appliedLevel);
            if (landing >= appliedLevel)
            {
                unsupportedFor = 0f;
                return;
            }

            unsupportedFor += dt;
            if (unsupportedFor < cfg.unsupportedDelay) return;

            unsupportedFor = 0f;
            if (cfg.fallEnabled) BeginFall(landing, false);
            else ApplyLevel(landing);
        }

        // The highest level, from "top" down, that holds this object up right here. The ground always does.
        private int LandingLevel(int top)
        {
            for (int level = top; level > 0; level--)
            {
                if (SupportedAt(level)) return level;
            }
            return 0;
        }

        private bool SupportedAt(int level)
        {
            if (level <= 0) return true;

            JumpColliderFilter surface = world.SurfaceOf(level);
            if (surface == null || surface.IsEmpty) return true;   // nothing says what holds this level up: it is solid everywhere

            return TouchesSurface(surface);
        }

        // Standing on it: the middle of the body is over it, or some part of the body still overlaps it (the same rule as the player, so the
        // edge of a platform does not drop a thing while half of it is still on).
        private bool TouchesSurface(JumpColliderFilter surface)
        {
            int count = Physics2D.OverlapPoint(FeetPoint, pointFilter, pointResults);
            for (int i = 0; i < count; i++)
            {
                if (IsSurface(pointResults[i], surface)) return true;
            }

            for (int s = 0; s < bodyColliders.Length; s++)
            {
                if (bodyColliders[s] == null) continue;

                bodyColliders[s].Overlap(pointFilter, overlaps);
                for (int i = 0; i < overlaps.Count; i++)
                {
                    if (IsSurface(overlaps[i], surface)) return true;
                }
            }
            return false;
        }

        private bool IsSurface(Collider2D c, JumpColliderFilter surface)
        {
            if (c == null || c.transform.IsChildOf(transform)) return false;

            Renderer r = c.GetComponent<Renderer>();
            if (r == null) r = c.GetComponentInParent<Renderer>();
            return surface.Matches(c, r);
        }

        // Where the body is: the middle of its first collider, or the object's position when it has none.
        private Vector2 FeetPoint
        {
            get
            {
                if (bodyColliders.Length > 0 && bodyColliders[0] != null) return bodyColliders[0].bounds.center;
                return transform.position;
            }
        }

        // ------------------------------------------------------------ levels

        // Moves the object to the layers of this level. Only things that are on the layer it is on follow it: a child on its own layer is left alone.
        private void ApplyLevel(int newLevel)
        {
            if (newLevel == appliedLevel) return;
            appliedLevel = newLevel;

            int newPhysics = physicsByLevel[newLevel];
            if (newPhysics != currentPhysics)
            {
                FollowLayer(transform, currentPhysics, newPhysics);
                currentPhysics = newPhysics;
            }

            int newSorting = sortingByLevel[newLevel];
            if (newSorting != currentSorting)
            {
                Renderer[] all = GetComponentsInChildren<Renderer>(true);   // not cached: other scripts add renderers at runtime
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].sortingLayerID == currentSorting) all[i].sortingLayerID = newSorting;
                }
                currentSorting = newSorting;
            }

            if (LevelChanged != null) LevelChanged(newLevel);
        }

        private static void FollowLayer(Transform t, int from, int to)
        {
            if (t.gameObject.layer == from) t.gameObject.layer = to;
            for (int i = 0; i < t.childCount; i++) FollowLayer(t.GetChild(i), from, to);
        }

        // ------------------------------------------------------------ falling

        // "landing" is the level it falls to. forced = FallTo: it lands there whatever holds it up below; otherwise it lands on the first level that does.
        private void BeginFall(int landing, bool forced)
        {
            falling = true;
            fallFromLevel = appliedLevel;
            fallForcedLevel = forced ? landing : -1;
            fallDepth = 0f;
            fallSpeed = 0f;
            blockedFor = 0f;
            unsupportedFor = 0f;
            fallHigh = 1f;
            fallShadowLift = landing * cfg.heightPerLevel;

            SetHittable(false);   // no hurt box in the air: a hit does nothing
            SetPaused(true);
            PlaySound(toggle != null ? toggle.FallClip : null);

            if (logFalls) Debug.Log("TopDownFaller on '" + name + "': falls from level " + fallFromLevel + " to level " + landing + ".", this);
            if (FallStarted != null) FallStarted(fallFromLevel - landing);
        }

        // Every frame of a fall. What is under the body is read again each frame: it can slide off a lower floor (the fall gets longer) or over one (the fall is caught on it).
        private void FallStep(float dt)
        {
            int landing = fallForcedLevel >= 0 ? Mathf.Min(fallForcedLevel, appliedLevel) : LandingLevel(appliedLevel);

            // Gravity in levels per second squared, so that ONE level from rest takes "Fall Seconds Per Level".
            float gravity = 2f / (cfg.fallSecondsPerLevel * cfg.fallSecondsPerLevel);
            fallSpeed += gravity * dt;
            fallDepth += fallSpeed * dt;

            // The body steps down through the levels as it passes them, and its physics and sorting layers go with it. If the level below is taken
            // by something solid the fall waits at the edge of it, up to Blocked Landing Seconds; then it comes down anyway.
            int wanted = Mathf.Max(landing, fallFromLevel - Mathf.FloorToInt(fallDepth));
            while (appliedLevel > wanted)
            {
                int next = appliedLevel - 1;
                if (blockedFor < cfg.blockedLandingSeconds && BlockedAtLevel(next))
                {
                    blockedFor += dt;
                    fallDepth = Mathf.Min(fallDepth, fallFromLevel - appliedLevel + 0.99f);   // hold just above that level
                    break;
                }

                blockedFor = 0f;
                ApplyLevel(next);
            }

            // The sprite drops through the levels; the shadow sits on the level it will land on and grows as the ground gets closer
            // (it uses levels, not world units, so it works with Height Per Level at 0 too).
            float levelsLeft = Mathf.Max(0f, (fallFromLevel - landing) - fallDepth);
            visualBase = Mathf.Max(landing, fallFromLevel - fallDepth) * cfg.heightPerLevel;
            fallShadowLift = landing * cfg.heightPerLevel;
            fallHigh = Mathf.Clamp01(levelsLeft);

            if (appliedLevel <= landing && fallDepth >= fallFromLevel - landing) EndFall();
        }

        private void EndFall()
        {
            int levelsFallen = fallFromLevel - appliedLevel;

            falling = false;
            fallForcedLevel = -1;
            fallDepth = 0f;
            fallSpeed = 0f;
            fallHigh = 0f;
            blockedFor = 0f;
            unsupportedFor = 0f;
            visualBase = appliedLevel * cfg.heightPerLevel;

            SetHittable(true);   // before the damage: something that is still "in the air" would take none
            SetPaused(false);

            if (levelsFallen <= 0) return;

            if (logFalls) Debug.Log("TopDownFaller on '" + name + "': landed on level " + appliedLevel + " after " + levelsFallen + " level(s).", this);
            ApplyFallDamage(levelsFallen);
            PlaySound(toggle != null ? toggle.LandClip : null);
            if (Fell != null) Fell(levelsFallen);
        }

        // Is something solid in the way of the body on this level's physics layer (so coming down onto it would shove the object out)?
        // The floor of that level is not in the way: it is what we land on.
        private bool BlockedAtLevel(int level)
        {
            blockFilter.SetLayerMask(Physics2D.GetLayerCollisionMask(physicsByLevel[level]));

            JumpColliderFilter surface = world.SurfaceOf(level);
            bool surfaceKnown = surface != null && !surface.IsEmpty;

            for (int s = 0; s < bodyColliders.Length; s++)
            {
                Collider2D mine = bodyColliders[s];
                if (mine == null || mine.isTrigger) continue;   // only a solid body is shoved

                mine.Overlap(blockFilter, overlaps);
                for (int i = 0; i < overlaps.Count; i++)
                {
                    Collider2D c = overlaps[i];
                    if (c == null || c.transform.IsChildOf(transform)) continue;
                    if (surfaceKnown && IsSurface(c, surface)) continue;
                    return true;
                }
            }
            return false;
        }

        // Fall Damage: every level past the safe ones hurts. Through CoopHealth when there is one (so Invincible and the co-op rules still apply),
        // otherwise by sending the message named in the settings (the health system's ApplyDamage).
        private void ApplyFallDamage(int levelsFallen)
        {
            if (!cfg.fallDamage) return;

            int hurting = levelsFallen - cfg.safeFallLevels;
            if (hurting <= 0) return;

            float amount = hurting * cfg.damagePerLevel;
            if (cfg.scaleDamageByDifficulty) amount *= DifficultyRules.TrapDamageScale;
            if (amount <= 0f) return;

            if (health != null)
            {
                if (health.IsAlive) health.ApplyDamage(amount);
            }
            else if (!string.IsNullOrEmpty(cfg.damageMessage))
            {
                SendMessage(cfg.damageMessage, amount, SendMessageOptions.DontRequireReceiver);
            }
        }

        private void SetHittable(bool hittable)
        {
            if (health != null) health.Airborne = !hittable;

            Collider2D[] boxes = toggle != null ? toggle.HitBoxes : null;
            if (boxes == null) return;

            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] != null) boxes[i].enabled = hittable;
            }
        }

        // Scripts listed on the toggle (the foe's AI...) are switched off for the fall and back on at the landing. Only the ones that were on.
        private void SetPaused(bool paused)
        {
            if (paused)
            {
                Behaviour[] list = toggle != null ? toggle.PauseWhileFalling : null;
                if (list == null) return;

                for (int i = 0; i < list.Length; i++)
                {
                    Behaviour b = list[i];
                    if (b == null || b == this || !b.enabled) continue;

                    b.enabled = false;
                    pausedByMe.Add(b);
                }
                return;
            }

            for (int i = 0; i < pausedByMe.Count; i++)
            {
                if (pausedByMe[i] != null) pausedByMe[i].enabled = true;
            }
            pausedByMe.Clear();
        }

        // Respects the Sound On/Off menu, like the footsteps do.
        private void PlaySound(AudioClip clip)
        {
            if (clip == null || toggle == null) return;

            AudioSource source = toggle.SoundSource;
            if (source == null || !source.isActiveAndEnabled || !GameSettings.SoundOn) return;

            source.PlayOneShot(clip);
        }

        // ------------------------------------------------------------ drawing

        // After the Animator has chosen this frame's sprite.
        private void LateUpdate()
        {
            if (!ready) return;

            // The copy is only there when Height Per Level is more than 0.
            bool lifted = proxy != null && body != null && visualBase > LiftEpsilon;
            if (lifted != proxyShown) SetLifted(lifted);
            if (lifted) MirrorBody(visualBase);

            UpdateShadow();
        }

        // The Animator draws the sprite on the object itself, which cannot move without taking the body with it. So while the sprite is high, that renderer
        // is hidden (without touching its "enabled", which other scripts use to blink it) and a copy is drawn raised instead.
        private void BuildSpriteProxy()
        {
            if (body == null) return;

            GameObject group = new GameObject("FallVisual");
            group.layer = gameObject.layer;
            group.transform.SetParent(body.transform, false);
            proxyGroup = group.AddComponent<SortingGroup>();

            GameObject sprite = new GameObject("FallSprite");
            sprite.layer = gameObject.layer;
            sprite.transform.SetParent(group.transform, false);
            proxy = sprite.AddComponent<SpriteRenderer>();

            group.SetActive(false);
        }

        private void SetLifted(bool lifted)
        {
            proxyShown = lifted;
            if (body != null) body.forceRenderingOff = lifted;
            if (proxyGroup != null) proxyGroup.gameObject.SetActive(lifted);
        }

        private void MirrorBody(float lift)
        {
            if (proxy.sprite != body.sprite) proxy.sprite = body.sprite;
            proxy.color = body.color;
            proxy.flipX = body.flipX;
            proxy.flipY = body.flipY;
            proxy.sharedMaterial = body.sharedMaterial;
            proxy.maskInteraction = body.maskInteraction;
            proxy.enabled = body.enabled;

            proxyGroup.sortingLayerID = body.sortingLayerID;
            proxyGroup.sortingOrder = body.sortingOrder;
            proxy.transform.localPosition = LocalLift(proxyGroup.transform, lift);
        }

        // The shadow is only there while the object falls. It sits on the level it will land on and is small while the ground is far.
        private void UpdateShadow()
        {
            if (!falling || !cfg.showShadow)
            {
                if (shadowRenderer != null && shadowRenderer.enabled) shadowRenderer.enabled = false;
                return;
            }

            if (shadow == null) BuildShadow();

            shadowRenderer.enabled = true;
            shadow.localScale = shadowBaseScale * Mathf.Lerp(1f, cfg.shadowShrink, fallHigh);
            shadow.localPosition = shadowBaseLocal + LocalLift(shadow.parent, fallShadowLift);
        }

        // A small hard-edged ellipse (the same one the player's jump uses), sized from the sprite or from the settings.
        private void BuildShadow()
        {
            GameObject go = new GameObject("FallShadow");
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false);
            shadow = go.transform;

            shadowRenderer = go.AddComponent<SpriteRenderer>();
            shadowRenderer.sprite = TopDownJump.SharedShadowSprite;
            shadowRenderer.color = new Color(0f, 0f, 0f, cfg.shadowOpacity);
            shadowRenderer.sortingLayerID = body != null ? body.sortingLayerID : currentSorting;
            shadowRenderer.sortingOrder = body != null ? body.sortingOrder - 1 : -1;
            shadowRenderer.enabled = false;

            Vector2 size = cfg.shadowSize;
            Vector3 offset = Vector3.zero;
            if (cfg.fitShadowToSprite && body != null)
            {
                Bounds b = body.bounds;
                float width = Mathf.Max(0.2f, b.size.x * 0.9f);
                size = new Vector2(width, width * 0.5f);
                offset = transform.InverseTransformPoint(new Vector3(b.center.x, b.min.y + size.y * 0.5f, transform.position.z));
            }

            // The object may be scaled: the shadow is a child, so it is divided by the scale to come out the size asked for in the world.
            Vector2 natural = TopDownJump.SharedShadowNaturalSize;
            Vector3 scale = transform.lossyScale;
            float sx = Mathf.Max(0.0001f, Mathf.Abs(scale.x));
            float sy = Mathf.Max(0.0001f, Mathf.Abs(scale.y));
            shadowBaseScale = new Vector3(size.x / natural.x / sx, size.y / natural.y / sy, 1f);
            shadowBaseLocal = new Vector3(offset.x, offset.y, 0f);
            shadow.localScale = shadowBaseScale;
            shadow.localPosition = shadowBaseLocal;
        }

        // "Up by this much in the world", expressed in the local space of a parent (which may be scaled).
        private static Vector3 LocalLift(Transform space, float height)
        {
            Vector3 up = Vector3.up * height;
            return space != null ? space.InverseTransformVector(up) : up;
        }
    }
}
