using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Tilemaps;

/// <summary>
/// EFFECT 1 - depth shadows. Lower tiles that sit NEXT TO higher tiles get a shadow cast onto them, so a raised block, a wall or a
/// ledge looks like it stands above the floor beside it.
///
/// HOW IT WORKS
///   You list your height LEVELS from the lowest to the highest. Each level is just the Tilemaps that live at that height (for example
///   Level 0 = the floor Tilemaps, Level 1 = the Tilemaps of the first raised layer, Level 2 = the next one up).
///   Every tile of a lower level that has a higher tile next to it, on the side the light comes from, gets a soft shadow. A bigger drop
///   (two levels) throws a longer shadow. Tiles that have a higher tile ON TOP of them are skipped.
///   The shadow is one mesh per level, built once and again whenever a tile changes, the angle changes or a light ray moves.
///
/// ANGLE: by default it follows FakeDepthShadows' angle, so both effects always agree. Untick "Follow Global Angle" to use its own.
/// LIGHT RAYS: TopDownLightRay zones hide this shadow inside their area (smoothly, a quarter tile at a time).
///
/// SET UP
///   1. Put this on an empty object (for example "Depth Shadows"). Keep that object at scale 1.
///   2. Fill in "Levels": lowest first. Drag in the Tilemaps of each level. You need at least two levels.
///   3. Press Play. Tune Shadow Length, Colour and Falloff. Resolution 4 gives pixel steps of a quarter tile.
///   4. If the shadow hides under the floor or over your props, change Sorting Order Offset (it is drawn on the lower level's
///      Sorting Layer, above that level's Tilemaps).
/// </summary>
[DisallowMultipleComponent]
public class DepthEdgeShadows : MonoBehaviour
{
    [System.Serializable]
    public class HeightLevel
    {
        [Tooltip("Just a name for you.")]
        public string name = "Level";
        [Tooltip("Every Tilemap at this height.")]
        public Tilemap[] tilemaps;
    }

    [Header("Levels (lowest first)")]
    [SerializeField] private HeightLevel[] levels = new HeightLevel[0];

    [Header("Light")]
    [Tooltip("ON: use FakeDepthShadows' angle so both effects agree.")]
    [SerializeField] private bool followGlobalAngle = true;
    [Tooltip("The way shadows fall when Follow Global Angle is off or there is no FakeDepthShadows. 0 = right, 90 = up, -90 = down.")]
    [SerializeField, Range(-180f, 180f)] private float angle = -45f;

    [Header("Shadow")]
    [Tooltip("How far a shadow reaches onto the lower ground for ONE level of drop, in world units.")]
    [SerializeField, Min(0.05f)] private float shadowLength = 0.6f;
    [Tooltip("ON: a drop of two levels throws a shadow twice as long, three levels three times.")]
    [SerializeField] private bool longerForBiggerDrop = true;
    [SerializeField] private Color shadowColor = new Color(0f, 0f, 0f, 0.45f);
    [Tooltip("1 = the shadow fades evenly away from the wall. Higher = dark at the wall and a quick fade.")]
    [SerializeField, Range(0.5f, 4f)] private float falloff = 1.5f;
    [Tooltip("Pieces per tile, each way. 4 = quarter-tile steps (suits pixel art), 8 = smoother.")]
    [SerializeField, Range(1, 8)] private int resolution = 4;

    [Header("Draw order")]
    [Tooltip("Added to the highest Order In Layer of the lower level's Tilemaps. The shadow is drawn on that level's Sorting Layer.")]
    [SerializeField] private int sortingOrderOffset = 1;

    [Header("Light rays")]
    [Tooltip("ON: TopDownLightRay zones hide this shadow.")]
    [SerializeField] private bool respectLightRays = true;
    [Tooltip("The shortest time between two rebuilds, so dragging the angle in the Inspector stays smooth.")]
    [SerializeField, Min(0.02f)] private float rebuildDelay = 0.1f;

    private readonly List<GameObject> built = new List<GameObject>();
    private readonly Dictionary<Tilemap, int> levelOf = new Dictionary<Tilemap, int>();
    private static Material sharedMaterial;

    private bool dirty = true;
    private float nextBuild;
    private float builtAngle = float.NaN;
    private int lightVersion = -1;
    private float settingsSig;

    // ------------------------------------------------------------ lifecycle

    private void OnEnable()
    {
        Tilemap.tilemapTileChanged += OnTileChanged;
        FloorManager.OnFloorChanged += OnFloorChanged;
        FloorManager.OnFloorRestarted += MarkDirty;
        dirty = true;
    }

    private void OnDisable()
    {
        Tilemap.tilemapTileChanged -= OnTileChanged;
        FloorManager.OnFloorChanged -= OnFloorChanged;
        FloorManager.OnFloorRestarted -= MarkDirty;
        ClearMeshes();
    }

