using System;
using System.Collections.Generic;
using System.Reflection;
using Ilumisoft.HealthSystem;
using UnityEngine;

namespace Cainos.PixelArtTopDown_Basic
{
    /// <summary>One player's pick-up keys (any of them works).</summary>
    [Serializable]
    public class PickUpKeys
    {
        [Tooltip("Any of these keys picks up. Empty = this player has no pick-up key of their own.")]
        public KeyCode[] keys = new KeyCode[0];
    }

    /// <summary>
    /// Pick things up: press the pick-up key while standing over (or right next to) an object that has an Interactable script on it,
    /// and that script is asked to do its thing. Add it to the player prefab next to TopDownCharacterController, which hands over the
    /// input each frame (so the player cannot pick anything up while the controller is switched off by a pause, a menu or a floor change).
    ///
    /// KEYS    Player 1 uses Left Ctrl and Player 2 uses Right Ctrl by default (Space is the JUMP key now: a key that is also this player's Jump key never picks up) ("Keys By Player": element 0 = Player 1, element 1 = Player 2...).
    ///         Any other key works the same: type it into that player's list. "Use Interact Button" also lets the normal Interact action
    ///         pick up (E, / and the controller's East button by default): that is the only way a controller player can pick up, because the
    ///         extra keys are keyboard keys. Interact is also the Swap / revive button, so a downed partner and an object at the same
    ///         spot would answer to one press; untick it if that matters.
    ///
    /// THE INTERACTABLE  this script was written without seeing your Interactable, so it finds it by NAME and calls a method by NAME
    ///         ("Interactable Name" and "Method Name" below; the defaults are "Interactable" and "Interact"). The method may take no
    ///         arguments, or one: the player's GameObject, Transform, CoopHealth or this component. A method that returns false counts as
    ///         "nothing happened". If the method is not found, the Console lists the methods that Interactable does have: type the right
    ///         one into Method Name. Subclasses of Interactable count too.
    ///
    /// WHERE    the objects checked are those within "Reach" of the player's body, on the "Layers" ticked (triggers included). The nearest
    ///         one is used. The player cannot pick up while jumping or falling, and with "Same Layer Only" it can only pick up what is drawn
    ///         on its own sorting layer, so a player standing on a raised level cannot grab what lies on the ground below.
    /// </summary>
    [DisallowMultipleComponent]
    public class TopDownPickUp : MonoBehaviour
    {
        [Header("Keys")]
        [Tooltip("Element 0 = Player 1's keys, element 1 = Player 2's, and so on. The player uses the element that matches the player number " +
                 "the PlayerInputHub gave it. Type any key you like. Player 3 and 4 have no key until you add one (they can still use Interact).")]
        [SerializeField] private PickUpKeys[] keysByPlayer =
        {
            new PickUpKeys { keys = new[] { KeyCode.LeftControl } },
            new PickUpKeys { keys = new[] { KeyCode.RightControl } },
            new PickUpKeys(),
            new PickUpKeys()
        };
        [Tooltip("ON: the normal Interact action (E, / and the controller's East button by default) picks up as well. It is the only way a controller player " +
                 "can pick up. It is also the Swap / revive button.")]
        [SerializeField] private bool useInteractButton = true;

        [Header("What can be picked up")]
        [Tooltip("The NAME of your Interactable script (the class name). Subclasses count too.")]
        [SerializeField] private string interactableName = "Interactable";
        [Tooltip("The NAME of the method on it to call. It may take no arguments or one (the player's GameObject, Transform, CoopHealth or this component). " +
                 "If it is not found, the Console lists the methods the script has.")]
        [SerializeField] private string methodName = "Interact";
        [Tooltip("Physics layers that are searched. Triggers count. Keep this to the layers your pick-ups are on if the scene is busy.")]
        [SerializeField] private LayerMask layers = ~0;
        [Tooltip("How far from the middle of the player's body an object may be, in world units. 0.5 is about 'standing over it'.")]
        [SerializeField, Min(0.05f)] private float reach = 0.5f;
        [Tooltip("ON: only objects drawn on the player's own sorting layer can be picked up (a player on a raised level cannot reach what lies below). " +
                 "OFF: any layer. Objects with no sprite are never filtered out.")]
        [SerializeField] private bool sameLayerOnly = false;
        [Tooltip("Seconds after a pick-up before the next one is allowed.")]
        [SerializeField, Min(0f)] private float cooldown = 0.2f;

        [Header("Debug")]
        [Tooltip("Prints a Console line for every pick-up, with what was picked up and which method was called.")]
        [SerializeField] private bool logPickUps = true;

        /// <summary>Raised after an Interactable has been asked to act, with the Interactable.</summary>
        public event Action<Component> PickedUp;

        // ------------------------------------------------------------ state

