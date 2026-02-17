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
    // UI / runtime guard
    private bool _isRunning = false;
    private Stopwatch _globalTimer;
    
    private readonly List<string> _changedTerrains = new List<string>();
    private readonly StringBuilder _terrainLog = new StringBuilder(256);
    
    // Per-terrain change trackers (reset per terrain)
    private bool _changedHeights, _changedAlpha, _changedDetails, _changedTrees;
    private int  _treesAdded, _treesRemoved, _treesModifiedTiles;
    
    // Per-run final summary (reset once per run)
    private readonly Dictionary<string, string> _finalContentSummary = new Dictionary<string, string>(32);
    private readonly Dictionary<string, string> _gizmoStatus = new Dictionary<string, string>(32);

    [SerializeField] private TileSliceSettings settings;
    [SerializeField] private string folder = "Assets/Level/Scenes/Tiles/";
    
    // Source (multi-terrain)
    [SerializeField] private bool autoCollectTerrains = true;
    [SerializeField] private string terrainNamePrefix = "Terrain_";
    [SerializeField] private Terrain[] sourceTerrains; // used when autoCollectTerrains = false

    // cached for current terrain
    private TerrainData _srcTD;
    private string _currentTerrainLabel = "";

    // Grid (meters)
    [SerializeField] private float tileSizeMeters = 250f;
    private int tilesX;
    private int tilesY;
    
    [SerializeField] private bool evenFitNoRemainder = true; // adjust size so terrain divides evenly
    [SerializeField] private bool forceSquareTiles   = true; // when even-fit, make tiles perfect squares

    // Output
    [SerializeField] private string sceneNamePattern = "{t}_Tile_{x}_{y}";
    [SerializeField] private string outputFolder = "Assets/Scenes/Tiles";
    [SerializeField] private string terrainDataPrefix = "TD_"; // saved as TD_<t>_<x>_<y>.asset
    [SerializeField] private bool subfolderPerTerrain = true;

    private string _outputScenesFolder;
    private string _outputDataFolder;
    private string _outputPropsFolder;

    // Copy Channels
    [SerializeField] private bool copyHeights = true;
    [SerializeField] private bool copyAlphamaps = true;
    [SerializeField] private bool copyDetails = true;
    [SerializeField] private bool copyTrees = true;
    [SerializeField] private bool copyProps = true;

    // Reslice Options
    [SerializeField] private bool nonDestructiveReslice = true; // update TerrainData in existing scenes, keep other objects
    [SerializeField] private bool onlyUpdateIfChanged = false; // small speed-up by skipping identical tiles (height-only compare)
    [SerializeField] private bool addToBuildSettings = true;

    
    
    // Snapshot type (do NOT hold Terrain refs while running)
    private sealed class TerrainSnapshot
    {
        public string label; // sanitized terrain name
        public TerrainData data; // stable asset ref
        public Vector3 origin; // cached world position
    }

    public bool IsRunning => _isRunning;

    public bool CanRun()
    {
        if (autoCollectTerrains) return (Terrain.activeTerrains?.Length ?? 0) > 0;
        return sourceTerrains != null && sourceTerrains.Any(t => t != null);
    }

    public void EnsureReadyForUi()
    {
        EnsureSettingsAsset();
    }

    public void AutoFillAndLogTerrains()
    {
        ClearConsole();
        var previewList = CollectSnapshots(onlySnapshotList: true);
        Debug.Log($"[TileSceneGenerator] Found {previewList.Count} terrain(s): {string.Join(", ", previewList.Select(s => s.label))}");
    }

    public string BuildPreviewText()
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

    public void RunSliceWithDialogs()
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
    

    public void CleanBuildSettingsWithDialog() => RemoveMissingScenesFromBuildSettings(true);
    public void RevealOutputFolder() => RevealGeneratedOutputFolder();
    public void DeleteGeneratedScenesWithDialog() => DeleteGeneratedTileScenesWithConfirmation();
    public void DeleteGeneratedAssetsWithDialog() => DeleteGeneratedTileAssetsWithConfirmation();
    public void DeleteAllGeneratedOutputWithDialog() => DeleteAllGeneratedOutputWithConfirmation();

    private const string LegacyTilesScenesRoot = "Assets/Level/Scenes/Tiles";
    private const string TileSceneNameRegex = ".*Tile.*";
    
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

        string legacyFolder = NormalizeAssetPath(folder);
        if (!string.IsNullOrWhiteSpace(legacyFolder) && legacyFolder.StartsWith("Assets", StringComparison.Ordinal))
            roots.Add(legacyFolder);

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

                if (!Regex.IsMatch(sceneName, TileSceneNameRegex, RegexOptions.IgnoreCase))
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

        // Matches: TD_Terrain_<anything>_<x>_<y>
        string tdRegex = $"^{escapedPrefix}Terrain_.+_\\d+_\\d+$";

        // Matches: Props_<anything>_<x>_<y>
        string propRegex = "^Props_.+_\\d+_\\d+$";

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
        string source = string.IsNullOrWhiteSpace(pattern) ? "{t}_Tile_{x}_{y}" : pattern;
        string escaped = Regex.Escape(source)
            .Replace("\\{t\\}", ".+")
            .Replace("\\{x\\}", "\\d+")
            .Replace("\\{y\\}", "\\d+");
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

        foreach (string folder in candidateFolders.Where(AssetDatabase.IsValidFolder))
        {
            bool hasTileScene = AssetDatabase.FindAssets("t:Scene", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Any(p =>
                {
                    string name = Path.GetFileNameWithoutExtension(p);
                    return !string.IsNullOrWhiteSpace(name) &&
                           Regex.IsMatch(name, TileSceneNameRegex, RegexOptions.IgnoreCase);
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
                    string tdRegex = $"^{escapedPrefix}Terrain_.+_\\d+_\\d+$";
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
        Debug.Log($"[TileSceneGenerator] Revealed tile output folder: {revealTarget}");
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

        Debug.Log($"[TileSceneGenerator] Cleanup complete. scenes deleted={sceneCount}");
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

        Debug.Log($"[TileSceneGenerator] Cleanup complete. assets deleted={terrainCount + otherAssetsCount}");
    }

    private void DeleteAllGeneratedOutputWithConfirmation()
    {
        var paths = CollectGeneratedOutputPaths();
        string[] folders = paths.GetAllFolders()
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
        Debug.Log($"[TileSceneGenerator] Cleanup complete. scenes deleted={sceneCount}, assets deleted={terrainCount + otherAssetsCount}, folders=[{string.Join(", ", folders)}]");
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

        Debug.Log(
            $"[TileSceneGenerator] {label}\n" +
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
        ClearConsole();
        _globalTimer = Stopwatch.StartNew(); // start total timer
        try
        {
            var snapshots = CollectSnapshots();
            if (snapshots.Count == 0)
                throw new InvalidOperationException("No valid terrains found. Check auto-collect prefix or assign terrains manually.");

            Debug.Log($"[TileSceneGenerator] Will process {snapshots.Count} terrain(s): {string.Join(", ", snapshots.Select(s => s.label))}");

            string originalOutputRoot = outputFolder;

            
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

                outputFolder = subfolderPerTerrain ? $"{originalOutputRoot}/{_currentTerrainLabel}" : originalOutputRoot;

                _gizmoStatus[_currentTerrainLabel] = settings ? "unchanged" : "n/a (no TileSliceSettings)";
                
                ComputeGridFromMeters();
                ValidateInputs();
                DeleteOldTileScenes(_outputScenesFolder, _currentTerrainLabel, tilesX, tilesY);
                DeleteOldTerrainDataAssets(_outputDataFolder, _currentTerrainLabel, terrainDataPrefix, tilesX, tilesY);
                DeleteOldPropDataAssets(_outputPropsFolder, _currentTerrainLabel, tilesX, tilesY);
                RemoveOldBuildSettingsEntries(_outputScenesFolder, _currentTerrainLabel, tilesX, tilesY);
                
                if (settings && settings.TryGet(_currentTerrainLabel, out var r))
                {
                    r.origin = cachedOrigin; 
                    EditorUtility.SetDirty(settings);
                }
                RunSliceOrReslice(cachedOrigin);

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
                Debug.Log(_terrainLog.ToString());

                // restore output root if you temporarily changed it
                outputFolder = originalOutputRoot;
            }
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
    
    private void DeleteOldTileScenes(string scenesFolder, string terrainLabel, int tilesX, int tilesY)
    {
        if (!Directory.Exists(scenesFolder))
            return;

        var files = Directory.GetFiles(scenesFolder, "*.unity", SearchOption.TopDirectoryOnly);

        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);

            // Accept both patterns:
            // {t}_Tile_{x}_{y} or custom but with tokens filled
            if (!name.Contains(terrainLabel)) 
                continue;

            // Try extract tile coords from the end of name
            var parts = name.Split('_');
            if (parts.Length < 2) 
                continue;

            if (int.TryParse(parts[^2], out int x) && int.TryParse(parts[^1], out int y))
            {
                // Out of range → delete scene
                if (x < 0 || y < 0 || x >= tilesX || y >= tilesY)
                {
                    Debug.Log($"[TileSceneGenerator] Deleting old tile scene: {file}");
                    AssetDatabase.DeleteAsset(file);
                }
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

            // Extract last two underscores: _x_y
            var parts = name.Split('_');
            if (parts.Length < 3)
                continue;

            if (int.TryParse(parts[^2], out int x) && int.TryParse(parts[^1], out int y))
            {
                if (x < 0 || y < 0 || x >= tilesX || y >= tilesY)
                {
                    Debug.Log($"[TileSceneGenerator] Deleting old TerrainData: {file}");
                    AssetDatabase.DeleteAsset(file);
                }
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

            var parts = name.Split('_');
            if (parts.Length < 4)
                continue;

            if (int.TryParse(parts[^2], out int x) && int.TryParse(parts[^1], out int y))
            {
                if (x < 0 || y < 0 || x >= tilesX || y >= tilesY)
                {
                    Debug.Log($"[TileSceneGenerator] Deleting old PropTileData: {file}");
                    AssetDatabase.DeleteAsset(file);
                }
            }
        }
    }
    
    private void RemoveOldBuildSettingsEntries(string scenesFolder, string terrainLabel, int tilesX, int tilesY)
    {
        var list = EditorBuildSettings.scenes.ToList();
        bool changed = false;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var s = list[i];

            if (!s.path.Contains(scenesFolder))
                continue;

            string name = Path.GetFileNameWithoutExtension(s.path);

            if (!name.Contains(terrainLabel))
                continue;

            // Extract x,y
            var parts = name.Split('_');
            if (parts.Length < 3)
                continue;

            if (int.TryParse(parts[^2], out int x) && int.TryParse(parts[^1], out int y))
            {
                if (x < 0 || y < 0 || x >= tilesX || y >= tilesY)
                {
                    Debug.Log($"[TileSceneGenerator] Removing old BuildSettings scene: {s.path}");
                    list.RemoveAt(i);
                    changed = true;
                }
            }
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
    
    // Compute tilesX/tilesY from meters for the CURRENT snapshot
    private void ComputeGridFromMeters()
    {
        if (_srcTD == null) throw new InvalidOperationException("Current Terrain has no TerrainData.");

        float desiredMeters = tileSizeMeters;
        bool evenFit = evenFitNoRemainder;
        bool forceSquares = forceSquareTiles;

        if (desiredMeters <= 0f) throw new ArgumentOutOfRangeException(nameof(desiredMeters), "Tile Size (meters) must be > 0.");

        var sz = _srcTD.size;

        int nx, ny;
        float finalX, finalY;

        if (!evenFit)
        {
            nx = Mathf.Max(1, Mathf.CeilToInt(sz.x / desiredMeters));
            ny = Mathf.Max(1, Mathf.CeilToInt(sz.z / desiredMeters));
            finalX = sz.x / nx;
            finalY = sz.z / ny;
        }
        else
        {
            nx = Mathf.Max(1, Mathf.RoundToInt(sz.x / desiredMeters));
            ny = Mathf.Max(1, Mathf.RoundToInt(sz.z / desiredMeters));
            finalX = sz.x / nx;
            finalY = sz.z / ny;

            if (forceSquares)
            {
                float s = Mathf.Min(finalX, finalY);
                nx = Mathf.Max(1, Mathf.RoundToInt(sz.x / s));
                ny = Mathf.Max(1, Mathf.RoundToInt(sz.z / s));
                finalX = sz.x / nx;
                finalY = sz.z / ny;
            }
        }
        tilesX = nx;
        tilesY = ny;

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
                    !Mathf.Approximately(old.tileSizeX, sz.x / tilesX) ||
                    !Mathf.Approximately(old.tileSizeY, sz.z / tilesY);
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
                _gizmoStatus[_currentTerrainLabel] = "unchanged"; // <-- important
            }
        }

        _terrainLog.AppendLine($"{_currentTerrainLabel}: {tilesX}×{tilesY} tiles, size {finalX:0.##}×{finalY:0.##} m (desired {desiredMeters:0.##}, evenFit={evenFit}, squares={forceSquares}).");
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
        string propsFolder  = Path.Combine(outputFolder, "Props").Replace("\\", "/");
        EnsureFolder(scenesFolder);
        EnsureFolder(dataFolder);
        EnsureFolder(propsFolder);

        _outputScenesFolder = scenesFolder;
        _outputDataFolder   = dataFolder;
        _outputPropsFolder  = propsFolder;
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

    private string GetPropAssetPath(int tx, int ty)
    {
        return $"{_outputPropsFolder}/Props_{_currentTerrainLabel}_{tx}_{ty}.asset";
    }

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

    // -------- core slicing (uses only cached data) --------
    private void RunSliceOrReslice(Vector3 cachedOrigin)
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
                Debug.LogWarning(
                    $"[TileSceneGenerator] Source terrain '{_currentTerrainLabel}' has {missingPrototypeCount} tree prototype(s) without prefabs. " +
                    "Tree instances using them will be skipped during slicing."
                );
            }

            if (treePrototypes == null || treePrototypes.Length == 0)
            {
                treePrototypes = null;
                Debug.LogWarning(
                    $"[TileSceneGenerator] Source terrain '{_currentTerrainLabel}' does not contain any valid tree prefabs to copy."
                );
            }
        }

        bool canCopyTrees = copyTrees && (treePrototypes?.Length ?? 0) > 0;

        int total = tilesX * tilesY;
        int processed = 0;

        Scene masterScene = SceneManager.GetActiveScene();
        string originalScenePath = masterScene.path;

        try
        {
            List<TileIndex.TileRecord> tempIndexRecords = new List<TileIndex.TileRecord>();

            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    float progress = processed / (float)total;
                    EditorUtility.DisplayProgressBar($"Tile Slice/Reslice [{_currentTerrainLabel}]",
                        $"Processing tile {tx},{ty}", progress);

                    // Build new TerrainData from the source master
                    TerrainData newTD = BuildTileTerrainData(
                        tx, ty, hStepX, hStepY, aStepX, aStepY, dStepX, dStepY,
                        layers, detailLayerCount, treePrototypes, treePrototypeRemap
                    );

                    // Save TerrainData under DATA folder
                    string tdPath = $"{_outputDataFolder}/{terrainDataPrefix}{_currentTerrainLabel}_{tx}_{ty}.asset";
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
                                CopyPropsIntoTileScene(masterScene, opened, tx, ty, tileSize, cachedOrigin, propAssetPath);

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
                                coord = new Vector2Int(tx, ty),
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
                        terrainGO.name = $"Terrain_{tx}_{ty}";
                        PositionTerrainGO(terrainGO.transform, tx, ty, tileSize, cachedOrigin);

                        var col = terrainGO.GetComponent<TerrainCollider>();
                        if (col != null) col.enabled = true;

                        if (copyProps)
                            CopyPropsIntoTileScene(masterScene, newScene, tx, ty, tileSize, cachedOrigin, propAssetPath);

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
                            coord = new Vector2Int(tx, ty),
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

            // Write TileIndex once all tiles are processed
            if (settings != null && settings.tileIndex != null)
            {
                var tileSize2D = new Vector2(tileSize.x, tileSize.z);
                settings.tileIndex.SetTileSizeMeters(tileSize2D);
                settings.tileIndex.SetOriginOffset(new Vector2(cachedOrigin.x, cachedOrigin.z));
                settings.tileIndex.SetTiles(tempIndexRecords);
                EditorUtility.SetDirty(settings.tileIndex);
                AssetDatabase.SaveAssets();
            }

            AssetDatabase.Refresh();
        }
        finally
        {
            EditorUtility.ClearProgressBar();

            if (!string.IsNullOrEmpty(originalScenePath) && File.Exists(originalScenePath))
                EditorSceneManager.OpenScene(originalScenePath, OpenSceneMode.Single);
        }

    }

    private void CopyPropsIntoTileScene(
        Scene masterScene,
        Scene tileScene,
        int tx,
        int ty,
        Vector3 tileSize,
        Vector3 terrainOrigin,
        string propAssetPath)
    {
        string propRootName = TileRuntimeConstants.PropRootPrefix + tx + "_" + ty;
        Vector3 tileOrigin = new Vector3(
            terrainOrigin.x + tx * tileSize.x,
            terrainOrigin.y,
            terrainOrigin.z + ty * tileSize.z);

        RemoveExistingPropRoots(tileScene, propRootName);

        var propRoot = new GameObject(propRootName);
        propRoot.transform.position = tileOrigin;
        propRoot.transform.rotation = Quaternion.identity;
        propRoot.transform.localScale = Vector3.one;
        SceneManager.MoveGameObjectToScene(propRoot, tileScene);

        var candidates = CollectPropCandidates(masterScene);
        var instanceData = new List<PropInstanceData>(Mathf.Max(4, candidates.Count));

        if (candidates.Count == 0)
        {
            SavePropTileDataAsset(propAssetPath, new Vector2Int(tx, ty), tileOrigin, tileSize, instanceData);
            return;
        }

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

            instanceData.Add(new PropInstanceData
            {
                prefab = prefabSource,
                localPosition = clone.transform.localPosition,
                localRotation = clone.transform.localRotation,
                localScale = clone.transform.localScale
            });
        }

        SavePropTileDataAsset(propAssetPath, new Vector2Int(tx, ty), tileOrigin, tileSize, instanceData);
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

    private void SavePropTileDataAsset(string assetPath, Vector2Int coord, Vector3 tileOrigin, Vector3 tileSize, List<PropInstanceData> instances)
    {
        if (string.IsNullOrEmpty(assetPath)) return;

        var data = AssetDatabase.LoadAssetAtPath<PropTileData>(assetPath);
        if (data == null)
        {
            data = ScriptableObject.CreateInstance<PropTileData>();
            AssetDatabase.CreateAsset(data, assetPath);
        }

        data.ResetForTile(coord, tileOrigin, tileSize);
        data.instances.AddRange(instances);
        EditorUtility.SetDirty(data);
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

    private string ReplaceTokens(string pattern, int tx, int ty)
    {
        return pattern
            .Replace("{x}", tx.ToString())
            .Replace("{y}", ty.ToString())
            .Replace("{t}", _currentTerrainLabel);
    }
    
    private void EnsureSettingsAsset()
    {
        if (settings != null) return;

        string folder = string.IsNullOrWhiteSpace(this.folder) ? "Assets/Scripts/Tiles" : this.folder;
        string defaultPath = $"{folder}/TileSliceSettings.asset";

        // Try to find an existing one first
        var guids = AssetDatabase.FindAssets("t:TileSliceSettings");
        if (guids != null && guids.Length > 0)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            settings = AssetDatabase.LoadAssetAtPath<TileSliceSettings>(path);
            Debug.Log($"[TileSceneGenerator] Auto-assigned existing TileSliceSettings at: {path}");
            return;
        }

        // Make sure the folder exists
        if (!AssetDatabase.IsValidFolder("Assets/Scripts"))
            AssetDatabase.CreateFolder("Assets", "Scripts");
        if (!AssetDatabase.IsValidFolder("Assets/Scripts/Tiles"))
            AssetDatabase.CreateFolder("Assets/Scripts", "Tiles");

        // Create a new asset there
        settings = ScriptableObject.CreateInstance<TileSliceSettings>();
        AssetDatabase.CreateAsset(settings, defaultPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[TileSceneGenerator] Created new TileSliceSettings at: {defaultPath}");
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
        int removed = 0;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var scene = list[i];

            if (!File.Exists(scene.path))
            {
                Debug.Log($"[TileSceneGenerator] Cleaned missing scene: {scene.path}");
                list.RemoveAt(i);
                removed++;
                changed = true;
            }
        }

        if (changed)
        {
            EditorBuildSettings.scenes = list.ToArray();
            if (showPopup)
                EditorUtility.DisplayDialog("Clean Build Settings",
                    $"Removed {removed} missing scene reference(s).", "OK");
        }
        else if (showPopup)
        {
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