    private void Update()
    {
        float a = CurrentAngle();
        if (float.IsNaN(builtAngle) || Mathf.Abs(Mathf.DeltaAngle(a, builtAngle)) > 0.05f) dirty = true;
        if (respectLightRays && TopDownLightRay.Version != lightVersion) dirty = true;

        float sig = shadowLength * 3.1f + shadowColor.a * 5.3f + falloff * 7.1f + resolution * 11f + sortingOrderOffset * 13f +
                    (longerForBiggerDrop ? 17f : 0f) + (respectLightRays ? 19f : 0f);
        if (!Mathf.Approximately(sig, settingsSig))
        {
            settingsSig = sig;
            dirty = true;
        }

        if (dirty && Time.unscaledTime >= nextBuild) Rebuild();
    }

    private void OnTileChanged(Tilemap map, Tilemap.SyncTile[] tiles)
    {
        if (map != null && levelOf.ContainsKey(map)) dirty = true;
    }

    private void OnFloorChanged(int floor) { dirty = true; }
    private void MarkDirty() { dirty = true; }

    /// <summary>Rebuilds the shadows on the next frame (call it after you swap tiles in a way no event reports).</summary>
    public void Refresh() { dirty = true; }

    private float CurrentAngle()
    {
        if (followGlobalAngle && FakeDepthShadows.Current != null) return FakeDepthShadows.Current.Angle;
        return angle;
    }

    // ------------------------------------------------------------ building

