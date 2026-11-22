using System.Collections.Generic;
using UnityEngine;
using Ilumisoft.HealthSystem;

/// <summary>
/// A player that is running out of health looks like a machine running out of power. It reads the player's health (Health, CoopHealth,
/// any HealthComponent) every frame, so it follows damage, healing, the drain and respawns without being told anything.
///
///   80% or less   a few sparks leave the character.
///   50% or less   the sparks get bigger and more frequent, and white smoke starts.
///   30% or less   the smoke turns black and a lot of sparks come out.
///   10% or less   the character dims in and out of black, like it has no power.
///   0%            the character turns black, every effect stops, and (optional) the character falls over and stands still.
///
/// SET UP: put it on the player prefab(s), next to PlayerGlow (Unity adds PlayerGlow for you if it is missing). Nothing else to wire up.
/// The dimming and blackout are drawn by PlayerGlow, because it is the one script that writes the sprite's colour, so the tint, the
/// gold invincibility pulse and this never fight over it.
///
/// EVERYTHING IS EDITABLE: each effect has its own tick box, the health levels above are settings, and every spark / smoke level has its
/// own rate, size, speed, lifetime and colour. Change them while the game runs and the effects follow at once.
/// The sparks and smoke are made in code (small square sparks, round smoke) so no art is needed. To use your own particle material,
/// drag it into Particle Material.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerGlow))]
public class PlayerDamageEffects : MonoBehaviour
{
    // ------------------------------------------------------------ settings blocks

    [System.Serializable]
    public class SparkLevel
    {
        [Tooltip("Sparks per second.")]
        [Min(0f)] public float perSecond = 1f;
        [Tooltip("Smallest and biggest spark, in Unity units (0.03 is about one pixel on a 32-pixel sprite).")]
        [Min(0.01f)] public float minSize = 0.05f;
        [Min(0.01f)] public float maxSize = 0.07f;
        [Tooltip("Slowest and fastest a spark flies away, in units per second.")]
        [Min(0f)] public float minSpeed = 0.8f;
        [Min(0f)] public float maxSpeed = 1.6f;
        [Tooltip("How long a spark lasts, in seconds.")]
        [Min(0.05f)] public float lifetime = 0.45f;
    }

    [System.Serializable]
    public class SmokeLevel
    {
        [Tooltip("Puffs per second.")]
        [Min(0f)] public float perSecond = 4f;
        [Tooltip("Smallest and biggest puff when it starts, in Unity units.")]
        [Min(0.01f)] public float minSize = 0.15f;
        [Min(0.01f)] public float maxSize = 0.25f;
        [Tooltip("How long a puff lasts, in seconds.")]
        [Min(0.1f)] public float lifetime = 1.1f;
        [Tooltip("How fast the smoke drifts up, in units per second.")]
        [Min(0f)] public float riseSpeed = 0.5f;
        [Tooltip("How many times bigger a puff is by the end of its life. 1 = it does not grow.")]
        [Min(1f)] public float growth = 2f;
        [Tooltip("The colour of the smoke. The alpha is how thick it looks.")]
        public Color color = new Color(1f, 1f, 1f, 0.55f);
    }

    // ------------------------------------------------------------ inspector

    [Header("Master")]
    [Tooltip("Off: no damage effects at all (no sparks, smoke, dimming, black, falling over).")]
    [SerializeField] private bool effectsOn = true;
    [Tooltip("The character's picture. Leave EMPTY to use the same one PlayerGlow uses.")]
    [SerializeField] private SpriteRenderer sprite;
    [Tooltip("Optional. Your own particle material for the sparks and smoke. Leave EMPTY to make a simple one in code.")]
    [SerializeField] private Material particleMaterial;

    [Header("Health levels (the share of max health that is LEFT: 0.8 = 80%)")]
    [SerializeField, Range(0f, 1f)] private float sparksBelow = 0.8f;
    [Tooltip("Sparks grow and white smoke starts.")]
    [SerializeField, Range(0f, 1f)] private float smokeBelow = 0.5f;
    [Tooltip("The smoke turns black and a lot of sparks come out.")]
    [SerializeField, Range(0f, 1f)] private float heavyBelow = 0.3f;
    [Tooltip("The character starts dimming in and out of black.")]
    [SerializeField, Range(0f, 1f)] private float dimBelow = 0.1f;

