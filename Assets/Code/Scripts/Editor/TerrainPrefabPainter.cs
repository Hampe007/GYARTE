using System.Collections.Generic;
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

    public float clearRadius = 1.5f; // meters around prefab where grass is removed

    [SerializeField] private List<SpawnCircleVolume> globalCircles;
    bool useGlobalCircles = false;

    public bool placingVolume = false;
    public VolumeAreaPreview preview;
    public PrefabPaintRule volumeRuleTarget;
    
    private Transform prefabRoot;

    #endregion

    #region RuleClass

    [System.Serializable]
    public class PrefabPaintRule
    {
        public string name = "Prefab Rule";
        public GameObject prefab;
        public PrefabVariant[] variants;
        public float density = 0.15f;
        public float minHeight = 0f;
        public float maxHeight = 1000f;
        public float maxSlope = 35f;
        public int splatIndex = -1;
        public float noiseScale = 0.01f;
        public float noiseThreshold = 0.5f;
        public Vector2 randomScale = new Vector2(0.9f, 1.2f);
        public float clearRadius = 1.5f;
        public bool deleteBeforeSpawn = false;
        public bool useVolumeArea = false;
        public ForestAreaVolume volumeRef = null;
    }
    
    [System.Serializable]
    public class PrefabVariant
    {
        public GameObject prefab;
        public float weight = 1.0f;
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

        // Global circle system UI
        useGlobalCircles = EditorGUILayout.Toggle(
            new GUIContent("Use Global Circle Mask", "Limit prefab painting to one or more circular areas."),
            useGlobalCircles
        );
        
        if (useGlobalCircles)
        {
            var circles = FindObjectsByType<SpawnCircleVolume>(FindObjectsSortMode.None);

            if (circles.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "Global Circle Mask is enabled but no circles exist.\n" +
                    "Spawning will fall back to normal terrain mode.",
                    MessageType.Warning
                );
            }
        }


        if (useGlobalCircles)
        {
            EditorGUILayout.BeginVertical("box");

            if (GUILayout.Button("Add Circle Volume"))
            {
                var c = CreateGlobalCircleVolume();
                globalCircles.Add(c);
            }

            if (globalCircles.Count > 0)
            {
                EditorGUILayout.LabelField($"Circles active: {globalCircles.Count}");

                if (GUILayout.Button("Remove All Circles"))
                {
                    foreach (var c in globalCircles)
                        if (c) DestroyImmediate(c.gameObject);
                    globalCircles.Clear();
                }
            }

            EditorGUILayout.EndVertical();
        }

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
        
        EditorGUILayout.Space(10);

        #region Presets
        
        EditorGUILayout.LabelField("Quick Presets", EditorStyles.boldLabel);

        // Forest presets
        EditorGUILayout.LabelField("Forests", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Sparse")) CreatePresetRule("Sparse Forest", 0.10f, 0.55f, new Vector2(0.9f, 1.1f), 32f, 1.2f);
            if (GUILayout.Button("Normal")) CreatePresetRule("Normal Forest", 0.22f, 0.45f, new Vector2(0.85f, 1.2f), 32f, 1.4f);
            if (GUILayout.Button("Dense")) CreatePresetRule("Dense Forest", 0.38f, 0.35f, new Vector2(0.8f, 1.25f), 40f, 1.6f);
            if (GUILayout.Button("Overgrown")) CreatePresetRule("Overgrown Forest", 0.60f, 0.25f, new Vector2(0.75f, 1.3f), 45f, 1.8f);
        }

        EditorGUILayout.Space(6);

        // Rock fields
        EditorGUILayout.LabelField("Rocks & Boulders", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scattered Rocks")) CreatePresetRule("Scattered Rocks", 0.08f, 0.60f, new Vector2(0.6f, 1.1f), 50f, 0.5f);
            if (GUILayout.Button("Rock Cluster")) CreatePresetRule("Rock Cluster", 0.20f, 0.40f, new Vector2(0.8f, 1.3f), 55f, 0.8f);
            if (GUILayout.Button("Boulder Field")) CreatePresetRule("Boulder Field", 0.35f, 0.30f, new Vector2(1.0f, 1.6f), 60f, 1.2f);
        }

        EditorGUILayout.Space(6);

        // Bush areas
        EditorGUILayout.LabelField("Bushes & Underbrush", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Garden Bushes")) CreatePresetRule("Garden Bushes", 0.12f, 0.50f, new Vector2(0.7f, 1.0f), 25f, 0.5f);
            if (GUILayout.Button("Wild Bushes")) CreatePresetRule("Wild Bushes", 0.25f, 0.40f, new Vector2(0.8f, 1.2f), 35f, 0.6f);
            if (GUILayout.Button("Thick Underbrush")) CreatePresetRule("Thick Underbrush", 0.45f, 0.30f, new Vector2(0.9f, 1.3f), 40f, 0.8f);
        }

        EditorGUILayout.Space(6);

        // Flowers
        EditorGUILayout.LabelField("Flowers & Meadow", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Sparse Flowers")) CreatePresetRule("Sparse Flowers", 0.05f, 0.65f, new Vector2(0.7f, 1.1f), 25f, 0.3f);
            if (GUILayout.Button("Meadow")) CreatePresetRule("Flower Meadow", 0.18f, 0.45f, new Vector2(0.8f, 1.2f), 30f, 0.4f);
        }

        EditorGUILayout.Space(6);

        // Dead areas
        EditorGUILayout.LabelField("Dead / Spooky Biomes", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Sparse Dead Trees")) CreatePresetRule("Dead Trees", 0.10f, 0.60f, new Vector2(0.9f, 1.1f), 35f, 1.2f);
            if (GUILayout.Button("Witchy Dense Dead")) CreatePresetRule("Witch Forest", 0.30f, 0.40f, new Vector2(0.8f, 1.2f), 50f, 1.5f);
        }

        EditorGUILayout.Space(6);

        // Snow biomes
        EditorGUILayout.LabelField("Snow Biomes", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Snow Sparse")) CreatePresetRule("Snow Sparse Trees", 0.08f, 0.55f, new Vector2(0.9f, 1.2f), 30f, 1.4f);
            if (GUILayout.Button("Snow Forest")) CreatePresetRule("Snow Forest", 0.25f, 0.40f, new Vector2(0.8f, 1.3f), 35f, 1.6f);
        }

        EditorGUILayout.Space(6);

        // Desert / dunes
        EditorGUILayout.LabelField("Desert & Dry Areas", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Desert Sparse")) CreatePresetRule("Desert Sparse Rocks", 0.04f, 0.60f, new Vector2(0.7f, 1.1f), 50f, 0.4f);
            if (GUILayout.Button("Dune Clutter")) CreatePresetRule("Dune Clutter", 0.12f, 0.45f, new Vector2(0.9f, 1.3f), 60f, 0.5f);
        }

        EditorGUILayout.Space(10);

        #endregion
        
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
            
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Prefab Variants", EditorStyles.boldLabel);

            if (GUILayout.Button("Add Variant"))
            {
                ArrayUtility.Add(ref rule.variants, new PrefabVariant());
            }

            for (int v = 0; v < rule.variants.Length; v++)
            {
                var variant = rule.variants[v];

                EditorGUILayout.BeginVertical("box");

                variant.prefab = (GameObject)EditorGUILayout.ObjectField(
                    "Prefab",
                    variant.prefab,
                    typeof(GameObject),
                    false
                );

                variant.weight = EditorGUILayout.Slider(
                    "Weight",
                    variant.weight,
                    0.01f,
                    10f
                );

                variant.randomScale = EditorGUILayout.Vector2Field(
                    "Scale",
                    variant.randomScale
                );

                if (GUILayout.Button("Remove"))
                {
                    ArrayUtility.RemoveAt(ref rule.variants, v);
                    break;
                }

                EditorGUILayout.EndVertical();
            }

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

            rule.clearRadius = EditorGUILayout.FloatField(
                new GUIContent("Clear Radius", "No-grass radius in meters around this prefab."),
                rule.clearRadius
            );

            rule.deleteBeforeSpawn = EditorGUILayout.Toggle(
                new GUIContent("Delete Old Prefabs", "Deletes all previously spawned prefabs before applying this rule."),
                rule.deleteBeforeSpawn
            );

            rule.useVolumeArea = EditorGUILayout.Toggle(
                new GUIContent("Use Volume Area", "Only spawn props inside a shaped volume region."),
                rule.useVolumeArea
            );
            
            // Warning if Volume Area enabled but missing volume object
            if (rule.useVolumeArea && rule.volumeRef == null)
            {
                EditorGUILayout.HelpBox(
                    "Volume Area is enabled but no volume exists.\n" +
                    "The rule will spawn normally until you create one.",
                    MessageType.Warning
                );
            }

            if (rule.useVolumeArea)
            {
                if (rule.volumeRef == null)
                {
                    if (GUILayout.Button("Create Volume Area"))
                    {
                        rule.volumeRef = CreateForestVolumeGizmo(rule.name);
                        // If you ever want interactive placement, call StartPlacingVolume(rule) instead.
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Volume area active in scene. Move/resize it freely.", MessageType.Info);

                    if (GUILayout.Button("Remove Volume Area"))
                    {
                        DestroyImmediate(rule.volumeRef.gameObject);
                        rule.volumeRef = null;
                    }
                }
            }
            
            if (terrain && terrain.terrainData)
            {
                TerrainData td = terrain.terrainData;
                int detailRes = td.detailResolution;
                float cellSize = td.size.x / detailRes;
                float cellArea = cellSize * cellSize;
            
                // Approximate filter impact multipliers
                float heightFactor = Mathf.Clamp01((rule.maxHeight - rule.minHeight) / td.size.y);
                float slopeFactor = Mathf.Clamp01((rule.maxSlope / 90f));
                float noiseFactor = 1f - rule.noiseThreshold;
            
                // Final probability a cell spawns something
                float spawnChance = rule.density * heightFactor * slopeFactor * noiseFactor;
                float prefabsPerM2 = spawnChance / cellArea;
            
                float terrainArea = td.size.x * td.size.z;
                float estimatedTotal = prefabsPerM2 * terrainArea;
            
                EditorGUILayout.HelpBox(
                    $"Estimated density: {prefabsPerM2:F3} prefabs/m²\n" +
                    $"Estimated total: {estimatedTotal:F0} prefabs",
                    MessageType.Info
                );
            }

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

        if (GUILayout.Button(
                new GUIContent("Delete All Spawned Prefabs", "Deletes every prefab spawned by this painter")
            ))
        {
            DeleteAllSpawnedPrefabs();
        }

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
            {
                RefreshCircleList();
                PaintPrefabs(td, heights, alpha);
            }
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

        // Detail buffer used for grass clearing
        int[,] detailBuffer = td.GetDetailLayer(0, 0, res, res, detailIndex);

        Vector3 size = td.size;
        Vector3 origin = terrain.transform.position;

        var rand = new System.Random(seed);

        // Delete once if any rule wants to wipe old prefabs
        bool shouldDelete = false;
        for (int i = 0; i < prefabRules.Length; i++)
        {
            if (prefabRules[i].deleteBeforeSpawn)
            {
                shouldDelete = true;
                break;
            }
        }

        if (shouldDelete)
            DeleteAllSpawnedPrefabs();

        // Helper to process one sample position
        void ProcessSample(float nx, float ny, float wx, float wz)
        {
            int hx = Mathf.RoundToInt(nx * (hmRes - 1));
            int hy = Mathf.RoundToInt(ny * (hmRes - 1));
            hx = Clamp01Index(hx, hmRes);
            hy = Clamp01Index(hy, hmRes);

            float worldHeight = heights[hy, hx] * size.y;
            float slopeDeg = Vector3.Angle(td.GetInterpolatedNormal(nx, ny), Vector3.up);

            for (int i = 0; i < prefabRules.Length; i++)
            {
                var rule = prefabRules[i];

                // if variants exist, ignore single prefab
                bool hasVariants = rule.variants != null && rule.variants.Length > 0;
                if (!hasVariants && rule.prefab == null) continue;

                // Per-rule volume area (box)
                if (rule.useVolumeArea)
                {
                    // If the user forgot to create a volume, just ignore volume filtering
                    if (rule.volumeRef != null && rule.volumeRef.col != null)
                    {
                        Vector3 volumeCheck = new Vector3(wx, worldHeight, wz);
                        if (!rule.volumeRef.col.bounds.Contains(volumeCheck))
                            return;
                    }
                }

                // Height and slope per rule
                if (worldHeight < rule.minHeight || worldHeight > rule.maxHeight) continue;
                if (slopeDeg > rule.maxSlope) continue;

                // Optional splat filter per rule
                if (rule.splatIndex >= 0 && alpha != null)
                {
                    int ax = Mathf.FloorToInt(nx * (amRes - 1));
                    int ay = Mathf.FloorToInt(ny * (amRes - 1));
                    ay = Mathf.Clamp(ay, 0, alpha.GetLength(0) - 1);
                    ax = Mathf.Clamp(ax, 0, alpha.GetLength(1) - 1);

                    float splatWeight = alpha[ay, ax, Mathf.Clamp(rule.splatIndex, 0, alpha.GetLength(2) - 1)];
                    if (splatWeight < 0.2f) continue;
                }

                // Noise per rule
                float noiseValue = Mathf.PerlinNoise(
                    (float)(rand.NextDouble() * 99999f) * Mathf.Max(rule.noiseScale, 1e-6f),
                    (float)(rand.NextDouble() * 99999f) * Mathf.Max(rule.noiseScale, 1e-6f)
                );
                if (noiseValue < rule.noiseThreshold) continue;

                // Density
                if (rand.NextDouble() > rule.density) continue;

                // Spawn prefab
                Vector3 pos = new Vector3(wx, worldHeight, wz);

                GameObject prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(rule.prefab);
                GameObject instance = prefabSource
                    ? (GameObject)PrefabUtility.InstantiatePrefab(prefabSource)
                    : Object.Instantiate(rule.prefab);
                
                // Scale
                float t = (float)rand.NextDouble();
                float sVal = Mathf.Lerp(rule.randomScale.x, rule.randomScale.y, t);
                instance.transform.localScale = Vector3.one * sVal;

                // Rotation will be set later if we align to slope
                float randomY = rand.Next(0, 360);

                // Place roughly at heightmap pos
                instance.transform.position = pos;

                // Snap to actual ground
                RaycastHit hit;
                Vector3 rayStart = pos + Vector3.up * 200f;
                if (Physics.Raycast(rayStart, Vector3.down, out hit, 500f, ~0, QueryTriggerInteraction.Ignore))
                {
                    instance.transform.position = hit.point;

                    // Align to ground slope
                    Vector3 normal = hit.normal;
                    Quaternion slopeRot = Quaternion.FromToRotation(Vector3.up, normal);
                    instance.transform.rotation = slopeRot * Quaternion.Euler(0, randomY, 0);
                }
                else
                {
                    // Fallback: flat rotation
                    instance.transform.rotation = Quaternion.Euler(0, randomY, 0);
                }
                
                instance.transform.SetParent(GetRuleRoot(rule), true);

                // Grass clear
                if (rule.clearRadius > 0f)
                    ClearGrassAround(detailBuffer, td, instance.transform.position, rule.clearRadius);

                if (!instance.GetComponent<TileProp>())
                    instance.AddComponent<TileProp>();
            }
        }

        // Auto-detect all circles in the scene
        RefreshCircleList();

        bool circlesAvailable = useGlobalCircles && globalCircles != null && globalCircles.Count > 0;

        if (circlesAvailable)
        {
            // Random sampling inside 2D circles
            int samples = 5000; // tweak for density

            for (int s = 0; s < samples; s++)
            {
                var c = globalCircles[rand.Next(globalCircles.Count)];
                if (!c) continue;

                float angle = (float)(rand.NextDouble() * Mathf.PI * 2f);
                float radius = c.radius * Mathf.Sqrt((float)rand.NextDouble());

                float wx = c.transform.position.x + Mathf.Cos(angle) * radius;
                float wz = c.transform.position.z + Mathf.Sin(angle) * radius;

                float nx = Mathf.InverseLerp(origin.x, origin.x + size.x, wx);
                float ny = Mathf.InverseLerp(origin.z, origin.z + size.z, wz);

                // skip samples outside terrain bounds
                if (nx < 0 || nx > 1 || ny < 0 || ny > 1) continue;

                ProcessSample(nx, ny, wx, wz);
            }
        }
        else
        {
            // Fallback grid mode when no circles exist
            int detailRes = res;

            for (int y = 0; y < detailRes; y++)
            {
                for (int x = 0; x < detailRes; x++)
                {
                    float nx = x / (float)(detailRes - 1);
                    float ny = y / (float)(detailRes - 1);

                    float wx = origin.x + nx * size.x;
                    float wz = origin.z + ny * size.z;

                    ProcessSample(nx, ny, wx, wz);
                }
            }
        }

        // Apply all grass clearing at once
        td.SetDetailLayer(0, 0, detailIndex, detailBuffer);
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

    void ClearGrassAround(int[,] buffer, TerrainData td, Vector3 worldPos, float radius)
    {
        int detailRes = td.detailResolution;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 size = td.size;

        float nx = Mathf.InverseLerp(terrainPos.x, terrainPos.x + size.x, worldPos.x);
        float nz = Mathf.InverseLerp(terrainPos.z, terrainPos.z + size.z, worldPos.z);

        int cx = Mathf.RoundToInt(nx * (detailRes - 1));
        int cz = Mathf.RoundToInt(nz * (detailRes - 1));

        float cellsPerMeter = detailRes / size.x;
        int radCells = Mathf.RoundToInt(radius * cellsPerMeter);

        for (int z = -radCells; z <= radCells; z++)
        {
            for (int x = -radCells; x <= radCells; x++)
            {
                int sx = cx + x;
                int sz = cz + z;

                if (sx < 0 || sx >= detailRes) continue;
                if (sz < 0 || sz >= detailRes) continue;

                float dist = Mathf.Sqrt(x * x + z * z);
                if (dist <= radCells)
                    buffer[sz, sx] = 0;
            }
        }
    }
    
    void RefreshCircleList()
    {
        globalCircles.Clear();

        // Find all circles in the scene (even if disabled)
        var found = FindObjectsByType<SpawnCircleVolume>(FindObjectsSortMode.None);

        foreach (var c in found)
        {
            if (!globalCircles.Contains(c))
                globalCircles.Add(c);
        }
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

    void DeleteAllSpawnedPrefabs()
    {
        var root = GetPrefabRoot();
        int count = root.childCount;

        for (int i = count - 1; i >= 0; i--)
            DestroyImmediate(root.GetChild(i).gameObject);

        Debug.Log($"Deleted {count} spawned prefabs.");
    }


    ForestAreaVolume CreateForestVolumeGizmo(string ruleName)
    {
        GameObject go = new GameObject($"Volume_{ruleName}");
        var col = go.AddComponent<BoxCollider>();
        col.size = new Vector3(50, 500, 50);
        col.isTrigger = true;

        var vol = go.AddComponent<ForestAreaVolume>();
        vol.col = col;

        SceneView view = SceneView.lastActiveSceneView;
        if (view != null && view.camera != null)
        {
            Ray ray = new Ray(view.camera.transform.position, view.camera.transform.forward);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, 5000f))
            {
                go.transform.position = hit.point;
            }
            else
            {
                go.transform.position = view.camera.transform.position + view.camera.transform.forward * 20f;
            }
        }
        else
        {
            go.transform.position = terrain
                ? terrain.transform.position + new Vector3(0, 2, 0)
                : Vector3.zero;
        }

        Selection.activeGameObject = go;
        return vol;
    }

    SpawnCircleVolume CreateGlobalCircleVolume()
    {
        GameObject go = new GameObject("SpawnCircle");
        
        var vol = go.AddComponent<SpawnCircleVolume>();
        vol.radius = 25f;

        SceneView view = SceneView.lastActiveSceneView;
        if (view != null && view.camera != null && terrain != null)
        {
            Ray ray = new Ray(view.camera.transform.position, view.camera.transform.forward);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, 5000f))
            {
                go.transform.position = hit.point;
            }
            else
            {
                go.transform.position = view.camera.transform.position + view.camera.transform.forward * 20f;
            }
        }
        else
        {
            go.transform.position = terrain ? terrain.transform.position + new Vector3(0, 2, 0) : Vector3.zero;
        }

        Selection.activeGameObject = go;

        return vol;
    }

    public void StartPlacingVolume(PrefabPaintRule rule)
    {
        placingVolume = true;
        volumeRuleTarget = rule;

        preview = new VolumeAreaPreview();
        preview.SetSize(new Vector3(50, 500, 50));
    }

    public void ConfirmVolumePlacement(Vector3 pos)
    {
        GameObject go = new GameObject($"Volume_{volumeRuleTarget.name}");
        var col = go.AddComponent<BoxCollider>();
        col.size = preview.col.size;
        col.isTrigger = true;

        go.transform.position = pos;

        var vol = go.AddComponent<ForestAreaVolume>();
        vol.col = col;

        volumeRuleTarget.volumeRef = vol;

        Selection.activeGameObject = go;
    }

    public void StopPlacingVolume()
    {
        placingVolume = false;
        if (preview != null)
        {
            preview.Destroy();
            preview = null;
        }
    }
    
    Transform GetRuleRoot(PrefabPaintRule rule)
    {
        Transform baseRoot = GetPrefabRoot();

        string folderName = string.IsNullOrWhiteSpace(rule.name)
            ? (rule.prefab != null ? rule.prefab.name : "UnnamedRule")
            : rule.name;

        // Try find existing child
        Transform found = baseRoot.Find(folderName);
        if (found != null)
            return found;

        // Create new folder
        GameObject newFolder = new GameObject(folderName);
        newFolder.transform.SetParent(baseRoot);
        return newFolder.transform;
    }

    Transform GetPrefabRoot()
    {
        if (prefabRoot == null)
        {
            // Try find an existing root
            var existing = GameObject.Find("TerrainPropRoot");
            if (existing != null)
            {
                prefabRoot = existing.transform;
            }
            else
            {
                // Create new root object
                GameObject root = new GameObject("TerrainPropRoot");
                prefabRoot = root.transform;
            }
        }

        return prefabRoot;
    }
    
    void CreatePresetRule(string name, float density, float noiseThreshold, Vector2 scale, float maxSlope, float clearRadius)
    {
        var r = new PrefabPaintRule();
        r.name = name;
        r.density = density;
        r.noiseThreshold = noiseThreshold;
        r.randomScale = scale;
        r.maxSlope = maxSlope;
        r.clearRadius = clearRadius;

        ArrayUtility.Add(ref prefabRules, r);
        ArrayUtility.Add(ref ruleFoldouts, true);
    }
    
    #endregion
}

