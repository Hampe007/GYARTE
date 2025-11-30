using Mirror;
using UnityEngine;

/// <summary>
/// Tracks player health and death state.
/// - Works in singleplayer.
/// - In Mirror: only the server should modify health.
///   Health/death are SyncVars so they replicate to clients.
/// </summary>
public class PlayerHealth : NetworkBehaviour
{
    [Header("Health Settings")]
    [Tooltip("Maximum health value for the player.")]
    public float playerMaxHealth = 100f;

    [SyncVar]
    [Tooltip("Current health of the player.")]
    public float playerCurrentHealth = 100f;

    [SyncVar]
    [Tooltip("True when the player is dead.")]
    public bool isPlayerDead = false;

    private void Awake()
    {
        // Clamp starting health in case it's not set correctly.
        playerCurrentHealth = Mathf.Clamp(playerCurrentHealth, 0f, playerMaxHealth);
    }

    /// <summary>
    /// Applies damage to this player.
    /// - In singleplayer: always runs.
    /// - In Mirror: only runs on the server.
    /// </summary>
    public void TakeDamage(float damageAmount)
    {
        if ((!NetworkServer.active && !NetworkClient.active) == false)
        {
            // Networking is active.
            // Only allow the server to apply damage.
            if (!isServer)
            {
                return;
            }
        }

        if (isPlayerDead)
        {
            return;
        }

        if (damageAmount <= 0f)
        {
            return;
        }

        playerCurrentHealth -= damageAmount;
        playerCurrentHealth = Mathf.Clamp(playerCurrentHealth, 0f, playerMaxHealth);

        if (playerCurrentHealth <= 0f)
        {
            MarkPlayerAsDead();
        }
    }

    /// <summary>
    /// Marks the player as dead. Additional death logic can be added here.
    /// </summary>
    public void MarkPlayerAsDead()
    {
        if (isPlayerDead)
        {
            return;
        }

        isPlayerDead = true;

        Debug.Log($"[PlayerHealth] Player '{gameObject.name}' died.");
        // TODO: disable movement, trigger death animation, start respawn timer, etc.
    }

    /// <summary>
    /// Returns current health / max health in the 0..1 range.
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
    /// Helper for other scripts to check if this player is dead.
    /// </summary>
    public bool IsPlayerDead()
    {
        return isPlayerDead;
    }
}