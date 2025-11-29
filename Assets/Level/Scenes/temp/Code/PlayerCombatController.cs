using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles simple melee combat:
/// - Listens to Attack input (LMB or similar).
/// - Plays a sword attack animation via Animator trigger.
/// - Uses animation events to enable/disable the sword hitbox during the swing.
/// - Applies damage to other PlayerHealth when the sword overlaps them.
/// 
/// This works in singleplayer and in Mirror:
/// - Singleplayer: everything runs normally.
/// - Mirror: only the local player processes input, and combat logic
///   (hit detection & damage) runs only on the server.
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerCombatController : NetworkBehaviour
{
    [Header("Attack Settings")]
    [Tooltip("Damage dealt by a single melee attack.")]
    public float attackDamageAmount = 25f;

    [Tooltip("Cooldown time between attacks in seconds.")]
    public float attackCooldownSeconds = 0.6f;

    [Tooltip("Animator trigger name used to start the attack animation.")]
    public string attackTriggerName = "AttackTrigger";

    [Header("References")]
    [Tooltip("Animator that plays character animations (on the Model GameObject).")]
    public Animator playerAnimator;

    [Tooltip("Hitbox script attached to the sword object.")]
    public WeaponHitbox weaponHitbox;

    [Tooltip("Equipment controller that knows if the sword is equipped.")]
    public PlayerEquipmentController equipmentController;

    [Tooltip("PlayerHealth component for this player.")]
    public PlayerHealth playerHealth;

    [Header("State (Read Only)")]
    [Tooltip("True while we are currently in an attack (from input/cooldown perspective).")]
    public bool isAttackInProgress;

    private PlayerInput playerInput;
    private InputAction attackAction;

    private float nextAllowedAttackTime;

    private void Awake()
    {
        playerInput = GetComponent<PlayerInput>();

        if (playerHealth == null)
        {
            playerHealth = GetComponent<PlayerHealth>();
        }

        if (playerInput == null)
        {
            Debug.LogError("[PlayerCombatController] PlayerInput is missing. Attack input will not work.");
        }

        if (playerAnimator == null)
        {
            Debug.LogWarning("[PlayerCombatController] Animator reference is not assigned. Please assign the Model's Animator in the inspector.");
        }

        if (equipmentController == null)
        {
            equipmentController = GetComponentInChildren<PlayerEquipmentController>();
            if (equipmentController == null)
            {
                Debug.LogWarning("[PlayerCombatController] PlayerEquipmentController not found in children. Sword equip state will not be checked.");
            }
        }

        if (weaponHitbox == null)
        {
            Debug.LogWarning("[PlayerCombatController] WeaponHitbox is not assigned. No damage will be applied.");
        }

        // Try to bind Attack input action
        try
        {
            if (playerInput != null && playerInput.actions != null)
            {
                attackAction = playerInput.actions["Primary Attack"];
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PlayerCombatController] Failed to find 'Attack' action. Exception: {exception.Message}");
        }
    }

    private void OnEnable()
    {
        if (attackAction != null)
        {
            attackAction.Enable();
            attackAction.performed += OnAttackPerformed;
        }
    }

    private void OnDisable()
    {
        if (attackAction != null)
        {
            attackAction.performed -= OnAttackPerformed;
            attackAction.Disable();
        }
    }

    /// <summary>
    /// Returns true if this instance should handle input:
    /// - In singleplayer: always true.
    /// - In Mirror: only for the local player.
    /// </summary>
    private bool IsLocalAndAllowedForInput()
    {
        if (NetworkClient.active)
        {
            return isLocalPlayer;
        }

        // Not in a networked game -> treat as singleplayer.
        return true;
    }

    /// <summary>
    /// Returns true if this instance should run combat logic (hit detection, damage).
    /// - Singleplayer: true.
    /// - Mirror: only on the server.
    /// </summary>
    private bool IsAuthoritativeForCombat()
    {
        if (!NetworkServer.active && !NetworkClient.active)
        {
            // No networking active -> singleplayer -> this instance is authoritative.
            return true;
        }

        // In a Mirror game: only the server runs combat logic.
        return isServer;
    }

    private void OnAttackPerformed(InputAction.CallbackContext context)
    {
        if (!IsLocalAndAllowedForInput())
        {
            return;
        }

        if (playerHealth != null && playerHealth.IsPlayerDead())
        {
            // Dead players don't attack.
            return;
        }

        if (equipmentController != null && !equipmentController.isSwordEquipped)
        {
            // Must have sword equipped to attack.
            return;
        }

        if (Time.time < nextAllowedAttackTime)
        {
            // Still in cooldown.
            return;
        }

        if (isAttackInProgress)
        {
            // Already attacking - do not queue another one yet.
            return;
        }

        StartAttack();
    }

    /// <summary>
    /// Starts the local attack animation & cooldown.
    /// Network visuals are not wired yet; this is local + server authoritative logic.
    /// </summary>
    private void StartAttack()
    {
        isAttackInProgress = true;
        nextAllowedAttackTime = Time.time + attackCooldownSeconds;

        if (playerAnimator != null && !string.IsNullOrEmpty(attackTriggerName))
        {
            playerAnimator.SetTrigger(attackTriggerName);
        }
    }

    /// <summary>
    /// Called from animation event via PlayerAnimationEventRelay when the sword
    /// should start dealing damage (hitbox active window).
    /// </summary>
    public void OnAnimationAttackHitboxStart()
    {
        if (!IsAuthoritativeForCombat())
        {
            return;
        }

        if (weaponHitbox == null)
        {
            return;
        }

        weaponHitbox.EnableHitbox(this, attackDamageAmount);
    }

    /// <summary>
    /// Called from animation event when the sword should stop dealing damage.
    /// </summary>
    public void OnAnimationAttackHitboxEnd()
    {
        if (!IsAuthoritativeForCombat())
        {
            return;
        }

        if (weaponHitbox != null)
        {
            weaponHitbox.DisableHitbox();
        }

        isAttackInProgress = false;
    }
}
