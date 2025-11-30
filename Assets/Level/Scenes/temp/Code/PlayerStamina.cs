using Mirror;
using UnityEngine;

/// <summary>
/// Handles player stamina: draining when sprinting and regenerating when resting.
/// Later we will also use this for attacking and swimming.
/// </summary>
public class PlayerStamina : NetworkBehaviour
{
    [Header("Stamina Settings")]
    [Tooltip("Maximum stamina value the player can have.")]
    public float maxStamina = 100f;

    [Tooltip("Current stamina value. Starts at maxStamina.")]
    public float currentStamina = 100f;

    [Tooltip("How much stamina is drained per second while sprinting.")]
    public float sprintStaminaDrainPerSecond = 15f;

    [Tooltip("How much stamina is regenerated per second when not sprinting.")]
    public float staminaRegenerationPerSecond = 10f;

    [Tooltip("Below this value, stamina is considered low and can trigger tired animations.")]
    public float lowStaminaThreshold = 20f;

    [Tooltip("Minimum stamina required to start or keep sprinting.")]
    public float minimumStaminaToStartSprinting = 10f;

    [Header("State (Read Only)")]
    [Tooltip("True when stamina is considered low.")]
    public bool isStaminaLow;

    [Tooltip("True when the player is currently draining stamina by sprinting.")]
    public bool isConsumingStaminaBySprinting;

    private void Awake()
    {
        // Make sure stamina is initialized to full.
        currentStamina = maxStamina;
    }

    private void Update()
    {
        // When networking is active, only the local player updates stamina.
        // When networking is not active, allow stamina to update for testing.
        if (Mirror.NetworkClient.active && !isLocalPlayer)
        {
            return;
        }

        UpdateStamina(Time.deltaTime);
    }


    private void UpdateStamina(float deltaTime)
    {
        if (deltaTime <= 0f)
        {
            return;
        }

        // Drain while sprinting
        if (isConsumingStaminaBySprinting)
        {
            float drainAmount = sprintStaminaDrainPerSecond * deltaTime;
            currentStamina = Mathf.Max(0f, currentStamina - drainAmount);
        }
        else
        {
            // Regenerate when not using stamina and below max
            if (currentStamina < maxStamina)
            {
                float regenAmount = staminaRegenerationPerSecond * deltaTime;
                currentStamina = Mathf.Min(maxStamina, currentStamina + regenAmount);
            }
        }

        // Update low stamina flag
        isStaminaLow = currentStamina <= lowStaminaThreshold;
    }

    /// <summary>
    /// Called by movement to tell stamina if the player is currently sprinting.
    /// </summary>
    public void SetSprintingState(bool isSprinting)
    {
        // Do not allow sprinting if we are below the minimum threshold.
        if (isSprinting && !CanStartSprinting())
        {
            isConsumingStaminaBySprinting = false;
            return;
        }

        isConsumingStaminaBySprinting = isSprinting;
    }

    /// <summary>
    /// Returns true if there is enough stamina to start or keep sprinting.
    /// </summary>
    public bool CanStartSprinting()
    {
        return currentStamina >= minimumStaminaToStartSprinting;
    }

    /// <summary>
    /// Returns stamina as a 0 to 1 fraction.
    /// Useful for UI later.
    /// </summary>
    public float GetStaminaNormalized()
    {
        if (maxStamina <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(currentStamina / maxStamina);
    }

    /// <summary>
    /// Returns true if stamina is currently low.
    /// </summary>
    public bool IsStaminaLow()
    {
        return isStaminaLow;
    }
}