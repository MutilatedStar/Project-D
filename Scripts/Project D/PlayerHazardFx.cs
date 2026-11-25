using System.Collections.Generic;
using UnityEngine;
using Ilumisoft.HealthSystem;

/// <summary>
/// The LOOKS of the water and lava hazards on one player (a robot). FloorManager decides what happens (slow, damage, heat, death); this script only draws it.
/// FloorManager adds it to a player the first time that player needs it, so there is nothing to set up. To change how it looks, add it to the
/// player prefab yourself and edit the numbers: FloorManager uses the one that is already there.
///
///   SPARKS (water)   small sparks hop out of the BOTTOM of the sprite and fall back, like a short circuit. More of them the longer the player has been in the
///                    water, and they die away as the player dries off, so they are temporary.
///   HEAT (lava)      a red layer fades in over the sprite: a little at first, then more and more, and in the last moments it flashes faster and faster.
///                    It is a separate red picture drawn on top, not the sprite's own colour, so it works on any robot colours and never fights with PlayerGlow or
///                    PlayerDamageEffects. While the player is jumping it rides on the raised copy that TopDownJump draws.
///   BLOWING UP       Explode() throws red-hot embers and bursts the robot into pieces of its own colours (FoeDeathExplosion, added to the player if it has none).
///                    The robot comes back by itself once the player is alive again (a revive or a respawn).
///
/// Needs a SpriteRenderer on the player (or a child) and a health component (Health / CoopHealth), the same as PlayerDamageEffects.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1000)]   // after TopDownJump's LateUpdate, so the red layer follows the picture that is really on screen
public class PlayerHazardFx : MonoBehaviour
{
    // ------------------------------------------------------------ inspector

    [Header("Picture")]
    [Tooltip("The character's picture. Leave EMPTY to use the same one PlayerDamageEffects uses (on this object, else the first child).")]
    [SerializeField] private SpriteRenderer sprite;
    [Tooltip("Optional. Your own particle material for the sparks and embers. Leave EMPTY to make a simple one in code.")]
    [SerializeField] private Material particleMaterial;

    [Header("Sparks (water)")]
    [SerializeField] private bool sparksOn = true;
    [Tooltip("Sparks per second the moment the player steps in, and after they have been in the water for the full ramp time.")]
    [SerializeField, Min(0f)] private float sparksPerSecondLow = 4f;
    [SerializeField, Min(0f)] private float sparksPerSecondHigh = 24f;
    [Tooltip("Smallest and biggest spark, in Unity units (0.03 is about one pixel on a 32-pixel sprite).")]
    [SerializeField] private Vector2 sparkSize = new Vector2(0.05f, 0.09f);
    [Tooltip("How fast a spark is thrown UP when it leaves the robot, slowest and fastest (units per second). It slows, stops, and falls back down.")]
    [SerializeField] private Vector2 sparkLaunchSpeed = new Vector2(1.5f, 3.2f);
    [Tooltip("How far sideways a spark may drift, in units per second (each spark picks left or right at random).")]
    [SerializeField, Min(0f)] private float sparkSideSpeed = 0.9f;
    [Tooltip("How long a spark lasts, shortest and longest, in seconds.")]
    [SerializeField] private Vector2 sparkLifetime = new Vector2(0.35f, 0.6f);
    [Tooltip("What fraction of the sprite's width the sparks come out across, along its bottom edge.")]
    [SerializeField, Range(0.1f, 1.5f)] private float feetWidth = 0.6f;
    [Tooltip("The colour a spark starts with, and the colour it burns down to.")]
    [SerializeField] private Color sparkStartColor = new Color(0.9f, 0.97f, 1f, 1f);
    [SerializeField] private Color sparkEndColor = new Color(0.35f, 0.75f, 1f, 1f);
    [Tooltip("Drawing order next to the character. Higher = in front of it.")]
    [SerializeField] private int sparksSortingOffset = 2;

