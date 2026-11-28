using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A fake top-down LIGHT RAY, placed with an empty object. It does two things:
///
///   1. It draws a soft patch of light (optionally streaked with rays, like sun coming through a window or a crack in the ceiling).
///   2. It HIDES the shadows laid over its area: DepthEdgeShadows (lower tiles beside higher ones) and FakeDepthShadows (the angled
///      3D shadow of everything) both fade out inside it, so the lit floor is never darkened. Shadows of sprites that walk into the
///      light fade away smoothly as they enter it.
///
/// SET UP
///   1. Right-click in the Hierarchy > Create Empty. Name it "Light Ray 1" and add this script.
///   2. Move / rotate / scale the object to cover the lit area. You can also type Size here. The yellow outline in the Scene view
///      shows the area and the arrow shows which way the rays travel.
///   3. Make as many as you like (copy and paste the object). Zones may overlap: the brightest one wins for hiding shadows.
///
/// DRAW ORDER: the light is a sprite. Give it the same Sorting Layer as the floor it lights and an Order In Layer ABOVE the shadows
/// (DepthEdgeShadows draws just above the floor Tilemap; FakeDepthShadows draws just below whatever casts). Raise Order In Layer
/// until it looks right. Leave Sorting Layer empty to use "Default".
///
/// No other script is needed: the shadow scripts find the light zones by themselves.
/// </summary>
[DisallowMultipleComponent]
public class TopDownLightRay : MonoBehaviour
{
    public enum ZoneShape { Rectangle, Ellipse }

    // ------------------------------------------------------------ registry (the shadow scripts read this)

    private static readonly List<TopDownLightRay> all = new List<TopDownLightRay>();

    /// <summary>Goes up every time a light zone appears, disappears, moves or changes size. The shadow scripts rebuild when it changes.</summary>
    public static int Version { get; private set; }

    /// <summary>How many light zones are running.</summary>
    public static int Count { get { return all.Count; } }

    /// <summary>0..1: how much the lights hide shadows at this world point (0 = no light, 1 = shadows fully hidden).</summary>
    public static float LightAt(Vector2 worldPoint)
    {
        float best = 0f;
        for (int i = 0; i < all.Count; i++)
        {
            TopDownLightRay z = all[i];
            if (z == null) continue;
            float v = z.ShadowHideAt(worldPoint);
            if (v > best) best = v;
        }
        return best;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        all.Clear();
        Version = 0;
    }

    // ------------------------------------------------------------ inspector

    [Header("Area (or move / rotate / scale this object)")]
    public ZoneShape shape = ZoneShape.Rectangle;
    [Tooltip("Width and height of the lit area in world units, before this object's own scale is applied.")]
    public Vector2 size = new Vector2(4f, 6f);
    [Tooltip("How soft the edge is. 0 = hard edge, 1 = fades from the very middle.")]
    [Range(0f, 1f)] public float softness = 0.5f;

    [Header("Shadows")]
    [Tooltip("How much this light hides the shadows laid over it. 1 = they vanish in the middle of the light, 0 = the light does not touch shadows.")]
    [Range(0f, 1f)] public float hideShadows = 1f;

    [Header("Look")]
    [Tooltip("Untick to hide the light patch and ONLY use this zone to hide shadows.")]
    public bool showLight = true;
    public Color lightColor = new Color(1f, 0.93f, 0.7f, 1f);
    [Tooltip("How bright the patch is at its strongest (it is drawn as a see-through tint).")]
    [Range(0f, 1f)] public float brightness = 0.3f;
    [Tooltip("How many streaks of light. 0 = a plain soft patch.")]
    [Range(0, 24)] public int rays = 6;
    [Tooltip("How strong the streaks are against the gaps between them.")]
    [Range(0f, 1f)] public float rayContrast = 0.7f;
    [Tooltip("The light is brightest where the rays start and dims towards the far end. 0 = even.")]
    [Range(0f, 1f)] public float fadeAlongRay = 0.4f;
    [Tooltip("ON: the rays run the same way as the shadows (FakeDepthShadows angle), so light and shadow always agree.")]
    public bool raysFollowShadowAngle = true;
    [Tooltip("Direction the rays travel when Rays Follow Shadow Angle is off (0 = right, 90 = up, -90 = down).")]
    [Range(-180f, 180f)] public float rayAngle = -45f;
    [Tooltip("How fast the light gently shimmers. 0 = still.")]
    [Min(0f)] public float shimmerSpeed = 0.5f;
    [Range(0f, 0.5f)] public float shimmerAmount = 0.12f;

