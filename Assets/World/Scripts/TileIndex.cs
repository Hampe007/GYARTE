using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Tiles/Tile Index", fileName = "TileIndex")]
public class TileIndex : ScriptableObject
{
    [Serializable] 
    public struct TileRecord
    {
        public Vector2Int coord;
        public string scenePath;
        public Bounds worldBounds;
    }

    [SerializeField] private Vector2 tileSizeMeters = new(250f, 250f);
    [SerializeField] private Vector2 originOffsetMeters = Vector2.zero;
    
    [SerializeField] private List<TileRecord> tiles = new();

    private readonly Dictionary<Vector2Int, TileRecord> coordLookup = new();
    private readonly Dictionary<string, TileRecord> pathLookup = new();

    public Vector2 TileSizeMeters => tileSizeMeters;
    public Vector2 OriginOffsetMeters => originOffsetMeters;
    
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

            if (!coordLookup.ContainsKey(record.coord))
            {
                coordLookup.Add(record.coord, record);
            }
            else
            {
                coordLookup[record.coord] = record;
            }

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

    public bool TryGetByCoord(Vector2Int coord, out TileRecord record)
    {
        return coordLookup.TryGetValue(coord, out record);
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
                if (coordLookup.TryGetValue(coord, out var record))
                {
                    yield return record.scenePath;
                }
            }
        }
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
#endif
}