    [Header("Sparks")]
    [SerializeField] private bool sparksOn = true;
    [Tooltip("At the first level: very few.")]
    [SerializeField] private SparkLevel sparksFew = new SparkLevel();
    [Tooltip("At the smoke level: bigger and more.")]
    [SerializeField] private SparkLevel sparksMore = new SparkLevel { perSecond = 3f, minSize = 0.08f, maxSize = 0.12f, minSpeed = 1f, maxSpeed = 2.2f, lifetime = 0.5f };
    [Tooltip("At the heavy level: a lot.")]
    [SerializeField] private SparkLevel sparksLots = new SparkLevel { perSecond = 14f, minSize = 0.08f, maxSize = 0.14f, minSpeed = 1.5f, maxSpeed = 3f, lifetime = 0.55f };
    [Tooltip("The colour a spark starts with, and the colour it burns down to.")]
    [SerializeField] private Color sparkStartColor = new Color(1f, 0.95f, 0.55f, 1f);
    [SerializeField] private Color sparkEndColor = new Color(1f, 0.4f, 0.1f, 1f);
    [Tooltip("How wide the area is that the sparks come out of, around the middle of the character (Unity units).")]
    [SerializeField, Min(0.01f)] private float sparkAreaRadius = 0.25f;
    [Tooltip("Drawing order next to the character. Higher = in front of it.")]
    [SerializeField] private int sparksSortingOffset = 2;

    [Header("Smoke")]
    [SerializeField] private bool smokeOn = true;
    [Tooltip("At the smoke level: white.")]
    [SerializeField] private SmokeLevel whiteSmoke = new SmokeLevel();
    [Tooltip("At the heavy level: black.")]
    [SerializeField] private SmokeLevel blackSmoke = new SmokeLevel { perSecond = 7f, minSize = 0.18f, maxSize = 0.3f, lifetime = 1.3f, riseSpeed = 0.6f, growth = 2.2f, color = new Color(0.08f, 0.08f, 0.08f, 0.7f) };
    [Tooltip("Where the smoke comes out, relative to the middle of the character (Unity units). Positive Y = higher up.")]
    [SerializeField] private Vector2 smokeOffset = new Vector2(0f, 0.2f);
    [SerializeField] private int smokeSortingOffset = 1;

    [Header("Dimming (lack of power)")]
    [SerializeField] private bool dimOn = true;
    [Tooltip("How dark the character gets at its darkest. 1 = fully black, 0.5 = half way.")]
    [SerializeField, Range(0f, 1f)] private float dimDepth = 0.85f;
    [Tooltip("How many times a second the character dims and comes back.")]
    [SerializeField, Min(0.05f)] private float dimSpeed = 1.2f;
    [Tooltip("Random stutter on top of the smooth dimming, like a failing battery. 0 = smooth.")]
    [SerializeField, Range(0f, 1f)] private float dimFlicker = 0.25f;

    [Header("At 0 health")]
    [Tooltip("The character turns black.")]
    [SerializeField] private bool blackOutAtZero = true;
    [Tooltip("Seconds it takes to turn black. 0 = instantly.")]
    [SerializeField, Min(0f)] private float blackoutTime = 0.4f;
    [Tooltip("The player's colour glow (PlayerGlow) switches off.")]
    [SerializeField] private bool glowOffAtZero = true;
    [Tooltip("The character tips over.")]
    [SerializeField] private bool fallOverAtZero = true;
    [Tooltip("How far it tips, in degrees. Positive tips one way, negative the other (90 = flat on its side).")]
    [SerializeField] private float fallAngle = 90f;
    [Tooltip("Seconds the fall takes.")]
    [SerializeField, Min(0f)] private float fallTime = 0.35f;
    [Tooltip("What tips over. Leave EMPTY for the character's picture. Careful: if the picture is on the same object as the collider, the collider tips too. To avoid that, put the picture on a child and drag the child here.")]
    [SerializeField] private Transform fallTarget;
    [Tooltip("The player can no longer move or act while at 0 health (it goes back to normal if they are healed or respawn).")]
    [SerializeField] private bool freezeAtZero = true;

    // ------------------------------------------------------------ state

