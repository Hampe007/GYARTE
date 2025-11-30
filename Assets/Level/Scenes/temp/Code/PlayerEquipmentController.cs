using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles equipping and unequipping the sword.
/// - Only the local player responds to input.
/// - Uses animation triggers + events to move the sword between back and hand.
/// - Sets IsSwordEquipped bool on the Animator.
/// - Uses NetworkAnimator.SetTrigger so other clients see draw/sheath.
/// </summary>
public class PlayerEquipmentController : MonoBehaviour
{
    [Header("Sword Setup")]
    [Tooltip("The sword GameObject that should move between back and hand sockets.")]
    public GameObject swordObject;

    [Tooltip("Where the sword rests when sheathed on the back.")]
    public Transform swordOnBackSocket;

    [Tooltip("Where the sword rests when held in the hand.")]
    public Transform swordInHandSocket;

    [Header("Animator Setup")]
    [Tooltip("Animator that plays the character animations. Usually on the same GameObject as this script.")]
    public Animator animator;

    [Tooltip("NetworkAnimator used to sync draw/sheath triggers over the network.")]
    public NetworkAnimator networkAnimator;

    [Tooltip("Trigger parameter used to start the sword draw animation (Sword_Enter).")]
    public string swordDrawTriggerName = "SwordDrawTrigger";

    [Tooltip("Trigger parameter used to start the sword sheath animation (Sword_Exit).")]
    public string swordSheathTriggerName = "SwordSheathTrigger";

    [Tooltip("Bool parameter that is true when the sword is equipped in the hand.")]
    public string isSwordEquippedBoolName = "IsSwordEquipped";

    [Header("Input Setup")]
    [Tooltip("Name of the input action used to toggle equip/unequip of the sword.")]
    public string equipSwordActionName = "EquipSword";

    [Header("State (Read Only)")]
    [Tooltip("True when the sword is currently equipped in the hand.")]
    public bool isSwordEquipped;

    private PlayerInput playerInput;
    private InputAction equipSwordAction;
    private NetworkIdentity networkIdentity;

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        if (networkAnimator == null)
        {
            networkAnimator = GetComponent<NetworkAnimator>() ?? GetComponentInParent<NetworkAnimator>() ?? GetComponentInChildren<NetworkAnimator>();
        }

        if (animator == null)
        {
            Debug.LogError("[PlayerEquipmentController] Animator is missing. Please add this script to the same GameObject that has the Animator.");
        }

        playerInput = GetComponentInParent<PlayerInput>();
        if (playerInput == null)
        {
            Debug.LogError("[PlayerEquipmentController] PlayerInput was not found in parent objects. Equip input will not work.");
        }

        networkIdentity = GetComponentInParent<NetworkIdentity>();
        if (Mirror.NetworkClient.active && networkIdentity == null)
        {
            Debug.LogWarning("[PlayerEquipmentController] NetworkIdentity not found on parent. Local/remote detection may fail in networked games.");
        }

        if (swordObject == null)
        {
            Debug.LogWarning("[PlayerEquipmentController] SwordObject is not assigned. Visual equip/unequip will not be visible.");
        }

        if (swordOnBackSocket == null)
        {
            Debug.LogWarning("[PlayerEquipmentController] SwordOnBackSocket is not assigned.");
        }

        if (swordInHandSocket == null)
        {
            Debug.LogWarning("[PlayerEquipmentController] SwordInHandSocket is not assigned.");
        }

        try
        {
            if (playerInput != null && playerInput.actions != null && !string.IsNullOrEmpty(equipSwordActionName))
            {
                equipSwordAction = playerInput.actions[equipSwordActionName];
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PlayerEquipmentController] Failed to find EquipSword input action with name '{equipSwordActionName}'. Exception: {exception.Message}");
        }

        AttachSwordToBack();
        UpdateAnimatorSwordEquippedFlag();
    }

    private void OnEnable()
    {
        if (equipSwordAction != null)
        {
            equipSwordAction.Enable();
            equipSwordAction.performed += OnEquipSwordPerformed;
        }
    }

    private void OnDisable()
    {
        if (equipSwordAction != null)
        {
            equipSwordAction.performed -= OnEquipSwordPerformed;
            equipSwordAction.Disable();
        }
    }

    private bool IsLocalOrOffline()
    {
        if (!Mirror.NetworkClient.active)
        {
            return true;
        }

        if (networkIdentity == null)
        {
            return false;
        }

        return networkIdentity.isLocalPlayer;
    }

    private void OnEquipSwordPerformed(InputAction.CallbackContext context)
    {
        if (!IsLocalOrOffline())
        {
            return;
        }

        if (animator == null)
        {
            return;
        }

        if (!isSwordEquipped)
        {
            TrySetNetworkedTrigger(swordDrawTriggerName);
        }
        else
        {
            TrySetNetworkedTrigger(swordSheathTriggerName);
        }
    }

    private void TrySetNetworkedTrigger(string triggerName)
    {
        if (string.IsNullOrEmpty(triggerName))
        {
            return;
        }

        try
        {
            if (networkAnimator != null && Mirror.NetworkClient.active && IsLocalOrOffline())
            {
                networkAnimator.SetTrigger(triggerName);
            }
            else if (animator != null)
            {
                animator.SetTrigger(triggerName);
            }
        }
        catch (Exception exception)
        {
            Debug.LogError($"[PlayerEquipmentController] Failed to set trigger '{triggerName}'. Exception: {exception.Message}");
        }
    }

    public void AttachSwordToBack()
    {
        if (swordObject == null || swordOnBackSocket == null)
        {
            return;
        }

        swordObject.transform.SetParent(swordOnBackSocket);
        swordObject.transform.localPosition = Vector3.zero;
        swordObject.transform.localRotation = Quaternion.identity;

        isSwordEquipped = false;
        UpdateAnimatorSwordEquippedFlag();
    }

    public void AttachSwordToHand()
    {
        if (swordObject == null || swordInHandSocket == null)
        {
            return;
        }

        swordObject.transform.SetParent(swordInHandSocket);
        swordObject.transform.localPosition = Vector3.zero;
        swordObject.transform.localRotation = Quaternion.identity;

        isSwordEquipped = true;
        UpdateAnimatorSwordEquippedFlag();
    }

    private void UpdateAnimatorSwordEquippedFlag()
    {
        if (animator == null || string.IsNullOrEmpty(isSwordEquippedBoolName))
        {
            return;
        }

        animator.SetBool(isSwordEquippedBoolName, isSwordEquipped);
    }

    // Animation events
    public void OnSwordDraw()
    {
        AttachSwordToHand();
    }

    public void OnSwordSheath()
    {
        AttachSwordToBack();
    }
}