using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.UI;

/// <summary>
/// SCANNER LENS (built by SwordController; nothing to add by hand, but the shader must be in a Resources folder, see ScannerGray.shader).
///
///   Tap   the lens turns on for a few seconds (tap again to turn it off early): the stage goes black and white, traps are red and
///         every collectable gets an arrow pointing at it. The game keeps running.
///   Hold  the same, and after a short moment the whole stage FREEZES: foes, traps, cooldowns, everything except the pause screen,
///         and nothing can hurt anybody. The camera is yours: use the pan keys to look around the stage. Let go to carry on.
///
/// How it finds things: a ScannerMarker on an object wins. Otherwise an object counts as a trap / collectable when its layer is ticked
/// in Trap Layers / Collectable Layers, or when its name (or its parent's) contains one of the words in Trap Names / Collectable Names.
/// </summary>
public class ScannerLensTool : PlayerTool
{
    [System.Serializable]
    public class Settings : ToolSettings
    {
        public Settings()
        {
            toolName = "Scanner Lens";
            shortLabel = "LENS";
            color = new Color(0.55f, 0.95f, 1f);
        }

        [Header("Lens")]
        [Tooltip("Seconds the attack button must be held before the stage freezes and the camera is free. A quicker press is a tap and only toggles the lens.")]
        [Min(0f)] public float holdToFreezeTime = 0.35f;
        [Tooltip("A tap keeps the lens on this many seconds. 0 = taps do nothing (only holding works).")]
        [Min(0f)] public float tapLensSeconds = 6f;
        [Tooltip("Seconds before the lens can be used again after it has been turned off.")]
        [Min(0f)] public float cooldown = 0.5f;

        [Header("Look around (while the stage is frozen)")]
        [Tooltip("How fast the camera moves, in Unity units per second.")]
        [Min(0.5f)] public float panSpeed = 9f;
        [Tooltip("The camera can't go further than this from where it started, in Unity units.")]
        [Min(1f)] public float maxPanDistance = 16f;
        [Tooltip("Keys that move the camera for Player 1, in this order: Up, Down, Left, Right.")]
        public KeyCode[] player1PanKeys = { KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D };
        [Tooltip("Keys that move the camera for Player 2, in this order: Up, Down, Left, Right.")]
        public KeyCode[] player2PanKeys = { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow };
        [Tooltip("ON: the freeze also sets Time.timeScale to 0, so even things that never check the game's pause switch stop (physics, animations, particles). Leave it ON.")]
        public bool alsoStopTimeScale = true;

        [Header("Look")]
        public Color trapColor = new Color(1f, 0.15f, 0.15f);
        public Color arrowColor = new Color(1f, 0.9f, 0.2f);
        [Range(0.5f, 2f)] public float contrast = 1.15f;
        [Tooltip("The lens looks for new traps and collectables this often (seconds) while it is on and the stage is running.")]
        [Min(0.25f)] public float rescanInterval = 1f;

        [Header("What counts as a trap or a collectable (a ScannerMarker on the object always wins)")]
        public LayerMask trapLayers = 0;
        public LayerMask collectableLayers = 0;
        [Tooltip("An object whose name (or whose parent's name) contains one of these words is a trap. Not case sensitive.")]
        public string[] trapNames = { "trap", "spike", "laser", "arrow", "turret" };
        [Tooltip("An object whose name (or whose parent's name) contains one of these words is a collectable. Not case sensitive.")]
        public string[] collectableNames = { "collect", "pickup", "coin", "gem", "key", "item" };
    }

    private enum ScanKind { None, Trap, Collectable }

    private struct Cls
    {
        public ScanKind kind;
        public Transform root;
    }

    private readonly Settings s;

    private bool lensOn;
    private bool holding;
    private bool pressedWhileOn;
    private bool frozen;
    private float holdTime;
    private float lensTimer;
    private float cooldownLeft;
    private float cooldownTotal;
    private float rescanTimer;

    private Vector2 panOffset;
    private Camera cam;
    private float camTimer;
    private bool panApplied;
    private bool panKeysBroken;

    private Material grayMat;
    private Material trapMat;
    private bool shaderWarned;

