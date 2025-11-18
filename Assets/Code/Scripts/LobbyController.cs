using System.Collections.Generic;
using UnityEngine;
using Mirror;
using Steamworks;
using UnityEngine.UI;
using System.Linq;
using TMPro;

public class LobbyController : MonoBehaviour
{

    public static LobbyController instance;
    
    // UI Elements
    public TMP_Text lobbyNameText;
    
    // Player Data
    public GameObject playerListViewContent;
    public GameObject playerListItemPrefab;
    public GameObject localPlayerObject;
    
    // Other Data
    public ulong currentLobbyID;
    public bool playerItemCreated = false;
    private List<PlayerListItem> _playerListItems = new List<PlayerListItem>();
    public PlayerObjectController localPlayerController;
    
    // Ready to begin
    public Button startGameButton;
    public TMP_Text readyButtonText;
    
    // Visibility UI (Lobby scene; host-only)
    [SerializeField] private TMP_Dropdown visibilityDropdown;  // 0=Public, 1=Friends Only, 2=Invite Only
    [SerializeField] private TMP_Text visibilityHintText;      // optional helper text
    
    // Disconnecting / losing connection
    [Header("Disconnect Overlay")]
    [SerializeField] private GameObject disconnectOverlay; // full-screen panel
    [SerializeField] private TMP_Text   disconnectText;    // message label
    [SerializeField] private CanvasGroup blackout;         // optional: black image (fully opaque)

    // Flags used by quit/connection logic
    public static bool LocalQuitInitiated = false;
    public static bool ServerShutdownReceived = false;
    public static string ServerShutdownReason = "";
    
    // Networking
    private CustomNetworkManager _nm;
    private CustomNetworkManager NetworkManager
    {
        get
        {
            if (_nm != null) return _nm;

            var nm = CustomNetworkManager.singleton as CustomNetworkManager;
            if (nm == null)
                nm = Object.FindAnyObjectByType<CustomNetworkManager>(FindObjectsInactive.Include);

            if (nm == null)
                Debug.LogWarning("[LobbyController] CustomNetworkManager not found (did you press Play in the Lobby scene directly?)");

            _nm = nm;
            return _nm;
        }
    }
    
    private void Awake()
    {
        if (instance == null) instance = this;
        _ = NetworkManager;
    }
    
    public void UpdateLobbyName()
    {
        if (NetworkManager == null) return;
        var steamLobby = NetworkManager.GetComponent<SteamLobby>();
        if (steamLobby == null || steamLobby.currentLobbyID == 0) return;

        currentLobbyID = steamLobby.currentLobbyID;
        if (lobbyNameText != null)
            lobbyNameText.text = SteamMatchmaking.GetLobbyData(new CSteamID(currentLobbyID), "name");
        
        SetupVisibilityUI();
    }

    public void UpdatePlayerList()
    {
        if (NetworkManager == null || NetworkManager.GamePlayers == null) return;

        if (!playerItemCreated) CreateHostPlayerItem();
        if (_playerListItems.Count < NetworkManager.GamePlayers.Count) CreateClientPlayerItem();
        if (_playerListItems.Count > NetworkManager.GamePlayers.Count) RemovePlayerItem();
        if (_playerListItems.Count == NetworkManager.GamePlayers.Count) UpdatePlayerItem();
    }

    public void FindLocalPlayer()
    {
        localPlayerObject = GameObject.Find("localGamePlayer");
        if (localPlayerObject != null)
            localPlayerController = localPlayerObject.GetComponent<PlayerObjectController>();
    }
    
    public void CreateHostPlayerItem()
    {
        if (NetworkManager == null) return;
        
        foreach (PlayerObjectController player in NetworkManager.GamePlayers)
        {
            GameObject newPlayerItem = Instantiate(playerListItemPrefab) as GameObject;
            PlayerListItem newPlayerItemScript = newPlayerItem.GetComponent<PlayerListItem>();
            
            newPlayerItemScript.PlayerName = player.playerName;
            newPlayerItemScript.connectionID = player.connectionID;
            newPlayerItemScript.PlayerSteamID = player.playerSteamID;
            newPlayerItemScript.ready = player.readyToBegin;
            newPlayerItemScript.SetPlayerValues();

            newPlayerItem.transform.SetParent(playerListViewContent.transform);
            newPlayerItem.transform.localScale = Vector3.one;
            
            _playerListItems.Add(newPlayerItemScript);
        }
        playerItemCreated = true;
    }

