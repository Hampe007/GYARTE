using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Handles equipping and unequipping the sword.
/// - Listens to an EquipSword input action.
/// - Triggers sword draw/sheath animations.
/// - Receives animation events to move the sword between back and hand.
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

    private void Awake()
    {
        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }

        if (animator == null)
        {
            Debug.LogError("[PlayerEquipmentController] Animator is missing. Please add this script to the same GameObject that has the Animator.");
        }

        // We expect PlayerInput to be on a parent object (the Player root).
        playerInput = GetComponentInParent<PlayerInput>();
        if (playerInput == null)
        {
            Debug.LogError("[PlayerEquipmentController] PlayerInput was not found in parent objects. Equip input will not work.");
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

        // Try to find the EquipSword action
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

        // At start, keep the sword on the back.
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

    private void OnEquipSwordPerformed(InputAction.CallbackContext context)
    {
        // When the EquipSword button is pressed, we toggle between draw and sheath.
        if (animator == null)
        {
            return;
        }

        if (!isSwordEquipped)
        {
            // Sword is currently on the back -> play draw animation
            if (!string.IsNullOrEmpty(swordDrawTriggerName))
            {
                animator.SetTrigger(swordDrawTriggerName);
            }
        }
        else
        {
            // Sword is currently in the hand -> play sheath animation
            if (!string.IsNullOrEmpty(swordSheathTriggerName))
            {
                animator.SetTrigger(swordSheathTriggerName);
            }
        }
    }

    /// <summary>
    /// Attaches the sword to the back socket.
    /// </summary>
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

    /// <summary>
    /// Attaches the sword to the hand socket.
    /// </summary>
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

    // === Animation events ===

    /// <summary>
    /// Animation event: called from Sword_Enter animation when the sword should appear in the hand.
    /// </summary>
    public void OnSwordDraw()
    {
        AttachSwordToHand();
    }

    /// <summary>
    /// Animation event: called from Sword_Exit animation when the sword should go back to the back.
    /// </summary>
    public void OnSwordSheath()
    {
        AttachSwordToBack();
    }
}
