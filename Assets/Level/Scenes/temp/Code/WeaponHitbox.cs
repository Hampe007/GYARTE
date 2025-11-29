using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Attached to the sword. Handles enabling/disabling its colliders as a hitbox
/// and applying damage to other PlayerHealth when they overlap during an attack.
/// 
/// - Colliders are normally disabled.
/// - During the attack window, PlayerCombatController.EnableHitbox(...) is called,
///   which enables all child colliders.
/// - OnTriggerEnter is used to detect hits and call TakeDamage() on PlayerHealth.
/// </summary>
public class WeaponHitbox : NetworkBehaviour
{
    [Header("Debug")]
    [Tooltip("If true, debug messages will be logged when the hitbox hits something.")]
    public bool logDebugMessages = false;

    private Collider[] weaponColliders;
    private bool hitboxIsActive;

    private PlayerCombatController currentAttacker;
    private float currentAttackDamage;

    // To avoid hitting the same target multiple times in one swing.
    private readonly HashSet<GameObject> alreadyDamagedObjects = new HashSet<GameObject>();

    private void Awake()
    {
        weaponColliders = GetComponentsInChildren<Collider>();

        if (weaponColliders == null || weaponColliders.Length == 0)
        {
            Debug.LogWarning("[WeaponHitbox] No colliders found in children. Hit detection will not work.");
        }

        // Ensure hitbox is disabled at start.
        SetHitboxCollidersEnabled(false);
    }

    /// <summary>
    /// Enables the hitbox colliders for a specific attacker and damage value.
    /// </summary>
    public void EnableHitbox(PlayerCombatController attacker, float attackDamage)
    {
        if (!IsAuthoritativeForCombat())
        {
            return;
        }

        currentAttacker = attacker;
        currentAttackDamage = attackDamage;
        hitboxIsActive = true;
        alreadyDamagedObjects.Clear();

        SetHitboxCollidersEnabled(true);

        if (logDebugMessages)
        {
            Debug.Log("[WeaponHitbox] Hitbox enabled.");
        }
    }

    /// <summary>
    /// Disables the hitbox colliders and clears state.
    /// </summary>
    public void DisableHitbox()
    {
        if (!IsAuthoritativeForCombat())
        {
            return;
        }

        hitboxIsActive = false;
        currentAttacker = null;
        currentAttackDamage = 0f;
        alreadyDamagedObjects.Clear();

        SetHitboxCollidersEnabled(false);

        if (logDebugMessages)
        {
            Debug.Log("[WeaponHitbox] Hitbox disabled.");
        }
    }

    private void SetHitboxCollidersEnabled(bool enabled)
    {
        if (weaponColliders == null)
        {
            return;
        }

        foreach (Collider colliderComponent in weaponColliders)
        {
            if (colliderComponent != null)
            {
                colliderComponent.enabled = enabled;
            }
        }
    }

    /// <summary>
    /// Singleplayer OR server-side authority check.
    /// - Singleplayer (no Mirror active): returns true.
    /// - Mirror: only the server processes hit detection & damage.
    /// </summary>
    private bool IsAuthoritativeForCombat()
    {
        if (!NetworkServer.active && !NetworkClient.active)
        {
            return true;
        }

        return isServer;
    }

    private void OnTriggerEnter(Collider otherCollider)
    {
        if (!hitboxIsActive)
        {
            return;
        }

        if (!IsAuthoritativeForCombat())
        {
            return;
        }

        if (currentAttacker == null || currentAttacker.playerHealth == null)
        {
            return;
        }

        // Find PlayerHealth on the object we hit (or its parents).
        PlayerHealth targetHealth = otherCollider.GetComponentInParent<PlayerHealth>();
        if (targetHealth == null)
        {
            // Not a player or no health -> ignore.
            return;
        }

        // Do not hit ourselves.
        if (targetHealth.gameObject == currentAttacker.playerHealth.gameObject)
        {
            return;
        }

        // Avoid multiple hits on the same target for one swing.
        if (alreadyDamagedObjects.Contains(targetHealth.gameObject))
        {
            return;
        }

        alreadyDamagedObjects.Add(targetHealth.gameObject);

        if (logDebugMessages)
        {
            Debug.Log($"[WeaponHitbox] {currentAttacker.gameObject.name} hit {targetHealth.gameObject.name} for {currentAttackDamage} damage.");
        }

        targetHealth.TakeDamage(currentAttackDamage);
    }
}