using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One player's inventory bar: a row of slots (the Frost Sword and the five tools, the held one highlighted) with the player's running
/// power-ups and tool buffs as small labelled timers above it. It is built by this script when the player appears, so there are no slots to
/// draw by hand, and it shows only what THAT player owns (with Shared Weapons ON, what anybody owns).
///
/// Each slot shows:
///   - the slot number (the number key that picks it),
///   - its Icon, or the short label on a coloured square while it has no icon (the Frost Sword shows a coloured bar like before),
///   - a dark cover that drains away while the tool is on cooldown,
///   - a thin bar underneath while the tool is being charged (held),
///   - "x2" style dash charges for the Magic Boots.
/// The timer squares above the row show power-ups (PlayerEffects) and tool buffs: the Magic Boots speed buff, the Bubble Shield, the lens.
///
/// SET UP, once for each player (the same as before):
///   1. Make an empty object in a Screen Space - Overlay Canvas (Create Empty under the Canvas gives it a Rect Transform)
///      and put this script on it. Leave it at a normal size: the script sizes it to fit its slots.
///   2. Set Player Number (1 or 2).
///   3. Add ONE CoopInventoryLayout to the scene to place the bars (Free / Mirror / Auto Snap), like the health bars.
/// The object stays switched on; the script shows and hides the bar itself as the player comes and goes.
///
/// It needs the player's SwordController (the slots) and, for the power-up timers, PlayerEffects (added automatically when the
/// player picks up a power-up).
/// This is separate from InventoryItemSlot on purpose: that one is the detective game's mouse inventory (covers, popups,
/// dragging items to suspects). This one is the per-player weapon bar and never touches it.
/// </summary>
[DisallowMultipleComponent]
public class CoopInventoryPanel : CoopHudPanel
{
    [Header("Slots")]
    [Tooltip("Width and height of one slot, in canvas units.")]
    [Min(8f)] public float slotSize = 52f;
    [Tooltip("Gap between slots and between rows.")]
    [Min(0f)] public float spacing = 6f;
    [Tooltip("ON: slots the player doesn't own yet show as dark empty slots, so they can see what is still to find. OFF: only owned slots show.")]
    public bool showLockedSlots = true;
    public Color slotColor = new Color(0.1f, 0.1f, 0.14f, 0.85f);
    public Color selectedColor = new Color(0.95f, 0.8f, 0.25f, 0.95f);
    public Color lockedColor = new Color(0.05f, 0.05f, 0.07f, 0.55f);
    [Tooltip("The dark cover that drains away while a tool is on cooldown.")]
    public Color cooldownColor = new Color(0f, 0f, 0f, 0.65f);
    [Tooltip("Show the slot numbers in the corner of each slot.")]
    public bool showSlotNumbers = true;

    [Header("Timers and labels")]
    [Tooltip("Width and height of one timer square.")]
    [Min(8f)] public float chipSize = 30f;
    [Tooltip("Optional. Leave empty to use TextMeshPro's default font.")]
    public TMP_FontAsset font;
    [Min(6f)] public float chipFontSize = 11f;
    [Min(6f)] public float labelFontSize = 11f;

    public override HudGroup Group { get { return HudGroup.Inventory; } }

    /// <summary>True while the bar is showing (that player is in the game).</summary>
    public override bool IsShown { get { return content != null && content.activeSelf; } }

    /// <summary>Why the bar is hidden: it only shows for a player that has a SwordController and counts.</summary>
    public override string HiddenReason
    {
        get
        {
            if (SwordController.Get(playerNumber) == null)
            {
                return "no SwordController for Player " + playerNumber + " is in the game (the player prefab needs one, with the matching Player Number).";
            }

            if (!CoopRules.Counts(playerNumber))
            {
                return "CoopRules says Player " + playerNumber + " does not count right now (Single Player Mode is on for the other player, " +
                       "or Player 2 is switched off in LocalCoopManager).";
            }

            return "shown normally";
        }
    }

    private class Slot
    {
        public GameObject root;
        public RectTransform rect;
        public Image background;
        public Image icon;
        public TextMeshProUGUI toolLabel;
        public Image cover;
        public Image chargeBar;
        public TextMeshProUGUI pips;
        public bool visible;
    }

