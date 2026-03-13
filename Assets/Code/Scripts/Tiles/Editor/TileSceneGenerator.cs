#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using System.Text;

public sealed class TileSceneGenerator : ScriptableObject
{
    public const string CanonicalSettingsAssetPath = "Assets/TileSliceSettings.asset";
    public const string CanonicalGridMetadataAssetPath = TileGridMetadataProvider.CanonicalAssetPath;
    
    // UI / runtime guard
    private bool _isRunning;
    private Stopwatch _globalTimer;
    
    private readonly List<string> _changedTerrains = new();
    private readonly StringBuilder _terrainLog = new(256);
    
    // Per-terrain change trackers (reset per terrain)
    private bool _changedHeights, _changedAlpha, _changedDetails, _changedTrees;
    private int  _treesAdded, _treesRemoved, _treesModifiedTiles;
    
    // Per-run final summary (reset once per run)
    private readonly Dictionary<string, string> _finalContentSummary = new(32);
    private readonly Dictionary<string, string> _gizmoStatus = new(32);

    [SerializeField] private TileSliceSettings settings;
    [SerializeField, HideInInspector] [Obsolete("Legacy field. Use outputFolder from the inspector.")] private string folder = "Assets/Level/Scenes/Tiles/";
    
    // Source (multi-terrain)
    [SerializeField] private bool autoCollectTerrains = true;
    [SerializeField] private string terrainNamePrefix = "Terrain_";
    [SerializeField] private Terrain[] sourceTerrains; // used when autoCollectTerrains = false
    [SerializeField] private string masterTerrainLabelOverride = "Terrain_Master";
    [SerializeField] private bool autoFixDivisibility = true;

    // cached for current terrain
    private TerrainData _srcTD;
    private string _currentTerrainLabel = "";

    // Grid (meters)
    [SerializeField] private float tileSizeMeters = 250f;
    private int tilesX;
    private int tilesY;
    [SerializeField] private string masterTerrainNameContains = "Master";
    [SerializeField] private bool masterForceSquareTiles = true;
    
    [SerializeField] private bool evenFitNoRemainder = true; // adjust size so terrain divides evenly
    [SerializeField] private bool forceSquareTiles   = true; // when even-fit, make tiles perfect squares
    [SerializeField] private int maxTilesPerAxis = 128;
    [SerializeField] private int maxTilesPerTerrain = 4096;

    // Output
    [SerializeField] private string sceneNamePattern = "{t}_{tile}";
    [SerializeField] private string outputFolder = "Assets/Tiles";
    [SerializeField] private string terrainDataPrefix = "TD_"; // saved as TD_<t>_<tileRef>.asset (example: TD_Terrain_East_A1.asset)
    [SerializeField] private bool subfolderPerTerrain = true;

    private string _outputScenesFolder;
    private string _outputDataFolder;
    private string _outputPropsFolder;
    private bool _outputWriteFoldersEnsured;

    // Copy Channels
    [SerializeField] private bool copyHeights = true;
    [SerializeField] private bool copyAlphamaps = true;
    [SerializeField] private bool copyDetails = true;
    [SerializeField] private bool copyTrees = true;
    [SerializeField] private bool copyProps = true;

    // Reslice Options
    [SerializeField] private bool nonDestructiveReslice = true; // update TerrainData in existing scenes, keep other objects
    [SerializeField] private bool onlyUpdateIfChanged; // small speed-up by skipping identical tiles (height-only compare)
    [SerializeField] private bool addToBuildSettings = true;
    [SerializeField] private bool clearConsoleBeforeActions;

    // Snapshot type (do NOT hold Terrain refs while running)
    private sealed class TerrainSnapshot
    {
        public string label; // sanitized terrain name
        public TerrainData data; // stable asset ref
        public Vector3 origin; // cached world position
    }

    private sealed class PropCandidateCache
    {
        private readonly Dictionary<Vector2Int, List<GameObject>> _tileBins;
        private static readonly List<GameObject> Empty = new(0);

        public int TotalCandidates { get; }

        public PropCandidateCache(List<GameObject> candidates, Vector3 terrainOrigin, Vector3 tileSize, int tileCountX, int tileCountY)
        {
            _tileBins = new Dictionary<Vector2Int, List<GameObject>>(Mathf.Max(1, tileCountX * tileCountY));
            if (candidates == null || candidates.Count == 0)
            {
                TotalCandidates = 0;
                return;
            }

            TotalCandidates = candidates.Count;

            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;

                Vector3 p = candidate.transform.position;
                int tx = Mathf.FloorToInt((p.x - terrainOrigin.x) / tileSize.x);
                int ty = Mathf.FloorToInt((p.z - terrainOrigin.z) / tileSize.z);

                if (tx < 0 || tx >= tileCountX || ty < 0 || ty >= tileCountY)
                    continue;

                var key = new Vector2Int(tx, ty);
                if (!_tileBins.TryGetValue(key, out var list))
                {
                    list = new List<GameObject>();
                    _tileBins.Add(key, list);
                }

                list.Add(candidate);
            }
        }

