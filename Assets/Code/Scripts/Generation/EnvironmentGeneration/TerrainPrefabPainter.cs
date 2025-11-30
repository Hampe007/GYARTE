using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public partial class TerrainPrefabPainter : EditorWindow
{
    #region Fields

    // Terrain & core painter state
    Terrain terrain;
    int detailIndex = 0;
    int splatIndex = -1;

    // Detail paint mode
    bool addMode = true;
    int maxAddPerCell = 6;
    int targetDensity = 12;
    
    // Grass collider buffer
    static readonly Collider[] grassHitBuffer = new Collider[16];
    [SerializeField] LayerMask grassBlockerLayers = ~0; // everything by default
    [SerializeField] float grassCollisionRadius = 0.35f; // tweakable

    // Height & slope filters
    float minHeight = 0f;
    float maxHeight = 1000f;
    float maxSlope = 35f;

    // Noise filtering
    float noiseScale = 0.003f;
    float noiseThreshold = 0.45f;

    // Random seed
    int seed = 12345;

    // Cached UI labels
    string[] detailLabels = new string[0];
    string[] splatLabels  = new string[0];

    // Editor scroll
    Vector2 scroll;

    // Prefab rule system
    [SerializeField] bool paintPrefabs = false;
    [SerializeField] PrefabPaintRule[] prefabRules;
    [SerializeField] bool[] ruleFoldouts;

    // Preset foldouts
    bool presetsFoldout       = false;
    bool presetForestsFoldout = false;
    bool presetRocksFoldout   = false;
    bool presetBushesFoldout  = false;
    bool presetFlowersFoldout = false;
    bool presetDeadFoldout    = false;
    bool presetSnowFoldout    = false;
    bool presetDesertFoldout  = false;

    // Grass clearing utility
    public float clearRadius = 1.5f;

    // Global circle mask system
    [SerializeField] private List<SpawnCircleVolume> globalCircles;
    bool useGlobalCircles = false;
    
    // Grass circle exclusion
    [SerializeField] private bool useGrassCircleExclusion = false;
    [SerializeField] private float grassCircleFalloff = 8f;

    // Hierarchy roots
    private Transform prefabRoot;
    private Transform currentSessionRoot;

    // Paint session index
    [SerializeField] private int paintSessionIndex = 0;

    // Batch spawning
    [SerializeField] private int maxBatchSize = 100;
    [SerializeField] private Transform lastBatchRoot;
    
    // Cached batch roots for stable batching
    Dictionary<(PrefabPaintRule rule, int batchIndex), Transform> cachedBatchRoots;

    // Terrain cached data
    float[,] cachedSlopes;
    float[,,] cachedAlpha;
    int terrainLayerMask;
    float noiseOffsetX, noiseOffsetY;

    // Cancel / interrupt state
    private bool cancelRequested = false;

    // Optional terrain override
    [SerializeField] private bool allowTerrainOverride = false;

    // Grass-only mode
    [SerializeField] private bool detailGrassOnly = false;
    [SerializeField] private bool fullGrassCoverage = false;

    // Underwater filtering
    [SerializeField] public float shorelineBuffer = 0.25f;
    public bool allowUnderwaterPainting = false;
    public Transform ocean;

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
        AutoAssignOcean();
        cachedBatchRoots = new Dictionary<(PrefabPaintRule rule, int batchIndex), Transform>();
        
        // Create or find prefabRoot
        if (prefabRoot == null)
        {
            var existing = GameObject.Find("PrefabPainterRoot");
            if (existing != null) prefabRoot = existing.transform;
            else
            {
                var go = new GameObject("PrefabPainterRoot");
                prefabRoot = go.transform;
            }
        }
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

        // Correct mode logic
        bool doGrass = detailGrassOnly || !paintPrefabs;
        bool doPrefabs = paintPrefabs && !detailGrassOnly;

        if (!doGrass && !doPrefabs)
        {
            Debug.Log("No painting mode active. Exiting Run().");
            return;
        }

        cachedBatchRoots = new Dictionary<(PrefabPaintRule rule, int batchIndex), Transform>();

        SyncFoldoutArray();
        var td = terrain.terrainData;
        
        Vector3 terrainPos = terrain.transform.position;
        Vector3 size = td.size;

        int detailRes = td.detailResolution;
        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;

        float invDetailResMinus1 = 1f / Mathf.Max(1, detailRes - 1);
        float invHmResMinus1 = 1f / Mathf.Max(1, hmRes - 1);
        float terrainHeight = td.size.y;
        
        var heights = td.GetHeights(0, 0, hmRes, hmRes);

        Undo.RegisterCompleteObjectUndo(td, "Prefab Painter");

        int[,] current = doGrass ? td.GetDetailLayer(0, 0, detailRes, detailRes, detailIndex) : null;
        int[,] output = doGrass ? new int[detailRes, detailRes] : null;

        float[,,] alphaAll = td.GetAlphamaps(0, 0, amRes, amRes);

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
                var alpha3d = td.GetAlphamaps(0, 0, amRes, amRes);
                int safe = Mathf.Clamp(splatIndex, 0, td.alphamapLayers - 1);

                alphaLayer = new float[amRes, amRes];
                for (int y = 0; y < amRes; y++)
                    for (int x = 0; x < amRes; x++)
                        alphaLayer[y, x] = alpha3d[y, x, safe];
            }
            catch
            {
                useAlpha = false;
                alphaLayer = null;
            }
        }

        int totalCells = 0;
        int affectedCells = 0;

        try
        {
            // GRASS DETAIL PAINTING
            if (doGrass)
            {
                // Do we have circles for grass exclusion?
                bool hasGrassCircles =
                    useGrassCircleExclusion &&
                    globalCircles != null &&
                    globalCircles.Count > 0;

                for (int y = 0; y < detailRes; y++)
                {
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

                        int hx = Mathf.RoundToInt(nx * (hmRes - 1));
                        int hy = Mathf.RoundToInt(ny * (hmRes - 1));

                        float worldHeight = heights[hy, hx] * terrainHeight;

                        // Compute world-space XZ for grass sample (needed by circles + collider)
                        float wx = terrainPos.x + nx * size.x;
                        float wz = terrainPos.z + ny * size.z;

                        // Underwater guard
                        if (!allowUnderwaterPainting && ocean != null)
                        {
                            float waterLine = ocean.position.y;
                            if (worldHeight < waterLine + 0.1f)
                            {
                                output[y, x] = addMode ? current[y, x] : 0;
                                continue;
                            }
                        }

                        // Alpha filter
                        if (useAlpha && splatIndex >= 0)
                        {
                            int ax = Mathf.Clamp(Mathf.FloorToInt(nx * (amRes - 1)), 0, amRes - 1);
                            int ay = Mathf.Clamp(Mathf.FloorToInt(ny * (amRes - 1)), 0, amRes - 1);

                            if (alphaLayer[ay, ax] < 0.3f)
                            {
                                output[y, x] = addMode ? current[y, x] : 0;
                                continue;
                            }
                        }

                        // Noise filter
                        if (!fullGrassCoverage)
                        {
                            float n = Mathf.PerlinNoise(
                                x * Mathf.Max(noiseScale, 1e-6f) + seed * 0.1234f,
                                y * Mathf.Max(noiseScale, 1e-6f) + seed * 0.5678f
                            );

                            if (n < noiseThreshold)
                            {
                                output[y, x] = addMode ? current[y, x] : 0;
                                continue;
                            }
                        }

                        // Circle falloff mask
                        float densityFactor = 1f;

                        if (hasGrassCircles)
                        {
                            float blockFactor = 0f;

                            for (int c = 0; c < globalCircles.Count; c++)
                            {
                                var circle = globalCircles[c];
                                if (!circle) continue;

                                float r = circle.radius;
                                Vector2 p = new Vector2(wx, wz);
                                Vector2 cc = new Vector2(circle.transform.position.x, circle.transform.position.z);

                                float dist = Vector2.Distance(p, cc);

                                if (dist >= r)
                                    continue;

                                if (grassCircleFalloff <= 0f)
                                {
                                    blockFactor = 1f;
                                    break;
                                }

                                float inner = Mathf.Max(0f, r - grassCircleFalloff);

                                float bf;
                                if (dist <= inner)
                                {
                                    bf = 1f;
                                }
                                else
                                {
                                    float t = (dist - inner) / Mathf.Max(0.001f, grassCircleFalloff);
                                    bf = 1f - Mathf.Clamp01(t);
                                }

                                if (bf > blockFactor)
                                    blockFactor = bf;
                            }

                            if (blockFactor >= 1f)
                            {
                                output[y, x] = addMode ? current[y, x] : 0;
                                continue;
                            }

                            densityFactor = 1f - blockFactor;
                        }

                        affectedCells++;

                        // COLLIDER BLOCKER CHECK (capsule)
                        Vector3 bottom = new Vector3(wx, worldHeight + 0.2f, wz);
                        Vector3 top = bottom + Vector3.up * 2.0f;

                        int hitCount = Physics.OverlapCapsuleNonAlloc(
                            bottom,
                            top,
                            grassCollisionRadius,
                            grassHitBuffer,
                            grassBlockerLayers
                        );

                        if (hitCount > 0)
                        {
                            output[y, x] = addMode ? current[y, x] : 0;
                            continue;
                        }

                        // Final add / replace logic
                        if (addMode)
                        {
                            int curr = current[y, x];
                            int add = Mathf.Min(maxAddPerCell, 32 - curr);
                            int target = Mathf.Clamp(curr + add, 0, 32);

                            if (densityFactor < 1f)
                                target = Mathf.RoundToInt(Mathf.Lerp(curr, target, densityFactor));

                            output[y, x] = target;
                        }
                        else
                        {
                            int target = Mathf.Clamp(targetDensity, 0, 32);

                            if (densityFactor < 1f)
                                target = Mathf.RoundToInt(target * densityFactor);

                            output[y, x] = target;
                        }
                    }

                }

                if (passPaint)
                {
                    td.SetDetailLayer(0, 0, detailIndex, output);
                    EditorUtility.SetDirty(td);
                }
            }

            // PREFABS
            if (passPaint && doPrefabs)
            {
                RefreshCircleList();
                BuildCaches(td);

                int count = PaintPrefabs(td, heights, alphaLayer);
                Debug.Log("[PrefabPainter] Spawned " + count + " prefabs.");
            }

            if (passPaint)
                paintSessionIndex++;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // Popup only for grass mode
        if (passPaint && doGrass)
        {
            EditorUtility.DisplayDialog("Terrain Prefab Painter", "Painted " + affectedCells + "/" + totalCells + " cells", "OK");
        }
        else if (!passPaint && doGrass)
        {
            LogDryRun(td, affectedCells);
            EditorUtility.DisplayDialog("Terrain Prefab Painter", "Dry Run: " + affectedCells + " cells affected", "OK");
        }

        cancelRequested = false;
    }

    #endregion

    #region PrefabSpawning

    // Uses cached alphaLayer and preprocessed rule data to speed up placement
    int PaintPrefabs(TerrainData td, float[,] heights, float[,] alphaLayer)
    {
        int spawnedCount = 0;
        
        cachedBatchRoots = new Dictionary<(PrefabPaintRule rule, int batchIndex), Transform>();
        
        if (prefabRules == null || prefabRules.Length == 0) 
            return spawnedCount;
        if (!terrain) 
            return spawnedCount;

        int hmRes = td.heightmapResolution;
        int amRes = td.alphamapResolution;
        int res = td.detailResolution;
        
        float[,,] alphaAll = td.GetAlphamaps(0, 0, amRes, amRes);

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
            return spawnedCount;

        // Reset batch indices once per paint pass
        for (int i = 0; i < activeRules.Count; i++)
        {
            var rule = activeRules[i];
            rule.variantBatchIndex = new Dictionary<string, int>();
        }
        
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
            
            // Prefab-only underwater + shoreline stopper
            if (!allowUnderwaterPainting && ocean != null)
            {
                float waterLine = ocean.position.y;

                // Underwater block
                if (worldHeight < waterLine)
                    return;

                // Shoreline buffer block
                if (worldHeight < waterLine + shorelineBuffer)
                    return;
            }

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
                    if (alpha < 0.75f) continue;
                }

                // Multi-circle masking (auto-detected circles)
                if (rule.useCircleArea && globalCircles != null && globalCircles.Count > 0)
                {
                    bool insideAny = false;

                    for (int c = 0; c < globalCircles.Count; c++)
                    {
                        var circle = globalCircles[c];
                        if (!circle) continue;

                        float r2 = circle.radius * circle.radius;

                        Vector2 p = new Vector2(wx, wz);
                        Vector2 cc = new Vector2(circle.transform.position.x,
                            circle.transform.position.z);

                        if ((p - cc).sqrMagnitude <= r2)
                        {
                            insideAny = true;
                            break;
                        }
                    }

                    if (rule.circleExcludes)
                    {
                        // Exclude → skip this rule if inside ANY circle
                        if (insideAny)
                            continue;
                    }
                    else
                    {
                        // Include → skip this rule if NOT inside ANY circle
                        if (!insideAny)
                            continue;
                    }
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
                
                Transform ruleRoot = GetRuleRoot(rule);
                if (currentSessionRoot != null && ruleRoot.parent != currentSessionRoot)
                    ruleRoot.SetParent(currentSessionRoot);

                // Global Batching
                Transform batchRoot = GetGlobalBatchRoot(rule, ruleRoot);
                instance.transform.SetParent(batchRoot, true);
                lastBatchRoot = batchRoot;
                
                if (currentSessionRoot != null && batchRoot.parent != currentSessionRoot)
                    batchRoot.SetParent(currentSessionRoot);

                // Grass clearing
                if (rule.clearRadius > 0f)
                {
                    ClearGrassAround(rule, detailBuffer, td, instance.transform.position, rule.clearRadius);
                }

                // Marker component
                if (!instance.TryGetComponent<TileProp>(out _))
                    instance.AddComponent<TileProp>();
                
                spawnedCount++;
            }
        }


        RefreshCircleList();

        bool circlesAvailable = globalCircles != null && globalCircles.Count > 0;

        if (useGlobalCircles && circlesAvailable)
        {
            int samples = 0;

            // Pick sample count based on largest circle
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
                    return spawnedCount;
                }

                var c = globalCircles[rand.Next(globalCircles.Count)];
                if (!c) continue;

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
                    return spawnedCount;
                }
                float ny = y * invResMinus1;

                for (int x = 0; x < detailRes; x++)
                {
                    if (cancelRequested)
                    {
                        CleanupCancelledSession();
                        return spawnedCount;
                    }
                    float nx = x * invResMinus1;

                    float wx = origin.x + nx * size.x;
                    float wz = origin.z + ny * size.z;

                    ProcessSample(nx, ny, wx, wz);
                }
            }
        }

        td.SetDetailLayer(0, 0, detailIndex, detailBuffer);

        return spawnedCount;
    }

    #endregion


    #region Helpers

    float splatMinCache => 0.01f;

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
    
    void SetHideFlagsRecursive(Transform root, HideFlags flags)
    {
        if (root == null) return;

        root.hideFlags = flags;

        foreach (Transform child in root)
            SetHideFlagsRecursive(child, flags);
    }
    
    bool IsInsideAnyCircle(float wx, float wz)
    {
        if (globalCircles == null || globalCircles.Count == 0)
            return false; // not inside ANY

        Vector2 p = new Vector2(wx, wz);

        for (int i = 0; i < globalCircles.Count; i++)
        {
            var c = globalCircles[i];
            if (!c) continue;

            float r2 = c.radius * c.radius;
            Vector2 cc = new Vector2(c.transform.position.x, c.transform.position.z);

            if ((p - cc).sqrMagnitude <= r2)
                return true;
        }

        return false;
    }

    void RemoveAllGrass()
    {
        if (!terrain || !terrain.terrainData)
            return;

        TerrainData td = terrain.terrainData;

        if (detailIndex < 0 || detailIndex >= td.detailPrototypes.Length)
            return;

        int res = td.detailResolution;

        int[,] empty = new int[res, res]; // autofilled with 0

        td.SetDetailLayer(0, 0, detailIndex, empty);

        Debug.Log("[GrassPainter] All grass removed from detail layer: " + detailIndex);
    }
    
    Transform GetGlobalBatchRoot(PrefabPaintRule rule, Transform ruleRoot)
    {
        if (rule.variantBatchIndex == null)
            rule.variantBatchIndex = new Dictionary<string, int>();

        const string batchKey = "_GLOBAL_";

        // Read current batch index
        rule.variantBatchIndex.TryGetValue(batchKey, out int currentBatchIndex);

        var cacheKey = (rule, currentBatchIndex);

        // Check existing batch
        if (cachedBatchRoots.TryGetValue(cacheKey, out Transform cached))
        {
            // If not full, use it
            if (cached.childCount < maxBatchSize)
                return cached;

            // If full -> increment & create new
            currentBatchIndex++;
            rule.variantBatchIndex[batchKey] = currentBatchIndex;
            cacheKey = (rule, currentBatchIndex);
        }

        // Create new batch
        string batchName = $"Batch_{currentBatchIndex:000}";
        GameObject b = new GameObject(batchName);
        Transform batchRoot = b.transform;
        batchRoot.SetParent(prefabRoot);
        batchRoot.hideFlags = HideFlags.HideInHierarchy;

        // Cache it
        cachedBatchRoots[cacheKey] = batchRoot;

        return batchRoot;
    }
    
    void AutoAssignOcean()
    {
        // If already assigned, do nothing
        if (ocean != null)
            return;

        // Try common names
        string[] names = { "Ocean", "Water", "Sea", "WaterPlane", "WaterSurface" };

        for (int i = 0; i < names.Length; i++)
        {
            var go = GameObject.Find(names[i]);
            if (go != null)
            {
                ocean = go.transform;
                return;
            }
        }

        // Fallback: search any transform with a water-like name
        var allTransforms = FindObjectsOfType<Transform>();
        for (int i = 0; i < allTransforms.Length; i++)
        {
            string n = allTransforms[i].name.ToLower();
            if (n.Contains("water") || n.Contains("ocean") || n.Contains("sea"))
            {
                ocean = allTransforms[i];
                return;
            }
        }
    }
   
