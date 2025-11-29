using UnityEngine;

/// <summary>
/// Lives on the Model (same GameObject as the Animator).
/// Animation events call methods here, which in turn forward them to
/// the PlayerCombatController on the Player root.
/// </summary>
public class PlayerAnimationEventRelay : MonoBehaviour
{
    private PlayerCombatController playerCombatController;

    private void Awake()
    {
        playerCombatController = GetComponentInParent<PlayerCombatController>();

        if (playerCombatController == null)
        {
            Debug.LogWarning("[PlayerAnimationEventRelay] PlayerCombatController not found in parents. Attack hitbox events will do nothing.");
        }
    }

    /// <summary>
    /// Called from the Sword_Attack animation event when the hitbox should turn on.
    /// </summary>
    public void OnAttackHitboxStart()
    {
        if (playerCombatController != null)
        {
            playerCombatController.OnAnimationAttackHitboxStart();
        }
    }

    /// <summary>
    /// Called from the Sword_Attack animation event when the hitbox should turn off.
    /// </summary>
    public void OnAttackHitboxEnd()
    {
        if (playerCombatController != null)
        {
            playerCombatController.OnAnimationAttackHitboxEnd();
        }
    }
}