using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Tiles/Tile Index", fileName = "TileIndex")]
public class TileIndex : ScriptableObject
{
    public const float BoundsOriginToleranceMeters = 0.01f;
    
    [Serializable] 
    public struct TileRecord
    {
        public Vector2Int coord;
        public string terrainLabel;
        public string scenePath;
        public Bounds worldBounds;
        public Vector3 worldOrigin;
        public Vector3 tileSize;
        public string propRootName;
        public string propDataPath;
    }

    [SerializeField] private Vector2 tileSizeMeters = new(250f, 250f);
    [SerializeField] private Vector2 originOffsetMeters = Vector2.zero;
    [SerializeField] private bool namespaceDuplicateCoordsByTerrainLabel;
    
    [SerializeField] private List<TileRecord> tiles = new();

    private readonly Dictionary<Vector2Int, List<TileRecord>> coordLookup = new();
    private readonly Dictionary<string, TileRecord> pathLookup = new();

    public Vector2 TileSizeMeters => tileSizeMeters;
    public Vector2 OriginOffsetMeters => originOffsetMeters;
    public bool NamespaceDuplicateCoordsByTerrainLabel => namespaceDuplicateCoordsByTerrainLabel;
    public IReadOnlyList<TileRecord> Tiles => tiles;

    private void OnEnable()
    {
        RebuildLookups();
    }

    private void OnValidate()
    {
        RebuildLookups();
    }

    private void RebuildLookups()
    {
        coordLookup.Clear();
        pathLookup.Clear();

        foreach (var record in tiles)
        {
            if (record.scenePath == null)
            {
                continue;
            }

            if (!coordLookup.TryGetValue(record.coord, out var recordsForCoord))
            {
                recordsForCoord = new List<TileRecord>();
                coordLookup.Add(record.coord, recordsForCoord);
            }
            
            recordsForCoord.Add(record);

            if (!pathLookup.ContainsKey(record.scenePath))
            {
                pathLookup.Add(record.scenePath, record);
            }
            else
            {
                pathLookup[record.scenePath] = record;
            }
        }
    }

    public Vector3 GetTileOrigin(Vector2Int coord)
    {
        return new Vector3(
            coord.x * tileSizeMeters.x + originOffsetMeters.x,
            0f,
            coord.y * tileSizeMeters.y + originOffsetMeters.y
        );
    }

    public bool TryGetByCoord(Vector2Int coord, out TileRecord record)
    {
        if (coordLookup.TryGetValue(coord, out var records) && records.Count > 0)
        {
            record = records[0];
            return true;
        }

        record = default;
        return false;
    }

    public bool TryGetByCoordAndTerrain(Vector2Int coord, string terrainLabel, out TileRecord record)
    {
        if (coordLookup.TryGetValue(coord, out var records))
        {
            foreach (var candidate in records)
            {
                if (string.Equals(candidate.terrainLabel, terrainLabel, StringComparison.OrdinalIgnoreCase))
                {
                    record = candidate;
                    return true;
                }
            }
        }

        record = default;
        return false;
    }

    public bool TryGetByScene(string path, out TileRecord record)
    {
        if (string.IsNullOrEmpty(path))
        {
            record = default;
            return false;
        }

        return pathLookup.TryGetValue(path, out record);
    }

    public Vector2Int WorldToTile(Vector3 worldPos)
    {
        var sizeX = Mathf.Approximately(tileSizeMeters.x, 0f) ? 1f : tileSizeMeters.x;
        var sizeY = Mathf.Approximately(tileSizeMeters.y, 0f) ? 1f : tileSizeMeters.y;

        var offsetPos = new Vector3(worldPos.x - originOffsetMeters.x, worldPos.y, worldPos.z - originOffsetMeters.y);

        int x = Mathf.FloorToInt(offsetPos.x / sizeX);
        int y = Mathf.FloorToInt(offsetPos.z / sizeY);

        return new Vector2Int(x, y);
    }

    public IEnumerable<string> CoordsToSceneSet(Vector2Int center, int radius)
    {
        if (radius < 0)
        {
            yield break;
        }

        for (int dx = -radius; dx <= radius; ++dx)
        {
            for (int dy = -radius; dy <= radius; ++dy)
            {
                var coord = new Vector2Int(center.x + dx, center.y + dy);
                if (coordLookup.TryGetValue(coord, out var records))
                {
                    foreach (var record in records)
                    {
                        yield return record.scenePath;
                    }
                }
            }
        }
    }

    public static bool IsCenterConsistentWithOrigin(in TileRecord record, float tolerance = BoundsOriginToleranceMeters)
    {
        var expectedCenterX = record.worldOrigin.x + record.tileSize.x * 0.5f;
        var expectedCenterZ = record.worldOrigin.z + record.tileSize.z * 0.5f;
        var maxTolerance = Mathf.Max(0f, tolerance);

        return Mathf.Abs(record.worldBounds.center.x - expectedCenterX) <= maxTolerance
               && Mathf.Abs(record.worldBounds.center.z - expectedCenterZ) <= maxTolerance;
    }
    
#if UNITY_EDITOR
    public void SetOriginOffset(Vector2 newOriginOffset)
    {
        originOffsetMeters = newOriginOffset;
    }
    
    public void SetTiles(List<TileRecord> newTiles)
    {
        tiles = newTiles ?? new List<TileRecord>();
        RebuildLookups();
    }

    public void SetTileSizeMeters(Vector2 newTileSize)
    {
        tileSizeMeters = newTileSize;
    }
    
    public void SetNamespaceDuplicateCoordsByTerrainLabel(bool enabled)
    {
        namespaceDuplicateCoordsByTerrainLabel = enabled;
    }
#endif
}
