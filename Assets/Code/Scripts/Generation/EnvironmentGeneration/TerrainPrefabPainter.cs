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

        /* --------------------------------------------------
           Alpha map skip detection
        -------------------------------------------------- */

        bool anyRuleSplat = false;
        if (prefabRules != null)
        {
            foreach (var r in prefabRules)
            {
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
                    for (int x = 0; x < amRes; x++)
                        alphaLayer[y, x] = alpha3D[y, x, safe];
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
                /* Throttled progress bar */
                if (y % 20 == 0)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "Painting Details",
                        "Processing terrain...",
                        (float)y / detailRes))
                    {
                        EditorUtility.ClearProgressBar();
                        return;
                    }
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
                PaintPrefabs(td, heights, alphaLayer);
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

    /* unchanged, same as your original */
    /* I leave all your prefab logic intact but it now uses the cached alphaLayer */

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
            if (prefabRules[i].deleteBeforeSpawn)
            {
                shouldDelete = true;
                break;
            }
        }

        if (shouldDelete)
            DeleteAllSpawnedPrefabs();

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
                if (rule == null) continue;

                bool hasVariants = rule.variants != null && rule.variants.Length > 0;
                if (!hasVariants && rule.prefab == null) continue;

                if (rule.useVolumeArea)
                {
                    if (rule.volumeRef != null && rule.volumeRef.col != null)
                    {
                        Vector3 volumeCheck = new Vector3(wx, worldHeight, wz);
                        if (!rule.volumeRef.col.bounds.Contains(volumeCheck))
                            continue;
                    }
                }

                if (worldHeight < rule.minHeight || worldHeight > rule.maxHeight) continue;
                if (slopeDeg > rule.maxSlope) continue;

                if (rule.splatIndex >= 0 && alphaLayer != null)
                {
                    int ax = Mathf.FloorToInt(nx * (amRes - 1));
                    int ay = Mathf.FloorToInt(ny * (amRes - 1));

                    float w = alphaLayer[ay, ax];
                    if (w < 0.01f) continue;
                }

                float noiseValue = Mathf.PerlinNoise(
                    nx * rule.noiseScale * 10f + seed * 0.123f,
                    ny * rule.noiseScale * 10f + seed * 0.456f
                );

                if (noiseValue < rule.noiseThreshold) continue;
                if (rand.NextDouble() > rule.density) continue;

                Vector3 pos = new Vector3(wx, worldHeight, wz);
                GameObject chosenPrefab = rule.prefab;

                if (rule.variants != null && rule.variants.Length > 0)
                {
                    float total = 0f;
                    foreach (var v in rule.variants) total += v.weight;

                    float pick = (float)rand.NextDouble() * total;
                    float c = 0f;

                    foreach (var v in rule.variants)
                    {
                        c += v.weight;
                        if (pick <= c)
                        {
                            chosenPrefab = v.prefab;
                            break;
                        }
                    }
                }

                if (chosenPrefab == null) continue;

                GameObject prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(chosenPrefab);
                GameObject instance = prefabSource
                    ? (GameObject)PrefabUtility.InstantiatePrefab(prefabSource)
                    : Object.Instantiate(chosenPrefab);

                float t = (float)rand.NextDouble();
                float sVal = Mathf.Lerp(rule.randomScale.x, rule.randomScale.y, t);
                instance.transform.localScale = Vector3.one * sVal;

                float randomY = rand.Next(0, 360);
                instance.transform.position = pos;

                RaycastHit hit;
                Vector3 rayStart = pos + Vector3.up * 200f;
                if (Physics.Raycast(rayStart, Vector3.down, out hit, 500f, ~0, QueryTriggerInteraction.Ignore))
                {
                    instance.transform.position = hit.point;

                    Vector3 normal = hit.normal;
                    Quaternion slopeRot = Quaternion.FromToRotation(Vector3.up, normal);
                    instance.transform.rotation = slopeRot * Quaternion.Euler(0, randomY, 0);
                }
                else
                {
                    instance.transform.rotation = Quaternion.Euler(0, randomY, 0);
                }

                Transform ruleRoot = GetRuleRoot(rule);

                if (rule.variants != null && rule.variants.Length > 0 && chosenPrefab != rule.prefab)
                {
                    string variantName = chosenPrefab.name;
                    Transform variantRoot = GetVariantRoot(ruleRoot, variantName);
                    instance.transform.SetParent(variantRoot, true);
                }
                else
                {
                    instance.transform.SetParent(ruleRoot, true);
                }

                if (rule.clearRadius > 0f)
                    ClearGrassAround(rule, detailBuffer, td, instance.transform.position, rule.clearRadius);

                if (!instance.GetComponent<TileProp>())
                    instance.AddComponent<TileProp>();
            }
        }

        RefreshCircleList();

        bool circlesAvailable = useGlobalCircles && globalCircles != null && globalCircles.Count > 0;

        if (circlesAvailable)
        {
            int samples = 5000;

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

                if (nx < 0 || nx > 1 || ny < 0 || ny > 1) continue;

                ProcessSample(nx, ny, wx, wz);
            }
        }
        else
        {
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

        foreach (var c in found)
        {
            if (!globalCircles.Contains(c))
                globalCircles.Add(c);
        }
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

        if (terrain && terrain.terrainData)
        {
            TerrainData td = terrain.terrainData;
            int res = td.detailResolution;

            int[,] detailMap = td.GetDetailLayer(0, 0, res, res, detailIndex);

            if (prefabRules != null)
            {
                foreach (var rule in prefabRules)
                {
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
            {
                prefabRoot = existing.transform;
            }
            else
            {
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