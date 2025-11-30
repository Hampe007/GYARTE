using System;
using Mirror;
using UnityEngine;

/// <summary>
/// Updates Animator parameters based on movement, health and stamina.
/// Also decides when the player should look tired.
/// </summary>
public class PlayerAnimationController : NetworkBehaviour
{
    [Header("References")]
    [Tooltip("Movement controller that provides speed and grounded state.")]
    public PlayerMovementController movementController;

    [Tooltip("Health controller that provides dead/alive state.")]
    public PlayerHealth healthController;

    [Tooltip("Stamina controller that provides stamina state.")]
    public PlayerStamina staminaController;

    [Tooltip("Animator that plays the character animations.")]
    public Animator playerAnimator;

    private NetworkAnimator networkAnimator;
    
    private NetworkIdentity networkIdentity;

    [Header("Animator Parameter Names")]
    [Tooltip("Float parameter that represents normalized movement speed (0 to 1).")]
    public string moveSpeedParameterName = "MoveSpeed";

    [Tooltip("Bool parameter that represents if the player is grounded.")]
    public string isGroundedParameterName = "IsGrounded";

    [Tooltip("Trigger parameter used when the player starts a jump.")]
    public string jumpTriggerParameterName = "JumpTrigger";

    [Tooltip("Bool parameter that represents if the player is crouching.")]
    public string isCrouchingParameterName = "IsCrouching";

    [Tooltip("Bool parameter that represents if the player is sprinting.")]
    public string isSprintingParameterName = "IsSprinting";

    [Tooltip("Bool parameter that becomes true when the player has died.")]
    public string isDeadParameterName = "IsDead";

    [Tooltip("Bool parameter that becomes true when the player is tired (low stamina while idle).")]
    public string isTiredParameterName = "IsTired";

    private void Awake()
    {
        // Auto-find references if not assigned manually
        if (movementController == null)
        {
            movementController = GetComponent<PlayerMovementController>();
        }

        if (healthController == null)
        {
            healthController = GetComponent<PlayerHealth>();
        }

        if (staminaController == null)
        {
            staminaController = GetComponent<PlayerStamina>();
        }

        if (playerAnimator == null)
        {
            playerAnimator = GetComponentInChildren<Animator>();
        }

        if (networkAnimator == null)
        {
            networkAnimator = GetComponent<NetworkAnimator>() ?? GetComponentInParent<NetworkAnimator>() ?? GetComponentInChildren<NetworkAnimator>();
        }

        if (networkIdentity == null)
        {
            networkIdentity = GetComponent<NetworkIdentity>() ?? GetComponentInParent<NetworkIdentity>();
        }

        if (playerAnimator == null)
        {
            Debug.LogError("[PlayerAnimationController] Animator is missing. Please assign an Animator (usually on the Model child).");
        }

        if (movementController == null)
        {
            Debug.LogWarning("[PlayerAnimationController] MovementController is not assigned. Movement-based animation will not update.");
        }

        if (healthController == null)
        {
            Debug.LogWarning("[PlayerAnimationController] HealthController is not assigned. Dead state will not update.");
        }

        if (staminaController == null)
        {
            Debug.LogWarning("[PlayerAnimationController] StaminaController is not assigned. Tired state will not update.");
        }
    }

    private void Update()
    {
        // If Mirror networking is active, only the local player should drive animation parameters.
        // If networking is NOT active, allow this object to drive its own animation for testing.
        if (Mirror.NetworkClient.active && !isLocalPlayer)
        {
            return;
        }

        if (playerAnimator == null)
        {
            return;
        }

        UpdateMovementParameters();
        UpdateVitalParameters();
    }


    private void UpdateMovementParameters()
    {
        if (movementController == null)
        {
            return;
        }

        try
        {
            float normalizedSpeed = movementController.GetNormalizedHorizontalSpeed();

            if (!string.IsNullOrEmpty(moveSpeedParameterName))
            {
                playerAnimator.SetFloat(moveSpeedParameterName, normalizedSpeed);
            }

            if (!string.IsNullOrEmpty(isGroundedParameterName))
            {
                playerAnimator.SetBool(isGroundedParameterName, movementController.isPlayerGrounded);
            }

            if (!string.IsNullOrEmpty(isCrouchingParameterName))
            {
                playerAnimator.SetBool(isCrouchingParameterName, movementController.isPlayerCrouching);
            }

            if (!string.IsNullOrEmpty(isSprintingParameterName))
            {
                playerAnimator.SetBool(isSprintingParameterName, movementController.isPlayerSprinting);
            }
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"[PlayerAnimationController] Failed to update movement animation parameters. Exception: {exception.Message}");
        }
    }

    private void UpdateVitalParameters()
    {
        try
        {
            // Dead state
            if (healthController != null && !string.IsNullOrEmpty(isDeadParameterName))
            {
                bool isDead = healthController.IsPlayerDead();
                playerAnimator.SetBool(isDeadParameterName, isDead);
            }

            // Tired state: low stamina AND basically not moving AND grounded AND not dead
            if (staminaController != null &&
                movementController != null &&
                !string.IsNullOrEmpty(isTiredParameterName))
            {
                bool isDead = healthController != null && healthController.IsPlayerDead();
                bool isLowStamina = staminaController.IsStaminaLow();
                bool isAlmostIdle = movementController.GetNormalizedHorizontalSpeed() < 0.05f;
                bool isGrounded = movementController.isPlayerGrounded;

                bool shouldLookTired = !isDead && isLowStamina && isAlmostIdle && isGrounded;

                playerAnimator.SetBool(isTiredParameterName, shouldLookTired);
            }
        }
        catch (System.Exception exception)
        {
            Debug.LogError($"[PlayerAnimationController] Failed to update vital animation parameters. Exception: {exception.Message}");
        }
    }

    /// <summary>
    /// Called by movement when the player successfully starts a jump.
    /// Sets the JumpTrigger on the Animator.
    /// </summary>
    public void OnJumpStarted()
    {
        if (playerAnimator == null)
        {
            return;
        }

        if (string.IsNullOrEmpty(jumpTriggerParameterName))
        {
            return;
        }

        try
        {
            // If we are in a networked game and this is the local player,
            // use NetworkAnimator so all clients see the jump.
            if (networkAnimator != null && NetworkClient.active && IsLocalOrOffline())
            {
                networkAnimator.SetTrigger(jumpTriggerParameterName);
            }
            else
            {
                // Offline / fallback
                playerAnimator.SetTrigger(jumpTriggerParameterName);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PlayerAnimationController] Failed to set jump trigger '{jumpTriggerParameterName}'. Exception: {exception.Message}");
        }
    }

    private bool IsLocalOrOffline()
    {
        if (!NetworkClient.active)
        {
            // No networking -> treat as singleplayer
            return true;
        }

        if (networkIdentity == null)
        {
            return false;
        }

        return networkIdentity.isLocalPlayer;
    }

}