    private HealthComponent health;
    private PlayerGlow glow;
    private Rigidbody2D fallBody;

    private ParticleSystem sparks, smoke;
    private ParticleSystemRenderer sparksRenderer, smokeRenderer;
    private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();   // materials and pictures made here, cleaned up in OnDestroy
    private bool particlesFailed;

    private int currentLevel = -1;
    private bool settingsDirty = true;
    private bool hasBeenAlive;   // a player that has never been seen alive is not "dead" (health may simply not be set up yet)
    private float darkness, dimTime, fallProgress, appliedAngle, noiseSeed;
    private PlayerInputSource lockedInput;

    /// <summary>0 = the character looks normal, 1 = pitch black (what this script hands to PlayerGlow).</summary>
    public float Darkness { get { return darkness; } }

    // ------------------------------------------------------------ setup

    private void Awake()
    {
        health = GetComponent<HealthComponent>();
        if (health == null) health = GetComponentInParent<HealthComponent>();
        glow = GetComponent<PlayerGlow>();

        if (sprite == null) sprite = GetComponent<SpriteRenderer>();
        if (sprite == null) sprite = GetComponentInChildren<SpriteRenderer>();

        if (health == null || sprite == null)
        {
            Debug.LogWarning("PlayerDamageEffects on '" + name + "': it needs a health component (Health / CoopHealth) and a SpriteRenderer, and found " +
                             (health == null ? "no health component" : "no SpriteRenderer") + ". These effects are switched off until that is fixed.", this);
            enabled = false;
            return;
        }

        if (fallTarget == null) fallTarget = sprite.transform;
        fallBody = fallTarget.GetComponent<Rigidbody2D>();
        noiseSeed = Random.value * 100f;
    }

    private void OnValidate()
    {
        settingsDirty = true;   // edits in the Inspector while playing are picked up on the next frame
    }

    private void OnDisable()
    {
        // Switched off (or the player was): put everything back and leave nothing behind.
        if (glow != null)
        {
            glow.Darkness = 0f;
            glow.GlowSuppressed = false;
        }
        if (appliedAngle != 0f)
        {
            SetAngle(0f);
            appliedAngle = 0f;
        }
        SetFrozen(false);
        darkness = 0f;
        dimTime = 0f;
        fallProgress = 0f;
        currentLevel = -1;
        SetRates(0f, 0f);
    }

    private void OnDestroy()
    {
        if (sparks != null) Destroy(sparks.gameObject);
        if (smoke != null) Destroy(smoke.gameObject);
        for (int i = 0; i < owned.Count; i++)
        {
            if (owned[i] != null) Destroy(owned[i]);
        }
    }

    // ------------------------------------------------------------ running

    private void Update()
    {
        bool alive = health.IsAlive;
        if (alive) hasBeenAlive = true;
        bool dead = hasBeenAlive && !alive;

        float max = health.MaxHealth;
        float left = max > 0f ? Mathf.Clamp01(health.CurrentHealth / max) : 0f;

        bool deadFx = effectsOn && dead;

        // 1. Which level are the sparks and smoke at?
        int level = 0;
        if (effectsOn && alive)
        {
            if (left <= sparksBelow) level = 1;
            if (left <= smokeBelow) level = 2;
            if (left <= heavyBelow) level = 3;
        }
        if (level != currentLevel || settingsDirty)
        {
            currentLevel = level;
            settingsDirty = false;
            ApplyLevel(level);
        }

        // 2. How dark is the character? (PlayerGlow draws it.)
        bool dimming = effectsOn && alive && dimOn && left <= dimBelow;
        if (deadFx && blackOutAtZero)
        {
            darkness = blackoutTime > 0f ? Mathf.MoveTowards(darkness, 1f, Time.deltaTime / blackoutTime) : 1f;
        }
        else if (dimming)
        {
            dimTime += Time.deltaTime;
            darkness = DimAmount();
        }
        else
        {
            darkness = 0f;
        }
        if (!dimming) dimTime = 0f;   // every dimming spell starts from normal and fades down

        if (glow != null)
        {
            glow.Darkness = darkness;
            glow.GlowSuppressed = deadFx && glowOffAtZero;
        }

        // 3. Fall over at 0 (and back up if the player is healed or respawns).
        if (deadFx && fallOverAtZero)
        {
            fallProgress = fallTime > 0f ? Mathf.MoveTowards(fallProgress, 1f, Time.deltaTime / fallTime) : 1f;
        }
        else
        {
            fallProgress = 0f;
        }
        float angle = fallAngle * fallProgress * fallProgress;   // speeds up like a real tip-over
        if (!Mathf.Approximately(angle, appliedAngle))
        {
            SetAngle(angle);
            appliedAngle = angle;
        }

        // 4. Stand still at 0.
        SetFrozen(deadFx && freezeAtZero);

        // 5. Keep the emitters on the character.
        if (sparks != null) PlaceParticles();
    }