#if UNITY_EDITOR
    void CreateCircleForRule(PrefabPaintRule rule)
    {
        // Create new GameObject
        GameObject go = new GameObject("Circle_" + rule.name);
        Undo.RegisterCreatedObjectUndo(go, "Create Circle Volume");

        // Add circle component
        var circle = go.AddComponent<SpawnCircleVolume>();
        circle.radius = 25f; // default size

        // Position it at terrain center
        if (terrain != null)
        {
            Vector3 pos = terrain.transform.position + terrain.terrainData.size * 0.5f;
            pos.y = terrain.transform.position.y;
            go.transform.position = pos;
        }

        // Add to global list
        RefreshCircleList();

        // Ensure painter updates immediately
        EditorUtility.SetDirty(this);

        Debug.Log($"[Painter] Created circle for rule '{rule.name}'");
    }
#endif

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

    SpawnCircleVolume CreateGlobalCircleVolume()
    {
        GameObject go = new GameObject("SpawnCircle");

        var vol = go.AddComponent<SpawnCircleVolume>();
        vol.radius = 100f;

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
        if (currentSessionRoot == null)
            GetPrefabRoot();

        string folderName = string.IsNullOrWhiteSpace(rule.name)
            ? (rule.prefab != null ? rule.prefab.name : "UnnamedRule")
            : rule.name;

        // Only search inside THIS session (never globally)
        Transform found = currentSessionRoot.Find(folderName);
        if (found != null)
            return found;

        // Create rule folder under THIS session
        GameObject folder = new GameObject(folderName);
        folder.transform.SetParent(currentSessionRoot);
        folder.hideFlags = HideFlags.HideInHierarchy;

        return folder.transform;
    }

    Transform GetPrefabRoot()
    {
        // Ensure global root exists
        if (prefabRoot == null)
        {
            var existing = GameObject.Find("TerrainPropRoot");
            if (existing != null)
            {
                prefabRoot = existing.transform;
            }
            else
            {
                GameObject root = new GameObject("TerrainPropRoot");
                prefabRoot = root.transform;
            }
        }

        prefabRoot.hideFlags = HideFlags.HideInHierarchy;

        // Create / get the correct paint session folder
        string sessionName = $"PaintSession_{paintSessionIndex:000}";
        Transform session = prefabRoot.Find(sessionName);

        if (session == null)
        {
            GameObject sessionObj = new GameObject(sessionName);
            sessionObj.transform.SetParent(prefabRoot);
            session = sessionObj.transform;
        }

        currentSessionRoot = session;
        currentSessionRoot.hideFlags = HideFlags.HideInHierarchy;

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

    #endregion
}