using UnityEngine;
using UnityEditor;

public class GrassAutoPainter : EditorWindow
{
    /* Terrain selection */
    Terrain terrain;

    /* Detail layer */
    int detailIndex = 0;

    /* Optional splat confinement */
    int splatIndex = -1; // -1 = ignore

    /* Modes */
    bool addMode = true;       // true = add, false = replace
    int maxAddPerCell = 6;     // add mode cap per pass
    int targetDensity = 12;    // replace mode density 0..32

    /* Masks */
    float minHeight = 0f;
    float maxHeight = 1000f;
    float maxSlope = 35f;      // degrees

    float noiseScale = 0.003f; // Perlin
    float noiseThreshold = 0.45f;

    int seed = 12345;

    /* UI caches */
    string[] detailLabels = new string[0];
    string[] splatLabels = new string[0];

    [MenuItem("Tools/Grass/Auto Paint Terrain Details")]
    static void Open() => GetWindow<GrassAutoPainter>("Auto Paint Grass");

    static int Clamp01Index(int i, int max) => Mathf.Clamp(i, 0, Mathf.Max(0, max - 1));
    static float Safe01(float v) => Mathf.Clamp01(v);
    
    void OnEnable()
    {
        RefreshLabels();
    }

    void OnSelectionChange()
    {
        if (!terrain && Selection.activeGameObject)
        {
            var t = Selection.activeGameObject.GetComponent<Terrain>();
            if (t) terrain = t;
        }
        RefreshLabels();
        Repaint();
    }

