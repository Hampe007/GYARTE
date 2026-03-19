using UnityEngine;

public class RuntimePerformanceControl : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TileStreamCoordinator streaming;
    [SerializeField] private PerformanceLogger perfLogger;
    [SerializeField] private PerformanceOverlay performanceOverlay;
    [SerializeField] private GameObject[] globalPropGroups;
    [SerializeField] private Terrain targetTerrain;

    [Header("Master Switches")]
    [SerializeField] private bool streamingEnabled = true;
    [SerializeField] private bool propsEnabled = true;
    [SerializeField] private bool terrainTexturesEnabled = true;
    [SerializeField] private bool showPerformanceStats = true;

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

        if (!streamingEnabled)
        {
            streaming.enabled = false;

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

        GUI.Label(new Rect(25, 165, 240, 20), "World map is now handled by WorldMapController (M)");
    }
}
