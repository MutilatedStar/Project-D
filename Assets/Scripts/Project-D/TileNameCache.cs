using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TILE NAME HELPER (used by FloorManager; nothing to add to a scene).
///
/// FloorManager finds lava, water and electricity by looking for a word in the name of the tile under a player, or in the name of the sprite
/// that tile shows. The rule is: capitals, spaces, dashes and underscores don't matter, so "lava" is found in "Lava-16px_strip12_3".
///
/// That check runs every frame for every player, so the cleaned-up names are remembered here instead of being rebuilt each time
/// (building a new string every frame is what makes the garbage collector hitch).
///
///   Normalize(text)            the cleaned-up text: lower case, with spaces, dashes and underscores removed. Null becomes "".
///   NormalizeWanted(text)      the same, for the words you type in the Inspector (lava / water / electric names). Remembered.
///   NormalizedName(asset)      the cleaned-up name of a tile or a sprite (or any Unity asset). Remembered. Null becomes "".
///   Clear()                    forgets everything; only needed if you rename an asset while the game is running.
/// </summary>
public static class TileNameCache
{
    // A safety limit so the lists can never grow forever (a few hundred tiles and sprites is normal).
    private const int MaxRemembered = 4096;

    private static readonly Dictionary<UnityEngine.Object, string> assetNames = new Dictionary<UnityEngine.Object, string>();
    private static readonly Dictionary<string, string> wantedNames = new Dictionary<string, string>();

    /// <summary>Lower case, with spaces, dashes and underscores removed. Never null.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        char[] buffer = new char[text.Length];
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '-' || c == '_' || char.IsWhiteSpace(c)) continue;
            buffer[count++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, count);
    }

    /// <summary>Normalize for a name typed in the Inspector. Remembered, so calling it every frame makes no garbage.</summary>
    public static string NormalizeWanted(string wanted)
    {
        if (string.IsNullOrEmpty(wanted)) return string.Empty;

        string result;
        if (!wantedNames.TryGetValue(wanted, out result))
        {
            if (wantedNames.Count >= MaxRemembered) wantedNames.Clear();
            result = Normalize(wanted);
            wantedNames[wanted] = result;
        }
        return result;
    }

    /// <summary>The normalized name of a tile, a sprite or any other Unity asset. Remembered. Null (or a destroyed asset) gives "".</summary>
    public static string NormalizedName(UnityEngine.Object asset)
    {
        if (asset == null) return string.Empty;   // Unity's own null check: a destroyed asset counts as null too

        string result;
        if (!assetNames.TryGetValue(asset, out result))
        {
            if (assetNames.Count >= MaxRemembered) assetNames.Clear();
            result = Normalize(asset.name);
            assetNames[asset] = result;
        }
        return result;
    }

    /// <summary>Forgets every remembered name.</summary>
    public static void Clear()
    {
        assetNames.Clear();
        wantedNames.Clear();
    }

    // With "Enter Play Mode Options" (no domain reload) static lists would survive between plays, so start every play with empty ones.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Clear();
    }
}