    private void Rebuild()
    {
        dirty = false;
        nextBuild = Time.unscaledTime + rebuildDelay;
        lightVersion = TopDownLightRay.Version;
        builtAngle = CurrentAngle();

        ClearMeshes();
        levelOf.Clear();

        if (levels == null || levels.Length < 2) return;

        Grid grid = null;
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] == null || levels[i].tilemaps == null) continue;
            for (int k = 0; k < levels[i].tilemaps.Length; k++)
            {
                Tilemap tm = levels[i].tilemaps[k];
                if (tm == null) continue;
                levelOf[tm] = i;
                if (grid == null) grid = tm.layoutGrid;
            }
        }
        if (grid == null) return;

        int n = levels.Length;

        // Which cells each level fills, and the HIGHEST level on every cell.
        HashSet<Vector3Int>[] occupied = new HashSet<Vector3Int>[n];
        Dictionary<Vector3Int, int> top = new Dictionary<Vector3Int, int>();
        for (int i = 0; i < n; i++)
        {
            occupied[i] = Collect(levels[i], grid);
            foreach (Vector3Int c in occupied[i]) top[c] = i;   // later (higher) levels overwrite
        }

        float a = builtAngle * Mathf.Deg2Rad;
        Vector3 shadowDir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
        Vector3 toLight = -shadowDir;

        Vector3 origin = grid.CellToWorld(Vector3Int.zero);
        Vector3 ex = grid.CellToWorld(Vector3Int.right) - origin;
        Vector3 ey = grid.CellToWorld(Vector3Int.up) - origin;
        float cellMin = Mathf.Max(0.01f, Mathf.Min(ex.magnitude, ey.magnitude));
        int res = Mathf.Max(1, resolution);
        float step = cellMin / Mathf.Max(2, res);

        for (int i = 0; i < n - 1; i++)
        {
            int maxGap = longerForBiggerDrop ? (n - 1 - i) : 1;
            float maxReach = shadowLength * maxGap;

            List<Vector3> verts = new List<Vector3>();
            List<Color32> cols = new List<Color32>();
            List<int> tris = new List<int>();

            GameObject go = new GameObject("Depth Edge Shadow (" + (levels[i] != null ? levels[i].name : i.ToString()) + ")");
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Transform mt = go.transform;

            foreach (Vector3Int cell in occupied[i])
            {
                if (top[cell] != i) continue;   // something higher is on this tile: it hides the shadow anyway

                Vector3 min = grid.CellToWorld(cell);
                Vector3 centre = min + (ex + ey) * 0.5f;

                // Quick reject: nothing higher anywhere near this tile on the light side.
                if (!AnyHigher(top, grid, centre, toLight, maxReach + cellMin * 1.5f, cellMin * 0.5f, i)) continue;

                for (int sy = 0; sy < res; sy++)
                {
                    for (int sx = 0; sx < res; sx++)
                    {
                        float fx0 = sx / (float)res, fx1 = (sx + 1) / (float)res;
                        float fy0 = sy / (float)res, fy1 = (sy + 1) / (float)res;
                        Vector3 p = min + ex * ((fx0 + fx1) * 0.5f) + ey * ((fy0 + fy1) * 0.5f);

                        float strength = ShadowAt(top, grid, p, toLight, step, maxReach, i);
                        if (strength <= 0f) continue;

                        float alpha = shadowColor.a * strength;
                        if (respectLightRays) alpha *= 1f - TopDownLightRay.LightAt(p);
                        if (alpha < 0.004f) continue;

                        Color32 col = new Color(shadowColor.r, shadowColor.g, shadowColor.b, alpha);
                        int baseIndex = verts.Count;
                        verts.Add(mt.InverseTransformPoint(min + ex * fx0 + ey * fy0));
                        verts.Add(mt.InverseTransformPoint(min + ex * fx1 + ey * fy0));
                        verts.Add(mt.InverseTransformPoint(min + ex * fx1 + ey * fy1));
                        verts.Add(mt.InverseTransformPoint(min + ex * fx0 + ey * fy1));
                        cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                        tris.Add(baseIndex); tris.Add(baseIndex + 2); tris.Add(baseIndex + 1);
                        tris.Add(baseIndex); tris.Add(baseIndex + 3); tris.Add(baseIndex + 2);
                    }
                }
            }

            if (verts.Count == 0)
            {
                Destroy(go);
                continue;
            }

            Mesh mesh = new Mesh();
            mesh.name = "DepthEdgeShadow";
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            int layerId, order;
            GetSorting(levels[i], out layerId, out order);
            mr.sortingLayerID = layerId;
            mr.sortingOrder = order;

            FakeDepthShadows.Ignore(mr);
            built.Add(go);
        }
    }

    // How dark the shadow is at this point (0 = none): march towards the light and look for a higher tile.
    private float ShadowAt(Dictionary<Vector3Int, int> top, Grid grid, Vector3 p, Vector3 toLight, float step, float maxReach, int myLevel)
    {
        float best = 0f;
        for (float d = step; d <= maxReach + 0.0001f; d += step)
        {
            Vector3Int c = grid.WorldToCell(p + toLight * d);
            c.z = 0;
            int lvl;
            if (!top.TryGetValue(c, out lvl) || lvl <= myLevel) continue;

            float allowed = shadowLength * (longerForBiggerDrop ? (lvl - myLevel) : 1);
            if (d > allowed) continue;

            float t = d / allowed;
            float s = Mathf.Pow(1f - t, falloff);
            if (s > best) best = s;
        }
        return best;
    }

    private static bool AnyHigher(Dictionary<Vector3Int, int> top, Grid grid, Vector3 from, Vector3 toLight, float reach, float step, int myLevel)
    {
        for (float d = 0f; d <= reach; d += step)
        {
            Vector3Int c = grid.WorldToCell(from + toLight * d);
            c.z = 0;
            int lvl;
            if (top.TryGetValue(c, out lvl) && lvl > myLevel) return true;
        }
        return false;
    }

    private static HashSet<Vector3Int> Collect(HeightLevel level, Grid grid)
    {
        HashSet<Vector3Int> set = new HashSet<Vector3Int>();
        if (level == null || level.tilemaps == null) return set;

        for (int k = 0; k < level.tilemaps.Length; k++)
        {
            Tilemap tm = level.tilemaps[k];
            if (tm == null) continue;

            foreach (Vector3Int pos in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(pos)) continue;
                Vector3Int c = grid.WorldToCell(tm.GetCellCenterWorld(pos));
                c.z = 0;
                set.Add(c);
            }
        }
        return set;
    }

    private void GetSorting(HeightLevel level, out int layerId, out int order)
    {
        layerId = 0;
        order = 0;
        bool found = false;

        if (level != null && level.tilemaps != null)
        {
            for (int k = 0; k < level.tilemaps.Length; k++)
            {
                Tilemap tm = level.tilemaps[k];
                if (tm == null) continue;
                TilemapRenderer tr = tm.GetComponent<TilemapRenderer>();
                if (tr == null) continue;

                if (!found)
                {
                    layerId = tr.sortingLayerID;
                    order = tr.sortingOrder;
                    found = true;
                }
                else if (tr.sortingLayerID == layerId && tr.sortingOrder > order)
                {
                    order = tr.sortingOrder;
                }
            }
        }
        order += sortingOrderOffset;
    }

    private static Material GetMaterial()
    {
        if (sharedMaterial != null) return sharedMaterial;

        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
        if (sh == null)
        {
            Debug.LogWarning("DepthEdgeShadows: could not find the Sprites/Default shader, so the shadows cannot be drawn.");
            return null;
        }

        sharedMaterial = new Material(sh);
        sharedMaterial.name = "DepthEdgeShadow (made)";
        sharedMaterial.hideFlags = HideFlags.HideAndDontSave;
        return sharedMaterial;
    }

    private void ClearMeshes()
    {
        for (int i = 0; i < built.Count; i++)
        {
            GameObject go = built[i];
            if (go == null) continue;

            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) Destroy(mf.sharedMesh);
            Destroy(go);
        }
        built.Clear();
    }
}