        public List<GameObject> GetTileCandidates(int tx, int ty)
        {
            return _tileBins.TryGetValue(new Vector2Int(tx, ty), out var list) ? list : Empty;
        }
    }
    
    public bool IsRunning => _isRunning;
    
    private int _runCounter;
    private string _activeRunId = "idle";

    public bool CanRun()
    {
        if (autoCollectTerrains) return (Terrain.activeTerrains?.Length ?? 0) > 0;
        return sourceTerrains != null && sourceTerrains.Any(t => t != null);
    }

    public void EnsureReadyForUi()
    {
        EnsureSettingsAsset();
    }
    
    public string[] FindAllSettingsAssetPaths()
    {
        var guids = AssetDatabase.FindAssets("t:TileSliceSettings");
        if (guids == null || guids.Length == 0)
            return Array.Empty<string>();

        return guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    public void AutoFillAndLogTerrains()
    {
        BeginOperation("Auto-fill Terrains");
        var previewList = CollectSnapshots(onlySnapshotList: true);
        Log($"Found {previewList.Count} terrain(s): {string.Join(", ", previewList.Select(s => s.label))}");
    }

    public string BuildPreviewText()
    {
        try
        {
            var snaps = CollectSnapshots(onlySnapshotList: true);
            if (snaps == null || snaps.Count == 0)
                return string.Empty;

            int grandTotal = 0;
            var sb = new StringBuilder(512);
            sb.AppendLine("Preview:");

            foreach (var s in snaps)
            {
                ComputePreviewGridFor(s.data, out int nx, out int ny, out float fx, out float fy);
                grandTotal += nx * ny;

                var sz = s.data.size;
                string label = s.label.PadRight(20);
                string sizeStr = $"{sz.x,6:0.#}×{sz.z,-6:0.#}";
                string tilesStr = $"{nx,3}×{ny,-3}";
                string tileSizeStr = $"{fx,5:0.#}×{fy,-5:0.#}";
                sb.AppendLine($"{label} | {sizeStr} m | {tilesStr} tiles @ {tileSizeStr} m");
            }

            sb.AppendLine(new string('-', 70));
            sb.AppendLine($"Total tiles: {grandTotal}");
            sb.AppendLine($"Options: evenFit={evenFitNoRemainder}, squares={forceSquareTiles}, desired≈{tileSizeMeters:0.##} m");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"[Preview error] {ex.Message}";
        }
    }

    public void RunSliceWithDialogs()
    {
        BeginOperation("Slice / Re-slice");
        
        try
        {
            RunForAllTerrains();
            EditorUtility.DisplayDialog("Tile Scene Generator", "All terrains processed successfully.", "Great");
        }
        catch (Exception ex)
        {
            LogError($"Failed: {ex}");
            EditorUtility.DisplayDialog("Tile Scene Generator", $"Failed:\n{ex.Message}", "OK");
        }
    }
    

    public void CleanBuildSettingsWithDialog()
    {
        BeginOperation("Clean Build Settings");
        RemoveMissingScenesFromBuildSettings(true);
    }

    public void RevealOutputFolder()
    {
        BeginOperation("Reveal Output Folder");
        RevealGeneratedOutputFolder();
    }

    public void DeleteGeneratedScenesWithDialog()
    {
        BeginOperation("Delete Generated Tile Scenes");
        DeleteGeneratedTileScenesWithConfirmation();
    }

    public void DeleteGeneratedAssetsWithDialog()
    {
        BeginOperation("Delete Generated TerrainData/Assets");
        DeleteGeneratedTileAssetsWithConfirmation();
    }

    public void DeleteAllGeneratedOutputWithDialog()
    {
        BeginOperation("Delete ALL Generated Tile Output");
        DeleteAllGeneratedOutputWithConfirmation();
    }

    private const string LegacyTilesScenesRoot = "Assets/Level/Scenes/Tiles";
    
    #region Generated Output Cleanup
    private readonly struct GeneratedOutputPaths
    {
        public readonly string ConfiguredRoot;
        public readonly string[] Roots;
        public readonly string[] SceneFolders;
        public readonly string[] TerrainDataFolders;
        public readonly string[] PropsFolders;
        public readonly string[] SearchSeeds;

        public GeneratedOutputPaths(string configuredRoot, string[] roots, string[] sceneFolders, string[] terrainDataFolders, string[] propsFolders, string[] searchSeeds)
        {
            ConfiguredRoot = configuredRoot;
            Roots = roots;
            SceneFolders = sceneFolders;
            TerrainDataFolders = terrainDataFolders;
            PropsFolders = propsFolders;
            SearchSeeds = searchSeeds;
        }

        public string[] GetAllFolders()
        {
            var all = new List<string>(Roots.Length + SceneFolders.Length + TerrainDataFolders.Length + PropsFolders.Length);
            all.AddRange(Roots);
            all.AddRange(SceneFolders);
            all.AddRange(TerrainDataFolders);
            all.AddRange(PropsFolders);
            return all.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToArray();
        }
    }

    private GeneratedOutputPaths CollectGeneratedOutputPaths()
    {
        string configuredRoot = NormalizeAssetPath(outputFolder);
        if (string.IsNullOrWhiteSpace(configuredRoot) || !configuredRoot.StartsWith("Assets", StringComparison.Ordinal))
            return new GeneratedOutputPaths(configuredRoot, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var roots = new HashSet<string>(StringComparer.Ordinal);

        if (subfolderPerTerrain)
        {
            var snapshots = CollectSnapshots(onlySnapshotList: true);
            foreach (var snap in snapshots)
            {
                if (snap == null || string.IsNullOrWhiteSpace(snap.label))
                    continue;

                roots.Add(NormalizeAssetPath($"{configuredRoot}/{snap.label}"));
            }
        }
        else
        {
            roots.Add(configuredRoot);
        }

        if (roots.Count == 0)
            roots.Add(configuredRoot);

        string[] rootFolders = roots.ToArray();

        // Scene folders = generated roots + legacy tiles scene folder.
        var sceneFoldersSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in rootFolders)
            sceneFoldersSet.Add(NormalizeAssetPath($"{r}/Scenes"));

        string legacyTilesScenes = NormalizeAssetPath(LegacyTilesScenesRoot);
        if (!string.IsNullOrWhiteSpace(legacyTilesScenes) && legacyTilesScenes.StartsWith("Assets", StringComparison.Ordinal))
            sceneFoldersSet.Add(legacyTilesScenes);

        string[] sceneFolders = sceneFoldersSet.ToArray();

        string[] terrainDataFolders = rootFolders.Select(r => NormalizeAssetPath($"{r}/TerrainData")).ToArray();
        string[] propsFolders = rootFolders.Select(r => NormalizeAssetPath($"{r}/Props")).ToArray();
        var searchSeeds = BuildSearchSeedFolders(configuredRoot, rootFolders);

        return new GeneratedOutputPaths(configuredRoot, rootFolders, sceneFolders, terrainDataFolders, propsFolders, searchSeeds);
    }


    private static string NormalizeAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string p = path.Replace("\\", "/").Trim().TrimEnd('/');

        // Normalize "assets/..." to "Assets/..."
        if (p.StartsWith("assets/", StringComparison.Ordinal))
            p = "Assets/" + p.Substring("assets/".Length);

        if (p.Equals("assets", StringComparison.Ordinal))
            p = "Assets";

        return p;
    }

    private static string[] BuildSearchSeedFolders(string configuredRoot, IEnumerable<string> rootFolders)
    {
        var seeds = new HashSet<string>(StringComparer.Ordinal) { "Assets", configuredRoot };

        foreach (var root in rootFolders)
        {
            string normalized = NormalizeAssetPath(root);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            seeds.Add(normalized);

            string parent = NormalizeAssetPath(Path.GetDirectoryName(normalized));
            if (!string.IsNullOrWhiteSpace(parent) && parent.StartsWith("Assets", StringComparison.Ordinal))
                seeds.Add(parent);
        }

        // Ensure legacy tiles scene folder is included as a discovery seed.
        string legacyTiles = NormalizeAssetPath(LegacyTilesScenesRoot);
        if (!string.IsNullOrWhiteSpace(legacyTiles) && legacyTiles.StartsWith("Assets", StringComparison.Ordinal))
        {
            seeds.Add(legacyTiles);

            string legacyParent = NormalizeAssetPath(Path.GetDirectoryName(legacyTiles));
            if (!string.IsNullOrWhiteSpace(legacyParent) && legacyParent.StartsWith("Assets", StringComparison.Ordinal))
                seeds.Add(legacyParent);
        }

        return seeds.Where(AssetDatabase.IsValidFolder).Distinct().ToArray();
    }

    private string[] DiscoverAdditionalSceneFolders(GeneratedOutputPaths paths)
    {
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        string sceneNameRegex = BuildSceneNameRegex(sceneNamePattern);

        foreach (string seed in paths.SearchSeeds)
        {
            if (!AssetDatabase.IsValidFolder(seed))
                continue;

            string[] guids = AssetDatabase.FindAssets("t:Scene", new[] { seed });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(assetPath))
                    continue;

                string sceneName = Path.GetFileNameWithoutExtension(assetPath);

                if (!Regex.IsMatch(sceneName, sceneNameRegex, RegexOptions.IgnoreCase))
                    continue;

                string parentFolder = NormalizeAssetPath(Path.GetDirectoryName(assetPath));

                if (!string.IsNullOrWhiteSpace(parentFolder) &&
                    IsCleanupPathCompatible(paths.ConfiguredRoot, parentFolder))
                {
                    discovered.Add(parentFolder);
                }
            }
        }

        return discovered.ToArray();
    }

    private string[] DiscoverAdditionalTerrainAssetFolders(GeneratedOutputPaths paths)
    {
        string escapedPrefix = Regex.Escape(terrainDataPrefix ?? "TD_");

        // Matches: TD_Terrain_<anything>_<tileRef> (example: TD_Terrain_East_A1)
        string tdRegex = $"^{escapedPrefix}Terrain_.+_(?:[A-Za-z]+\\d+|\\d+_\\d+)$";

        // Matches: Props_<anything>_<tileRef> (example: Props_Terrain_East_A1)
        string propRegex = "^Props_.+_(?:[A-Za-z]+\\d+|\\d+_\\d+)$";

        var discovered = new HashSet<string>(StringComparer.Ordinal);

        foreach (string seed in paths.SearchSeeds)
        {
            if (!AssetDatabase.IsValidFolder(seed))
                continue;

            // TerrainData search (fast)
            string[] tdGuids = AssetDatabase.FindAssets("t:TerrainData", new[] { seed });
            foreach (string guid in tdGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(assetPath) || AssetDatabase.IsValidFolder(assetPath))
                    continue;

                string file = Path.GetFileNameWithoutExtension(assetPath);
                if (!Regex.IsMatch(file, tdRegex, RegexOptions.IgnoreCase))
                    continue;

                string parentFolder = NormalizeAssetPath(Path.GetDirectoryName(assetPath));
                if (!string.IsNullOrWhiteSpace(parentFolder) && IsCleanupPathCompatible(paths.ConfiguredRoot, parentFolder))
                    discovered.Add(parentFolder);
            }

            // Also scan non-scene assets for Props_* patterns (keeps your old behavior)
            string[] objGuids = AssetDatabase.FindAssets("t:Object", new[] { seed });
            foreach (string guid in objGuids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(assetPath) ||
                    AssetDatabase.IsValidFolder(assetPath) ||
                    assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;

                string file = Path.GetFileNameWithoutExtension(assetPath);
                if (!Regex.IsMatch(file, propRegex, RegexOptions.IgnoreCase))
                    continue;

                string parentFolder = NormalizeAssetPath(Path.GetDirectoryName(assetPath));
                if (!string.IsNullOrWhiteSpace(parentFolder) && IsCleanupPathCompatible(paths.ConfiguredRoot, parentFolder))
                    discovered.Add(parentFolder);
            }
        }

        return discovered.ToArray();
    }

    private static string BuildSceneNameRegex(string pattern)
    {
        string source = NormalizeLegacySceneNamePattern(pattern);
        string escaped = Regex.Escape(source)
            .Replace("\\{t\\}", ".+")
            .Replace("\\{x\\}", "\\d+")
            .Replace("\\{y\\}", "\\d+")
            .Replace("\\{tile\\}", "(?:[A-Za-z]+\\d+|\\d+_\\d+)");
        return "^" + escaped + "$";
    }

    private static bool IsCleanupPathCompatible(string configuredRoot, string folder)
    {
        string root = NormalizeAssetPath(configuredRoot);
        string candidate = NormalizeAssetPath(folder);

        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            return false;

        // Allow the legacy tiles folder explicitly.
        string legacyTiles = NormalizeAssetPath(LegacyTilesScenesRoot);
        if (!string.IsNullOrWhiteSpace(legacyTiles))
        {
            if (candidate.Equals(legacyTiles, StringComparison.Ordinal) ||
                candidate.StartsWith(legacyTiles + "/", StringComparison.Ordinal))
                return true;
        }

        if (candidate.Equals(root, StringComparison.Ordinal) || candidate.StartsWith(root + "/", StringComparison.Ordinal))
            return true;

        string[] rootSegments = root.Split('/');
        string[] candidateSegments = candidate.Split('/');
        if (rootSegments.Length < 2 || candidateSegments.Length < 2)
            return false;

        string rootTail2 = string.Join("/", rootSegments.Skip(rootSegments.Length - 2));
        string candTail2 = string.Join("/", candidateSegments.Skip(candidateSegments.Length - 2));

        return rootTail2.Equals(candTail2, StringComparison.OrdinalIgnoreCase) &&
               candidate.IndexOf("/Tiles", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RevealGeneratedOutputFolder()
    {
        var paths = CollectGeneratedOutputPaths();

        // Prefer folders that actually contain tile scenes or generated TerrainData.
        string[] candidateFolders = paths.GetAllFolders()
            .Concat(DiscoverAdditionalSceneFolders(paths))
            .Concat(DiscoverAdditionalTerrainAssetFolders(paths))
            .Distinct()
            .ToArray();

        // Try to reveal the first valid folder that has content we care about.
        string revealTarget = null;

        string sceneNameRegex = BuildSceneNameRegex(sceneNamePattern);

        foreach (string folder in candidateFolders.Where(AssetDatabase.IsValidFolder))
        {
            bool hasTileScene = AssetDatabase.FindAssets("t:Scene", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Any(p =>
                {
                    string name = Path.GetFileNameWithoutExtension(p);
                    return !string.IsNullOrWhiteSpace(name) &&
                           Regex.IsMatch(name, sceneNameRegex, RegexOptions.IgnoreCase);
                });

            if (hasTileScene)
            {
                revealTarget = folder;
                break;
            }

            bool hasTerrainData = AssetDatabase.FindAssets("t:TerrainData", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Any(p =>
                {
                    string name = Path.GetFileNameWithoutExtension(p);
                    if (string.IsNullOrWhiteSpace(name))
                        return false;

                    string escapedPrefix = Regex.Escape(terrainDataPrefix ?? "TD_");
                    string tdRegex = $"^{escapedPrefix}Terrain_.+_(?:[A-Za-z]+\\d+|\\d+_\\d+)$";
                    return Regex.IsMatch(name, tdRegex, RegexOptions.IgnoreCase);
                });

            if (hasTerrainData)
            {
                revealTarget = folder;
                break;
            }
        }

        // Fall back to configured roots if nothing was discovered.
        if (string.IsNullOrWhiteSpace(revealTarget))
            revealTarget = paths.Roots.FirstOrDefault(AssetDatabase.IsValidFolder) ?? paths.ConfiguredRoot;

        if (string.IsNullOrWhiteSpace(revealTarget) || !AssetDatabase.IsValidFolder(revealTarget))
        {
            EditorUtility.DisplayDialog("Reveal Output Folder", "Could not locate a folder containing current tiles.", "OK");
            return;
        }

        string absolute = Path.GetFullPath(revealTarget);
        EditorUtility.RevealInFinder(absolute);
        Log($"Revealed tile output folder: {revealTarget}");
    }

    private void DeleteGeneratedTileScenesWithConfirmation()
    {
        var paths = CollectGeneratedOutputPaths();

        string[] discovered = DiscoverAdditionalSceneFolders(paths);

        DebugLogResolvedFolders("Base Scene Folders", paths.SceneFolders);
        DebugLogResolvedFolders("Discovered Scene Folders (Tile Regex)", discovered);

        string[] sceneFolders = paths.SceneFolders
            .Concat(discovered)
            .Distinct()
            .ToArray();

        DebugLogResolvedFolders("Final Scene Folders Used For Deletion", sceneFolders);

        if (!ValidateCleanupSafety(paths, sceneFolders, out string reason))
        {
            EditorUtility.DisplayDialog("Delete Generated Tile Scenes", reason, "OK");
            return;
        }

        if (!ConfirmCleanup("Delete Generated Tile Scenes", "This will delete generated tile scene assets.", sceneFolders))
            return;

        int sceneCount = DeleteAssetsByFilter("t:Scene", sceneFolders);

        DeleteEmptyFolders(sceneFolders, paths.ConfiguredRoot);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Log($"Cleanup complete. scenes deleted={sceneCount}");
    }

    private void DeleteGeneratedTileAssetsWithConfirmation()
    {
        var paths = CollectGeneratedOutputPaths();

        string[] discovered = DiscoverAdditionalTerrainAssetFolders(paths);

        DebugLogResolvedFolders("Base TerrainData Folders", paths.TerrainDataFolders);
        DebugLogResolvedFolders("Base Props Folders", paths.PropsFolders);
        DebugLogResolvedFolders("Discovered Terrain/Props Folders", discovered);

        string[] folders = paths.TerrainDataFolders
            .Concat(paths.PropsFolders)
            .Concat(discovered)
            .Distinct()
            .ToArray();

        DebugLogResolvedFolders("Final Terrain Folders Used For Deletion", folders);

        if (!ValidateCleanupSafety(paths, folders, out string reason))
        {
            EditorUtility.DisplayDialog("Delete Generated TerrainData/Assets", reason, "OK");
            return;
        }

        if (!ConfirmCleanup("Delete Generated TerrainData/Assets", "This will delete generated TerrainData and tile asset output.", folders))
            return;

        int terrainCount = DeleteAssetsByFilter("t:TerrainData", folders);
        int otherAssetsCount = DeleteNonSceneAssets(folders);

        DeleteEmptyFolders(folders, paths.ConfiguredRoot);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Log($"Cleanup complete. assets deleted={terrainCount + otherAssetsCount}");
    }

    private void DeleteAllGeneratedOutputWithConfirmation()
    {
        var paths = CollectGeneratedOutputPaths();

        string[] folders = paths.GetAllFolders()
            .Concat(new[] { paths.ConfiguredRoot }) // ensures root can be deleted if empty (but never "Assets")
            .Concat(DiscoverAdditionalSceneFolders(paths))
            .Concat(DiscoverAdditionalTerrainAssetFolders(paths))
            .Distinct()
            .ToArray();

        DebugLogResolvedFolders("ALL Folders Before Delete", folders);

        if (!ValidateCleanupSafety(paths, folders, out string reason))
        {
            EditorUtility.DisplayDialog("Delete ALL Generated Tile Output", reason, "OK");
            return;
        }

        if (!ConfirmCleanup("Delete ALL Generated Tile Output", "This will delete generated scenes and tile assets.", folders))
            return;

        int sceneCount = DeleteAssetsByFilter("t:Scene", folders);
        int terrainCount = DeleteAssetsByFilter("t:TerrainData", folders);
        int otherAssetsCount = DeleteNonSceneAssets(folders);

        DeleteEmptyFolders(folders, paths.ConfiguredRoot);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Log($"Cleanup complete. scenes deleted={sceneCount}, assets deleted={terrainCount + otherAssetsCount}, folders=[{string.Join(", ", folders)}]");
    }

    private bool ConfirmCleanup(string title, string message, IEnumerable<string> folders)
    {
        string folderBlock = string.Join("\n", folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().Select(f => $"• {f}"));
        return EditorUtility.DisplayDialog(
            title,
            $"{message}\n\nTarget folders:\n{folderBlock}\n\nProceed?",
            "Delete",
            "Cancel");
    }

    private bool ValidateCleanupSafety(GeneratedOutputPaths paths, IEnumerable<string> candidateFolders, out string reason)
    {
        string configuredRoot = NormalizeAssetPath(paths.ConfiguredRoot);
        if (string.IsNullOrWhiteSpace(configuredRoot) || !configuredRoot.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Cleanup aborted. Output folder must be a valid Assets/ path.";
            return false;
        }

        foreach (string rawFolder in candidateFolders)
        {
            string folder = NormalizeAssetPath(rawFolder);
            if (string.IsNullOrWhiteSpace(folder))
                continue;

            bool compatible = IsCleanupPathCompatible(configuredRoot, folder);
            if (!compatible && !folder.Equals(configuredRoot, StringComparison.Ordinal))
            {
                reason =
                    "Cleanup aborted by safety check.\n" +
                    $"Configured output root: {configuredRoot}\n" +
                    $"Unsafe folder: {folder}";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private int DeleteAssetsByFilter(string filter, string[] searchFolders)
    {
        int deleted = 0;
        foreach (var folder in searchFolders.Where(AssetDatabase.IsValidFolder).Distinct())
        {
            string[] guids = AssetDatabase.FindAssets(filter, new[] { folder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                if (AssetDatabase.DeleteAsset(path))
                    deleted++;
            }
        }

        return deleted;
    }

    private int DeleteNonSceneAssets(string[] searchFolders)
    {
        int deleted = 0;
        foreach (var folder in searchFolders.Where(AssetDatabase.IsValidFolder).Distinct())
        {
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { folder });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || AssetDatabase.IsValidFolder(path) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AssetDatabase.DeleteAsset(path))
                    deleted++;
            }
        }

        return deleted;
    }

    private void DeleteEmptyFolders(IEnumerable<string> folders, string configuredRoot)
    {
        // Work bottom-up: deepest folders first.
        var ordered = folders
            .Where(AssetDatabase.IsValidFolder)
            .Select(NormalizeAssetPath)
            .Distinct()
            .OrderByDescending(f => f.Count(c => c == '/'))
            .ToArray();

        foreach (string start in ordered)
            DeleteEmptyFolderAndParents(start, configuredRoot);
    }

    private void DeleteEmptyFolderAndParents(string folder, string configuredRoot)
    {
        string current = NormalizeAssetPath(folder);

        while (!string.IsNullOrWhiteSpace(current) &&
               AssetDatabase.IsValidFolder(current) &&
               !current.Equals("Assets", StringComparison.Ordinal))
        {
            // Only allow deleting folders that pass your safety rules
            if (!IsCleanupPathCompatible(configuredRoot, current) && !current.Equals(NormalizeAssetPath(configuredRoot), StringComparison.Ordinal))
                break;

            if (!IsFolderTrulyEmpty(current))
                break;

            FileUtil.DeleteFileOrDirectory(current);
            FileUtil.DeleteFileOrDirectory(current + ".meta");

            string parent = NormalizeAssetPath(Path.GetDirectoryName(current));
            if (string.IsNullOrWhiteSpace(parent) || parent.Equals(current, StringComparison.Ordinal))
                break;

            current = parent;
        }
    }

    private static bool IsFolderTrulyEmpty(string assetFolderPath)
    {
        // Convert "Assets/..." to an absolute path on disk.
        string absolute = Path.GetFullPath(assetFolderPath);

        if (!Directory.Exists(absolute))
            return true;

        // Any real files (excluding .meta) means not empty.
        foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Any subdirectories means not empty.
        foreach (var dir in Directory.EnumerateDirectories(absolute, "*", SearchOption.TopDirectoryOnly))
            return false;

        return true;
    }
    
    private void DebugLogResolvedFolders(string label, IEnumerable<string> folders)
    {
        var list = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct()
            .OrderBy(f => f)
            .ToArray();

        Log(
            $"{label}\n" +
            $"Count: {list.Length}\n" +
            string.Join("\n", list.Select(f => $"  • {f}"))
        );
    }
    #endregion

    // MAIN ORCHESTRATOR (safe snapshots)
    private void RunForAllTerrains()
    {
        _isRunning = true;
        
        RemoveMissingScenesFromBuildSettings(false);
        
        _changedTerrains.Clear();
        _finalContentSummary.Clear();
        _gizmoStatus.Clear();
        _globalTimer = Stopwatch.StartNew(); // start total timer
        try
        {
            var snapshots = CollectSnapshots();
            if (snapshots.Count == 0)
                throw new InvalidOperationException("No valid terrains found. Check auto-collect prefix or assign terrains manually.");
            
            var allTileIndexRecords = new List<TileIndex.TileRecord>();
            int expectedTileCount = 0;
            Vector2? sharedTileSize2D = null;
            Vector2? sharedOriginOffset = null;

            Log($"Will process {snapshots.Count} terrain(s): {string.Join(", ", snapshots.Select(s => s.label))}");

            string originalOutputRoot = NormalizeAssetPath(outputFolder);

            if (subfolderPerTerrain && TryGetKnownTerrainLabelSuffix(originalOutputRoot, snapshots, out string matchedTerrainLabel))
            {
                throw new InvalidOperationException(
                    $"Output folder '{originalOutputRoot}' already ends with terrain label '{matchedTerrainLabel}' while 'Subfolder Per Terrain' is enabled. " +
                    "Use a neutral root (for example 'Assets/Tiles') so folders are generated only once per terrain.");
            }

            EnsureSettingsAsset();
            // loop through each terrain and time individually
            foreach (var snap in snapshots)
            {
                _terrainLog.Clear();
                _changedHeights = _changedAlpha = _changedDetails = _changedTrees = false;
                _treesAdded = _treesRemoved = _treesModifiedTiles = 0;
                var terrainTimer = Stopwatch.StartNew();

                _currentTerrainLabel = snap.label;
                _srcTD               = snap.data;
                Vector3 cachedOrigin = snap.origin;

                Vector2Int coordOffset = Vector2Int.zero;
                if (sharedOriginOffset.HasValue && sharedTileSize2D.HasValue && sharedTileSize2D.Value.x > 0f && sharedTileSize2D.Value.y > 0f)
                {
                    coordOffset = new Vector2Int(
                        Mathf.RoundToInt((cachedOrigin.x - sharedOriginOffset.Value.x) / sharedTileSize2D.Value.x),
                        Mathf.RoundToInt((cachedOrigin.z - sharedOriginOffset.Value.y) / sharedTileSize2D.Value.y)
                    );
                }

                string perTerrainOutputRoot = subfolderPerTerrain
                    ? NormalizeAssetPath($"{originalOutputRoot}/{_currentTerrainLabel}")
                    : originalOutputRoot;

                _gizmoStatus[_currentTerrainLabel] = settings ? "unchanged" : "n/a (no TileSliceSettings)";
                
                ComputeGridFromMeters();
                
                bool isMaster = IsMasterTerrainLabel(_currentTerrainLabel);
                AutoFixTilesToDivisibleResolutionsIfNeeded(requireSquareTiles: isMaster);
                
                ValidateInputs(perTerrainOutputRoot);
                DeleteOldTileScenes(_outputScenesFolder, _currentTerrainLabel, tilesX, tilesY);
                DeleteOldTerrainDataAssets(_outputDataFolder, _currentTerrainLabel, terrainDataPrefix, tilesX, tilesY);
                DeleteOldPropDataAssets(_outputPropsFolder, _currentTerrainLabel, tilesX, tilesY);
                var currentGeneratedSceneManifest = BuildGeneratedScenePathManifest(_outputScenesFolder, tilesX, tilesY);
                RemoveOldBuildSettingsEntries(_currentTerrainLabel, currentGeneratedSceneManifest);
                
                if (settings && settings.TryGet(_currentTerrainLabel, out var r))
                {
                    r.origin = cachedOrigin; 
                    EditorUtility.SetDirty(settings);
                }
                var terrainRecords = RunSliceOrReslice(cachedOrigin, cordOffset);
                allTileIndexRecords.AddRange(terrainRecords);
                expectedTileCount += tilesX * tilesY;

                var terrainTileSize2D = new Vector2(_srcTD.size.x / tilesX, _srcTD.size.z / tilesY);
                if (sharedTileSize2D == null)
                {
                    sharedTileSize2D = terrainTileSize2D;
                }
                else if (sharedTileSize2D.Value != terrainTileSize2D)
                {
                    Debug.LogWarning(
                        $"[TileSceneGenerator] Terrain '{_currentTerrainLabel}' has tile size {terrainTileSize2D}, " +
                        $"but prior terrains used {sharedTileSize2D.Value}. TileIndex tile size remains {sharedTileSize2D.Value}."
                    );
                }

                if (sharedOriginOffset == null)
                    sharedOriginOffset = new Vector2(cachedOrigin.x, cachedOrigin.z);

                terrainTimer.Stop(); // stop individual timer
                
                var elapsed = terrainTimer.Elapsed;
                string formatted = $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
                _terrainLog.AppendLine($"Finished {_currentTerrainLabel} in {formatted}.");

                var changedKinds = new List<string>(4);
                if (_changedHeights)  changedKinds.Add("heights");
                if (_changedAlpha)    changedKinds.Add("splatmaps");
                if (_changedDetails)  changedKinds.Add("details");
                if (_changedTrees)
                {
                    var parts = new List<string>(3);
                    if (_treesAdded   > 0) parts.Add($"added { _treesAdded }");
                    if (_treesRemoved > 0) parts.Add($"removed { _treesRemoved }");
                    if (_treesModifiedTiles > 0) parts.Add($"modified ({_treesModifiedTiles} tile(s))");
                    changedKinds.Add(parts.Count > 0 ? $"trees: {string.Join(", ", parts)}" : "trees");
                }
                
                string contentLine;
                if (changedKinds.Count > 0)
                {
                    contentLine = $"Content changed → {string.Join(", ", changedKinds)}.";
                    _terrainLog.AppendLine(contentLine);
                }
                else
                {
                    contentLine = "No content changes.";
                    _terrainLog.AppendLine(contentLine);
                }
                
                // Save per-terrain summary for the final run report
                _finalContentSummary[_currentTerrainLabel] = contentLine;

                // Emit ONE log for this terrain (includes grid line, gizmo status, finish time, and the change summary)
                Log(_terrainLog.ToString());
            }
            WriteMergedTileIndex(allTileIndexRecords, expectedTileCount, sharedTileSize2D, sharedOriginOffset);
            UpdateGridMetadataAsset(sharedTileSize2D, sharedOriginOffset);
        }
        finally
        {
            if (_globalTimer != null && _globalTimer.IsRunning)
                _globalTimer.Stop();

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Final run summary log
            var sb = new StringBuilder(512);
            sb.AppendLine("[TileSceneGenerator] Final summary:");
            
            var totalTime = _globalTimer.Elapsed;
            string formattedTime = $"{(int)totalTime.TotalMinutes}m {totalTime.Seconds}s";
            sb.AppendLine($"• All terrains processed in {formattedTime} total.");

            if (_changedTerrains.Count > 0)
                sb.AppendLine($"• Gizmo updated for {_changedTerrains.Count} terrain(s): {string.Join(", ", _changedTerrains)}");
            else
                sb.AppendLine("• Gizmo data unchanged for all terrains.");

            // Determine master terrain and compass ordering (clockwise from North)
            var compassLabels = OrderLabelsClockwiseFromNorth(settings, null);

            // Collect for alignment (compute max widths for label and content)
            var rows = new List<(string Label, string Content, string Gizmo)>();
            int maxLabelLen = 0;
            int maxContentLen = 0;

            foreach (var label in compassLabels)
            {
                if (!_finalContentSummary.TryGetValue(label, out var content))
                    continue;

                var gizmo = _gizmoStatus.TryGetValue(label, out var gs) ? gs : "n/a";
                maxLabelLen   = Mathf.Max(maxLabelLen,   label.Length);
                maxContentLen = Mathf.Max(maxContentLen, content.Length);
                rows.Add((label, content, gizmo));
            }

            // Print aligned lines (plain text, clean columns)
            foreach (var row in rows)
            {
                string labelPadded   = row.Label.PadRight(maxLabelLen);
                string contentPadded = row.Content.PadRight(maxContentLen + 2);
                sb.AppendLine($"  • {labelPadded}: {contentPadded}(gizmo {row.Gizmo})");
            }

            Debug.Log(sb.ToString());

            _changedTerrains.Clear();
            _finalContentSummary.Clear();
            _gizmoStatus.Clear();
            _isRunning = false;
        }
    }
    
    private static bool TryParseTileCoordSuffix(string name, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var parts = name.Split('_');
        if (parts.Length < 2)
            return false;

        if (int.TryParse(parts[^2], out x) && int.TryParse(parts[^1], out y))
            return true;

        return TileDisplayNameUtility.TryParseTileReference(parts[^1], out x, out y);
    }

    private static bool IsCoordOutOfRange(int x, int y, int tilesX, int tilesY)
    {
        return x < 0 || y < 0 || x >= tilesX || y >= tilesY;
    }

    private void DeleteOldTileScenes(string scenesFolder, string terrainLabel, int tilesX, int tilesY)
    {
        if (!Directory.Exists(scenesFolder))
            return;

        var files = Directory.GetFiles(scenesFolder, "*.unity", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);

            // Accept both patterns:
            // {t}_{tile} (preferred) or legacy/custom token formats
            if (!name.Contains(terrainLabel)) 
                continue;

            if (TryParseTileCoordSuffix(name, out int x, out int y) && IsCoordOutOfRange(x, y, tilesX, tilesY))
            {
                Log($"Deleting old tile scene: {file} ({TileDisplayNameUtility.FormatTileReference(x, y)})");
                AssetDatabase.DeleteAsset(file);
            }
        }
    }

    private void DeleteOldTerrainDataAssets(string dataFolder, string terrainLabel, string prefix, int tilesX, int tilesY)
    {
        if (!Directory.Exists(dataFolder))
            return;

        var files = Directory.GetFiles(dataFolder, "*.asset", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);

            // Only clean TerrainData created by the slicer
            if (!name.StartsWith(prefix + terrainLabel + "_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryParseTileCoordSuffix(name, out int x, out int y) && IsCoordOutOfRange(x, y, tilesX, tilesY))
            {
                Log($"Deleting old TerrainData: {file} ({TileDisplayNameUtility.FormatTileReference(x, y)})");
                AssetDatabase.DeleteAsset(file);
            }
        }
    }

    private void DeleteOldPropDataAssets(string propsFolder, string terrainLabel, int tilesX, int tilesY)
    {
        if (!Directory.Exists(propsFolder))
            return;

        var files = Directory.GetFiles(propsFolder, "Props_*.asset", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);

            if (!name.StartsWith("Props_" + terrainLabel + "_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryParseTileCoordSuffix(name, out int x, out int y) && IsCoordOutOfRange(x, y, tilesX, tilesY))
            {
                Log($"Deleting old PropTileData: {file} ({TileDisplayNameUtility.FormatTileReference(x, y)})");
                AssetDatabase.DeleteAsset(file);
            }
        }
    }
    
    private HashSet<string> BuildGeneratedScenePathManifest(string scenesFolder, int tilesX, int tilesY)
    {
        var manifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int tx = 0; tx < tilesX; tx++)
        {
            for (int ty = 0; ty < tilesY; ty++)
            {
                string sceneName = ReplaceTokens(sceneNamePattern, tx, ty);
                string scenePath = $"{scenesFolder}/{sceneName}.unity".Replace("\\", "/");
                manifest.Add(scenePath);
            }
        }

        return manifest;
    }

    private static bool IsGeneratedTileSceneForTerrain(string scenePath, string terrainLabel)
    {
        if (string.IsNullOrWhiteSpace(scenePath) || string.IsNullOrWhiteSpace(terrainLabel))
            return false;

        string name = Path.GetFileNameWithoutExtension(scenePath);
        if (!name.Contains(terrainLabel, StringComparison.OrdinalIgnoreCase))
            return false;

        return TryParseTileCoordSuffix(name, out _, out _);
    }

    private static void RemoveOldBuildSettingsEntries(string terrainLabel, HashSet<string> currentGeneratedSceneManifest)
    {
        var list = EditorBuildSettings.scenes.ToList();
        bool changed = false;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var scene = list[i];
            string path = scene.path.Replace("\\", "/");

            if (!IsGeneratedTileSceneForTerrain(path, terrainLabel))
                continue;

            // Extract x,y
            if (currentGeneratedSceneManifest.Contains(path))
                continue;

            Debug.Log($"[TileSceneGenerator] Removing stale BuildSettings tile scene: {path}");
            list.RemoveAt(i);
            changed = true;
        }

        if (changed)
            EditorBuildSettings.scenes = list.ToArray();
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
            string safe = TileTerrainLabelUtility.ToLabel(t.name);
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

    // Compute a preview grid for an arbitrary TerrainData using current inspector options
    private void ComputePreviewGridFor(TerrainData td, out int nx, out int ny, out float finalX, out float finalY)
    {
        if (td == null) throw new ArgumentNullException(nameof(td));
        if (tileSizeMeters <= 0f) throw new ArgumentOutOfRangeException(nameof(tileSizeMeters));

        var sz = td.size;
        float desired = tileSizeMeters;

        if (!evenFitNoRemainder)
        {
            nx = Mathf.Max(1, Mathf.CeilToInt(sz.x / desired));
            ny = Mathf.Max(1, Mathf.CeilToInt(sz.z / desired));
            finalX = sz.x / nx;
            finalY = sz.z / ny;
            return;
        }

        nx = Mathf.Max(1, Mathf.RoundToInt(sz.x / desired));
        ny = Mathf.Max(1, Mathf.RoundToInt(sz.z / desired));
        finalX = sz.x / nx;
        finalY = sz.z / ny;

        if (forceSquareTiles)
        {
            float s = Mathf.Min(finalX, finalY);
            nx = Mathf.Max(1, Mathf.RoundToInt(sz.x / s));
            ny = Mathf.Max(1, Mathf.RoundToInt(sz.z / s));
            finalX = sz.x / nx;
            finalY = sz.z / ny;
        }
    }
    
    private void AutoFixTilesToDivisibleResolutionsIfNeeded(bool requireSquareTiles)
    {
        if (!autoFixDivisibility || _srcTD == null)
            return;

        int h = _srcTD.heightmapResolution - 1;
        int a = _srcTD.alphamapResolution;
        int d = _srcTD.detailResolution;

        int g = Gcd(Gcd(h, a), d);
        if (g <= 0)
            return;

        int oldX = tilesX;
        int oldY = tilesY;

        tilesX = PickBestDivisorCount(g, _srcTD.size.x, tileSizeMeters, requireSquareTiles: false);
        tilesY = PickBestDivisorCount(g, _srcTD.size.z, tileSizeMeters, requireSquareTiles: false);

        if (requireSquareTiles)
        {
            // For master: choose (tilesX, tilesY) pair that is divisible AND makes tiles as square as possible
            PickBestSquarePair(g, _srcTD.size.x, _srcTD.size.z, tileSizeMeters, out tilesX, out tilesY);
        }

        if (tilesX < 1) tilesX = 1;
        if (tilesY < 1) tilesY = 1;

        if (tilesX != oldX || tilesY != oldY)
        {
            float fx = _srcTD.size.x / tilesX;
            float fy = _srcTD.size.z / tilesY;

            _terrainLog.AppendLine(
                $"Auto-fix divisibility: {oldX}×{oldY} -> {tilesX}×{tilesY} " +
                $"(tile {fx:0.##}×{fy:0.##} m) for '{_currentTerrainLabel}'.");
        }
    }

    private static int PickBestDivisorCount(int g, float sizeMeters, float desiredTileMeters, bool requireSquareTiles)
    {
        // We want sizeMeters / n close to desiredTileMeters  =>  n close to sizeMeters / desiredTileMeters
        int target = Mathf.Max(1, Mathf.RoundToInt(sizeMeters / Mathf.Max(0.001f, desiredTileMeters)));

        int best = 1;
        float bestScore = float.PositiveInfinity;

        for (int n = 1; n <= g; n++)
        {
            if (g % n != 0)
                continue;

            float tile = sizeMeters / n;
            float score = Mathf.Abs(tile - desiredTileMeters) + 0.01f * Mathf.Abs(n - target);

            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }

        return best;
    }

    private static void PickBestSquarePair(int g, float sizeX, float sizeZ, float desired, out int bestX, out int bestY)
    {
        bestX = 1;
        bestY = 1;
        float bestScore = float.PositiveInfinity;

        // Iterate all divisor pairs of g
        for (int nx = 1; nx <= g; nx++)
        {
            if (g % nx != 0) continue;

            float tileX = sizeX / nx;

            for (int ny = 1; ny <= g; ny++)
            {
                if (g % ny != 0) continue;

                float tileZ = sizeZ / ny;

                float sizeError = Mathf.Abs(tileX - desired) + Mathf.Abs(tileZ - desired);
                float squarePenalty = Mathf.Abs(tileX - tileZ) * 2.0f; // push square tiles hard for master
                float score = sizeError + squarePenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestX = nx;
                    bestY = ny;
                }
            }
        }
    }

    // Compute tilesX/tilesY from meters for the CURRENT snapshot
    private void ComputeGridFromMeters()
    {
        if (_srcTD == null)
            throw new InvalidOperationException("Current Terrain has no TerrainData.");

        if (tileSizeMeters <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tileSizeMeters), "Tile Size (meters) must be > 0.");

        bool isMaster = IsMasterTerrain(_currentTerrainLabel);

        var sz = _srcTD.size;

        // Pick a tile count that is VALID for height/alpha/detail resolutions.
        // Master = square tiles (tilesX == tilesY). Others = independent X/Y.
        if (isMaster && masterForceSquareTiles)
        {
            int bestN = PickBestSquareTileCount(_srcTD, tileSizeMeters);
            tilesX = bestN;
            tilesY = bestN;
        }
        else
        {
            tilesX = PickBestTileCountForAxis(_srcTD, axisIsX: true,  desiredMeters: tileSizeMeters);
            tilesY = PickBestTileCountForAxis(_srcTD, axisIsX: false, desiredMeters: tileSizeMeters);
        }

        float finalX = sz.x / tilesX;
        float finalY = sz.z / tilesY;
        
        ValidateTileCountBudget(tilesX, tilesY);

        // Persist exact results (origin filled later)
        if (settings)
        {
            bool changed = true;
            if (settings.TryGet(_currentTerrainLabel, out var old))
            {
                changed =
                    !Mathf.Approximately(old.size.x, sz.x) ||
                    !Mathf.Approximately(old.size.z, sz.z) ||
                    old.tilesX != tilesX ||
                    old.tilesY != tilesY ||
                    !Mathf.Approximately(old.tileSizeX, finalX) ||
                    !Mathf.Approximately(old.tileSizeY, finalY);
            }

            if (changed)
            {
                settings.Upsert(_currentTerrainLabel, Vector3.zero, sz, tilesX, tilesY, finalX, finalY);
                EditorUtility.SetDirty(settings);
                _changedTerrains.Add(_currentTerrainLabel);

                _terrainLog.AppendLine($"Gizmo data updated for '{_currentTerrainLabel}' → {tilesX}×{tilesY} tiles @ {finalX:0.##}×{finalY:0.##} m each.");
                _gizmoStatus[_currentTerrainLabel] = "updated";
            }
            else
            {
                _terrainLog.AppendLine($"Gizmo data unchanged for '{_currentTerrainLabel}'.");
                _gizmoStatus[_currentTerrainLabel] = "unchanged";
            }
        }

        _terrainLog.AppendLine($"{_currentTerrainLabel}: {tilesX}×{tilesY} tiles, size {finalX:0.##}×{finalY:0.##} m (desired {tileSizeMeters:0.##}, master={isMaster}).");
    }
    
    private void ValidateTileCountBudget(int countX, int countY)
    {
        int axisLimit = Mathf.Max(1, maxTilesPerAxis);
        int totalLimit = Mathf.Max(1, maxTilesPerTerrain);

        if (countX > axisLimit || countY > axisLimit)
        {
            throw new InvalidOperationException(
                $"Terrain '{_currentTerrainLabel}' resolves to {countX}×{countY} tiles, which exceeds Max Tiles Per Axis ({axisLimit}). " +
                "Increase tile size or raise the safety limit in Advanced Options.");
        }

        long total = (long)countX * countY;
        if (total > totalLimit)
        {
            throw new InvalidOperationException(
                $"Terrain '{_currentTerrainLabel}' resolves to {countX}×{countY} = {total} tiles, which exceeds Max Tiles Per Terrain ({totalLimit}). " +
                "Increase tile size or raise the safety limit in Advanced Options.");
        }
    }
    
    private bool IsMasterTerrain(string label)
    {
        if (!string.IsNullOrWhiteSpace(masterTerrainNameContains) &&
            label.IndexOf(masterTerrainNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Fallback: if you have TileSliceSettings results, treat the centroid-closest as master.
        if (settings != null)
        {
            string detected = DetectMasterLabel(settings);
            if (!string.IsNullOrEmpty(detected) &&
                string.Equals(detected, label, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int PickBestSquareTileCount(TerrainData td, float desiredTileMeters)
    {
        var sz = td.size;

        // Target count based on meters (average axis), then we pick the best VALID divisor near it.
        float avg = (sz.x + sz.z) * 0.5f;
        int target = Mathf.Clamp(Mathf.RoundToInt(avg / desiredTileMeters), 1, 512);

        var valid = GetValidTileCounts(td);
        if (valid.Count == 0)
            throw new InvalidOperationException("No valid tile counts found for this terrain resolutions.");

        // Score = closeness to target, slight bias toward fewer tiles (faster)
        int best = valid[0];
        float bestScore = float.PositiveInfinity;

        foreach (int n in valid)
        {
            float score = Mathf.Abs(n - target) + (n * 0.002f);
            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }

        return best;
    }

    private static int PickBestTileCountForAxis(TerrainData td, bool axisIsX, float desiredMeters)
    {
        var sz = td.size;
        float axisMeters = axisIsX ? sz.x : sz.z;
        int target = Mathf.Clamp(Mathf.RoundToInt(axisMeters / desiredMeters), 1, 512);

        var valid = GetValidTileCounts(td);
        if (valid.Count == 0)
            throw new InvalidOperationException("No valid tile counts found for this terrain resolutions.");

        int best = valid[0];
        float bestScore = float.PositiveInfinity;

        foreach (int n in valid)
        {
            float tileMeters = axisMeters / n;
            float score = Mathf.Abs(tileMeters - desiredMeters) + (n * 0.002f);
            if (score < bestScore)
            {
                bestScore = score;
                best = n;
            }
        }

        return best;
    }

    private static List<int> GetValidTileCounts(TerrainData td)
    {
        int h = td.heightmapResolution - 1;
        int a = td.alphamapResolution;
        int d = td.detailResolution;

        // Valid tile counts are common divisors of all three.
        int g = Gcd(Gcd(h, a), d);
        if (g <= 0) return new List<int>();

        var divisors = new List<int>(64);
        for (int i = 1; i * i <= g; i++)
        {
            if (g % i != 0) continue;
            divisors.Add(i);
            int other = g / i;
            if (other != i) divisors.Add(other);
        }

        divisors.Sort();
        return divisors;
    }

    private static int Gcd(int a, int b)
    {
        a = Mathf.Abs(a);
        b = Mathf.Abs(b);
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return a;
    }


    private static bool TryGetKnownTerrainLabelSuffix(string rootPath, IEnumerable<TerrainSnapshot> snapshots, out string matchedLabel)
    {
        matchedLabel = null;
        string normalizedRoot = NormalizeAssetPath(rootPath);
        if (string.IsNullOrWhiteSpace(normalizedRoot))
            return false;

        string rootLeaf = Path.GetFileName(normalizedRoot);
        if (string.IsNullOrWhiteSpace(rootLeaf))
            return false;

        foreach (var snap in snapshots)
        {
            if (snap == null || string.IsNullOrWhiteSpace(snap.label))
                continue;

            if (string.Equals(rootLeaf, snap.label, StringComparison.OrdinalIgnoreCase))
            {
                matchedLabel = snap.label;
                return true;
            }
        }

        return false;
    }

    private void ValidateInputs(string effectiveOutputFolder)
    {
        if (_srcTD == null) throw new InvalidOperationException("Current Terrain has no TerrainData.");
        if (tilesX < 1 || tilesY < 1) throw new ArgumentOutOfRangeException(nameof(tilesX), "tilesX/tilesY must be ≥ 1.");
        if (string.IsNullOrWhiteSpace(sceneNamePattern) || (!sceneNamePattern.Contains("{tile}") && (!sceneNamePattern.Contains("{x}") || !sceneNamePattern.Contains("{y}"))))
            throw new InvalidOperationException("Scene Name Pattern must include either {tile}, or both {x} and {y}. You can also use {t} for terrain name.");
        if (string.IsNullOrWhiteSpace(effectiveOutputFolder) || !effectiveOutputFolder.StartsWith("Assets", StringComparison.Ordinal))
            throw new InvalidOperationException("Output folder must be under Assets/.");
        if (!IsSafeAssetFolderPath(effectiveOutputFolder))
            throw new InvalidOperationException($"Output folder path is invalid: '{effectiveOutputFolder}'. Use a valid Assets/... path without invalid characters.");

        string sampleTileSceneName = ReplaceTokens(NormalizeLegacySceneNamePattern(sceneNamePattern), 0, 0);
        if (string.IsNullOrWhiteSpace(sampleTileSceneName)
            || sampleTileSceneName.Contains('/')
            || sampleTileSceneName.Contains('\\')
            || sampleTileSceneName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Scene Name Pattern resolves to an invalid scene file name ('{sampleTileSceneName}'). Remove path separators and invalid filename characters.");
        }

        // Create root + two subfolders
        EnsureFolder(effectiveOutputFolder);

        string scenesFolder = Path.Combine(effectiveOutputFolder, "Scenes").Replace("\\", "/");
        string dataFolder   = Path.Combine(effectiveOutputFolder, "TerrainData").Replace("\\", "/");
        string propsFolder  = Path.Combine(effectiveOutputFolder, "Props").Replace("\\", "/");

        _outputScenesFolder = scenesFolder;
        _outputDataFolder   = dataFolder;
        _outputPropsFolder  = propsFolder;
        _outputWriteFoldersEnsured = false;
    }

    private void EnsureOutputWriteFolders()
    {
        if (_outputWriteFoldersEnsured)
            return;

        EnsureFolder(outputFolder);
        EnsureFolder(_outputScenesFolder);
        EnsureFolder(_outputDataFolder);
        EnsureFolder(_outputPropsFolder);
        _outputWriteFoldersEnsured = true;
    }

    private static void EnsureFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Folder path cannot be null or empty.", nameof(path));
        
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        var leaf = Path.GetFileName(path);
        
        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(leaf))
            throw new ArgumentException($"Invalid folder path: '{path}'.", nameof(path));
        
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
    
    private static bool IsSafeAssetFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = NormalizeAssetPath(path);
        if (!normalized.StartsWith("Assets", StringComparison.Ordinal))
            return false;

        if (normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        return normalized
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private string GetPropAssetPath(int tx, int ty)
    {
        return $"{_outputPropsFolder}/Props_{TileDisplayNameUtility.FormatTerrainTileLabel(_currentTerrainLabel, tx, ty)}.asset";
    }
    
    #region PropTileData Saving

    private static PropTileData LoadOrCreatePropTileData(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return null;

        var existing = AssetDatabase.LoadAssetAtPath<PropTileData>(assetPath);
        if (existing != null)
            return existing;

        EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace("\\", "/"));

        var created = ScriptableObject.CreateInstance<PropTileData>();
        AssetDatabase.CreateAsset(created, assetPath);
        AssetDatabase.SaveAssets();
        return created;
    }

    private static void SavePropTileDataAsset(PropTileData data)
    {
        if (data == null) return;
        EditorUtility.SetDirty(data);
        AssetDatabase.SaveAssets();
    }

    #endregion

    private void ValidateResolutionDivisibility()
    {
        if (_srcTD == null) return;

        RequireDivisible(_srcTD.heightmapResolution - 1, tilesX, "heightmap X");
        RequireDivisible(_srcTD.heightmapResolution - 1, tilesY, "heightmap Y");
        RequireDivisible(_srcTD.alphamapResolution, tilesX, "alphamap X");
        RequireDivisible(_srcTD.alphamapResolution, tilesY, "alphamap Y");
        RequireDivisible(_srcTD.detailResolution, tilesX, "detail X");
        RequireDivisible(_srcTD.detailResolution, tilesY, "detail Y");
    }

    private void RequireDivisible(int value, int divisor, string label)
    {
        if (divisor <= 0) throw new InvalidOperationException($"{label} divisor must be > 0");
        if (value < divisor) return;

        int remainder = value % divisor;
        if (remainder != 0)
        {
            throw new InvalidOperationException($"Terrain '{_currentTerrainLabel}' {label} ({value}) must divide evenly by tiles ({divisor}). Adjust tile size or terrain resolution to avoid seams.");
        }
    }
    
    private static void ClearConsole()
    {
        var logEntries = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
        var clearMethod = logEntries?.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        clearMethod?.Invoke(null, null);
    }
    
    private void BeginOperation(string operationName)
    {
        if (clearConsoleBeforeActions)
            ClearConsole();

        _runCounter++;
        _activeRunId = $"{DateTime.Now:HHmmss}-{_runCounter:000}";
        Log($"Starting {operationName}.");
    }

    private string WithPrefix(string message) => $"[TileSceneGenerator][run:{_activeRunId}] {message}";

    private void Log(string message) => Debug.Log(WithPrefix(message));
    private void LogWarning(string message) => Debug.LogWarning(WithPrefix(message));
    private void LogError(string message) => Debug.LogError(WithPrefix(message));

    // -------- core slicing (uses only cached data) --------
    private List<TileIndex.TileRecord> RunSliceOrReslice(Vector3 cachedOrigin, Vector2Int coordOffset = default)
    {
        ValidateResolutionDivisibility();

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
        int detailLayerCount = detailPrototypes?.Length ?? 0;
        
        TreePrototype[] treePrototypes = null;
        int[] treePrototypeRemap = null;
        if (copyTrees)
        {
            treePrototypes = SanitizeTreePrototypes(_srcTD.treePrototypes, out treePrototypeRemap, out int missingPrototypeCount);

            if (missingPrototypeCount > 0)
            {
                LogWarning(
                    $"Source terrain '{_currentTerrainLabel}' has {missingPrototypeCount} tree prototype(s) without prefabs. " +
                    "Tree instances using them will be skipped during slicing."
                );
            }

            if (treePrototypes == null || treePrototypes.Length == 0)
            {
                treePrototypes = null;
                LogWarning(
                    $"Source terrain '{_currentTerrainLabel}' does not contain any valid tree prefabs to copy."
                );
            }
        }

        bool canCopyTrees = copyTrees && (treePrototypes?.Length ?? 0) > 0;

        int total = tilesX * tilesY;
        int processed = 0;

        Scene masterScene = SceneManager.GetActiveScene();
        string originalScenePath = masterScene.path;
        PropCandidateCache propCandidateCache = null;

        if (copyProps)
        {
            var candidates = CollectPropCandidates(masterScene);
            propCandidateCache = new PropCandidateCache(candidates, cachedOrigin, tileSize, tilesX, tilesY);
        }

        try
        {
            List<TileIndex.TileRecord> tempIndexRecords = new List<TileIndex.TileRecord>();

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    float progress = processed / (float)total;
                    EditorUtility.DisplayProgressBar($"Tile Slice/Reslice [{_currentTerrainLabel}]",
                        $"Processing tile {TileDisplayNameUtility.FormatTileReference(tx, ty)}", progress);
                    
                    EnsureOutputWriteFolders();

                    // Build new TerrainData from the source master
                    TerrainData newTD = BuildTileTerrainData(
                        tx, ty, hStepX, hStepY, aStepX, aStepY, dStepX, dStepY,
                        layers, detailLayerCount, treePrototypes, treePrototypeRemap
                    );

                    // Save TerrainData under DATA folder
                    string tdPath = $"{_outputDataFolder}/{terrainDataPrefix}{TileDisplayNameUtility.FormatTerrainTileLabel(_currentTerrainLabel, tx, ty)}.asset";
                    var existingTD = AssetDatabase.LoadAssetAtPath<TerrainData>(tdPath);

                    bool heightsChanged = false, alphaChanged = false, detailsChanged = false, treesChanged = false;
                    int treesAdded = 0, treesRemoved = 0; bool treesModified = false;

                    if (existingTD != null)
                    {
                        if (copyHeights)
                            heightsChanged = !HeightsEqual(existingTD, newTD);

                        if (copyAlphamaps && _srcTD.alphamapLayers > 0)
                            alphaChanged = !AlphamapsEqual(existingTD, newTD);

                        if (copyDetails && (_srcTD.detailPrototypes?.Length ?? 0) > 0)
                            detailsChanged = !DetailsEqual(existingTD, newTD);

                        if (canCopyTrees)
                        {
                            bool equal = TreesEqualAndDeltas(existingTD, newTD,
                                out treesAdded, out treesRemoved, out bool modified);
                            treesChanged = !equal;
                            treesModified = modified;
                        }
                    }

                    _changedHeights |= heightsChanged;
                    _changedAlpha |= alphaChanged;
                    _changedDetails |= detailsChanged;
                    _changedTrees |= treesChanged;
                    if (treesAdded > 0) _treesAdded += treesAdded;
                    if (treesRemoved > 0) _treesRemoved += treesRemoved;
                    if (treesModified) _treesModifiedTiles += 1;

                    bool anyChannelChanged = heightsChanged || alphaChanged || detailsChanged || treesChanged;

                    if (existingTD == null)
                    {
                        SaveOrReplaceTerrainDataAsset(tdPath, newTD, null);
                    }
                    else
                    {
                        if (onlyUpdateIfChanged && !anyChannelChanged)
                        {
                            DestroyImmediate(newTD);
                            newTD = existingTD;
                        }
                        else
                        {
                            SaveOrReplaceTerrainDataAsset(tdPath, newTD, existingTD);
                        }
                    }

                    // Scene setup
                    string tileSceneName = ReplaceTokens(sceneNamePattern, tx, ty);
                    string tileScenePath = $"{_outputScenesFolder}/{tileSceneName}.unity";
                    bool sceneExists = File.Exists(tileScenePath);
                    string propAssetPath = copyProps ? GetPropAssetPath(tx, ty) : string.Empty;

                    if (nonDestructiveReslice && sceneExists)
                    {
                        var opened = EditorSceneManager.OpenScene(tileScenePath, OpenSceneMode.Additive);
                        try
                        {
                            var terrainGO = FindOrCreateTerrainGO(tx, ty, newTD, tileSize, cachedOrigin, opened);

                            if (copyProps)
                                CopyPropsIntoTileScene(opened, tx, ty, tileSize, cachedOrigin, propAssetPath, propCandidateCache);

                            EditorSceneManager.MarkSceneDirty(opened);
                            EditorSceneManager.SaveScene(opened);

                            // ---------------------------
                            // TILE INDEX RECORD (RESLICE)
                            // ---------------------------
                            Vector3 tileOrigin = cachedOrigin + new Vector3(
                                tx * tileSize.x,
                                0f,
                                ty * tileSize.z
                            );

                            float verticalExtent = Mathf.Max(tileSize.y * 0.5f, 4000f);
                            Vector3 boundsCenter = tileOrigin + new Vector3(tileSize.x * 0.5f, verticalExtent, tileSize.z * 0.5f);
                            Vector3 boundsSize = new Vector3(tileSize.x, verticalExtent * 2f, tileSize.z);

                            tempIndexRecords.Add(new TileIndex.TileRecord
                            {
                                coord = new Vector2Int(tx + coordOffset.x, ty + coordOffset.y),
                                scenePath = tileScenePath,
                                worldBounds = new Bounds(boundsCenter, boundsSize),
                                worldOrigin = tileOrigin,
                                tileSize = tileSize,
                                propRootName = TileRuntimeConstants.PropRootPrefix + tx + "_" + ty,
                                propDataPath = propAssetPath
                            });
                        }
                        finally
                        {
                            EditorSceneManager.CloseScene(opened, true);
                        }
                    }
                    else
                    {
                        // Fresh tile
                        var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                        newScene.name = tileSceneName;

                        var terrainGO = Terrain.CreateTerrainGameObject(newTD);
                        terrainGO.name = $"Terrain_{TileDisplayNameUtility.FormatTileReference(tx, ty)}";
                        PositionTerrainGO(terrainGO.transform, tx, ty, tileSize, cachedOrigin);

                        var col = terrainGO.GetComponent<TerrainCollider>();
                        if (col != null) col.enabled = true;

                        if (copyProps)
                            CopyPropsIntoTileScene(newScene, tx, ty, tileSize, cachedOrigin, propAssetPath, propCandidateCache);

                        EditorSceneManager.SaveScene(newScene, tileScenePath);
                        EditorSceneManager.CloseScene(newScene, true);

                        // ---------------------------
                        // TILE INDEX RECORD (FRESH)
                        // ---------------------------
                        Vector3 tileOrigin = cachedOrigin + new Vector3(
                            tx * tileSize.x,
                            0f,
                            ty * tileSize.z
                        );

                        float verticalExtent = Mathf.Max(tileSize.y * 0.5f, 4000f);
                        Vector3 boundsCenter = tileOrigin + new Vector3(tileSize.x * 0.5f, verticalExtent, tileSize.z * 0.5f);
                        Vector3 boundsSize = new Vector3(tileSize.x, verticalExtent * 2f, tileSize.z);

                        tempIndexRecords.Add(new TileIndex.TileRecord
                        {
                            coord = new Vector2Int(tx + coordOffset.x, ty + coordOffset.y),
                            scenePath = tileScenePath,
                            worldBounds = new Bounds(boundsCenter, boundsSize),
                            worldOrigin = tileOrigin,
                            tileSize = tileSize,
                            propRootName = TileRuntimeConstants.PropRootPrefix + tx + "_" + ty,
                            propDataPath = propAssetPath
                        });
                    }

                    if (addToBuildSettings)
                        EnsureInBuildSettings(tileScenePath);

                    processed++;
                }
            }

            AssetDatabase.Refresh();
            return tempIndexRecords;
        }
        finally
        {
            EditorUtility.ClearProgressBar();

            if (!string.IsNullOrEmpty(originalScenePath) && File.Exists(originalScenePath))
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }

    }
    
    private void WriteMergedTileIndex(
        List<TileIndex.TileRecord> mergedRecords,
        int expectedTileCount,
        Vector2? sharedTileSize2D,
        Vector2? sharedOriginOffset)
    {
        if (settings == null || settings.tileIndex == null)
            return;

        settings.tileIndex.SetTiles(mergedRecords);

        if (sharedTileSize2D.HasValue)
            settings.tileIndex.SetTileSizeMeters(sharedTileSize2D.Value);

        if (sharedOriginOffset.HasValue)
            settings.tileIndex.SetOriginOffset(sharedOriginOffset.Value);

        EditorUtility.SetDirty(settings.tileIndex);
        AssetDatabase.SaveAssets();

        int indexedTileCount = settings.tileIndex.Tiles.Count;
        if (indexedTileCount != expectedTileCount)
        {
            throw new InvalidOperationException(
                $"[TileSceneGenerator] TileIndex validation failed: expected {expectedTileCount} tile(s), " +
                $"but TileIndex contains {indexedTileCount}."
            );
        }
        
        int inconsistentBounds = settings.tileIndex.Tiles.Count(r => !TileIndex.IsCenterConsistentWithOrigin(r));
        if (inconsistentBounds > 0)
        {
            throw new InvalidOperationException(
                $"[TileSceneGenerator] TileIndex validation failed: {inconsistentBounds} tile(s) have mismatched worldBounds.center/worldOrigin/tileSize."
            );
        }

            var duplicatePaths = settings.tileIndex.Tiles
        .GroupBy(r => r.scenePath, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

        if (duplicatePaths.Count > 0)
        {
            throw new InvalidOperationException(
                $"[TileSceneGenerator] TileIndex validation failed: duplicate scene paths detected " +
                $"({duplicatePaths.Count}): {string.Join(", ", duplicatePaths)}\n" +
                "This usually means two terrains produced tiles with the same name. Check sceneNamePattern and subfolderPerTerrain settings."
            );
        }

        Debug.Log($"[TileSceneGenerator] TileIndex validation passed: {indexedTileCount}/{expectedTileCount} tiles indexed.");
    }

    private void CopyPropsIntoTileScene(Scene tileScene, int tx, int ty, Vector3 tileSize, Vector3 terrainOrigin, string propAssetPath, PropCandidateCache candidateCache)
    {
        string propRootName = TileRuntimeConstants.PropRootPrefix + tx + "_" + ty;
        Vector3 tileOrigin = new Vector3(
            terrainOrigin.x + tx * tileSize.x,
            terrainOrigin.y,
            terrainOrigin.z + ty * tileSize.z);

        RemoveExistingPropRoots(tileScene, propRootName);

        PropTileData data = null;
        if (copyProps && !string.IsNullOrWhiteSpace(propAssetPath))
        {
            data = LoadOrCreatePropTileData(propAssetPath);
            data.ResetForTile(new Vector2Int(tx, ty), tileOrigin, tileSize);
        }

        var propRoot = new GameObject(propRootName);
        propRoot.transform.position = tileOrigin;
        propRoot.transform.rotation = Quaternion.identity;
        propRoot.transform.localScale = Vector3.one;
        SceneManager.MoveGameObjectToScene(propRoot, tileScene);

        var candidates = candidateCache?.GetTileCandidates(tx, ty);
        if (candidates == null || candidates.Count == 0)
            return;

        float minX = tileOrigin.x;
        float maxX = minX + tileSize.x;
        float minZ = tileOrigin.z;
        float maxZ = minZ + tileSize.z;

        foreach (var go in candidates)
        {
            if (go == null) continue;

            Vector3 p = go.transform.position;
            if (p.x < minX || p.x >= maxX) continue;
            if (p.z < minZ || p.z >= maxZ) continue;

            GameObject prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(go);

            GameObject clone;
            if (prefabSource != null)
            {
                clone = (GameObject)PrefabUtility.InstantiatePrefab(prefabSource, tileScene);
            }
            else
            {
                clone = UnityEngine.Object.Instantiate(go);
                SceneManager.MoveGameObjectToScene(clone, tileScene);
            }

            if (clone == null) continue;

            clone.transform.SetParent(propRoot.transform, false);
            clone.transform.localPosition = p - tileOrigin;
            clone.transform.localRotation = go.transform.rotation;
            clone.transform.localScale = go.transform.lossyScale;

            if (!clone.GetComponent<TileProp>())
                clone.AddComponent<TileProp>();

            if (data != null && prefabSource != null)
            {
                data.AddInstance(
                    prefabSource,
                    clone.transform.localPosition,
                    clone.transform.localRotation,
                    clone.transform.localScale
                );
            }
        }

        if (data != null)
            SavePropTileDataAsset(data);
    }

    private static void RemoveExistingPropRoots(Scene tileScene, string propRootName)
    {
        var roots = tileScene.GetRootGameObjects();
        foreach (var root in roots)
        {
            if (root == null) continue;
            if (root.name == propRootName || root.name.StartsWith(TileRuntimeConstants.PropRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }

    private List<GameObject> CollectPropCandidates(Scene masterScene)
    {
        var results = new List<GameObject>(256);
        var seen = new HashSet<GameObject>();

        foreach (var root in masterScene.GetRootGameObjects())
        {
            if (root == null) continue;

            foreach (var marker in root.GetComponentsInChildren<TileProp>(true))
            {
                if (marker == null) continue;
                var candidate = PrefabUtility.GetOutermostPrefabInstanceRoot(marker.gameObject) ?? marker.gameObject;
                if (candidate != null && candidate.scene == masterScene && seen.Add(candidate))
                    results.Add(candidate);
            }

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                var outer = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
                if (outer != null && outer.scene == masterScene && seen.Add(outer))
                    results.Add(outer);
            }
        }

        return results;
    }

    private TerrainData BuildTileTerrainData(
    int tx, int ty,
    int hStepX, int hStepY, int aStepX, int aStepY, int dStepX, int dStepY,
    TerrainLayer[] layers, int detailLayerCount, TreePrototype[] treePrototypes, int[] treePrototypeRemap)
{
    var td = new TerrainData();

    // HEIGHTS
    int hW = hStepX + 1;
    int hH = hStepY + 1;
    int hResTile = Mathf.Max(hW, hH);
    td.heightmapResolution = hResTile;

    // ALPHAMAPS
    int aW = Mathf.Max(1, aStepX);
    int aH = Mathf.Max(1, aStepY);
    int aResTile = Mathf.Max(aW, aH);
    td.alphamapResolution = aResTile;

    // DETAILS
    int dW = Mathf.Max(1, dStepX);
    int dH = Mathf.Max(1, dStepY);
    int dResTile = Mathf.Max(dW, dH); 
    td.SetDetailResolution(dResTile, _srcTD.detailResolutionPerPatch);

    // size/layers
    var srcSize = _srcTD.size;
    var tileSize = new Vector3(srcSize.x / tilesX, srcSize.y, srcSize.z / tilesY);
    td.size = tileSize;
    td.terrainLayers = layers;
    td.detailPrototypes = _srcTD.detailPrototypes; 

    if (copyHeights)
    {
        int hX = tx * hStepX;
        int hY = ty * hStepY;
        var heights = _srcTD.GetHeights(hX, hY, hW, hH);
        td.SetHeights(0, 0, heights);
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
        var splats = _srcTD.GetAlphamaps(aX, aY, w, h);
        td.SetAlphamaps(0, 0, splats);
    }
    else if (layers != null && layers.Length > 0)
    {
        // Ensure tiles still render with the base layer even when splatmaps are skipped.
        int aTileRes = Mathf.Max(1, Mathf.RoundToInt(_srcTD.alphamapResolution / (float)tilesX));
        td.alphamapResolution = aTileRes;

        var fallback = new float[aTileRes, aTileRes, layers.Length];
        for (int x = 0; x < aTileRes; x++)
        {
            for (int y = 0; y < aTileRes; y++)
            {
                fallback[x, y, 0] = 1f;
            }
        }

        td.SetAlphamaps(0, 0, fallback);
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
                int mappedPrototypeIndex = nt.prototypeIndex;
                if (treePrototypeRemap != null && treePrototypeRemap.Length > 0)
                {
                    if (nt.prototypeIndex < 0 || nt.prototypeIndex >= treePrototypeRemap.Length)
                        continue;

                    mappedPrototypeIndex = treePrototypeRemap[nt.prototypeIndex];
                }

                if (mappedPrototypeIndex < 0 || mappedPrototypeIndex >= treePrototypes.Length)
                    continue;

                nt.prototypeIndex = mappedPrototypeIndex;
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
        string expectedName = $"Terrain_{TileDisplayNameUtility.FormatTileReference(tx, ty)}";
        string legacyName = $"Terrain_{tx}_{ty}";
        GameObject terrainGO = scene.GetRootGameObjects().FirstOrDefault(go =>
            go.name == expectedName ||
            go.name == legacyName);

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

        terrainGO.name = expectedName;
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
    
    private static TreePrototype[] SanitizeTreePrototypes(TreePrototype[] prototypes, out int[] remap, out int removedCount)
    {
        removedCount = 0;

        if (prototypes == null || prototypes.Length == 0)
        {
            remap = Array.Empty<int>();
            return Array.Empty<TreePrototype>();
        }

        var valid = new List<TreePrototype>(prototypes.Length);
        remap = new int[prototypes.Length];

        for (int i = 0; i < prototypes.Length; i++)
        {
            var proto = prototypes[i];
            if (proto != null && proto.prefab != null)
            {
                remap[i] = valid.Count;
                valid.Add(proto);
            }
            else
            {
                remap[i] = -1;
                removedCount++;
            }
        }

        if (valid.Count == prototypes.Length)
            return prototypes;

        return valid.ToArray();
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
    
    private static bool AlphamapsEqual(TerrainData a, TerrainData b)
    {
        try
        {
            if (a.alphamapResolution != b.alphamapResolution) return false;
            if (a.alphamapLayers != b.alphamapLayers) return false;

            int res = a.alphamapResolution;
            int layers = a.alphamapLayers;
            int step = Mathf.Max(1, res / 32); // sample grid (fast enough, accurate enough)

            for (int y = 0; y < res; y += step)
            {
                for (int x = 0; x < res; x += step)
                {
                    // Fetch a 1x1 sample (Unity returns [1,1,layers])
                    var A = a.GetAlphamaps(x, y, 1, 1);
                    var B = b.GetAlphamaps(x, y, 1, 1);
                    for (int l = 0; l < layers; l++)
                    {
                        if (!Mathf.Approximately(A[0, 0, l], B[0, 0, l]))
                            return false;
                    }
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static bool DetailsEqual(TerrainData a, TerrainData b)
    {
        try
        {
            int layers = Mathf.Min(a.detailPrototypes.Length, b.detailPrototypes.Length);
            if (layers == 0) return a.detailPrototypes.Length == b.detailPrototypes.Length
                                    && a.detailWidth == b.detailWidth && a.detailHeight == b.detailHeight;

            if (a.detailWidth != b.detailWidth || a.detailHeight != b.detailHeight) return false;

            int aw = a.detailWidth, ah = a.detailHeight;
            int stepX = Mathf.Max(1, aw / 32);
            int stepY = Mathf.Max(1, ah / 32);

            for (int l = 0; l < layers; l++)
            for (int y = 0; y < ah; y += stepY)
            for (int x = 0; x < aw; x += stepX)
                if (a.GetDetailLayer(x, y, 1, 1, l)[0,0] != b.GetDetailLayer(x, y, 1, 1, l)[0,0])
                    return false;

            // if one has extra layers beyond 'layers', count that as change
            return a.detailPrototypes.Length == b.detailPrototypes.Length;
        }
        catch { return false; }
    }

    // Compare tree instances; also compute deltas (added / removed / modified)
    private static bool TreesEqualAndDeltas(
        TerrainData a, TerrainData b,
        out int added, out int removed, out bool anyModifiedPositions)
    {
        added = removed = 0; anyModifiedPositions = false;

        try
        {
            var A = a.treeInstances ?? Array.Empty<TreeInstance>();
            var B = b.treeInstances ?? Array.Empty<TreeInstance>();

            // Fast path: different counts are changes
            if (A.Length != B.Length)
            {
                if (B.Length > A.Length) added = B.Length - A.Length;
                else removed = A.Length - B.Length;
                // We still check some positions where both have entries
                int n = Mathf.Min(A.Length, B.Length);
                for (int i = 0; i < n; i++)
                {
                    if (!ApproxTree(A[i], B[i])) anyModifiedPositions = true;
                }
                return false;
            }

            // Same count; check pairs (order is deterministic for our tiles)
            for (int i = 0; i < A.Length; i++)
            {
                if (!ApproxTree(A[i], B[i]))
                {
                    anyModifiedPositions = true;
                    return false;
                }
            }
            return true;
        }
        catch
        {
            anyModifiedPositions = true; // be conservative
            return false;
        }

        static bool ApproxTree(TreeInstance t1, TreeInstance t2)
        {
            if (t1.prototypeIndex != t2.prototypeIndex) return false;
            // Loose tolerance for positions inside tile-normalized [0..1] space
            const float tol = 1e-2f;
            if (Mathf.Abs(t1.position.x - t2.position.x) > tol) return false;
            if (Mathf.Abs(t1.position.z - t2.position.z) > tol) return false;
            // Height/width/color variations are usually OK differences -> ignore
            return true;
        }
    }

    private static string NormalizeLegacySceneNamePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "{t}_{tile}";

        return pattern
            .Replace("_Tile_{x}_{y}", "_{tile}", StringComparison.OrdinalIgnoreCase)
            .Replace("_{x}_{y}", "_{tile}", StringComparison.Ordinal);
    }

    private string ReplaceTokens(string pattern, int tx, int ty)
    {
        string normalizedPattern = NormalizeLegacySceneNamePattern(pattern);
        string tileRef = TileDisplayNameUtility.FormatTileReference(tx, ty);

        return normalizedPattern
            .Replace("{tile}", tileRef)
            .Replace("{x}", tx.ToString())
            .Replace("{y}", ty.ToString())
            .Replace("{t}", _currentTerrainLabel);
    }
    
    private void EnsureSettingsAsset()
    {
        var canonical = AssetDatabase.LoadAssetAtPath<TileSliceSettings>(CanonicalSettingsAssetPath);
        if (canonical != null)
        {
            settings = canonical;
            return;
        }

        var paths = FindAllSettingsAssetPaths();
        if (paths.Length > 0)
        {
            string sourcePath = paths[0];
            string moveError = AssetDatabase.MoveAsset(sourcePath, CanonicalSettingsAssetPath);
            if (string.IsNullOrEmpty(moveError))
            {
                settings = AssetDatabase.LoadAssetAtPath<TileSliceSettings>(CanonicalSettingsAssetPath);
                Debug.Log($"[TileSceneGenerator] Moved TileSliceSettings to canonical path: {CanonicalSettingsAssetPath}");
                AssetDatabase.SaveAssets();
                return;
            }

            settings = AssetDatabase.LoadAssetAtPath<TileSliceSettings>(sourcePath);
            Debug.LogWarning($"[TileSceneGenerator] Failed to move TileSliceSettings to canonical path ({moveError}). Using existing asset: {sourcePath}");
            return;
        }

        // Create a new asset at canonical path.
        settings = ScriptableObject.CreateInstance<TileSliceSettings>();
        AssetDatabase.CreateAsset(settings, CanonicalSettingsAssetPath);
        AssetDatabase.SaveAssets();
        Log($"Created new TileSliceSettings at: {CanonicalSettingsAssetPath}");
    }
    
    private void UpdateGridMetadataAsset(Vector2? sharedTileSize2D, Vector2? sharedOriginOffset)
    {
        if (settings == null)
            return;

        var metadata = EnsureGridMetadataAsset();
        if (metadata == null)
            return;

        var validLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in settings.lastResults)
        {
            if (result == null || string.IsNullOrWhiteSpace(result.label))
                continue;

            validLabels.Add(result.label);
            metadata.UpsertTerrain(
                result.label,
                result.origin,
                result.size,
                result.tilesX,
                result.tilesY,
                result.tileSizeX,
                result.tileSizeY
            );
        }

        metadata.RemoveMissingTerrains(validLabels);

        Vector2Int globalDimensions = ComputeGlobalGridDimensions(settings.lastResults);
        Vector2 resolvedTileSize = sharedTileSize2D ?? metadata.TileSizeXZ;
        Vector3 resolvedOrigin = sharedOriginOffset.HasValue
            ? new Vector3(sharedOriginOffset.Value.x, 0f, sharedOriginOffset.Value.y)
            : metadata.GridOriginWorld;

        metadata.SetGlobal(
            settings.tileIndex,
            resolvedOrigin,
            resolvedTileSize,
            globalDimensions,
            TileGridMetadata.CoordinateSystemDefinition.WorldXZ_FloorToInt_PositiveXEast_PositiveZNorth
        );

        EditorUtility.SetDirty(metadata);
    }

    private static Vector2Int ComputeGlobalGridDimensions(List<TileSliceSettings.PerTerrain> results)
    {
        if (results == null || results.Count == 0)
            return Vector2Int.one;

        int maxX = 1;
        int maxY = 1;

        foreach (var result in results)
        {
            if (result == null)
                continue;

            maxX = Mathf.Max(maxX, result.tilesX);
            maxY = Mathf.Max(maxY, result.tilesY);
        }

        return new Vector2Int(maxX, maxY);
    }

    private static TileGridMetadata EnsureGridMetadataAsset()
    {
        var canonical = AssetDatabase.LoadAssetAtPath<TileGridMetadata>(CanonicalGridMetadataAssetPath);
        if (canonical != null)
            return canonical;

        EnsureFolderHierarchy(Path.GetDirectoryName(CanonicalGridMetadataAssetPath));

        var metadata = ScriptableObject.CreateInstance<TileGridMetadata>();
        AssetDatabase.CreateAsset(metadata, CanonicalGridMetadataAssetPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[TileSceneGenerator] Created TileGridMetadata at: {CanonicalGridMetadataAssetPath}");
        return metadata;
    }

    private static void EnsureFolderHierarchy(string assetFolderPath)
    {
        if (string.IsNullOrWhiteSpace(assetFolderPath) || assetFolderPath == "Assets")
            return;

        string normalized = NormalizeAssetPath(assetFolderPath);
        if (AssetDatabase.IsValidFolder(normalized))
            return;

        string[] parts = normalized.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }

    private static IEnumerable<string> SortTerrainsByCompass(TileSliceSettings settings, string masterLabel)
    {
        if (settings == null) yield break;

        // Get the master terrain’s origin
        if (!settings.TryGet(masterLabel, out var master))
        {
            foreach (var kv in settings.lastResults)
                yield return kv.label;
            yield break;
        }

        var masterOrigin = master.origin;

        // Build list of all terrains with positions
        var all = new List<(string label, Vector3 origin)>();
        foreach (var kv in settings.lastResults)
            all.Add((kv.label, kv.origin));

        // Custom sort: primary north-south (Z descending), then east-west (X ascending)
        all.Sort((a, b) =>
        {
            // north is positive Z
            int zComp = -a.origin.z.CompareTo(b.origin.z); // larger Z first (north up)
            if (zComp != 0) return zComp;
            return a.origin.x.CompareTo(b.origin.x);       // smaller X first (west left)
        });

        // Master terrain first, then the rest in compass order
        yield return masterLabel;
        foreach (var kv in all)
        {
            if (kv.label != masterLabel)
                yield return kv.label;
        }
    }
    
    private bool IsMasterTerrainLabel(string label)
    {
        if (!string.IsNullOrWhiteSpace(masterTerrainLabelOverride))
            return string.Equals(label, masterTerrainLabelOverride, StringComparison.OrdinalIgnoreCase);

        // fallback: auto-detect via your existing centroid logic (requires settings)
        string detected = DetectMasterLabel(settings);
        return !string.IsNullOrWhiteSpace(detected) &&
               string.Equals(label, detected, StringComparison.OrdinalIgnoreCase);
    }
    
    private static string DetectMasterLabel(TileSliceSettings s)
    {
        if (s == null || s.lastResults == null || s.lastResults.Count == 0)
            return null;

        // centroid of all origins
        float cx = 0f, cz = 0f;
        foreach (var r in s.lastResults) { cx += r.origin.x; cz += r.origin.z; }
        cx /= s.lastResults.Count; cz /= s.lastResults.Count;

        // pick the label closest to centroid
        string best = null; float bestD2 = float.PositiveInfinity;
        foreach (var r in s.lastResults)
        {
            float dx = r.origin.x - cx, dz = r.origin.z - cz;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestD2) { bestD2 = d2; best = r.label; }
        }
        return best;
    }
    
    private static List<string> OrderLabelsClockwiseFromNorth(TileSliceSettings s, string masterLabel)
    {
        var ordered = new List<string>();
        if (s == null || s.lastResults == null || s.lastResults.Count == 0) return ordered;

        // Fallback to detected master if none provided
        if (string.IsNullOrEmpty(masterLabel))
            masterLabel = DetectMasterLabel(s);

        // Get master origin (if not found, just return in arbitrary order)
        if (!s.TryGet(masterLabel, out var master))
        {
            ordered.AddRange(s.lastResults.Select(r => r.label));
            return ordered;
        }

        var others = new List<(string label, float angle)>();
        foreach (var r in s.lastResults)
        {
            if (r.label == masterLabel) continue;

            // relative vector from master to this tile (X right, Z north)
            float rx = r.origin.x - master.origin.x;
            float rz = r.origin.z - master.origin.z;

            // Angle measured from +Z (north) rotating clockwise:
            // use atan2(x, z). Range (-π..π); normalize to (0..2π)
            float angle = Mathf.Atan2(rx, rz);       // 0 = north, π/2 = east, π = south, -π/2 = west
            if (angle < 0f) angle += 2f * Mathf.PI;  // normalize
            others.Add((r.label, angle));
        }

        // Sort by angle ascending: N -> NE -> E -> SE -> S -> SW -> W -> NW
        others.Sort((a, b) => a.angle.CompareTo(b.angle));

        ordered.Add(masterLabel);
        ordered.AddRange(others.Select(o => o.label));
        return ordered;
    }
    
    private static void RemoveMissingScenesFromBuildSettings(bool showPopup)
    {
        var list = EditorBuildSettings.scenes.ToList();
        bool changed = false;

        var removedPaths = new List<string>(32);

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var scene = list[i];

            if (!File.Exists(scene.path))
            {
                removedPaths.Add(scene.path);
                list.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
        {
            EditorBuildSettings.scenes = list.ToArray();

            // One log, grouped
            var sb = new StringBuilder(512);
            sb.AppendLine("[TileSceneGenerator] Clean Build Settings removed missing scenes:");
            foreach (var p in removedPaths)
                sb.AppendLine("  • " + p);
            Debug.Log(sb.ToString());

            if (showPopup)
                EditorUtility.DisplayDialog("Clean Build Settings",
                    $"Removed {removedPaths.Count} missing scene reference(s).", "OK");
        }
        else if (showPopup)
        {
            Debug.Log("Clean Build Settings: no missing scenes found.");
            EditorUtility.DisplayDialog("Clean Build Settings",
                "No missing scenes found.", "OK");
        }
    }
    
    private Bounds ComputeTileBounds(int tx, int ty, float tileSizeX, float tileSizeY, Vector3 masterOrigin)
    {
        // origin of this tile in world space
        Vector3 tileOrigin = new Vector3(
            masterOrigin.x + tx * tileSizeX,
            masterOrigin.y,
            masterOrigin.z + ty * tileSizeY
        );

        Vector3 center = new Vector3(
            tileOrigin.x + tileSizeX * 0.5f,
            masterOrigin.y + 2000f, // tall so SqrDistance always works
            tileOrigin.z + tileSizeY * 0.5f
        );

        Vector3 extents = new Vector3(
            tileSizeX * 0.5f,
            2000f,                   // tall vertical range
            tileSizeY * 0.5f
        );

        return new Bounds(center, extents * 2f);
    }
}
#endif