    [Header("Heat (lava)")]
    [Tooltip("The colour the robot glows as it heats up.")]
    [SerializeField] private Color heatColor = new Color(1f, 0.12f, 0.04f, 1f);
    [Tooltip("How much of the red covers the robot at full heat. 1 = flat red, lower lets some of the robot's own picture show through.")]
    [SerializeField, Range(0f, 1f)] private float heatMaxOverlay = 0.85f;
    [Tooltip("The red starts to flash once the heat is above this (0 = cold, 1 = about to blow up).")]
    [SerializeField, Range(0f, 1f)] private float pulseStartsAt = 0.6f;
    [Tooltip("How much the flashing dims the red at its dimmest. 0 = no flashing.")]
    [SerializeField, Range(0f, 1f)] private float pulseDepth = 0.35f;
    [Tooltip("Flashes per second (times 2π) when the flashing starts, and just before the explosion.")]
    [SerializeField, Min(0f)] private float pulseSpeedSlow = 6f;
    [SerializeField, Min(0f)] private float pulseSpeedFast = 22f;
    [Tooltip("Drawing order of the red layer above the robot's picture.")]
    [SerializeField] private int overlaySortingOffset = 1;

    [Header("Blowing up")]
    [Tooltip("How many red-hot embers fly out. 0 = none (the robot still bursts into pieces).")]
    [SerializeField, Min(0)] private int emberCount = 36;
    [SerializeField] private Vector2 emberSpeed = new Vector2(1.5f, 4f);
    [SerializeField] private Vector2 emberSize = new Vector2(0.07f, 0.16f);
    [SerializeField] private Vector2 emberLifetime = new Vector2(0.45f, 0.9f);
    [SerializeField, Min(0.01f)] private float emberAreaRadius = 0.15f;
    [SerializeField] private int embersSortingOffset = 3;

    // ------------------------------------------------------------ state

    private HealthComponent health;
    private FoeDeathExplosion boom;
    private ParticleSystem sparks, embers;
    private ParticleSystemRenderer sparksRenderer, embersRenderer;
    private SpriteRenderer overlay;
    private SpriteRenderer proxy, proxyOwner;   // TopDownJump's raised copy of the robot (JumpVisual / JumpSprite), if it has one
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();   // materials and pictures made here, cleaned up in OnDestroy
    private bool particlesFailed;
    private bool settingsDirty;
    private bool exploded;
    private float sparkLevel;   // 0..1, set by FloorManager
    private float heat;         // 0..1, set by FloorManager
    private float shapeWidth = -1f;

    /// <summary>True from the moment the robot blows up until the player is alive again.</summary>
    public bool IsExploded { get { return exploded; } }

    // ------------------------------------------------------------ what FloorManager calls

    /// <summary>0 = no sparks. Above 0 the sparks come out, more of them the closer to 1 (the player has been in the water a long time).</summary>
    public void SetSparks(float level)
    {
        sparkLevel = Mathf.Clamp01(level);
    }

    /// <summary>0 = the robot looks normal, 1 = glowing red, about to blow up.</summary>
    public void SetHeat(float value)
    {
        heat = Mathf.Clamp01(value);
    }

    /// <summary>Stops the sparks and the red right now (a death, a new floor). Does not bring back a robot that has blown up: that happens when the player is alive again.</summary>
    public void Clear()
    {
        sparkLevel = 0f;
        heat = 0f;
    }

    /// <summary>The red-hot explosion: embers fly out and the robot bursts into pieces of its own colours. FloorManager kills the player right after calling this.</summary>
    public void Explode()
    {
        if (exploded || sprite == null) return;
        exploded = true;

        // The red layer must be off BEFORE the explosion looks for pictures to turn into pieces, or it would burst too.
        heat = 0f;
        sparkLevel = 0f;
        if (overlay != null) overlay.enabled = false;
        UpdateSparks();

        BurstEmbers();

        if (boom == null) boom = GetComponent<FoeDeathExplosion>();
        if (boom == null) boom = gameObject.AddComponent<FoeDeathExplosion>();
        boom.Explode();
    }

    // ------------------------------------------------------------ setup

