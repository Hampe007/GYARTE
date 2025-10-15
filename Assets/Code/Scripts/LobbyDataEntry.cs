using UnityEngine;
using UnityEngine.UI;
using Steamworks;
using TMPro;

public class LobbyDataEntry : MonoBehaviour
{

    // Data
    public CSteamID lobbyID;
    public string lobbyName;
    public TMP_Text lobbyNameText;
    public TMP_Text visibilityText;
    private string visibility;
    
    public void SetLobbyData()
    {
        visibility = SteamMatchmaking.GetLobbyData(lobbyID, "visibility"); // may be empty for older hosts
        lobbyNameText.text = string.IsNullOrEmpty(lobbyName) ? "Empty" : lobbyName;

        if (visibilityText != null)
            visibilityText.text = string.IsNullOrEmpty(visibility) ? "" : visibility; // e.g., "Public"
        
        if (string.IsNullOrEmpty(lobbyName))
        {
            lobbyNameText.text = "Empty";
            Debug.Log($"[Lobbies] UI item for {lobbyID.m_SteamID}: name empty -> showing 'Empty'");
        }
        else
        {
            lobbyNameText.text = lobbyName;
            Debug.Log($"[Lobbies] UI item for {lobbyID.m_SteamID}: name '{lobbyName}'");
        }
    }

    public void JoinLobby()
    {
        Debug.Log($"[Lobbies] Join clicked: {lobbyID.m_SteamID}");
        SteamLobby.instance.JoinLobby(lobbyID);
    }
    
}