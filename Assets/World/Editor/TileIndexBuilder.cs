#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class TileIndexBuilder
{
    private const string AssetPath = "Assets/World/Resources/TileIndex.asset";
    private const float DefaultVerticalPadding = 4000f;
    private const float OriginClusteringToleranceMeters = 0.05f;

    // Matches:
    // - <prefix>_Tile_<x>_<y>   (legacy)
    // - <prefix>_<x>_<y>        (legacy-ish)
    // - <prefix>_<tileRef>      (new) where tileRef is "A1"/"AA12" or "0_0"
    private static readonly Regex NamePattern = new(
        @"^(?<prefix>.+?)(?:_[Tt]ile)?_(?<tile>(?:[A-Za-z]+\d+|\-?\d+_\-?\d+))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private readonly struct TileBuildSource
    {
        public readonly Vector2Int Coord;
        public readonly string Path;
        public readonly string TerrainLabel;
        public readonly bool HasTerrain;
        public readonly Vector3 TerrainPosition;
        public readonly float TerrainHeight;

        public TileBuildSource(Vector2Int coord, string path, string terrainLabel, bool hasTerrain, Vector3 terrainPosition, float terrainHeight)
        {
            Coord = coord;
            Path = path;
            TerrainLabel = terrainLabel;
            HasTerrain = hasTerrain;
            TerrainPosition = terrainPosition;
            TerrainHeight = terrainHeight;
        }

        public Vector2 CandidateOriginOffset(Vector2 tileSize)
        {
            return new Vector2(TerrainPosition.x - Coord.x * tileSize.x, TerrainPosition.z - Coord.y * tileSize.y);
        }
    }

    [MenuItem("Tiles/Validate TileIndex")]
    public static void ValidateIndex()
    {
        var index = AssetDatabase.LoadAssetAtPath<TileIndex>(AssetPath);
        if (index == null)
        {
            Debug.LogError("[TileIndexBuilder] Cannot validate TileIndex because the asset does not exist.");
            return;
        }

        int failures = ValidateRecordConsistency(index.Tiles, true);

        if (failures == 0)
        {
            Debug.Log($"[TileIndexBuilder] Validation passed for {index.Tiles.Count} tile record(s).");
            return;
        }

        Debug.LogError($"[TileIndexBuilder] Validation failed with {failures} inconsistent tile record(s) out of {index.Tiles.Count}.");
    }

    [MenuItem("Tiles/Rebuild TileIndex (Auto)")]
    public static void Rebuild()
    {
        var guids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
            .Distinct()
            .ToArray();

        if (guids.Length == 0)
        {
            Debug.LogWarning("[TileIndexBuilder] No scenes with '*_Tile_*' found under Assets");
            return;
        }

        var index = AssetDatabase.LoadAssetAtPath<TileIndex>(AssetPath);
        if (index == null)
        {
            index = ScriptableObject.CreateInstance<TileIndex>();
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            AssetDatabase.CreateAsset(index, AssetPath);
        }

        var size = index.TileSizeMeters;
        if (size.x <= 0f || size.y <= 0f)
        {
            Debug.LogError("[TileIndexBuilder] TileSizeMeters must be set to valid >0 values on TileIndex");
            return;
        }

        var candidates = new List<(Vector2Int coord, string path, string label)>();
        int skippedName = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || Path.GetExtension(path) != ".unity")
                continue;

            var name = Path.GetFileNameWithoutExtension(path);
            var m = NamePattern.Match(name);
            if (!m.Success)
            {
                skippedName++;
                continue;
            }

            string label = m.Groups["prefix"].Value;
            string tileToken = m.Groups["tile"].Value;

            if (!TryParseTileToken(tileToken, out int x, out int y))
            {
                skippedName++;
                continue;
            }

            candidates.Add((new Vector2Int(x, y), path, label));
        }

        if (candidates.Count == 0)
        {
            Debug.LogWarning("[TileIndexBuilder] No scene names matched '*_Tile_<x>_<y>'");
            return;
        }

        var duplicatesByCoord = candidates
            .GroupBy(c => c.coord)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(v => v.path).OrderBy(p => p).ToList());

        if (duplicatesByCoord.Count > 0)
        {
            Debug.LogError(
                $"[TileIndexBuilder] Found {duplicatesByCoord.Count} duplicate tile coord groups, but TileIndex.NamespaceDuplicateCoordsByTerrainLabel is disabled. " +
                "Enable namespacing or normalize scenes to a single global coord grid before rebuilding.");

            foreach (var kv in duplicatesByCoord.Take(20))
                Debug.LogError($"[TileIndexBuilder] Duplicate coord {kv.Key} from: {string.Join(", ", kv.Value)}");

            if (duplicatesByCoord.Count > 20)
                Debug.LogError($"[TileIndexBuilder] ...and {duplicatesByCoord.Count - 20} more duplicate coord groups");

            return;
        }
        
        var selected = candidates
            .GroupBy(c => c.coord, (coord, group) =>
            {
                var preferred = group.OrderByDescending(v => v.path.Count(ch => ch == '/')).ThenBy(v => v.path).First();
                return (coord, preferred.path, preferred.label);
            })
            .ToList();

        var sources = new List<TileBuildSource>(selected.Count);
        
        foreach (var entry in selected)
        {
            using (new SceneLoadScope(entry.path))
            {
                var scene = SceneManager.GetSceneByPath(entry.path);
                var terrain = scene.GetRootGameObjects()
                    .SelectMany(go => go.GetComponentsInChildren<Terrain>(true))
                    .FirstOrDefault();

                if (terrain != null)
                {
                    float terrainHeight = terrain.terrainData != null ? terrain.terrainData.size.y : 0f;
                    sources.Add(new TileBuildSource(entry.coord, entry.path, entry.label, true, terrain.transform.position, terrainHeight));
                }
                else
                {
                    sources.Add(new TileBuildSource(entry.coord, entry.path, entry.label, false, Vector3.zero, 0f));
                }
            }
        }

        var originOffset = ResolveGlobalOriginOffset(sources, size, index.OriginOffsetMeters);
        index.SetOriginOffset(originOffset);

        var records = new List<TileIndex.TileRecord>(sources.Count);
        foreach (var source in sources)
        {
            float verticalExtent = Mathf.Max(size.x, DefaultVerticalPadding);
            if (source.HasTerrain)
                verticalExtent = Mathf.Max(verticalExtent, source.TerrainHeight * 0.5f);

            var tileSize = new Vector3(size.x, verticalExtent * 2f, size.y);
            var worldOrigin = new Vector3(source.Coord.x * size.x + originOffset.x, 0f, source.Coord.y * size.y + originOffset.y);
            var bounds = new Bounds(worldOrigin + tileSize * 0.5f, tileSize);

            string propDataPath = string.Empty;
            var sceneDir = Path.GetDirectoryName(source.Path)?.Replace("\\", "/");
            var rootDir = string.IsNullOrEmpty(sceneDir) ? string.Empty : Path.GetDirectoryName(sceneDir)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(rootDir))
            {
                string tileRef = TileDisplayNameUtility.FormatTileReference(source.Coord.x, source.Coord.y);
                string guess = Path.Combine(rootDir, "Props", $"Props_{source.TerrainLabel}_{tileRef}.asset").Replace("\\", "/");
                if (File.Exists(guess))
                    propDataPath = guess;
            }

            records.Add(new TileIndex.TileRecord
            {
                coord = source.Coord,
                terrainLabel = source.TerrainLabel,
                scenePath = source.Path,
                worldBounds = bounds,
                worldOrigin = worldOrigin,
                tileSize = tileSize,
                propRootName = TileRuntimeConstants.PropRootPrefix + source.Coord.x + "_" + source.Coord.y,
                propDataPath = propDataPath
            });
        }

        records = records.OrderBy(r => r.coord.x).ThenBy(r => r.coord.y).ToList();

        int inconsistentCount = ValidateRecordConsistency(records, true);
        if (inconsistentCount > 0)
            throw new System.InvalidOperationException($"[TileIndexBuilder] Generated {inconsistentCount} inconsistent tile record(s). Aborting save.");

        index.SetTiles(records);
        EditorUtility.SetDirty(index);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log(
            $"[TileIndexBuilder] Rebuilt TileIndex with {records.Count} tiles. " +
            $"Scanned scenes: {guids.Length}, matched names: {candidates.Count}, skipped (name): {skippedName}");
    }

    private static Vector2 ResolveGlobalOriginOffset(IEnumerable<TileBuildSource> sources, Vector2 tileSize, Vector2 fallback)
    {
        var offsets = sources.Where(s => s.HasTerrain).Select(s => s.CandidateOriginOffset(tileSize)).ToList();
        if (offsets.Count == 0)
            return fallback;

        var resolved = new Vector2(
            ResolveAxisOrigin(offsets.Select(v => v.x).ToList(), fallback.x),
            ResolveAxisOrigin(offsets.Select(v => v.y).ToList(), fallback.y));

        float toleranceSqr = OriginClusteringToleranceMeters * OriginClusteringToleranceMeters;
        int disagreements = offsets.Count(v => (v - resolved).sqrMagnitude > toleranceSqr);
        if (disagreements > 0)
            Debug.LogWarning($"[TileIndexBuilder] {disagreements} tile(s) disagree with resolved global origin {resolved} by more than {OriginClusteringToleranceMeters:0.###}m. Using deterministic median-derived origin.");

        return resolved;
    }

    private static float ResolveAxisOrigin(List<float> candidates, float fallback)
    {
        if (candidates == null || candidates.Count == 0)
            return fallback;

        candidates.Sort();
        int n = candidates.Count;
        if ((n & 1) == 1)
            return candidates[n / 2];

        return (candidates[(n / 2) - 1] + candidates[n / 2]) * 0.5f;
    }

    private static int ValidateRecordConsistency(IEnumerable<TileIndex.TileRecord> records, bool logFailures)
    {
        int failures = 0;
        foreach (var record in records)
        {
            if (TileIndex.IsCenterConsistentWithOrigin(record))
                continue;

            failures++;
            if (!logFailures)
                continue;

            var expected = record.worldOrigin + new Vector3(record.tileSize.x * 0.5f, record.tileSize.y * 0.5f, record.tileSize.z * 0.5f);
            Debug.LogError($"[TileIndexBuilder] Tile {record.coord} is inconsistent. center={record.worldBounds.center}, expected={expected}, origin={record.worldOrigin}, tileSize={record.tileSize}");
        }
        return failures;
    }
    
    private static bool TryParseTileToken(string token, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        int underscore = token.IndexOf('_');
        if (underscore > 0)
        {
            string xs = token.Substring(0, underscore);
            string ys = token.Substring(underscore + 1);
            return int.TryParse(xs, out x) && int.TryParse(ys, out y);
        }

        return TileDisplayNameUtility.TryParseTileReference(token, out x, out y);
    }
}

internal readonly struct SceneLoadScope : System.IDisposable
{
    private readonly Scene scene;

    public SceneLoadScope(string path)
    {
        scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
    }

    public void Dispose()
    {
        if (scene.IsValid())
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }
}
#endif
