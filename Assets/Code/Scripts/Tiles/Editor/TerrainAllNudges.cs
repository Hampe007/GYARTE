//#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

public static class TerrainAllNudges
{
    static Terrain ActiveTerrain => Terrain.activeTerrain;
    static TerrainData TD => ActiveTerrain ? ActiveTerrain.terrainData : null;

    // last-change snapshots for clean reverts
    static float[,] lastHeights;
    static int lastHx0, lastHy0, lastHw, lastHh;

    static float[,,] lastSplat;
    static int lastSx0, lastSy0, lastSw, lastSh;

    static int[,] lastDetail;
    static int lastDx0, lastDy0, lastDSize, lastDetailLayer;

    static bool addedTree;
    static int addedTreeProtoIdx = 0;
    static Vector3 addedTreePos = new Vector3(0.5f, 0f, 0.5f);

    [MenuItem("Tools/Tiles Test/Apply ALL Terrain Changes")]
    public static void ApplyAllChanges()
    {
        if (!TD) { Debug.LogWarning("[Test] No active terrain."); return; }

        Undo.RegisterCompleteObjectUndo(TD, "Apply ALL Terrain Changes");

        bool h = NudgeHeightsTiny();
        bool a = PaintSplatRectLayer1();
        bool d = ToggleDetailPatchOn();
        bool t = EnsureTreePrototypeAndAddOne();

        var changed = new List<string>(4);
        if (h) changed.Add("heights");
        if (a) changed.Add("splatmaps");
        if (d) changed.Add("details");
        if (t) changed.Add("trees");

        Debug.Log(changed.Count > 0
            ? $"[Test] Applied changes → {string.Join(", ", changed)}"
            : "[Test] Nothing changed.");
    }

    [MenuItem("Tools/Tiles Test/Revert LAST Terrain Changes")]
    public static void RevertLastChanges()
    {
        if (!TD) { Debug.LogWarning("[Test] No active terrain."); return; }

        Undo.RegisterCompleteObjectUndo(TD, "Revert LAST Terrain Changes");

        // heights
        if (lastHeights != null && lastHw > 0 && lastHh > 0)
        {
            TD.SetHeights(lastHx0, lastHy0, lastHeights);
            lastHeights = null;
        }

        // splatmaps
        if (lastSplat != null && lastSw > 0 && lastSh > 0)
        {
            TD.SetAlphamaps(lastSx0, lastSy0, lastSplat);
            lastSplat = null;
        }

        // details
        if (lastDetail != null && lastDSize > 0)
        {
            TD.SetDetailLayer(lastDx0, lastDy0, lastDetailLayer, lastDetail);
            lastDetail = null;
        }

        // tree
        if (addedTree)
        {
            var list = new List<TreeInstance>(TD.treeInstances ?? new TreeInstance[0]);
            int idx = list.FindIndex(t =>
                t.prototypeIndex == addedTreeProtoIdx &&
                Mathf.Abs(t.position.x - addedTreePos.x) < 1e-3f &&
                Mathf.Abs(t.position.z - addedTreePos.z) < 1e-3f
            );
            if (idx >= 0) list.RemoveAt(idx);
            TD.treeInstances = list.ToArray();
            addedTree = false;
        }

        Debug.Log("[Test] Reverted last terrain test changes.");
    }

    // heights
    static bool NudgeHeightsTiny()
    {
        int res = TD.heightmapResolution;
        if (res <= 0) return false;

        int cx = res / 2, cy = res / 2;
        int r = Mathf.Max(1, res / 64);
        int x0 = Mathf.Clamp(cx - r, 0, res - 1);
        int y0 = Mathf.Clamp(cy - r, 0, res - 1);
        int w = Mathf.Min(2 * r + 1, res - x0);
        int h = Mathf.Min(2 * r + 1, res - y0);

        lastHeights = TD.GetHeights(x0, y0, w, h);
        lastHx0 = x0; lastHy0 = y0; lastHw = w; lastHh = h;

        var patch = (float[,])lastHeights.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                patch[y, x] = Mathf.Clamp01(patch[y, x] + 0.001f);

        TD.SetHeights(x0, y0, patch);
        return true;
    }