    public void CreateClientPlayerItem()
    {
        if (NetworkManager == null) return;
        
        foreach (PlayerObjectController player in NetworkManager.GamePlayers)
        {
            if (!_playerListItems.Any(b => b.connectionID == player.connectionID))
            {
                GameObject newPlayerItem = Instantiate(playerListItemPrefab) as GameObject;
                PlayerListItem newPlayerItemScript = newPlayerItem.GetComponent<PlayerListItem>();
            
                newPlayerItemScript.PlayerName = player.playerName;
                newPlayerItemScript.connectionID = player.connectionID;
                newPlayerItemScript.PlayerSteamID = player.playerSteamID;
                newPlayerItemScript.ready = player.readyToBegin;
                newPlayerItemScript.SetPlayerValues();

                newPlayerItem.transform.SetParent(playerListViewContent.transform);
                newPlayerItem.transform.localScale = Vector3.one;
            
                _playerListItems.Add(newPlayerItemScript);
            }
        }
    }

    public void UpdatePlayerItem()
    {
        if (NetworkManager == null) return;
        
        foreach (PlayerObjectController player in NetworkManager.GamePlayers)
        {
            foreach (PlayerListItem playerListItemScript in _playerListItems)
            {
                if (playerListItemScript.connectionID == player.connectionID)
                {
                    playerListItemScript.PlayerName = player.playerName;
                    playerListItemScript.ready = player.readyToBegin;
                    playerListItemScript.SetPlayerValues();
                    if (player == localPlayerController)
                    {
                        UpdateReadyButton();
                    }
                }
            }
        }
        CheckIfAllPlayersReadyToBegin();
    }
    
    public void RemovePlayerItem()
    {
        if (NetworkManager == null) return;
        
        List<PlayerListItem> playerListItemsToRemove = new List<PlayerListItem>();
        
        foreach (PlayerListItem playerListItem in _playerListItems)
        {
            if (!NetworkManager.GamePlayers.Any(b => b.connectionID == playerListItem.connectionID))
            {
                playerListItemsToRemove.Add(playerListItem);
            }
        }
        if(playerListItemsToRemove.Count > 0)
        {
            foreach (PlayerListItem _playerListItemToRemove in playerListItemsToRemove)
            {
                GameObject objectToRemove = _playerListItemToRemove.gameObject;
                _playerListItems.Remove(_playerListItemToRemove);
                Destroy(objectToRemove);
                objectToRemove = null;
            }
        }
    }

    public void CheckIfAllPlayersReadyToBegin()
    {
        if (NetworkManager == null || NetworkManager.GamePlayers == null) return;

        bool allReadyToBegin = true; // assume true, disprove below
        foreach (PlayerObjectController player in NetworkManager.GamePlayers)
        {
            if (!player.readyToBegin)
            {
                allReadyToBegin = false;
                break;
            }
        }

        bool shouldEnable = allReadyToBegin && localPlayerController != null && localPlayerController.playerIDNumber == 1;

        if (startGameButton != null)
        {
            bool prev = startGameButton.interactable;
            startGameButton.interactable = shouldEnable;
            
            if (prev != shouldEnable)
            {
                if (shouldEnable)
                    Debug.Log("[UI] All players ready. Host can START GAME (button enabled).");
                else if (allReadyToBegin)
                    Debug.Log("[UI] All ready, but you are not host. START GAME remains disabled.");
                else
                    Debug.Log("[UI] Not all players ready. START GAME disabled.");
            }
        }
    }
    
    public void ReadyPlayer()
    {
        localPlayerController.ChangeReadyToBegin();
    }

    public void UpdateReadyButton()
    {
        if (localPlayerController == null || readyButtonText == null) return;

        if (localPlayerController.readyToBegin)
        {
            // Player is READY -> show the action they can do next: "Unready" (red)
            readyButtonText.text = "Unready";
            readyButtonText.color = Color.red;
            Debug.Log("[UI] You are READY. Button now shows 'Unready' (red).");
        }
        else
        {
            // Player is NOT ready -> show "Ready" (green)
            readyButtonText.text = "Ready";
            readyButtonText.color = Color.green;
            Debug.Log("[UI] You are NOT ready. Button now shows 'Ready' (green).");
        }
    }
    
