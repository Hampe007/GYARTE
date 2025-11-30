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
        _connToSteam.Remove(conn.connectionId);
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
            NetworkServer.ReplacePlayerForConnection(conn, newPlayer, true);
        }
        else
        {
            NetworkServer.AddPlayerForConnection(conn, newPlayer);
        }
    }

    private static bool IsGameScene(string sceneName)
        => sceneName == "GameExp1" || sceneName == "Terrain" || sceneName == "Game Exp" || sceneName == "Game";

    private static bool IsLobbyScene(string sceneName)
        => sceneName == "Lobby";
}
