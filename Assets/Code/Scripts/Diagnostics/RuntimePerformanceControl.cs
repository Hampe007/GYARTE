using UnityEngine;
using UnityEngine.InputSystem;

public class RuntimePerformanceControl : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TileStreamCoordinator streaming;   // from your scene
    [SerializeField] private PerformanceLogger perfLogger;       // optional
    [SerializeField] private PerformanceOverlay performanceOverlay;
    [SerializeField] private GameObject[] globalPropGroups;      // trees, rocks, villages etc.
    [SerializeField] private Terrain targetTerrain;              // optional, for layer toggles

    [Header("Master Switches")]
    [SerializeField] private bool streamingEnabled = true;
    [SerializeField] private bool propsEnabled = true;
    [SerializeField] private bool terrainTexturesEnabled = true;
    [SerializeField] private bool showPerformanceStats = true;

    [Header("Streaming Map Overlay")]
    [SerializeField] private bool showStreamingMap;
    [SerializeField] private Key mapToggleKey = Key.M;
    [SerializeField] private Vector2 mapPanelSize = new Vector2(360f, 360f);
    [SerializeField] private Vector2 mapPanelMargin = new Vector2(20f, 20f);
    [SerializeField] private Color mapBackgroundColor = new Color(0f, 0f, 0f, 0.78f);
    [SerializeField] private Color mapGridColor = new Color(1f, 1f, 1f, 0.12f);
    [SerializeField] private Color mapWorldFillColor = new Color(1f, 1f, 1f, 0.05f);
    [SerializeField] private Color mapLoadedTileColor = new Color(0.12f, 0.85f, 0.35f, 0.95f);
    [SerializeField] private Color mapPlayerColor = new Color(1f, 0.85f, 0.15f, 1f);
    [SerializeField] private Color mapLoadRadiusColor = new Color(0.25f, 0.75f, 1f, 1f);

    private Texture2D whitePixel;

    void Start()
    {
        ApplyAll();
    }

    public void ApplyAll()
    {
        ApplyStreaming();
        ApplyProps();
        ApplyTerrainLayers();
        ApplyPerformanceOverlayVisibility();
    }

    public void SetStreaming(bool value)
    {
        streamingEnabled = value;
        ApplyStreaming();
    }

    public void SetProps(bool value)
    {
        propsEnabled = value;
        ApplyProps();
    }

    public void SetTerrainTextures(bool value)
    {
        terrainTexturesEnabled = value;
        ApplyTerrainLayers();
    }

    public void SetPerformanceStats(bool value)
    {
        showPerformanceStats = value;
        ApplyPerformanceOverlayVisibility();
    }

    private void ApplyStreaming()
    {
        if (streaming == null) return;

        if (streaming.StreamingLocked)
        {
            streaming.enabled = false;
            return;
        }

        // If off → force unload everything and stop scanning
        if (!streamingEnabled)
        {
            streaming.enabled = false;

            // unload all currently loaded tile scenes
            var loaded = streaming.ClientTiles;
            foreach (var p in loaded)
                UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(p);
        }
        else
        {
            streaming.enabled = true;
        }
    }

    private void ApplyProps()
    {
        if (globalPropGroups != null)
        {
            foreach (var g in globalPropGroups)
                if (g != null) g.SetActive(propsEnabled);
        }

        if (perfLogger != null)
        {
            perfLogger.SetPropsToggle(propsEnabled);
        }
    }

    private void ApplyTerrainLayers()
    {
        if (perfLogger != null)
        {
            perfLogger.SetTextureToggle(terrainTexturesEnabled);

        }
    }

    private void ApplyPerformanceOverlayVisibility()
    {
        if (performanceOverlay == null)
        {
            return;
        }

        bool loadingScreenVisible = streaming != null && streaming.IsStartupLoadingOverlayVisible;
        performanceOverlay.visible = showPerformanceStats && !loadingScreenVisible;
    }

    void Update()
    {
        HandleMapToggle();
        ApplyPerformanceOverlayVisibility();
    }

    void OnGUI()
    {
        GUI.Box(new Rect(15, 15, 260, 190), "Perf Control");

        bool streamingLocked = streaming != null && streaming.StreamingLocked;

        bool previousEnabled = GUI.enabled;
        GUI.enabled = !streamingLocked;

        if (GUI.Button(new Rect(25, 45, 240, 25), streamingEnabled ? "Disable Streaming" : "Enable Streaming"))
            SetStreaming(!streamingEnabled);

        GUI.enabled = previousEnabled;

        if (streamingLocked)
        {
            GUI.Label(new Rect(25, 45, 240, 40), "Tile streaming disabled\nfor this session.");
        }

        if (GUI.Button(new Rect(25, 75, 240, 25), propsEnabled ? "Disable Props" : "Enable Props"))
            SetProps(!propsEnabled);

        if (GUI.Button(new Rect(25, 105, 240, 25), terrainTexturesEnabled ? "Textures: ON (click to disable)" : "Textures: OFF (click to enable)"))
            SetTerrainTextures(!terrainTexturesEnabled);

        bool loadingScreenVisible = streaming != null && streaming.IsStartupLoadingOverlayVisible;
        string perfButtonLabel;
        if (loadingScreenVisible)
        {
            perfButtonLabel = "Performance Stats: HIDDEN (loading)";
        }
        else
        {
            perfButtonLabel = showPerformanceStats ? "Performance Stats: ON" : "Performance Stats: OFF";
        }

        if (GUI.Button(new Rect(25, 135, 240, 25), perfButtonLabel))
            SetPerformanceStats(!showPerformanceStats);

        GUI.Label(new Rect(25, 165, 240, 20), "Press M to toggle streaming map");

        if (showStreamingMap)
        {
            DrawStreamingMapOverlay();
        }
    }

    private void HandleMapToggle()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        var keyControl = Keyboard.current[mapToggleKey];
        if (keyControl != null && keyControl.wasPressedThisFrame)
        {
            showStreamingMap = !showStreamingMap;
        }
    }

    private void DrawStreamingMapOverlay()
    {
        if (streaming == null || streaming.index == null)
        {
            return;
        }

        TileIndex tileIndex = streaming.index;
        TileGridMetadata metadata = streaming.GridMetadata;

        Vector2 tileSize = metadata != null ? metadata.TileSizeXZ : tileIndex.TileSizeMeters;
        Vector3 gridOrigin = metadata != null
            ? metadata.GridOriginWorld
            : new Vector3(tileIndex.OriginOffsetMeters.x, 0f, tileIndex.OriginOffsetMeters.y);
        Vector2Int gridDimensions = metadata != null ? metadata.GridDimensions : EstimateGridDimensions(tileIndex, gridOrigin, tileSize);

        if (gridDimensions.x <= 0 || gridDimensions.y <= 0 || tileSize.x <= 0f || tileSize.y <= 0f)
        {
            return;
        }

        Rect panelRect = new Rect(
            Screen.width - mapPanelSize.x - mapPanelMargin.x,
            mapPanelMargin.y,
            mapPanelSize.x,
            mapPanelSize.y);
        Rect mapRect = new Rect(panelRect.x + 16f, panelRect.y + 44f, panelRect.width - 32f, panelRect.height - 72f);

        DrawFilledRect(panelRect, mapBackgroundColor);
        GUI.Box(panelRect, GUIContent.none);
        GUI.Label(new Rect(panelRect.x + 14f, panelRect.y + 10f, panelRect.width - 28f, 22f), "Streaming Map (M)");
        GUI.Label(new Rect(panelRect.x + 14f, panelRect.y + panelRect.height - 24f, panelRect.width - 28f, 18f), $"Loaded Tiles: {streaming.ClientTiles.Count}");

        DrawFilledRect(mapRect, mapWorldFillColor);

        float worldWidth = gridDimensions.x * tileSize.x;
        float worldHeight = gridDimensions.y * tileSize.y;
        float scale = Mathf.Min(mapRect.width / Mathf.Max(1f, worldWidth), mapRect.height / Mathf.Max(1f, worldHeight));
        Vector2 mapDrawSize = new Vector2(worldWidth * scale, worldHeight * scale);
        Vector2 mapOrigin = new Vector2(
            mapRect.x + (mapRect.width - mapDrawSize.x) * 0.5f,
            mapRect.y + (mapRect.height - mapDrawSize.y) * 0.5f);

        DrawGrid(mapOrigin, mapDrawSize, gridDimensions);
        DrawLoadedTiles(tileIndex, gridOrigin, tileSize, gridDimensions, mapOrigin, mapDrawSize);
        DrawPlayerAndRadius(gridOrigin, tileSize, gridDimensions, mapOrigin, mapDrawSize);
    }

    private void DrawGrid(Vector2 mapOrigin, Vector2 mapDrawSize, Vector2Int gridDimensions)
    {
        for (int x = 0; x <= gridDimensions.x; x++)
        {
            float normalizedX = gridDimensions.x == 0 ? 0f : (float)x / gridDimensions.x;
            float lineX = mapOrigin.x + mapDrawSize.x * normalizedX;
            DrawFilledRect(new Rect(lineX, mapOrigin.y, 1f, mapDrawSize.y), mapGridColor);
        }

        for (int y = 0; y <= gridDimensions.y; y++)
        {
            float normalizedY = gridDimensions.y == 0 ? 0f : (float)y / gridDimensions.y;
            float lineY = mapOrigin.y + mapDrawSize.y * normalizedY;
            DrawFilledRect(new Rect(mapOrigin.x, lineY, mapDrawSize.x, 1f), mapGridColor);
        }
    }

    private void DrawLoadedTiles(TileIndex tileIndex, Vector3 gridOrigin, Vector2 tileSize, Vector2Int gridDimensions, Vector2 mapOrigin, Vector2 mapDrawSize)
    {
        foreach (string path in streaming.ClientTiles)
        {
            if (!tileIndex.TryGetByScene(path, out TileIndex.TileRecord record))
            {
                continue;
            }

            Rect tileRect = WorldTileToMapRect(record.worldOrigin, record.tileSize, gridOrigin, tileSize, gridDimensions, mapOrigin, mapDrawSize);
            DrawFilledRect(tileRect, mapLoadedTileColor);
        }
    }

    private void DrawPlayerAndRadius(Vector3 gridOrigin, Vector2 tileSize, Vector2Int gridDimensions, Vector2 mapOrigin, Vector2 mapDrawSize)
    {
        Transform target = streaming.CurrentStreamingTarget;
        if (target == null)
        {
            return;
        }

        Vector2 normalizedPosition = WorldToMapNormalized(target.position, gridOrigin, tileSize, gridDimensions);
        Vector2 playerPoint = new Vector2(
            mapOrigin.x + mapDrawSize.x * normalizedPosition.x,
            mapOrigin.y + mapDrawSize.y * (1f - normalizedPosition.y));

        float scaleX = mapDrawSize.x / Mathf.Max(1f, gridDimensions.x * tileSize.x);
        float scaleY = mapDrawSize.y / Mathf.Max(1f, gridDimensions.y * tileSize.y);
        float radiusPixels = streaming.loadRadius * Mathf.Min(scaleX, scaleY);

        DrawCircle(playerPoint, radiusPixels, mapLoadRadiusColor, 3f);
        DrawFilledRect(new Rect(playerPoint.x - 4f, playerPoint.y - 4f, 8f, 8f), mapPlayerColor);
    }

    private Rect WorldTileToMapRect(Vector3 worldOrigin, Vector3 worldSize, Vector3 gridOrigin, Vector2 tileSize, Vector2Int gridDimensions, Vector2 mapOrigin, Vector2 mapDrawSize)
    {
        float totalWidth = Mathf.Max(1f, gridDimensions.x * tileSize.x);
        float totalHeight = Mathf.Max(1f, gridDimensions.y * tileSize.y);

        float minX = (worldOrigin.x - gridOrigin.x) / totalWidth;
        float minY = (worldOrigin.z - gridOrigin.z) / totalHeight;
        float width = worldSize.x / totalWidth;
        float height = worldSize.z / totalHeight;

        return new Rect(
            mapOrigin.x + mapDrawSize.x * minX,
            mapOrigin.y + mapDrawSize.y * (1f - minY - height),
            mapDrawSize.x * width,
            mapDrawSize.y * height);
    }

    private Vector2 WorldToMapNormalized(Vector3 worldPosition, Vector3 gridOrigin, Vector2 tileSize, Vector2Int gridDimensions)
    {
        float totalWidth = Mathf.Max(1f, gridDimensions.x * tileSize.x);
        float totalHeight = Mathf.Max(1f, gridDimensions.y * tileSize.y);

        return new Vector2(
            Mathf.Clamp01((worldPosition.x - gridOrigin.x) / totalWidth),
            Mathf.Clamp01((worldPosition.z - gridOrigin.z) / totalHeight));
    }

    private Vector2Int EstimateGridDimensions(TileIndex tileIndex, Vector3 gridOrigin, Vector2 tileSize)
    {
        int maxX = 0;
        int maxY = 0;

        foreach (TileIndex.TileRecord tile in tileIndex.Tiles)
        {
            int x = Mathf.RoundToInt((tile.worldOrigin.x - gridOrigin.x) / Mathf.Max(1f, tileSize.x));
            int y = Mathf.RoundToInt((tile.worldOrigin.z - gridOrigin.z) / Mathf.Max(1f, tileSize.y));
            maxX = Mathf.Max(maxX, x + 1);
            maxY = Mathf.Max(maxY, y + 1);
        }

        return new Vector2Int(Mathf.Max(1, maxX), Mathf.Max(1, maxY));
    }

    private void DrawCircle(Vector2 center, float radius, Color color, float thickness)
    {
        const int segments = 96;
        Vector2 previousPoint = center + new Vector2(radius, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector2 nextPoint = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            DrawLine(previousPoint, nextPoint, color, thickness);
            previousPoint = nextPoint;
        }
    }

    private void DrawLine(Vector2 start, Vector2 end, Color color, float thickness)
    {
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (length <= 0.01f)
        {
            return;
        }

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Matrix4x4 previousMatrix = GUI.matrix;
        Color previousColor = GUI.color;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(angle, start);
        DrawFilledRect(new Rect(start.x, start.y - (thickness * 0.5f), length, thickness), color);

        GUI.matrix = previousMatrix;
        GUI.color = previousColor;
    }

    private void DrawFilledRect(Rect rect, Color color)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        EnsureWhitePixel();

        Color previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whitePixel);
        GUI.color = previousColor;
    }

    private void EnsureWhitePixel()
    {
        if (whitePixel != null)
        {
            return;
        }

        whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        whitePixel.SetPixel(0, 0, Color.white);
        whitePixel.Apply();
    }

    private void OnDestroy()
    {
        if (whitePixel != null)
        {
            Destroy(whitePixel);
            whitePixel = null;
        }
    }
}
