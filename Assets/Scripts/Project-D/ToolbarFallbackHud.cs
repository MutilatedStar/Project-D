using UnityEngine;

/// <summary>
/// STAND-IN TOOLBAR. It does not use CoopInventoryPanel or CoopInventoryLayout, so it shows the tools even when those are not working.
/// Add it to ANY object in the scene (Add Component > Toolbar Fallback Hud). One is enough.
///
/// It draws one row of slots for every player that really exists (it asks SwordController.All, so a removed Player 2 is simply not drawn):
///   Slot 1 = Frost Sword, then Scanner Lens, Magic Boots, Pulse Beam, Grapple Arm, Bubble Shield.
///   Bright border   the slot in the player's hand
///   Dimmed slot     a tool this player has not unlocked yet
///   Dark cover      the cooldown, draining away from the top
///   Bar under slot  hold / charge progress (Pulse Beam, Scanner Lens, Grapple)
///   White squares   dash charges (Magic Boots)
///   Timer above     a running buff (SPD boots, SHLD bubble, LENS scan)
/// Player 1 is bottom left, Player 2 bottom right, Players 3 and 4 sit above them.
///
/// If NO row shows up, a red message tells you why (no SwordController is switched on). That means the problem is not the toolbar:
/// check the Console for red compile errors first, because nothing runs while one is left.
///
/// Once the real inventory bar works, delete this component so you don't see two toolbars.
/// </summary>
[DisallowMultipleComponent]
public class ToolbarFallbackHud : MonoBehaviour
{
    [Header("Size  (scales with the screen height)")]
    [Tooltip("Slot size in pixels on a 1080 pixel high screen.")]
    [Min(16f)] [SerializeField] private float slotSize = 56f;
    [Tooltip("Space between slots, in pixels on a 1080 pixel high screen.")]
    [Min(0f)] [SerializeField] private float gap = 6f;
    [Tooltip("Space between the toolbar and the screen edge, in pixels on a 1080 pixel high screen.")]
    [Min(0f)] [SerializeField] private float margin = 16f;

    [Header("Options")]
    [Tooltip("ON: tools the player has not unlocked yet are drawn dimmed. OFF: they are left out (the other slots keep their place).")]
    [SerializeField] private bool showLockedSlots = true;
    [Tooltip("ON: a red line appears when no player with a SwordController is active, so you know the toolbar has nothing to draw.")]
    [SerializeField] private bool warnWhenNoPlayers = true;

    private const int MaxPlayers = 4;

    private GUIStyle labelStyle;
    private GUIStyle warnStyle;

    private void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;

        float scale = Screen.height / 1080f;
        float size = Mathf.Max(24f, slotSize * scale);
        float space = gap * scale;
        float edge = margin * scale;

