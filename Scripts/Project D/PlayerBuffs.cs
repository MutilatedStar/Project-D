using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Timed "take less damage" effects on a player: the Bubble Shield uses it, and the Scanner Lens uses it to make the player
/// untouchable while the stage is frozen.
///
/// ONE LINE makes it exact: in CoopHealth.ApplyDamage, as the first line, add
///       amount = PlayerBuffs.ModifyIncoming(PlayerNumber, amount);
/// (use whatever the damage parameter and the player number are called there). Every hit, including tick damage, is then reduced
/// BEFORE it is taken, so even a hit that would have killed a player at low health only does the reduced damage.
///
/// Without that line the SwordController still works: it hands back the share of any health the player loses while a buff runs.
/// That is close, but a hit big enough to kill is not softened, which is why the line is worth adding.
/// </summary>
public static class PlayerBuffs
{
    private const int MaxPlayers = 4;

    private static readonly Dictionary<object, float>[] sources = NewTables();

    /// <summary>True once CoopHealth has called ModifyIncoming at least once, so the SwordController stops handing health back.</summary>
    public static bool HookInstalled { get; private set; }

    private static Dictionary<object, float>[] NewTables()
    {
        Dictionary<object, float>[] tables = new Dictionary<object, float>[MaxPlayers + 1];
        for (int i = 0; i <= MaxPlayers; i++) tables[i] = new Dictionary<object, float>();
        return tables;
    }

    private static int Slot(int player) { return Mathf.Clamp(player, 1, MaxPlayers); }

    /// <summary>This source makes the player take this fraction of normal damage (0.5 = half, 0 = none). Setting it again replaces it.</summary>
    public static void Set(int player, object source, float damageTakenMultiplier)
    {
        sources[Slot(player)][source] = Mathf.Max(0f, damageTakenMultiplier);
    }

    public static void Clear(int player, object source)
    {
        sources[Slot(player)].Remove(source);
    }

    public static void ClearAll(int player)
    {
        sources[Slot(player)].Clear();
    }

    /// <summary>1 = normal damage. All running effects multiplied together.</summary>
    public static float DamageTaken(int player)
    {
        float result = 1f;
        foreach (KeyValuePair<object, float> kv in sources[Slot(player)]) result *= kv.Value;
        return result;
    }

    /// <summary>Call this from CoopHealth.ApplyDamage (see the top of this file). Returns the damage to really take.</summary>
    public static float ModifyIncoming(int player, float amount)
    {
        HookInstalled = true;
        return amount * DamageTaken(player);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetOnPlay()
    {
        for (int i = 0; i <= MaxPlayers; i++) sources[i].Clear();
        HookInstalled = false;
    }
}