        private sealed class Handler
        {
            public MethodInfo method;
            public ParameterInfo[] parameters;
        }

        private PlayerInputSource input;
        private TopDownJump jump;
        private CoopHealth health;
        private SpriteRenderer body;
        private Collider2D bodyCollider;
        private float cooldownLeft;

        private readonly List<Collider2D> hits = new List<Collider2D>(16);
        private readonly Dictionary<Type, bool> isInteractable = new Dictionary<Type, bool>();
        private readonly Dictionary<Type, Handler> handlers = new Dictionary<Type, Handler>();   // null entry = looked, nothing usable, already warned

        // ------------------------------------------------------------ setup

        private void Awake()
        {
            jump = GetComponent<TopDownJump>();
            health = GetComponent<CoopHealth>();
            body = GetComponent<SpriteRenderer>();

            Collider2D[] all = GetComponentsInChildren<Collider2D>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].isTrigger) continue;
                bodyCollider = all[i];
                break;
            }
        }

        private void Start()
        {
            input = PlayerInputSource.For(gameObject);
            if (input != null) WarnIfKeyIsShared(input);
        }

        // A pick-up key that is also another action's key does both on one press. PlayerInputSource only checks clashes between two
        // PLAYERS, so the clash inside one player is checked here (Interact is skipped: using it for pick-up is the point).
        private void WarnIfKeyIsShared(PlayerInputSource source)
        {
            KeyCode[] mine = OwnKeys(source);
            for (int a = 0; a < PlayerInputSource.ActionCount; a++)
            {
                PlayerAction action = (PlayerAction)a;
                if (action == PlayerAction.Interact || action == PlayerAction.Jump) continue;   // a Jump key is skipped by KeyPressed below

                KeyCode[] other = source.KeysFor(action);
                for (int k = 0; k < mine.Length; k++)
                {
                    if (Array.IndexOf(other, mine[k]) < 0) continue;

                    Debug.LogWarning("TopDownPickUp on '" + name + "' (Player " + source.playerNumber + "): the pick-up key " + mine[k] + " is also a " + action +
                                     " key, so one press does both. Give pick-up a key of its own in Keys By Player.", this);
                    return;   // one warning is enough
                }
            }
        }

        private KeyCode[] OwnKeys(PlayerInputSource source)
        {
            int index = Mathf.Clamp(source.playerNumber, 1, PlayerInputSource.MaxPlayers) - 1;
            if (keysByPlayer == null || index >= keysByPlayer.Length || keysByPlayer[index] == null || keysByPlayer[index].keys == null) return new KeyCode[0];
            return keysByPlayer[index].keys;
        }

        // ------------------------------------------------------------ input

        /// <summary>Called by TopDownCharacterController every frame it is running (not while paused, locked or on a stairs menu).</summary>
        public void Process(PlayerInputSource source)
        {
            input = source;

            if (cooldownLeft > 0f) cooldownLeft -= Time.deltaTime;
            if (cooldownLeft > 0f || !KeyPressed(source)) return;

            TryPickUp();
        }

        private bool KeyPressed(PlayerInputSource source)
        {
            if (useInteractButton && source.InteractPressed) return true;

            // The same gates PlayerInputSource puts on the keyboard: this player's keyboard is on, a text field is not being typed in,
            // and Player 2 and up only answer once a second player is really in the game.
            if (!source.KeyboardEnabled || KeyInput.IsTyping()) return false;
            if (source.playerNumber > 1 && !PlayerInputSource.TwoPlayers) return false;

            KeyCode[] keys = OwnKeys(source);
            KeyCode[] jumpKeys = source.KeysFor(PlayerAction.Jump);
            for (int i = 0; i < keys.Length; i++)
            {
                // Space used to be Player 1's pick-up key and may still be saved on the prefab. A key that jumps never also picks up.
                if (Array.IndexOf(jumpKeys, keys[i]) >= 0) continue;
                if (KeyInput.WasPressed(keys[i])) return true;
            }
            return false;
        }

        // ------------------------------------------------------------ picking up

        /// <summary>Picks up the nearest Interactable in reach, if there is one. Returns true when one was asked to act.</summary>
        public bool TryPickUp()
        {
            if (!isActiveAndEnabled) return false;
            if (health != null && !health.IsAlive) return false;
            if (jump != null && jump.IsInAir) return false;   // nothing is reached from the air

            Vector2 feet = FeetPoint;

            ContactFilter2D filter = new ContactFilter2D();
            filter.useTriggers = true;
            filter.SetLayerMask(layers);
            Physics2D.OverlapCircle(feet, reach, filter, hits);

            Component best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Count; i++)
            {
                Collider2D c = hits[i];
                if (c == null || c.transform.IsChildOf(transform)) continue;
                if (sameLayerOnly && !OnMyLayer(c)) continue;

                Component target = FindInteractable(c);
                if (target == null) continue;

                Vector2 closest = c.ClosestPoint(feet);
                float distance = (closest - feet).sqrMagnitude;
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = target;
            }

            if (best == null) return false;   // nothing in reach: the key press is simply ignored

            cooldownLeft = cooldown;
            if (!Invoke(best)) return false;

            if (logPickUps) Debug.Log("TopDownPickUp on '" + name + "': asked '" + best.name + "' (" + best.GetType().Name + "." + methodName + ") to act.", best);
            if (PickedUp != null) PickedUp(best);
            return true;
        }

        // Where the body is: the middle of the solid collider (the same spot the jump and the hazard tiles use).
        private Vector2 FeetPoint
        {
            get { return bodyCollider != null ? (Vector2)bodyCollider.bounds.center : (Vector2)transform.position; }
        }

        // The player's sorting layer changes with its level, so this follows the levels of TopDownJump without asking it.
        private bool OnMyLayer(Collider2D c)
        {
            if (body == null) return true;

            Renderer r = c.GetComponent<Renderer>();
            if (r == null) r = c.GetComponentInParent<Renderer>();
            return r == null || r.sortingLayerID == body.sortingLayerID;
        }

        // ------------------------------------------------------------ finding and calling the Interactable (by name)

        private Component FindInteractable(Collider2D c)
        {
            MonoBehaviour[] parts = c.GetComponentsInParent<MonoBehaviour>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null && IsInteractable(parts[i].GetType())) return parts[i];
            }
            return null;
        }

        // A type is an Interactable when it, or any class it inherits from, has the name set in Interactable Name.
        private bool IsInteractable(Type type)
        {
            bool known;
            if (isInteractable.TryGetValue(type, out known)) return known;

            known = false;
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (t.Name != interactableName) continue;
                known = true;
                break;
            }
            isInteractable[type] = known;
            return known;
        }

        private bool Invoke(Component target)
        {
            Type type = target.GetType();

            Handler handler;
            if (!handlers.TryGetValue(type, out handler))
            {
                handler = BuildHandler(type, target);
                handlers[type] = handler;
            }
            if (handler == null) return false;

            object[] args = new object[handler.parameters.Length];
            for (int i = 0; i < args.Length; i++) args[i] = ArgumentFor(handler.parameters[i].ParameterType);

            try
            {
                object result = handler.method.Invoke(target, args);
                return !(result is bool) || (bool)result;   // a method that answers false did nothing
            }
            catch (TargetInvocationException e)
            {
                Debug.LogException(e.InnerException != null ? e.InnerException : e, target);   // the Interactable's own error, shown as it is
                return false;
            }
        }

        // The method called Method Name that takes the most arguments this script can supply (no arguments is always fine).
        private Handler BuildHandler(Type type, Component target)
        {
            MethodInfo[] all = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo best = null;
            int bestCount = -1;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name != methodName) continue;

                ParameterInfo[] p = all[i].GetParameters();
                if (!CanSupply(p) || p.Length <= bestCount) continue;

                best = all[i];
                bestCount = p.Length;
            }

            if (best != null) return new Handler { method = best, parameters = best.GetParameters() };

            // Not found: say what IS there, so the right name can be typed into Method Name.
            List<string> names = new List<string>();
            MethodInfo[] declared = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            for (int i = 0; i < declared.Length; i++)
            {
                if (declared[i].IsSpecialName) continue;   // property getters and setters

                ParameterInfo[] p = declared[i].GetParameters();
                string[] types = new string[p.Length];
                for (int k = 0; k < p.Length; k++) types[k] = p[k].ParameterType.Name;
                names.Add(declared[i].Name + "(" + string.Join(", ", types) + ")");
            }

            Debug.LogWarning("TopDownPickUp on '" + name + "': '" + type.Name + "' has no method called \"" + methodName + "\" that takes nothing or the player " +
                             "(GameObject, Transform, CoopHealth or this component). Its public methods are: " +
                             (names.Count > 0 ? string.Join(", ", names.ToArray()) : "none") + ". Type the right name into Method Name.", target);
            return null;
        }

        private bool CanSupply(ParameterInfo[] parameters)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (ArgumentFor(parameters[i].ParameterType) == null) return false;
            }
            return true;
        }

        // What can be handed to a method that asks for this type: the player's GameObject, Transform, CoopHealth, this component or its input.
        private object ArgumentFor(Type wanted)
        {
            if (wanted.IsInstanceOfType(gameObject)) return gameObject;
            if (wanted.IsInstanceOfType(transform)) return transform;
            if (health != null && wanted.IsInstanceOfType(health)) return health;
            if (wanted.IsInstanceOfType(this)) return this;
            if (input != null && wanted.IsInstanceOfType(input)) return input;
            return null;
        }
    }
}
