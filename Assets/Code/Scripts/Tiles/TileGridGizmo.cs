// Assets/Scripts/Tiles/TileGridGizmo.cs
// Draws tile bounds as gizmos for one or more terrains.
// Reads authoritative grid metadata written by the slicer.

using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public sealed class TileGridGizmo : MonoBehaviour
{
    [Header("Terrains")]
    [Tooltip("Collect all terrains whose names start with this prefix (optional). Leave empty to use Manual Terrains list.")]
    public string autoPrefix = "Terrain_";
    [Tooltip("When true, uses Terrain.activeTerrains filtered by prefix. When false, uses Manual Terrains.")]
    public bool autoCollect = true;
    public Terrain[] manualTerrains;

    [Header("Grid")]
    [Tooltip("Optional explicit metadata reference. If empty, the canonical metadata asset is auto-loaded.")]
    public TileGridMetadata metadata;
    [Tooltip("Offset the boxes to match each terrain's origin (recommended: leave at 0,0,0).")]
    public Vector3 worldOffset = Vector3.zero;

    [Header("Gizmo Style")]
    public Color lineColor = new Color(0f, 1f, 1f, 0.85f);
    public bool drawAlways = true; // if false → only when this object is selected

    // Global toggle controlled by menu item
    public static bool ShowGizmos = true;

    private void OnDrawGizmos()
    {
        if (!drawAlways) return;
        Draw();
    }

    private void OnDrawGizmosSelected()
    {
        if (drawAlways) return;
        Draw();
    }

    private void Draw()
    {
        if (!ShowGizmos) return;

        List<Terrain> terrains = GetTerrains();
        if (terrains.Count == 0) return;

        TileGridMetadata gridMetadata = metadata != null ? metadata : TileGridMetadataProvider.GetOrLoad();
        if (gridMetadata == null) return;

        Color oldColor = Gizmos.color;
        Gizmos.color = lineColor;

        foreach (Terrain terrain in terrains)
        {
            if (terrain == null || terrain.terrainData == null) continue;

            string label = TileTerrainLabelUtility.ToLabel(terrain.name);
            if (!gridMetadata.TryGetTerrain(label, out TileGridMetadata.TerrainGridRecord record))
                continue;

            int tilesX = record.gridDimensions.x;
            int tilesY = record.gridDimensions.y;
            float tileSizeX = record.tileSizeXZ.x;
            float tileSizeY = record.tileSizeXZ.y;

            if (tilesX <= 0 || tilesY <= 0 || tileSizeX <= 0f || tileSizeY <= 0f)
                continue;

            Vector3 drawOrigin = record.origin + worldOffset;
            float height = Mathf.Max(1f, terrain.terrainData.size.y * 0.05f);

            for (int y = 0; y < tilesY; y++)
            {
                for (int x = 0; x < tilesX; x++)
                {
                    Vector3 min = new Vector3(
                        drawOrigin.x + x * tileSizeX,
                        drawOrigin.y,
                        drawOrigin.z + y * tileSizeY
                    );

                    Vector3 center = new Vector3(
                        min.x + tileSizeX * 0.5f,
                        drawOrigin.y + height * 0.5f,
                        min.z + tileSizeY * 0.5f
                    );

                    Vector3 box = new Vector3(tileSizeX, height, tileSizeY);
                    Gizmos.DrawWireCube(center, box);
                }
            }
        }

        Gizmos.color = oldColor;
    }

    private List<Terrain> GetTerrains()
    {
        if (autoCollect)
        {
            Terrain[] all = Terrain.activeTerrains ?? Array.Empty<Terrain>();
            if (string.IsNullOrEmpty(autoPrefix)) return all.ToList();
            return all.Where(tt => tt && tt.name.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(tt => tt.name)
                .ToList();
        }

        return (manualTerrains ?? Array.Empty<Terrain>()).Where(tt => tt).Distinct().ToList();
    }
}
