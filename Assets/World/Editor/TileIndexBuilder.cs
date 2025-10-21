#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class TileIndexBuilder
{
    private const string AssetPath = "Assets/World/Resources/TileIndex.asset";
    private static readonly Regex NamePattern = new(@"_Tile_(?<x>-?\d+)_(?<y>-?\d+)$", RegexOptions.Compiled);

    [MenuItem("Tiles/Rebuild TileIndex")] 
    public static void Rebuild()
    {
        var guids = AssetDatabase.FindAssets("t:Scene Tile_", new[] { "Assets" });
        if (guids == null || guids.Length == 0)
        {
            Debug.LogWarning("[TileIndexBuilder] No Tile_?_?.unity scenes found in project.");
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
        var records = new Dictionary<Vector2Int, TileIndex.TileRecord>();

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path) || Path.GetExtension(path) != ".unity")
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var match = NamePattern.Match(name);
            if (!match.Success)
            {
                Debug.LogWarning($"[TileIndexBuilder] Scene name '{name}' does not match Tile_x_y pattern.");
                continue;
            }

            int x = int.Parse(match.Groups["x"].Value);
            int y = int.Parse(match.Groups["y"].Value);
            var coord = new Vector2Int(x, y);

            var center = new Vector3(coord.x * size.x + size.x * 0.5f, 0f, coord.y * size.y + size.y * 0.5f);
            var bounds = new Bounds(center, new Vector3(size.x, 0f, size.y));

            var record = new TileIndex.TileRecord
            {
                coord = coord,
                scenePath = path,
                worldBounds = bounds
            };

            records[coord] = record;
        }

        if (records.Count == 0)
        {
            Debug.LogWarning("[TileIndexBuilder] No valid tile scenes found in project.");
            return;
        }

        var ordered = records.Values
            .OrderBy(r => r.coord.x)
            .ThenBy(r => r.coord.y)
            .ToList();

        index.SetTiles(ordered);
        EditorUtility.SetDirty(index);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[TileIndexBuilder] Rebuilt TileIndex with {ordered.Count} tiles.");
    }
}
#endif
