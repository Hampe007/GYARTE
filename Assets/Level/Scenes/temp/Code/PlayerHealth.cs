using Mirror;
using UnityEngine;

/// <summary>
/// Handles player health and dead/alive state.
/// Damage will be applied here later from the combat system.
/// </summary>
public class PlayerHealth : NetworkBehaviour
{
    [Header("Health Settings")]
    [Tooltip("Maximum health value the player can have.")]
    public float playerMaxHealth = 100f;

    [Tooltip("Current health value. Starts at max health.")]
    public float playerCurrentHealth = 100f;

    [Tooltip("True when the player has died.")]
    public bool isPlayerDead;

    private void Awake()
    {
        // Initialize health to full.
        playerCurrentHealth = playerMaxHealth;
        isPlayerDead = false;
    }

    /// <summary>
    /// Returns true if the player is currently dead.
    /// </summary>
    public bool IsPlayerDead()
    {
        return isPlayerDead;
    }

    /// <summary>
    /// Returns health as a 0 to 1 fraction. Useful for UI later.
    /// </summary>
    public float GetHealthNormalized()
    {
        if (playerMaxHealth <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(playerCurrentHealth / playerMaxHealth);
    }

    /// <summary>
    /// This will mark the player as dead and set health to zero.
    /// We will call this from damage logic later.
    /// </summary>
    [Server]
    public void MarkPlayerAsDead()
    {
        if (isPlayerDead)
        {
            return;
        }

        playerCurrentHealth = 0f;
        isPlayerDead = true;
    }
}