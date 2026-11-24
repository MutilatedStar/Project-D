using UnityEngine;

/// <summary>
/// Put this on a crate, block, statue... that the Pulse Beam may pick up and the Grapple Arm may reel in.
/// (Instead of adding it to each object you can put them all on one layer and tick that layer in SwordController's Interactable Mask.)
/// It works with or without a Rigidbody2D. While a tool holds the object it is turned kinematic and does not push the player,
/// and it goes back to normal when let go.
/// </summary>
[DisallowMultipleComponent]
public class ToolInteractable : MonoBehaviour
{
    [Tooltip("The Pulse Beam can pick this up and move it closer and further away.")]
    public bool allowPulseBeam = true;
    [Tooltip("The Grapple Arm can reel this in toward the player.")]
    public bool allowGrapple = true;

    /// <summary>True while a tool is holding it.</summary>
    public bool IsHeld { get; private set; }
    /// <summary>The player holding it, or null.</summary>
    public SwordController HeldBy { get; private set; }

    /// <summary>Raised when a tool picks it up / lets it go. Hook sounds or a pressure plate check to these.</summary>
    public event System.Action<SwordController> Grabbed;
    public event System.Action<SwordController> Released;

    internal void NotifyGrabbed(SwordController by)
    {
        IsHeld = true;
        HeldBy = by;
        if (Grabbed != null) Grabbed(by);
    }

    internal void NotifyReleased(SwordController by)
    {
        IsHeld = false;
        HeldBy = null;
        if (Released != null) Released(by);
    }
}