    // splatmaps
    static bool PaintSplatRectLayer1()
    {
        EnsureTwoLayers();
        if (TD.alphamapResolution <= 0 || TD.alphamapLayers == 0) return false;

        int res = TD.alphamapResolution;
        int w = Mathf.Max(4, res / 16);
        int h = Mathf.Max(4, res / 16);
        int x0 = Mathf.Clamp(res / 3, 0, res - w);
        int y0 = Mathf.Clamp(res / 3, 0, res - h);

        lastSplat = TD.GetAlphamaps(x0, y0, w, h);
        lastSx0 = x0; lastSy0 = y0; lastSw = w; lastSh = h;

        var splat = (float[,,])lastSplat.Clone();
        int layers = TD.alphamapLayers;

        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                if (layers >= 2)
                {
                    splat[yy, xx, 0] = 0f;
                    splat[yy, xx, 1] = 1f;
                    for (int l = 2; l < layers; l++) splat[yy, xx, l] = 0f;
                }
                else
                {
                    splat[yy, xx, 0] = 1f;
                }
            }

        TD.SetAlphamaps(x0, y0, splat);
        return true;
    }

    static void EnsureTwoLayers()
    {
        var layers = new List<TerrainLayer>();
        if (TD.terrainLayers != null) layers.AddRange(TD.terrainLayers);

        if (layers.Count == 0)
        {
            var baseLayer = new TerrainLayer();
            baseLayer.diffuseTexture = Texture2D.whiteTexture;
            var p1 = AssetDatabase.GenerateUniqueAssetPath("Assets/TestLayer1.terrainlayer");
            AssetDatabase.CreateAsset(baseLayer, p1);
            layers.Add(baseLayer);
        }

        if (layers.Count < 2)
        {
            var layer2 = new TerrainLayer();
            layer2.diffuseTexture = Texture2D.grayTexture;
            var p2 = AssetDatabase.GenerateUniqueAssetPath("Assets/TestLayer2.terrainlayer");
            AssetDatabase.CreateAsset(layer2, p2);
            layers.Add(layer2);
        }

        if (layers.Count != (TD.terrainLayers?.Length ?? 0))
            TD.terrainLayers = layers.ToArray();

        AssetDatabase.SaveAssets();
    }

    // details
    static bool ToggleDetailPatchOn()
    {
        EnsureDetailPrototype();

        int w = TD.detailWidth;
        int h = TD.detailHeight;
        if (w == 0 || h == 0) return false;

        int size = Mathf.Max(4, w / 16);
        int x0 = Mathf.Clamp(w / 4, 0, w - size);
        int y0 = Mathf.Clamp(h / 4, 0, h - size);
        int layer = 0;

        lastDetail = TD.GetDetailLayer(x0, y0, size, size, layer);
        lastDx0 = x0; lastDy0 = y0; lastDSize = size; lastDetailLayer = layer;

        var patch = (int[,])lastDetail.Clone();
        for (int yy = 0; yy < size; yy++)
            for (int xx = 0; xx < size; xx++)
                patch[yy, xx] = 4;

        TD.SetDetailLayer(x0, y0, layer, patch);
        return true;
    }

    static void EnsureDetailPrototype()
    {
        if (TD.detailPrototypes != null && TD.detailPrototypes.Length > 0) return;

        var dp = new DetailPrototype
        {
            renderMode = DetailRenderMode.GrassBillboard,
            healthyColor = Color.green,
            dryColor = Color.yellow,
            minWidth = 0.3f, maxWidth = 0.6f,
            minHeight = 0.3f, maxHeight = 0.6f,
            noiseSpread = 0.2f,
            prototypeTexture = Texture2D.whiteTexture
        };
        TD.detailPrototypes = new[] { dp };
        if (TD.detailResolution == 0) TD.SetDetailResolution(256, 8);
    }

    // trees
    static bool EnsureTreePrototypeAndAddOne()
    {
        EnsureTreePrototype();

        var list = new List<TreeInstance>(TD.treeInstances ?? new TreeInstance[0]);

        var ti = new TreeInstance
        {
            position = addedTreePos,
            prototypeIndex = addedTreeProtoIdx,
            widthScale = 1f,
            heightScale = 1f,
            color = Color.white,
            lightmapColor = Color.white
        };

        list.Add(ti);
        TD.treeInstances = list.ToArray();
        addedTree = true;
        return true;
    }

    static void EnsureTreePrototype()
    {
        if (TD.treePrototypes != null && TD.treePrototypes.Length > 0) return;

        const string prefabPath = "Assets/TestTree.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (!prefab)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            tmp.name = "TestTree";
            prefab = PrefabUtility.SaveAsPrefabAsset(tmp, prefabPath);
            Object.DestroyImmediate(tmp);
        }

        var tp = new TreePrototype { prefab = prefab };
        TD.treePrototypes = new[] { tp };
        addedTreeProtoIdx = 0;
    }
}
//#endif