    void OnGUI()
    {
        DrawTerrainPickers();

        EditorGUILayout.Space(6);
        DrawModeSection();

        EditorGUILayout.Space(6);
        DrawMasksSection();

        EditorGUILayout.Space(6);
        DrawNoiseSection();

        EditorGUILayout.Space(6);
        seed = EditorGUILayout.IntField("Seed", seed);

        EditorGUILayout.Space(10);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Dry Run"))
            {
                if (!ValidateTerrain()) return;
                Run(passPaint:false);
            }
            if (GUILayout.Button("Paint"))
            {
                if (!ValidateTerrain()) return;
                Run(passPaint:true);
            }
        }
    }

    void DrawTerrainPickers()
    {
        terrain = (Terrain)EditorGUILayout.ObjectField("Terrain", terrain, typeof(Terrain), true);
        if (terrain && terrain.terrainData)
        {
            EnsureLabelArrays(terrain.terrainData);

            detailIndex = EditorGUILayout.Popup("Detail Layer", Mathf.Clamp(detailIndex, 0, Mathf.Max(0, detailLabels.Length - 1)), detailLabels);
            int splatPopup = EditorGUILayout.Popup("Confine to Splat", splatIndex + 1, WithNoneFirst(splatLabels)); // shift by +1 for "None"
            splatIndex = splatPopup - 1;
        }
        else
        {
            EditorGUILayout.HelpBox("Assign a Terrain. Also make sure your grass is added as a Detail Mesh first.", MessageType.Info);
        }
    }

    void DrawModeSection()
    {
        EditorGUILayout.LabelField("Mode", EditorStyles.boldLabel);
        addMode = EditorGUILayout.Toggle("Add Mode (non-destructive)", addMode);
        using (new EditorGUI.DisabledScope(!addMode))
            maxAddPerCell = EditorGUILayout.IntSlider("Max Add Per Cell", maxAddPerCell, 0, 32);
        using (new EditorGUI.DisabledScope(addMode))
            targetDensity = EditorGUILayout.IntSlider("Target Density (replace)", targetDensity, 0, 32);
    }

    void DrawMasksSection()
    {
        EditorGUILayout.LabelField("Height & Slope Masks", EditorStyles.boldLabel);
        var td = terrain ? terrain.terrainData : null;
        float yMax = td ? td.size.y : 1000f;
        minHeight = EditorGUILayout.Slider("Min Height", minHeight, 0f, yMax);
        maxHeight = EditorGUILayout.Slider("Max Height", maxHeight, 0f, yMax);
        maxSlope  = EditorGUILayout.Slider("Max Slope (deg)", maxSlope, 0f, 90f);
    }

    void DrawNoiseSection()
    {
        EditorGUILayout.LabelField("Noise", EditorStyles.boldLabel);
        noiseScale = EditorGUILayout.FloatField("Noise Scale", noiseScale);
        noiseThreshold = EditorGUILayout.Slider("Noise Threshold", noiseThreshold, 0f, 1f);
    }

    bool ValidateTerrain()
    {
        if (!terrain || !terrain.terrainData)
        {
            Debug.LogError("Assign a Terrain.");
            return false;
        }
        var td = terrain.terrainData;
        if (td.detailPrototypes == null || td.detailPrototypes.Length == 0)
        {
            Debug.LogError("No Detail layers found. Add your grass as a Detail Mesh first.");
            return false;
        }
        if (detailIndex < 0 || detailIndex >= td.detailPrototypes.Length)
        {
            Debug.LogError("Detail layer index out of range.");
            return false;
        }
        if (splatIndex >= td.alphamapLayers)
        {
            Debug.LogError("Splat index out of range.");
            return false;
        }
        return true;
    }

    void Run(bool passPaint)
    {
        if (!terrain || !terrain.terrainData)
        {
            Debug.LogError("Assign a Terrain first.");
            return;
        }

        var td = terrain.terrainData;

        // detail layer must exist on THIS terrain
        var dps = td.detailPrototypes;
        if (dps == null || dps.Length == 0)
        {
            Debug.LogError("No Detail layers found. Add your grass as a Detail Mesh first.");
            return;
        }
        if (detailIndex < 0 || detailIndex >= dps.Length)
        {
            Debug.LogError($"Detail layer index {detailIndex} out of range (has {dps.Length}).");
            return;
        }
        if (dps[detailIndex] == null)
        {
            Debug.LogError($"Detail prototype at index {detailIndex} is null.");
            return;
        }

        // resolutions
        int detailRes = td.detailResolution;
        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;

        Undo.RegisterCompleteObjectUndo(td, "Auto Paint Grass Details");

        // read sources
        int[,] current = addMode
            ? td.GetDetailLayer(0, 0, detailIndex, detailRes, detailRes)
            : new int[detailRes, detailRes];

        int[,] output = new int[detailRes, detailRes];
        var heights = td.GetHeights(0, 0, hmRes, hmRes);

        // optional splat filter
        float[,,] alpha = null;
        bool useAlpha = splatIndex >= 0 && splatIndex < td.alphamapLayers;
        if (useAlpha)
        {
            try { alpha = td.GetAlphamaps(0, 0, amRes, amRes); }
            catch
            {
                Debug.LogWarning($"[{terrain.name}] failed to get alphamaps, disabling splat filter");
                useAlpha = false;
            }
        }

        var rand = new System.Random(seed);
        int totalCells = 0;
        int affectedCells = 0;

        try
        {
            for (int y = 0; y < detailRes; y++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Auto Painting Grass",
                        $"Row {y + 1}/{detailRes}",
                        (float)y / detailRes))
                {
                    EditorUtility.ClearProgressBar();
                    return;
                }

                for (int x = 0; x < detailRes; x++)
                {
                    totalCells++;

                    float nx = x / (float)(detailRes - 1);
                    float ny = y / (float)(detailRes - 1);

                    // height
                    int hx = Clamp01Index(Mathf.RoundToInt(nx * (hmRes - 1)), hmRes);
                    int hy = Clamp01Index(Mathf.RoundToInt(ny * (hmRes - 1)), hmRes);
                    float worldHeight = heights[hy, hx] * td.size.y;
                    if (worldHeight < minHeight || worldHeight > maxHeight)
                    {
                        output[y, x] = addMode ? current[y, x] : 0;
                        continue;
                    }

                    // slope
                    float slopeDeg = Vector3.Angle(td.GetInterpolatedNormal(nx, ny), Vector3.up);
                    if (slopeDeg > maxSlope)
                    {
                        output[y, x] = addMode ? current[y, x] : 0;
                        continue;
                    }

                    // splat
                    if (useAlpha && alpha != null)
                    {
                        int ax = Mathf.FloorToInt(nx * (amRes - 1));
                        int ay = Mathf.FloorToInt(ny * (amRes - 1));
                        ay = Mathf.Clamp(ay, 0, alpha.GetLength(0) - 1);
                        ax = Mathf.Clamp(ax, 0, alpha.GetLength(1) - 1);
                        int safeSplat = Mathf.Clamp(splatIndex, 0, alpha.GetLength(2) - 1);

                        float w = alpha[ay, ax, safeSplat];
                        if (w < Safe01(splatMinCache))
                        {
                            output[y, x] = addMode ? current[y, x] : 0;
                            continue;
                        }
                    }

                    // noise
                    float n = Mathf.PerlinNoise(
                        (x + rand.Next(-9999, 9999)) * Mathf.Max(1e-6f, noiseScale),
                        (y + rand.Next(-9999, 9999)) * Mathf.Max(1e-6f, noiseScale)
                    );
                    if (n < noiseThreshold)
                    {
                        output[y, x] = addMode ? current[y, x] : 0;
                        continue;
                    }

                    affectedCells++;

                    if (addMode)
                    {
                        int curr = current[y, x];
                        int add = Mathf.Min(maxAddPerCell, 32 - curr);
                        output[y, x] = Mathf.Clamp(curr + add, 0, 32);
                    }
                    else
                    {
                        output[y, x] = Mathf.Clamp(targetDensity, 0, 32);
                    }
                }
            }

            if (passPaint)
            {
                if (output.GetLength(0) != td.detailResolution || output.GetLength(1) != td.detailResolution)
                {
                    Debug.LogError($"Detail array size mismatch. output=({output.GetLength(0)}x{output.GetLength(1)}), detailRes={td.detailResolution}");
                    return;
                }

                td.SetDetailLayer(0, 0, detailIndex, output);
                EditorUtility.SetDirty(td);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (passPaint)
            EditorUtility.DisplayDialog("Grass Auto Painter", $"Painted. Affected cells: {affectedCells} / {totalCells}", "OK");
        else
            EditorUtility.DisplayDialog("Grass Auto Painter", $"Dry Run only. Would affect: {affectedCells} / {totalCells} cells", "OK");
    }



    /* Cache and helpers */
    float splatMinCache => 0.3f; // simple default; tweak if you want a UI slider by splat

    void RefreshLabels()
    {
        if (!terrain || !terrain.terrainData) { detailLabels = new string[0]; splatLabels = new string[0]; return; }
        EnsureLabelArrays(terrain.terrainData);
    }

    void EnsureLabelArrays(TerrainData td)
    {
        // Detail labels
        var dps = td.detailPrototypes;
        if (dps != null)
        {
            detailLabels = new string[dps.Length];
            for (int i = 0; i < dps.Length; i++)
                detailLabels[i] = BuildDetailLabel(dps[i], i);
        }
        else detailLabels = new string[0];

        // Splat labels
        var tls = td.terrainLayers;
        if (tls != null)
        {
            splatLabels = new string[tls.Length];
            for (int i = 0; i < tls.Length; i++)
                splatLabels[i] = string.IsNullOrEmpty(tls[i].name) ? $"Layer {i}" : tls[i].name;
        }
        else splatLabels = new string[0];
        
        if (detailIndex >= td.detailPrototypes.Length) detailIndex = 0;
        if (splatIndex >= td.alphamapLayers) splatIndex = -1;
    }

    static string BuildDetailLabel(DetailPrototype dp, int i)
    {
        // Unity exposes different fields depending on version. We try to make a readable tag.
        string kind = dp.usePrototypeMesh ? "Mesh" : "Texture";
        string baseName = "Detail";
        // Try to grab something human readable
        if (dp.prototypeTexture) baseName = dp.prototypeTexture.name;
        if (dp.prototype) baseName = dp.prototype.name;
        return $"{i}: {baseName} [{kind}]";
    }

    static string[] WithNoneFirst(string[] arr)
    {
        var result = new string[(arr?.Length ?? 0) + 1];
        result[0] = "None";
        if (arr != null) System.Array.Copy(arr, 0, result, 1, arr.Length);
        return result;
    }
}
