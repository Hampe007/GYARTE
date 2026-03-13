using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles local player movement: walking, jogging, sprinting, crouching and jumping.
/// Uses CharacterController and the new Input System.
/// Movement is camera-relative (third-person) and player rotates to face movement direction.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerMovementController : NetworkBehaviour
{
    [Header("Movement Speeds")]
    [Tooltip("Speed when walking slowly.")]
    public float walkSpeed = 3.5f;

    [Tooltip("Speed when jogging (normal movement).")]
    public float jogSpeed = 5.0f;

    [Tooltip("Speed when sprinting.")]
    public float sprintSpeed = 7.5f;

    [Tooltip("Speed when crouching.")]
    public float crouchSpeed = 2.0f;

    [Header("Jump & Gravity")]
    [Tooltip("Upward force applied when jumping.")]
    public float jumpForce = 5.0f;

    [Tooltip("Gravity force applied to the player.")]
    public float gravityForce = -9.82f;

    [Header("Rotation")]
    [Tooltip("How quickly the player rotates to face the movement direction.")]
    public float rotationSpeed = 10f;

    [Header("Camera Reference")]
    [Tooltip("Transform of the camera used to determine movement direction (usually the Main Camera).")]
    public Transform playerCameraTransform;

    [Header("State (Read Only)")]
    [Tooltip("Current world-space velocity of the player.")]
    public Vector3 playerCurrentVelocity;

    [Tooltip("True when the player is on the ground.")]
    public bool isPlayerGrounded;
    
    [Tooltip("True while the Walk modifier button is held down.")]
    public bool isWalkModifierActive;
    
    [Tooltip("True when the player is currently crouching.")]
    public bool isPlayerCrouching;

    [Tooltip("True when the player is currently sprinting.")]
    public bool isPlayerSprinting;

    private PlayerHealth playerHealth;

    private CharacterController playerCharacterController;
    private PlayerAnimationController animationController;
    private PlayerInput playerInput;
    private PlayerStamina playerStamina;

    // Input actions from the new Input System
    private InputAction moveAction;
    private InputAction lookAction;      // Not used directly anymore, Cinemachine reads this.
    private InputAction jumpAction;
    private InputAction walkAction;
    private InputAction sprintAction;
    private InputAction crouchAction;

    private void Awake()
    {
        playerCharacterController = GetComponent<CharacterController>();
        playerInput = GetComponent<PlayerInput>();
        playerStamina = GetComponent<PlayerStamina>();
        playerHealth = GetComponent<PlayerHealth>();
        animationController = GetComponent<PlayerAnimationController>();

        if (playerCharacterController == null)
        {
            Debug.LogError("[PlayerMovementController] CharacterController is missing. Movement will not work.");
        }

        if (animationController == null)
        {
            animationController = GetComponentInChildren<PlayerAnimationController>();
        }

        if (playerInput == null)
        {
            Debug.LogError("[PlayerMovementController] PlayerInput is missing. Input will not work.");
        }

        if (playerHealth == null)
        {
            playerHealth = GetComponentInParent<PlayerHealth>();
        }

        // Try to cache input actions by name
        try
        {
            if (playerInput != null && playerInput.actions != null)
            {
                moveAction   = playerInput.actions["Move"];
                lookAction   = playerInput.actions["Look"];   // Only used by Cinemachine via CinemachineInputProvider
                jumpAction   = playerInput.actions["Jump"];
                walkAction   = playerInput.actions["Walk"];
                sprintAction = playerInput.actions["Sprint"];
                crouchAction = playerInput.actions["Crouch"];
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PlayerMovementController] Failed to find one or more input actions. Please verify action names. Exception: {exception.Message}");
        }

        // Enable actions if they exist
        moveAction?.Enable();
        lookAction?.Enable();
        jumpAction?.Enable();
        walkAction?.Enable();
        sprintAction?.Enable();
        crouchAction?.Enable();
    }

    private void Update()
    {
        if (!CanProcessMovement())
        {
            StopMovementState();
            return;
        }

        // If Mirror networking is active, only the local player should move.
        // If networking is NOT active (editor singleplayer test), allow movement.
        if (NetworkClient.active && !isLocalPlayer)
        {
            return;
        }

        // Stop if dead
        if (playerHealth != null && playerHealth.IsPlayerDead())
        {
            playerCurrentVelocity = Vector3.zero;
            return;
        }

        if (playerCharacterController == null)
        {
            StopMovementState();
            return;
        }

        HandleLook();
        HandleMovement();
    }

    /// <summary>
    /// When using Cinemachine, we do not manually rotate the camera or player from Look here.
    /// CinemachineInputProvider reads the Look action for us and rotates the camera.
    /// </summary>
    private void HandleLook()
    {
        // When using Cinemachine, we rotate the player to match the camera's yaw.
        // The camera itself is already being rotated by CinemachineInputProvider using the Look input.

        if (playerCameraTransform == null)
        {
            // Try to auto-assign main camera as a fallback
            if (Camera.main == null)
            {
                return;
            }

            playerCameraTransform = Camera.main.transform;
        }

        // Get camera forward on the horizontal plane
        Vector3 cameraForward = playerCameraTransform.forward;
        cameraForward.y = 0f;
        cameraForward.Normalize();

        if (cameraForward.sqrMagnitude < 0.0001f)
        {
            return;
        }

        // Rotate the player to look where the camera looks (yaw only)
        Quaternion targetRotation = Quaternion.LookRotation(cameraForward, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
    }


    private void HandleMovement()
    {
        if (!CanProcessMovement())
        {
            StopMovementState();
            return;
        }

        isPlayerGrounded = playerCharacterController.isGrounded;

        // 1) Read movement input (x = strafe, y = forward/back)
        Vector2 moveInput = moveAction != null ? moveAction.ReadValue<Vector2>() : Vector2.zero;
        float moveMagnitude = moveInput.magnitude;

        bool wantsToCrouch = crouchAction != null && crouchAction.IsPressed();
        isPlayerCrouching = wantsToCrouch;

        bool wantsToSprint = sprintAction != null && sprintAction.IsPressed();
        bool wantsToWalk   = walkAction   != null && walkAction.IsPressed();   // NEW

        // Track walk modifier in a readable bool
        isWalkModifierActive = wantsToWalk;

        // 2) Decide if we can sprint
        // Sprint should NOT happen if we are crouching or explicitly walking.
        bool canSprint = wantsToSprint &&
                         moveMagnitude > 0.1f &&
                         !isPlayerCrouching &&
                         !isWalkModifierActive &&
                         (playerStamina == null || playerStamina.CanStartSprinting());

        isPlayerSprinting = canSprint;

        if (playerStamina != null)
        {
            playerStamina.SetSprintingState(isPlayerSprinting);
        }

        // 3) Decide target horizontal speed based on state
        float targetSpeed = 0f;

        if (moveMagnitude < 0.05f)
        {
            // Not really moving -> idle
            targetSpeed = 0f;
        }
        else if (isPlayerCrouching)
        {
            // Crouch movement speed
            targetSpeed = crouchSpeed;
        }
        else if (isPlayerSprinting)
        {
            // Sprint (fastest)
            targetSpeed = sprintSpeed;
        }
        else if (isWalkModifierActive)
        {
            // Walking modifier held: walk speed
            targetSpeed = walkSpeed;
        }
        else
        {
            // Normal movement -> jog
            targetSpeed = jogSpeed;
        }

        // 4) Build movement direction relative to camera (keep as you have now)
        Transform referenceTransform = playerCameraTransform != null ? playerCameraTransform : transform;

        Vector3 cameraForward = referenceTransform.forward;
        cameraForward.y = 0f;
        cameraForward.Normalize();

        Vector3 cameraRight = referenceTransform.right;
        cameraRight.y = 0f;
        cameraRight.Normalize();

        Vector3 moveDirection = (cameraRight * moveInput.x) + (cameraForward * moveInput.y);
        moveDirection.Normalize();

        // NO rotation here, as we decided earlier – rotation is handled in HandleLook()

        // 5) Vertical movement + jump + gravity (keep your existing code)
        Vector3 horizontalVelocity = moveDirection * targetSpeed;

        if (isPlayerGrounded && playerCurrentVelocity.y < 0f)
        {
            playerCurrentVelocity.y = -2f;
        }

        bool pressedJumpThisFrame = jumpAction != null && jumpAction.triggered;

        if (pressedJumpThisFrame && isPlayerGrounded && !isPlayerCrouching)
        {
            playerCurrentVelocity.y = jumpForce;

            // Inform animation system that a jump started
            if (animationController != null)
            {
                animationController.OnJumpStarted();
            }
        }

        playerCurrentVelocity.y += gravityForce * Time.deltaTime;

        Vector3 finalVelocity = new Vector3(
            horizontalVelocity.x,
            playerCurrentVelocity.y,
            horizontalVelocity.z
        );

        playerCurrentVelocity = finalVelocity;

        if (CanProcessMovement())
        {
            playerCharacterController.Move(playerCurrentVelocity * Time.deltaTime);
        }
        else
        {
            StopMovementState();
        }
    }

    private bool CanProcessMovement()
    {
        return isActiveAndEnabled &&
               gameObject.activeInHierarchy &&
               playerCharacterController != null &&
               playerCharacterController.enabled;
    }

    private void OnDisable()
    {
        StopMovementState();
    }

    private void OnDestroy()
    {
        StopMovementState();
    }

    private void StopMovementState()
    {
        playerCurrentVelocity = Vector3.zero;
        isPlayerSprinting = false;

        if (playerStamina != null)
        {
            playerStamina.SetSprintingState(false);
        }
    }
    
    /// <summary>
    /// Returns normalized horizontal speed (0 to 1), where 1 is sprint speed.
    /// This is used for driving the MoveSpeed animation parameter.
    /// </summary>
    public float GetNormalizedHorizontalSpeed()
    {
        Vector3 horizontal = playerCurrentVelocity;
        horizontal.y = 0f;

        float currentSpeed = horizontal.magnitude;

        if (sprintSpeed <= 0f)
        {
            return 0f;
        }

        return Mathf.Clamp01(currentSpeed / sprintSpeed);
    }
}