public class TileProp : MonoBehaviour {}

public class ForestAreaVolume : MonoBehaviour
{
    public BoxCollider col;
    void OnValidate() { col = GetComponent<BoxCollider>(); }
}

public class VolumeAreaPreview
{
    public GameObject preview;
    public BoxCollider col;

    public VolumeAreaPreview()
    {
        preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
        preview.name = "VolumePreview";
        preview.hideFlags = HideFlags.HideAndDontSave;

        col = preview.GetComponent<BoxCollider>();
        col.isTrigger = true;

        var renderer = preview.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = new Material(Shader.Find("Hidden/Internal-Colored"))
        {
            color = new Color(0, 1, 0, 0.25f)
        };
    }

    public void SetPosition(Vector3 pos)
    {
        preview.transform.position = pos;
    }

    public void SetSize(Vector3 size)
    {
        preview.transform.localScale = size;
        col.size = size;
    }

    public void Destroy()
    {
        if (preview) GameObject.DestroyImmediate(preview);
    }
}

public class SpawnCircleVolume : MonoBehaviour
{
    public float radius = 25f;

    public bool Contains(Vector3 worldPos)
    {
        Vector2 p = new Vector2(worldPos.x, worldPos.z);
        Vector2 c = new Vector2(transform.position.x, transform.position.z);
        return (p - c).sqrMagnitude <= radius * radius;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0, 1, 0, 0.5f);
        UnityEditor.Handles.DrawSolidDisc(
            new Vector3(transform.position.x, transform.position.y, transform.position.z),
            Vector3.up,
            radius
        );

        Gizmos.color = Color.green;
        UnityEditor.Handles.DrawWireDisc(
            new Vector3(transform.position.x, transform.position.y, transform.position.z),
            Vector3.up,
            radius
        );
    }
#endif
}