        if (labelStyle == null)
        {
            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.alignment = TextAnchor.MiddleCenter;
            labelStyle.fontStyle = FontStyle.Bold;
            labelStyle.normal.textColor = Color.white;
            labelStyle.wordWrap = false;

            warnStyle = new GUIStyle(GUI.skin.label);
            warnStyle.fontStyle = FontStyle.Bold;
            warnStyle.normal.textColor = new Color(1f, 0.35f, 0.3f);
        }
        labelStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * 0.26f));
        warnStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(size * 0.3f));

        int drawn = 0;
        for (int n = 1; n <= MaxPlayers; n++)
        {
            SwordController player = SwordController.Get(n);
            if (player == null) continue;

            drawn++;
            DrawBar(player, n, size, space, edge);
        }

        if (drawn == 0 && warnWhenNoPlayers)
        {
            GUI.Label(new Rect(edge, Screen.height - edge - size, Screen.width - edge * 2f, size),
                      "Toolbar: no player with an active SwordController was found.", warnStyle);
        }
    }

    private void DrawBar(SwordController player, int number, float size, float space, float edge)
    {
        int count = player.SwordCount;
        if (count <= 0) return;

        float width = count * size + (count - 1) * space;
        bool left = (number % 2) == 1;
        int row = (number - 1) / 2;
        float rowHeight = size * 1.7f + space;

        float x = left ? edge : Screen.width - edge - width;
        float y = Screen.height - edge - size - row * rowHeight;

        // Running buffs, as small timers above the bar.
        float buffHeight = size * 0.4f;
        float buffX = x;
        for (int i = 0; i < count; i++)
        {
            PlayerTool tool = player.ToolAt(i);
            if (tool == null) continue;

            float fraction = tool.BuffFraction;
            if (fraction <= 0f) continue;

            Rect box = new Rect(buffX, y - space - buffHeight, size * 0.95f, buffHeight);
            Fill(box, new Color(0f, 0f, 0f, 0.8f));

            Color buffColor = tool.BuffColor;
            buffColor.a = 0.9f;
            Fill(new Rect(box.x, box.y, box.width * Mathf.Clamp01(fraction), box.height), buffColor);

            string text = tool.BuffLabel;
            if (!string.IsNullOrEmpty(text)) GUI.Label(box, text, labelStyle);
            buffX += box.width + space;
        }

        for (int i = 0; i < count; i++)
        {
            Rect slot = new Rect(x + i * (size + space), y, size, size);
            DrawSlot(player, i, slot);
        }
    }

    private void DrawSlot(SwordController player, int index, Rect slot)
    {
        bool unlocked = player.IsUnlocked(index);
        if (!unlocked && !showLockedSlots) return;

        bool equipped = player.CurrentIndex == index;
        float dim = unlocked ? 1f : 0.3f;

        SwordController.SwordStats stats = player.StatsAt(index);
        PlayerTool tool = player.ToolAt(index);

        Color tint = Color.white;
        if (tool != null) tint = tool.TintColor;
        else if (stats != null) tint = stats.placeholderColor;

        // Border (bright when this slot is in hand), then the coloured square.
        Fill(slot, equipped ? new Color(1f, 1f, 1f, dim) : new Color(0f, 0f, 0f, 0.9f * dim));
        Rect inner = new Rect(slot.x + 3f, slot.y + 3f, slot.width - 6f, slot.height - 6f);
        Fill(inner, new Color(tint.r * 0.45f, tint.g * 0.45f, tint.b * 0.45f, dim));

        // The picture if the tool has one, otherwise its short label.
        if (stats != null && stats.sprite != null)
        {
            DrawSprite(stats.sprite, inner, new Color(1f, 1f, 1f, dim));
        }
        else
        {
            string label = tool != null ? tool.ShortLabel : "SWRD";
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, dim);
            GUI.Label(inner, label, labelStyle);
            GUI.color = old;
        }

        if (tool == null) return;

        // Cooldown cover, draining from the top.
        float cooldown = Mathf.Clamp01(tool.CooldownFraction);
        if (cooldown > 0f) Fill(new Rect(inner.x, inner.y, inner.width, inner.height * cooldown), new Color(0f, 0f, 0f, 0.65f));

        // Dash charges (Magic Boots).
        int pipMax = tool.PipMax;
        if (pipMax > 0)
        {
            int pips = tool.PipCount;
            float pip = Mathf.Max(4f, slot.width * 0.14f);
            for (int k = 0; k < pipMax; k++)
            {
                Rect r = new Rect(inner.x + 2f + k * (pip + 2f), inner.yMax - pip - 2f, pip, pip);
                Fill(r, k < pips ? Color.white : new Color(1f, 1f, 1f, 0.25f));
            }
        }

        // Hold / charge progress under the slot.
        float charge = Mathf.Clamp01(tool.ChargeFraction);
        if (charge > 0f)
        {
            Color c = tint;
            c.a = 1f;
            Fill(new Rect(slot.x, slot.yMax + 2f, slot.width * charge, 4f), c);
        }
    }

    private static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    // Draws a sprite inside an area, keeping its shape. Works for sprites cut out of a bigger texture.
    private static void DrawSprite(Sprite sprite, Rect area, Color tint)
    {
        Texture2D tex = sprite.texture;
        if (tex == null) return;

        Rect source;
        try { source = sprite.textureRect; }
        catch (System.Exception) { source = new Rect(0f, 0f, tex.width, tex.height); }   // tightly packed sprites have no plain rectangle

        Rect uv = new Rect(source.x / tex.width, source.y / tex.height, source.width / tex.width, source.height / tex.height);

        float aspect = source.width / Mathf.Max(1f, source.height);
        Rect fit = area;
        if (aspect > 1f)
        {
            fit.height = area.width / aspect;
            fit.y += (area.height - fit.height) * 0.5f;
        }
        else
        {
            fit.width = area.height * aspect;
            fit.x += (area.width - fit.width) * 0.5f;
        }

        Color old = GUI.color;
        GUI.color = tint;
        GUI.DrawTextureWithTexCoords(fit, tex, uv);
        GUI.color = old;
    }
}
