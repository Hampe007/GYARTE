// Assets/Scripts/Tiles/Editor/TileSceneGenerator.cs
// Unity 2021+ / Unity 6000 compatible
// Multi-terrain slice + non-destructive re-slice, with safe snapshots.
// BACK UP / commit before large operations.

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class TileSceneGenerator : EditorWindow
{
    // ===== UI / runtime guard =====
    private bool _isRunning = false;

    // ===== Source (multi-terrain) =====
    [Header("Source Terrains")]
    [SerializeField] private bool autoCollectTerrains = true;
    [SerializeField] private string terrainNamePrefix = "Terrain_";
    [SerializeField] private Terrain[] sourceTerrains; // used when autoCollectTerrains = false

    // cached for current terrain
    private TerrainData _srcTD;
    private string _currentTerrainLabel = "";

    // ===== Grid (meters) =====
    [Header("Grid (auto-calculated from meters)")]
    [SerializeField] private float tileSizeMeters = 250f;
    [HideInInspector] private int tilesX;
    [HideInInspector] private int tilesY;

    // ===== Output =====
    [Header("Output")]
    [SerializeField] private string sceneNamePattern = "{t}_Tile_{x}_{y}";
    [SerializeField] private string outputFolder = "Assets/Scenes/Tiles";
    [SerializeField] private string terrainDataPrefix = "TD_"; // saved as TD_<t>_<x>_<y>.asset
    [SerializeField] private bool subfolderPerTerrain = true;

    private string _outputScenesFolder;
    private string _outputDataFolder;

    // ===== Copy Channels =====
    [Header("Copy Channels")]
    [SerializeField] private bool copyHeights = true;
    [SerializeField] private bool copyAlphamaps = false;
    [SerializeField] private bool copyDetails = false;
    [SerializeField] private bool copyTrees = false;

    // ===== Reslice Options =====
    [Header("Reslice Options")]
    [SerializeField] private bool nonDestructiveReslice = true;    // update TerrainData in existing scenes, keep other objects
    [SerializeField] private bool onlyUpdateIfChanged = false;     // small speed-up by skipping identical tiles (height-only compare)
    [SerializeField] private bool addToBuildSettings = true;

    // ===== Snapshot type (do NOT hold Terrain refs while running) =====
    private sealed class TerrainSnapshot
    {
        public string label;      // sanitized terrain name
        public TerrainData data;  // stable asset ref
        public Vector3 origin;    // cached world position
    }

    [MenuItem("Tools/Tiles/Tile Scene Generator & Reslicer")]
    private static void Open() => GetWindow<TileSceneGenerator>("Tile Scene Generator");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Slice terrains → tile scenes, and safely re-slice later.", EditorStyles.boldLabel);
        EditorGUILayout.Space(6);

        if (_isRunning)
        {
            EditorGUILayout.HelpBox("Slicing in progress… preview and controls are disabled to avoid touching live scene objects.", MessageType.Info);
            GUI.enabled = false;
        }

        // Source selector
        autoCollectTerrains = EditorGUILayout.ToggleLeft("Auto-collect terrains (by prefix)", autoCollectTerrains);
        using (new EditorGUI.IndentLevelScope())
        {
            terrainNamePrefix = EditorGUILayout.TextField("Name prefix", terrainNamePrefix);
        }

        if (!autoCollectTerrains)
        {
            SerializedObject so = new SerializedObject(this);
            SerializedProperty arr = so.FindProperty("sourceTerrains");
            EditorGUILayout.PropertyField(arr, new GUIContent("Source Terrains (manual)"), true);
            so.ApplyModifiedProperties();
        }
        else
        {
            if (GUILayout.Button("Auto-fill now (Terrain.activeTerrains)"))
            {
                var previewList = CollectSnapshots(onlySnapshotList: true);
                Debug.Log($"[TileSceneGenerator] Found {previewList.Count} terrains: {string.Join(", ", previewList.Select(s => s.label))}");
            }
        }

        EditorGUILayout.Space(6);
        tileSizeMeters = EditorGUILayout.FloatField("Tile Size (meters)", tileSizeMeters);

        // Preview (best-effort, does not touch live Terrains when running)
        if (!_isRunning)
        {
            var anyTD = TryGetAnyTerrainDataForPreview();
            if (anyTD != null)
            {
                var sz = anyTD.size;
                int previewX = Mathf.CeilToInt(sz.x / tileSizeMeters);
                int previewY = Mathf.CeilToInt(sz.z / tileSizeMeters);
                EditorGUILayout.HelpBox($"Preview: {sz.x:0}×{sz.z:0} m → {previewX}×{previewY} tiles (~{tileSizeMeters:0} m each)", MessageType.Info);
            }
        }

        sceneNamePattern = EditorGUILayout.TextField("Scene Name Pattern", sceneNamePattern);
        outputFolder = EditorGUILayout.TextField("Output Root Folder", outputFolder);
        terrainDataPrefix = EditorGUILayout.TextField("TerrainData Prefix", terrainDataPrefix);
        subfolderPerTerrain = EditorGUILayout.Toggle("Subfolder Per Terrain", subfolderPerTerrain);

        EditorGUILayout.Space(6);
        copyHeights    = EditorGUILayout.Toggle("Copy Heights", copyHeights);
        copyAlphamaps  = EditorGUILayout.Toggle("Copy Alphamaps (Textures)", copyAlphamaps);
        copyDetails    = EditorGUILayout.Toggle("Copy Details (Grass)", copyDetails);
        copyTrees      = EditorGUILayout.Toggle("Copy Trees", copyTrees);

        EditorGUILayout.Space(6);
        nonDestructiveReslice = EditorGUILayout.Toggle("Non-Destructive Re-slice (preserve props)", nonDestructiveReslice);
        onlyUpdateIfChanged   = EditorGUILayout.Toggle(new GUIContent("Only Update If Changed (height check)", "Skips writing a tile if heights are identical to current data."), onlyUpdateIfChanged);
        addToBuildSettings    = EditorGUILayout.Toggle("Ensure In Build Settings", addToBuildSettings);

        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(!CanRun()))
        {
            if (GUILayout.Button("Run Slice / Re-slice"))
            {
                try
                {
                    RunForAllTerrains();
                    EditorUtility.DisplayDialog("Tile Scene Generator", "All terrains processed successfully.", "Great");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[TileSceneGenerator] Failed: {ex}");
                    EditorUtility.DisplayDialog("Tile Scene Generator", $"Failed:\n{ex.Message}", "OK");
                }
            }
        }

        GUI.enabled = true;

        EditorGUILayout.HelpBox(
            "Workflow:\n" +
            "1) Keep a MASTER scene with the full terrain(s) for editing.\n" +
            "2) Generate tiles once.\n" +
            "3) When you change a terrain, re-run — tile scenes update in-place (keeps props).\n\n" +
            "Tips:\n" +
            "• Commit or back up before large re-slices.\n" +
            "• After reslicing, re-bake NavMesh/Lighting per tile.\n" +
            "• Generated scenes can be auto-added to Build Settings.",
            MessageType.Info);
    }

    private bool CanRun()
    {
        if (autoCollectTerrains) return (Terrain.activeTerrains?.Length ?? 0) > 0;
        return sourceTerrains != null && sourceTerrains.Any(t => t != null);
    }

    // --------- MAIN ORCHESTRATOR (safe snapshots) ---------
    private void RunForAllTerrains()
    {
        _isRunning = true;
        try
        {
            var snapshots = CollectSnapshots();
            if (snapshots.Count == 0)
                throw new InvalidOperationException("No valid terrains found. Check auto-collect prefix or assign terrains manually.");

            Debug.Log($"[TileSceneGenerator] Will process {snapshots.Count} terrain(s): {string.Join(", ", snapshots.Select(s => s.label))}");

            string originalOutputRoot = outputFolder;

            foreach (var snap in snapshots)
            {
                _currentTerrainLabel = snap.label;
                _srcTD               = snap.data;     // cached TerrainData asset
                Vector3 cachedOrigin = snap.origin;   // cached world position

                // Per-terrain root: Assets/Scenes/Tiles/<TerrainName>
                outputFolder = subfolderPerTerrain ? $"{originalOutputRoot}/{_currentTerrainLabel}" : originalOutputRoot;

                ComputeGridFromMeters();   // uses _srcTD only
                ValidateInputs();          // sets _outputScenesFolder/_outputDataFolder
                RunSliceOrReslice(cachedOrigin);

                outputFolder = originalOutputRoot;
            }
        }
        finally
        {
            _isRunning = false;
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }

    // Build a list of stable snapshots (no Terrain refs kept).
    private List<TerrainSnapshot> CollectSnapshots(bool onlySnapshotList = false)
    {
        Terrain[] terrains = autoCollectTerrains
            ? (string.IsNullOrEmpty(terrainNamePrefix)
                ? Terrain.activeTerrains
                : Terrain.activeTerrains.Where(t => t && t.name.StartsWith(terrainNamePrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            : (sourceTerrains ?? Array.Empty<Terrain>());

        var list = new List<TerrainSnapshot>(terrains.Length);
        foreach (var t in terrains)
        {
            if (!t || !t.terrainData) continue;
            string safe = Regex.Replace(t.name, @"[^A-Za-z0-9_\-]", "_");
            list.Add(new TerrainSnapshot
            {
                label  = safe,
                data   = t.terrainData,        // asset (won’t “destroy” mid-run)
                origin = t.transform.position  // value type snapshot
            });

            // If just previewing, we can stop early to avoid heavy allocations.
            if (onlySnapshotList && list.Count > 32) break;
        }
        return list;
    }

    // Try to get any TerrainData for preview (no Terrain refs while running)
    private TerrainData TryGetAnyTerrainDataForPreview()
    {
        if (autoCollectTerrains)
        {
            var t = Terrain.activeTerrains?.FirstOrDefault();
            return t ? t.terrainData : null;
        }
        else
        {
            var t = sourceTerrains?.FirstOrDefault(tt => tt != null);
            return t ? t.terrainData : null;
        }
    }

    // --- Compute tilesX/tilesY from meters for the CURRENT snapshot ---
    private void ComputeGridFromMeters()
    {
        if (_srcTD == null)
            throw new InvalidOperationException("Current Terrain has no TerrainData.");

        if (tileSizeMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSizeMeters), "Tile Size (meters) must be > 0.");

        var sz = _srcTD.size;
        tilesX = Mathf.Max(1, Mathf.CeilToInt(sz.x / tileSizeMeters));
        tilesY = Mathf.Max(1, Mathf.CeilToInt(sz.z / tileSizeMeters));
        Debug.Log($"[TileSceneGenerator] {_currentTerrainLabel}: grid {tilesX}×{tilesY} at ≈{tileSizeMeters:0} m.");
    }

    private void ValidateInputs()
    {
        if (_srcTD == null) throw new InvalidOperationException("Current Terrain has no TerrainData.");
        if (tilesX < 1 || tilesY < 1) throw new ArgumentOutOfRangeException(nameof(tilesX), "tilesX/tilesY must be ≥ 1.");
        if (string.IsNullOrWhiteSpace(sceneNamePattern) || !sceneNamePattern.Contains("{x}") || !sceneNamePattern.Contains("{y}"))
            throw new InvalidOperationException("Scene Name Pattern must include {x} and {y}. You can also use {t} for terrain name.");
        if (!outputFolder.StartsWith("Assets"))
            throw new InvalidOperationException("Output folder must be under Assets/.");

        // Create root + two subfolders
        EnsureFolder(outputFolder);

        string scenesFolder = Path.Combine(outputFolder, "Scenes").Replace("\\", "/");
        string dataFolder   = Path.Combine(outputFolder, "TerrainData").Replace("\\", "/");
        EnsureFolder(scenesFolder);
        EnsureFolder(dataFolder);

        _outputScenesFolder = scenesFolder;
        _outputDataFolder   = dataFolder;
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        var leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    // -------- core slicing (uses only cached data) --------
    private void RunSliceOrReslice(Vector3 cachedOrigin)
    {
        var srcSize = _srcTD.size; // world size in meters
        var tileSize = new Vector3(srcSize.x / tilesX, srcSize.y, srcSize.z / tilesY);

        // Source resolutions
        int hRes = _srcTD.heightmapResolution;
        int aRes = _srcTD.alphamapResolution;
        int dRes = _srcTD.detailResolution;

        // Steps per tile
        int hStepX = (hRes - 1) / tilesX;
        int hStepY = (hRes - 1) / tilesY;
        int aStepX = aRes / tilesX;
        int aStepY = aRes / tilesY;
        int dStepX = dRes / tilesX;
        int dStepY = dRes / tilesY;

        var layers = _srcTD.terrainLayers;
        var detailPrototypes = _srcTD.detailPrototypes;
        var treePrototypes = _srcTD.treePrototypes;
        int detailLayerCount = detailPrototypes?.Length ?? 0;

        int total = tilesX * tilesY;
        int processed = 0;

        string originalScenePath = SceneManager.GetActiveScene().path;

        try
        {
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    float progress = processed / (float)total;
                    EditorUtility.DisplayProgressBar($"Tile Slice/Reslice [{_currentTerrainLabel}]", $"Processing tile {tx},{ty}", progress);

                    // Build new TerrainData from the source master
                    TerrainData newTD = BuildTileTerrainData(
                        tx, ty, hStepX, hStepY, aStepX, aStepY, dStepX, dStepY,
                        layers, detailLayerCount, treePrototypes
                    );

                    // --- Save/replace TerrainData under the DATA subfolder, include terrain name ---
                    string tdPath = $"{_outputDataFolder}/{terrainDataPrefix}{_currentTerrainLabel}_{tx}_{ty}.asset";
                    var existingTD = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);

                    if (onlyUpdateIfChanged && existingTD != null && copyHeights)
                    {
                        if (HeightsEqual(existingTD, newTD))
                        {
                            UnityEngine.Object.DestroyImmediate(newTD);
                            newTD = existingTD;
                        }
                    }

                    if (existingTD == null || newTD != existingTD)
                        SaveOrReplaceTerrainDataAsset(tdPath, newTD, existingTD);

                    // --- Scene name/path under the SCENES subfolder, supports {t} token ---
                    string tileSceneName = ReplaceTokens(sceneNamePattern, tx, ty);
                    string tileScenePath = $"{_outputScenesFolder}/{tileSceneName}.unity";
                    bool sceneExists = File.Exists(tileScenePath);

                    if (nonDestructiveReslice && sceneExists)
                    {
                        var opened = EditorSceneManager.OpenScene(tileScenePath, OpenSceneMode.Additive);
                        try
                        {
                            var terrainGO = FindOrCreateTerrainGO(tx, ty, newTD, tileSize, cachedOrigin, opened);
                            EditorSceneManager.MarkSceneDirty(opened);
                            EditorSceneManager.SaveScene(opened);
                        }
                        finally
                        {
                            EditorSceneManager.CloseScene(opened, true);
                        }
                    }
                    else
                    {
                        // Fresh create (Additive mode so master stays loaded)
                        var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                        newScene.name = tileSceneName;

                        var terrainGO = Terrain.CreateTerrainGameObject(newTD);
                        terrainGO.name = $"Terrain_{tx}_{ty}";
                        PositionTerrainGO(terrainGO.transform, tx, ty, tileSize, cachedOrigin);

                        var col = terrainGO.GetComponent<TerrainCollider>();
                        if (col != null) col.enabled = true;

                        EditorSceneManager.SaveScene(newScene, tileScenePath);
                        EditorSceneManager.CloseScene(newScene, true);
                    }

                    if (addToBuildSettings)
                        EnsureInBuildSettings(tileScenePath);

                    processed++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            if (!string.IsNullOrEmpty(originalScenePath) && File.Exists(originalScenePath))
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }
    }

    private TerrainData BuildTileTerrainData(
    int tx, int ty,
    int hStepX, int hStepY, int aStepX, int aStepY, int dStepX, int dStepY,
    TerrainLayer[] layers, int detailLayerCount, TreePrototype[] treePrototypes)
{
    var td = new TerrainData();

    // --- HEIGHTS ---
    int hW = hStepX + 1;
    int hH = hStepY + 1;
    int hResTile = Mathf.Max(hW, hH);                 // NEW: square res large enough for both
    td.heightmapResolution = hResTile;

    // --- ALPHAMAPS ---
    int aW = Mathf.Max(1, aStepX);
    int aH = Mathf.Max(1, aStepY);
    int aResTile = Mathf.Max(aW, aH);                 // NEW
    td.alphamapResolution = aResTile;

    // --- DETAILS ---
    int dW = Mathf.Max(1, dStepX);
    int dH = Mathf.Max(1, dStepY);
    int dResTile = Mathf.Max(dW, dH);                 // NEW
    td.SetDetailResolution(dResTile, _srcTD.detailResolutionPerPatch);

    // size/layers
    var srcSize = _srcTD.size;
    var tileSize = new Vector3(srcSize.x / tilesX, srcSize.y, srcSize.z / tilesY);
    td.size = tileSize;
    td.terrainLayers = layers;

    if (copyHeights)
    {
        int hX = tx * hStepX;
        int hY = ty * hStepY;
        var heights = _srcTD.GetHeights(hX, hY, hW, hH);   // shape: hW × hH
        td.SetHeights(0, 0, heights);                      // OK because hResTile >= hW,hH
    }

    if (copyAlphamaps && _srcTD.alphamapLayers > 0 && layers != null && layers.Length > 0)
    {
        int aRes = _srcTD.alphamapResolution;
        int aX = tx * aStepX;
        int aY = ty * aStepY;
        int w = Math.Min(aStepX, aRes - aX);
        int h = Math.Min(aStepY, aRes - aY);
        w = Mathf.Max(1, w);
        h = Mathf.Max(1, h);
        var splats = _srcTD.GetAlphamaps(aX, aY, w, h);    // shape: w × h × layers
        // Ensure destination is big enough (it is: aResTile = max(aStepX,aStepY))
        td.SetAlphamaps(0, 0, splats);
    }

    if (copyDetails && detailLayerCount > 0)
    {
        int dRes = _srcTD.detailResolution;
        for (int layer = 0; layer < detailLayerCount; layer++)
        {
            int dX = tx * dStepX;
            int dY = ty * dStepY;
            int w = Math.Min(dStepX, dRes - dX);
            int h = Math.Min(dStepY, dRes - dY);
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var detail = _srcTD.GetDetailLayer(dX, dY, w, h, layer);
            // Destination detail map is square with size dResTile >= w,h
            td.SetDetailLayer(0, 0, layer, detail);
        }
    }

    if (copyTrees && treePrototypes != null && treePrototypes.Length > 0)
    {
        td.treePrototypes = treePrototypes;
        var srcTrees = _srcTD.treeInstances;
        var tileTrees = new List<TreeInstance>(128);

        float x0 = tx / (float)tilesX, x1 = (tx + 1) / (float)tilesX;
        float y0 = ty / (float)tilesY, y1 = (ty + 1) / (float)tilesY;

        foreach (var t in srcTrees)
        {
            if (t.position.x >= x0 && t.position.x < x1 && t.position.z >= y0 && t.position.z < y1)
            {
                var nt = t;
                nt.position = new Vector3(
                    Mathf.InverseLerp(x0, x1, t.position.x),
                    t.position.y,
                    Mathf.InverseLerp(y0, y1, t.position.z)
                );
                tileTrees.Add(nt);
            }
        }
        td.treeInstances = tileTrees.ToArray();
    }

    return td;
}

    private static void SaveOrReplaceTerrainDataAsset(string tdPath, TerrainData newTD, TerrainData existingTD)
    {
        if (existingTD == null)
        {
            AssetDatabase.CreateAsset(newTD, tdPath);
        }
        else
        {
            AssetDatabase.DeleteAsset(tdPath);
            AssetDatabase.CreateAsset(newTD, tdPath);
        }
    }

    private static GameObject FindOrCreateTerrainGO(int tx, int ty, TerrainData td, Vector3 tileSize, Vector3 srcPos, Scene scene)
    {
        string expectedName = $"Terrain_{tx}_{ty}";
        GameObject terrainGO = scene.GetRootGameObjects().FirstOrDefault(go => go.name == expectedName);

        if (terrainGO == null)
        {
            var anyTerrain = scene.GetRootGameObjects()
                                  .SelectMany(go => go.GetComponentsInChildren<Terrain>(true))
                                  .FirstOrDefault();
            if (anyTerrain != null) terrainGO = anyTerrain.gameObject;
        }

        if (terrainGO == null)
        {
            terrainGO = Terrain.CreateTerrainGameObject(td);
            terrainGO.name = expectedName;
        }
        else
        {
            var terrain = terrainGO.GetComponent<Terrain>() ?? terrainGO.AddComponent<Terrain>();
            terrain.terrainData = td;

            var col = terrainGO.GetComponent<TerrainCollider>() ?? terrainGO.AddComponent<TerrainCollider>();
            col.terrainData = td;
            col.enabled = true;
        }

        PositionTerrainGO(terrainGO.transform, tx, ty, tileSize, srcPos);
        return terrainGO;
    }

    private static void PositionTerrainGO(Transform t, int tx, int ty, Vector3 tileSize, Vector3 srcPos)
    {
        t.position = new Vector3(
            srcPos.x + tx * tileSize.x,
            srcPos.y,
            srcPos.z + ty * tileSize.z
        );
    }

    private static void EnsureInBuildSettings(string scenePath)
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.Any(s => s.path == scenePath)) return;
        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    private static bool HeightsEqual(TerrainData a, TerrainData b)
    {
        try
        {
            if (a.heightmapResolution != b.heightmapResolution) return false;
            int w = a.heightmapResolution;
            int h = a.heightmapResolution;
            int step = Mathf.Max(1, w / 64);
            for (int y = 0; y < h; y += step)
            {
                for (int x = 0; x < w; x += step)
                {
                    float ha = a.GetHeight(y, x);
                    float hb = b.GetHeight(y, x);
                    if (!Mathf.Approximately(ha, hb))
                        return false;
                }
            }
            return true;
        }
        catch { return false; }
    }

    private string ReplaceTokens(string pattern, int tx, int ty)
    {
        return pattern
            .Replace("{x}", tx.ToString())
            .Replace("{y}", ty.ToString())
            .Replace("{t}", _currentTerrainLabel);
    }
}
#endif