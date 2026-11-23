using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Ilumisoft.HealthSystem.UI
{
    /// <summary>
    /// While the bar is filling up (healing) this shows a white charging bolt and, optionally,
    /// a soft glow at the leading edge of the fill, in the fill's own colour.
    /// Everything is built at runtime as children of the fill image, so just add this component
    /// to the same GameObject as the Healthbar / PlayerHealthbar (works for bars spawned by CoopHealthbarBinder too).
    /// </summary>
    [RequireComponent(typeof(Healthbar))]
    [AddComponentMenu("Health System/UI/Healthbar Healing Effect")]
    public class HealthbarHealingEffect : MonoBehaviour
    {
        [Header("Lighting effect")]
        [SerializeField, Tooltip("Toggle the glow at the leading edge of the fill")]
        bool glowEnabled = true;

        [SerializeField, Min(0.1f), Tooltip("Glow diameter relative to the bar's thickness")]
        float glowSize = 2.5f;

        [SerializeField, Range(0f, 1f)]
        float glowIntensity = 0.9f;

        [SerializeField, Range(0f, 1f), Tooltip("0 = steady glow, 1 = pulses fully")]
        float pulseDepth = 0.35f;

        [SerializeField, Min(0f)]
        float pulseSpeed = 12f;

        [Header("Charging symbol (always white)")]
        [SerializeField, Tooltip("Leave empty to use a generated lightning bolt")]
        Sprite chargeSprite;

        [SerializeField, Min(0.1f), Tooltip("Symbol size relative to the bar's thickness")]
        float iconSize = 1.2f;

        [SerializeField, Tooltip("On: the symbol rides the leading edge of the fill. Off: it stays centred on the bar")]
        bool iconFollowsFill = true;

        [Header("Timing")]
        [SerializeField, Min(0f), Tooltip("How long the effect lingers after the bar stops rising (so small heals are still visible)")]
        float holdTime = 0.25f;

        [SerializeField, Min(0.01f)]
        float fadeTime = 0.15f;

        public bool GlowEnabled { get { return glowEnabled; } set { glowEnabled = value; } }

        Healthbar bar;
        Image glow;
        Image icon;
        float visibility;   // 0..1, fades in and out
        float holdTimer;

        // Textures and sprites generated at runtime, destroyed together with this component
        readonly List<UnityEngine.Object> generated = new List<UnityEngine.Object>();

        // Lightning bolt outline in a 0..1 square, y pointing up
        static readonly Vector2[] BoltShape =
        {
            new Vector2(0.60f, 1.00f),
            new Vector2(0.20f, 0.45f),
            new Vector2(0.45f, 0.45f),
            new Vector2(0.35f, 0.00f),
            new Vector2(0.80f, 0.58f),
            new Vector2(0.55f, 0.58f),
        };

        void Awake()
        {
            bar = GetComponent<Healthbar>();
        }

        void OnDisable()
        {
            visibility = 0f;
            holdTimer = 0f;
            if (glow != null) glow.enabled = false;
            if (icon != null) icon.enabled = false;
        }

        void OnDestroy()
        {
            foreach (UnityEngine.Object o in generated)
            {
                if (o != null) Destroy(o);
            }
        }

        // LateUpdate so the Healthbar has already moved its fill this frame
        void LateUpdate()
        {
            Image fill = bar.FillImage;
            HealthComponent health = bar.Health;

            if (fill == null || health == null || health.MaxHealth <= 0f) return;

            BuildIfNeeded(fill);

            // Healing = the displayed fill is still catching up to a higher health value
            float target = Mathf.Clamp01(health.CurrentHealth / health.MaxHealth);
            bool healing = fill.fillAmount < target - 0.0005f;

            if (healing) holdTimer = holdTime;
            else holdTimer -= Time.deltaTime;

            bool show = healing || holdTimer > 0f;
            visibility = Mathf.MoveTowards(visibility, show ? 1f : 0f, Time.deltaTime / fadeTime);
            bool visible = visibility > 0f;

            Rect rect = fill.rectTransform.rect;
            float thickness = Mathf.Min(rect.width, rect.height);

            Vector2 edge;
            bool hasEdge = TryGetEdge(fill, out edge);

            // Lighting effect: same colour as the health, sitting on the rising edge
            bool showGlow = visible && glowEnabled && hasEdge;
            glow.enabled = showGlow;

            if (showGlow)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed);
                float pulse = Mathf.Lerp(1f - pulseDepth, 1f, wave);

                Color c = fill.color;
                c.a *= glowIntensity * pulse * visibility;
                glow.color = c;

                glow.rectTransform.anchoredPosition = edge;
                glow.rectTransform.sizeDelta = Vector2.one * (thickness * glowSize);
            }

            // Charging symbol: always white
            icon.enabled = visible;

            if (visible)
            {
                icon.color = new Color(1f, 1f, 1f, visibility);
                icon.rectTransform.anchoredPosition = (iconFollowsFill && hasEdge) ? edge : Vector2.zero;
                icon.rectTransform.sizeDelta = Vector2.one * (thickness * iconSize);
            }
        }

        /// <summary>
        /// Position of the fill's leading edge relative to the centre of the fill's rect.
        /// Only horizontal and vertical fills have a straight edge; radial fills return false.
        /// </summary>
        bool TryGetEdge(Image fill, out Vector2 edge)
        {
            Rect rect = fill.rectTransform.rect;
            float f = fill.fillAmount;
            edge = Vector2.zero;

            if (fill.fillMethod == Image.FillMethod.Horizontal)
            {
                bool fromRight = fill.fillOrigin == (int)Image.OriginHorizontal.Right;
                edge.x = (fromRight ? 0.5f - f : f - 0.5f) * rect.width;
                return true;
            }

            if (fill.fillMethod == Image.FillMethod.Vertical)
            {
                bool fromTop = fill.fillOrigin == (int)Image.OriginVertical.Top;
                edge.y = (fromTop ? 0.5f - f : f - 0.5f) * rect.height;
                return true;
            }

            return false;
        }

        void BuildIfNeeded(Image fill)
        {
            if (glow != null && icon != null) return;

            // Glow first so the symbol is drawn on top of it
            glow = CreateChild(fill, "HealingGlow", MakeGlowSprite());
            icon = CreateChild(fill, "HealingChargeIcon", chargeSprite != null ? chargeSprite : MakeBoltSprite());

            glow.enabled = false;
            icon.enabled = false;
        }

        static Image CreateChild(Image fill, string childName, Sprite sprite)
        {
            GameObject go = new GameObject(childName, typeof(RectTransform), typeof(Image));

            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(fill.rectTransform, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            Image img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.raycastTarget = false;
            return img;
        }

        Sprite ToSprite(Texture2D tex)
        {
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.Apply();

            Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);

            generated.Add(tex);
            generated.Add(sprite);
            return sprite;
        }

        // Soft white radial gradient; the Image colour tints it to the health colour
        Sprite MakeGlowSprite()
        {
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half)) / half;
                    float a = Mathf.Clamp01(1f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }

            return ToSprite(tex);
        }

        // White lightning bolt, rasterised with 3x3 supersampling for smooth edges
        Sprite MakeBoltSprite()
        {
            const int size = 128;
            const int n = 3;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;

                    for (int sy = 0; sy < n; sy++)
                    {
                        for (int sx = 0; sx < n; sx++)
                        {
                            // 5% margin so the tips are not clipped by the texture border
                            float u = ((x + (sx + 0.5f) / n) / size - 0.05f) / 0.9f;
                            float v = ((y + (sy + 0.5f) / n) / size - 0.05f) / 0.9f;
                            if (Inside(BoltShape, u, v)) hits++;
                        }
                    }

                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, hits / (float)(n * n)));
                }
            }

            return ToSprite(tex);
        }

        // Standard even-odd point-in-polygon test
        static bool Inside(Vector2[] poly, float x, float y)
        {
            bool inside = false;

            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                bool crosses = (poly[i].y > y) != (poly[j].y > y);

                if (crosses && x < (poly[j].x - poly[i].x) * (y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
