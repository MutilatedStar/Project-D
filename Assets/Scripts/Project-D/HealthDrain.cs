using UnityEngine;

namespace Ilumisoft.HealthSystem
{
    /// <summary>
    /// Slowly drains the health of the HealthComponent on this object, like a battery running down.
    /// Any heal pauses the drain for a set time, then it picks back up.
    /// Works with Health or any other HealthComponent.
    /// </summary>
    [AddComponentMenu("Health System/Health Drain")]
    public class HealthDrain : MonoBehaviour
    {
        [SerializeField, Tooltip("The health to drain. Leave empty to use the HealthComponent on this object")]
        HealthComponent health;

        [Header("Drain")]
        [SerializeField, Tooltip("Turns the drain on or off. Can be changed while the game is running")]
        bool drainEnabled = false;

        [SerializeField, Min(0), Tooltip("How much health is lost every tick")]
        float drainAmount = 1f;

        [SerializeField, Min(0.05f), Tooltip("Seconds between ticks")]
        float tickInterval = 1f;

        [SerializeField, Min(0), Tooltip("How long the drain runs after being turned on, in seconds. 0 = keeps draining until turned off")]
        float drainDuration = 0f;

        [Header("Healing")]
        [SerializeField, Min(0), Tooltip("How many seconds the drain stops for after the player is healed")]
        float pauseAfterHeal = 5f;

        float tickTimer;
        float elapsed;        // time spent actually draining since it was turned on
        float pauseTimer;     // counts down after a heal
        bool wasEnabled;
        bool started;         // ignores the heal Health.Awake does when it sets the starting health

        public bool DrainEnabled { get { return drainEnabled; } set { drainEnabled = value; } }
        public bool IsPaused { get { return pauseTimer > 0f; } }
        public float PauseTimeRemaining { get { return Mathf.Max(0f, pauseTimer); } }

        private void Reset()
        {
            health = GetComponent<HealthComponent>();
        }

        private void Awake()
        {
            if (health == null)
            {
                health = GetComponent<HealthComponent>();
            }
        }

        private void OnEnable()
        {
            if (health != null)
            {
                health.OnHealthChanged += HandleHealthChanged;
            }
        }

        private void OnDisable()
        {
            if (health != null)
            {
                health.OnHealthChanged -= HandleHealthChanged;
            }
        }

        private void Start()
        {
            started = true;
        }

        private void HandleHealthChanged(float change)
        {
            // Only heals (positive changes) pause the drain. The drain's own ticks are negative.
            if (!started || change <= 0f) return;

            pauseTimer = pauseAfterHeal;
            tickTimer = 0f;   // the next tick comes a full interval after the pause ends
        }

        private void Update()
        {
            if (pauseTimer > 0f)
            {
                pauseTimer -= Time.deltaTime;
            }

            if (!drainEnabled)
            {
                wasEnabled = false;
                return;
            }

            // Just got turned on: start a fresh drain
            if (!wasEnabled)
            {
                wasEnabled = true;
                tickTimer = 0f;
                elapsed = 0f;
            }

            if (health == null || !health.IsAlive) return;

            // Healed recently, so no draining yet
            if (pauseTimer > 0f) return;

            elapsed += Time.deltaTime;

            if (drainDuration > 0f && elapsed >= drainDuration)
            {
                drainEnabled = false;   // time's up, the toggle switches itself back off
                return;
            }

            tickTimer += Time.deltaTime;

            while (tickTimer >= tickInterval)
            {
                tickTimer -= tickInterval;

                // Goes through ApplyDamage, so OnHealthChanged / OnHealthEmpty fire like normal damage
                health.ApplyDamage(drainAmount);

                if (!health.IsAlive) break;
            }
        }
    }
}