    private readonly Dictionary<Renderer, Material> originals = new Dictionary<Renderer, Material>();
    private readonly Dictionary<SpriteRenderer, Color> originalColors = new Dictionary<SpriteRenderer, Color>();
    private readonly Dictionary<Transform, Cls> cache = new Dictionary<Transform, Cls>();
    private readonly List<Transform> collectables = new List<Transform>();
    private readonly HashSet<Transform> seen = new HashSet<Transform>();

    private GameObject overlayRoot;
    private Canvas canvas;
    private TextMeshProUGUI label;
    private readonly List<Image> arrows = new List<Image>();
    private static Sprite arrowSprite;

    public ScannerLensTool(SwordController owner, Settings settings) : base(owner, settings)
    {
        s = settings;
    }

    public override ToolKind Kind { get { return ToolKind.ScannerLens; } }

    /// <summary>True while the lens has the stage frozen. SwordController keeps running this tool (and nothing else) during it.</summary>
    public bool IsFrozen { get { return frozen; } }

    public override bool IsBusy { get { return holding || frozen; } }

    public override float CooldownFraction
    {
        get { return cooldownTotal > 0f ? Mathf.Clamp01(cooldownLeft / cooldownTotal) : 0f; }
    }

    public override float ChargeFraction
    {
        get { return (holding && !frozen) ? Mathf.Clamp01(holdTime / Mathf.Max(0.01f, s.holdToFreezeTime)) : 0f; }
    }

    public override float BuffFraction
    {
        get
        {
            if (frozen) return 1f;
            if (!lensOn) return 0f;
            if (holding || s.tapLensSeconds <= 0f) return 1f;
            return Mathf.Clamp01(lensTimer / s.tapLensSeconds);
        }
    }

    public override string BuffLabel { get { return "LENS"; } }

    // ------------------------------------------------------------ every frame

    public override void TickAlways(float dt)
    {
        if (cooldownLeft > 0f) cooldownLeft = Mathf.Max(0f, cooldownLeft - dt);

        if (lensOn && !holding && !frozen && lensTimer > 0f)
        {
            lensTimer -= dt;
            if (lensTimer <= 0f) LensOff();
        }

        if (lensOn && !frozen)
        {
            rescanTimer -= dt;
            if (rescanTimer <= 0f)
            {
                rescanTimer = s.rescanInterval;
                Rescan();
            }
        }
    }

    public override void TickEquipped(float dt)
    {
        if (frozen)
        {
            TickFrozen(dt);
            return;
        }

        if (owner.AttackPressedNow && !holding && cooldownLeft <= 0f)
        {
            holding = true;
            holdTime = 0f;
            pressedWhileOn = lensOn;
            if (!lensOn)
            {
                LensOn();
                lensTimer = 0f;
            }
        }

        if (!holding) return;

        if (owner.AttackHeldNow)
        {
            holdTime += dt;
            if (holdTime >= s.holdToFreezeTime) BeginFreeze();
            return;
        }

        // Let go before the freeze: that was a tap.
        holding = false;
        if (pressedWhileOn)
        {
            LensOff();                         // a second tap turns it off
            StartCooldown();
        }
        else if (s.tapLensSeconds > 0f)
        {
            lensTimer = s.tapLensSeconds;
        }
        else
        {
            LensOff();
            StartCooldown();
        }
    }

    private void TickFrozen(float dt)
    {
        GameFreeze.Reassert();

        if (!owner.AttackHeldNow)
        {
            EndFreeze();
            return;
        }

        panOffset += ReadPanInput() * s.panSpeed * dt;
        panOffset = Vector2.ClampMagnitude(panOffset, s.maxPanDistance);
    }

    public override void OnUnequipped() { Cancel(); }

    public override void Cancel()
    {
        if (frozen) EndFreeze();
        else if (lensOn) LensOff();
        holding = false;
    }

    public override void DestroyVisuals()
    {
        Cancel();
        if (overlayRoot != null) Object.Destroy(overlayRoot);
        if (grayMat != null) Object.Destroy(grayMat);
        if (trapMat != null) Object.Destroy(trapMat);
    }

    // ------------------------------------------------------------ the freeze