    private void Awake()
    {
        health = GetComponent<HealthComponent>();
        if (health == null) health = GetComponentInParent<HealthComponent>();

        if (sprite == null) sprite = GetComponent<SpriteRenderer>();
        if (sprite == null) sprite = GetComponentInChildren<SpriteRenderer>();

        if (sprite == null)
        {
            Debug.LogWarning("PlayerHazardFx on '" + name + "': no SpriteRenderer found on the player or its children, so the water sparks and the lava heat cannot be drawn. " +
                             "Drag the character's SpriteRenderer into Sprite.", this);
            enabled = false;
        }
    }

    private void OnValidate()
    {
        settingsDirty = true;   // edits in the Inspector while playing are picked up on the next frame
    }

    private void OnDestroy()
    {
        if (sparks != null) Destroy(sparks.gameObject);
        if (embers != null) Destroy(embers.gameObject);
        if (overlay != null) Destroy(overlay.gameObject);
        for (int i = 0; i < owned.Count; i++)
        {
            if (owned[i] != null) Destroy(owned[i]);
        }
    }

    // ------------------------------------------------------------ running

    private void Update()
    {
        // Back from the dead (a revive or a respawn): put the robot back together.
        if (exploded && (health == null || health.IsAlive)) Recover();
    }

    private void LateUpdate()
    {
        UpdateSparks();
        UpdateOverlay();
    }

    private void Recover()
    {
        exploded = false;
        heat = 0f;
        sparkLevel = 0f;
        if (boom != null) boom.Restore();
    }

    // ------------------------------------------------------------ sparks

    private void UpdateSparks()
    {
        if (settingsDirty)
        {
            settingsDirty = false;
            if (sparks != null) ConfigureSparks();
            if (embers != null) ConfigureEmbers();
        }

        bool want = sparksOn && sparkLevel > 0.001f && !exploded && sprite != null;
        if (want && sparks == null && !particlesFailed) CreateSparks();
        if (sparks == null) return;

        ParticleSystem.EmissionModule emission = sparks.emission;
        if (!want)
        {
            emission.rateOverTime = 0f;
            return;
        }

        emission.rateOverTime = Mathf.Lerp(sparksPerSecondLow, sparksPerSecondHigh, sparkLevel);
        if (!sparks.isPlaying) sparks.Play();
        PlaceSparks();
    }

    // The sparks come out along the bottom edge of the current picture (not its pivot, which may be anywhere).
    private void PlaceSparks()
    {
        Sprite shape = sprite.sprite;
        if (shape == null) return;

        Bounds b = shape.bounds;
        float x = sprite.flipX ? -b.center.x : b.center.x;
        float y = sprite.flipY ? -b.max.y : b.min.y;
        sparks.transform.localPosition = new Vector3(x, y, 0f);

        float width = Mathf.Max(0.05f, b.size.x * feetWidth);
        if (!Mathf.Approximately(width, shapeWidth))
        {
            shapeWidth = width;
            ParticleSystem.ShapeModule sh = sparks.shape;
            sh.scale = new Vector3(width, 0.02f, 0.01f);
        }

        sparksRenderer.sortingLayerID = sprite.sortingLayerID;
        sparksRenderer.sortingOrder = sprite.sortingOrder + sparksSortingOffset;
    }

    private void CreateSparks()
    {
        Material material = MakeMaterial(MakeTexture(2, false));   // square sparks: they look right on pixel art
        if (material == null)
        {
            particlesFailed = true;
            return;
        }

        sparks = MakeSystem("Water Sparks", material, 150, ParticleSystemShapeType.Box, 0f, out sparksRenderer);
        ConfigureSparks();
    }

