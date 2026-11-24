using UnityEngine;

/// <summary>
/// Optional. Tells the Scanner Lens what an object is: a Trap shows red, a Collectable gets an arrow pointing at it,
/// Ignore keeps it out (use it when the lens guesses wrong).
/// You don't need this if the lens already finds your objects by layer or by name (see the Scanner Lens settings on SwordController).
/// </summary>
public class ScannerMarker : MonoBehaviour
{
    public enum Kind { Trap, Collectable, Ignore }

    [Tooltip("Trap: drawn red while the lens is on. Collectable: gets an arrow. Ignore: the lens leaves it alone.")]
    public Kind kind = Kind.Trap;
}
