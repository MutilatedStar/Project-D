using UnityEngine;

/// <summary>
/// Optional. Put this on a trap arrow (Projectile) or a laser (Beam) so the Bubble Shield definitely stops it.
/// The shield also looks for arrows and lasers by layer or by name (see its settings), but this is the sure way.
///
/// Projectile: the shield destroys it when it touches the bubble (or just makes it harmless if Destroy On Block is off). It also sends
///             "OnShieldBlocked" to the object, so your own arrow script can play a pop effect: void OnShieldBlocked() { ... }
/// Beam:       while the bubble is up the beam's colliders can't touch the player. For a laser that damages with a raycast instead of a collider,
///             also ask BubbleShieldTool.SegmentBlocked(from, to, out stopPoint) before it hurts anyone and shorten the beam to stopPoint.
/// </summary>
public class ShieldBlockable : MonoBehaviour
{
    public enum Kind { Projectile, Beam }

    public Kind kind = Kind.Projectile;

    [Tooltip("Projectile only: destroy the arrow when the shield blocks it. OFF: it is only made harmless and your own script decides what happens.")]
    public bool destroyOnBlock = true;
}
