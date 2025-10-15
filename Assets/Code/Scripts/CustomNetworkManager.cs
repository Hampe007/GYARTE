using UnityEngine;
using Mirror;
using UnityEngine.SceneManagement;
using Steamworks;
using System.Collections.Generic;

public class CustomNetworkManager : NetworkManager
{
[SerializeField] private PlayerObjectController GamePlayerPrefab;
    public List<PlayerObjectController> GamePlayers { get; } = new List<PlayerObjectController>();

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
        // LOBBY: custom spawn exactly like you had before
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Lobby")
        {
            PlayerObjectController instance = Instantiate(GamePlayerPrefab);
            instance.connectionID     = conn.connectionId;
            instance.playerIDNumber   = GamePlayers.Count + 1;
            instance.playerSteamID    = (ulong)SteamMatchmaking.GetLobbyMemberByIndex(
                (CSteamID)SteamLobby.instance.currentLobbyID,
                GamePlayers.Count);

            NetworkServer.AddPlayerForConnection(conn, instance.gameObject);
            
        }
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

        // host local UX (optional)
        if (LobbyController.instance != null)
            LobbyController.instance.ShowBlackout();
    }
    
    public struct ServerShutdownMsg : NetworkMessage // Tells clients the host is shutting down on purpose
    {
        public string reason; // e.g., "host_exit"
    }
    
    public override void OnServerSceneChanged(string sceneName)
    {
        base.OnServerSceneChanged(sceneName);

        try
        {
            if (sceneName != "Game") return; // only swap in the Game scene

            // 1) Find the SpawnPointManager in the newly loaded Game scene
            var spawnMgr = Object.FindFirstObjectByType<SpawnPointManager>(FindObjectsInactive.Include);
            if (spawnMgr != null) spawnMgr.ResetAll();

            // 2) For each connected client, replace their lobby player with a game character at the chosen spawn
            foreach (var kvp in NetworkServer.connections)
            {
                var conn = kvp.Value;
                if (conn == null) continue;

                // Attempt to read SteamID from the existing lobby identity before replacing
                ulong steamId = 0;
                if (conn.identity != null)
                {
                    var lobbyComp = conn.identity.GetComponent<PlayerObjectController>();
                    if (lobbyComp != null) steamId = lobbyComp.playerSteamID; // comes from your lobby player SyncVar
                }

                Vector3 pos = Vector3.zero;
                Quaternion rot = Quaternion.identity;

                if (spawnMgr != null)
                {
                    (pos, rot) = spawnMgr.GetSpawnFor(steamId);
                }

                var newPlayer = Instantiate(GameCharacterPrefab, pos, rot);
                NetworkServer.ReplacePlayerForConnection(conn, newPlayer);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }
}