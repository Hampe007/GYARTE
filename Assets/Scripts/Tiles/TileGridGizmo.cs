// Assets/Scripts/Tiles/TileGridGizmo.cs
// Draws tile bounds as gizmos for one or more terrains.
// Works in edit mode; uses the same "tileSizeMeters" approach as the slicer.

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
    [Min(1f)] public float tileSizeMeters = 250f;
    [Tooltip("Offset the boxes to match each terrain's origin (recommended: leave at 0,0,0).")]
    public Vector3 worldOffset = Vector3.zero;

    [Header("Gizmo Style")]
    public Color lineColor = new Color(0f, 1f, 1f, 0.85f);
    public Color labelColor = new Color(1f, 1f, 1f, 0.9f);
    public bool drawLabels = false;
    public bool drawAlways = true;     // if false → only when this object is selected
    public bool solidFill = false;     // off by default (wireframe only)
    [Range(0.05f, 1.0f)] public float heightRatio = 0.05f; // visual box height vs terrain.height

    // Global toggle controlled by menu item
    public static bool ShowGizmos = true;

    void OnDrawGizmos()
    {
        if (!drawAlways) return;
        Draw();
    }

    void OnDrawGizmosSelected()
    {
        if (drawAlways) return;
        Draw();
    }

    void Draw()
    {
        if (!ShowGizmos) return;
        if (tileSizeMeters <= 0f) return;

        var terrains = GetTerrains();
        if (terrains.Count == 0) return;

        var oldColor = Gizmos.color;
        Gizmos.color = lineColor;

#if UNITY_EDITOR
        var oldHc = UnityEditor.Handles.color;
        UnityEditor.Handles.color = labelColor;
#endif

        foreach (var t in terrains)
        {
            if (t == null || t.terrainData == null) continue;

            var td = t.terrainData;
            var size = td.size;
            int tilesX = Mathf.Max(1, Mathf.CeilToInt(size.x / tileSizeMeters));
            int tilesY = Mathf.Max(1, Mathf.CeilToInt(size.z / tileSizeMeters));

            var tileSize = new Vector3(size.x / tilesX, size.y, size.z / tilesY);
            float gizmoHeight = Mathf.Max(1f, size.y * heightRatio);

            var origin = t.transform.position + worldOffset;

            for (int y = 0; y < tilesY; y++)
            {
                for (int x = 0; x < tilesX; x++)
                {
                    var min = new Vector3(
                        origin.x + x * tileSize.x,
                        origin.y,
                        origin.z + y * tileSize.z
                    );

                    var center = new Vector3(
                        min.x + tileSize.x * 0.5f,
                        origin.y + gizmoHeight * 0.5f,
                        min.z + tileSize.z * 0.5f
                    );

                    var box = new Vector3(tileSize.x, gizmoHeight, tileSize.z);

                    if (solidFill)
                        Gizmos.DrawCube(center, box);
                    Gizmos.DrawWireCube(center, box);

#if UNITY_EDITOR
                    if (drawLabels)
                    {
                        var labelPos = new Vector3(center.x, center.y + gizmoHeight * 0.55f, center.z);
                        UnityEditor.Handles.Label(labelPos, $"{t.name}  [{x},{y}]");
                    }
#endif
                }
            }
        }

        Gizmos.color = oldColor;
#if UNITY_EDITOR
        UnityEditor.Handles.color = oldHc;
#endif
    }

    List<Terrain> GetTerrains()
    {
        if (autoCollect)
        {
            var all = Terrain.activeTerrains ?? Array.Empty<Terrain>();
            if (string.IsNullOrEmpty(autoPrefix)) return all.ToList();
            return all.Where(tt => tt && tt.name.StartsWith(autoPrefix, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(tt => tt.name).ToList();
        }
        return (manualTerrains ?? Array.Empty<Terrain>()).Where(tt => tt).Distinct().ToList();
    }
}