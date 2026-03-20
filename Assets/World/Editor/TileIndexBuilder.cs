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
        public readonly Vector3 TerrainSize;

        public TileBuildSource(Vector2Int coord, string path, string terrainLabel, bool hasTerrain, Vector3 terrainPosition, Vector3 terrainSize)
        {
            Coord = coord;
            Path = path;
            TerrainLabel = terrainLabel;
            HasTerrain = hasTerrain;
            TerrainPosition = terrainPosition;
            TerrainSize = terrainSize;
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

        var duplicateCoordAndLabel = candidates
            .GroupBy(c => (c.coord, label: c.label), (key, group) => new
            {
                key.coord,
                key.label,
                Paths = group.Select(v => v.path).OrderBy(p => p).ToList()
            })
            .Where(g => g.Paths.Count > 1)
            .ToList();

        if (duplicateCoordAndLabel.Count > 0)
        {
            Debug.LogError(
                $"[TileIndexBuilder] Found {duplicateCoordAndLabel.Count} duplicate tile coord+terrain groups. " +
                "Each terrain label can have only one scene per coord.");

            foreach (var duplicate in duplicateCoordAndLabel.Take(20))
                Debug.LogError($"[TileIndexBuilder] Duplicate coord {duplicate.coord} for terrain '{duplicate.label}' from: {string.Join(", ", duplicate.Paths)}");

            if (duplicateCoordAndLabel.Count > 20)
                Debug.LogError($"[TileIndexBuilder] ...and {duplicateCoordAndLabel.Count - 20} more duplicate coord+terrain groups");

            return;
        }
        
        var selected = candidates
            .GroupBy(c => (c.coord, c.label), (key, group) =>
            {
                var preferred = group.OrderByDescending(v => v.path.Count(ch => ch == '/')).ThenBy(v => v.path).First();
                return (key.coord, preferred.path, preferred.label);
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
                    Vector3 terrainSize = terrain.terrainData != null ? terrain.terrainData.size : new Vector3(size.x, 0f, size.y);
                    sources.Add(new TileBuildSource(entry.coord, entry.path, entry.label, true, terrain.transform.position, terrainSize));
                }
                else
                {
                    sources.Add(new TileBuildSource(entry.coord, entry.path, entry.label, false, Vector3.zero, new Vector3(size.x, 0f, size.y)));
                }
            }
        }

        var originOffset = ResolveGlobalOriginOffset(sources, size, index.OriginOffsetMeters);
        index.SetOriginOffset(originOffset);

        var records = new List<TileIndex.TileRecord>(sources.Count);
        foreach (var source in sources)
        {
            Vector3 terrainSize = source.HasTerrain ? source.TerrainSize : new Vector3(size.x, 0f, size.y);
            float verticalExtent = Mathf.Max(Mathf.Max(terrainSize.x, terrainSize.z), DefaultVerticalPadding);
            verticalExtent = Mathf.Max(verticalExtent, terrainSize.y * 0.5f);

            var tileSize = new Vector3(Mathf.Max(1f, terrainSize.x), verticalExtent * 2f, Mathf.Max(1f, terrainSize.z));
            var worldOrigin = source.HasTerrain
                ? new Vector3(source.TerrainPosition.x, 0f, source.TerrainPosition.z)
                : new Vector3(source.Coord.x * size.x + originOffset.x, 0f, source.Coord.y * size.y + originOffset.y);
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
        var terrainSources = sources.Where(s => s.HasTerrain).ToList();
        if (terrainSources.Count == 0)
            return fallback;

        float minX = terrainSources.Min(s => s.TerrainPosition.x);
        float minZ = terrainSources.Min(s => s.TerrainPosition.z);
        var resolved = new Vector2(minX, minZ);

        var offsets = terrainSources.Select(s => s.CandidateOriginOffset(tileSize)).ToList();
        float toleranceSqr = OriginClusteringToleranceMeters * OriginClusteringToleranceMeters;
        int disagreements = offsets.Count(v => (v - resolved).sqrMagnitude > toleranceSqr);
        if (disagreements > 0)
            Debug.LogWarning($"[TileIndexBuilder] Legacy global origin offsets disagree across {disagreements} tile(s); using world-min origin {resolved} for debug/reference output while per-record bounds drive streaming.");

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