    [Header("Draw order")]
    [Tooltip("Sorting Layer NAME the light is drawn on. Empty = Default.")]
    public string sortingLayer = "";
    public int orderInLayer = 5;

    // ------------------------------------------------------------ internals

    private const int TextureSize = 128;

    private GameObject visual;
    private SpriteRenderer spriteRenderer;
    private Texture2D texture;
    private Sprite sprite;

    private float builtAngle = float.NaN;
    private ZoneShape builtShape;
    private float builtSoftness = -1f;
    private int builtRays = -1;
    private float builtContrast = -1f;
    private float builtFade = -1f;
    private float signature;
    private float seed;
    private float phase;
    private string appliedLayerName = null;
    private bool warnedLayer;

    // ------------------------------------------------------------ lifecycle

    private void OnEnable()
    {
        all.Add(this);
        Version++;
        seed = Mathf.Abs(transform.position.x * 13.1f + transform.position.y * 7.7f) % 100f;
        phase = (seed * 0.37f) % 6.2832f;
        signature = Signature();
        if (showLight) EnsureVisual();
        if (visual != null) visual.SetActive(showLight);
    }

    private void OnDisable()
    {
        all.Remove(this);
        Version++;
        if (visual != null) visual.SetActive(false);
    }

    private void OnDestroy()
    {
        if (texture != null) Destroy(texture);
        if (sprite != null) Destroy(sprite);
    }

    private void Update()
    {
        float sig = Signature();
        if (transform.hasChanged || !Mathf.Approximately(sig, signature))
        {
            transform.hasChanged = false;
            signature = sig;
            Version++;
        }

        if (!showLight)
        {
            if (visual != null && visual.activeSelf) visual.SetActive(false);
            return;
        }

        if (visual == null) EnsureVisual();
        if (visual == null) return;
        if (!visual.activeSelf) visual.SetActive(true);

        float localAngle = DesiredLocalAngle();
        if (float.IsNaN(builtAngle) ||
            Mathf.Abs(Mathf.DeltaAngle(localAngle, builtAngle)) > 0.25f ||
            builtShape != shape || builtSoftness != softness || builtRays != rays ||
            builtContrast != rayContrast || builtFade != fadeAlongRay)
        {
            RebuildTexture(localAngle);
        }

        visual.transform.localScale = new Vector3(Mathf.Max(0.01f, size.x), Mathf.Max(0.01f, size.y), 1f);
        ApplySorting();

        float f = 1f + Mathf.Sin(Time.time * shimmerSpeed * 6.2832f + phase) * shimmerAmount;
        Color c = lightColor;
        c.a = Mathf.Clamp01(brightness * f);
        spriteRenderer.color = c;
    }

    private float Signature()
    {
        return size.x * 1.7f + size.y * 2.3f + softness * 5.1f + hideShadows * 7.3f + (int)shape * 11f;
    }

    // ------------------------------------------------------------ the shadow hiding

    /// <summary>0..1 of how much THIS zone hides shadows at the point (shape and softness times Hide Shadows).</summary>
    public float ShadowHideAt(Vector2 worldPoint)
    {
        if (!isActiveAndEnabled || hideShadows <= 0f) return 0f;

        Vector3 l = transform.InverseTransformPoint(new Vector3(worldPoint.x, worldPoint.y, transform.position.z));
        float hx = Mathf.Max(0.0001f, size.x * 0.5f);
        float hy = Mathf.Max(0.0001f, size.y * 0.5f);
        return ShapeValue(l.x / hx, l.y / hy) * hideShadows;
    }

    // 1 inside, falling to 0 at the edge. u and v run -1..1 across the shape.
    private float ShapeValue(float u, float v)
    {
        float d = shape == ZoneShape.Rectangle
            ? Mathf.Max(Mathf.Abs(u), Mathf.Abs(v))
            : Mathf.Sqrt(u * u + v * v);
        return 1f - Smooth01(1f - softness, 1f, d);
    }

