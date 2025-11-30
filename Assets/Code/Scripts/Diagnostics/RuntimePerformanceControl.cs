using UnityEngine;

public class RuntimePerformanceControl : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TileStreamCoordinator streaming;   // from your scene
    [SerializeField] private PerformanceLogger perfLogger;       // optional
    [SerializeField] private GameObject[] globalPropGroups;      // trees, rocks, villages etc.
    [SerializeField] private Terrain targetTerrain;              // optional, for layer toggles

    [Header("Master Switches")]
    [SerializeField] private bool streamingEnabled = true;
    [SerializeField] private bool propsEnabled = true;
    [SerializeField] private bool terrainTexturesEnabled = true;

    void Start()
    {
        ApplyAll();
    }

    public void ApplyAll()
    {
        ApplyStreaming();
        ApplyProps();
        ApplyTerrainLayers();
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
    
    void OnGUI()
    {
        GUI.Box(new Rect(15, 15, 220, 130), "Perf Control");

        bool streamingLocked = streaming != null && streaming.StreamingLocked;

        bool previousEnabled = GUI.enabled;
        GUI.enabled = !streamingLocked;
        
        if (GUI.Button(new Rect(25, 45, 200, 25), streamingEnabled ? "Disable Streaming" : "Enable Streaming"))
            SetStreaming(!streamingEnabled);

        GUI.enabled = previousEnabled;

        if (streamingLocked)
        {
            GUI.Label(new Rect(25, 45, 200, 40), "Tile streaming disabled\nfor this session.");
        }
        
        if (GUI.Button(new Rect(25, 75, 200, 25), propsEnabled ? "Disable Props" : "Enable Props"))
            SetProps(!propsEnabled);

        if (GUI.Button(new Rect(25, 105, 200, 25), terrainTexturesEnabled ? "Textures: ON (click to disable)" : "Textures: OFF (click to enable)"))
            SetTerrainTextures(!terrainTexturesEnabled);
    }
}
