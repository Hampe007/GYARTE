using UnityEngine;
using UnityEditor;

public class TerrainPrefabPainter : EditorWindow
{
    #region Fields

    Terrain terrain;

    int detailIndex = 0;
    int splatIndex = -1;

    bool addMode = true;
    int maxAddPerCell = 6;
    int targetDensity = 12;

    float minHeight = 0f;
    float maxHeight = 1000f;
    float maxSlope = 35f;

    float noiseScale = 0.003f;
    float noiseThreshold = 0.45f;

    int seed = 12345;

    string[] detailLabels = new string[0];
    string[] splatLabels = new string[0];

    Vector2 scroll;

    [SerializeField] bool paintPrefabs = false;
    [SerializeField] PrefabPaintRule[] prefabRules = new PrefabPaintRule[0];
    [SerializeField] bool[] ruleFoldouts = new bool[0];

    #endregion

    #region RuleClass

    [System.Serializable]
    public class PrefabPaintRule
    {
        public string name = "Prefab Rule";
        public GameObject prefab;
        public float density = 0.15f;
        public float minHeight = 0f;
        public float maxHeight = 1000f;
        public float maxSlope = 35f;
        public int splatIndex = -1;
        public float noiseScale = 0.01f;
        public float noiseThreshold = 0.5f;
        public Vector2 randomScale = new Vector2(0.9f, 1.2f);
    }

    #endregion

    #region Menu

    [MenuItem("Tools/Terrain Prefab Painter")]
    static void Open() => GetWindow<TerrainPrefabPainter>("Terrain Prefab Painter");

    static int Clamp01Index(int i, int max) => Mathf.Clamp(i, 0, Mathf.Max(0, max - 1));
    static float Safe01(float v) => Mathf.Clamp01(v);

    #endregion

    #region UnityCallbacks

    void OnEnable()
    {
        RefreshLabels();
        SyncFoldoutArray();
    }

    void OnSelectionChange()
    {
        if (!terrain && Selection.activeGameObject)
        {
            var t = Selection.activeGameObject.GetComponent<Terrain>();
            if (t) terrain = t;
        }

        RefreshLabels();
        SyncFoldoutArray();
        Repaint();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        SyncFoldoutArray();

        DrawTerrainPickerSection();
        DrawDetailModeSection();
        DrawDetailMaskSection();
        DrawDetailNoiseSection();
        DrawSeedSection();
        DrawPrefabRuleSection();
        DrawExecuteButtons();

        EditorGUILayout.EndScrollView();
    }

    #endregion

    #region UISections

    void DrawTerrainPickerSection()
    {
        EditorGUILayout.Space(6);

        terrain = (Terrain)EditorGUILayout.ObjectField(
            new GUIContent(
                "Terrain",
                "Terrain used for detail painting and prefab sampling."
            ),
            terrain,
            typeof(Terrain),
            true
        );

        if (!terrain || !terrain.terrainData)
        {
            EditorGUILayout.HelpBox("Assign a Terrain.", MessageType.Info);
            return;
        }

        EnsureLabelArrays(terrain.terrainData);

        detailIndex = EditorGUILayout.Popup(
            new GUIContent("Detail Layer", "Detail layer to modify."),
            Mathf.Clamp(detailIndex, 0, detailLabels.Length - 1),
            detailLabels
        );

        int splatPopup = EditorGUILayout.Popup(
            new GUIContent("Confine Detail To Splat", "Optional texture filter."),
            splatIndex + 1,
            WithNoneFirst(splatLabels)
        );
        splatIndex = splatPopup - 1;
    }



    void DrawDetailModeSection()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Detail Layer Mode", EditorStyles.boldLabel);

        addMode = EditorGUILayout.Toggle(
            new GUIContent("Add Mode", "Adds to existing detail instead of replacing."),
            addMode
        );

        using (new EditorGUI.DisabledScope(!addMode))
        {
            maxAddPerCell = EditorGUILayout.IntSlider(
                new GUIContent("Max Add Per Cell", "Cap for density added per sample."),
                maxAddPerCell, 0, 32
            );
        }