    private void BeginFreeze()
    {
        holding = false;
        frozen = true;
        GameFreeze.Begin(s.alsoStopTimeScale);
        PlayerBuffs.Set(owner.PlayerNo, this, 0f);   // nothing can hurt the player while the stage is frozen

        panOffset = Vector2.zero;
        cam = ResolveCamera();
        camTimer = 0.5f;
    }

    private void EndFreeze()
    {
        if (!frozen) return;

        frozen = false;
        GameFreeze.End();
        PlayerBuffs.Clear(owner.PlayerNo, this);
        ResetCamera();
        LensOff();
        StartCooldown();
    }

    private void StartCooldown()
    {
        cooldownTotal = Cooldown(s.cooldown);
        cooldownLeft = cooldownTotal;
    }

    private Vector2 ReadPanInput()
    {
        if (panKeysBroken) return Vector2.zero;

        KeyCode[] keys = owner.PlayerNo == 2 ? s.player2PanKeys : s.player1PanKeys;
        if (keys == null || keys.Length < 4) return Vector2.zero;

        try
        {
            float x = 0f;
            float y = 0f;
            if (Input.GetKey(keys[0])) y += 1f;
            if (Input.GetKey(keys[1])) y -= 1f;
            if (Input.GetKey(keys[2])) x -= 1f;
            if (Input.GetKey(keys[3])) x += 1f;

            Vector2 v = new Vector2(x, y);
            return v.sqrMagnitude > 1f ? v.normalized : v;
        }
        catch (System.InvalidOperationException)
        {
            // The project only uses the new Input System, so UnityEngine.Input can't be read.
            panKeysBroken = true;
            Debug.LogWarning("Scanner Lens: the pan keys use the old Input Manager, which this project has switched off. Tell Claude and the pan keys will be changed.");
            return Vector2.zero;
        }
    }

    // ------------------------------------------------------------ camera

    // The camera is moved by changing its projection (the picture slides sideways), not its position, so the game's own camera
    // scripts never notice and nothing has to be put back afterwards.
    public override void LateTickAlways()
    {
        if (!lensOn) return;

        camTimer -= Time.unscaledDeltaTime;
        if (!frozen && (cam == null || camTimer <= 0f))
        {
            Camera found = ResolveCamera();
            if (found != cam)
            {
                ResetCamera();
                cam = found;
            }
            camTimer = 0.5f;
        }

        if (frozen) ApplyPan();

        UpdateTrapPulse();
        UpdateOverlay();
    }

    private void ApplyPan()
    {
        if (cam == null || !cam.orthographic) return;

        float h = cam.orthographicSize;
        float w = h * cam.aspect;
        Vector3 local = Quaternion.Inverse(cam.transform.rotation) * new Vector3(panOffset.x, panOffset.y, 0f);
        cam.projectionMatrix = Matrix4x4.Ortho(-w + local.x, w + local.x, -h + local.y, h + local.y, cam.nearClipPlane, cam.farClipPlane);
        panApplied = true;
    }

    private void ResetCamera()
    {
        if (cam != null && panApplied) cam.ResetProjectionMatrix();
        panApplied = false;
    }

