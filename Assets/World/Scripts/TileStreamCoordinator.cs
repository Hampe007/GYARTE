using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    [SerializeField] private float loadRadius = 500f;
    [SerializeField] private float edgeBuffer = 25f;
    public float scanInterval = 0.5f;
    public bool logActions = false;

    private readonly HashSet<string> serverLoaded = new();
    private readonly HashSet<string> clientLoaded = new();
    private readonly List<Vector3> tempPlayerPositions = new();
    private static readonly Dictionary<string, Type> cinemachineTypeCache = new();

    private float loadRadiusSquared;
    private float unloadRadiusSquared;
    private float cachedLoadRadius = -1f;
    private float cachedEdgeBuffer = -1f;

    private Coroutine serverLoop;
    private Coroutine clientApplyCoroutine;
    
    public bool offlineStandalone = true;
    public Transform offlineTarget;
    private Coroutine offlineLoop;

    public IReadOnlyCollection<string> ServerTiles => serverLoaded;
    public IReadOnlyCollection<string> ClientTiles => clientLoaded;
    
    public int ServerQueuedLoads { get; private set; }
    public int ClientQueuedLoads { get; private set; }
    public int ServerLoadsThisFrame { get; private set; }
    public int ClientLoadsThisFrame { get; private set; }

    private int serverLoadFrame = -1;
    private int clientLoadFrame = -1;

    private bool EnsureIndexLoaded()
    {
        if (index != null)
        {
            return true;
        }

        index = Resources.Load<TileIndex>("TileIndex");
        return index != null;
    }

    private void TryStartOfflineLoop()
    {
        if (!offlineStandalone || offlineLoop != null || !isActiveAndEnabled)
        {
            return;
        }

        if (NetworkServer.active || NetworkClient.active)
        {
            return;
        }

        if (!EnsureIndexLoaded())
        {
            return;
        }

        offlineLoop = StartCoroutine(OfflineLoop());
    }

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
        TryStartOfflineLoop();
    }

    private void OnValidate()
    {
        UpdateRadiusCache();
    }

    private void LateUpdate()
    {
        UpdateRadiusCache();
        TryStartOfflineLoop();
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

            Vector3 center = record.worldBounds.center;
            if ((center - position).sqrMagnitude <= radiusSquared)
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
        Transform target = ResolveStreamingAnchor();
        if (target == null && !Application.isPlaying)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
            {
                target = sceneView.camera.transform;
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
        TryStartOfflineLoop();
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
            if (!EnsureIndexLoaded())
            {
                ClientQueuedLoads = 0;
                yield return wait;
                continue;
            }

            Transform t = ResolveStreamingAnchor();
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

        offlineLoop = null;
    }

    private Transform ResolveStreamingAnchor()
    {
        if (offlineTarget != null)
        {
            return offlineTarget;
        }

        if (player != null)
        {
            return player;
        }

        if (TryGetCinemachineTransform(out var cinemachineTransform))
        {
            return cinemachineTransform;
        }

        var mainCamera = Camera.main;
        if (mainCamera != null)
        {
            return mainCamera.transform;
        }

        return null;
    }

    private bool TryGetCinemachineTransform(out Transform transform)
    {
        transform = null;

        var dolly = FindFirstCinemachineComponent(
            "Unity.Cinemachine.CinemachineSplineDollyCart",
            "Unity.Cinemachine.CinemachineDollyCart",
            "Cinemachine.CinemachineSplineDollyCart",
            "Cinemachine.CinemachineDollyCart");
        if (dolly != null)
        {
            var type = dolly.GetType();

            var cartTransform = GetTransformProperty(type, dolly, "Cart");
            if (cartTransform == null)
            {
                cartTransform = GetTransformProperty(type, dolly, "CartTransform");
            }

            if (cartTransform != null)
            {
                transform = cartTransform;
                return true;
            }

            if (dolly is Component component)
            {
                transform = component.transform;
                if (transform != null)
                {
                    return true;
                }
            }
        }

        var brain = FindFirstCinemachineComponent(
            "Unity.Cinemachine.CinemachineBrain",
            "Cinemachine.CinemachineBrain");
        if (brain != null)
        {
            if (brain is Behaviour behaviour)
            {
                if (behaviour.isActiveAndEnabled)
                {
                    transform = behaviour.transform;
                    if (transform != null)
                    {
                        return true;
                    }
                }
            }
            else if (brain is Component component && component.gameObject.activeInHierarchy)
            {
                transform = component.transform;
                if (transform != null)
                {
                    return true;
                }
            }
        }

        var cineCamera = FindFirstCinemachineComponent(
            "Unity.Cinemachine.CinemachineCamera",
            "Cinemachine.CinemachineVirtualCameraBase",
            "Cinemachine.CinemachineVirtualCamera");
        if (cineCamera is Component cameraComponent && cameraComponent.gameObject.activeInHierarchy)
        {
            transform = cameraComponent.transform;
            return transform != null;
        }

        return false;
    }

    private static Transform GetTransformProperty(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null && typeof(Transform).IsAssignableFrom(property.PropertyType))
        {
            return property.GetValue(instance) as Transform;
        }

        var field = type.GetField(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null && typeof(Transform).IsAssignableFrom(field.FieldType))
        {
            return field.GetValue(instance) as Transform;
        }

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < properties.Length; ++i)
        {
            if (properties[i].Name == propertyName || properties[i].Name == "transform")
            {
                continue;
            }

            if (typeof(Transform).IsAssignableFrom(properties[i].PropertyType))
            {
                return properties[i].GetValue(instance) as Transform;
            }
        }

        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < fields.Length; ++i)
        {
            if (fields[i].Name == propertyName)
            {
                continue;
            }

            if (typeof(Transform).IsAssignableFrom(fields[i].FieldType))
            {
                return fields[i].GetValue(instance) as Transform;
            }
        }

        return null;
    }

    private Component FindFirstCinemachineComponent(params string[] qualifiedNames)
    {
        for (int i = 0; i < qualifiedNames.Length; ++i)
        {
            Type type = ResolveType(qualifiedNames[i]);
            if (type == null)
            {
                continue;
            }

            var objects = Resources.FindObjectsOfTypeAll(type);
            for (int j = 0; j < objects.Length; ++j)
            {
                if (objects[j] is Component component && component.transform != null && component.gameObject.scene.IsValid())
                {
                    if (component is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                    {
                        continue;
                    }

                    return component;
                }
            }
        }

        return null;
    }

    private static Type ResolveType(string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return null;
        }

        if (cinemachineTypeCache.TryGetValue(fullName, out var cached))
        {
            return cached;
        }

        var type = Type.GetType(fullName);
        if (type == null)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; ++i)
            {
                try
                {
                    type = assemblies[i].GetType(fullName);
                }
                catch (ReflectionTypeLoadException)
                {
                    continue;
                }

                if (type != null)
                {
                    break;
                }
            }
        }

        cinemachineTypeCache[fullName] = type;
        return type;
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
