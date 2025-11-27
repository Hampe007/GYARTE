using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public partial class TerrainPrefabPainter : EditorWindow
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
    [SerializeField] PrefabPaintRule[] prefabRules;
    [SerializeField] bool[] ruleFoldouts;

    bool presetsFoldout = false;
    bool presetForestsFoldout = false;
    bool presetRocksFoldout = false;
    bool presetBushesFoldout = false;
    bool presetFlowersFoldout = false;
    bool presetDeadFoldout = false;
    bool presetSnowFoldout = false;
    bool presetDesertFoldout = false;

    public float clearRadius = 1.5f;

    [SerializeField] private List<SpawnCircleVolume> globalCircles = new List<SpawnCircleVolume>();
    bool useGlobalCircles = false;

    private Transform prefabRoot;
    
    [SerializeField] private int paintSessionIndex = 0;
    private Transform currentSessionRoot;
    
    float[,] cachedSlopes;
    float[,,] cachedAlpha;
    int terrainLayerMask;
    float noiseOffsetX, noiseOffsetY;
    
    private bool cancelRequested = false;

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
        cancelRequested = false;
        
        SyncFoldoutArray();
        var td = terrain.terrainData;

        int detailRes = td.detailResolution;
        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;

        float invDetailResMinus1 = 1f / Mathf.Max(1, detailRes - 1);
        float invHmResMinus1 = 1f / Mathf.Max(1, hmRes - 1);
        float terrainHeight = td.size.y;

        Undo.RegisterCompleteObjectUndo(td, "Prefab Painter");

        int[,] current = addMode
            ? td.GetDetailLayer(0, 0, detailRes, detailRes, detailIndex)
            : new int[detailRes, detailRes];

        int[,] output = new int[detailRes, detailRes];
        var heights = td.GetHeights(0, 0, hmRes, hmRes);

        /* --------------------------------------------------
           Alpha map skip detection
        -------------------------------------------------- */

        bool anyRuleSplat = false;
        if (prefabRules != null)
        {
            for (int i = 0; i < prefabRules.Length; i++)
            {
                var r = prefabRules[i];
                if (r != null && r.splatIndex >= 0)
                {
                    anyRuleSplat = true;
                    break;
                }
            }
        }

        bool useAlpha = (splatIndex >= 0 || anyRuleSplat);
        float[,] alphaLayer = null;

        if (useAlpha)
        {
            try
            {
                var alpha3D = td.GetAlphamaps(0, 0, amRes, amRes);

                alphaLayer = new float[amRes, amRes];
                int safe = Mathf.Clamp(splatIndex, 0, td.alphamapLayers - 1);

                for (int y = 0; y < amRes; y++)
                {
                    for (int x = 0; x < amRes; x++)
                    {
                        alphaLayer[y, x] = alpha3D[y, x, safe];
                    }
                }
            }
            catch
            {
                useAlpha = false;
                alphaLayer = null;
            }
        }

        var rand = new System.Random(seed);

        int totalCells = 0;
        int affectedCells = 0;

        try
        {
            for (int y = 0; y < detailRes; y++)
            {
                // Throttled progress bar to reduce editor overhead
                if (y % 64 == 0)
                {
                    float progress = (float)y / detailRes;
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "Painting Details",
                        "Processing terrain...",
                        progress))
                    {
                        EditorUtility.ClearProgressBar();
                        cancelRequested = true;
                        return;
                    }
                }

                float ny = y * invDetailResMinus1;

                for (int x = 0; x < detailRes; x++)
                {
                    totalCells++;

                    float nx = x * invDetailResMinus1;

                    int hx = Clamp01Index(Mathf.RoundToInt(nx * (hmRes - 1) * invHmResMinus1 * hmRes), hmRes);
                    int hy = Clamp01Index(Mathf.RoundToInt(ny * (hmRes - 1) * invHmResMinus1 * hmRes), hmRes);

                    float worldHeight = heights[hy, hx] * terrainHeight;

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

                    if (useAlpha && alphaLayer != null && splatIndex >= 0)
                    {
                        int ax = Mathf.FloorToInt(nx * (amRes - 1));
                        int ay = Mathf.FloorToInt(ny * (amRes - 1));

                        float w = alphaLayer[ay, ax];

                        if (w < Safe01(splatMinCache))
                        {
                            output[y, x] = addMode ? current[y, x] : 0;
                            continue;
                        }
                    }

                    float n = Mathf.PerlinNoise(
                        x * Mathf.Max(noiseScale, 1e-6f) + seed * 0.1234f,
                        y * Mathf.Max(noiseScale, 1e-6f) + seed * 0.5678f
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
                td.SetDetailLayer(0, 0, detailIndex, output);
                EditorUtility.SetDirty(td);
            }

            if (passPaint && paintPrefabs)
            {
                RefreshCircleList();
                BuildCaches(td);
                PaintPrefabs(td, heights, alphaLayer);
            }
            
            if (passPaint)
                paintSessionIndex++;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (passPaint)
        {
            EditorUtility.DisplayDialog("Terrain Prefab Painter", $"Painted {affectedCells}/{totalCells} cells", "OK");
        }
        else
        {
            LogDryRun(td, affectedCells);
            EditorUtility.DisplayDialog("Terrain Prefab Painter", $"Dry Run: {affectedCells} cells affected", "OK");
        }
        cancelRequested = false;
    }

    #endregion


    #region PrefabSpawning

    // Uses cached alphaLayer and preprocessed rule data to speed up placement
    void PaintPrefabs(TerrainData td, float[,] heights, float[,] alphaLayer)
    {
        if (prefabRules == null || prefabRules.Length == 0) return;
        if (!terrain) return;

        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;
        int res = td.detailResolution;

        int[,] detailBuffer = td.GetDetailLayer(0, 0, res, res, detailIndex);

        Vector3 size = td.size;
        Vector3 origin = terrain.transform.position;

        var rand = new System.Random(seed);

        bool shouldDelete = false;
        for (int i = 0; i < prefabRules.Length; i++)
        {
            var rule = prefabRules[i];
            if (rule != null && rule.deleteBeforeSpawn)
            {
                shouldDelete = true;
                break;
            }
        }

        // Build a compact list of active rules
        List<PrefabPaintRule> activeRules = new List<PrefabPaintRule>();
        for (int i = 0; i < prefabRules.Length; i++)
        {
            var rule = prefabRules[i];
            if (rule == null) continue;

            bool hasVariants = rule.variants != null && rule.variants.Length > 0;
            bool hasBasePrefab = rule.prefab != null;

            if (!hasVariants && !hasBasePrefab) continue;
            if (rule.density <= 0f) continue;

            if (hasVariants)
            {
                float total = 0f;
                for (int v = 0; v < rule.variants.Length; v++)
                {
                    var variant = rule.variants[v];
                    if (variant == null || variant.prefab == null) continue;
                    if (variant.weight <= 0f) continue;
                    total += variant.weight;
                }
                rule.cachedVariantWeight = total;
            }
            else
            {
                rule.cachedVariantWeight = 0f;
            }

            activeRules.Add(rule);
        }

        if (activeRules.Count == 0)
            return;

        int hmResMinus1 = Mathf.Max(1, hmRes - 1);
        int amResMinus1 = Mathf.Max(1, amRes - 1);
        float invHmResMinus1 = 1f / hmResMinus1;
        float invResMinus1 = 1f / Mathf.Max(1, res - 1);
        float terrainHeight = size.y;

        bool alphaAvailable = (alphaLayer != null);

        void ProcessSample(float nx, float ny, float wx, float wz)
        {
            if (cancelRequested)
            {
                CleanupCancelledSession();
                return;
            }
            // Precompute height & slope once
            int hx = Mathf.RoundToInt(nx * hmResMinus1);
            int hy = Mathf.RoundToInt(ny * hmResMinus1);
            hx = Clamp01Index(hx, hmRes);
            hy = Clamp01Index(hy, hmRes);

            float worldHeight = heights[hy, hx] * terrainHeight;
            float slopeDeg = cachedSlopes[hy, hx];

            // Loop rules
            for (int i = 0; i < activeRules.Count; i++)
            {
                var rule = activeRules[i];
                if (rule == null) continue;

                bool hasVariants = rule.variants != null && rule.variants.Length > 0;
                bool hasBasePrefab = rule.prefab != null;
                if (!hasVariants && !hasBasePrefab) continue;

                // Slope / Height fast reject
                if (worldHeight < rule.minHeight || worldHeight > rule.maxHeight) continue;
                if (slopeDeg > rule.maxSlope) continue;

                // Terrain layer (alpha) check
                if (rule.splatIndex >= 0)
                {
                    int ax = Mathf.FloorToInt(nx * amResMinus1);
                    int ay = Mathf.FloorToInt(ny * amResMinus1);

                    float alpha = cachedAlpha[ay, ax, rule.splatIndex]; // use cached alphamap
                    if (alpha < 0.01f) continue;
                }

                // Volume area check
                if (rule.useVolumeArea && rule.volumeRef != null && rule.volumeRef.col != null)
                {
                    Vector3 checkPos = new Vector3(wx, worldHeight, wz);
                    if (!rule.volumeRef.col.bounds.Contains(checkPos)) continue;
                }

                // Noise + density (noise first for high-density)
                float noiseValue = Mathf.PerlinNoise(
                    nx * rule.noiseScale * 10f + noiseOffsetX,
                    ny * rule.noiseScale * 10f + noiseOffsetY
                );

                if (noiseValue < rule.noiseThreshold) continue;
                if (rand.NextDouble() > rule.density) continue;

                // Pick prefab
                GameObject chosenPrefab = rule.prefab;

                if (hasVariants && rule.cachedVariantWeight > 0f)
                {
                    float pick = (float)rand.NextDouble() * rule.cachedVariantWeight;
                    float c = 0f;

                    for (int v = 0; v < rule.variants.Length; v++)
                    {
                        var vr = rule.variants[v];
                        if (vr == null || vr.prefab == null || vr.weight <= 0f) continue;

                        c += vr.weight;
                        if (pick <= c)
                        {
                            chosenPrefab = vr.prefab;
                            break;
                        }
                    }
                }

                if (chosenPrefab == null) continue;

                // Spawn prefab (no expensive Raycast unless needed)
                Vector3 pos = new Vector3(wx, worldHeight, wz);

                GameObject prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(chosenPrefab);
                GameObject instance = prefabSource
                    ? (GameObject)PrefabUtility.InstantiatePrefab(prefabSource)
                    : Object.Instantiate(chosenPrefab);

                // Random scale
                float t = (float)rand.NextDouble();
                float baseScale = Mathf.Lerp(rule.randomScale.x, rule.randomScale.y, t);

                // Random per-axis variance
                float vx = (float)(rand.NextDouble() * 2.0 - 1.0) * rule.axisVariance.x;
                float vy = (float)(rand.NextDouble() * 2.0 - 1.0) * rule.axisVariance.y;
                float vz = (float)(rand.NextDouble() * 2.0 - 1.0) * rule.axisVariance.z;

                // Perlin shape noise
                float shapeNoise = 0f;
                if (rule.shapeNoiseScale > 0f && rule.shapeNoiseStrength > 0f)
                {
                    shapeNoise = Mathf.PerlinNoise(
                        (wx + seed * 11.13f) * rule.shapeNoiseScale,
                        (wz + seed * 7.33f) * rule.shapeNoiseScale
                    ) * 2f - 1f;

                    shapeNoise *= rule.shapeNoiseStrength;
                }

                // Final scale
                Vector3 finalScale = new Vector3(
                    baseScale + vx + shapeNoise,
                    baseScale + vy + shapeNoise,
                    baseScale + vz + shapeNoise
                );

                instance.transform.localScale = finalScale;

                // Random rotation & ground alignment
                float randomY = rand.Next(0, 360);
                Vector3 rayStart = pos + Vector3.up * 200f;

                if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 500f, terrainLayerMask, QueryTriggerInteraction.Ignore))
                {
                    instance.transform.position = hit.point;
                    Quaternion slopeRot = Quaternion.FromToRotation(Vector3.up, hit.normal);
                    instance.transform.rotation = slopeRot * Quaternion.Euler(0, randomY, 0);
                }
                else
                {
                    instance.transform.position = pos;
                    instance.transform.rotation = Quaternion.Euler(0, randomY, 0);
                }

                // Parenting
                Transform ruleRoot = GetRuleRoot(rule);

                if (hasVariants && chosenPrefab != rule.prefab)
                {
                    Transform variantRoot = GetVariantRoot(ruleRoot, chosenPrefab.name);
                    instance.transform.SetParent(variantRoot, true);
                }
                else
                {
                    instance.transform.SetParent(ruleRoot, true);
                }

                // Grass clearing
                if (rule.clearRadius > 0f)
                {
                    ClearGrassAround(rule, detailBuffer, td, instance.transform.position, rule.clearRadius);
                }

                // Marker component
                if (!instance.TryGetComponent<TileProp>(out _))
                    instance.AddComponent<TileProp>();
            }
        }


        RefreshCircleList();

        bool circlesAvailable = useGlobalCircles && globalCircles != null && globalCircles.Count > 0;

        if (circlesAvailable)
        {
            int samples = 0;

            for (int i = 0; i < globalCircles.Count; i++)
            {
                float r = globalCircles[i].radius;
                int sCount = Mathf.RoundToInt(r * r * 2f);
                samples = Mathf.Max(samples, sCount);
            }

            samples = Mathf.Clamp(samples, 5000, 50000);

            for (int s = 0; s < samples; s++)
            {
                if (cancelRequested)
                {
                    CleanupCancelledSession();
                    return;
                }
                var c = globalCircles[rand.Next(globalCircles.Count)];

                float u = (float)rand.NextDouble();
                float a = (float)rand.NextDouble() * Mathf.PI * 2f;
                float dist = Mathf.Sqrt(u) * c.radius;

                float wx = c.transform.position.x + Mathf.Cos(a) * dist;
                float wz = c.transform.position.z + Mathf.Sin(a) * dist;

                float nx = Mathf.Clamp01((wx - origin.x) / size.x);
                float ny = Mathf.Clamp01((wz - origin.z) / size.z);

                ProcessSample(nx, ny, wx, wz);
            }
        }
        else
        {
            int detailRes = res;

            for (int y = 0; y < detailRes; y++)
            {
                if (cancelRequested)
                {
                    CleanupCancelledSession();
                    return;
                }
                float ny = y * invResMinus1;

                for (int x = 0; x < detailRes; x++)
                {
                    if (cancelRequested)
                    {
                        CleanupCancelledSession();
                        return;
                    }
                    float nx = x * invResMinus1;

                    float wx = origin.x + nx * size.x;
                    float wz = origin.z + ny * size.z;

                    ProcessSample(nx, ny, wx, wz);
                }
            }
        }

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
        if (prefabRules == null)
            prefabRules = new PrefabPaintRule[0];

        if (ruleFoldouts == null)
            ruleFoldouts = new bool[0];

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

        detailIndex = Mathf.Clamp(detailIndex, 0, Mathf.Max(0, td.detailPrototypes.Length - 1));
        splatIndex = Mathf.Clamp(splatIndex, -1, td.alphamapLayers - 1);
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

    void ClearGrassAround(PrefabPaintRule rule, int[,] buffer, TerrainData td, Vector3 worldPos, float radius)
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

        if (rule.clearedGrass == null)
            rule.clearedGrass = new Dictionary<Vector2Int, int>();

        for (int z = -radCells; z <= radCells; z++)
        {
            for (int x = -radCells; x <= radCells; x++)
            {
                int sx = cx + x;
                int sz = cz + z;

                if (sx < 0 || sx >= detailRes) continue;
                if (sz < 0 || sz >= detailRes) continue;

                float dist = Mathf.Sqrt(x * x + z * z);
                if (dist > radCells) continue;

                Vector2Int key = new Vector2Int(sx, sz);

                if (!rule.clearedGrass.ContainsKey(key))
                    rule.clearedGrass[key] = buffer[sz, sx];

                buffer[sz, sx] = 0;
            }
        }
    }

    void RefreshCircleList()
    {
        if (globalCircles == null)
            globalCircles = new List<SpawnCircleVolume>();
        else
            globalCircles.Clear();

        var found = FindObjectsByType<SpawnCircleVolume>(FindObjectsSortMode.None);

        for (int i = 0; i < found.Length; i++)
        {
            var c = found[i];
            if (!globalCircles.Contains(c))
                globalCircles.Add(c);
        }
    }
    
    void BuildCaches(TerrainData td)
    {
        int hmWidth = td.heightmapResolution;
        int hmHeight = td.heightmapResolution;

        // Build slope cache
        cachedSlopes = new float[hmHeight, hmWidth];
        for (int y = 0; y < hmHeight; y++)
        {
            for (int x = 0; x < hmWidth; x++)
            {
                float nx = (float)x / (hmWidth - 1);
                float ny = (float)y / (hmHeight - 1);

                Vector3 n = td.GetInterpolatedNormal(nx, ny);
                float slope = Vector3.Angle(n, Vector3.up);
                cachedSlopes[y, x] = slope;
            }
        }

        // Build alphamap cache
        int aw = td.alphamapWidth;
        int ah = td.alphamapHeight;
        int layers = td.alphamapLayers;

        cachedAlpha = new float[ah, aw, layers];
        float[,,] alpha = td.GetAlphamaps(0, 0, aw, ah);

        for (int y = 0; y < ah; y++)
        for (int x = 0; x < aw; x++)
        for (int l = 0; l < layers; l++)
            cachedAlpha[y, x, l] = alpha[y, x, l];

        // Layer mask for terrain only
        terrainLayerMask = LayerMask.GetMask("Default");

        // Random noise offsets per frame
        noiseOffsetX = seed * 0.123f;
        noiseOffsetY = seed * 0.456f;
    }
    
    void CleanupCancelledSession()
    {
        if (currentSessionRoot != null)
        {
            DestroyImmediate(currentSessionRoot.gameObject);
            currentSessionRoot = null;
        }

        // Reset all cleared grass buffers since they might be half-written
        if (prefabRules != null)
        {
            for (int i = 0; i < prefabRules.Length; i++)
            {
                if (prefabRules[i].clearedGrass != null)
                    prefabRules[i].clearedGrass.Clear();
            }
        }

        Debug.Log("Painting cancelled. Partial session deleted and grass buffers cleared.");
    }

    #endregion


    #region Debug / Management

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
                if (r != null && r.prefab != null)
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

    void DeleteLastSession()
    {
        if (prefabRoot == null)
        {
            Debug.Log("No sessions exist.");
            return;
        }

        if (paintSessionIndex == 0)
        {
            Debug.Log("No sessions to delete.");
            return;
        }

        // last session is index-1
        int sessionToDelete = paintSessionIndex - 1;
        string sessionName = $"PaintSession_{sessionToDelete:000}";

        Transform session = prefabRoot.Find(sessionName);
        if (session == null)
        {
            Debug.Log($"Session {sessionName} not found.");
            return;
        }

        int count = session.childCount;

        // Delete all prefabs inside the session
        for (int i = count - 1; i >= 0; i--)
            DestroyImmediate(session.GetChild(i).gameObject);

        // Delete the session folder
        DestroyImmediate(session.gameObject);

        // Move index back one step
        paintSessionIndex--;

        // Restore grass exactly like your original code
        RestoreGrassForDeletedSession();

        // NEW: If TerrainPropRoot is empty, delete it too
        if (prefabRoot != null && prefabRoot.childCount == 0)
        {
            DestroyImmediate(prefabRoot.gameObject);
            prefabRoot = null;
        }

        Debug.Log($"Deleted session {sessionName} containing {count} prefabs.");
    }
    
    void DeleteAllSessions()
    {
        if (prefabRoot == null)
            return;

        int totalRemoved = 0;

        // Delete every session under TerrainPropRoot
        for (int i = prefabRoot.childCount - 1; i >= 0; i--)
        {
            Transform session = prefabRoot.GetChild(i);
            totalRemoved += session.childCount;
            DestroyImmediate(session.gameObject);
        }

        paintSessionIndex = 0;

        // Restore grass from all clearedGrass dictionaries
        RestoreGrassForDeletedSession();

        // If TerrainPropRoot is now empty, delete it too
        if (prefabRoot.childCount == 0)
        {
            DestroyImmediate(prefabRoot.gameObject);
            prefabRoot = null;
        }

        Debug.Log($"NUKED ALL sessions. Removed {totalRemoved} prefabs.");
    }

    void RestoreGrassForDeletedSession()
    {
        if (terrain && terrain.terrainData)
        {
            TerrainData td = terrain.terrainData;
            int res = td.detailResolution;

            int[,] detailMap = td.GetDetailLayer(0, 0, res, res, detailIndex);

            if (prefabRules != null)
            {
                for (int i = 0; i < prefabRules.Length; i++)
                {
                    var rule = prefabRules[i];
                    if (rule == null || rule.clearedGrass == null) continue;

                    foreach (var kvp in rule.clearedGrass)
                    {
                        Vector2Int pos = kvp.Key;
                        if (pos.x >= 0 && pos.x < res && pos.y >= 0 && pos.y < res)
                            detailMap[pos.y, pos.x] = kvp.Value;
                    }

                    rule.clearedGrass.Clear();
                }
            }

            td.SetDetailLayer(0, 0, detailIndex, detailMap);
        }
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

    Transform GetRuleRoot(PrefabPaintRule rule)
    {
        Transform baseRoot = GetPrefabRoot();

        string folderName = string.IsNullOrWhiteSpace(rule.name)
            ? (rule.prefab != null ? rule.prefab.name : "UnnamedRule")
            : rule.name;

        Transform found = baseRoot.Find(folderName);
        if (found != null)
            return found;

        GameObject newFolder = new GameObject(folderName);
        newFolder.transform.SetParent(baseRoot);
        return newFolder.transform;
    }

    Transform GetPrefabRoot()
    {
        if (prefabRoot == null)
        {
            var existing = GameObject.Find("TerrainPropRoot");
            if (existing != null)
                prefabRoot = existing.transform;
            else
            {
                GameObject root = new GameObject("TerrainPropRoot");
                prefabRoot = root.transform;
            }
        }

        // Create / get session folder
        string sessionName = $"PaintSession_{paintSessionIndex:000}";
        Transform session = prefabRoot.Find(sessionName);
        if (session == null)
        {
            GameObject g = new GameObject(sessionName);
            g.transform.SetParent(prefabRoot);
            currentSessionRoot = g.transform;
        }
        else
        {
            currentSessionRoot = session;
        }

        return currentSessionRoot;
    }

    void CreatePresetRule(
        string name,
        float density,
        float noiseThreshold,
        Vector2 scale,
        float maxSlope,
        float clearRadius,
        Vector3 axisVar = default,
        float shapeNoiseScale = 0f,
        float shapeNoiseStrength = 0f
    )
    {
        var r = new PrefabPaintRule();
        r.name = name;

        r.density = density;
        r.noiseThreshold = noiseThreshold;
        r.randomScale = scale;
        r.maxSlope = maxSlope;
        r.clearRadius = clearRadius;
        r.axisVariance = axisVar;
        r.shapeNoiseScale = shapeNoiseScale;
        r.shapeNoiseStrength = shapeNoiseStrength;

        r.variants = new PrefabVariant[0];

        ArrayUtility.Add(ref prefabRules, r);
        ArrayUtility.Add(ref ruleFoldouts, true);
    }

    Transform GetVariantRoot(Transform ruleRoot, string variantName)
    {
        Transform t = ruleRoot.Find(variantName);
        if (t == null)
        {
            GameObject g = new GameObject(variantName);
            g.transform.SetParent(ruleRoot);
            t = g.transform;
        }
        return t;
    }

    #endregion
}