        using (new EditorGUI.DisabledScope(addMode))
        {
            targetDensity = EditorGUILayout.IntSlider(
                new GUIContent("Target Density", "Density used in replace mode."),
                targetDensity, 0, 32
            );
        }
    }



    void DrawDetailMaskSection()
    {
        if (!terrain || !terrain.terrainData)
            return;
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Height and Slope Filters", EditorStyles.boldLabel);

        var td = terrain.terrainData;
        float yMax = td.size.y;

        minHeight = EditorGUILayout.Slider(
            new GUIContent("Min Height", "Minimum world height allowed."),
            minHeight, 0f, yMax
        );

        maxHeight = EditorGUILayout.Slider(
            new GUIContent("Max Height", "Maximum world height allowed."),
            maxHeight, 0f, yMax
        );

        maxSlope = EditorGUILayout.Slider(
            new GUIContent("Max Slope", "Maximum slope allowed."),
            maxSlope, 0f, 90f
        );
    }



    void DrawDetailNoiseSection()
    {
        if (!terrain || !terrain.terrainData)
            return;
        
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Detail Noise Filter", EditorStyles.boldLabel);

        noiseScale = EditorGUILayout.FloatField(
            new GUIContent("Noise Scale", "Scale of Perlin noise for detail masking."),
            noiseScale
        );

        noiseThreshold = EditorGUILayout.Slider(
            new GUIContent("Noise Threshold", "Cells below this noise value are skipped."),
            noiseThreshold, 0f, 1f
        );
    }



    void DrawSeedSection()
    {
        if (!terrain || !terrain.terrainData)
            return;
        
        EditorGUILayout.Space(6);
        seed = EditorGUILayout.IntField(
            new GUIContent("Seed", "Random seed used for both detail and prefab placement."),
            seed
        );
    }



    void DrawPrefabRuleSection()
    {
        EditorGUILayout.Space(20);
        EditorGUILayout.LabelField("Prefab Props", EditorStyles.boldLabel);

        paintPrefabs = EditorGUILayout.Toggle(
            new GUIContent("Paint Prefabs", "Enable prefab placement after detail painting."),
            paintPrefabs
        );

        if (!paintPrefabs)
            return;

        EditorGUILayout.Space(6);

        if (GUILayout.Button(
            new GUIContent("Add Prefab Rule", "Adds a new rule with default values.")
        ))
        {
            ArrayUtility.Add(ref prefabRules, new PrefabPaintRule());
            ArrayUtility.Add(ref ruleFoldouts, true);
        }

        SyncFoldoutArray();

        SerializedObject so = new SerializedObject(this);
        SerializedProperty rulesProp = so.FindProperty("prefabRules");

        int removeIndex = -1;

        for (int i = 0; i < prefabRules.Length; i++)
        {
            var rule = prefabRules[i];
            string header = rule.prefab ? rule.prefab.name : rule.name;

            ruleFoldouts[i] = EditorGUILayout.Foldout(ruleFoldouts[i], header, true);
            if (!ruleFoldouts[i]) continue;

            EditorGUILayout.BeginVertical("box");

            rule.name = EditorGUILayout.TextField(
                new GUIContent("Rule Name", "Optional name for UI."),
                rule.name
            );

            rule.prefab = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Prefab", "Prefab to spawn."),
                rule.prefab,
                typeof(GameObject),
                false
            );

            rule.density = EditorGUILayout.Slider(
                new GUIContent("Density", "Probability of spawning."),
                rule.density, 0f, 1f
            );

            rule.minHeight = EditorGUILayout.FloatField(
                new GUIContent("Min Height", "Minimum world height allowed."),
                rule.minHeight
            );

            rule.maxHeight = EditorGUILayout.FloatField(
                new GUIContent("Max Height", "Maximum world height allowed."),
                rule.maxHeight
            );

            rule.maxSlope = EditorGUILayout.FloatField(
                new GUIContent("Max Slope", "Maximum allowed slope."),
                rule.maxSlope
            );

            rule.splatIndex = EditorGUILayout.IntField(
                new GUIContent("Splat Index", "Optional splat layer filter."),
                rule.splatIndex
            );

            rule.noiseScale = EditorGUILayout.FloatField(
                new GUIContent("Noise Scale", "Scale of noise for this rule."),
                rule.noiseScale
            );

            rule.noiseThreshold = EditorGUILayout.Slider(
                new GUIContent("Noise Threshold", "Noise cutoff for placement."),
                rule.noiseThreshold, 0f, 1f
            );

            rule.randomScale = EditorGUILayout.Vector2Field(
                new GUIContent("Random Scale", "Min and Max scale."),
                rule.randomScale
            );

            EditorGUILayout.Space(6);

            if (GUILayout.Button(
                new GUIContent("Remove Rule", "Deletes this rule.")
            ))
            {
                removeIndex = i;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);
        }

        if (removeIndex >= 0)
        {
            ArrayUtility.RemoveAt(ref prefabRules, removeIndex);
            ArrayUtility.RemoveAt(ref ruleFoldouts, removeIndex);

            GUIUtility.ExitGUI();
        }
        
        so.ApplyModifiedProperties();
    }

    void DrawExecuteButtons()
    {
        EditorGUILayout.Space(20);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(
                new GUIContent("Dry Run", "Simulates painting without modifying anything.")
            ))
            {
                if (!ValidateTerrain()) return;
                Run(false);
            }

            if (GUILayout.Button(
                new GUIContent("Paint", "Applies detail painting and prefab placement.")
            ))
            {
                if (!ValidateTerrain()) return;
                Run(true);
            }
        }
    }

    #endregion

    #region Validation

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
            Debug.LogError("Terrain has no detail layers.");
            return false;
        }

        if (detailIndex < 0 || detailIndex >= td.detailPrototypes.Length)
        {
            Debug.LogError("Detail index out of range.");
            return false;
        }

        if (splatIndex >= td.alphamapLayers)
        {
            Debug.LogError("Splat index out of range.");
            return false;
        }

        return true;
    }

    #endregion

    #region Execution

    void Run(bool passPaint)
    {
        SyncFoldoutArray();
        var td = terrain.terrainData;
        var dps = td.detailPrototypes;

        int detailRes = td.detailResolution;
        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;

        Undo.RegisterCompleteObjectUndo(td, "Prefab Painter");

        int[,] current = addMode
            ? td.GetDetailLayer(0, 0, detailRes, detailRes, detailIndex)
            : new int[detailRes, detailRes];

        int[,] output = new int[detailRes, detailRes];
        var heights = td.GetHeights(0, 0, hmRes, hmRes);

        float[,,] alpha = null;
        bool useAlpha = splatIndex >= 0 && splatIndex < td.alphamapLayers;

        if (useAlpha)
        {
            try { alpha = td.GetAlphamaps(0, 0, amRes, amRes); }
            catch { useAlpha = false; }
        }

        var rand = new System.Random(seed);

        int totalCells = 0;
        int affectedCells = 0;

        try
        {
            for (int y = 0; y < detailRes; y++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                    "Painting Details",
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

                    int hx = Clamp01Index(Mathf.RoundToInt(nx * (hmRes - 1)), hmRes);
                    int hy = Clamp01Index(Mathf.RoundToInt(ny * (hmRes - 1)), hmRes);

                    float worldHeight = heights[hy, hx] * td.size.y;

                    if (worldHeight < minHeight || worldHeight > maxHeight)
                    {
                        output[y, x] = addMode ? current[y, x] : 0;
                        continue;
                    }

                    float slopeDeg = Vector3.Angle(td.GetInterpolatedNormal(nx, ny), Vector3.up);

                    if (slopeDeg > maxSlope)
                    {
                        output[y, x] = addMode ? current[y, x] : 0;
                        continue;
                    }

                    if (useAlpha && alpha != null)
                    {
                        int ax = Mathf.FloorToInt(nx * (amRes - 1));
                        int ay = Mathf.FloorToInt(ny * (amRes - 1));
                        ay = Mathf.Clamp(ay, 0, alpha.GetLength(0) - 1);
                        ax = Mathf.Clamp(ax, 0, alpha.GetLength(1) - 1);
                        int safe = Mathf.Clamp(splatIndex, 0, alpha.GetLength(2) - 1);

                        float w = alpha[ay, ax, safe];
                        if (w < Safe01(splatMinCache))
                        {
                            output[y, x] = addMode ? current[y, x] : 0;
                            continue;
                        }
                    }

                    float n = Mathf.PerlinNoise(
                        (x + rand.Next(-9999, 9999)) * Mathf.Max(noiseScale, 1e-6f),
                        (y + rand.Next(-9999, 9999)) * Mathf.Max(noiseScale, 1e-6f)
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
                        output[y, x] = Mathf.Clamp(curr + Mathf.Min(maxAddPerCell, 32 - curr), 0, 32);
                    }
                    else
                    {
                        output[y, x] = Mathf.Clamp(targetDensity, 0, 32);
                    }
                }
            }

            if (passPaint)
            {
                td.SetDetailLayer(0, 0, detailIndex, output);
                EditorUtility.SetDirty(td);
            }

            if (passPaint && paintPrefabs)
                PaintPrefabs(td, heights, alpha);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (passPaint)
            EditorUtility.DisplayDialog("Terrain Prefab Painter", $"Painted {affectedCells}/{totalCells} cells", "OK");
        else
        {
            LogDryRun(td, affectedCells);
            EditorUtility.DisplayDialog("Terrain Prefab Painter", $"Dry Run: {affectedCells} cells affected", "OK");
        }
    }

    #endregion
    
    #region PrefabSpawning

    void PaintPrefabs(TerrainData td, float[,] heights, float[,,] alpha)
    {
        if (prefabRules == null || prefabRules.Length == 0) return;
        if (!terrain) return;

        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;
        int res = td.detailResolution;

        Vector3 size = td.size;
        Vector3 origin = terrain.transform.position;

        var rand = new System.Random(seed);

        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float nx = x / (float)(res - 1);
                float ny = y / (float)(res - 1);

                int hx = Clamp01Index(Mathf.RoundToInt(nx * (hmRes - 1)), hmRes);
                int hy = Clamp01Index(Mathf.RoundToInt(ny * (hmRes - 1)), hmRes);

                float worldHeight = heights[hy, hx] * size.y;
                float slopeDeg = Vector3.Angle(td.GetInterpolatedNormal(nx, ny), Vector3.up);

                for (int i = 0; i < prefabRules.Length; i++)
                {
                    var rule = prefabRules[i];
                    if (rule.prefab == null) continue;

                    if (worldHeight < rule.minHeight || worldHeight > rule.maxHeight) continue;
                    if (slopeDeg > rule.maxSlope) continue;

                    if (rule.splatIndex >= 0 && alpha != null)
                    {
                        int ax = Mathf.FloorToInt(nx * (amRes - 1));
                        int ay = Mathf.FloorToInt(ny * (amRes - 1));
                        ay = Mathf.Clamp(ay, 0, alpha.GetLength(0) - 1);
                        ax = Mathf.Clamp(ax, 0, alpha.GetLength(1) - 1);

                        float splatWeight = alpha[ay, ax, Mathf.Clamp(rule.splatIndex, 0, alpha.GetLength(2) - 1)];
                        if (splatWeight < 0.2f) continue;
                    }

                    float noiseValue = Mathf.PerlinNoise(
                        (x + rand.Next(-9999, 9999)) * Mathf.Max(rule.noiseScale, 1e-6f),
                        (y + rand.Next(-9999, 9999)) * Mathf.Max(rule.noiseScale, 1e-6f)
                    );

                    if (noiseValue < rule.noiseThreshold) continue;
                    if (rand.NextDouble() > rule.density) continue;

                    Vector3 pos = new Vector3(
                        origin.x + nx * size.x,
                        worldHeight,
                        origin.z + ny * size.z
                    );

                    GameObject prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(rule.prefab);
                    GameObject instance = prefabSource
                        ? (GameObject)PrefabUtility.InstantiatePrefab(prefabSource)
                        : Instantiate(rule.prefab);

                    instance.transform.position = pos;

                    float t = (float)rand.NextDouble();
                    float s = Mathf.Lerp(rule.randomScale.x, rule.randomScale.y, t);
                    instance.transform.localScale = Vector3.one * s;

                    instance.transform.rotation = Quaternion.Euler(0, rand.Next(0, 360), 0);

                    if (!instance.GetComponent<TileProp>())
                        instance.AddComponent<TileProp>();
                }
            }
        }
    }

    #endregion

    #region Helpers

    float splatMinCache => 0.3f;

    void RefreshLabels()
    {
        if (!terrain || !terrain.terrainData)
        {
            detailLabels = new string[0];
            splatLabels = new string[0];
            return;
        }
        EnsureLabelArrays(terrain.terrainData);
    }

    void SyncFoldoutArray()
    {
        if (ruleFoldouts.Length != prefabRules.Length)
            System.Array.Resize(ref ruleFoldouts, prefabRules.Length);
    }

    void EnsureLabelArrays(TerrainData td)
    {
        var dps = td.detailPrototypes;

        if (dps != null)
        {
            detailLabels = new string[dps.Length];
            for (int i = 0; i < dps.Length; i++)
                detailLabels[i] = BuildDetailLabel(dps[i], i);
        }

        var tls = td.terrainLayers;

        if (tls != null)
        {
            splatLabels = new string[tls.Length];
            for (int i = 0; i < tls.Length; i++)
                splatLabels[i] = string.IsNullOrEmpty(tls[i].name) ? $"Layer {i}" : tls[i].name;
        }

        if (detailIndex >= td.detailPrototypes.Length) detailIndex = 0;
        if (splatIndex >= td.alphamapLayers) splatIndex = -1;
    }

    static string BuildDetailLabel(DetailPrototype dp, int i)
    {
        string name = dp.prototype ? dp.prototype.name :
                      dp.prototypeTexture ? dp.prototypeTexture.name :
                      "Detail";

        string kind = dp.usePrototypeMesh ? "Mesh" : "Texture";

        return $"{i}: {name} [{kind}]";
    }

    static string[] WithNoneFirst(string[] arr)
    {
        var result = new string[(arr?.Length ?? 0) + 1];
        result[0] = "None";

        if (arr != null)
            System.Array.Copy(arr, 0, result, 1, arr.Length);

        return result;
    }

    #endregion
    
    #region Debug

    void LogDryRun(TerrainData td, int affectedCells)
    {
        string detailName =
            (detailLabels != null && detailIndex >= 0 && detailIndex < detailLabels.Length)
                ? detailLabels[detailIndex]
                : "Unknown Detail Layer";

        Vector3 size = td.size;
        int detailRes = td.detailResolution;

        float cellSizeX = size.x / detailRes;
        float cellSizeZ = size.z / detailRes;
        float cellArea = cellSizeX * cellSizeZ;

        float affectedArea = affectedCells * cellArea;
        float totalArea = size.x * size.z;
        float percent = totalArea > 0 ? (affectedArea / totalArea) * 100f : 0f;

        // Prefab summary
        int prefabCount = 0;
        System.Text.StringBuilder prefabList = new System.Text.StringBuilder();

        if (paintPrefabs && prefabRules != null)
        {
            for (int i = 0; i < prefabRules.Length; i++)
            {
                var r = prefabRules[i];
                if (r.prefab != null)
                {
                    prefabCount++;
                    prefabList.AppendLine($"   - {r.prefab.name} (rule '{r.name}')");
                }
            }
        }

        Debug.Log(
            "<b>[Prefab Painter Dry Run]</b>\n" +
            $"Terrain: {terrain.name}\n" +
            $"Detail Layer: {detailName}\n\n" +

            $"Cells affected: {affectedCells}\n" +
            $"Approx area: {affectedArea:F1} m² ({percent:F2}% of terrain)\n" +
            $"Cell size: {cellSizeX:F2} × {cellSizeZ:F2} m\n\n" +

            $"{(paintPrefabs ? $"Prefab rules active: {prefabCount}\n" : "Prefab painting disabled.\n")}" +
            $"{(prefabCount > 0 ? "Prefabs that WOULD spawn:\n" + prefabList.ToString() : "")}"
        );
    }

    #endregion

}

public class TileProp : MonoBehaviour {}