    // Smooth in-and-out: starts at normal, reaches Dim Depth, comes back. Flicker adds random stutter on top.
    private float DimAmount()
    {
        float pulse = 0.5f - 0.5f * Mathf.Cos(dimTime * dimSpeed * Mathf.PI * 2f);
        pulse = Mathf.Clamp01(pulse + (Mathf.PerlinNoise(Time.time * 18f, noiseSeed) - 0.5f) * dimFlicker * 2f);
        return dimDepth * pulse;
    }

    // ------------------------------------------------------------ fall over and freeze

    private void SetAngle(float z)
    {
        if (fallTarget == null) return;

        // A Rigidbody2D rewrites its object's rotation every step, so tip the body itself, not just the transform.
        if (fallBody != null) fallBody.rotation = z;
        else fallTarget.localRotation = Quaternion.Euler(0f, 0f, z);
    }

    private void SetFrozen(bool freeze)
    {
        if (freeze)
        {
            if (lockedInput != null) return;
            PlayerInputSource source = PlayerInputSource.For(gameObject);
            if (source == null) return;
            source.Locked = true;
            lockedInput = source;
        }
        else if (lockedInput != null)
        {
            lockedInput.Locked = false;
            lockedInput = null;
        }
    }

    // ------------------------------------------------------------ sparks and smoke

    private void ApplyLevel(int level)
    {
        bool sparkActive = sparksOn && level >= 1;
        bool smokeActive = smokeOn && level >= 2;

        // The particle objects are only made the first time a player actually needs them.
        if ((sparkActive || smokeActive) && sparks == null && !particlesFailed) CreateParticles();
        if (sparks == null) return;

        SparkLevel sl = level >= 3 ? sparksLots : (level == 2 ? sparksMore : sparksFew);
        ConfigureSparks(sparkActive ? sl : null);
        ConfigureSmoke(smokeActive ? (level >= 3 ? blackSmoke : whiteSmoke) : null);
    }

    private void SetRates(float sparkRate, float smokeRate)
    {
        if (sparks != null)
        {
            ParticleSystem.EmissionModule e = sparks.emission;
            e.rateOverTime = sparkRate;
        }
        if (smoke != null)
        {
            ParticleSystem.EmissionModule e = smoke.emission;
            e.rateOverTime = smokeRate;
        }
    }

    private void ConfigureSparks(SparkLevel s)
    {
        ParticleSystem.EmissionModule emission = sparks.emission;
        if (s == null)
        {
            emission.rateOverTime = 0f;
            return;
        }

        ParticleSystem.MainModule main = sparks.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(s.lifetime * 0.7f, s.lifetime * 1.3f);
        main.startSize = Between(s.minSize, s.maxSize);
        main.startSpeed = Between(s.minSpeed, s.maxSpeed);
        emission.rateOverTime = s.perSecond;

        ParticleSystem.ShapeModule shape = sparks.shape;
        shape.radius = Mathf.Max(0.01f, sparkAreaRadius);

        // The picture is white, so the colour comes entirely from this: start colour burning down to the end colour, then fading out.
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(sparkStartColor, 0f), new GradientColorKey(sparkEndColor, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule col = sparks.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(g);

        if (!sparks.isPlaying) sparks.Play();
    }

    private void ConfigureSmoke(SmokeLevel s)
    {
        ParticleSystem.EmissionModule emission = smoke.emission;
        if (s == null)
        {
            emission.rateOverTime = 0f;
            return;
        }

        ParticleSystem.MainModule main = smoke.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(s.lifetime * 0.8f, s.lifetime * 1.2f);
        main.startSize = Between(s.minSize, s.maxSize);
        main.startSpeed = 0f;
        main.startColor = s.color;
        emission.rateOverTime = s.perSecond;

        ParticleSystem.VelocityOverLifetimeModule vel = smoke.velocityOverLifetime;
        vel.enabled = true;
        vel.space = ParticleSystemSimulationSpace.World;
        vel.x = 0f;
        vel.y = s.riseSpeed;
        vel.z = 0f;

        ParticleSystem.SizeOverLifetimeModule size = smoke.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, Mathf.Max(1f, s.growth)));

