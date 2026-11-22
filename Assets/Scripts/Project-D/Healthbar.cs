using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ilumisoft.HealthSystem.UI
{
    [AddComponentMenu("Health System/UI/Healthbar")]
    public class Healthbar : MonoBehaviour
    {
        public enum BarColor { Green, Yellow, Custom }
        public enum LabelMode { None, Percent, CurrentOverMax }

        [field:SerializeField]
        public HealthComponent Health { get; set; }

        [SerializeField]
        Canvas canvas;

        [SerializeField]
        Image fillImage;

        [SerializeField, Tooltip("Whether the healthbar should be hidden when health is empty")]
        bool hideEmpty = false;

        [SerializeField, Tooltip("Makes the healthbar being aligned with the camera")]
        bool alignWithCamera = false;

        [SerializeField, Min(0.1f), Tooltip("Controls how fast changes will be animated in points/second")]
        float changeSpeed = 100;

        [Header("Battery Look (optional)")]
        [SerializeField, Tooltip("OFF (default): this bar's colors are left exactly as you set them on its Image and Text. ON: only this bar's Fill Image and text slots get the colors below, and only while the game is running.")]
        bool useBatteryColors = false;

        [SerializeField, Tooltip("Color of the bar. Use a white fill sprite so the color shows up true")]
        BarColor barColor = BarColor.Green;

        [SerializeField, Tooltip("Only used when Bar Color is Custom")]
        Color customColor = Color.cyan;

        [Header("Text Overlay")]
        [SerializeField, Tooltip("Percent shows 75%, Current Over Max shows 75 / 100")]
        LabelMode labelMode = LabelMode.Percent;

        [SerializeField, Tooltip("TextMeshPro text to write the health into (use this OR the legacy one)")]
        TMP_Text tmpLabel;

        [SerializeField, Tooltip("Legacy UI Text to write the health into (use this OR the TMP one)")]
        Text legacyLabel;

        [SerializeField]
        Color textColor = Color.white;

        float currentValue;
        bool started;   // has currentValue been set from a real Health yet?

        // What the label currently shows, so the string is only rebuilt when the number actually changes
        int labelA = int.MinValue;
        int labelB = int.MinValue;
        LabelMode labelModeShown = (LabelMode)(-1);

        static readonly Color GreenColor = new Color(0.25f, 0.85f, 0.3f);
        static readonly Color YellowColor = new Color(1f, 0.85f, 0.1f);

        // Read (and, for Align With Camera, set) by CoopHealthbarBinder: a bar in the screen HUD must not turn to face the camera,
        // and the binder's "why is nothing drawn?" report needs to see these.
        public bool AlignsWithCamera { get { return alignWithCamera; } set { alignWithCamera = value; } }
        public bool HideEmpty { get { return hideEmpty; } }
        public Canvas BarCanvas { get { return canvas; } }
        public Image FillImage { get { return fillImage; } }

        /// <summary>Sets the bar to any color you like (switches Bar Color to Custom)</summary>
        public void SetFillColor(Color color)
        {
            useBatteryColors = true;   // asking for a color from code turns the colors on for this bar
            barColor = BarColor.Custom;
            customColor = color;
            ApplyColors();
        }

        /// <summary>Switches between the green and yellow presets</summary>
        public void SetBarColor(BarColor color)
        {
            useBatteryColors = true;
            barColor = color;
            ApplyColors();
        }

        public void SetTextColor(Color color)
        {
            useBatteryColors = true;
            textColor = color;
            ApplyColors();
        }

        public void SetLabelMode(LabelMode mode)
        {
            labelMode = mode;
            labelA = int.MinValue;   // forces the label to refresh next frame
        }

        protected virtual void Reset()
        {
            if (Health == null)
            {
                Health = GetComponentInParent<HealthComponent>();
            }
        }

        private void OnValidate()
        {
            labelA = int.MinValue;   // makes the label refresh after an inspector change

            // Colors are only written while the game runs. Writing them in edit mode would save them into the scene or prefab
            // for good, which is how a color can end up stuck on UI that it was never meant for.
            if (Application.isPlaying) ApplyColors();
        }

        private void Start()
        {
            ApplyColors();

            // A bar that is given its Health later (the co-op health bars: CoopHealthbarBinder hands it the player once
            // that player exists) has none yet. It just waits, instead of throwing an error every frame.
            if (Health != null)
            {
                currentValue = Health.CurrentHealth;
                started = true;
            }
        }

        private void Update()
        {
            if (alignWithCamera)
            {
                AlignWithCamera();
            }

            if (Health == null || fillImage == null) return;

            if (!started)
            {
                currentValue = Health.CurrentHealth;   // the Health arrived after Start: begin from where it really is
                started = true;
            }

            currentValue = Mathf.MoveTowards(currentValue, Health.CurrentHealth, Time.deltaTime * changeSpeed);
            
            UpdateFillbar();
            UpdateLabel();
            UpdateVisibility();
        }

        private void AlignWithCamera()
        {
            Camera cam = Camera.main;
            if (cam != null) transform.forward = cam.transform.forward;
        }

        void UpdateFillbar()
        {
            // Update the fill amount
            float value = Mathf.InverseLerp(0, Health.MaxHealth, currentValue);

            fillImage.fillAmount = value;
        }

        void UpdateLabel()
        {
            if (tmpLabel == null && legacyLabel == null) return;

            int a = 0;
            int b = 0;

            switch (labelMode)
            {
                case LabelMode.Percent:
                    a = ToDisplay(Health.MaxHealth > 0 ? currentValue / Health.MaxHealth * 100f : 0f);
                    break;
                case LabelMode.CurrentOverMax:
                    a = ToDisplay(currentValue);
                    b = ToDisplay(Health.MaxHealth);
                    break;
            }

            if (a == labelA && b == labelB && labelMode == labelModeShown) return;

            labelA = a;
            labelB = b;
            labelModeShown = labelMode;

            string text = "";

            switch (labelMode)
            {
                case LabelMode.Percent:
                    text = a + "%";
                    break;
                case LabelMode.CurrentOverMax:
                    text = a + " / " + b;
                    break;
            }

            if (tmpLabel != null) tmpLabel.text = text;
            if (legacyLabel != null) legacyLabel.text = text;
        }

        // Rounds for display, but never shows 0 while there is still some health left
        static int ToDisplay(float value)
        {
            if (value <= 0f) return 0;
            return Mathf.Max(1, Mathf.RoundToInt(value));
        }

        void ApplyColors()
        {
            // Not opted in: leave every color on this bar (and everything else) exactly as it is.
            if (!useBatteryColors) return;

            if (fillImage != null)
            {
                switch (barColor)
                {
                    case BarColor.Green: fillImage.color = GreenColor; break;
                    case BarColor.Yellow: fillImage.color = YellowColor; break;
                    default: fillImage.color = customColor; break;
                }
            }

            if (tmpLabel != null) tmpLabel.color = textColor;
            if (legacyLabel != null) legacyLabel.color = textColor;
        }

        void UpdateVisibility()
        {
            float value = fillImage.fillAmount;

            if (canvas != null)
            {
                // Hide if empty
                if (Mathf.Approximately(value, 0))
                {
                    if (hideEmpty && canvas.gameObject.activeSelf)
                    {
                        canvas.gameObject.SetActive(false);
                    }
                }
                // Make sure the canvas is enabled if health is not empty
                else if (value > 0 && canvas.gameObject.activeSelf == false)
                {
                    canvas.gameObject.SetActive(true);
                }
            }
        }
    }
}
