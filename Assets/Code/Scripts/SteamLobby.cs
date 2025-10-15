using System;
using System.Collections.Generic;
using System.Runtime;
using UnityEngine;
using Mirror;
using Steamworks;
using TMPro;
using Object = UnityEngine.Object;

public class SteamLobby : MonoBehaviour
{

    public static SteamLobby instance;
    
    // Callbacks
    protected Callback<LobbyCreated_t> lobbyCreated;
    protected Callback<GameLobbyJoinRequested_t> gameLobbyJoinRequested;
    protected Callback<LobbyEnter_t> lobbyEntered;

    // Lobbies
    protected Callback<LobbyMatchList_t> lobbyMatchList;
    protected Callback<LobbyDataUpdate_t> lobbyDataUpdated;
        
    private readonly List<CSteamID> lobbyIDs = new List<CSteamID>();
    private int expectedLobbyCount;
    private int receivedLobbyDataCount;
    private readonly HashSet<ulong> friendOwned = new(); // store lobbyIDs owned by friends

    // default selection (exposed in Inspector if you like)
    [SerializeField] private LobbyVisibility selectedVisibility = LobbyVisibility.Public;
    public enum LobbyVisibility { Public = 0, FriendsOnly = 1, InviteOnly = 2 }
    
    private readonly int capLimit = 50;
    
    // Variables
    public ulong currentLobbyID;
    private const string hostAddressKey = "hostAddress";

    private bool NetActive() =>
        NetworkServer.active || NetworkClient.active || NetworkClient.isConnected;
    
        
    // Networking
    private CustomNetworkManager NetworkManager
    {
        get
        {
            // 1) Mirror’s singleton if alive
            var networkManager = CustomNetworkManager.singleton as CustomNetworkManager;
            if (networkManager != null) return networkManager;

            // 2) Find anywhere (incl. DontDestroyOnLoad, inactive)
            networkManager = Object.FindAnyObjectByType<CustomNetworkManager>(FindObjectsInactive.Include);
            if (networkManager == null)
                Debug.LogError("[SteamLobby] CustomNetworkManager not found. Ensure there is one in your Offline (MainMenu) scene.");
            return networkManager;
        }
    }
    