        if (!smoke.isPlaying) smoke.Play();
    }

    private static ParticleSystem.MinMaxCurve Between(float a, float b)
    {
        return new ParticleSystem.MinMaxCurve(Mathf.Min(a, b), Mathf.Max(a, b));
    }

    private void PlaceParticles()
    {
        // The middle of the character's current picture (not its pivot, which may be at the feet).
        Sprite shape = sprite.sprite;
        Vector3 centre = shape != null ? shape.bounds.center : Vector3.zero;
        if (sprite.flipX) centre.x = -centre.x;
        if (sprite.flipY) centre.y = -centre.y;
        centre.z = 0f;

        sparks.transform.localPosition = centre;
        smoke.transform.localPosition = centre + new Vector3(smokeOffset.x, smokeOffset.y, 0f);

        sparksRenderer.sortingLayerID = sprite.sortingLayerID;
        sparksRenderer.sortingOrder = sprite.sortingOrder + sparksSortingOffset;
        smokeRenderer.sortingLayerID = sprite.sortingLayerID;
        smokeRenderer.sortingOrder = sprite.sortingOrder + smokeSortingOffset;
    }

    private void CreateParticles()
    {
        Material sparkMaterial = MakeMaterial(MakeTexture(2, false));   // square sparks: they look right on pixel art
        Material smokeMaterial = MakeMaterial(MakeTexture(8, true));    // round puffs
        if (sparkMaterial == null || smokeMaterial == null)
        {
            particlesFailed = true;
            return;
        }

        sparks = MakeSystem("Damage Sparks", sparkMaterial, 120, 0.25f, out sparksRenderer);
        smoke = MakeSystem("Damage Smoke", smokeMaterial, 60, 0.08f, out smokeRenderer);

        // Sparks shrink as they burn out. (Their colour is set in ConfigureSparks, the smoke's in ConfigureSmoke.)
        ParticleSystem.MainModule sparkMain = sparks.main;
        sparkMain.startColor = Color.white;
        ParticleSystem.SizeOverLifetimeModule sparkSize = sparks.sizeOverLifetime;
        sparkSize.enabled = true;
        sparkSize.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.25f));

        // Smoke fades in quickly and fades out slowly.
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0f, 1f) });
        ParticleSystem.ColorOverLifetimeModule smokeColor = smoke.colorOverLifetime;
        smokeColor.enabled = true;
        smokeColor.color = new ParticleSystem.MinMaxGradient(g);
    }

    private ParticleSystem MakeSystem(string objectName, Material material, int maxParticles, float radius, out ParticleSystemRenderer rend)
    {
        GameObject go = new GameObject(objectName);
        go.layer = sprite.gameObject.layer;   // same camera layer, so split-screen cameras draw it too
        go.transform.SetParent(sprite.transform, false);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);   // it starts playing on its own: stop it before changing settings

        ParticleSystem.MainModule main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;     // sparks and smoke stay where they left, the character walks on
        main.scalingMode = ParticleSystemScalingMode.Local;
        main.gravityModifier = 0f;
        main.maxParticles = maxParticles;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = 0f;

        // A flat circle in the XY plane: particles appear anywhere inside it and (for sparks) fly outward.
        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = radius;
        shape.radiusThickness = 1f;

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
            Debug.LogWarning("PlayerDamageEffects on '" + name + "': the Sprites/Default shader was not found, so no sparks or smoke can be drawn. " +
                             "Drag a particle material into Particle Material.", this);
            return null;
        }

        Material m = new Material(shader);
        m.mainTexture = picture;
        owned.Add(m);
        return m;
    }

    // A tiny white picture, with hard pixel edges: a square, or a round blob.
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
}