    // The camera that is showing THIS player: the one that has the player nearest to the middle of its view.
    private Camera ResolveCamera()
    {
        Camera best = null;
        float bestScore = float.MaxValue;
        Vector3 pos = owner.transform.position;
        Camera[] all = Camera.allCameras;

        for (int i = 0; i < all.Length; i++)
        {
            Camera c = all[i];
            if (c == null || !c.enabled || c.targetTexture != null || c.cullingMask == 0) continue;

            Vector3 vp = c.WorldToViewportPoint(pos);
            float score = Mathf.Abs(vp.x - 0.5f) + Mathf.Abs(vp.y - 0.5f);
            if (vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f) score += 1f;

            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best != null ? best : Camera.main;
    }

    // ------------------------------------------------------------ the lens (black and white, red traps)

    private void LensOn()
    {
        if (lensOn) return;

        lensOn = true;
        originals.Clear();
        originalColors.Clear();
        cache.Clear();
        collectables.Clear();
        seen.Clear();
        EnsureMaterials();
        Rescan();
        rescanTimer = s.rescanInterval;
        camTimer = 0f;
    }

    private void LensOff()
    {
        if (!lensOn) return;

        lensOn = false;
        lensTimer = 0f;

        foreach (KeyValuePair<Renderer, Material> kv in originals)
        {
            if (kv.Key != null) kv.Key.sharedMaterial = kv.Value;
        }
        originals.Clear();

        foreach (KeyValuePair<SpriteRenderer, Color> kv in originalColors)
        {
            if (kv.Key != null) kv.Key.color = kv.Value;
        }
        originalColors.Clear();

        cache.Clear();
        collectables.Clear();
        seen.Clear();
        ResetCamera();
        if (overlayRoot != null) overlayRoot.SetActive(false);
    }

    private void EnsureMaterials()
    {
        if (grayMat != null) return;

        Shader shader = Resources.Load<Shader>("ScannerGray");
        if (shader == null)
        {
            if (!shaderWarned)
            {
                shaderWarned = true;
                Debug.LogWarning("Scanner Lens: ScannerGray.shader was not found. Put it in a folder called Resources (Assets/Resources/ScannerGray.shader). " +
                                 "Until then the screen does not turn black and white (traps are still tinted red and the arrows still work).");
            }
            return;
        }

        grayMat = new Material(shader);
        grayMat.name = "ScannerGray";
        grayMat.SetColor("_ScanTint", Color.white);
        grayMat.SetFloat("_Contrast", s.contrast);

        trapMat = new Material(shader);
        trapMat.name = "ScannerTrap";
        trapMat.SetColor("_ScanTint", s.trapColor);
        trapMat.SetFloat("_Contrast", s.contrast);
    }

    private void UpdateTrapPulse()
    {
        if (trapMat == null) return;
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f);
        trapMat.SetColor("_ScanTint", Color.Lerp(s.trapColor * 0.7f, s.trapColor, pulse));
    }