    private void Start()
    {
        if(!SteamManager.Initialized){ return; }
        if(instance == null) { instance = this; }
        
        lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
        gameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequest);
        lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);

        lobbyMatchList = Callback<LobbyMatchList_t>.Create(OnGetLobbyList);
        lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(OnGetLobbyData);
    }

    public void HostLobby()
    {
        
        if (NetActive())
        {
            Debug.LogWarning("[Lobby] Can't host: networking still active; wait a moment.");
            return;
        }
        
        var type = ToSteamType(selectedVisibility);
        Debug.Log($"[Lobby] Creating lobby type: {selectedVisibility} ({type})");
        SteamMatchmaking.CreateLobby(type, NetworkManager.maxConnections);
    }
    
    private void OnLobbyCreated(LobbyCreated_t callback)
    {
        if (callback.m_eResult != EResult.k_EResultOK) return;

        
        if (NetActive())
        {
            Debug.LogWarning("[Lobby] Lobby created, but networking already active. Skipping StartHost().");
            return;
        }
        
        Debug.Log("Lobby Created Successfully");
        NetworkManager.StartHost();

        currentLobbyID = callback.m_ulSteamIDLobby;
        CSteamID id = new CSteamID(currentLobbyID);

        SteamMatchmaking.SetLobbyData(id, hostAddressKey, SteamUser.GetSteamID().ToString());
        SteamMatchmaking.SetLobbyData(id, "name", SteamFriends.GetPersonaName() + "'s Lobby");

        // store visibility as a readable string for your UI rows
        string vis = selectedVisibility.ToString(); // "Public" | "FriendsOnly" | "InviteOnly"
        SteamMatchmaking.SetLobbyData(id, "visibility", vis);

        // After setting "visibility" data:
        SteamMatchmaking.SetLobbyJoinable(id, true);
    }

    public void SetLobbyVisibilityRuntime(LobbyVisibility newVis)
    {
        if (currentLobbyID == 0) return;
        var id = new CSteamID(currentLobbyID);
        var type = ToSteamType(newVis);
        if (SteamMatchmaking.SetLobbyType(id, type))
        {
            SteamMatchmaking.SetLobbyData(id, "visibility", newVis.ToString());
            SteamMatchmaking.SetLobbyJoinable(id, newVis != LobbyVisibility.InviteOnly);
            selectedVisibility = newVis;
            Debug.Log($"[Lobby] Runtime visibility changed to {newVis}");
        }
        else Debug.LogWarning("[Lobby] Failed to change lobby type at runtime.");
    }
    
    public void OpenSteamInviteOverlay()
    {
        if (currentLobbyID == 0)
        {
            Debug.LogWarning("[Lobby] Can't open invite overlay: no lobby yet.");
            return;
        }

        // Optional: if overlay is disabled, tell the user
        if (!SteamUtils.IsOverlayEnabled())
        {
            Debug.LogWarning("[Lobby] Steam overlay is disabled. Enable it in Steam settings to invite.");
            return;
        }

        var id = new CSteamID(currentLobbyID);
        Debug.Log($"[Lobby] Opening Steam Invite Overlay for lobby {id.m_SteamID}...");
        SteamFriends.ActivateGameOverlayInviteDialog(id);
    }
    
    private void OnJoinRequest(GameLobbyJoinRequested_t callback)
    {
        Debug.Log("Request to join Lobby");
        SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
    }

    private void OnLobbyEntered(LobbyEnter_t callback)
    {
        currentLobbyID = callback.m_ulSteamIDLobby; // This is for clients (and host re-enter)
        if (NetworkServer.active) return; // host skips, clients continue
        if (NetActive())
        {
            Debug.LogWarning("[Lobby] Already a client when lobby entered. Skipping StartClient().");
            return;
        }
        
        NetworkManager.networkAddress = SteamMatchmaking.GetLobbyData(new CSteamID(currentLobbyID), hostAddressKey);
        NetworkManager.StartClient();
    }

    public void JoinLobby(CSteamID lobbyID)
    {
        SteamMatchmaking.JoinLobby(lobbyID);
    }

    public void GetLobbiesList()
    {
        lobbyIDs.Clear();
        friendOwned.Clear();
        expectedLobbyCount = 0;
        receivedLobbyDataCount = 0;

        // Limit results & keep them joinable & reasonably close
        SteamMatchmaking.AddRequestLobbyListResultCountFilter(capLimit); // Cap limit ~50
        SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1); // Joinable
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault); // "nearby" first

        Debug.Log("[Lobbies] Requesting lobby list (<=50, joinable, default distance)...");
        SteamMatchmaking.RequestLobbyList();
    }
    
    void OnGetLobbyList(LobbyMatchList_t result)
    {
        // m_nLobbiesMatching is uint -> cast to int and clamp to your cap
        expectedLobbyCount = Mathf.Min((int)result.m_nLobbiesMatching, capLimit);
        Debug.Log($"[Lobbies] Received {expectedLobbyCount} lobbies.");

        if (LobbiesListManager.instance != null && LobbiesListManager.instance.listOfLobbies.Count > 0)
            LobbiesListManager.instance.DestroyLobbies();

        // Note: results are distance-sorted already when using DistanceFilter
        for (int i = 0; i < expectedLobbyCount; i++)
        {
            CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
            lobbyIDs.Add(id);
            SteamMatchmaking.RequestLobbyData(id); // triggers OnGetLobbyData per lobby
        }
    }
    
    void OnGetLobbyData(LobbyDataUpdate_t result)
    {
        var id = new CSteamID(result.m_ulSteamIDLobby);

        // is lobby owned by a friend?
        var owner = SteamMatchmaking.GetLobbyOwner(id);
        bool isFriendOwner = SteamFriends.HasFriend(owner, EFriendFlags.k_EFriendFlagImmediate);
        if (isFriendOwner) friendOwned.Add(id.m_SteamID);

        receivedLobbyDataCount++;

        // when all data arrived: sort friend-first (stable), then render (max 50)
        if (receivedLobbyDataCount >= expectedLobbyCount && LobbiesListManager.instance != null)
        {
            // stable partition: friends first, preserve distance order within groups
            var sorted = new List<CSteamID>(lobbyIDs.Count);
            foreach (var lid in lobbyIDs)
                if (friendOwned.Contains(lid.m_SteamID)) sorted.Add(lid);
            foreach (var lid in lobbyIDs)
                if (!friendOwned.Contains(lid.m_SteamID)) sorted.Add(lid);

            // show up to 50 (the request is already capped, but clamp just in case)
            int count = Mathf.Min(sorted.Count, capLimit);
            LobbiesListManager.instance.DisplaySortedLobbies(sorted.GetRange(0, count));
        }
    }
    
    public void SetLobbyVisibilityFromUI(int dropdownIndex)
    {
        selectedVisibility = (LobbyVisibility)dropdownIndex;
        Debug.Log($"[Lobby] Visibility selection set to {selectedVisibility}");
    }

    private ELobbyType ToSteamType(LobbyVisibility v)
    {
        switch (v)
        {
            case LobbyVisibility.FriendsOnly: return ELobbyType.k_ELobbyTypeFriendsOnly;
            case LobbyVisibility.InviteOnly: return ELobbyType.k_ELobbyTypePrivate; // invite-only
            default: return ELobbyType.k_ELobbyTypePublic;
        }
    }
    
    private void OnDestroy()
    {
        // Clean up Steam callbacks so destroyed instances can’t fire into new scenes.
        lobbyCreated?.Dispose();
        gameLobbyJoinRequested?.Dispose();
        lobbyEntered?.Dispose();
        lobbyMatchList?.Dispose();
        lobbyDataUpdated?.Dispose();
    }
    
}