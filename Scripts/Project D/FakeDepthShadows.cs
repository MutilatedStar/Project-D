using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;
using Cainos.PixelArtTopDown_Basic;

/// <summary>
/// EFFECT 2 - fake 3D. Everything in the scene throws a soft drop shadow in ONE direction you choose with the ANGLE slider: every
/// Tilemap (walls, props, and also the tiles drawn ABOVE the player, like roofs and tree tops) and every sprite (players, foes,
/// crates, pickups). Each thing is shadowed by a dark copy of itself, moved along the angle and drawn just under it, so it looks lifted
/// off the ground.
///
/// HEIGHT: the further a Sorting Layer is "up", the further its shadow lands. Give each Sorting Layer a height in "Layer Heights"
/// (1 = the normal distance, 2 = twice as far, 0 = no shadow at all, use that for the ground). Unlisted layers use Default Height.
/// A layer ABOVE the player throws its shadow onto the player, which is exactly what makes a roof or a branch feel like it is overhead.
///
/// ANGLE: 0 = shadows fall to the right, 90 = up, 180 = left, -90 = down. Drag the Angle slider while playing to see it. DepthEdgeShadows
/// and the light rays follow this angle too. From your own code: FakeDepthShadows.Current.SetAngle(30f);
///
/// JUMPING: the player's raised jump sprite (TopDownJump) keeps its shadow on the ground and the shadow slides further away the higher
/// the jump goes (Jump Shadow Spread).
///
/// LIGHT RAYS: TopDownLightRay zones hide these shadows. Tile shadows fade a tile at a time, sprite shadows fade as the sprite walks in.
///
/// SET UP
///   1. Put this on an empty object (for example "Fake Depth Shadows"). Keep it at scale 1, at the world origin.
///   2. Press Play. Everything that is not on a height-0 layer now has a shadow. Drag Angle and Length until it looks right.
///   3. In Layer Heights, set your ground layers to 0 and any layers that are really high (roofs, tree tops) to 2 or 3.
///   4. If something should not throw a shadow, add part of its name to "Ignore Names" or put it on a physics layer in "Ignore Physics Layers".
///
/// Tile shadows are made from copies of your tiles that are renamed "FakeShadow" (lava, water and spike tiles are skipped), so the
/// hazard scripts never read them. Shadows of two things that overlap add up, so keep the opacity modest.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(150)]
public class FakeDepthShadows : MonoBehaviour
{
    public static FakeDepthShadows Current { get; private set; }

    private static readonly HashSet<Renderer> ignored = new HashSet<Renderer>();

    /// <summary>Tell the shadows never to copy this renderer (your own effects, light overlays...).</summary>
    public static void Ignore(Renderer r)
    {
        if (r != null) ignored.Add(r);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Current = null;
        ignored.Clear();
    }

    [Serializable]
    public class LayerHeight
    {
        [Tooltip("Sorting Layer NAME (Project Settings > Tags and Layers).")]
        public string sortingLayer;
        [Tooltip("0 = no shadow (the ground). 1 = normal. 2 = the shadow lands twice as far away.")]
        [Min(0f)] public float height = 1f;
    }

    // ------------------------------------------------------------ inspector

    [Header("Angle and size")]
    [Tooltip("The way shadows fall. 0 = right, 90 = up, 180 = left, -90 = down. Change it while playing.")]
    [SerializeField, Range(-180f, 180f)] private float angle = -45f;
    [Tooltip("How far the shadow of a height-1 thing lands from it, in world units.")]
    [SerializeField, Min(0f)] private float length = 0.25f;
    [Tooltip("Black with a see-through alpha looks best.")]
    [SerializeField] private Color shadowColor = new Color(0f, 0f, 0f, 0.35f);

    [Header("What throws a shadow")]
    [SerializeField] private bool includeTilemaps = true;
    [SerializeField] private bool includeSprites = true;
    [Tooltip("Height of each Sorting Layer. 0 = that layer throws no shadow.")]
    [SerializeField] private LayerHeight[] layerHeights = new LayerHeight[0];
    [Tooltip("Height of every Sorting Layer that is not listed above.")]
    [SerializeField, Min(0f)] private float defaultHeight = 1f;
    [Tooltip("Anything whose name contains one of these is left alone. Capitals do not matter.")]
    [SerializeField] private string[] ignoreNames = { "Fake Shadow", "JumpShadow", "Light Ray Visual", "Glow" };
    [Tooltip("Objects on these physics layers are left alone.")]
    [SerializeField] private LayerMask ignorePhysicsLayers = 0;
    [Tooltip("Tiles whose tile or sprite name contains one of these get no shadow (animated hazards).")]
    [SerializeField] private string[] skipTileNames = { "lava", "water", "spike" };

