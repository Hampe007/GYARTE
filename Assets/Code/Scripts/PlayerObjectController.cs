using UnityEngine;
using Mirror;
using Steamworks;
using System.Collections;
using System.Collections.Generic;

public class PlayerObjectController : NetworkBehaviour
{
    
    // Player Data
    [SyncVar] public int connectionID;
    [SyncVar] public int playerIDNumber;
    [SyncVar] public ulong playerSteamID;
    [SyncVar(hook = nameof(PlayerNameUpdate))] public string playerName; 
    [SyncVar(hook = nameof(PlayerReadyUpdate))] public bool readyToBegin;
    
    // Networking
    private CustomNetworkManager networkManager;
    private CustomNetworkManager NetworkManager
    {
        get
        {
            if (networkManager != null) return networkManager;

            var nm = CustomNetworkManager.singleton as CustomNetworkManager;
            if (nm == null)
                nm = Object.FindAnyObjectByType<CustomNetworkManager>(FindObjectsInactive.Include);

            networkManager = nm; // may be null if you pressed Play in Lobby scene
            return networkManager;
        }
    }

    private void Awake()
    {
        // touch the property once so it’s ready later (optional)
        _ = NetworkManager;
    }
    
    public override void OnStartAuthority()
    {
        CmdSetPlayerName(SteamFriends.GetPersonaName().ToString());
        gameObject.name = "localGamePlayer";
        if (LobbyController.instance != null)
        {
            LobbyController.instance.FindLocalPlayer();
            LobbyController.instance.UpdateLobbyName();
        }
    }

    public override void OnStartClient()
    {
        // guard against missing NM / list
        if (NetworkManager != null && NetworkManager.GamePlayers != null)
        {
            NetworkManager.GamePlayers.Add(this);
        }
        else
        {
            Debug.LogWarning("[Player] NetworkManager/GamePlayers not available in OnStartClient.");
        }

        if (LobbyController.instance != null)
        {
            LobbyController.instance.UpdateLobbyName();
            LobbyController.instance.UpdatePlayerList();
        }
    }

    public override void OnStopClient()
    {
        if (NetworkManager != null && NetworkManager.GamePlayers != null)
            NetworkManager.GamePlayers.Remove(this);

        if (LobbyController.instance != null)
            LobbyController.instance.UpdatePlayerList();
    }

    [Command]
    private void CmdSetPlayerName(string newName)
    {
        // Server sets the SyncVar; Mirror will apply + call hook on clients.
        playerName = newName;
    }
    
    [Command]
    private void CmdSetPlayerReadyToBegin()
    {
        // Flip ready on the SERVER. Mirror will sync to all clients and invoke the hook.
        readyToBegin = !readyToBegin;
        Debug.Log($"[Server] Ready state set for '{playerName}' (conn {connectionID}) => {readyToBegin}");
    }
    
    public void PlayerNameUpdate(string oldValue, string newValue)
    {
        // If you also want the host/server UI to refresh, call UI here unconditionally:
        LobbyController.instance?.UpdatePlayerList();
        LobbyController.instance?.UpdateLobbyName();
    }
    
    public void PlayerReadyUpdate(bool oldValue, bool newValue)
    {
        // This runs on clients when the SyncVar changes (and on host's client too).
        Debug.Log($"[Client] Ready SyncVar changed for '{playerName}' (conn {connectionID}): {oldValue} -> {newValue}");
        LobbyController.instance?.UpdatePlayerList();
    }
    
    public void ChangeReadyToBegin()
    {
        if (isOwned)
        {
            Debug.Log($"[Client] {playerName} clicked Ready. Sending Cmd...");
            CmdSetPlayerReadyToBegin();
        }
        else
        {
            Debug.LogWarning($"[Client] Tried to toggle ready on a non-owned object: {name}");
        }
    }
    
}