    private static float Smooth01(float e0, float e1, float x)
    {
        if (e1 <= e0) return x >= e1 ? 1f : 0f;
        float t = Mathf.Clamp01((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    // ------------------------------------------------------------ the picture

    private float WorldRayAngle()
    {
        if (raysFollowShadowAngle && FakeDepthShadows.Current != null) return FakeDepthShadows.Current.Angle;
        return rayAngle;
    }

    // The texture is drawn in this object's own space, so the angle has to be turned back by the object's rotation.
    private float DesiredLocalAngle()
    {
        return Mathf.DeltaAngle(0f, WorldRayAngle() - transform.eulerAngles.z);
    }

    private void EnsureVisual()
    {
        if (visual != null) return;

        visual = new GameObject("Light Ray Visual");
        visual.layer = gameObject.layer;
        visual.transform.SetParent(transform, false);
        spriteRenderer = visual.AddComponent<SpriteRenderer>();
        FakeDepthShadows.Ignore(spriteRenderer);

        RebuildTexture(DesiredLocalAngle());
        ApplySorting();
    }

    private void RebuildTexture(float localAngle)
    {
        if (texture == null)
        {
            texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.HideAndDontSave;
        }

        float a = localAngle * Mathf.Deg2Rad;
        float ca = Mathf.Cos(a);
        float sa = Mathf.Sin(a);

        Color32[] px = new Color32[TextureSize * TextureSize];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                float u = (x + 0.5f) / TextureSize * 2f - 1f;
                float v = (y + 0.5f) / TextureSize * 2f - 1f;

                float edge = ShapeValue(u, v);
                float across = -u * sa + v * ca;   // sideways across the rays
                float along = u * ca + v * sa;     // the way the rays travel

                float ray = 1f;
                if (rays > 0 && rayContrast > 0f)
                {
                    float n = Mathf.PerlinNoise(seed + (across * 0.5f + 0.5f) * rays, seed * 0.37f);
                    ray = Mathf.Lerp(1f, Mathf.Clamp01((n - 0.2f) / 0.6f), rayContrast);
                }

                float fade = 1f - fadeAlongRay * Mathf.Clamp01(along * 0.5f + 0.5f);
                byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(edge * ray * fade) * 255f);
                px[y * TextureSize + x] = new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(px);
        texture.Apply(false);

        if (sprite == null)
        {
            sprite = Sprite.Create(texture, new Rect(0f, 0f, TextureSize, TextureSize), new Vector2(0.5f, 0.5f), TextureSize);
            sprite.hideFlags = HideFlags.HideAndDontSave;
        }
        if (spriteRenderer != null) spriteRenderer.sprite = sprite;

        builtAngle = localAngle;
        builtShape = shape;
        builtSoftness = softness;
        builtRays = rays;
        builtContrast = rayContrast;
        builtFade = fadeAlongRay;
    }

    private void ApplySorting()
    {
        if (spriteRenderer == null) return;

        if (appliedLayerName != sortingLayer)
        {
            appliedLayerName = sortingLayer;
            if (string.IsNullOrEmpty(sortingLayer))
            {
                spriteRenderer.sortingLayerID = 0;
            }
            else
            {
                int id = SortingLayer.NameToID(sortingLayer);
                if (SortingLayer.IsValid(id)) spriteRenderer.sortingLayerID = id;
                else if (!warnedLayer)
                {
                    warnedLayer = true;
                    Debug.LogWarning("TopDownLightRay on '" + name + "': there is no sorting layer called \"" + sortingLayer + "\". Using Default.", this);
                }
            }
        }
        spriteRenderer.sortingOrder = orderInLayer;
    }

    // ------------------------------------------------------------ Scene view

    private void OnDrawGizmos() { DrawZoneGizmo(0.45f); }
    private void OnDrawGizmosSelected() { DrawZoneGizmo(1f); }

    private void DrawZoneGizmo(float alpha)
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.9f, 0.3f, alpha);

        float hx = size.x * 0.5f;
        float hy = size.y * 0.5f;

        if (shape == ZoneShape.Rectangle)
        {
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(size.x, size.y, 0.01f));
        }
        else
        {
            const int steps = 32;
            Vector3 prev = new Vector3(hx, 0f, 0f);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps * Mathf.PI * 2f;
                Vector3 next = new Vector3(Mathf.Cos(t) * hx, Mathf.Sin(t) * hy, 0f);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        // The arrow shows the way the rays travel.
        float a = DesiredLocalAngle() * Mathf.Deg2Rad;
        Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
        Vector3 side = new Vector3(-dir.y, dir.x, 0f);
        float len = Mathf.Min(hx, hy) * 0.8f;
        Vector3 tail = -dir * len;
        Vector3 tip = dir * len;
        Gizmos.DrawLine(tail, tip);
        Gizmos.DrawLine(tip, tip - dir * len * 0.25f + side * len * 0.15f);
        Gizmos.DrawLine(tip, tip - dir * len * 0.25f - side * len * 0.15f);

        Gizmos.matrix = Matrix4x4.identity;
    }
}