    private void ConfigureSparks()
    {
        ParticleSystem.MainModule main = sparks.main;
        main.startLifetime = Between(sparkLifetime.x, sparkLifetime.y);
        main.startSize = Between(sparkSize.x, sparkSize.y);
        main.startSpeed = 0f;   // the hop is made by the velocity below, so it does not depend on the shape's direction or on Physics gravity
        main.startColor = Color.white;

        // A hop: every spark starts going UP, slows down in a straight line and ends going DOWN just as fast, so it lands back where it left from.
        // x, y and z must all use the same kind of curve, so all three are "between two curves".
        float launchLow = Mathf.Min(sparkLaunchSpeed.x, sparkLaunchSpeed.y);
        float launchHigh = Mathf.Max(sparkLaunchSpeed.x, sparkLaunchSpeed.y);
        float side = Mathf.Max(0f, sparkSideSpeed);

        ParticleSystem.VelocityOverLifetimeModule vel = sparks.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, -side), AnimationCurve.Constant(0f, 1f, side));
        vel.y = new ParticleSystem.MinMaxCurve(1f, Hop(launchLow), Hop(launchHigh));
        vel.z = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Constant(0f, 1f, 0f), AnimationCurve.Constant(0f, 1f, 0f));

        // The picture is white, so the colour comes entirely from this: start colour burning down to the end colour, then fading out.
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(sparkStartColor, 0f), new GradientColorKey(sparkEndColor, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule col = sparks.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(g);

        ParticleSystem.SizeOverLifetimeModule size = sparks.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));

        shapeWidth = -1f;   // makes PlaceSparks write the emission width again
    }

    private static AnimationCurve Hop(float up)
    {
        return AnimationCurve.Linear(0f, up, 1f, -up);
    }

    // ------------------------------------------------------------ embers (the explosion)

    private void BurstEmbers()
    {
        if (emberCount <= 0 || particlesFailed) return;

        if (embers == null)
        {
            Material material = MakeMaterial(MakeTexture(2, false));
            if (material == null)
            {
                particlesFailed = true;
                return;
            }
            embers = MakeSystem("Heat Embers", material, Mathf.Max(60, emberCount * 2), ParticleSystemShapeType.Circle, emberAreaRadius, out embersRenderer);
            ConfigureEmbers();
        }

        // The middle of the robot's current picture.
        Sprite shape = sprite.sprite;
        Vector3 centre = shape != null ? shape.bounds.center : Vector3.zero;
        if (sprite.flipX) centre.x = -centre.x;
        if (sprite.flipY) centre.y = -centre.y;
        centre.z = 0f;
        embers.transform.localPosition = centre;

        embersRenderer.sortingLayerID = sprite.sortingLayerID;
        embersRenderer.sortingOrder = sprite.sortingOrder + embersSortingOffset;

        embers.Play();
        embers.Emit(emberCount);
    }

    private void ConfigureEmbers()
    {
        ParticleSystem.MainModule main = embers.main;
        main.startLifetime = Between(emberLifetime.x, emberLifetime.y);
        main.startSize = Between(emberSize.x, emberSize.y);
        main.startSpeed = Between(emberSpeed.x, emberSpeed.y);
        main.startColor = Color.white;

        ParticleSystem.ShapeModule shape = embers.shape;
        shape.radius = Mathf.Max(0.01f, emberAreaRadius);

        // White-hot, then orange, then deep red, then gone.
        Gradient g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.95f, 0.7f, 1f), 0f),
                new GradientColorKey(new Color(1f, 0.45f, 0.08f, 1f), 0.35f),
                new GradientColorKey(new Color(0.75f, 0.05f, 0.02f, 1f), 1f)
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule col = embers.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(g);

        ParticleSystem.SizeOverLifetimeModule size = embers.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));
    }

    // ------------------------------------------------------------ the red layer

    private void UpdateOverlay()
    {
        float amount = exploded ? 0f : heat * heatMaxOverlay;
        if (amount <= 0.002f || sprite == null || sprite.sprite == null)
        {
            if (overlay != null && overlay.enabled) overlay.enabled = false;
            return;
        }

        // The last stretch before the explosion: the red flashes, faster and faster.
        if (heat > pulseStartsAt && pulseDepth > 0f)
        {
            float k = Mathf.InverseLerp(pulseStartsAt, 1f, heat);
            float speed = Mathf.Lerp(pulseSpeedSlow, pulseSpeedFast, k);
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.time * speed);
            amount *= 1f - pulseDepth * k * wave;
        }

        EnsureOverlay();

        // Which picture is really on screen: the Animator's own, or the raised copy TopDownJump draws while the player is in the air.
        SpriteRenderer drawn = sprite;
        SpriteRenderer copy = ProxyOf(sprite);
        if (sprite.forceRenderingOff && copy != null && copy.gameObject.activeInHierarchy) drawn = copy;

        Transform t = overlay.transform;
        if (t.parent != drawn.transform)
        {
            t.SetParent(drawn.transform, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        overlay.sprite = drawn.sprite;
        overlay.flipX = drawn.flipX;
        overlay.flipY = drawn.flipY;
        if (overlay.sharedMaterial != drawn.sharedMaterial) overlay.sharedMaterial = drawn.sharedMaterial;   // same lighting as the robot itself
        overlay.sortingLayerID = drawn.sortingLayerID;
        overlay.sortingOrder = drawn.sortingOrder + overlaySortingOffset;
        overlay.color = new Color(heatColor.r, heatColor.g, heatColor.b, Mathf.Clamp01(amount));
        overlay.enabled = true;
    }

    private void EnsureOverlay()
    {
        if (overlay != null) return;

        GameObject go = new GameObject("HeatOverlay");
        go.layer = sprite.gameObject.layer;   // same camera layer, so split-screen cameras draw it too
        go.transform.SetParent(sprite.transform, false);
        overlay = go.AddComponent<SpriteRenderer>();
        overlay.enabled = false;
    }

    // TopDownJump keeps a raised copy of the robot at JumpVisual/JumpSprite under the body's renderer. null if there is no jump on this player.
    private SpriteRenderer ProxyOf(SpriteRenderer body)
    {
        if (proxy == null || proxyOwner != body)
        {
            proxyOwner = body;
            Transform t = body.transform.Find("JumpVisual/JumpSprite");
            proxy = t != null ? t.GetComponent<SpriteRenderer>() : null;
        }
        return proxy;
    }

    // ------------------------------------------------------------ particle helpers

    private ParticleSystem MakeSystem(string objectName, Material material, int maxParticles, ParticleSystemShapeType shapeType, float radius, out ParticleSystemRenderer rend)
    {
        GameObject go = new GameObject(objectName);
        go.layer = sprite.gameObject.layer;
        go.transform.SetParent(sprite.transform, false);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);   // it starts playing on its own: stop it before changing settings

        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;   // sparks and embers stay where they left, the robot walks on
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.gravityModifier = 0f;
        main.maxParticles = maxParticles;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = shapeType;
        if (shapeType == ParticleSystemShapeType.Circle)
        {
            shape.radius = radius;
            shape.radiusThickness = 1f;
        }

        rend = go.GetComponent<ParticleSystemRenderer>();
        rend.sharedMaterial = material;
        rend.sortingLayerID = sprite.sortingLayerID;
        return ps;
    }

    private Material MakeMaterial(Texture2D picture)
    {
        if (particleMaterial != null) return particleMaterial;   // yours: used as it is, and never destroyed here

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogWarning("PlayerHazardFx on '" + name + "': the Sprites/Default shader was not found, so no sparks or embers can be drawn. " +
                             "Drag a particle material into Particle Material.", this);
            return null;
        }

        Material m = new Material(shader);
        m.mainTexture = picture;
        owned.Add(m);
        return m;
    }

    // A tiny white picture with hard pixel edges: a square, or a round blob.
    private Texture2D MakeTexture(int size, bool round)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;

        float c = (size - 1) * 0.5f;
        float r = size * 0.5f - 0.25f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = !round || Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) <= r;
                tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }
        tex.Apply();
        owned.Add(tex);
        return tex;
    }

    private static ParticleSystem.MinMaxCurve Between(float a, float b)
    {
        return new ParticleSystem.MinMaxCurve(Mathf.Min(a, b), Mathf.Max(a, b));
    }
}