    public void OnStartGamePressed()
    {
        if (NetworkManager == null) return;                  // safety

        // Lock lobby AFTER you've chosen to start (prevents late joins)
        if (currentLobbyID != 0)
        {
            SteamMatchmaking.SetLobbyJoinable(new CSteamID(currentLobbyID), false);
            Debug.Log("[Game] Lobby joinable = false");
        }

        // Prevent double-clicks while the scene change message is in-flight
        if (startGameButton != null) startGameButton.interactable = false;

        // Kick off the game scene (ensure "Terrain" scene is in Build Settings)
        const string sceneName = "Terrain";
        Debug.Log($"[Game] ServerChangeScene('{sceneName}')");
        NetworkManager.ServerChangeScene(sceneName);         // Mirror syncs all clients to this scene
    }

    
    public void OnQuitLobbyClicked()
    {
        if (NetworkManager == null)
        {
            Debug.LogWarning("[Quit] NetworkManager not found.");
            return;
        }

        // mark that *we* chose to leave
        LocalQuitInitiated = true;

        // Optional: leave Steam lobby (host or client)
        var sl = NetworkManager.GetComponent<SteamLobby>();
        if (sl != null && sl.currentLobbyID != 0)
        {
            Debug.Log($"[Quit] Leaving Steam lobby {sl.currentLobbyID}.");
            SteamMatchmaking.LeaveLobby(new CSteamID(sl.currentLobbyID));
            sl.currentLobbyID = 0;
        }

        if (NetworkServer.active && NetworkClient.isConnected)
        {
            Debug.Log("[Quit] Host pressed X -> notify clients + StopHost()");
            // tell clients we are shutting down cleanly
            NetworkServer.SendToAll(new CustomNetworkManager.ServerShutdownMsg { reason = "host_exit" });

            ShowBlackout(); // host local UX (optional)
            NetworkManager.StopHost();  // Mirror loads Offline Scene for everyone after delay
        }
        else if (NetworkClient.isConnected)
        {
            Debug.Log("[Quit] Client pressed X -> StopClient()");
            ShowBlackout(); // client local UX
            NetworkManager.StopClient(); // Mirror loads Offline Scene for this client after delay
        }
        else
        {
            Debug.Log("[Quit] Not connected; nothing to stop.");
        }
    }
    
    private int MapVisibilityStringToIndex(string vis)
    {
        switch ((vis ?? "").ToLowerInvariant())
        {
            case "friendsonly": return 1;
            case "inviteonly":  return 2;
            default:            return 0; // "Public" or empty
        }
    }

    private void OnVisibilityDropdownChanged(int index)
    {
        // Only host is allowed to change
        if (!NetworkServer.active) return;

        var sl = NetworkManager?.GetComponent<SteamLobby>();
        if (sl == null) return;

        sl.SetLobbyVisibilityRuntime((SteamLobby.LobbyVisibility)index);
    }

    public void ApplyVisibilityFromDropdown()
    {
        if (visibilityDropdown == null) return;
        OnVisibilityDropdownChanged(visibilityDropdown.value);
    }

    /// <summary>
    /// Call this after a lobby is known (ID set) or when entering the Lobby scene.
    /// Safe to call multiple times; it rewires listeners idempotently.
    /// </summary>
    public void SetupVisibilityUI()
    {
        if (visibilityDropdown == null) return;
        if (NetworkManager == null) return;

        var steamLobby = NetworkManager.GetComponent<SteamLobby>();
        if (steamLobby == null || steamLobby.currentLobbyID == 0) return;

        bool isHost = NetworkServer.active; // host runs server+client

        // Read current value from lobby data
        string vis = SteamMatchmaking.GetLobbyData(new CSteamID(steamLobby.currentLobbyID), "visibility");
        int idx = MapVisibilityStringToIndex(vis);
        visibilityDropdown.SetValueWithoutNotify(idx);

        // Host can change; clients see but cannot edit
        visibilityDropdown.interactable = isHost;

        if (visibilityHintText != null)
            visibilityHintText.text = isHost
                ? "You are the host. You can change visibility."
                : "Only the host can change visibility.";
        
        // Immediate apply on change
        visibilityDropdown.onValueChanged.RemoveAllListeners();
        visibilityDropdown.onValueChanged.AddListener(OnVisibilityDropdownChanged);
    }
    
    public void OpenInviteOverlay()
    {
        var sl = NetworkManager?.GetComponent<SteamLobby>();
        if (sl == null) { Debug.LogWarning("[Lobby] SteamLobby not found"); return; }
        sl.OpenSteamInviteOverlay();
    }
    
    public void ShowHostExitedOverlay()
    {
        if (blackout != null) blackout.gameObject.SetActive(false);
        if (disconnectOverlay != null) disconnectOverlay.SetActive(true);
        if (disconnectText != null) disconnectText.text = "Host exited, returning to main menu...";
        Debug.Log("[UI] Showing 'Host exited' overlay");
    }

    public void ShowConnectionLostOverlay()
    {
        if (blackout != null) blackout.gameObject.SetActive(false);
        if (disconnectOverlay != null) disconnectOverlay.SetActive(true);
        if (disconnectText != null) disconnectText.text = "Connection lost, returning to main menu...";
        Debug.Log("[UI] Showing 'Connection lost' overlay");
    }

    public void ShowBlackout()
    {
        if (disconnectOverlay != null) disconnectOverlay.SetActive(false);
        if (blackout != null)
        {
            blackout.gameObject.SetActive(true);
            blackout.alpha = 1f; // ensure fully black
        }
        Debug.Log("[UI] Showing blackout");
    }
    
}