using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TileStreamCoordinator : NetworkBehaviour
{
    [Header("Configuration")]
    public TileIndex index;
    [SerializeField] private TileGridMetadata gridMetadata;
    [SerializeField] private Transform player;
    [SerializeField] public float loadRadius = 500f;
    [SerializeField] private float edgeBuffer = 25f;
    public float scanInterval = 0.5f;
    public bool logActions;

    private readonly HashSet<string> serverLoaded = new();
    private readonly HashSet<string> clientLoaded = new();
    private readonly List<Vector3> tempPlayerPositions = new();
    private readonly HashSet<string> desiredTiles = new();
    private readonly Dictionary<string, TileInstance> liveTiles = new();

    private float loadRadiusSquared;
    private float unloadRadiusSquared;
    private float cachedLoadRadius = -1f;
    private float cachedEdgeBuffer = -1f;

    private Coroutine serverLoop;
    private Coroutine clientApplyCoroutine;
    
    public bool offlineStandalone = true;
    
    [Tooltip("Transform to follow in offline streaming mode; defaults to player or main camera when unset.")]
    public Transform offlineTarget;
    private Coroutine offlineLoop;

    private TileStreamer streamer;
    private TileLoader loader;

    [Header("Build Overrides")]
    [SerializeField] private bool disableInPlayerBuilds;
    [SerializeField] private bool disableAtRuntime;

    [Header("Master Terrain")]
    [SerializeField] private Terrain masterTerrain;
    [SerializeField] private bool disableMasterOnStart = true;
    [SerializeField] private bool unloadMasterTerrainScene = true;
    [SerializeField] private bool unloadAllTerrainScenesOnStart = true;

    private string masterTerrainScenePath = string.Empty;
    private bool masterSceneUnloaded;

    private bool masterDisabled;
    private bool masterWorkRunning;
    private bool firstTileLoadConfirmed;
    private bool startupTerrainsHandled;

    [Header("Startup Safety")]
    [SerializeField] private bool gatePlayerUntilTileReady = true;

    [Header("Startup Loading Overlay")]
    [SerializeField] private bool showStartupLoadingScreen = true;
    [SerializeField] private string startupLoadingText = "Loading world...";
    [SerializeField, Min(0f)] private float hideLoadingOverlayDelay = 0.2f;
    [SerializeField, Min(0f)] private float maxGroundSettleWait = 2f;

    private Transform gatedPlayer;
    private CharacterController gatedCharacterController;
    private Rigidbody gatedRigidbody;
    private bool gateApplied;
    private bool playerStartupGateReleased;
    private bool cachedRigidbodyUseGravity;
    private bool cachedRigidbodyIsKinematic;
    private RigidbodyConstraints cachedRigidbodyConstraints;
    private bool showLoadingOverlay;
    private Coroutine hideLoadingOverlayCoroutine;

    public IReadOnlyCollection<string> ServerTiles => serverLoaded;
    public IReadOnlyCollection<string> ClientTiles => clientLoaded;
    
    public int ServerQueuedLoads { get; private set; }
    public int ClientQueuedLoads { get; private set; }
    public int ServerLoadsThisFrame { get; private set; }
    public int ClientLoadsThisFrame { get; private set; }

    private int serverLoadFrame = -1;
    private int clientLoadFrame = -1;
    
    private Coroutine masterEnsureCoroutine;
    
    private Coroutine resolvePlayerLoop;

    public override void OnStartServer()
    {
        base.OnStartServer();
        if (serverLoop == null)
        {
            serverLoop = StartCoroutine(ServerLoop());
        }
    }

    public override void OnStopServer()
    {
        if (serverLoop != null)
        {
            StopCoroutine(serverLoop);
            serverLoop = null;
        }

        if (serverLoaded.Count > 0)
        {
            StartCoroutine(UnloadAllTiles(serverLoaded, isServer: true));
        }

        base.OnStopServer();
    }

    private void Awake()
    {
        if (StreamingLocked)
        {
            if (logActions)
            {
                Debug.Log("Tile streaming disabled for this session; coordinator will remain inactive.");
            }

            enabled = false;
            return;
        }

        ResolveGridMetadataAndIndex();
        streamer = new TileStreamer(index);
        loader = new TileLoader(this, liveTiles);

        TryResolvePlayerAndOfflineTarget();
        UpdateRadiusCache();
        masterDisabled = masterTerrain == null || !masterTerrain.gameObject.activeSelf;
        masterSceneUnloaded = string.IsNullOrEmpty(masterTerrainScenePath) || !SceneManager.GetSceneByPath(masterTerrainScenePath).isLoaded;
        firstTileLoadConfirmed = liveTiles.Count > 0;

        if (masterTerrain != null)
        {
            var ms = masterTerrain.gameObject.scene;
            if (ms.IsValid() && !string.IsNullOrEmpty(ms.path))
            {
                masterTerrainScenePath = ms.path;
                masterSceneUnloaded = !ms.isLoaded;
            }
        }
    }

    private void OnValidate()
    {
        ResolveGridMetadataAndIndex();
        UpdateRadiusCache();
    }

    public TileGridMetadata GridMetadata => gridMetadata;

    private void ResolveGridMetadataAndIndex()
    {
        if (gridMetadata == null)
        {
            gridMetadata = TileGridMetadataProvider.GetOrLoad();
        }

        if (index == null && gridMetadata != null)
        {
            index = gridMetadata.TileIndex;
        }
    }


    public bool BuildStreamingDisabled => disableInPlayerBuilds && !Application.isEditor;
    public bool RuntimeStreamingDisabled => disableAtRuntime;
    public bool StreamingLocked => BuildStreamingDisabled || RuntimeStreamingDisabled;
    
    private void UpdateRadiusCache()
    {
        if (loadRadius < 0f)
        {
            loadRadius = 0f;
        }

        if (edgeBuffer < 0f)
        {
            edgeBuffer = 0f;
        }

        if (!Mathf.Approximately(cachedLoadRadius, loadRadius) || !Mathf.Approximately(cachedEdgeBuffer, edgeBuffer))
        {
            cachedLoadRadius = loadRadius;
            cachedEdgeBuffer = edgeBuffer;

            loadRadiusSquared = loadRadius * loadRadius;
            float unloadRadius = loadRadius + edgeBuffer;
            unloadRadiusSquared = unloadRadius * unloadRadius;
        }
    }

    private IEnumerator ServerLoop()
    {
        var wait = new WaitForSeconds(scanInterval);
        while (isActiveAndEnabled && NetworkServer.active)
        {
            yield return RecomputeServerTiles();
            yield return wait;
        }
    }

    private IEnumerator RecomputeServerTiles()
    {
        if (!isServer || index == null)
        {
            yield break;
        }

        UpdateRadiusCache();
        tempPlayerPositions.Clear();

        foreach (var kvp in NetworkServer.connections)
        {
            var conn = kvp.Value;
            if (conn == null || conn.identity == null)
            {
                continue;
            }

            Vector3 position = conn.identity.transform.position;
            tempPlayerPositions.Add(position);
        }

        if (tempPlayerPositions.Count == 0 && player != null)
        {
            Vector3 position = player.position;
            tempPlayerPositions.Add(position);
        }

        streamer.ComputeDesired(tempPlayerPositions, loadRadiusSquared, unloadRadiusSquared, serverLoaded, desiredTiles);

        if (desiredTiles.SetEquals(serverLoaded))
        {
            ServerQueuedLoads = 0;
            yield break;
        }

        var toLoad = desiredTiles.Except(serverLoaded).ToList();
        var toUnload = serverLoaded.Except(desiredTiles).ToList();

        ServerQueuedLoads = toLoad.Count;
        
        foreach (var path in toLoad)
        {
            yield return loader.Load(path, true, logActions);
            ServerQueuedLoads = Mathf.Max(0, ServerQueuedLoads - 1);
        }

        foreach (var path in toUnload)
        {
            yield return loader.Unload(path, true, logActions);
        }

        serverLoaded.Clear();
        foreach (var path in desiredTiles)
        {
            if (SceneManager.GetSceneByPath(path).isLoaded)
            {
                serverLoaded.Add(path);
            }
        }

        if (isServer)
        {
            RpcSyncSceneSet(serverLoaded.ToArray());
        }
    }

    [ClientRpc(channel = Channels.Reliable)]
    private void RpcSyncSceneSet(string[] scenePaths)
    {
        if (!isClient || scenePaths == null)
        {
            return;
        }

        if (clientApplyCoroutine != null)
        {
            StopCoroutine(clientApplyCoroutine);
        }

        clientApplyCoroutine = StartCoroutine(ApplyClientSceneSet(scenePaths));
    }

    private IEnumerator ApplyClientSceneSet(IEnumerable<string> scenePaths)
    {
        // If a previous coroutine was interrupted mid-load, scenes may have finished
        // loading without being registered in liveTiles. Catch them up now.
        if (index != null)
        {
            foreach (var path in clientLoaded.ToList())
            {
                var existingScene = SceneManager.GetSceneByPath(path);
                if (existingScene.isLoaded && !liveTiles.ContainsKey(path))
                {
                    if (index.TryGetByScene(path, out var rec))
                    {
                        liveTiles[path] = new TileInstance(rec, existingScene);
                        WireTerrainNeighbors(path);
                        if (logActions)
                            Debug.Log($"[TileStream] Reconciled untracked loaded scene: {path}");
                    }
                }
            }
        }
    
        var desired = new HashSet<string>(scenePaths ?? Enumerable.Empty<string>());

        if (desired.SetEquals(clientLoaded))
        {
            ClientQueuedLoads = 0;
            yield break;
        }

        var toLoad = desired.Except(clientLoaded).ToList();
        var toUnload = clientLoaded.Except(desired).ToList();

        ClientQueuedLoads = toLoad.Count;
        
        foreach (var path in toLoad)
        {
            yield return loader.Load(path, false, logActions);
            ClientQueuedLoads = Mathf.Max(0, ClientQueuedLoads - 1);
        }

        foreach (var path in toUnload)
        {
            yield return loader.Unload(path, false, logActions);
        }

        clientLoaded.Clear();
        foreach (var path in desired)
        {
            if (SceneManager.GetSceneByPath(path).isLoaded)
            {
                clientLoaded.Add(path);
            }
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform target = player != null ? player : offlineTarget;

        if (target == null)
        {
            return;
        }

        UpdateRadiusCache();

        Handles.color = Color.cyan;
        Handles.DrawWireDisc(target.position, Vector3.up, loadRadius);
    }
#endif
    
    private void OnEnable()
    {
        if (StreamingLocked)
        {
            enabled = false;
            return;
        }

        TryResolvePlayerAndOfflineTarget();

        if (resolvePlayerLoop == null)
        {
            resolvePlayerLoop = StartCoroutine(ResolvePlayerLoop());
        }

        if (offlineStandalone && !NetworkServer.active && !NetworkClient.active && index != null)
        {
            if (offlineLoop == null) offlineLoop = StartCoroutine(OfflineLoop());
        }

        TryUpdatePlayerStartupGate();
    }


    private void OnDisable()
    {
        ReleasePlayerStartupGate();
        showLoadingOverlay = false;

        if (hideLoadingOverlayCoroutine != null)
        {
            StopCoroutine(hideLoadingOverlayCoroutine);
            hideLoadingOverlayCoroutine = null;
        }

        if (resolvePlayerLoop != null)
        {
            StopCoroutine(resolvePlayerLoop);
            resolvePlayerLoop = null;
        }

        if (offlineLoop != null)
        {
            StopCoroutine(offlineLoop);
            offlineLoop = null;
        }

        if (clientApplyCoroutine != null)
        {
            StopCoroutine(clientApplyCoroutine);
            clientApplyCoroutine = null;
        }
        
        if (clientLoaded.Count > 0)
        {
            StartCoroutine(UnloadAllTiles(clientLoaded, isServer: false));
        }
    }

    // Add the offline loop (local client-side streaming)
    private IEnumerator OfflineLoop()
    {
        var wait = new WaitForSeconds(scanInterval);

        var desired = new HashSet<string>();

        while (isActiveAndEnabled && !NetworkServer.active && !NetworkClient.active)
        {
            Transform t = offlineTarget != null ? offlineTarget : player;
            if (t == null || index == null)
            {
                ClientQueuedLoads = 0;
                yield return wait;
                continue;
            }

            UpdateRadiusCache();

            tempPlayerPositions.Clear();
            tempPlayerPositions.Add(t.position);

            streamer.ComputeDesired(tempPlayerPositions, loadRadiusSquared, unloadRadiusSquared, clientLoaded, desired);

            var toLoad = desired.Except(clientLoaded).ToList();
            var toUnload = clientLoaded.Except(desired).ToList();

            ClientQueuedLoads = toLoad.Count;

            foreach (var path in toLoad)
            {
                yield return loader.Load(path, false, logActions);
                ClientQueuedLoads = Mathf.Max(0, ClientQueuedLoads - 1);
            }

            foreach (var path in toUnload)
                yield return loader.Unload(path, false, logActions);

            clientLoaded.Clear();
            foreach (var path in desired)
            {
                if (SceneManager.GetSceneByPath(path).isLoaded)
                    clientLoaded.Add(path);
            }

            yield return wait;
        }
    }

    private bool TryResolvePlayerAndOfflineTarget()
    {
        Transform prevPlayer = player;
        Transform prevOffline = offlineTarget;

        // Client: prefer local player
        if (player == null && NetworkClient.active && NetworkClient.localPlayer != null)
        {
            player = NetworkClient.localPlayer.transform;
        }

        // Server: pick first available identity if no explicit player set
        if (player == null && NetworkServer.active)
        {
            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn?.identity != null)
                {
                    player = conn.identity.transform;
                    break;
                }
            }
        }

        // Offline: fallback to main camera if still null
        if (offlineTarget == null)
        {
            if (player != null) offlineTarget = player;
            else if (Camera.main != null) offlineTarget = Camera.main.transform;
        }

        // Keep offlineTarget in sync once player exists (optional but recommended)
        if (player != null && offlineTarget == null)
        {
            offlineTarget = player;
        }

        TryUpdatePlayerStartupGate();

        CacheMasterTerrainScene();

        return prevPlayer != player || prevOffline != offlineTarget;
    }

    private void Update()
    {
        TryUpdatePlayerStartupGate();
    }

    private void TryUpdatePlayerStartupGate()
    {
        if (!gatePlayerUntilTileReady)
        {
            ReleasePlayerStartupGate();
            showLoadingOverlay = false;
            return;
        }

        if (playerStartupGateReleased)
        {
            return;
        }

        showLoadingOverlay = showStartupLoadingScreen;

        if (player == null)
        {
            ApplyPlayerStartupGate(player);
            return;
        }

        if (HasLoadedTileCoveringPosition(player.position))
        {
            ReleasePlayerStartupGate();
            playerStartupGateReleased = true;

            if (logActions)
            {
                Debug.Log($"[TileStream] Released startup gate for '{player.name}' after initial tile became available.");
            }

            BeginHideLoadingOverlayAfterSettle();
            return;
        }

        ApplyPlayerStartupGate(player);
    }

    private void ApplyPlayerStartupGate(Transform target)
    {
        if (target != gatedPlayer)
        {
            ReleasePlayerStartupGate();
            gatedPlayer = target;
        }

        if (gatedPlayer == null)
        {
            return;
        }

        if (gatedCharacterController == null)
        {
            gatedCharacterController = gatedPlayer.GetComponent<CharacterController>();
        }

        if (gatedRigidbody == null)
        {
            gatedRigidbody = gatedPlayer.GetComponent<Rigidbody>();
            if (gatedRigidbody != null)
            {
                cachedRigidbodyUseGravity = gatedRigidbody.useGravity;
                cachedRigidbodyIsKinematic = gatedRigidbody.isKinematic;
                cachedRigidbodyConstraints = gatedRigidbody.constraints;
            }
        }

        if (gatedCharacterController != null)
        {
            gatedCharacterController.enabled = false;
        }

        if (gatedRigidbody != null)
        {
            gatedRigidbody.useGravity = false;
            gatedRigidbody.isKinematic = true;
            gatedRigidbody.constraints = RigidbodyConstraints.FreezeAll;
            gatedRigidbody.linearVelocity = Vector3.zero;
            gatedRigidbody.angularVelocity = Vector3.zero;
        }

        gateApplied = true;
    }

    private void ReleasePlayerStartupGate()
    {
        if (!gateApplied)
        {
            return;
        }

        if (gatedCharacterController != null)
        {
            gatedCharacterController.enabled = true;
        }

        if (gatedRigidbody != null)
        {
            gatedRigidbody.useGravity = cachedRigidbodyUseGravity;
            gatedRigidbody.isKinematic = cachedRigidbodyIsKinematic;
            gatedRigidbody.constraints = cachedRigidbodyConstraints;
        }

        gatedPlayer = null;
        gatedCharacterController = null;
        gatedRigidbody = null;
        gateApplied = false;
    }

    public bool HasLoadedTileCoveringPosition(Vector3 worldPosition)
    {
        if (liveTiles.Count == 0)
        {
            return false;
        }

        foreach (var tile in liveTiles.Values)
        {
            if (!tile.Scene.isLoaded)
            {
                continue;
            }

            Bounds bounds = tile.Record.worldBounds;
            if (worldPosition.x < bounds.min.x || worldPosition.x > bounds.max.x)
            {
                continue;
            }

            if (worldPosition.z < bounds.min.z || worldPosition.z > bounds.max.z)
            {
                continue;
            }

            return true;
        }

        return false;
    }
    
    private void BeginHideLoadingOverlayAfterSettle()
    {
        if (!showStartupLoadingScreen)
        {
            showLoadingOverlay = false;
            return;
        }

        if (hideLoadingOverlayCoroutine != null)
        {
            StopCoroutine(hideLoadingOverlayCoroutine);
        }

        hideLoadingOverlayCoroutine = StartCoroutine(HideLoadingOverlayAfterSettle());
    }

    private IEnumerator HideLoadingOverlayAfterSettle()
    {
        float elapsed = 0f;
        while (elapsed < maxGroundSettleWait && !IsPlayerSettledOnGround())
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (hideLoadingOverlayDelay > 0f)
        {
            yield return new WaitForSeconds(hideLoadingOverlayDelay);
        }

        showLoadingOverlay = false;
        hideLoadingOverlayCoroutine = null;
    }

    private bool IsPlayerSettledOnGround()
    {
        Transform target = player != null ? player : gatedPlayer;
        if (target == null)
        {
            return true;
        }

        var controller = target.GetComponent<CharacterController>();
        if (controller != null && controller.enabled && controller.isGrounded)
        {
            return true;
        }

        var body = target.GetComponent<Rigidbody>();
        if (body != null)
        {
            if (body.IsSleeping())
            {
                return true;
            }

            if (Mathf.Abs(body.linearVelocity.y) <= 0.05f && Physics.Raycast(target.position + Vector3.up * 0.1f, Vector3.down, 0.4f, ~0, QueryTriggerInteraction.Ignore))
            {
                return true;
            }
        }

        return Physics.Raycast(target.position + Vector3.up * 0.2f, Vector3.down, 0.6f, ~0, QueryTriggerInteraction.Ignore);
    }

    private void OnGUI()
    {
        if (!showLoadingOverlay || !ShouldDisplayLoadingOverlay())
        {
            return;
        }

        var oldColor = GUI.color;
        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            normal = { textColor = Color.white }
        };

        GUI.Label(new Rect(0, 0, Screen.width, Screen.height), startupLoadingText, style);
        GUI.color = oldColor;
    }

    private bool ShouldDisplayLoadingOverlay()
    {
        if (!showStartupLoadingScreen)
        {
            return false;
        }

        if (NetworkClient.active && NetworkClient.localPlayer != null && player != null)
        {
            return player == NetworkClient.localPlayer.transform;
        }

        return true;
    }

    private void CacheMasterTerrainScene()
    {
        if (masterTerrain == null)
        {
            return;
        }

        var scene = masterTerrain.gameObject.scene;
        if (scene.IsValid() && scene.isLoaded)
        {
            masterTerrainScenePath = scene.path;
        }
    }

    private IEnumerator EnsureMasterTerrainDisabled()
    {
        if (!ShouldDisableMasterTerrainNow())
        {
            yield break;
        }
        
        // If someone else is already doing the work, wait for them.
        if (masterWorkRunning)
        {
            while (masterWorkRunning)
            {
                yield return null;
            }
            yield break;
        }

        if (startupTerrainsHandled)
        {
            yield break;
        }

        masterWorkRunning = true;

        yield return DisableAndUnloadStartupTerrains();

        startupTerrainsHandled = true;
        masterDisabled = true;
        masterSceneUnloaded = true;

        masterWorkRunning = false;
    }

    private IEnumerator DisableAndUnloadStartupTerrains()
    {
        var terrains = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var scenesToUnload = new HashSet<string>();
        var coordinatorScenePath = gameObject.scene.path;

        foreach (var terrain in terrains)
        {
            if (terrain == null)
            {
                continue;
            }

            bool isConfiguredMasterTerrain = masterTerrain != null && terrain == masterTerrain;
            bool shouldDisableTerrain = disableMasterOnStart
                                        && terrain.gameObject.activeSelf
                                        && (isConfiguredMasterTerrain || masterTerrain == null);

            if (shouldDisableTerrain)
            {
                terrain.gameObject.SetActive(false);
            }

            if (!unloadAllTerrainScenesOnStart || !unloadMasterTerrainScene)
            {
                continue;
            }

            var scene = terrain.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(coordinatorScenePath) && scene.path == coordinatorScenePath)
            {
                continue;
            }

            if (liveTiles.TryGetValue(scene.path, out var liveTile) && liveTile.Scene.isLoaded)
            {
                continue;
            }

            scenesToUnload.Add(scene.path);
        }

        foreach (var path in scenesToUnload)
        {
            var scene = SceneManager.GetSceneByPath(path);

            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }

            if (scene == SceneManager.GetActiveScene())
            {
                Scene tempActive = GetOrCreateTemporaryActiveScene();
                if (tempActive.IsValid() && tempActive.isLoaded)
                {
                    SceneManager.SetActiveScene(tempActive);
                }
            }

            var op = SceneManager.UnloadSceneAsync(scene);
            if (op == null)
            {
                continue;
            }

            while (!op.isDone)
            {
                yield return null;
            }
        }
    }
    
    private bool ShouldDisableMasterTerrainNow()
    {
        if (masterTerrain == null) return false;
        if (masterDisabled) return false;
        if (liveTiles.Count > 0) firstTileLoadConfirmed = true;
        return firstTileLoadConfirmed && liveTiles.Count > 0;
    }

    private void WarnIfMasterDisabledWithoutLoadedTiles(string requestedPath)
    {
        if (!masterDisabled || liveTiles.Count > 0)
        {
            return;
        }

        Debug.LogWarning($"[TileStream] Tile load failed for '{requestedPath}' while master terrain is disabled and there are no live tiles. Players may fall through the world.");
    }

    private IEnumerator LoadSceneInternal(string path, bool isServer, bool log)
    {
        if (string.IsNullOrEmpty(path)) yield break;
        if (index == null)
        {
            Debug.LogWarning("[TileStream] Cannot load tiles without a TileIndex assigned.");
            yield break;
        }

        if (!index.TryGetByScene(path, out var record))
        {
            Debug.LogWarning($"[TileStream] Unknown tile scene {path}");
            yield break;
        }

        var existing = SceneManager.GetSceneByPath(path);
        if (existing.isLoaded)
        {
            liveTiles[path] = new TileInstance(record, existing);
            yield break;
        }

        if (log)
        {
            Debug.Log($"[TileStream] {(isServer ? "Server" : "Client")} loading {path}");
        }

        var op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogWarning($"[TileStream] Failed to start loading scene {path}");
            yield break;
        }

        while (!op.isDone)
        {
            yield return null;
        }

        var scene = SceneManager.GetSceneByPath(path);
        while (!scene.isLoaded)
        {
            yield return null;
            scene = SceneManager.GetSceneByPath(path);
        }

        liveTiles[path] = new TileInstance(record, scene);
        WireTerrainNeighbors(path);

        if (isServer)
        {
            NetworkServer.SpawnObjects();
            IncrementServerLoadsThisFrame();
        }
        else
        {
            IncrementClientLoadsThisFrame();
        }
        
        firstTileLoadConfirmed = true;
    }

    private IEnumerator UnloadSceneInternal(string path, bool isServer, bool log)
    {
        var scene = SceneManager.GetSceneByPath(path);
        if (!scene.isLoaded)
        {
            yield break;
        }

        if (log)
        {
            Debug.Log($"[TileStream] {(isServer ? "Server" : "Client")} unloading {path}");
        }

        UnwireNeighbors(path);

        var op = SceneManager.UnloadSceneAsync(scene);
        if (op == null)
        {
            Debug.LogWarning($"[TileStream] Failed to start unloading scene {path}");
            yield break;
        }

        while (!op.isDone)
        {
            yield return null;
        }
    }

    private IEnumerator UnloadAllTiles(HashSet<string> tiles, bool isServer)
    {
        var paths = tiles.ToList();
        foreach (var path in paths)
        {
            yield return loader.Unload(path, isServer, logActions);
        }

        tiles.Clear();
    }
    private void IncrementServerLoadsThisFrame()
    {
        int frame = Time.frameCount;
        if (frame != serverLoadFrame)
        {
            serverLoadFrame = frame;
            ServerLoadsThisFrame = 0;
        }

        ServerLoadsThisFrame++;
    }

    private void IncrementClientLoadsThisFrame()
    {
        int frame = Time.frameCount;
        if (frame != clientLoadFrame)
        {
            clientLoadFrame = frame;
            ClientLoadsThisFrame = 0;
        }

        ClientLoadsThisFrame++;
    }
    
    private void WireTerrainNeighbors(string scenePath)
    {
        if (index == null || string.IsNullOrEmpty(scenePath)) return;
        if (!index.TryGetByScene(scenePath, out var record)) return;

        var scene = SceneManager.GetSceneByPath(scenePath);
        if (!scene.isLoaded) return;

        Terrain center = FindTerrainInScene(scene);
        if (center == null) return;

        Terrain left = FindNeighborTerrain(record.coord + Vector2Int.left);
        Terrain right = FindNeighborTerrain(record.coord + Vector2Int.right);
        Terrain top = FindNeighborTerrain(record.coord + Vector2Int.up);
        Terrain bottom = FindNeighborTerrain(record.coord + Vector2Int.down);

        center.SetNeighbors(left, right, top, bottom);

        if (left != null) left.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.left * 2), center, FindNeighborTerrain(record.coord + Vector2Int.left + Vector2Int.up), FindNeighborTerrain(record.coord + Vector2Int.left + Vector2Int.down));
        if (right != null) right.SetNeighbors(center, FindNeighborTerrain(record.coord + Vector2Int.right * 2), FindNeighborTerrain(record.coord + Vector2Int.right + Vector2Int.up), FindNeighborTerrain(record.coord + Vector2Int.right + Vector2Int.down));
        if (top != null) top.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.up + Vector2Int.left), FindNeighborTerrain(record.coord + Vector2Int.up + Vector2Int.right), FindNeighborTerrain(record.coord + Vector2Int.up * 2), center);
        if (bottom != null) bottom.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.down + Vector2Int.left), FindNeighborTerrain(record.coord + Vector2Int.down + Vector2Int.right), center, FindNeighborTerrain(record.coord + Vector2Int.down * 2));
    }

    private void UnwireNeighbors(string scenePath)
    {
        if (index == null || string.IsNullOrEmpty(scenePath)) return;
        if (!index.TryGetByScene(scenePath, out var record)) return;

        ClearNeighborSide(record.coord + Vector2Int.left, neighbor => neighbor.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.left * 2), null, FindNeighborTerrain(record.coord + Vector2Int.left + Vector2Int.up), FindNeighborTerrain(record.coord + Vector2Int.left + Vector2Int.down)));
        ClearNeighborSide(record.coord + Vector2Int.right, neighbor => neighbor.SetNeighbors(null, FindNeighborTerrain(record.coord + Vector2Int.right * 2), FindNeighborTerrain(record.coord + Vector2Int.right + Vector2Int.up), FindNeighborTerrain(record.coord + Vector2Int.right + Vector2Int.down)));
        ClearNeighborSide(record.coord + Vector2Int.up, neighbor => neighbor.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.up + Vector2Int.left), FindNeighborTerrain(record.coord + Vector2Int.up + Vector2Int.right), FindNeighborTerrain(record.coord + Vector2Int.up * 2), null));
        ClearNeighborSide(record.coord + Vector2Int.down, neighbor => neighbor.SetNeighbors(FindNeighborTerrain(record.coord + Vector2Int.down + Vector2Int.left), FindNeighborTerrain(record.coord + Vector2Int.down + Vector2Int.right), null, FindNeighborTerrain(record.coord + Vector2Int.down * 2)));
    }

    private void ClearNeighborSide(Vector2Int coord, System.Action<Terrain> clearer)
    {
        var neighbor = FindNeighborTerrain(coord);
        if (neighbor == null) return;
        clearer?.Invoke(neighbor);
    }

    private Terrain FindNeighborTerrain(Vector2Int coord)
    {
        if (index != null && index.TryGetByCoord(coord, out var neighborRecord))
        {
            var neighborScene = SceneManager.GetSceneByPath(neighborRecord.scenePath);
            if (neighborScene.isLoaded)
            {
                return FindTerrainInScene(neighborScene);
            }
        }

        return null;
    }

    private static Terrain FindTerrainInScene(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;

        foreach (var root in scene.GetRootGameObjects())
        {
            var terrain = root.GetComponentInChildren<Terrain>(true);
            if (terrain != null) return terrain;
        }

        return null;
    }

    private Scene GetOrCreateTemporaryActiveScene()
    {
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.IsValid() && s.isLoaded && s != SceneManager.GetActiveScene() && s.path != masterTerrainScenePath)
            {
                return s;
            }
        }

        return SceneManager.CreateScene("TileStream_TempActive");
    }

    private sealed class TileInstance
    {
        public TileIndex.TileRecord Record { get; }
        public Scene Scene { get; }

        public TileInstance(TileIndex.TileRecord record, Scene scene)
        {
            Record = record;
            Scene = scene;
        }
    }

    private sealed class TileLoader
    {
        private readonly TileStreamCoordinator owner;
        private readonly Dictionary<string, TileInstance> live;

        public TileLoader(TileStreamCoordinator owner, Dictionary<string, TileInstance> live)
        {
            this.owner = owner;
            this.live = live;
        }

        public IEnumerator Load(string path, bool isServer, bool log)
        {
            if (string.IsNullOrEmpty(path)) yield break;
            if (live.ContainsKey(path) && live[path].Scene.isLoaded) yield break;

            var existing = SceneManager.GetSceneByPath(path);
            if (existing.isLoaded)
            {
                if (owner.index != null && owner.index.TryGetByScene(path, out var record))
                {
                    live[path] = new TileInstance(record, existing);
                    owner.firstTileLoadConfirmed = true;
                    owner.WireTerrainNeighbors(path);
                    if (isServer) owner.IncrementServerLoadsThisFrame(); else owner.IncrementClientLoadsThisFrame();
                }
                
                yield return owner.EnsureMasterTerrainDisabled();
                yield break;
            }

            yield return owner.LoadSceneInternal(path, isServer, log);
            
            if (live.TryGetValue(path, out var loaded) && loaded.Scene.isLoaded)
            {
                yield return owner.EnsureMasterTerrainDisabled();
            }
            else
            {
                owner.WarnIfMasterDisabledWithoutLoadedTiles(path);
            }
        }

        public IEnumerator Unload(string path, bool isServer, bool log)
        {
            if (string.IsNullOrEmpty(path)) yield break;
            if (!live.ContainsKey(path)) yield break;

            yield return owner.UnloadSceneInternal(path, isServer, log);
            live.Remove(path);
        }
    }

    private sealed class TileStreamer
    {
        private readonly TileIndex index;
        private readonly List<Vector3> cachedPositions = new();
        private readonly Dictionary<Vector2Int, List<TileIndex.TileRecord>> tilesByCoord = new();
        private readonly List<TileIndex.TileRecord> coordQueryScratch = new();
        private readonly HashSet<string> candidatePathScratch = new();
        private readonly float minTileDimension;
        private readonly float maxTileHalfDiagonal;

        public TileStreamer(TileIndex index)
        {
            this.index = index;
            
            if (index == null)
            {
                minTileDimension = 1f;
                maxTileHalfDiagonal = 0f;
                return;
            }

            var tileSize = index.TileSizeMeters;
            minTileDimension = Mathf.Max(1f, Mathf.Min(Mathf.Abs(tileSize.x), Mathf.Abs(tileSize.y)));

            float computedMaxHalfDiagonal = 0f;
            var tiles = index.Tiles;
            for (int i = 0; i < tiles.Count; ++i)
            {
                var record = tiles[i];
                if (string.IsNullOrEmpty(record.scenePath))
                {
                    continue;
                }

                if (!tilesByCoord.TryGetValue(record.coord, out var bucket))
                {
                    bucket = new List<TileIndex.TileRecord>(1);
                    tilesByCoord.Add(record.coord, bucket);
                }

                bucket.Add(record);

                float halfDiagonal = record.worldBounds.extents.magnitude;
                if (halfDiagonal > computedMaxHalfDiagonal)
                {
                    computedMaxHalfDiagonal = halfDiagonal;
                }
            }

            maxTileHalfDiagonal = computedMaxHalfDiagonal;
        }

        public void ComputeDesired(IEnumerable<Vector3> positions, float loadRadiusSquared, float unloadRadiusSquared, HashSet<string> current, HashSet<string> output)
        {
            output.Clear();
            if (index == null) return;

            cachedPositions.Clear();
            foreach (var pos in positions)
            {
                cachedPositions.Add(pos);
                AddTilesWithinRadius(pos, output, loadRadiusSquared);
            }

            if (cachedPositions.Count > 0 && unloadRadiusSquared > loadRadiusSquared)
            {
                MaintainHysteresis(current, output, cachedPositions, unloadRadiusSquared);
            }
        }

        private void AddTilesWithinRadius(Vector3 position, HashSet<string> destination, float radiusSquared)
        {
            CollectCandidateTiles(position, radiusSquared, coordQueryScratch);
            candidatePathScratch.Clear();

            for (int i = 0; i < coordQueryScratch.Count; ++i)
            {
                var record = coordQueryScratch[i];
                if (!candidatePathScratch.Add(record.scenePath))
                {
                    continue;
                }

                if (record.worldBounds.SqrDistance(position) <= radiusSquared)
                {
                    destination.Add(record.scenePath);
                }
            }
        }

        private void CollectCandidateTiles(Vector3 position, float radiusSquared, List<TileIndex.TileRecord> destination)
        {
            destination.Clear();
            if (tilesByCoord.Count == 0)
            {
                return;
            }

            float radius = Mathf.Sqrt(Mathf.Max(0f, radiusSquared));
            float paddedRadius = radius + maxTileHalfDiagonal;
            int coordRadius = Mathf.Max(0, Mathf.CeilToInt(paddedRadius / minTileDimension));

            Vector2Int centerCoord = index.WorldToTile(position);
            for (int dx = -coordRadius; dx <= coordRadius; ++dx)
            {
                for (int dy = -coordRadius; dy <= coordRadius; ++dy)
                {
                    var coord = new Vector2Int(centerCoord.x + dx, centerCoord.y + dy);
                    if (tilesByCoord.TryGetValue(coord, out List<TileIndex.TileRecord> bucket))
                    {
                        destination.AddRange(bucket);
                    }
                }
            }
        }

        private void MaintainHysteresis(HashSet<string> currentTiles, HashSet<string> desiredTiles, IList<Vector3> playerPositions, float unloadRadiusSquared)
        {
            foreach (var path in currentTiles)
            {
                if (desiredTiles.Contains(path))
                {
                    continue;
                }

                if (!index.TryGetByScene(path, out var record))
                {
                    continue;
                }

                Vector3 center = record.worldBounds.center;
                for (int i = 0; i < playerPositions.Count; ++i)
                {
                    if ((center - playerPositions[i]).sqrMagnitude <= unloadRadiusSquared)
                    {
                        desiredTiles.Add(path);
                        break;
                    }
                }
            }
        }
    }
    
    private IEnumerator ResolvePlayerLoop()
    {
        var wait = new WaitForSeconds(0.25f);

        while (isActiveAndEnabled)
        {
            bool changed = TryResolvePlayerAndOfflineTarget();
            if (changed && logActions)
            {
                Debug.Log($"[TileStream] Resolved target: {(player != null ? player.name : "null")} / offlineTarget: {(offlineTarget != null ? offlineTarget.name : "null")}");
            }

            yield return wait;
        }
    }

}
