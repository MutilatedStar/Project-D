using System.Collections.Generic;
using UnityEngine;

namespace Cainos.PixelArtTopDown_Basic
{
    /// <summary>
    /// A hole in the floor: anything standing in this area drops down one or more levels. A trapdoor, a pit, a bridge that gives way, a crumbling ledge.
    /// It works on the player (TopDownJump) and on everything the TopDownLevelWorld lets fall (TopDownFaller), because both answer to ILevelFaller.
    ///
    /// SETUP: put it on an object with a Collider2D set to Is Trigger. Put that object on the PHYSICS LAYER of the level the hole is in (the same layer as that level's walls),
    /// so only things standing on that level touch it. "From Level" then says the same thing in numbers. Switch the collider off, or this script, to close the hole.
    ///
    /// A player who is jumping when he is over the hole does not fall until he lands (the jump refuses to be dropped in the air), and then falls if he is still over it.
    /// The thing must really be on a level the ladder knows (see TopDownLevelWorld).
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider2D))]
    public class TopDownDropZone : MonoBehaviour
    {
        [Header("Who drops")]
        [Tooltip("The level this hole is in the floor of: things standing on exactly this level drop. 1 = the first raised level.")]
        [SerializeField, Min(1)] private int fromLevel = 1;
        [Tooltip("ON: things on any level at or above From Level drop too (a hole in a roof). Each one drops by Drop Levels.")]
        [SerializeField] private bool includeHigherLevels = false;
        [Tooltip("Players (anything with a TopDownJump) drop.")]
        [SerializeField] private bool affectPlayers = true;
        [Tooltip("Everything else that falls (foes, crates, items) drops.")]
        [SerializeField] private bool affectOthers = true;

        [Header("The drop")]
        [Tooltip("How many levels they drop. 0 = all the way to the ground.")]
        [SerializeField, Min(0)] private int dropLevels = 1;
        [Tooltip("Seconds something must stand in the hole before it drops, so a thing that only clips the edge does not.")]
        [SerializeField, Min(0f)] private float delay = 0.15f;

        // When each thing came into the hole.
        private readonly Dictionary<ILevelFaller, float> entered = new Dictionary<ILevelFaller, float>();

        private void Reset()
        {
            Collider2D c = GetComponent<Collider2D>();
            if (c != null) c.isTrigger = true;
        }

        private void Awake()
        {
            Collider2D c = GetComponent<Collider2D>();
            if (c != null && !c.isTrigger)
            {
                Debug.LogWarning("TopDownDropZone on '" + name + "': its Collider2D is not a trigger, so nothing will drop. Tick Is Trigger on the collider.", this);
            }
        }

        private void OnDisable()
        {
            entered.Clear();
        }

        private void OnTriggerStay2D(Collider2D other)
        {
            ILevelFaller faller = FallerOf(other);
            if (faller == null || !Wants(faller)) return;

            float since;
            if (!entered.TryGetValue(faller, out since))
            {
                since = Time.time;
                entered[faller] = since;
            }
            if (Time.time - since < delay) return;

            int target = dropLevels > 0 ? Mathf.Max(0, faller.Level - dropLevels) : 0;
            if (faller.FallTo(target)) entered.Remove(faller);   // refused (in the air, or busy): tried again on the next step
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            ILevelFaller faller = FallerOf(other);
            if (faller != null) entered.Remove(faller);
        }

        private bool Wants(ILevelFaller faller)
        {
            bool isPlayer = faller is TopDownJump;
            if (isPlayer ? !affectPlayers : !affectOthers) return false;

            int level = faller.Level;
            return includeHigherLevels ? level >= fromLevel : level == fromLevel;
        }

        private static ILevelFaller FallerOf(Collider2D other)
        {
            Rigidbody2D rb = other.attachedRigidbody;
            ILevelFaller faller = rb != null ? rb.GetComponent<ILevelFaller>() : other.GetComponent<ILevelFaller>();
            if (faller == null) faller = other.GetComponentInParent<ILevelFaller>();
            return faller;
        }
    }
}
