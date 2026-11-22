using UnityEngine;

namespace Cainos.PixelArtTopDown_Basic
{
    /// <summary>
    /// The small switch to put on ONE object (a foe, a crate, a pick-up, the player) when the world's settings are not what you want for it.
    /// It is optional: everything works without it, because TopDownLevelWorld decides for everything that has none.
    ///
    ///   Falls = Follow World   do what the TopDownLevelWorld says (with Mode = MarkedOnly, having this script is what marks the object).
    ///   Falls = Yes            this object falls, whatever the world's mode says. Use it for things the world does not find by itself
    ///                          (an item with no Rigidbody2D, or anything while the world is in Off mode).
    ///   Falls = No             this object never falls (a boss, a flying foe, a moving platform).
    ///
    /// The numbers below change the world's numbers for this object only. -1 (or Follow World) means "use the world's". Whatever you leave alone
    /// still comes from the rule that matches the object, and then from the world's Defaults.
    ///
    /// Works on the player too: a TopDownJump with "Use World Settings" ticked reads this script.
    /// For things that are not the player it also lists what to pause and what to make untouchable while the object is falling.
    /// </summary>
    [DisallowMultipleComponent]
    public class TopDownLevelToggle : MonoBehaviour
    {
        [Header("Does this object fall?")]
        [Tooltip("Follow World = what the TopDownLevelWorld says. Yes = it falls whatever the world says. No = it never falls.")]
        [SerializeField] private FallChoice falls = FallChoice.FollowWorld;

        [Header("Numbers for just this object (-1 or Follow World = use the world's)")]
        [Tooltip("Seconds one level takes to fall from rest. -1 = the world's.")]
        [SerializeField] private float fallSecondsPerLevel = -1f;
        [Tooltip("Does landing hurt? Follow World = the world's choice.")]
        [SerializeField] private FallChoice fallDamage = FallChoice.FollowWorld;
        [Tooltip("Damage for every level fallen past the safe ones. -1 = the world's.")]
        [SerializeField] private float damagePerLevel = -1f;
        [Tooltip("Levels that can be fallen without damage. -1 = the world's.")]
        [SerializeField] private int safeFallLevels = -1;
        [Tooltip("How far the sprite is drawn higher for each level (world units). -1 = the world's.")]
        [SerializeField] private float heightPerLevel = -1f;

        [Header("While this object falls (not used by the player)")]
        [Tooltip("Scripts to switch off until it has landed (the foe's AI, an attack script), so it cannot act while it is in the air.")]
        [SerializeField] private Behaviour[] pauseWhileFalling = new Behaviour[0];
        [Tooltip("OPTIONAL extra. TRIGGER colliders to switch off in the air (the hurt box a hit touches). Things with a CoopHealth are already safe in the air.")]
        [SerializeField] private Collider2D[] hitBoxes = new Collider2D[0];

        [Header("Sounds (optional, not used by the player)")]
        [Tooltip("Put this AudioSource on its OWN empty child object with a SoundSwitch on it, so the Sound On/Off setting controls it.")]
        [SerializeField] private AudioSource soundSource;
        [Tooltip("Played when the fall starts.")]
        [SerializeField] private AudioClip fallClip;
        [Tooltip("Played when it lands.")]
        [SerializeField] private AudioClip landClip;

        // ------------------------------------------------------------ what the world and the faller read

        public FallChoice Falls { get { return falls; } }
        public Behaviour[] PauseWhileFalling { get { return pauseWhileFalling; } }
        public Collider2D[] HitBoxes { get { return hitBoxes; } }
        public AudioSource SoundSource { get { return soundSource; } }
        public AudioClip FallClip { get { return fallClip; } }
        public AudioClip LandClip { get { return landClip; } }

        /// <summary>The given numbers with this object's changes on top. A copy: the world's own numbers are never touched.</summary>
        public FallSettings ApplyTo(FallSettings baseSettings)
        {
            FallSettings result = baseSettings != null ? baseSettings.Clone() : new FallSettings();

            if (fallSecondsPerLevel > 0f) result.fallSecondsPerLevel = Mathf.Max(0.05f, fallSecondsPerLevel);

            if (fallDamage == FallChoice.Yes) result.fallDamage = true;
            else if (fallDamage == FallChoice.No) result.fallDamage = false;

            if (damagePerLevel >= 0f) result.damagePerLevel = damagePerLevel;
            if (safeFallLevels >= 0) result.safeFallLevels = safeFallLevels;
            if (heightPerLevel >= 0f) result.heightPerLevel = heightPerLevel;
            return result;
        }

        // Hands this object to the world, so Yes (and MarkedOnly) work at once, without waiting for the world's next look.
        // A player with a TopDownJump is skipped by the world: the jump reads this script itself.
        private void OnEnable()
        {
            if (falls == FallChoice.No) return;

            TopDownLevelWorld world = TopDownLevelWorld.Active;
            if (world != null) world.Adopt(gameObject);
        }
    }
}
