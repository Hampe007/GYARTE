using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Controls the local player's HUD:
/// - Updates HP and Stamina sliders.
/// - Shows a death screen when the local player dies.
/// - Handles "Back to Main Menu" button.
/// </summary>
public class PlayerHUDController : MonoBehaviour
{
    [Header("Sliders")]
    [Tooltip("Slider used for HP (value 0..1).")]
    public Slider healthSlider;

    [Tooltip("Slider used for Stamina (value 0..1).")]
    public Slider staminaSlider;

    [Header("Death Screen UI")]
    [Tooltip("Panel shown when the local player dies.")]
    public GameObject deathScreenPanel;

    private PlayerHealth playerHealth;
    private PlayerStamina playerStamina; // Adapt to your stamina script
    private bool isInitialized;

    private void Start()
    {
        // Ensure sliders are configured for 0..1 range and non-interactable
        if (healthSlider != null)
        {
            healthSlider.minValue = 0f;
            healthSlider.maxValue = 1f;
            healthSlider.wholeNumbers = false;
            healthSlider.interactable = false;
        }

        if (staminaSlider != null)
        {
            staminaSlider.minValue = 0f;
            staminaSlider.maxValue = 1f;
            staminaSlider.wholeNumbers = false;
            staminaSlider.interactable = false;
        }

        if (deathScreenPanel != null)
        {
            deathScreenPanel.SetActive(false);
        }

    }

    /// <summary>
    /// Called by the local player's UI connector to hook this HUD to the correct PlayerHealth / PlayerStamina.
    /// </summary>
    public void Initialize(PlayerHealth health, PlayerStamina stamina)
    {
        playerHealth = health;
        playerStamina = stamina;

        isInitialized = (playerHealth != null);
    }

    private void Update()
    {
        if (!isInitialized || playerHealth == null)
        {
            return;
        }

        // Escape key -> behave like pressing the "Back to Main Menu" button.
        // This gives the player a quick way to quit to main menu from the game scene.
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            OnBackToMainMenuButtonPressed();
            return;
        }

        // Health slider – expects 0..1
        if (healthSlider != null)
        {
            healthSlider.value = playerHealth.GetHealthNormalized();
        }

        // Stamina slider – expects 0..1
        if (staminaSlider != null && playerStamina != null)
        {
            staminaSlider.value = playerStamina.GetStaminaNormalized();
        }

        // Death screen
        if (playerHealth.IsPlayerDead())
        {
            if (deathScreenPanel != null && !deathScreenPanel.activeSelf)
            {
                deathScreenPanel.SetActive(true);
                healthSlider.gameObject.SetActive(false);
                staminaSlider.gameObject.SetActive(false);

            }
        }
    }

    /// <summary>
    /// Called by the death screen button.
    /// Stops networking (if running) and loads the main menu scene.
    /// </summary>
    public void OnBackToMainMenuButtonPressed()
    {
        try
        {
            NetworkManager netManager = NetworkManager.singleton;
            if (netManager != null)
            {
                if (NetworkServer.active && NetworkClient.isConnected)
                {
                    // Host
                    netManager.StopHost();
                }
                else if (NetworkClient.isConnected)
                {
                    // Client only
                    netManager.StopClient();
                }
                else if (NetworkServer.active)
                {
                    // Dedicated server
                    netManager.StopServer();
                }
            }

            // Replace with your main menu scene name
            SceneManager.LoadScene("MainMenu");
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"[PlayerHUDController] Failed to go back to main menu. Exception: {exception.Message}");
        }
    }
}
