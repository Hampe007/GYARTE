using UnityEngine;
using Mirror;
using UnityEngine.SceneManagement;
using Steamworks;
using System.Collections.Generic;

public class CustomNetworkManager : NetworkManager
{
    [SerializeField] private PlayerObjectController GamePlayerPrefab;
    public List<PlayerObjectController> GamePlayers { get; } = new List<PlayerObjectController>();
    private readonly Dictionary<int, ulong> _connToSteam = new(); // server-only: connectionId -> steamId
    private bool _spawnMgrResetInGameScene = false;

    // Background scene preloading (server/host only)
    private AsyncOperation _preloadedSceneOp;
    private string _preloadedSceneName;

    // Add under your existing fields in CustomNetworkManager
    [SerializeField] private GameObject GameCharacterPrefab; // your in-game prefab

    // Use Mirror's NetworkStartPosition objects in the Game scene?
    [SerializeField] private bool useNetworkStartPositions = true;

    // Optional: explicit per-player spawn coordinates by SteamID
    [System.Serializable]
    public struct ExplicitSpawn
    {
        public ulong steamId;          // Steamworks ID
        public Vector3 position;       // x,y,z
        public Vector3 rotationEuler;  // yaw/pitch/roll (usually yaw only)
    }
    [SerializeField] private List<ExplicitSpawn> explicitSpawns = new();

    private bool TryGetExplicitSpawn(ulong steamId, out Vector3 pos, out Quaternion rot)
    {
        foreach (var e in explicitSpawns)
        {
            if (e.steamId == steamId)
            {
                pos = e.position;
                rot = Quaternion.Euler(e.rotationEuler);
                return true;
            }
        }
        pos = default;
        rot = default;
        return false;
    }

    // Optional helper if you want to set spawns via code before pressing Start:
    public void SetExplicitSpawn(ulong steamId, Vector3 position, Vector3 rotationEuler)
    {
        for (int i = 0; i < explicitSpawns.Count; i++)
        {
            if (explicitSpawns[i].steamId == steamId)
            {
                explicitSpawns[i] = new ExplicitSpawn { steamId = steamId, position = position, rotationEuler = rotationEuler };
                return;
            }
        }
        explicitSpawns.Add(new ExplicitSpawn { steamId = steamId, position = position, rotationEuler = rotationEuler });
    }
    
    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        string activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        bool isLobbyScene = IsLobbyScene(activeScene);
        bool isGameScene = IsGameScene(activeScene);

        // Prevent double-spawns if Mirror invokes this while a player already exists for the connection
        if (conn.identity != null)
        {
            Debug.LogWarning($"[Net] Connection {conn.connectionId} already has a player in scene '{activeScene}'. Skipping OnServerAddPlayer.");
            return;
        }

        if (isLobbyScene)
        {
            PlayerObjectController instance = Instantiate(GamePlayerPrefab);
            instance.connectionID     = conn.connectionId;
            instance.playerIDNumber   = GamePlayers.Count + 1;
            instance.playerSteamID    = (ulong)SteamMatchmaking.GetLobbyMemberByIndex(
                (CSteamID)SteamLobby.instance.currentLobbyID,
                GamePlayers.Count);

            // cache steamId for the later game-scene spawn
            _connToSteam[conn.connectionId] = instance.playerSteamID;

            NetworkServer.AddPlayerForConnection(conn, instance.gameObject);
            return;
        }

        if (isGameScene)
        {
            SpawnOrReplaceGamePlayer(conn);
            return;
        }

