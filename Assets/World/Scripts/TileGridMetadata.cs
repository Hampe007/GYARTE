using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Tiles/Tile Grid Metadata", fileName = "TileGridMetadata")]
public sealed class TileGridMetadata : ScriptableObject
{
    public const string ResourcesAssetName = "TileGridMetadata";

    public enum CoordinateSystemDefinition
    {
        WorldXZ_FloorToInt_PositiveXEast_PositiveZNorth = 0
    }

    [Serializable]
    public sealed class TerrainGridRecord
    {
        public string label;
        public Vector3 origin;
        public Vector2 tileSizeXZ;
        public Vector2Int gridDimensions;
        public Vector3 terrainSize;
    }

    [SerializeField] private TileIndex tileIndex;
    [SerializeField] private Vector3 gridOriginWorld;
    [SerializeField] private Vector2 tileSizeXZ = new(250f, 250f);
    [SerializeField] private Vector2Int gridDimensions = Vector2Int.one;
    [SerializeField] private CoordinateSystemDefinition coordinateSystem = CoordinateSystemDefinition.WorldXZ_FloorToInt_PositiveXEast_PositiveZNorth;
    [SerializeField] private List<TerrainGridRecord> terrains = new();

    private readonly Dictionary<string, TerrainGridRecord> terrainLookup = new(StringComparer.OrdinalIgnoreCase);

    public TileIndex TileIndex => tileIndex;
    public Vector3 GridOriginWorld => gridOriginWorld;
    public Vector2 TileSizeXZ => tileSizeXZ;
    public Vector2Int GridDimensions => gridDimensions;
    public CoordinateSystemDefinition CoordinateSystem => coordinateSystem;
    public IReadOnlyList<TerrainGridRecord> Terrains => terrains;

    private void OnEnable() => RebuildLookup();
    private void OnValidate() => RebuildLookup();

    private void RebuildLookup()
    {
        terrainLookup.Clear();
        for (int i = 0; i < terrains.Count; i++)
        {
            var record = terrains[i];
            if (record == null || string.IsNullOrWhiteSpace(record.label))
                continue;

            terrainLookup[record.label] = record;
        }
    }

    public bool TryGetTerrain(string label, out TerrainGridRecord record)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            record = null;
            return false;
        }

        return terrainLookup.TryGetValue(label, out record) && record != null;
    }

#if UNITY_EDITOR
    public void SetGlobal(TileIndex sourceTileIndex, Vector3 newOriginWorld, Vector2 newTileSizeXZ, Vector2Int newGridDimensions, CoordinateSystemDefinition newCoordinateSystem)
    {
        tileIndex = sourceTileIndex;
        gridOriginWorld = newOriginWorld;
        tileSizeXZ = new Vector2(Mathf.Max(0f, newTileSizeXZ.x), Mathf.Max(0f, newTileSizeXZ.y));
        gridDimensions = new Vector2Int(Mathf.Max(1, newGridDimensions.x), Mathf.Max(1, newGridDimensions.y));
        coordinateSystem = newCoordinateSystem;
    }

    public void UpsertTerrain(string label, Vector3 origin, Vector3 terrainSizeMeters, int tilesX, int tilesY, float tileSizeX, float tileSizeY)
    {
        if (string.IsNullOrWhiteSpace(label))
            return;

        int index = terrains.FindIndex(t => string.Equals(t.label, label, StringComparison.OrdinalIgnoreCase));
        var record = new TerrainGridRecord
        {
            label = label,
            origin = origin,
            terrainSize = terrainSizeMeters,
            gridDimensions = new Vector2Int(Mathf.Max(1, tilesX), Mathf.Max(1, tilesY)),
            tileSizeXZ = new Vector2(Mathf.Max(0f, tileSizeX), Mathf.Max(0f, tileSizeY))
        };

        if (index >= 0) terrains[index] = record;
        else terrains.Add(record);

        RebuildLookup();
    }

    public void RemoveMissingTerrains(ICollection<string> validLabels)
    {
        terrains.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.label) || validLabels == null || !validLabels.Contains(r.label));
        RebuildLookup();
    }
#endif
}
