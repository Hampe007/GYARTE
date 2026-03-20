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
    [SerializeField] private List<TileRecord> tiles = new();

    private readonly Dictionary<Vector2Int, List<TileRecord>> coordLookup = new();
    private readonly Dictionary<string, TileRecord> pathLookup = new();
    private readonly Dictionary<Vector2Int, Bounds> coordBoundsLookup = new();

    public Vector2 TileSizeMeters => tileSizeMeters;
    public Vector2 OriginOffsetMeters => originOffsetMeters;
    public IReadOnlyList<TileRecord> Tiles => tiles;

    private void OnEnable() => RebuildLookups();
    private void OnValidate() => RebuildLookups();

    private void RebuildLookups()
    {
        coordLookup.Clear();
        pathLookup.Clear();
        coordBoundsLookup.Clear();

        foreach (var record in tiles)
        {
            if (string.IsNullOrEmpty(record.scenePath))
                continue;

            if (!coordLookup.TryGetValue(record.coord, out var recordsForCoord))
            {
                recordsForCoord = new List<TileRecord>();
                coordLookup.Add(record.coord, recordsForCoord);
            }

            recordsForCoord.Add(record);
            pathLookup[record.scenePath] = record;

            if (coordBoundsLookup.TryGetValue(record.coord, out var existingBounds))
            {
                existingBounds.Encapsulate(record.worldBounds.min);
                existingBounds.Encapsulate(record.worldBounds.max);
                coordBoundsLookup[record.coord] = existingBounds;
            }
            else
            {
                coordBoundsLookup.Add(record.coord, record.worldBounds);
            }
        }
    }

    public Vector3 GetTileOrigin(Vector2Int coord)
    {
        if (coordBoundsLookup.TryGetValue(coord, out var bounds))
            return new Vector3(bounds.min.x, 0f, bounds.min.z);

        return new Vector3(
            coord.x * tileSizeMeters.x + originOffsetMeters.x,
            0f,
            coord.y * tileSizeMeters.y + originOffsetMeters.y);
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

    public bool TryGetCoordBounds(Vector2Int coord, out Bounds bounds)
        => coordBoundsLookup.TryGetValue(coord, out bounds);

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
        if (TryGetCoordAtWorldPosition(worldPos, out var coord))
            return coord;

        if (TryGetNearestCoord(worldPos, out coord))
            return coord;

        var sizeX = Mathf.Approximately(tileSizeMeters.x, 0f) ? 1f : tileSizeMeters.x;
        var sizeY = Mathf.Approximately(tileSizeMeters.y, 0f) ? 1f : tileSizeMeters.y;
        var offsetPos = new Vector3(worldPos.x - originOffsetMeters.x, worldPos.y, worldPos.z - originOffsetMeters.y);
        return new Vector2Int(Mathf.FloorToInt(offsetPos.x / sizeX), Mathf.FloorToInt(offsetPos.z / sizeY));
    }

    public bool TryGetCoordAtWorldPosition(Vector3 worldPos, out Vector2Int coord)
    {
        foreach (var pair in coordBoundsLookup)
        {
            var bounds = pair.Value;
            if (worldPos.x < bounds.min.x || worldPos.x > bounds.max.x || worldPos.z < bounds.min.z || worldPos.z > bounds.max.z)
                continue;

            coord = pair.Key;
            return true;
        }

        coord = default;
        return false;
    }

    public bool TryGetNearestCoord(Vector3 worldPos, out Vector2Int coord)
    {
        float bestDistance = float.PositiveInfinity;
        coord = default;
        bool found = false;

        foreach (var pair in coordBoundsLookup)
        {
            float distance = pair.Value.SqrDistance(worldPos);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            coord = pair.Key;
            found = true;
        }

        return found;
    }

    public IEnumerable<TileRecord> GetRecordsForCoord(Vector2Int coord)
    {
        if (!coordLookup.TryGetValue(coord, out var records))
            yield break;

        for (int i = 0; i < records.Count; i++)
            yield return records[i];
    }

    public IEnumerable<string> CoordsToSceneSet(Vector2Int center, int radius)
    {
        if (radius < 0)
            yield break;

        for (int dx = -radius; dx <= radius; ++dx)
        {
            for (int dy = -radius; dy <= radius; ++dy)
            {
                foreach (var record in GetRecordsForCoord(new Vector2Int(center.x + dx, center.y + dy)))
                    yield return record.scenePath;
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
    public void SetOriginOffset(Vector2 newOriginOffset) => originOffsetMeters = newOriginOffset;

    public void SetTiles(List<TileRecord> newTiles)
    {
        tiles = newTiles ?? new List<TileRecord>();
        RebuildLookups();
    }

    public void SetTileSizeMeters(Vector2 newTileSize) => tileSizeMeters = newTileSize;
#endif
}
