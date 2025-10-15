using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

public class TileStreamCoordinator : NetworkBehaviour
{
    [Header("Configuration")]
    public TileIndex index;
    public int innerRadius = 2;
    public int outerRadius = 3;
    public float scanInterval = 0.5f;
    public bool logActions = false;

    private readonly HashSet<string> serverLoaded = new();
    private readonly Dictionary<NetworkConnectionToClient, Vector2Int> lastCenters = new();
    private readonly HashSet<string> clientLoaded = new();

    private Coroutine serverLoop;
    private Coroutine clientApplyCoroutine;

    public IReadOnlyCollection<string> ServerTiles => serverLoaded;
    public IReadOnlyCollection<string> ClientTiles => clientLoaded;

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
        lastCenters.Clear();

        base.OnStopServer();
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

        var desired = new HashSet<string>();
        var validConnections = new HashSet<NetworkConnectionToClient>();

        foreach (var kvp in NetworkServer.connections)
        {
            var conn = kvp.Value;
            if (conn == null || conn.identity == null)
            {
                continue;
            }

            validConnections.Add(conn);

            var playerTransform = conn.identity.transform;
            var center = index.WorldToTile(playerTransform.position);

            bool moved = !lastCenters.TryGetValue(conn, out var previous) || previous != center;
            lastCenters[conn] = center;

            int radius = Mathf.Max(0, moved ? outerRadius : innerRadius);
            foreach (var path in index.CoordsToSceneSet(center, radius))
            {
                desired.Add(path);
            }
        }

        var staleConnections = lastCenters.Keys.Where(k => !validConnections.Contains(k)).ToList();
        foreach (var stale in staleConnections)
        {
            lastCenters.Remove(stale);
        }

        if (desired.SetEquals(serverLoaded))
        {
            yield break;
        }

        var toLoad = desired.Except(serverLoaded).ToList();
        var toUnload = serverLoaded.Except(desired).ToList();

        foreach (var path in toLoad)
        {
            yield return LoadTileServer(path);
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
            yield break;
        }

        var toLoad = desired.Except(clientLoaded).ToList();
        var toUnload = clientLoaded.Except(desired).ToList();

        foreach (var path in toLoad)
        {
            yield return LoadTileClient(path);
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

    private void OnDrawGizmos()
    {
        // Guard against missing index or uninitialized dictionaries
        if (index == null)
            return;

        // Ensure lookups exist (OnEnable might not have run yet in edit mode)
        var _ = index.Tiles; // forces ScriptableObject deserialization
        var tileField = typeof(TileIndex).GetField("coordLookup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (tileField?.GetValue(index) == null)
            return;

        // Skip when Mirror isn't active
        if (!Application.isPlaying)
            return;

        Vector3? focusPosition = null;

        if (isServer && NetworkServer.active && NetworkServer.connections != null)
        {
            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn?.identity != null)
                {
                    focusPosition = conn.identity.transform.position;
                    break;
                }
            }
        }
        else if (isClient && NetworkClient.localPlayer != null)
        {
            focusPosition = NetworkClient.localPlayer.transform.position;
        }

        if (!focusPosition.HasValue)
            return;

        var centerCoord = index.WorldToTile(focusPosition.Value);

        Bounds worldBounds;
        if (index.TryGetByCoord(centerCoord, out var found))
        {
            worldBounds = found.worldBounds;
        }
        else
        {
            var size = index.TileSizeMeters;
            var halfX = size.x * 0.5f;
            var halfY = size.y * 0.5f;
            worldBounds = new Bounds(
                new Vector3(centerCoord.x * size.x + halfX, focusPosition.Value.y, centerCoord.y * size.y + halfY),
                new Vector3(size.x, 0f, size.y));
        }

        var tileCenter = worldBounds.center;
        var sizeInner = new Vector3(index.TileSizeMeters.x * (innerRadius * 2 + 1), 0f,
                                    index.TileSizeMeters.y * (innerRadius * 2 + 1));
        var sizeOuter = new Vector3(index.TileSizeMeters.x * (outerRadius * 2 + 1), 0f,
                                    index.TileSizeMeters.y * (outerRadius * 2 + 1));

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireCube(tileCenter, sizeInner);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(tileCenter, sizeOuter);
    }
}
