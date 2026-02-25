// Assets/Scripts/Tiles/TileGridGizmo.cs
// Draws active streamed tile bounds as gizmos.
// Uses slicer metadata + tile streamer state; no manual tile size input required.

using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public sealed class TileGridGizmo : MonoBehaviour
{
    [Header("Gizmo Style")]
    public Color lineColor = new Color(0f, 1f, 1f, 0.85f);

    [Header("Debug")]
    [Tooltip("Additional runtime toggle for active tile gizmo drawing.")]
    public bool debugEnabled = true;

    // Global toggle controlled by menu item
    public static bool ShowGizmos = true;

    private TileStreamCoordinator cachedCoordinator;

    private void OnDrawGizmos()
    {
        Draw();
    }

    private void OnDrawGizmosSelected()
    {
        Draw();
    }

    private void Draw()
    {
        if (!ShowGizmos || !debugEnabled)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying || !EditorApplication.isPaused)
            return;
#else
        return;
#endif

        TileGridMetadata metadata = TileGridMetadataProvider.GetOrLoad();
        if (metadata == null || metadata.TileIndex == null)
            return;

        TileStreamCoordinator coordinator = ResolveCoordinator();
        if (coordinator == null)
            return;

        var activePaths = GatherActiveTilePaths(coordinator);
        if (activePaths.Count == 0)
            return;

        Color oldColor = Gizmos.color;
        Gizmos.color = lineColor;

        foreach (string scenePath in activePaths)
        {
            if (!metadata.TileIndex.TryGetByScene(scenePath, out TileIndex.TileRecord record))
                continue;

            Gizmos.DrawWireCube(record.worldBounds.center, record.worldBounds.size);
        }

        Gizmos.color = oldColor;
    }

    private TileStreamCoordinator ResolveCoordinator()
    {
        if (cachedCoordinator != null)
            return cachedCoordinator;

#if UNITY_EDITOR
        cachedCoordinator = FindObjectOfType<TileStreamCoordinator>(true);
#endif
        return cachedCoordinator;
    }

    private static HashSet<string> GatherActiveTilePaths(TileStreamCoordinator coordinator)
    {
        var active = new HashSet<string>();

        if (coordinator.ServerTiles != null)
        {
            foreach (string path in coordinator.ServerTiles)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    active.Add(path);
            }
        }

        if (coordinator.ClientTiles != null)
        {
            foreach (string path in coordinator.ClientTiles)
            {
                if (!string.IsNullOrWhiteSpace(path))
                    active.Add(path);
            }
        }

        return active;
    }
}