    [Header("Draw order")]
    [Tooltip("Added to the Order In Layer of the thing the shadow belongs to. -1 = just under it.")]
    [SerializeField] private int shadowOrderOffset = -1;

    [Header("Jumping")]
    [Tooltip("How much further a jumping player's shadow slides for every world unit of jump height. 0 = it stays put.")]
    [SerializeField, Min(0f)] private float jumpShadowSpread = 1f;

    [Header("Light rays")]
    [Tooltip("ON: TopDownLightRay zones hide these shadows.")]
    [SerializeField] private bool respectLightRays = true;

    [Header("Performance")]
    [Tooltip("How often the scene is searched for new things to shadow (foes that spawn, pickups...).")]
    [SerializeField, Min(0.1f)] private float scanSeconds = 0.5f;
    [Tooltip("The most sprites that get a shadow at once.")]
    [SerializeField, Min(1)] private int maxSprites = 400;

    /// <summary>The way shadows fall, in degrees.</summary>
    public float Angle { get { return angle; } }

    /// <summary>A unit vector pointing the way shadows fall.</summary>
    public Vector2 Direction
    {
        get
        {
            float a = angle * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
    }

    public void SetAngle(float degrees) { angle = Mathf.DeltaAngle(0f, degrees); }

    // ------------------------------------------------------------ data

    private class SpriteEntry
    {
        public SpriteRenderer source;
        public SpriteRenderer layerSource;   // where the sorting comes from (the body, for a jump copy)
        public SpriteRenderer shadow;
        public Transform shadowT;
        public TopDownJump jump;
        public bool isJumpCopy;
        public float height;
    }

    private class TileEntry
    {
        public Tilemap source;
        public TilemapRenderer sourceRenderer;
        public Tilemap clone;
        public TilemapRenderer cloneRenderer;
        public float height;
        public bool fillDirty = true;
        public bool lightDirty = true;
        public float nextFill;
        public float nextLight;
        public int lightVersion = -1;
        public Vector3 lastOffset = new Vector3(float.NaN, 0f, 0f);
    }

    private readonly List<SpriteEntry> spriteEntries = new List<SpriteEntry>();
    private readonly List<TileEntry> tileEntries = new List<TileEntry>();
    private readonly Dictionary<Tilemap, TileEntry> tileBySource = new Dictionary<Tilemap, TileEntry>();
    private readonly HashSet<SpriteRenderer> spriteSources = new HashSet<SpriteRenderer>();
    private readonly Dictionary<int, float> heights = new Dictionary<int, float>();
    private readonly Dictionary<Sprite, Tile> tileCache = new Dictionary<Sprite, Tile>();
    private readonly List<UnityEngine.Object> madeAssets = new List<UnityEngine.Object>();
    private float nextScan;

    // ------------------------------------------------------------ lifecycle

    private void OnEnable()
    {
        Current = this;
        Tilemap.tilemapTileChanged += OnTileChanged;
        nextScan = 0f;
    }

    private void OnDisable()
    {
        Tilemap.tilemapTileChanged -= OnTileChanged;
        if (Current == this) Current = null;
        ClearAll();
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + scanSeconds;
            Scan();
        }
    }

    private void LateUpdate()
    {
        SyncTiles();
        SyncSprites();
    }

    /// <summary>Forget every shadow and make them again from scratch.</summary>
    [ContextMenu("Rebuild all shadows")]
    public void Rebuild()
    {
        ClearAll();
        nextScan = 0f;
    }

    private void OnTileChanged(Tilemap map, Tilemap.SyncTile[] tiles)
    {
        TileEntry e;
        if (map != null && tileBySource.TryGetValue(map, out e))
        {
            e.fillDirty = true;
            e.nextFill = Time.unscaledTime + 0.05f;
        }
    }

    // ------------------------------------------------------------ finding things

