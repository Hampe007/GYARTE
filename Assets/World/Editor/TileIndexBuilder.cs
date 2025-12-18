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

    // Matches ..._Tile_<x>_<y> or ..._tile_<x>_<y>
    private static readonly Regex NamePattern = new(
        @"^(?<prefix>.+?)_[Tt]ile_(?<x>-?\d+)_(?<y>-?\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Prefer the deepest path when duplicate coords exist (e.g. multiple terrain sets)
    private static readonly bool PreferDeepestPath = true;

    [MenuItem("Tiles/Rebuild TileIndex (Auto)")]
    public static void Rebuild()
    {
        // Find all scenes whose name contains "_Tile_"
        var guids = AssetDatabase.FindAssets("t:Scene _Tile_", new[] { "Assets" })
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

        var size = index.TileSizeMeters; // expect x=z>0
        if (size.x <= 0f || size.y <= 0f)
        {
            Debug.LogError("[TileIndexBuilder] TileSizeMeters must be set to valid >0 values on TileIndex");
            return;
        }

        // Use a generous vertical size so bounds-based distance checks include flying cameras/players
        var boundsSize = new Vector3(size.x, Mathf.Max(size.x, DefaultVerticalPadding), size.y);

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

            int x = int.Parse(m.Groups["x"].Value);
            int y = int.Parse(m.Groups["y"].Value);
            string label = m.Groups["prefix"].Value;
            candidates.Add((new Vector2Int(x, y), path, label));
        }

        if (candidates.Count == 0)
        {
            Debug.LogWarning("[TileIndexBuilder] No scene names matched '*_Tile_<x>_<y>'");
            return;
        }

        // Resolve duplicates per coord
        var chosen = new Dictionary<Vector2Int, (string path, string label)>();
        var dups = new Dictionary<Vector2Int, List<string>>();

        foreach (var g in candidates.GroupBy(c => c.coord))
        {
            if (g.Count() == 1)
            {
                var single = g.First();
                chosen[g.Key] = (single.path, single.label);
                continue;
            }

            var paths = g.Select(v => v.path).ToList();
            string pick;

            if (PreferDeepestPath)
                pick = paths.OrderByDescending(p => p.Count(ch => ch == '/')).First(); // deeper = more specific
            else
                pick = paths.OrderBy(p => p).First(); // stable but shallow

            var chosenLabel = g.First(v => v.path == pick).label;
            chosen[g.Key] = (pick, chosenLabel);
            dups[g.Key] = paths;
        }

        // Build records
        var records = new List<TileIndex.TileRecord>(chosen.Count);
        var originOffset = Vector2.zero;
        bool originSet = false;
        
        foreach (var kv in chosen)
        {
            var coord = kv.Key;
            var path = kv.Value.path;
            var terrainLabel = kv.Value.label;
            
            Vector3 center;
            using (new SceneLoadScope(path))
            {
                var scene = SceneManager.GetSceneByPath(path);
                var terrain = scene.GetRootGameObjects()
                    .SelectMany(go => go.GetComponentsInChildren<Terrain>(true))
                    .FirstOrDefault();

                if (terrain != null)
                {
                    var pos = terrain.transform.position;
                    var candidateOrigin = new Vector2(pos.x - coord.x * size.x, pos.z - coord.y * size.y);

                    if (!originSet)
                    {
                        originOffset = candidateOrigin;
                        originSet = true;
                    }
                    else if ((originOffset - candidateOrigin).sqrMagnitude > 0.0001f)
                    {
                        Debug.LogWarning($"[TileIndexBuilder] Tile {coord} has origin offset {candidateOrigin} that differs from {originOffset}. Using first origin.");
                    }

                    center = pos + new Vector3(size.x * 0.5f, terrain.terrainData != null ? terrain.terrainData.size.y * 0.5f : 0f, size.y * 0.5f);
                }
                else
                {
                    center = new Vector3(coord.x * size.x + size.x * 0.5f, 0f, coord.y * size.y + size.y * 0.5f);
                }
            }

            var bounds = new Bounds(center, boundsSize);

            string propDataPath = string.Empty;
            var sceneDir = Path.GetDirectoryName(path)?.Replace("\\", "/");
            var rootDir = string.IsNullOrEmpty(sceneDir) ? string.Empty : Path.GetDirectoryName(sceneDir)?.Replace("\\", "/");
            if (!string.IsNullOrEmpty(rootDir))
            {
                string guess = Path.Combine(rootDir, "Props", $"Props_{terrainLabel}_{coord.x}_{coord.y}.asset").Replace("\\", "/");
                if (File.Exists(guess))
                {
                    propDataPath = guess;
                }
            }

            records.Add(new TileIndex.TileRecord
            {
                coord = coord,
                scenePath = path,
                worldBounds = bounds,
                worldOrigin = new Vector3(coord.x * size.x + originOffset.x, 0f, coord.y * size.y + originOffset.y),
                tileSize = new Vector3(size.x, boundsSize.y, size.y),
                propRootName = TileRuntimeConstants.PropRootPrefix + coord.x + "_" + coord.y,
                propDataPath = propDataPath
            });
        }

        // Sort for deterministic output
        records = records
            .OrderBy(r => r.coord.x)
            .ThenBy(r => r.coord.y)
            .ToList();
        
        if (originSet)
        {
            index.SetOriginOffset(originOffset);
        }

        index.SetTiles(records);
        EditorUtility.SetDirty(index);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Log summary
        var dupCount = dups.Count;
        var totalScenes = guids.Length;
        Debug.Log(
            $"[TileIndexBuilder] Rebuilt TileIndex with {records.Count} tiles. " +
            $"Scanned scenes: {totalScenes}, matched names: {candidates.Count}, skipped (name): {skippedName}, duplicate coords: {dupCount}");

        if (dupCount > 0)
        {
            foreach (var kv in dups.Take(20)) // cap spam
            {
                var list = string.Join(", ", kv.Value.Select(p => p));
                Debug.LogWarning($"[TileIndexBuilder] Duplicate coord {kv.Key} from: {list}. Chosen: {chosen[kv.Key]}");
            }
            if (dupCount > 20) Debug.LogWarning($"[TileIndexBuilder] ...and {dupCount - 20} more duplicate coord groups");
        }
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
