using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cainos.PixelArtTopDown_Basic
{
    /// <summary>
    /// Anything that can be dropped to a lower level: the player's TopDownJump and every TopDownFaller.
    /// TopDownLevelWorld.DropAllTo and TopDownDropZone talk to this, so they work on the player and on everything else the same way.
    /// </summary>
    public interface ILevelFaller
    {
        /// <summary>0 = the ground, 1 = the first raised level, 2 = the second...</summary>
        int Level { get; }

        /// <summary>True while it is jumping or falling.</summary>
        bool IsInAir { get; }

        /// <summary>Drops it to <paramref name="level"/> as a fall. Returns false when there is nothing to fall to, or it is busy in the air.</summary>
        bool FallTo(int level);
    }

    /// <summary>
    /// How the TopDownLevelWorld decides WHICH things fall.
    ///   Everything  every moving thing (a Rigidbody2D that is not Static) falls, with no setup. Rules with Falls switched off and a toggle set to No are the exceptions.
    ///   Rules       only the things that match a rule in the Rules list fall (by tag, physics layer, sorting layer or script name).
    ///   MarkedOnly  only things that carry a TopDownLevelToggle fall. Nothing else is touched.
    ///   Off         nothing falls on its own. A toggle set to Yes still makes its object fall.
    /// </summary>
    public enum LevelWorldMode
    {
        Everything,
        Rules,
        MarkedOnly,
        Off
    }

    /// <summary>The three-way choice on a toggle: do what the world says, or force it on or off for one object.</summary>
    public enum FallChoice
    {
        FollowWorld,
        Yes,
        No
    }

    /// <summary>One step of the ladder: the layers a thing is moved to, and what holds a thing up on that level.</summary>
    [Serializable]
    public class WorldLevel
    {
        [Tooltip("Just a label for you.")]
        public string name = "Level";

        [Tooltip("Physics layer NAME of this level (the layer that collides with this level's walls). Empty = the same as the level below.")]
        public string physicsLayer = "";

        [Tooltip("Sorting layer NAME of this level. Empty = the same as the level below.")]
        public string sortingLayer = "";

        [Tooltip("What holds a thing up while it stands on this level: the colliders (platform, bridge, crate top...) that count as floor here. " +
                 "A thing that is NOT over one of them falls to the level below. EMPTY = this level counts as solid floor everywhere, so nothing falls off it on its own " +
                 "(drop zones, code and the player's own jump rules can still drop things). Ignored for the ground level.")]
        public JumpColliderFilter surface = new JumpColliderFilter();
    }

    /// <summary>
    /// Every number of the fall. The world has one set for everything (Defaults), a rule can have its own, and a TopDownLevelToggle can change a few of them
    /// for one object. These are plain numbers, so the same set drives the player's TopDownJump and every TopDownFaller.
    /// </summary>
    [Serializable]
    public class FallSettings
    {
        [Tooltip("ON: stepping off a raised level is a FALL: it drops level by level, its layers follow it down and a shadow shows where it will land. " +
                 "OFF: the old instant step down.")]
        public bool fallEnabled = true;

        [Tooltip("ON: a thing on a raised level falls as soon as nothing holds it up (see Surface on each level of the world). " +
                 "OFF: only code, drop zones and FallTo make it fall. Levels with an empty Surface never drop things on their own either way.")]
        public bool fallWhenUnsupported = true;

        [Tooltip("Seconds a thing may hang over the edge before it falls. A little delay stops flicker at the edge of a platform.")]
        [Min(0f)] public float unsupportedDelay = 0.1f;

        [Tooltip("Seconds one level takes to fall from rest. A longer fall speeds up, so two levels take about 1.4 times as long as one.")]
        [Min(0.05f)] public float fallSecondsPerLevel = 0.2f;

        [Tooltip("ON: landing from a fall hurts. Damage = (levels fallen - Safe Fall Levels) x Damage Per Level. OFF: falling never hurts.")]
        public bool fallDamage = false;

        [Tooltip("Damage for every level fallen past the safe ones. 5 is the same as one tick of the electric tiles.")]
        [Min(0f)] public float damagePerLevel = 5f;

        [Tooltip("This many levels can be fallen without damage. 0 = every level hurts.")]
        [Min(0)] public int safeFallLevels = 0;

        [Tooltip("ON: fall damage is scaled by the difficulty like the traps (Easy x0.5, Medium x1, Hard x2). The player always is. Foes and objects usually should not be.")]
        public bool scaleDamageByDifficulty = false;

        [Tooltip("For things WITHOUT a CoopHealth: the name of the method that takes the damage, called with SendMessage (the health system's is ApplyDamage). " +
                 "Things with a CoopHealth are hurt through it directly.")]
        public string damageMessage = "ApplyDamage";

        [Tooltip("How far the sprite is drawn higher for each level (world units). 0 = the layer changes but the sprite stays level with the ground. " +
                 "Use the same number as the player's Height Per Level so everything sits at the same height.")]
        [Min(0f)] public float heightPerLevel = 0f;

        [Tooltip("A landing that is blocked (the spot below is inside something solid) waits this long, then comes down anyway.")]
        [Min(0.1f)] public float blockedLandingSeconds = 1f;

        [Tooltip("Show a shadow on the level the thing will land on while it falls.")]
        public bool showShadow = true;

        [Tooltip("ON: the shadow is sized from the thing's sprite. OFF: Shadow Size is used.")]
        public bool fitShadowToSprite = true;

        [Tooltip("Shadow size in world units, when Fit Shadow To Sprite is OFF.")]
        public Vector2 shadowSize = new Vector2(0.8f, 0.4f);

        [Tooltip("How dark the shadow is.")]
        [Range(0f, 1f)] public float shadowOpacity = 0.35f;

        [Tooltip("How small the shadow is at the start of the fall (1 = it does not change). It grows to full size at the landing.")]
        [Range(0.2f, 1f)] public float shadowShrink = 0.6f;

        /// <summary>A copy, so a toggle can change numbers for one object without touching the world's.</summary>
        public FallSettings Clone()
        {
            return (FallSettings)MemberwiseClone();
        }
    }

    /// <summary>A rule of the world: which things it matches, whether they fall, and (optionally) numbers of their own.</summary>
    [Serializable]
    public class FallRule
    {
        [Tooltip("Just a label for you.")]
        public string name = "Rule";

        [Tooltip("Switch a rule off without deleting it.")]
        public bool enabled = true;

        [Tooltip("ON: the things this rule matches fall. OFF: the things this rule matches NEVER fall (an exception list: projectiles, moving platforms, a boss...). " +
                 "The first rule that matches a thing decides.")]
        public bool falls = true;

        [Tooltip("A thing matches when its tag is one of these.")]
        public string[] tags = new string[0];

        [Tooltip("A thing matches when it is on a ticked physics layer.")]
        public LayerMask physicsLayers;

        [Tooltip("Sorting layer NAMES. A thing matches when its sprite is on one of them.")]
        public string[] sortingLayers = new string[0];

        [Tooltip("Script (class) NAMES. A thing matches when it carries one of these scripts, or a script that inherits from one (type FoeController, no .cs). " +
                 "Names are matched as text, so a script that does not exist in your project is simply ignored.")]
        public string[] scriptNames = new string[0];

        [Tooltip("ON: the matched things use the world's Defaults. OFF: they use the Settings below.")]
        public bool useWorldSettings = true;

        [Tooltip("Used only when Use World Settings is OFF.")]
        public FallSettings settings = new FallSettings();

        public bool Matches(GameObject go)
        {
            if (!enabled || go == null) return false;

            if (physicsLayers.value != 0 && (physicsLayers.value & (1 << go.layer)) != 0) return true;

            if (tags != null && tags.Length > 0)
            {
                string tag = go.tag;
                for (int i = 0; i < tags.Length; i++)
                {
                    if (!string.IsNullOrEmpty(tags[i]) && tags[i] == tag) return true;
                }
            }

            if (sortingLayers != null && sortingLayers.Length > 0)
            {
                Renderer r = go.GetComponent<Renderer>();
                if (r == null) r = go.GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    for (int i = 0; i < sortingLayers.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(sortingLayers[i]) && sortingLayers[i] == r.sortingLayerName) return true;
                    }
                }
            }

            if (scriptNames != null && scriptNames.Length > 0)
            {
                MonoBehaviour[] parts = go.GetComponents<MonoBehaviour>();
                for (int p = 0; p < parts.Length; p++)
                {
                    if (parts[p] == null) continue;   // a script that is missing on the object

                    for (Type t = parts[p].GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                    {
                        for (int n = 0; n < scriptNames.Length; n++)
                        {
                            if (!string.IsNullOrEmpty(scriptNames[n]) && t.Name == scriptNames[n]) return true;
                        }
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The one place that says how everything in the scene falls down a level. Put ONE in the scene. If there is none, a default one is made when the game starts.
    ///
    /// THREE WAYS TO SET IT UP, and they work together:
    ///
    ///   1. UNIVERSAL       Mode = Everything. Every moving thing (a Rigidbody2D that is not Static) falls, with no setup at all. The numbers are "Defaults" below.
    ///   2. THIS SCRIPT     Mode = Rules. Only what matches a rule falls, and a rule can have numbers of its own (foes drop slower, crates take damage...).
    ///                      A rule with "Falls" OFF is an exception list. Rules match by tag, physics layer, sorting layer or script name, so a script
    ///                      you cannot edit still gets picked up.
    ///   3. A TOGGLE        Put a TopDownLevelToggle on any object: it can force that object to fall (Yes) or not to fall (No) whatever the mode says,
    ///                      and change a few numbers for it. With Mode = MarkedOnly the toggle is the only thing that makes anything fall.
    ///
    ///   Order of the numbers:  toggle (if it changes them)  >  the rule that matches  >  Defaults.
    ///   Order of "does it fall": toggle Yes / No  >  a matching rule's Falls switch  >  Mode.
    ///
    /// WHAT MAKES A THING FALL
    ///   * It stands on a raised level and nothing holds it up any more (the Surface of that level, below).
    ///   * It touches a TopDownDropZone (a hole, a trapdoor, a bridge that gives way).
    ///   * Code: TopDownFaller.FallTo / Drop, TopDownJump.FallTo, or TopDownLevelWorld.DropAllTo(level) for the whole scene at once.
    ///   The player (TopDownJump) joins in when its "Use World Settings" is ticked: it then takes the levels and the fall numbers from here too.
    ///
    /// THINGS WITHOUT A RIGIDBODY (an item on the floor, a pick-up) are not found by "Everything", because walls and tiles would be found too.
    /// Give them a TopDownLevelToggle set to Yes.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class TopDownLevelWorld : MonoBehaviour
    {
        [Header("Who falls")]
        [Tooltip("Everything = every moving thing, no setup. Rules = only what matches a rule below. MarkedOnly = only things with a TopDownLevelToggle. Off = nothing on its own.")]
        [SerializeField] private LevelWorldMode mode = LevelWorldMode.Everything;
        [Tooltip("Everything / Rules: also pick up kinematic bodies (foes that are moved by script usually are).")]
        [SerializeField] private bool includeKinematicBodies = true;
        [Tooltip("Seconds between looks for new things that appeared (spawned foes, crates...). 0 = look once, when the scene starts. " +
                 "Things you spawn in code can be handed over at once with Adopt.")]
        [SerializeField, Min(0f)] private float rescanSeconds = 1f;
        [Tooltip("The rules, first match wins. They matter in Rules mode, and in every mode for the exceptions (Falls OFF) and for their own numbers.")]
        [SerializeField] private FallRule[] rules =
        {
            new FallRule { name = "Players", scriptNames = new[] { "TopDownJump", "TopDownCharacterController" } },
            new FallRule { name = "Foes", scriptNames = new[] { "FoeController" } }
        };

        [Header("The levels (ground first)")]
        [Tooltip("Element 0 = the ground, element 1 = the first level up, and so on. Names must exist in Project Settings > Tags and Layers " +
                 "(physics layer and sorting layer are separate lists). A thing's level is read from its physics layer, then its sprite's sorting layer.")]
        [SerializeField] private WorldLevel[] levels =
        {
            new WorldLevel { name = "Ground", physicsLayer = "Layer 1", sortingLayer = "Layer 1" },
            new WorldLevel { name = "Level 1", physicsLayer = "Layer 2", sortingLayer = "Layer 2" },
            new WorldLevel { name = "Level 2", physicsLayer = "Layer 3", sortingLayer = "Layer 3" }
        };

        [Header("Defaults for everything that falls")]
        [SerializeField] private FallSettings defaults = new FallSettings();

        [Header("Debug")]
        [Tooltip("Prints a Console line for every thing that is picked up.")]
        [SerializeField] private bool logAdoptions = false;

        // ------------------------------------------------------------ the one in the scene

        private static TopDownLevelWorld active;
        private static bool shuttingDown;
        private static readonly List<ILevelFaller> fallers = new List<ILevelFaller>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            active = null;   // all of this survives between Play sessions when domain reload is switched off
            shuttingDown = false;
            fallers.Clear();
            Application.quitting -= HandleQuitting;
            Application.quitting += HandleQuitting;
        }

        private static void HandleQuitting()
        {
            shuttingDown = true;
        }

        /// <summary>
        /// The world of this scene. Found on first use; if the scene has none, one with the default settings is made.
        /// Null only in edit mode or while the game is closing.
        /// </summary>
        public static TopDownLevelWorld Active
        {
            get
            {
                if (active != null) return active;
                if (shuttingDown || !Application.isPlaying) return null;

                active = FindFirstObjectByType<TopDownLevelWorld>();
                if (active == null)
                {
                    GameObject go = new GameObject("TopDownLevelWorld");
                    active = go.AddComponent<TopDownLevelWorld>();
                    Debug.Log("TopDownLevelWorld: there is none in the scene, so one with the default settings was made. " +
                              "Add a TopDownLevelWorld to the scene to change them.");
                }
                active.EnsurePrepared();
                return active;
            }
        }

        /// <summary>Called by TopDownJump and TopDownFaller when they switch on, so DropAllTo can reach them.</summary>
        public static void Register(ILevelFaller faller)
        {
            if (faller != null && !fallers.Contains(faller)) fallers.Add(faller);
        }

        /// <summary>Called by TopDownJump and TopDownFaller when they switch off.</summary>
        public static void Unregister(ILevelFaller faller)
        {
            fallers.Remove(faller);
        }

        /// <summary>
        /// Drops everything that stands higher than <paramref name="level"/> down to it, the player included: a floor that collapses, a quake.
        /// Returns how many started to fall.
        /// </summary>
        public static int DropAllTo(int level)
        {
            int count = 0;
            for (int i = fallers.Count - 1; i >= 0; i--)
            {
                ILevelFaller faller = fallers[i];
                Component part = faller as Component;
                if (part == null) continue;   // destroyed

                if (faller.Level > level && faller.FallTo(level)) count++;
            }
            return count;
        }

        // ------------------------------------------------------------ levels

        private bool prepared;
        private int[] physicsIds;
        private int[] sortingIds;

        /// <summary>Number of levels, the ground included (always at least 1).</summary>
        public int LevelCount
        {
            get
            {
                EnsurePrepared();
                return levels.Length;
            }
        }

        /// <summary>The level at this index (0 = the ground), with its layer names and its surface.</summary>
        public WorldLevel LevelAt(int level)
        {
            EnsurePrepared();
            return levels[Mathf.Clamp(level, 0, levels.Length - 1)];
        }

        /// <summary>The physics layer a thing on this level is moved to.</summary>
        public int PhysicsLayerOf(int level)
        {
            EnsurePrepared();
            return physicsIds[Mathf.Clamp(level, 0, physicsIds.Length - 1)];
        }

        /// <summary>The sorting layer id a thing's sprites are moved to on this level.</summary>
        public int SortingLayerIdOf(int level)
        {
            EnsurePrepared();
            return sortingIds[Mathf.Clamp(level, 0, sortingIds.Length - 1)];
        }

        /// <summary>What holds a thing up on this level (never null). An empty filter means "solid everywhere".</summary>
        public JumpColliderFilter SurfaceOf(int level)
        {
            return LevelAt(level).surface;
        }

        /// <summary>The level that uses this physics layer, or -1. When several levels share one layer, the lowest wins.</summary>
        public int LevelOfPhysicsLayer(int physicsLayer)
        {
            EnsurePrepared();
            for (int i = 0; i < physicsIds.Length; i++)
            {
                if (physicsIds[i] == physicsLayer) return i;
            }
            return -1;
        }

        /// <summary>The level that uses this sorting layer id, or -1. When several levels share one layer, the lowest wins.</summary>
        public int LevelOfSortingLayer(int sortingLayerId)
        {
            EnsurePrepared();
            for (int i = 0; i < sortingIds.Length; i++)
            {
                if (sortingIds[i] == sortingLayerId) return i;
            }
            return -1;
        }

        /// <summary>Safe to call any time and any number of times: the layer names are turned into ids once.</summary>
        public void EnsurePrepared()
        {
            if (prepared) return;
            prepared = true;

            if (levels == null || levels.Length == 0) levels = new WorldLevel[] { new WorldLevel { name = "Ground" } };
            if (defaults == null) defaults = new FallSettings();

            int count = levels.Length;
            physicsIds = new int[count];
            sortingIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                if (levels[i] == null) levels[i] = new WorldLevel();
                WorldLevel level = levels[i];
                if (level.surface == null) level.surface = new JumpColliderFilter();

                physicsIds[i] = ResolvePhysicsLayer(level.physicsLayer, i > 0 ? physicsIds[i - 1] : 0, i);
                sortingIds[i] = ResolveSortingLayer(level.sortingLayer, i > 0 ? sortingIds[i - 1] : 0, i);
                level.surface.Prepare("TopDownLevelWorld level " + i, this);
            }
        }

        private int ResolvePhysicsLayer(string layerName, int fallback, int index)
        {
            if (string.IsNullOrEmpty(layerName)) return fallback;

            int layer = LayerMask.NameToLayer(layerName);
            if (layer >= 0) return layer;

            Debug.LogWarning("TopDownLevelWorld: level " + index + " names a physics layer \"" + layerName + "\" that does not exist " +
                             "(Project Settings > Tags and Layers), so it keeps the layer of the level below.", this);
            return fallback;
        }

        private int ResolveSortingLayer(string layerName, int fallback, int index)
        {
            if (string.IsNullOrEmpty(layerName)) return fallback;

            int id;
            if (TopDownJump.TryGetSortingLayerId(layerName, out id)) return id;

            Debug.LogWarning("TopDownLevelWorld: level " + index + " names a sorting layer \"" + layerName + "\" that does not exist " +
                             "(Project Settings > Tags and Layers), so it keeps the sorting layer of the level below.", this);
            return fallback;
        }

        // ------------------------------------------------------------ who falls, and with which numbers

        /// <summary>Does this object fall? Toggle Yes / No first, then a matching rule's Falls switch, then the mode.</summary>
        public bool ShouldFall(GameObject go, TopDownLevelToggle toggle)
        {
            FallChoice choice = toggle != null ? toggle.Falls : FallChoice.FollowWorld;
            if (choice == FallChoice.No) return false;
            if (choice == FallChoice.Yes) return true;

            if (mode == LevelWorldMode.Off) return false;

            FallRule rule = FindRule(go);
            if (rule != null && !rule.falls) return false;

            switch (mode)
            {
                case LevelWorldMode.Everything: return true;
                case LevelWorldMode.Rules: return rule != null;
                case LevelWorldMode.MarkedOnly: return toggle != null;
                default: return false;
            }
        }

        /// <summary>
        /// The numbers for this object: the toggle's changes on top of the matching rule's own numbers on top of the Defaults.
        /// Treat the result as read-only: it can be the world's own set.
        /// </summary>
        public FallSettings SettingsFor(GameObject go, TopDownLevelToggle toggle)
        {
            FallSettings result = defaults;

            FallRule rule = FindRule(go);
            if (rule != null && !rule.useWorldSettings && rule.settings != null) result = rule.settings;

            if (toggle != null) result = toggle.ApplyTo(result);
            return result;
        }

        /// <summary>The first enabled rule that matches this object, or null.</summary>
        public FallRule FindRule(GameObject go)
        {
            if (rules == null) return null;

            for (int i = 0; i < rules.Length; i++)
            {
                FallRule rule = rules[i];
                if (rule != null && rule.Matches(go)) return rule;
            }
            return null;
        }

        // ------------------------------------------------------------ picking things up

        private readonly HashSet<int> examined = new HashSet<int>();
        private float rescanTimer;

        /// <summary>
        /// Gives this object a TopDownFaller if the world says it should fall. Returns it, or null when it should not (or it is the player,
        /// whose TopDownJump handles its own levels). Call it for things you spawn in code to skip the wait for the next look.
        /// </summary>
        public TopDownFaller Adopt(GameObject go)
        {
            if (go == null) return null;
            if (go.GetComponent<TopDownJump>() != null) return null;   // the jump owns this one's levels; it reads this world's numbers itself

            TopDownFaller existing = go.GetComponent<TopDownFaller>();
            if (existing != null) return existing;

            TopDownLevelToggle toggle = go.GetComponent<TopDownLevelToggle>();
            if (!ShouldFall(go, toggle)) return null;

            TopDownFaller faller = go.AddComponent<TopDownFaller>();
            if (logAdoptions) Debug.Log("TopDownLevelWorld: '" + go.name + "' now falls.", go);
            return faller;
        }

        /// <summary>Looks for moving things that do not fall yet. Runs by itself (see Rescan Seconds).</summary>
        public void Scan()
        {
            Rigidbody2D[] bodies = FindObjectsByType<Rigidbody2D>(FindObjectsSortMode.None);
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody2D rb = bodies[i];
                if (rb == null) continue;

                GameObject go = rb.gameObject;
                if (!examined.Add(go.GetInstanceID())) continue;   // looked at before

                if (rb.bodyType == RigidbodyType2D.Static) continue;
                if (rb.bodyType == RigidbodyType2D.Kinematic && !includeKinematicBodies) continue;

                Adopt(go);
            }
        }

        private bool Scanning
        {
            get { return mode == LevelWorldMode.Everything || mode == LevelWorldMode.Rules; }
        }

        // ------------------------------------------------------------ setup

        private void Awake()
        {
            if (active != null && active != this)
            {
                Debug.LogWarning("TopDownLevelWorld on '" + name + "': there is already one in the scene ('" + active.name + "'). " +
                                 "Only that one is used; this one is switched off.", this);
                enabled = false;
                return;
            }

            active = this;
            EnsurePrepared();
        }

        private void Start()
        {
            if (Scanning) Scan();
            rescanTimer = rescanSeconds;
        }

        private void Update()
        {
            if (rescanSeconds <= 0f || !Scanning) return;

            rescanTimer -= Time.deltaTime;
            if (rescanTimer > 0f) return;

            rescanTimer = rescanSeconds;
            Scan();
        }

        private void OnDestroy()
        {
            if (active == this) active = null;
        }
    }
}
