using Mirror;
using UnityEngine;

/// <summary>
/// Lives on the Player root. When this is the local player (or in offline mode),
/// finds the PlayerHUDController in the scene and initializes it with this
/// player's health and stamina.
/// </summary>
public class PlayerUIConnector : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("PlayerHealth component for this player.")]
    public PlayerHealth playerHealth;

    [Tooltip("Optional stamina component for this player.")]
    public PlayerStamina playerStamina;

    private void Awake()
    {
        if (playerHealth == null)
        {
            playerHealth = GetComponent<PlayerHealth>() ?? GetComponentInChildren<PlayerHealth>();
        }

        if (playerStamina == null)
        {
            playerStamina = GetComponent<PlayerStamina>() ?? GetComponentInChildren<PlayerStamina>();
        }
    }

    private void Start()
    {
        // Offline / singleplayer OR local player in a networked game
        if (!NetworkClient.active || isLocalPlayer)
        {
            HookUpHud();
        }
    }

    private void HookUpHud()
    {
        PlayerHUDController hud = FindObjectOfType<PlayerHUDController>();
        if (hud == null)
        {
            Debug.LogWarning("[PlayerUIConnector] No PlayerHUDController found in the scene.");
            return;
        }

        if (playerHealth == null)
        {
            Debug.LogWarning("[PlayerUIConnector] PlayerHealth is missing, HUD will not be fully initialized.");
            return;
        }

        hud.Initialize(playerHealth, playerStamina);
    }
}