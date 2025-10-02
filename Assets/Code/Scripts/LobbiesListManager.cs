using System.Collections.Generic;
using Edgegap;
using UnityEngine;
using Steamworks;

public class LobbiesListManager : MonoBehaviour
{

    public static LobbiesListManager instance;
    
    // Lobbies List Variables
    public GameObject lobbiesMenu;
    public GameObject lobbyDataItemPrefab;
    public GameObject lobbyListContent;

    public GameObject mainMenu;

    public List<GameObject> listOfLobbies = new List<GameObject>();

    private void Awake()
    {
        if(instance == null) { instance = this; }
    }

    public void GetListOfLobbies()
    {
        lobbiesMenu.SetActive(true);
        mainMenu.SetActive(false);
        
        SteamLobby.instance.GetLobbiesList();
    }
    
    public void DisplaySortedLobbies(List<CSteamID> sortedLobbyIDs)
    {
        if (lobbyListContent == null) {
            Debug.LogWarning("[Lobbies] lobbyListContent is NULL.");
            return;
        }

        DestroyLobbies();

        foreach (var id in sortedLobbyIDs)
        {
            GameObject row = Instantiate(lobbyDataItemPrefab);
            var entry = row.GetComponent<LobbyDataEntry>();

            entry.lobbyID = id;
            entry.lobbyName = SteamMatchmaking.GetLobbyData(id, "name");

            entry.SetLobbyData();
            row.transform.SetParent(lobbyListContent.transform, false);
            listOfLobbies.Add(row);
        }

        Debug.Log($"[Lobbies] Rendered {sortedLobbyIDs.Count} lobbies (friends first).");
    }

    
    public void DestroyLobbies()
    {
        Debug.Log($"[Lobbies] Destroying {listOfLobbies.Count} lobby UI items.");
        foreach (GameObject lobbyItem in listOfLobbies)
            Destroy(lobbyItem);
        listOfLobbies.Clear();
    }
    
}