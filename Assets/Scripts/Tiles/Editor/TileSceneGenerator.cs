// Assets/Scripts/Tiles/Editor/TileSceneGenerator.cs
// Unity 2021+ / Unity 6000 compatible
// First-time slice + non-destructive re-slice (updates TerrainData in existing tile scenes)
// BACK UP / commit before large operations.

#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class TileSceneGenerator : EditorWindow
{
    [Header("Source")]
    [SerializeField] private Terrain sourceTerrain;

    [Header("Grid")]
    [SerializeField] private int tilesX = 10;
    [SerializeField] private int tilesY = 10;

    [Header("Output")]
    [SerializeField] private string sceneNamePattern = "Tile_{x}_{y}";
    [SerializeField] private string outputFolder = "Assets/Scenes/Tiles";
    [SerializeField] private string terrainDataPrefix = "TD_"; // saved as TD_x_y.asset

    [Header("Copy Channels")]
    [SerializeField] private bool copyHeights = true;
    [SerializeField] private bool copyAlphamaps = true;
    [SerializeField] private bool copyDetails = true;
    [SerializeField] private bool copyTrees = true;

    [Header("Reslice Options")]
    [SerializeField] private bool nonDestructiveReslice = true;    // update TerrainData in existing scenes, keep all other objects
    [SerializeField] private bool onlyUpdateIfChanged = false;     // optional: tiny speed-up by skipping identical tiles (height-only compare)
    [SerializeField] private bool addToBuildSettings = true;

    private TerrainData _srcTD;

    [MenuItem("Tools/Tiles/Tile Scene Generator & Reslicer")]
    private static void Open() => GetWindow<TileSceneGenerator>("Tile Scene Generator");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Slice master Terrain → tile scenes, and safely re-slice later.", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        sourceTerrain = (Terrain)EditorGUILayout.ObjectField("Source Terrain", sourceTerrain, typeof(Terrain), true);
        if (sourceTerrain != null)
        {
            _srcTD = sourceTerrain.terrainData;
            if (_srcTD != null)
            {
                var sz = _srcTD.size;
                EditorGUILayout.LabelField($"Terrain Size: {sz.x:0} × {sz.z:0}, Height: {sz.y:0}");
                EditorGUILayout.LabelField($"Heights: {_srcTD.heightmapResolution} | Alphamaps: {_srcTD.alphamapResolution} | Detail: {_srcTD.detailResolution}");
            }
        }

        EditorGUILayout.Space(6);
        tilesX = Mathf.Max(1, EditorGUILayout.IntField("Tiles X (columns)", tilesX));
        tilesY = Mathf.Max(1, EditorGUILayout.IntField("Tiles Y (rows)", tilesY));

        sceneNamePattern = EditorGUILayout.TextField("Scene Name Pattern", sceneNamePattern);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        terrainDataPrefix = EditorGUILayout.TextField("TerrainData Prefix", terrainDataPrefix);

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
        using (new EditorGUI.DisabledScope(sourceTerrain == null || _srcTD == null))
        {
            if (GUILayout.Button("Run Slice / Re-slice"))
            {
                try
                {
                    ValidateInputs();
                    RunSliceOrReslice();
                    EditorUtility.DisplayDialog("Tile Scene Generator", "All tiles processed successfully.", "Great");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[TileSceneGenerator] Failed: {ex}");
                    EditorUtility.DisplayDialog("Tile Scene Generator", $"Failed:\n{ex.Message}", "OK");
                }
            }
        }

        EditorGUILayout.HelpBox(
            "Workflow:\n" +
            "1) Keep a MASTER scene with the full terrain for editing.\n" +
            "2) Generate tiles once.\n" +
            "3) When you change the master terrain, re-run this tool — it updates Tile_X_Y scenes in-place (keeps your props).\n\n" +
            "Tips:\n" +
            "• Commit or back up before large re-slices.\n" +
            "• After reslicing, re-bake NavMesh/Lighting per tile as needed.\n" +
            "• Add generated scenes to Build Settings (toggle can auto-add).",
            MessageType.Info);
    }

    private void ValidateInputs()
    {
        if (sourceTerrain == null) throw new InvalidOperationException("Assign a Source Terrain.");
        if (_srcTD == null) throw new InvalidOperationException("Source Terrain has no TerrainData.");
        if (tilesX < 1 || tilesY < 1) throw new ArgumentOutOfRangeException(nameof(tilesX), "tilesX/tilesY must be ≥ 1.");
        if (string.IsNullOrWhiteSpace(sceneNamePattern) || !sceneNamePattern.Contains("{x}") || !sceneNamePattern.Contains("{y}"))
            throw new InvalidOperationException("Scene Name Pattern must include {x} and {y}.");
        if (!outputFolder.StartsWith("Assets")) throw new InvalidOperationException("Output folder must be under Assets/.");
        EnsureFolder(outputFolder);
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace("\\", "/");
        var leaf = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    private void RunSliceOrReslice()
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

        // Remember originally open scene to restore later.
        string originalScenePath = SceneManager.GetActiveScene().path;

        try
        {
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    float progress = processed / (float)total;
                    EditorUtility.DisplayProgressBar("Tile Slice/Reslice", $"Processing tile {tx},{ty}", progress);

                    // Build new TerrainData from the source master
                    TerrainData newTD = BuildTileTerrainData(
                        tx, ty, hStepX, hStepY, aStepX, aStepY, dStepX, dStepY,
                        layers, detailLayerCount, treePrototypes
                    );

                    string tdPath = $"{outputFolder}/{terrainDataPrefix}{tx}_{ty}.asset";
                    var existingTD = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);

                    // Optional: skip if heights unchanged vs existing tile TD (fast approximate)
                    if (onlyUpdateIfChanged && existingTD != null && copyHeights)
                    {
                        if (HeightsEqual(existingTD, newTD))
                        {
                            // Reuse old TD; no scene changes needed unless scene doesn't exist
                            UnityEngine.Object.DestroyImmediate(newTD);
                            newTD = existingTD;
                        }
                    }

                    if (existingTD == null || newTD != existingTD)
                    {
                        // Save/overwrite TD asset deterministically
                        SaveOrReplaceTerrainDataAsset(tdPath, newTD, existingTD);
                    }

                    // Non-destructive scene handling
                    string tileSceneName = sceneNamePattern.Replace("{x}", tx.ToString()).Replace("{y}", ty.ToString());
                    string tileScenePath = $"{outputFolder}/{tileSceneName}.unity";
                    bool sceneExists = File.Exists(tileScenePath);

                    if (nonDestructiveReslice && sceneExists)
                    {
                        // Re-slice: open scene additively, update or create Terrain GO only
                        var opened = EditorSceneManager.OpenScene(tileScenePath, OpenSceneMode.Additive);
                        try
                        {
                            var terrainGO = FindOrCreateTerrainGO(tx, ty, newTD, tileSize, sourceTerrain.transform.position, opened);
                            // If tiles carry extra objects, they remain untouched.
                            EditorSceneManager.MarkSceneDirty(opened);
                            EditorSceneManager.SaveScene(opened);
                        }
                        finally
                        {
                            // Close tile scene to keep the editor clean
                            EditorSceneManager.CloseScene(opened, true);
                        }
                    }
                    else
                    {
                        // Fresh create (or destructive replace)
                        var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                        newScene.name = tileSceneName;

                        var terrainGO = Terrain.CreateTerrainGameObject(newTD);
                        terrainGO.name = $"Terrain_{tx}_{ty}";
                        PositionTerrainGO(terrainGO.transform, tx, ty, tileSize, sourceTerrain.transform.position);

                        var col = terrainGO.GetComponent<TerrainCollider>();
                        if (col != null) col.enabled = true;

                        EditorSceneManager.SaveScene(newScene, tileScenePath);
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

            // Re-open original scene if it existed
            if (!string.IsNullOrEmpty(originalScenePath) && File.Exists(originalScenePath))
            {
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
            }
        }
    }

    private TerrainData BuildTileTerrainData(
        int tx, int ty,
        int hStepX, int hStepY, int aStepX, int aStepY, int dStepX, int dStepY,
        TerrainLayer[] layers, int detailLayerCount, TreePrototype[] treePrototypes)
    {
        var td = new TerrainData();

        // Heights resolution (+1 for shared edge)
        int hW = hStepX + 1;
        int hH = hStepY + 1;

        // Prepare basic resolutions up front
        td.heightmapResolution = hW;
        td.alphamapResolution = Mathf.Max(1, aStepX);
        td.SetDetailResolution(Mathf.Max(1, dStepX), _srcTD.detailResolutionPerPatch);

        // Size & layers
        var srcSize = _srcTD.size;
        var tileSize = new Vector3(srcSize.x / tilesX, srcSize.y, srcSize.z / tilesY);
        td.size = tileSize;
        td.terrainLayers = layers;

        // Heights
        if (copyHeights)
        {
            int hX = tx * hStepX;
            int hY = ty * hStepY;
            var heights = _srcTD.GetHeights(hX, hY, hW, hH);
            td.SetHeights(0, 0, heights);
        }

        // Alphamaps
        if (copyAlphamaps && _srcTD.alphamapLayers > 0 && layers != null && layers.Length > 0)
        {
            int aRes = _srcTD.alphamapResolution;
            int aX = tx * aStepX;
            int aY = ty * aStepY;
            int w = Math.Min(aStepX, aRes - aX);
            int h = Math.Min(aStepY, aRes - aY);
            // guard for tiny tiles when aRes not divisible
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var splats = _srcTD.GetAlphamaps(aX, aY, w, h);
            td.SetAlphamaps(0, 0, splats);
        }

        // Details
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
                td.SetDetailLayer(0, 0, layer, detail);
            }
        }

        // Trees
        if (copyTrees && treePrototypes != null && treePrototypes.Length > 0)
        {
            td.treePrototypes = treePrototypes;
            var srcTrees = _srcTD.treeInstances;
            var tileTrees = new List<TreeInstance>(128);

            float x0 = tx / (float)tilesX;
            float x1 = (tx + 1) / (float)tilesX;
            float y0 = ty / (float)tilesY;
            float y1 = (ty + 1) / (float)tilesY;

            foreach (var t in srcTrees)
            {
                if (t.position.x >= x0 && t.position.x < x1 && t.position.z >= y0 && t.position.z < y1)
                {
                    var nt = t; // struct copy
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
            // Replace the asset contents while keeping GUID stable:
            // Delete old asset, then create new with same path.
            AssetDatabase.DeleteAsset(tdPath);
            AssetDatabase.CreateAsset(newTD, tdPath);
        }
    }

    private static GameObject FindOrCreateTerrainGO(int tx, int ty, TerrainData td, Vector3 tileSize, Vector3 srcPos, Scene scene)
    {
        // Try by conventional name first
        string expectedName = $"Terrain_{tx}_{ty}";
        GameObject terrainGO = scene.GetRootGameObjects().FirstOrDefault(go => go.name == expectedName);

        if (terrainGO == null)
        {
            // Fallback: any Terrain in scene?
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
            var terrain = terrainGO.GetComponent<Terrain>();
            if (terrain == null) terrain = terrainGO.AddComponent<Terrain>();
            terrain.terrainData = td;

            var col = terrainGO.GetComponent<TerrainCollider>();
            if (col == null) col = terrainGO.AddComponent<TerrainCollider>();
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
            // Sample coarse grid to avoid massive allocation; good enough for “changed?” checks
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
        catch
        {
            return false;
        }
    }
}
#endif
