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
    [SerializeField] private Transform player;
    [SerializeField] public float loadRadius = 500f;
    [SerializeField] private float edgeBuffer = 25f;
    public float scanInterval = 0.5f;
    public bool logActions = false;

    private readonly HashSet<string> serverLoaded = new();
    private readonly HashSet<string> clientLoaded = new();
    private readonly List<Vector3> tempPlayerPositions = new();

    private float loadRadiusSquared;
    private float unloadRadiusSquared;
    private float cachedLoadRadius = -1f;
    private float cachedEdgeBuffer = -1f;

    private Coroutine serverLoop;
    private Coroutine clientApplyCoroutine;
    
    public bool offlineStandalone = true;
    public Transform offlineTarget;
    private Coroutine offlineLoop;

    [SerializeField] private Terrain masterTerrain;
    [SerializeField] private bool disableMasterOnStart = true;
    [SerializeField] private bool unloadMasterTerrainScene = true;

    private string masterTerrainScenePath = string.Empty;
    private bool masterSceneUnloaded = false;

    private bool masterDisabled = false;
    private bool masterWorkRunning = false;

    public IReadOnlyCollection<string> ServerTiles => serverLoaded;
    public IReadOnlyCollection<string> ClientTiles => clientLoaded;
    
    public int ServerQueuedLoads { get; private set; }
    public int ClientQueuedLoads { get; private set; }
    public int ServerLoadsThisFrame { get; private set; }
    public int ClientLoadsThisFrame { get; private set; }

    private int serverLoadFrame = -1;
    private int clientLoadFrame = -1;

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

        serverLoaded.Clear();

        base.OnStopServer();
    }

    private void Awake()
    {
        UpdateRadiusCache();
        CacheMasterTerrainScene();
        masterDisabled = masterTerrain == null || !masterTerrain.gameObject.activeSelf;
        masterSceneUnloaded = string.IsNullOrEmpty(masterTerrainScenePath) || !SceneManager.GetSceneByPath(masterTerrainScenePath).isLoaded;
    }

    private void OnValidate()
    {
        UpdateRadiusCache();
    }

    private void Update()
    {
        // If streaming is active, ensure the master terrain is disabled/unloaded so we don't double load
        bool streamingActive = clientLoaded.Count > 0 || serverLoaded.Count > 0;

        bool masterActive = masterTerrain != null && masterTerrain.gameObject.activeSelf;
        masterDisabled = !masterActive; // track actual state instead of only the cached flag

        bool needDisable = streamingActive && disableMasterOnStart && masterActive;
        bool needUnload = streamingActive && unloadMasterTerrainScene && !masterSceneUnloaded;

        if ((needDisable || needUnload) && !masterWorkRunning)
        {
            StartCoroutine(EnsureMasterTerrainDisabled());
        }
    }
    
    private void LateUpdate()
    {
        UpdateRadiusCache();
    }

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

        var desired = new HashSet<string>();
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
            AddTilesWithinRadius(position, desired, loadRadiusSquared);
        }

        if (tempPlayerPositions.Count == 0 && player != null)
        {
            Vector3 position = player.position;
            tempPlayerPositions.Add(position);
            AddTilesWithinRadius(position, desired, loadRadiusSquared);
        }

        if (edgeBuffer > 0f && tempPlayerPositions.Count > 0)
        {
            MaintainHysteresis(serverLoaded, desired, tempPlayerPositions);
        }

        if (desired.SetEquals(serverLoaded))
        {
            ServerQueuedLoads = 0;
            yield break;
        }

        var toLoad = desired.Except(serverLoaded).ToList();
        var toUnload = serverLoaded.Except(desired).ToList();

        ServerQueuedLoads = toLoad.Count;
        
        foreach (var path in toLoad)
        {
            yield return LoadTileServer(path);
            ServerQueuedLoads = Mathf.Max(0, ServerQueuedLoads - 1);
        }

        foreach (var path in toUnload)
        {
            yield return UnloadTileServer(path);
        }

        serverLoaded.Clear();
        foreach (var path in desired)
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

    private IEnumerator LoadTileServer(string path)
    {
        if (string.IsNullOrEmpty(path) || serverLoaded.Contains(path))
        {
            yield break;
        }

        var existing = SceneManager.GetSceneByPath(path);
        if (existing.isLoaded)
        {
            if (logActions)
            {
                Debug.Log($"[TileStream] Server already has {path} loaded");
            }
            yield break;
        }

        yield return EnsureMasterTerrainDisabled();

        var op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogWarning($"[TileStream] Failed to start loading scene {path}");
            yield break;
        }

        if (logActions)
        {
            Debug.Log($"[TileStream] Server loading {path}");
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

        NetworkServer.SpawnObjects();
        IncrementServerLoadsThisFrame();
    }

    private IEnumerator UnloadTileServer(string path)
    {
        var scene = SceneManager.GetSceneByPath(path);
        if (!scene.isLoaded)
        {
            yield break;
        }

        if (logActions)
        {
            Debug.Log($"[TileStream] Server unloading {path}");
        }

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
            yield return LoadTileClient(path);
            ClientQueuedLoads = Mathf.Max(0, ClientQueuedLoads - 1);
        }

        foreach (var path in toUnload)
        {
            yield return UnloadTileClient(path);
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

    private IEnumerator LoadTileClient(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        var scene = SceneManager.GetSceneByPath(path);
        if (scene.isLoaded)
        {
            yield break;
        }

        yield return EnsureMasterTerrainDisabled();

        var op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogWarning($"[TileStream] Client failed to start loading scene {path}");
            yield break;
        }

        if (logActions)
        {
            Debug.Log($"[TileStream] Client loading {path}");
        }

        while (!op.isDone)
        {
            yield return null;
        }
        
        IncrementClientLoadsThisFrame();
    }

    private IEnumerator UnloadTileClient(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        var scene = SceneManager.GetSceneByPath(path);
        if (!scene.isLoaded)
        {
            yield break;
        }

        if (logActions)
        {
            Debug.Log($"[TileStream] Client unloading {path}");
        }

        var op = SceneManager.UnloadSceneAsync(scene);
        if (op == null)
        {
            Debug.LogWarning($"[TileStream] Client failed to start unloading scene {path}");
            yield break;
        }

        while (!op.isDone)
        {
            yield return null;
        }
    }

    private void AddTilesWithinRadius(Vector3 position, HashSet<string> destination, float radiusSquared)
    {
        if (index == null)
        {
            return;
        }

        var tiles = index.Tiles;
        for (int i = 0; i < tiles.Count; ++i)
        {
            var record = tiles[i];
            if (string.IsNullOrEmpty(record.scenePath))
            {
                continue;
            }

            float sqrDistance = record.worldBounds.SqrDistance(position);
            if (sqrDistance <= radiusSquared)
            {
                destination.Add(record.scenePath);
            }
        }
    }

    private void MaintainHysteresis(HashSet<string> currentTiles, HashSet<string> desiredTiles, IList<Vector3> playerPositions)
    {
        if (index == null)
        {
            return;
        }

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

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Transform target = player != null ? player : offlineTarget;
        if (target == null && !Application.isPlaying)
        {
            var camera = Camera.main;
            if (camera != null)
            {
                target = camera.transform;
            }
        }

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
        // Offline mode: run when Mirror is not active
        if (offlineStandalone && !NetworkServer.active && !NetworkClient.active && index != null)
        {
            if (offlineLoop == null) offlineLoop = StartCoroutine(OfflineLoop());
        }
    }

    private void OnDisable()
    {
        if (offlineLoop != null)
        {
            StopCoroutine(offlineLoop);
            offlineLoop = null;
        }
    }

    // Add the offline loop (local client-side streaming)
    private IEnumerator OfflineLoop()
    {
        var wait = new WaitForSeconds(scanInterval);

        var desired = new HashSet<string>();

        while (isActiveAndEnabled && !NetworkServer.active && !NetworkClient.active)
        {
            Transform t = offlineTarget != null ? offlineTarget : (player != null ? player : (Camera.main != null ? Camera.main.transform : null));
            if (t == null || index == null)
            {
                ClientQueuedLoads = 0;
                yield return wait;
                continue;
            }

            UpdateRadiusCache();

            desired.Clear();
            AddTilesWithinRadius(t.position, desired, loadRadiusSquared);

            if (edgeBuffer > 0f)
            {
                tempPlayerPositions.Clear();
                tempPlayerPositions.Add(t.position);
                MaintainHysteresis(clientLoaded, desired, tempPlayerPositions);
            }

            var toLoad = desired.Except(clientLoaded).ToList();
            var toUnload = clientLoaded.Except(desired).ToList();

            ClientQueuedLoads = toLoad.Count;

            foreach (var path in toLoad)
            {
                yield return LoadTileClient(path);
                ClientQueuedLoads = Mathf.Max(0, ClientQueuedLoads - 1);
            }

            foreach (var path in toUnload)
                yield return UnloadTileClient(path);

            clientLoaded.Clear();
            foreach (var path in desired)
            {
                if (SceneManager.GetSceneByPath(path).isLoaded)
                    clientLoaded.Add(path);
            }

            yield return wait;
        }
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
        masterWorkRunning = true;

        if (masterTerrain != null && disableMasterOnStart && masterTerrain.gameObject.activeSelf)
        {
            masterTerrain.gameObject.SetActive(false);
            masterDisabled = true;
        }

        if (unloadMasterTerrainScene && !masterSceneUnloaded && !string.IsNullOrEmpty(masterTerrainScenePath))
        {
            var scene = SceneManager.GetSceneByPath(masterTerrainScenePath);
            // Avoid unloading the active scene; assume master terrain lives in a dedicated additive scene
            if (scene.IsValid() && scene.isLoaded && scene != SceneManager.GetActiveScene())
            {
                var op = SceneManager.UnloadSceneAsync(scene);
                if (op != null)
                {
                    while (!op.isDone)
                    {
                        yield return null;
                    }

                    masterSceneUnloaded = true;
                }
            }
            else if (!scene.IsValid() || !scene.isLoaded)
            {
                masterSceneUnloaded = true;
            }
        }

        masterWorkRunning = false;
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
}