    private void BuildHeights()
    {
        heights.Clear();
        if (layerHeights == null) return;

        for (int i = 0; i < layerHeights.Length; i++)
        {
            LayerHeight lh = layerHeights[i];
            if (lh == null || string.IsNullOrEmpty(lh.sortingLayer)) continue;

            int id = SortingLayer.NameToID(lh.sortingLayer);
            if (!SortingLayer.IsValid(id))
            {
                Debug.LogWarning("FakeDepthShadows: there is no sorting layer called \"" + lh.sortingLayer + "\". That entry is ignored.", this);
                continue;
            }
            heights[id] = lh.height;
        }
    }

    private float HeightOf(int sortingLayerId)
    {
        float h;
        return heights.TryGetValue(sortingLayerId, out h) ? h : defaultHeight;
    }

    private bool IsAllowed(GameObject go)
    {
        if ((ignorePhysicsLayers.value & (1 << go.layer)) != 0) return false;

        string n = go.name;
        for (int i = 0; i < ignoreNames.Length; i++)
        {
            if (string.IsNullOrEmpty(ignoreNames[i])) continue;
            if (n.IndexOf(ignoreNames[i], StringComparison.OrdinalIgnoreCase) >= 0) return false;
        }
        return true;
    }

    private void Scan()
    {
        BuildHeights();
        ignored.RemoveWhere(r => r == null);

        if (includeTilemaps)
        {
            Tilemap[] maps = FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
            for (int i = 0; i < maps.Length; i++)
            {
                Tilemap tm = maps[i];
                if (tm == null || tileBySource.ContainsKey(tm) || tm.transform.IsChildOf(transform)) continue;
                if (tm.name.StartsWith("Fake Shadow Tilemap", StringComparison.Ordinal)) continue;

                TilemapRenderer tr = tm.GetComponent<TilemapRenderer>();
                if (tr == null || ignored.Contains(tr) || !IsAllowed(tm.gameObject)) continue;

                float h = HeightOf(tr.sortingLayerID);
                if (h <= 0f) continue;

                AddTile(tm, tr, h);
            }
        }

        if (includeSprites)
        {
            // Inactive objects are included: the player's jump copy (TopDownJump) is switched off until the first jump.
            SpriteRenderer[] sprites = FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < sprites.Length; i++)
            {
                if (spriteEntries.Count >= maxSprites) break;

                SpriteRenderer r = sprites[i];
                if (r == null || spriteSources.Contains(r) || ignored.Contains(r)) continue;
                if (r.transform.IsChildOf(transform)) continue;
                if (!IsAllowed(r.gameObject)) continue;

                // The jump copy of a player (TopDownJump) is drawn raised; its shadow belongs on the ground, on the body's layer.
                TopDownJump jump = r.GetComponentInParent<TopDownJump>();
                bool jumpCopy = jump != null && r.name == "JumpSprite";
                SpriteRenderer layerSource = r;
                if (jumpCopy)
                {
                    SpriteRenderer body = jump.GetComponent<SpriteRenderer>();
                    if (body != null) layerSource = body;
                }

                float h = HeightOf(layerSource.sortingLayerID);
                if (h <= 0f) continue;

                AddSprite(r, layerSource, jump, jumpCopy, h);
            }
        }
    }

    // ------------------------------------------------------------ sprite shadows

    private void AddSprite(SpriteRenderer source, SpriteRenderer layerSource, TopDownJump jump, bool jumpCopy, float height)
    {
        GameObject go = new GameObject("Fake Shadow");
        go.layer = source.gameObject.layer;
        go.transform.SetParent(transform, false);

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.enabled = false;
        Ignore(sr);

        SpriteEntry e = new SpriteEntry
        {
            source = source,
            layerSource = layerSource,
            shadow = sr,
            shadowT = go.transform,
            jump = jump,
            isJumpCopy = jumpCopy,
            height = height
        };
        spriteEntries.Add(e);
        spriteSources.Add(source);
    }

    private void SyncSprites()
    {
        Vector2 dir = Direction;

        for (int i = spriteEntries.Count - 1; i >= 0; i--)
        {
            SpriteEntry e = spriteEntries[i];
            SpriteRenderer s = e.source;

            if (s == null || e.shadow == null)
            {
                RemoveSpriteAt(i);
                continue;
            }

            bool visible = s.enabled && s.gameObject.activeInHierarchy && s.sprite != null && s.color.a > 0.01f;
            if (!visible)
            {
                if (e.shadow.enabled) e.shadow.enabled = false;
                continue;
            }

            float lift = (e.isJumpCopy && e.jump != null) ? e.jump.Height : 0f;
            Transform st = s.transform;

            Vector3 basePos = st.position - new Vector3(0f, lift, 0f);
            float reach = length * (e.height + lift * jumpShadowSpread);
            Vector3 pos = basePos + new Vector3(dir.x * reach, dir.y * reach, 0f);

            e.shadowT.SetPositionAndRotation(pos, st.rotation);
            e.shadowT.localScale = st.lossyScale;

            SpriteRenderer sr = e.shadow;
            sr.sprite = s.sprite;
            sr.flipX = s.flipX;
            sr.flipY = s.flipY;
            if (s.drawMode != SpriteDrawMode.Simple)
            {
                sr.drawMode = s.drawMode;
                sr.size = s.size;
            }

            SpriteRenderer sorter = e.layerSource != null ? e.layerSource : s;
            sr.sortingLayerID = sorter.sortingLayerID;
            sr.sortingOrder = sorter.sortingOrder + shadowOrderOffset;

            Color c = shadowColor;
            c.a *= s.color.a;
            if (respectLightRays && TopDownLightRay.Count > 0)
            {
                c.a *= 1f - TopDownLightRay.LightAt(sr.bounds.center);
            }
            sr.color = c;
            sr.enabled = c.a > 0.004f;
        }
    }

    private void RemoveSpriteAt(int index)
    {
        SpriteEntry e = spriteEntries[index];
        if (e.source != null) spriteSources.Remove(e.source);
        if (e.shadow != null) Destroy(e.shadow.gameObject);
        spriteEntries.RemoveAt(index);
        spriteSources.RemoveWhere(r => r == null);
    }

    // ------------------------------------------------------------ tile shadows

    private void AddTile(Tilemap source, TilemapRenderer sourceRenderer, float height)
    {
        GameObject go = new GameObject("Fake Shadow Tilemap (" + source.name + ")");
        go.layer = source.gameObject.layer;
        go.transform.SetParent(source.transform.parent, false);   // same Grid, so the cells line up

        Tilemap clone = go.AddComponent<Tilemap>();
        clone.tileAnchor = source.tileAnchor;
        clone.color = shadowColor;

        TilemapRenderer cr = go.AddComponent<TilemapRenderer>();
        cr.mode = sourceRenderer.mode;
        Ignore(cr);

        TileEntry e = new TileEntry
        {
            source = source,
            sourceRenderer = sourceRenderer,
            clone = clone,
            cloneRenderer = cr,
            height = height
        };
        tileEntries.Add(e);
        tileBySource[source] = e;
    }

    private void SyncTiles()
    {
        Vector2 dir = Direction;

        for (int i = tileEntries.Count - 1; i >= 0; i--)
        {
            TileEntry e = tileEntries[i];
            if (e.source == null || e.clone == null)
            {
                RemoveTileAt(i);
                continue;
            }

            Transform st = e.source.transform;
            Transform ct = e.clone.transform;

            // Moved along the angle, in the Grid's own space.
            Vector3 worldOffset = new Vector3(dir.x, dir.y, 0f) * (length * e.height);
            Transform parent = ct.parent;
            Vector3 localOffset = parent != null ? parent.InverseTransformVector(worldOffset) : worldOffset;
            ct.localPosition = st.localPosition + localOffset;
            ct.localRotation = st.localRotation;
            ct.localScale = st.localScale;

            e.cloneRenderer.enabled = e.sourceRenderer.enabled && st.gameObject.activeInHierarchy;
            e.cloneRenderer.sortingLayerID = e.sourceRenderer.sortingLayerID;
            e.cloneRenderer.sortingOrder = e.sourceRenderer.sortingOrder + shadowOrderOffset;
            if (e.clone.color != shadowColor) e.clone.color = shadowColor;

            if (e.fillDirty && Time.unscaledTime >= e.nextFill)
            {
                FillClone(e);
                e.fillDirty = false;
                e.lightDirty = true;
            }

            if ((worldOffset - e.lastOffset).sqrMagnitude > 0.00001f || float.IsNaN(e.lastOffset.x))
            {
                e.lastOffset = worldOffset;
                e.lightDirty = true;
            }
            if (respectLightRays && e.lightVersion != TopDownLightRay.Version)
            {
                e.lightVersion = TopDownLightRay.Version;
                e.lightDirty = true;
            }

            if (e.lightDirty && Time.unscaledTime >= e.nextLight)
            {
                e.lightDirty = false;
                e.nextLight = Time.unscaledTime + 0.1f;
                ApplyLight(e);
            }
        }
    }

    // Copies the source's tiles onto the shadow Tilemap as plain "FakeShadow" tiles.
    private void FillClone(TileEntry e)
    {
        Tilemap src = e.source;
        Tilemap dst = e.clone;
        dst.ClearAllTiles();

        List<TileChangeData> changes = new List<TileChangeData>();
        foreach (Vector3Int pos in src.cellBounds.allPositionsWithin)
        {
            TileBase t = src.GetTile(pos);
            if (t == null) continue;

            Sprite s = src.GetSprite(pos);
            if (s == null || IsSkippedTile(t, s)) continue;

            changes.Add(new TileChangeData(pos, ShadowTileFor(s), Color.white, src.GetTransformMatrix(pos)));
        }

        if (changes.Count > 0) dst.SetTiles(changes.ToArray(), true);
    }

    // Tiles under a light ray fade out (a tile at a time).
    private void ApplyLight(TileEntry e)
    {
        Tilemap dst = e.clone;
        bool use = respectLightRays && TopDownLightRay.Count > 0;

        foreach (Vector3Int pos in dst.cellBounds.allPositionsWithin)
        {
            if (!dst.HasTile(pos)) continue;

            float hide = use ? TopDownLightRay.LightAt(dst.GetCellCenterWorld(pos)) : 0f;
            Color c = new Color(1f, 1f, 1f, 1f - hide);
            if (dst.GetColor(pos) != c) dst.SetColor(pos, c);
        }
    }

    private bool IsSkippedTile(TileBase t, Sprite s)
    {
        if (skipTileNames == null) return false;

        string names = t.name + "|" + s.name;
        for (int i = 0; i < skipTileNames.Length; i++)
        {
            if (string.IsNullOrEmpty(skipTileNames[i])) continue;
            if (names.IndexOf(skipTileNames[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // One plain tile per source sprite, using a renamed copy of the sprite so no script mistakes it for lava, water or spikes.
    private Tile ShadowTileFor(Sprite s)
    {
        Tile t;
        if (tileCache.TryGetValue(s, out t) && t != null) return t;

        Sprite shadowSprite = s;
        if (!s.packed)
        {
            Rect r = s.rect;
            Vector2 pivot = new Vector2(s.pivot.x / r.width, s.pivot.y / r.height);
            shadowSprite = Sprite.Create(s.texture, r, pivot, s.pixelsPerUnit, 0u, SpriteMeshType.FullRect);
            shadowSprite.name = "FakeShadow";
            madeAssets.Add(shadowSprite);
        }

        t = ScriptableObject.CreateInstance<Tile>();
        t.name = "FakeShadow";
        t.sprite = shadowSprite;
        t.color = Color.white;
        t.flags = TileFlags.None;
        t.colliderType = Tile.ColliderType.None;
        madeAssets.Add(t);

        tileCache[s] = t;
        return t;
    }

    private void RemoveTileAt(int index)
    {
        TileEntry e = tileEntries[index];
        if (e.source != null) tileBySource.Remove(e.source);
        if (e.clone != null) Destroy(e.clone.gameObject);
        tileEntries.RemoveAt(index);

        // A source that was destroyed leaves a dead key behind.
        List<Tilemap> dead = null;
        foreach (KeyValuePair<Tilemap, TileEntry> kv in tileBySource)
        {
            if (kv.Key == null)
            {
                if (dead == null) dead = new List<Tilemap>();
                dead.Add(kv.Key);
            }
        }
        if (dead != null)
        {
            for (int i = 0; i < dead.Count; i++) tileBySource.Remove(dead[i]);
        }
    }

    // ------------------------------------------------------------ cleanup

    private void ClearAll()
    {
        for (int i = 0; i < spriteEntries.Count; i++)
        {
            if (spriteEntries[i].shadow != null) Destroy(spriteEntries[i].shadow.gameObject);
        }
        spriteEntries.Clear();
        spriteSources.Clear();

        for (int i = 0; i < tileEntries.Count; i++)
        {
            if (tileEntries[i].clone != null) Destroy(tileEntries[i].clone.gameObject);
        }
        tileEntries.Clear();
        tileBySource.Clear();

        for (int i = 0; i < madeAssets.Count; i++)
        {
            if (madeAssets[i] != null) Destroy(madeAssets[i]);
        }
        madeAssets.Clear();
        tileCache.Clear();
    }
}
