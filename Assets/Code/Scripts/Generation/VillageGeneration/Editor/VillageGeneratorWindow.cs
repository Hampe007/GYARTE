using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class VillageGeneratorWindow : EditorWindow
{
    #region Fields

    VillageRuleSet rules;

    // Random
    bool useRandomSeed = false;
    int seed = 12345;

    // Plaza
    float plazaRadius = 8f;
    float plazaJitterPercent = 0.1f; // how far from center we allow the plaza to move

    // Roads
    int roadLayerIndex = 3;
    
    float baseRoadWidth = 4f;
    float roadWidthNarrowFactor = 0.7f; // at the edge
    
    int minRoadCount = 1;
    int maxRoadCount = 2;
    
    float branchChance = 0.35f;         // chance a sampled point becomes a branch
    int maxBranchesPerRoad = 2;         // limit
    float branchLengthPercent = 0.45f;  // branch length relative to main road

    // Layout
    float roadSampleSpacing = 2f;

    // Houses
    float minHouseSpacing = 6f;
    float maxHouseSpacing = 9f;
    float roadsideOffsetMin = 3f;
    float roadsideOffsetMax = 6f;
    int maxHousesPerArea = 40;

    // Props
    bool enableProps = true;
    float propDensityRoadside = 0.3f;
    float propDensityPlaza = 0.5f;

    bool clearBeforeGenerate = true;

    Vector2 scroll;

    const string RootName = "GeneratedVillages";

    #endregion

    #region Menu

    [MenuItem("Tools/Village Generator")]
    public static void Open()
    {
        GetWindow<VillageGeneratorWindow>("Village Generator");
    }

    #endregion

    #region GUI

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawRulesSection();
        DrawSeedSection();
        DrawPlazaSection();
        DrawRoadSection();
        DrawHousesSection();
        DrawPropsSection();
        DrawVolumesSection();
        DrawButtonsSection();

        EditorGUILayout.EndScrollView();
    }

    void DrawRulesSection()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Rule Set", EditorStyles.boldLabel);

        rules = (VillageRuleSet)EditorGUILayout.ObjectField(
            new GUIContent("Village Rules", "Prefab rules for buildings and props."),
            rules,
            typeof(VillageRuleSet),
            false
        );

        if (!rules)
        {
            EditorGUILayout.HelpBox("Assign a VillageRuleSet asset.", MessageType.Warning);
        }

        EditorGUILayout.Space(6);
    }

    void DrawSeedSection()
    {
        EditorGUILayout.LabelField("Random Seed", EditorStyles.boldLabel);

        useRandomSeed = EditorGUILayout.Toggle(
            new GUIContent("Use Random Seed", "If enabled, ignores the Seed field."),
            useRandomSeed
        );

        using (new EditorGUI.DisabledScope(useRandomSeed))
        {
            seed = EditorGUILayout.IntField(
                new GUIContent("Seed", "Deterministic seed for village generation."),
                seed
            );
        }

        if (GUILayout.Button("Randomize Seed"))
        {
            seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
        }

        EditorGUILayout.Space(6);
    }

    void DrawPlazaSection()
    {
        EditorGUILayout.LabelField("Plaza", EditorStyles.boldLabel);

        plazaRadius = EditorGUILayout.Slider(
            new GUIContent("Plaza Radius", "Radius where center houses and decorations spawn."),
            plazaRadius,
            4f, 20f
        );

        plazaJitterPercent = EditorGUILayout.Slider(
            new GUIContent("Center Jitter", "How far from geometric center the plaza can move."),
            plazaJitterPercent,
            0f, 0.25f
        );

        EditorGUILayout.Space(6);
    }

    void DrawRoadSection()
    {
        EditorGUILayout.LabelField("Roads (Terrain Layer)", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox("Roads are painted on the active Terrain using layer index 3.", MessageType.Info);

        roadLayerIndex = EditorGUILayout.IntField(
            new GUIContent("Road Layer Index", "Terrain layer index used for roads."),
            roadLayerIndex
        );

        baseRoadWidth = EditorGUILayout.Slider(
            new GUIContent("Base Road Width (m)", "Width near the plaza in meters."),
            baseRoadWidth,
            2f, 10f
        );

        roadWidthNarrowFactor = EditorGUILayout.Slider(
            new GUIContent("Edge Narrow Factor", "Multiplier at the road end, relative to the base width."),
            roadWidthNarrowFactor,
            0.4f, 1f
        );

        // Min / max road count as ints
        minRoadCount = EditorGUILayout.IntSlider(
            new GUIContent("Min Roads Per Area"),
            minRoadCount,
            1, 4
        );

        maxRoadCount = EditorGUILayout.IntSlider(
            new GUIContent("Max Roads Per Area"),
            maxRoadCount,
            1, 4
        );

        if (maxRoadCount < minRoadCount)
            maxRoadCount = minRoadCount;

        roadSampleSpacing = EditorGUILayout.Slider(
            new GUIContent("Road Sample Spacing", "Distance between samples when painting the road."),
            roadSampleSpacing,
            1f, 5f
        );

        EditorGUILayout.Space(6);
    }

    void DrawHousesSection()
    {
        EditorGUILayout.LabelField("Houses", EditorStyles.boldLabel);

        EditorGUILayout.MinMaxSlider(
            new GUIContent("Along Road Spacing (m)", "Random spacing for houses along roads."),
            ref minHouseSpacing,
            ref maxHouseSpacing,
            3f, 20f
        );

        roadsideOffsetMin = EditorGUILayout.Slider(
            new GUIContent("Roadside Offset Min (m)"),
            roadsideOffsetMin,
            1f, 10f
        );

        roadsideOffsetMax = EditorGUILayout.Slider(
            new GUIContent("Roadside Offset Max (m)"),
            roadsideOffsetMax,
            roadsideOffsetMin, 15f
        );

        maxHousesPerArea = EditorGUILayout.IntSlider(
            new GUIContent("Max Houses Per Area"),
            maxHousesPerArea,
            4, 100
        );

        EditorGUILayout.Space(6);
    }

    void DrawPropsSection()
    {
        EditorGUILayout.LabelField("Props", EditorStyles.boldLabel);

        enableProps = EditorGUILayout.Toggle(
            new GUIContent("Enable Props", "If disabled, only buildings are spawned."),
            enableProps
        );

        using (new EditorGUI.DisabledScope(!enableProps))
        {
            propDensityPlaza = EditorGUILayout.Slider(
                new GUIContent("Plaza Decoration Density"),
                propDensityPlaza,
                0f, 1f
            );

            propDensityRoadside = EditorGUILayout.Slider(
                new GUIContent("Roadside Decoration Density"),
                propDensityRoadside,
                0f, 1f
            );
        }

        EditorGUILayout.Space(6);
    }

    void DrawVolumesSection()
    {
        EditorGUILayout.LabelField("Village Areas", EditorStyles.boldLabel);

        var areas = FindObjectsByType<VillageAreaVolume>(FindObjectsSortMode.None);
        EditorGUILayout.LabelField($"Found {areas.Length} VillageAreaVolume in scene.");

        if (areas.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "Create one or more VillageAreaVolume objects. " +
                "The generator fills those areas with a medieval style village.",
                MessageType.Info
            );
        }

        if (GUILayout.Button("Create Area Volume"))
        {
            CreateAreaVolume();
        }

        clearBeforeGenerate = EditorGUILayout.Toggle(
            new GUIContent("Clear Before Generate", "Restore grass and delete old villages before generating."),
            clearBeforeGenerate
        );

        EditorGUILayout.Space(6);
    }

    void DrawButtonsSection()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.BeginVertical("box");

        if (GUILayout.Button("Generate Villages", GUILayout.Height(26)))
        {
            GenerateVillages();
        }

        if (GUILayout.Button("Clear Generated Villages", GUILayout.Height(22)))
        {
            ClearAllGenerated();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(6);
    }
    
    

    #endregion

    #region Area Helpers

    void CreateAreaVolume()
    {
        GameObject go = new GameObject("VillageArea");
        var vol = go.AddComponent<VillageAreaVolume>();

        vol.col.size = new Vector3(40f, 20f, 40f);

        SceneView view = SceneView.lastActiveSceneView;
        if (view != null && view.camera != null)
        {
            Ray ray = new Ray(view.camera.transform.position, view.camera.transform.forward);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 500f))
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
            go.transform.position = Vector3.zero;
        }

        Selection.activeGameObject = go;
    }

    #endregion

    #region Generation Entry

    void GenerateVillages()
    {
        if (!rules)
        {
            Debug.LogError("VillageGeneratorWindow: No VillageRuleSet assigned.");
            return;
        }

        var areas = FindObjectsByType<VillageAreaVolume>(FindObjectsSortMode.None);
        if (areas == null || areas.Length == 0)
        {
            Debug.LogWarning("VillageGeneratorWindow: No VillageAreaVolume in scene.");
            return;
        }

        Terrain terrain = Terrain.activeTerrain;
        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogError("VillageGeneratorWindow: No active Terrain found. Terrain roads and grass will not work.");
            return;
        }

        TerrainData td = terrain.terrainData;

        if (roadLayerIndex < 0 || roadLayerIndex >= td.alphamapLayers)
        {
            Debug.LogError($"VillageGeneratorWindow: Road layer index {roadLayerIndex} is out of range for this terrain.");
            return;
        }

        Transform root = GetOrCreateRoot();

        if (clearBeforeGenerate)
        {
            foreach (var area in areas)
                if (area != null)
                    area.RestoreGrass(terrain);

            ClearChildren(root);
        }

        int effectiveSeed = useRandomSeed ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : seed;
        System.Random rand = new System.Random(effectiveSeed);

        Transform villageGroup = new GameObject($"Village_{effectiveSeed}").transform;
        villageGroup.SetParent(root, false);

        foreach (var area in areas)
            if (area != null)
                area.BackupAndClearGrass(terrain);

        int aw = td.alphamapWidth;
        int ah = td.alphamapHeight;
        float[,,] alphamaps = td.GetAlphamaps(0, 0, aw, ah);

        List<Vector3> globalPlacedHouses = new List<Vector3>();

        foreach (var area in areas)
        {
            if (!area) continue;

            GenerateVillageInArea(area, rand, villageGroup, terrain, td, alphamaps, globalPlacedHouses);
        }

        td.SetAlphamaps(0, 0, alphamaps);

        Debug.Log($"Village generation complete with seed {effectiveSeed} across {areas.Length} areas.");
        
        // restore temporary grass removal after generation
        foreach (var area in areas)
            if (area != null)
                area.RestoreGrass(terrain);
    }

    #endregion

    #region Village Generation

    void GenerateVillageInArea(
        VillageAreaVolume area,
        System.Random rand,
        Transform parent,
        Terrain terrain,
        TerrainData td,
        float[,,] alphamaps,
        List<Vector3> globalPlacedHouses)
    {
        Bounds b = area.GetWorldBounds();

        Vector3 plaza = GetPlazaPosition(b, rand, terrain);

        Transform areaRoot = new GameObject(area.name + "_Village").transform;
        areaRoot.SetParent(parent, true);

        List<Vector3> placedHouses = new List<Vector3>();

        GeneratePlazaCluster(plaza, rand, areaRoot, placedHouses, globalPlacedHouses, terrain);
        List<List<Vector3>> roadSamples = GenerateCurvedRoads(plaza, b, rand, td, alphamaps, area);

        foreach (var road in roadSamples)
        {
            GenerateRoadHouses(road, plaza, rand, areaRoot, placedHouses, globalPlacedHouses, terrain);
            if (enableProps)
                GenerateRoadProps(road, rand, areaRoot, terrain);
        }

        if (enableProps)
            GeneratePlazaProps(plaza, rand, areaRoot, terrain);
    }

    Vector3 GetPlazaPosition(Bounds b, System.Random rand, Terrain terrain)
    {
        Vector3 center = b.center;

        float jitterX = (float)(rand.NextDouble() * 2.0 - 1.0) * plazaJitterPercent * b.extents.x;
        float jitterZ = (float)(rand.NextDouble() * 2.0 - 1.0) * plazaJitterPercent * b.extents.z;

        Vector3 pos = new Vector3(center.x + jitterX, center.y, center.z + jitterZ);

        TryGetGroundPosition(pos, out pos);

        return pos;
    }

    void GeneratePlazaCluster(
        Vector3 plaza,
        System.Random rand,
        Transform parent,
        List<Vector3> localPlaced,
        List<Vector3> globalPlaced,
        Terrain terrain)
    {
        int houseCount = rand.Next(2, 5);

        for (int i = 0; i < houseCount; i++)
        {
            float ang = (float)(rand.NextDouble() * Math.PI * 2.0);
            float r = UnityEngine.Random.Range(plazaRadius * 0.4f, plazaRadius);

            Vector3 offset = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;
            Vector3 candidate = plaza + offset;

            if (!TryGetGroundPosition(candidate, out candidate))
                continue;

            if (!HasSpacing(candidate, localPlaced, minHouseSpacing * 0.8f))
                continue;
            if (!HasSpacing(candidate, globalPlaced, minHouseSpacing * 0.8f))
                continue;

            GameObject prefab = rules.GetRandomCenterBuilding(rand);
            if (!prefab) continue;

            GameObject inst = InstantiatePrefabEditorSafe(prefab, candidate, parent);

            float rotY = (float)(rand.NextDouble() * 360.0);
            inst.transform.rotation = Quaternion.Euler(0f, rotY, 0f);

            localPlaced.Add(candidate);
            globalPlaced.Add(candidate);
        }
    }

    List<List<Vector3>> GenerateCurvedRoads(
    Vector3 plaza,
    Bounds b,
    System.Random rand,
    TerrainData td,
    float[,,] alphamaps,
    VillageAreaVolume area)
    {
        List<List<Vector3>> roads = new List<List<Vector3>>();

        int roadCount = rand.Next(minRoadCount, maxRoadCount + 1);

        for (int i = 0; i < roadCount; i++)
        {
            var mainRoad = CreateRoad(plaza, b, rand, td, alphamaps, area);
            roads.Add(mainRoad);

            int branches = 0;

            for (int k = 2; k < mainRoad.Count - 2; k += 3)
            {
                if (branches >= maxBranchesPerRoad)
                    break;

                if (rand.NextDouble() > branchChance)
                    continue;

                if (!TryGetGroundPosition(mainRoad[k], out var origin))
                    continue;

                var branchTarget = GetRandomEdgePoint(b, rand);

                Vector3 mid = Vector3.Lerp(origin, branchTarget, branchLengthPercent);

                TryGetGroundPosition(mid, out mid);
                TryGetGroundPosition(branchTarget, out branchTarget);

                List<Vector3> branchCP = new List<Vector3>();
                branchCP.Add(origin);   // start from main road
                branchCP.Add(mid);
                branchCP.Add(branchTarget);

                var branchRoad = SamplePath(branchCP, roadSampleSpacing);
                PaintRoadOnTerrain(branchRoad, td, alphamaps, b, origin, area);

                roads.Add(branchRoad);
                branches++;
            }
        }

        return roads;
    }

    List<Vector3> CreateRoad(
        Vector3 plaza,
        Bounds b,
        System.Random rand,
        TerrainData td,
        float[,,] alphamaps,
        VillageAreaVolume area)
    {
        Vector3 exit = GetRandomEdgePoint(b, rand);

        List<Vector3> control = new List<Vector3>();
        control.Add(plaza);

        int midCount = rand.Next(2, 4);

        for (int m = 0; m < midCount; m++)
        {
            float t = (m + 1f) / (midCount + 1f);
            Vector3 between = Vector3.Lerp(plaza, exit, t);

            float offset = (float)(rand.NextDouble() * 2.0 - 1.0);
            Vector3 dir = (exit - plaza).normalized;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x);

            between += perp * offset * b.extents.magnitude * 0.25f;

            TryGetGroundPosition(between, out between);
            control.Add(between);
        }

        control.Add(exit);

        var samples = SamplePath(control, roadSampleSpacing);
        PaintRoadOnTerrain(samples, td, alphamaps, b, plaza, area);

        return samples;
    }

    Vector3 GetRandomEdgePoint(Bounds b, System.Random rand)
    {
        int side = rand.Next(0, 4);
        float t = (float)rand.NextDouble();

        switch (side)
        {
            case 0:
                return new Vector3(Mathf.Lerp(b.min.x, b.max.x, t), b.center.y, b.min.z);
            case 1:
                return new Vector3(Mathf.Lerp(b.min.x, b.max.x, t), b.center.y, b.max.z);
            case 2:
                return new Vector3(b.min.x, b.center.y, Mathf.Lerp(b.min.z, b.max.z, t));
            default:
                return new Vector3(b.max.x, b.center.y, Mathf.Lerp(b.min.z, b.max.z, t));
        }
    }

    List<Vector3> SamplePath(List<Vector3> controlPoints, float spacing)
    {
        List<Vector3> samples = new List<Vector3>();
        if (controlPoints.Count < 2) return samples;

        float totalDist = 0f;
        for (int i = 0; i < controlPoints.Count - 1; i++)
            totalDist += Vector3.Distance(controlPoints[i], controlPoints[i + 1]);

        int steps = Mathf.Max(2, Mathf.RoundToInt(totalDist / spacing));

        for (int s = 0; s <= steps; s++)
        {
            float t = steps == 0 ? 0f : s / (float)steps;

            float segT = t * (controlPoints.Count - 1);
            int idx = Mathf.Clamp(Mathf.FloorToInt(segT), 0, controlPoints.Count - 2);
            float lerpT = segT - idx;

            Vector3 p0 = controlPoints[idx];
            Vector3 p1 = controlPoints[idx + 1];

            Vector3 p = Vector3.Lerp(p0, p1, lerpT);
            TryGetGroundPosition(p, out p);

            samples.Add(p);
        }

        return samples;
    }

    void PaintRoadOnTerrain(
        List<Vector3> samples,
        TerrainData td,
        float[,,] alphamaps,
        Bounds areaBounds,
        Vector3 plaza,
        VillageAreaVolume area)
    {
        if (samples.Count == 0)
            return;

        int aw = td.alphamapWidth;
        int ah = td.alphamapHeight;
        int layers = td.alphamapLayers;

        Vector3 tPos = td.bounds.min;
        Vector3 tSize = td.size;

        float maxDist = 0f;
        foreach (var p in samples)
        {
            float d = Vector3.Distance(plaza, p);
            if (d > maxDist) maxDist = d;
        }
        maxDist = Mathf.Max(maxDist, 0.01f);

        for (int i = 0; i < samples.Count; i++)
        {
            Vector3 p = samples[i];

            if (p.x < areaBounds.min.x || p.x > areaBounds.max.x ||
                p.z < areaBounds.min.z || p.z > areaBounds.max.z)
                continue;

            float dist = Vector3.Distance(plaza, p);
            float t = Mathf.Clamp01(dist / maxDist);

            float width = Mathf.Lerp(baseRoadWidth, baseRoadWidth * roadWidthNarrowFactor, t);

            float nx = Mathf.InverseLerp(tPos.x, tPos.x + tSize.x, p.x);
            float nz = Mathf.InverseLerp(tPos.z, tPos.z + tSize.z, p.z);

            int cx = Mathf.RoundToInt(nx * (aw - 1));
            int cz = Mathf.RoundToInt(nz * (ah - 1));

            float radiusCells = width / tSize.x * aw * 0.5f;
            int r = Mathf.CeilToInt(radiusCells);

            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= ah) continue;

                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= aw) continue;

                    float g = (dx * dx + dz * dz) / (radiusCells * radiusCells);
                    if (g > 1f) continue;

                    float strength = 1f - g;

                    for (int l = 0; l < layers; l++)
                        alphamaps[z, x, l] *= (1f - 0.9f * strength);

                    alphamaps[z, x, roadLayerIndex] += 0.9f * strength;
                }
            }

            area.SuppressGrassCircle(samples[i], width * 0.5f, Terrain.activeTerrain);
        }
    }

    void GenerateRoadHouses(
        List<Vector3> samples,
        Vector3 plaza,
        System.Random rand,
        Transform parent,
        List<Vector3> localPlaced,
        List<Vector3> globalPlaced,
        Terrain terrain)
    {
        if (samples.Count < 3) return;

        float traveled = 0f;
        float currentTargetSpacing = UnityEngine.Random.Range(minHouseSpacing, maxHouseSpacing);

        for (int i = 1; i < samples.Count - 1; i++)
        {
            Vector3 prev = samples[i - 1];
            Vector3 curr = samples[i];

            traveled += Vector3.Distance(prev, curr);

            if (traveled < currentTargetSpacing)
                continue;

            traveled = 0f;
            currentTargetSpacing = UnityEngine.Random.Range(minHouseSpacing, maxHouseSpacing);

            if (rand.NextDouble() < 0.3)
                continue;

            Vector3 next = samples[i + 1];

            Vector3 tangent = (next - prev).normalized;
            Vector3 perp = new Vector3(-tangent.z, 0f, tangent.x);

            int side = rand.Next(0, 2) == 0 ? -1 : 1;
            float offsetDist = UnityEngine.Random.Range(roadsideOffsetMin, roadsideOffsetMax);

            Vector3 candidate = curr + perp * side * offsetDist;

            if (!TryGetGroundPosition(candidate, out candidate))
                continue;

            if (!HasSpacing(candidate, localPlaced, minHouseSpacing))
                continue;
            if (!HasSpacing(candidate, globalPlaced, minHouseSpacing))
                continue;

            GameObject prefab = rules.GetRandomRoadsideHouse(rand);
            if (!prefab) continue;

            GameObject inst = InstantiatePrefabEditorSafe(prefab, candidate, parent);

            float yawRand = (float)(rand.NextDouble() * 40.0 - 20.0);
            Vector3 lookDir = perp * side * -1f;

            inst.transform.rotation =
                Quaternion.LookRotation(lookDir, Vector3.up) *
                Quaternion.Euler(0f, yawRand, 0f);

            localPlaced.Add(candidate);
            globalPlaced.Add(candidate);
        }
    }

    void GenerateRoadProps(
        List<Vector3> samples,
        System.Random rand,
        Transform parent,
        Terrain terrain)
    {
        if (rules == null) return;
        if (propDensityRoadside <= 0f) return;

        for (int i = 1; i < samples.Count - 1; i += 2)
        {
            if (rand.NextDouble() > propDensityRoadside)
                continue;

            Vector3 prev = samples[i - 1];
            Vector3 curr = samples[i];
            Vector3 next = samples[i + 1];

            Vector3 tangent = (next - prev).normalized;
            Vector3 perp = new Vector3(-tangent.z, 0f, tangent.x);

            int side = rand.Next(0, 2) == 0 ? -1 : 1;
            float offsetDist = UnityEngine.Random.Range(roadsideOffsetMin * 0.5f, roadsideOffsetMax * 0.8f);

            Vector3 candidate = curr + perp * side * offsetDist;

            if (!TryGetGroundPosition(candidate, out candidate))
                continue;

            GameObject prefab = rules.GetRandomDecorationRoadside(rand);
            if (!prefab) continue;

            GameObject inst = InstantiatePrefabEditorSafe(prefab, candidate, parent);

            float yaw = (float)(rand.NextDouble() * 360.0);
            inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    void GeneratePlazaProps(
        Vector3 plaza,
        System.Random rand,
        Transform parent,
        Terrain terrain)
    {
        if (!rules || !enableProps) return;

        int attempts = Mathf.RoundToInt(6 * propDensityPlaza);

        for (int i = 0; i < attempts; i++)
        {
            if (rand.NextDouble() > propDensityPlaza)
                continue;

            float ang = (float)(rand.NextDouble() * Math.PI * 2.0);
            float r = UnityEngine.Random.Range(plazaRadius * 0.3f, plazaRadius * 1.1f);

            Vector3 p = plaza + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * r;

            if (!TryGetGroundPosition(p, out p))
                continue;

            GameObject prefab = rules.GetRandomDecorationCenter(rand);
            if (!prefab) continue;

            GameObject inst = InstantiatePrefabEditorSafe(prefab, p, parent);
            float yaw = (float)(rand.NextDouble() * 360.0);
            inst.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    #endregion

    #region Helpers

    Transform GetOrCreateRoot()
    {
        GameObject rootGO = GameObject.Find(RootName);
        if (!rootGO)
            rootGO = new GameObject(RootName);

        return rootGO.transform;
    }

    void ClearAllGenerated()
    {
        var areas = FindObjectsByType<VillageAreaVolume>(FindObjectsSortMode.None);
        Terrain terrain = Terrain.activeTerrain;

        if (areas != null && terrain != null)
        {
            foreach (var area in areas)
                if (area != null)
                    area.RestoreGrass(terrain);
        }

        GameObject rootGO = GameObject.Find(RootName);
        if (!rootGO) return;

#if UNITY_EDITOR
        DestroyImmediate(rootGO);
#else
        Destroy(rootGO);
#endif
    }

    void ClearChildren(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
        {
#if UNITY_EDITOR
            DestroyImmediate(root.GetChild(i).gameObject);
#else
            Destroy(root.GetChild(i).gameObject);
#endif
        }
    }

    bool TryGetGroundPosition(Vector3 horizontalPos, out Vector3 hitPos)
    {
        float rayStartY = horizontalPos.y + 200f;
        Vector3 origin = new Vector3(horizontalPos.x, rayStartY, horizontalPos.z);
        Ray ray = new Ray(origin, Vector3.down);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, 500f, ~0, QueryTriggerInteraction.Ignore))
        {
            hitPos = hit.point;
            return true;
        }

        if (Terrain.activeTerrain != null)
        {
            float h = Terrain.activeTerrain.SampleHeight(horizontalPos) +
                      Terrain.activeTerrain.transform.position.y;
            hitPos = new Vector3(horizontalPos.x, h, horizontalPos.z);
            return true;
        }

        hitPos = horizontalPos;
        return false;
    }

    bool HasSpacing(Vector3 candidate, List<Vector3> existing, float minDist)
    {
        float minSqr = minDist * minDist;

        for (int i = 0; i < existing.Count; i++)
        {
            float dSqr = (existing[i] - candidate).sqrMagnitude;
            if (dSqr < minSqr)
                return false;
        }

        return true;
    }
    
    void RecalculateGrass()
    {
        var areas = FindObjectsByType<VillageAreaVolume>(FindObjectsSortMode.None);
        Terrain terrain = Terrain.activeTerrain;

        foreach (var area in areas)
        {
            // restore full grass
            area.RestoreGrass(terrain);
        }

        Debug.Log("Grass recalculated based on current objects.");
    }


    GameObject InstantiatePrefabEditorSafe(GameObject prefab, Vector3 position, Transform parent)
    {
        GameObject instance;

#if UNITY_EDITOR
        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
        if (source != null)
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
            instance.transform.position = position;
            instance.transform.SetParent(parent, true);
        }
        else
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = position;
            instance.transform.SetParent(parent, true);
        }
#else
        instance = GameObject.Instantiate(prefab, position, Quaternion.identity, parent);
#endif

        return instance;
    }

    #endregion
}