    private class Chip
    {
        public GameObject root;
        public RectTransform rect;
        public Image background;
        public TextMeshProUGUI label;
        public Image bar;
    }

    private RectTransform panelRect;
    private GameObject content;
    private RectTransform contentRect;
    private SwordController controller;
    private PlayerEffects effects;
    private readonly List<Slot> slots = new List<Slot>();
    private readonly List<Chip> chips = new List<Chip>();
    private Vector2 lastSize = new Vector2(-1f, -1f);

    private void Awake()
    {
        panelRect = transform as RectTransform;
        if (panelRect == null)
        {
            Debug.LogError("CoopInventoryPanel on '" + name + "': this object is not a UI object, so the bar can't be drawn (it needs a Rect Transform inside a Canvas). " +
                           "Fix: drag this object onto your HUD Canvas in the Hierarchy. Unity turns it into a UI object by itself. This script is switched off until you do.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        SwordController found = SwordController.Get(playerNumber);
        bool show = found != null && CoopRules.Counts(playerNumber);

        if (!show)
        {
            if (content != null && content.activeSelf) content.SetActive(false);
            return;
        }

        if (found != controller)
        {
            controller = found;
            effects = null;
            Build();
        }

        if (content == null) return;
        if (!content.activeSelf) content.SetActive(true);

        if (effects == null) effects = controller.GetComponent<PlayerEffects>();   // appears once the player first picks up a power-up

        Refresh();
    }

    // ------------------------------------------------------------ building

    private void Build()
    {
        // A new player (Player 2 respawned): throw the old bar away and make one for them.
        if (content != null) Destroy(content);
        slots.Clear();
        chips.Clear();
        lastSize = new Vector2(-1f, -1f);

        content = new GameObject("Inventory", typeof(RectTransform));
        contentRect = (RectTransform)content.transform;
        contentRect.SetParent(panelRect, false);
        contentRect.anchorMin = Vector2.zero;
        contentRect.anchorMax = Vector2.one;
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        for (int i = 0; i < controller.SwordCount; i++) slots.Add(NewSlot(i));
    }

    private Slot NewSlot(int index)
    {
        Slot s = new Slot();
        s.root = new GameObject("Slot " + (index + 1), typeof(RectTransform), typeof(Image));
        s.rect = (RectTransform)s.root.transform;
        s.rect.SetParent(contentRect, false);
        AnchorBottomLeft(s.rect, new Vector2(slotSize, slotSize));

        s.background = s.root.GetComponent<Image>();
        s.background.raycastTarget = false;

        SwordController.SwordStats stats = controller.StatsAt(index);
        bool isTool = controller.KindAt(index) != ToolKind.Sword;
        PlayerTool tool = controller.ToolAt(index);

        GameObject iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        RectTransform iconRect = (RectTransform)iconObject.transform;
        iconRect.SetParent(s.rect, false);
        iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;

        s.icon = iconObject.GetComponent<Image>();
        s.icon.raycastTarget = false;
        s.icon.preserveAspect = true;

        if (stats != null && stats.sprite != null)
        {
            s.icon.sprite = stats.sprite;
            s.icon.color = Color.white;
            iconRect.sizeDelta = new Vector2(slotSize * 0.8f, slotSize * 0.8f);
        }
        else if (isTool)
        {
            // No picture yet: a coloured square with the tool's short name on it.
            s.icon.sprite = SwordController.WhiteSquare;
            s.icon.color = stats != null ? stats.placeholderColor : Color.white;
            iconRect.sizeDelta = new Vector2(slotSize * 0.72f, slotSize * 0.72f);

            s.toolLabel = NewText("Name", iconRect, labelFontSize);
            s.toolLabel.text = tool != null ? tool.ShortLabel : "";
            s.toolLabel.color = new Color(0.05f, 0.05f, 0.08f, 1f);
            s.toolLabel.fontStyle = FontStyles.Bold;
            s.toolLabel.alignment = TextAlignmentOptions.Center;
            Stretch(s.toolLabel.rectTransform);
        }
        else
        {
            // The sword with no sprite yet: a coloured bar laid diagonally, like a sword.
            s.icon.sprite = SwordController.WhiteSquare;
            s.icon.color = stats != null ? stats.placeholderColor : Color.white;
            iconRect.sizeDelta = new Vector2(slotSize * 0.85f, slotSize * 0.16f);
            iconRect.localEulerAngles = new Vector3(0f, 0f, 45f);
        }

        // The cooldown cover: full when the tool was just used, draining to nothing.
        GameObject coverObject = new GameObject("Cooldown", typeof(RectTransform), typeof(Image));
        RectTransform coverRect = (RectTransform)coverObject.transform;
        coverRect.SetParent(s.rect, false);
        Stretch(coverRect);
        s.cover = coverObject.GetComponent<Image>();
        s.cover.raycastTarget = false;
        s.cover.sprite = SwordController.WhiteSquare;   // a filled Image needs a sprite
        s.cover.type = Image.Type.Filled;
        s.cover.fillMethod = Image.FillMethod.Vertical;
        s.cover.fillOrigin = (int)Image.OriginVertical.Bottom;
        s.cover.color = cooldownColor;
        s.cover.enabled = false;

        // The charge bar along the bottom edge.
        GameObject barObject = new GameObject("Charge", typeof(RectTransform), typeof(Image));
        RectTransform barRect = (RectTransform)barObject.transform;
        barRect.SetParent(s.rect, false);
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(1f, 0f);
        barRect.pivot = new Vector2(0f, 0f);
        barRect.sizeDelta = new Vector2(0f, 4f);
        barRect.anchoredPosition = Vector2.zero;
        s.chargeBar = barObject.GetComponent<Image>();
        s.chargeBar.raycastTarget = false;
        s.chargeBar.sprite = SwordController.WhiteSquare;
        s.chargeBar.type = Image.Type.Filled;
        s.chargeBar.fillMethod = Image.FillMethod.Horizontal;
        s.chargeBar.color = Color.white;
        s.chargeBar.enabled = false;

        // The slot number, top left.
        if (showSlotNumbers)
        {
            TextMeshProUGUI number = NewText("Number", s.rect, labelFontSize);
            number.text = (index + 1).ToString();
            number.color = new Color(1f, 1f, 1f, 0.7f);
            number.alignment = TextAlignmentOptions.TopLeft;
            Stretch(number.rectTransform);
            number.rectTransform.offsetMin = new Vector2(3f, 0f);
            number.rectTransform.offsetMax = new Vector2(0f, -1f);
        }

        // Charges ("x2"), bottom right.
        s.pips = NewText("Charges", s.rect, labelFontSize);
        s.pips.color = Color.white;
        s.pips.fontStyle = FontStyles.Bold;
        s.pips.alignment = TextAlignmentOptions.BottomRight;
        Stretch(s.pips.rectTransform);
        s.pips.rectTransform.offsetMin = new Vector2(0f, 5f);
        s.pips.rectTransform.offsetMax = new Vector2(-3f, 0f);
        s.pips.gameObject.SetActive(false);

        return s;
    }

    private TextMeshProUGUI NewText(string objectName, Transform parent, float size)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.fontSize = size;
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private Chip NewChip()
    {
        Chip c = new Chip();
        c.root = new GameObject("Timer", typeof(RectTransform), typeof(Image));
        c.rect = (RectTransform)c.root.transform;
        c.rect.SetParent(contentRect, false);
        AnchorBottomLeft(c.rect, new Vector2(chipSize, chipSize));

        c.background = c.root.GetComponent<Image>();
        c.background.raycastTarget = false;

        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.SetParent(c.rect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(0f, 6f);   // keeps clear of the time bar along the bottom
        labelRect.offsetMax = Vector2.zero;

        c.label = labelObject.GetComponent<TextMeshProUGUI>();
        if (font != null) c.label.font = font;
        c.label.fontSize = chipFontSize;
        c.label.fontStyle = FontStyles.Bold;
        c.label.alignment = TextAlignmentOptions.Center;
        c.label.color = Color.white;
        c.label.raycastTarget = false;

        // The time bar: full when the effect starts, empty when it runs out.
        GameObject barObject = new GameObject("Time", typeof(RectTransform), typeof(Image));
        RectTransform barRect = (RectTransform)barObject.transform;
        barRect.SetParent(c.rect, false);
        barRect.anchorMin = new Vector2(0f, 0f);
        barRect.anchorMax = new Vector2(1f, 0f);
        barRect.pivot = new Vector2(0f, 0f);
        barRect.sizeDelta = new Vector2(0f, 4f);
        barRect.anchoredPosition = Vector2.zero;

        c.bar = barObject.GetComponent<Image>();
        c.bar.raycastTarget = false;
        c.bar.sprite = SwordController.WhiteSquare;   // a filled Image needs a sprite
        c.bar.type = Image.Type.Filled;
        c.bar.fillMethod = Image.FillMethod.Horizontal;
        c.bar.color = Color.white;
        return c;
    }

    private static void AnchorBottomLeft(RectTransform rect, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
        rect.sizeDelta = size;
    }

    // ------------------------------------------------------------ every frame

    private void Refresh()
    {
        // Slots: a row along the bottom.
        int current = controller.CurrentIndex;
        int visible = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            Slot s = slots[i];
            bool owned = controller.IsUnlocked(i);
            bool show = owned || showLockedSlots;

            if (s.visible != show)
            {
                s.visible = show;
                s.root.SetActive(show);
            }
            if (!show) continue;

            s.rect.anchoredPosition = new Vector2(visible * (slotSize + spacing), 0f);
            s.background.color = !owned ? lockedColor : (i == current ? selectedColor : slotColor);
            s.icon.enabled = owned;
            if (s.toolLabel != null) s.toolLabel.enabled = owned;

            PlayerTool tool = controller.ToolAt(i);
            float cooldown = (owned && tool != null) ? tool.CooldownFraction : 0f;
            s.cover.enabled = cooldown > 0.001f;
            if (s.cover.enabled) s.cover.fillAmount = cooldown;

            float charge = (owned && tool != null) ? tool.ChargeFraction : 0f;
            s.chargeBar.enabled = charge > 0.001f;
            if (s.chargeBar.enabled) s.chargeBar.fillAmount = charge;

            bool hasPips = owned && tool != null && tool.PipMax > 0;
            if (s.pips.gameObject.activeSelf != hasPips) s.pips.gameObject.SetActive(hasPips);
            if (hasPips) s.pips.text = "x" + tool.PipCount;

            visible++;
        }

        // Timers: a row above the slots. First the power-ups, then the tool buffs.
        float chipY = slotSize + spacing;
        int used = 0;

        int running = effects != null ? effects.Active.Count : 0;
        for (int k = 0; k < running; k++)
        {
            PlayerEffects.ActiveEffect e = effects.Active[k];
            Color tint = PlayerEffects.ColorOf(e.kind);
            float fraction = e.duration > 0f ? Mathf.Clamp01(e.remaining / e.duration) : 0f;
            SetChip(used, tint, PlayerEffects.ShortName(e.kind), fraction, chipY);
            used++;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            PlayerTool tool = controller.ToolAt(i);
            if (tool == null || !controller.IsUnlocked(i)) continue;

            float fraction = tool.BuffFraction;
            if (fraction <= 0.001f) continue;

            SetChip(used, tool.BuffColor, tool.BuffLabel, fraction, chipY);
            used++;
        }

        for (int k = used; k < chips.Count; k++)
        {
            if (chips[k].root.activeSelf) chips[k].root.SetActive(false);
        }

        // The panel is always as big as the room reserved: the slot row plus one timer row. (Reserved even when no
        // timer is running, so the bar doesn't jump around when a power-up starts and the layout can place it once.)
        float slotsWidth = visible * slotSize + Mathf.Max(0, visible - 1) * spacing;
        float chipsWidth = used * chipSize + Mathf.Max(0, used - 1) * spacing;
        float width = Mathf.Max(Mathf.Max(slotsWidth, chipsWidth), chipSize);
        float height = slotSize + spacing + chipSize;
        Vector2 size = new Vector2(width, height);
        if (size != lastSize)
        {
            lastSize = size;
            panelRect.sizeDelta = size;
        }
    }

    private void SetChip(int index, Color tint, string label, float fraction, float y)
    {
        while (chips.Count <= index) chips.Add(NewChip());

        Chip c = chips[index];
        if (!c.root.activeSelf) c.root.SetActive(true);
        c.rect.anchoredPosition = new Vector2(index * (chipSize + spacing), y);
        c.background.color = new Color(tint.r * 0.45f, tint.g * 0.45f, tint.b * 0.45f, 0.9f);
        c.bar.color = tint;
        c.bar.fillAmount = fraction;
        c.label.text = label;
    }
}