        // Default fallback: if some unexpected scene, use base behaviour
        base.OnServerAddPlayer(conn);
    }
    
    public override void OnStartClient()
    {
        base.OnStartClient();
        NetworkClient.RegisterHandler<ServerShutdownMsg>(msg =>
        {
            LobbyController.ServerShutdownReceived = true;
            LobbyController.ServerShutdownReason = msg.reason;
            Debug.Log($"[Net] ServerShutdownMsg received: {msg.reason}");
        });
    }

    public override void OnStopClient()
    {
        base.OnStopClient();

        // existing Steam lobby cleanup
        var sl = GetComponent<SteamLobby>();
        if (sl != null && sl.currentLobbyID != 0)
        {
            Debug.Log($"[Quit] OnStopClient -> Leaving Steam lobby {sl.currentLobbyID}");
            SteamMatchmaking.LeaveLobby(new CSteamID(sl.currentLobbyID));
            sl.currentLobbyID = 0;
        }

        // choose overlay during Offline Scene Load Delay
        if (LobbyController.instance != null)
        {
            if (LobbyController.LocalQuitInitiated)
            {
                LobbyController.instance.ShowBlackout();
            }
            else
            {
                if (LobbyController.ServerShutdownReceived &&
                    LobbyController.ServerShutdownReason == "host_exit")
                {
                    LobbyController.instance.ShowHostExitedOverlay();
                }
                else
                {
                    LobbyController.instance.ShowConnectionLostOverlay();
                }
            }
        }
    }

    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        if (conn == null)
        {
            return;
        }

        _connToSteam.Remove(conn.connectionId);

        if (conn.identity != null)
        {
            var playerObj = conn.identity.GetComponent<PlayerObjectController>();
            if (playerObj != null)
            {
                GamePlayers.Remove(playerObj);
            }
        }

        if (!NetworkServer.active)
        {
            return;
        }

        base.OnServerDisconnect(conn);
    }

    public override void OnStopHost()
    {
        base.OnStopHost();

        // existing Steam lobby cleanup
        var sl = GetComponent<SteamLobby>();
        if (sl != null && sl.currentLobbyID != 0)
        {
            Debug.Log($"[Quit] OnStopHost -> Leaving Steam lobby {sl.currentLobbyID}");
            SteamMatchmaking.LeaveLobby(new CSteamID(sl.currentLobbyID));
            sl.currentLobbyID = 0;
        }

        GamePlayers.Clear();
        _connToSteam.Clear();
        _spawnMgrResetInGameScene = false;

        // host local UX (optional)
        if (LobbyController.instance != null)
            LobbyController.instance.ShowBlackout();
    }
    
    public struct ServerShutdownMsg : NetworkMessage // Tells clients the host is shutting down on purpose
    {
        public string reason; // e.g., "host_exit"
    }
    
    private void Awake()
    {
        if (spawnPrefabs == null)
            spawnPrefabs = new List<GameObject>();

        // Ensure both prefabs are in spawnPrefabs so clients can spawn them.
        // This is defensive; you can also wire it in the inspector.
        if (GamePlayerPrefab != null && !spawnPrefabs.Contains(GamePlayerPrefab.gameObject))
            spawnPrefabs.Add(GamePlayerPrefab.gameObject);

        if (GameCharacterPrefab != null && !spawnPrefabs.Contains(GameCharacterPrefab))
            spawnPrefabs.Add(GameCharacterPrefab);
    }

    public override void OnServerSceneChanged(string sceneName)
    {
        base.OnServerSceneChanged(sceneName);

        try
        {
            if (!IsGameScene(sceneName))
            {
                _spawnMgrResetInGameScene = false;
                return;
            }

            // Reset spawn manager once when entering the game scene.
            var spawnMgr = Object.FindFirstObjectByType<SpawnPointManager>(FindObjectsInactive.Include);
            if (spawnMgr != null)
            {
                spawnMgr.ResetAll();
                _spawnMgrResetInGameScene = true;
            }

            // Actual replacement/add happens in OnServerReady after clients are ready.
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    public override void OnServerReady(NetworkConnectionToClient conn)
    {
        base.OnServerReady(conn);

        if (IsGameScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name))
        {
            SpawnOrReplaceGamePlayer(conn);
        }
    }

    public override void OnClientSceneChanged()
    {
        // Always send Ready but avoid Mirror's auto AddPlayer when we already have one.
        if (NetworkClient.connection != null &&
            NetworkClient.connection.isAuthenticated &&
            !NetworkClient.ready)
        {
            NetworkClient.Ready();
        }

        var conn = NetworkClient.connection;
        bool alreadyHasPlayer = NetworkClient.localPlayer != null ||
                                (conn != null && conn.identity != null);
        if (alreadyHasPlayer)
            return;

        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        // Only request AddPlayer in Lobby; in Game scenes the server swaps us in via OnServerReady
        if (IsLobbyScene(sceneName))
        {
            NetworkClient.AddPlayer();
        }
    }

    private void SpawnOrReplaceGamePlayer(NetworkConnectionToClient conn)
    {
        if (GameCharacterPrefab == null)
        {
            Debug.LogError("[Net] GameCharacterPrefab is not assigned on CustomNetworkManager.");
            return;
        }

        var prefabIdentity = GameCharacterPrefab.GetComponent<NetworkIdentity>();

        ulong steamId = 0;
        if (_connToSteam.TryGetValue(conn.connectionId, out var cachedSteam))
            steamId = cachedSteam;

        if (conn.identity != null)
        {
            var lobbyComp = conn.identity.GetComponent<PlayerObjectController>();
            if (lobbyComp != null) steamId = lobbyComp.playerSteamID;

            // Already a game character? Skip extra replace.
            if (prefabIdentity != null)
            {
                var currentIdentity = conn.identity.GetComponent<NetworkIdentity>();
                if (currentIdentity != null && currentIdentity.assetId == prefabIdentity.assetId)
                    return;
            }
        }

        Vector3 pos = Vector3.zero;
        Quaternion rot = Quaternion.identity;

        var spawnMgr = Object.FindFirstObjectByType<SpawnPointManager>(FindObjectsInactive.Include);
        if (spawnMgr != null)
        {
            // Only reset once per scene entry to avoid wiping usage while players join mid-game
            if (!_spawnMgrResetInGameScene)
            {
                spawnMgr.ResetAll();
                _spawnMgrResetInGameScene = true;
            }
            (pos, rot) = spawnMgr.GetSpawnFor(steamId);
        }

        var newPlayer = Instantiate(GameCharacterPrefab, pos, rot);

        if (conn.identity != null)
        {
            NetworkServer.ReplacePlayerForConnection(conn, newPlayer, ReplacePlayerOptions.Destroy);
        }
        else
        {
            NetworkServer.AddPlayerForConnection(conn, newPlayer);
        }
    }

    public void BeginPreloadGameScene(string sceneName)
    {
        // Only the server/host controls authoritative scene changes.
        if (!NetworkServer.active)
            return;

        if (string.IsNullOrWhiteSpace(sceneName))
            return;

        // Only support preloading for scenes considered "game scenes".
        if (!IsGameScene(sceneName))
            return;

        // Don't start a second preload or interfere with Mirror's own load.
        if (_preloadedSceneOp != null || loadingSceneAsync != null)
            return;

        // If we are already in that scene, no need to preload.
        if (SceneManager.GetActiveScene().name == sceneName)
            return;

        Debug.Log($"[Net] Begin preloading scene '{sceneName}' in background (server/host).");
        _preloadedSceneName = sceneName;
        _preloadedSceneOp = SceneManager.LoadSceneAsync(sceneName);
        if (_preloadedSceneOp != null)
            _preloadedSceneOp.allowSceneActivation = false;
    }

    public override void ServerChangeScene(string newSceneName)
    {
        // If we have a matching preloaded scene operation, reuse it on the server.
        if (_preloadedSceneOp != null && !string.IsNullOrWhiteSpace(newSceneName) && newSceneName == _preloadedSceneName)
        {
            if (NetworkServer.isLoadingScene && newSceneName == networkSceneName)
            {
                Debug.LogError($"Scene change is already in progress for {newSceneName}");
                return;
            }

            // Throw error if called from client
            // Allow changing scene while stopping the server
            if (!NetworkServer.active && newSceneName != offlineScene)
            {
                Debug.LogError("ServerChangeScene can only be called on an active server.");
                return;
            }

            // Debug.Log($"ServerChangeScene {newSceneName} (using preloaded op)");
            NetworkServer.SetAllClientsNotReady();
            networkSceneName = newSceneName;

            // Let server prepare for scene change
            OnServerChangeScene(newSceneName);

            // set server flag to stop processing messages while changing scenes
            // it will be re-enabled in FinishLoadScene.
            NetworkServer.isLoadingScene = true;

            // Hand our preloaded op to Mirror and allow it to activate now.
            loadingSceneAsync = _preloadedSceneOp;
            _preloadedSceneOp.allowSceneActivation = true;
            _preloadedSceneOp = null;
            _preloadedSceneName = null;

            // ServerChangeScene can be called when stopping the server
            // when this happens the server is not active so does not need to tell clients about the change
            if (NetworkServer.active)
            {
                // notify all clients about the new scene
                NetworkServer.SendToAll(new SceneMessage
                {
                    sceneName = newSceneName
                });
            }

            startPositionIndex = 0;
            startPositions.Clear();
            return;
        }

        // No valid preload, fall back to default Mirror behaviour.
        base.ServerChangeScene(newSceneName);
    }

    private static bool IsGameScene(string sceneName)
        => sceneName == "GameExp1" || sceneName == "Terrain" || sceneName == "Game Exp" || sceneName == "Game";

    private static bool IsLobbyScene(string sceneName)
        => sceneName == "Lobby";
}
