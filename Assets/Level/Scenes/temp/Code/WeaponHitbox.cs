using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// Attached to the sword. Handles enabling/disabling its colliders as a hitbox
/// and applying damage to other PlayerHealth when they overlap during an attack.
/// </summary>
public class WeaponHitbox : MonoBehaviour
{
    [Header("Debug")]
    [Tooltip("If true, debug messages will be logged for hitbox events and collisions.")]
    public bool logDebugMessages = false;

    private Collider[] weaponColliders;
    private bool hitboxIsActive;

    [HideInInspector] public PlayerCombatController currentAttacker;
    private float currentAttackDamage;

    private readonly HashSet<GameObject> alreadyDamagedObjects = new HashSet<GameObject>();

    private void Awake()
    {
        weaponColliders = GetComponentsInChildren<Collider>();

        if (weaponColliders == null || weaponColliders.Length == 0)
        {
            Debug.LogWarning("[WeaponHitbox] No colliders found in children. Hit detection will not work.");
        }

        // Ensure hitbox is disabled at start (colliders off)
        SetHitboxCollidersEnabled(false);
    }

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
            Debug.Log("[WeaponHitbox] Hitbox ENABLED.");
        }
    }

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
            Debug.Log("[WeaponHitbox] Hitbox DISABLED.");
        }
    }

    private void SetHitboxCollidersEnabled(bool enabled)
    {
        if (weaponColliders == null)
        {
            return;
        }

        foreach (Collider c in weaponColliders)
        {
            if (c != null)
            {
                c.enabled = enabled;
            }
        }
    }

    private bool IsAuthoritativeForCombat()
    {
        // Singleplayer: no networking active -> do the logic
        if (!NetworkServer.active && !NetworkClient.active)
        {
            return true;
        }

        // Network game: only the server applies damage
        return NetworkServer.active;
    }

    private void OnTriggerEnter(Collider other)
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

        if (logDebugMessages)
        {
            Debug.Log($"[WeaponHitbox] OnTriggerEnter with {other.gameObject.name}");
        }

        PlayerHealth targetHealth = other.GetComponentInParent<PlayerHealth>();
        if (targetHealth == null)
        {
            if (logDebugMessages)
            {
                Debug.Log("[WeaponHitbox] Collider has no PlayerHealth in parents, ignoring.");
            }
            return;
        }

        // Don't hit ourselves
        if (targetHealth.gameObject == currentAttacker.playerHealth.gameObject)
        {
            if (logDebugMessages)
            {
                Debug.Log("[WeaponHitbox] Ignoring self hit.");
            }
            return;
        }

        if (alreadyDamagedObjects.Contains(targetHealth.gameObject))
        {
            if (logDebugMessages)
            {
                Debug.Log("[WeaponHitbox] Target already damaged in this swing, ignoring.");
            }
            return;
        }

        alreadyDamagedObjects.Add(targetHealth.gameObject);

        if (logDebugMessages)
        {
            Debug.Log($"[WeaponHitbox] Hitting {targetHealth.gameObject.name} for {currentAttackDamage} damage.");
        }

        targetHealth.TakeDamage(currentAttackDamage);
    }
}