    // Goes through every sprite, tile and line in the scene: swaps its material and notes the collectables.
    private void Rescan()
    {
        EnsureMaterials();

        Renderer[] all = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            Renderer r = all[i];
            if (r == null) continue;
            if (!(r is SpriteRenderer || r is TilemapRenderer || r is LineRenderer)) continue;

            Cls c = Classify(r.transform);
            bool trap = c.kind == ScanKind.Trap;
            if (c.kind == ScanKind.Collectable && c.root != null && seen.Add(c.root)) collectables.Add(c.root);

            if (grayMat != null)
            {
                Material current = r.sharedMaterial;
                if (current == null) continue;

                Material want = trap ? trapMat : grayMat;
                if (current == grayMat || current == trapMat)
                {
                    if (current != want) r.sharedMaterial = want;
                    continue;
                }

                if (!originals.ContainsKey(r)) originals[r] = current;
                r.sharedMaterial = want;
            }
            else if (trap)
            {
                SpriteRenderer sr = r as SpriteRenderer;
                if (sr != null && !originalColors.ContainsKey(sr))
                {
                    originalColors[sr] = sr.color;
                    sr.color = s.trapColor;
                }
            }
        }
    }

    private Cls Classify(Transform t)
    {
        Cls known;
        if (cache.TryGetValue(t, out known)) return known;

        Cls result = new Cls();
        result.kind = ScanKind.None;
        result.root = null;

        if (!t.IsChildOf(owner.transform))
        {
            Transform cur = t;
            for (int depth = 0; depth < 4 && cur != null; depth++)
            {
                ScannerMarker marker = cur.GetComponent<ScannerMarker>();
                if (marker != null)
                {
                    if (marker.kind == ScannerMarker.Kind.Ignore) break;
                    result.kind = marker.kind == ScannerMarker.Kind.Trap ? ScanKind.Trap : ScanKind.Collectable;
                    result.root = cur;
                    break;
                }

                int bit = 1 << cur.gameObject.layer;
                if ((s.trapLayers.value & bit) != 0 || NameHas(cur.name, s.trapNames))
                {
                    result.kind = ScanKind.Trap;
                    result.root = cur;
                    break;
                }
                if ((s.collectableLayers.value & bit) != 0 || NameHas(cur.name, s.collectableNames))
                {
                    result.kind = ScanKind.Collectable;
                    result.root = cur;
                    break;
                }

                cur = cur.parent;
            }
        }

        cache[t] = result;
        return result;
    }

    private static bool NameHas(string objectName, string[] words)
    {
        if (words == null) return false;
        for (int i = 0; i < words.Length; i++)
        {
            if (string.IsNullOrEmpty(words[i])) continue;
            if (objectName.IndexOf(words[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ the arrows on the screen

    private void EnsureOverlay()
    {
        if (overlayRoot != null) return;

        overlayRoot = new GameObject("ScannerOverlay", typeof(RectTransform), typeof(Canvas));
        canvas = overlayRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.SetParent(overlayRoot.transform, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.zero;
        labelRect.pivot = new Vector2(0.5f, 0.5f);
        labelRect.sizeDelta = new Vector2(1000f, 40f);

        label = labelObject.GetComponent<TextMeshProUGUI>();
        label.fontSize = 22f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
        label.outlineWidth = 0.25f;
        label.outlineColor = new Color32(0, 0, 0, 255);
    }

    private Image GetArrow(int index)
    {
        while (arrows.Count <= index)
        {
            GameObject go = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(overlayRoot.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);

            Image image = go.GetComponent<Image>();
            image.sprite = ArrowSprite();
            image.raycastTarget = false;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(2f, -2f);

            arrows.Add(image);
        }
        return arrows[index];
    }

    // An upward-pointing triangle, made once.
    private static Sprite ArrowSprite()
    {
        if (arrowSprite == null)
        {
            const int size = 32;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < size; y++)
            {
                float half = (size - 1 - y) / (float)(size - 1) * (size * 0.48f);
                for (int x = 0; x < size; x++)
                {
                    bool inside = Mathf.Abs(x + 0.5f - size * 0.5f) <= half;
                    tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
                }
            }
            tex.Apply();
            arrowSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
        return arrowSprite;
    }

    private void UpdateOverlay()
    {
        EnsureOverlay();

        if (cam == null)
        {
            overlayRoot.SetActive(false);
            return;
        }
        overlayRoot.SetActive(true);

        Rect view = cam.pixelRect;
        float scale = canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
        float unit = Mathf.Max(20f, Screen.height * 0.035f);

        RectTransform labelRect = label.rectTransform;
        labelRect.anchoredPosition = new Vector2(view.center.x, view.yMax - unit * 1.5f) / scale;
        label.text = frozen ? "SCANNING   move keys: look around   release: resume" : "SCANNER LENS";

        float edge = unit * 1.3f;
        Vector2 center = view.center;
        float halfW = Mathf.Max(1f, view.width * 0.5f - edge);
        float halfH = Mathf.Max(1f, view.height * 0.5f - edge);
        float alpha = 0.7f + 0.3f * Mathf.Sin(Time.unscaledTime * 6f);
        Color tint = s.arrowColor;
        tint.a = alpha;

        int used = 0;
        for (int i = 0; i < collectables.Count; i++)
        {
            Transform t = collectables[i];
            if (t == null || !t.gameObject.activeInHierarchy) continue;

            Vector3 screen = cam.WorldToScreenPoint(t.position);
            Vector2 sp = new Vector2(screen.x, screen.y);
            Vector2 offset = sp - center;

            Vector2 position;
            float angle;
            if (Mathf.Abs(offset.x) <= halfW && Mathf.Abs(offset.y) <= halfH)
            {
                // On screen: the arrow floats just above the collectable and points down at it.
                position = sp + new Vector2(0f, unit * 1.2f + Mathf.Sin(Time.unscaledTime * 5f) * unit * 0.15f);
                angle = 180f;
            }
            else
            {
                // Off screen: the arrow sits on the edge of this player's view and points toward it.
                float kx = halfW / Mathf.Max(0.0001f, Mathf.Abs(offset.x));
                float ky = halfH / Mathf.Max(0.0001f, Mathf.Abs(offset.y));
                position = center + offset * Mathf.Min(kx, ky);
                angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg - 90f;
            }

            Image arrow = GetArrow(used);
            used++;
            arrow.gameObject.SetActive(true);
            arrow.color = tint;
            RectTransform rect = arrow.rectTransform;
            rect.sizeDelta = new Vector2(unit, unit) / scale;
            rect.anchoredPosition = position / scale;
            rect.localEulerAngles = new Vector3(0f, 0f, angle);
        }

        for (int i = used; i < arrows.Count; i++) arrows[i].gameObject.SetActive(false);
    }
}
