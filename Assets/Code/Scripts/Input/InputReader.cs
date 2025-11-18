using UnityEngine;
using UnityEngine.InputSystem;

namespace Characters.Inputs
{
    // Minimal input wrapper for the New Input System.
    // Exposes state used by Movement FSM and basic combat (later).
    public sealed class InputReader : MonoBehaviour
    {
        [Header("Input Actions Asset")]
        [Tooltip("Reference to the Player.inputactions asset.")]
        [SerializeField] private InputActionAsset inputActions;

        // Cached map + actions
        private InputActionMap playerMap;
        private InputAction move;
        private InputAction look;
        private InputAction jump;
        private InputAction sprint;
        private InputAction dodge;
        private InputAction primaryAttack;
        private InputAction secondaryAttack;
        private InputAction slot1;
        private InputAction slot2;
        private InputAction slot3;
        private InputAction interact;
        //private InputAction inventory;

        [Header("Polled Axes")]
        [SerializeField] private Vector2 moveAxis;
        [SerializeField] private Vector2 lookAxis;

        [Header("Held Buttons")]
        [SerializeField] private bool sprintHeld;
        [SerializeField] private bool jumpHeld;
        [SerializeField] private bool dodgeHeld;

        [Header("One-shot Queues (buffered)")]
        [Tooltip("Jump press queued this frame (Movement FSM consumes and clears).")]
        [SerializeField] private bool jumpQueued;
        [SerializeField] private bool dodgeQueued;
        [SerializeField] private bool primaryQueued;
        [SerializeField] private bool secondaryQueued;
        [SerializeField] private bool interactQueued;
        //[SerializeField] private bool inventoryQueued;
        [SerializeField] private int slotSelectQueued = -1; // 1/2/3 when queued

        public Vector2 MoveAxis => moveAxis;
        public Vector2 LookAxis => lookAxis;
        public bool SprintHeld => sprintHeld;
        public bool JumpHeld => jumpHeld;
        public bool DodgeHeld => dodgeHeld;

        public bool ConsumeJump() { var v = jumpQueued; jumpQueued = false; return v; }
        public bool ConsumeDodge() { var v = dodgeQueued; dodgeQueued = false; return v; }
        public bool ConsumePrimary() { var v = primaryQueued; primaryQueued = false; return v; }
        public bool ConsumeSecondary() { var v = secondaryQueued; secondaryQueued = false; return v; }
        public bool ConsumeInteract() { var v = interactQueued; interactQueued = false; return v; }
        //public bool ConsumeInventory() { var v = inventoryQueued; inventoryQueued = false; return v; }
        public int ConsumeSlotSelect() { var v = slotSelectQueued; slotSelectQueued = -1; return v; }

        private void Awake()
        {
            if (inputActions == null)
            {
                Debug.LogWarning("InputReader: InputActionAsset is not assigned. Assign Assets/Settings/Input/Player.inputactions in inspector.");
                return;
            }

            playerMap = inputActions.FindActionMap("Player", throwIfNotFound: false);
            if (playerMap == null)
            {
                Debug.LogError("InputReader: Could not find 'Player' action map in assigned InputActionAsset.");
                return;
            }

            move = playerMap.FindAction("Move");
            look = playerMap.FindAction("Look");
            jump = playerMap.FindAction("Jump");
            sprint = playerMap.FindAction("Sprint");
            dodge = playerMap.FindAction("Dodge");
            primaryAttack = playerMap.FindAction("Primary Attack");
            secondaryAttack = playerMap.FindAction("Secondary Attack");
            slot1 = playerMap.FindAction("Slot1");
            slot2 = playerMap.FindAction("Slot2");
            slot3 = playerMap.FindAction("Slot3");
            interact = playerMap.FindAction("Interact");
            //inventory = playerMap.FindAction("Inventory");
        }

        private void OnEnable()
        {
            if (playerMap == null) return;

            playerMap.Enable();

            move.performed += OnMove;
            move.canceled += OnMove;

            look.performed += OnLook;
            look.canceled += OnLook;

            sprint.performed += ctx => sprintHeld = true;
            sprint.canceled += ctx => sprintHeld = false;

            jump.performed += ctx => { jumpQueued = true; jumpHeld = true; };
            jump.canceled += ctx => jumpHeld = false;

            dodge.performed += ctx => { dodgeQueued = true; dodgeHeld = true; };
            dodge.canceled += ctx => dodgeHeld = false;
            primaryAttack.performed += ctx => primaryQueued = true;
            secondaryAttack.performed += ctx => secondaryQueued = true;
            interact.performed += ctx => interactQueued = true;
            //inventory.performed += ctx => inventoryQueued = true;
            slot1.performed += ctx => slotSelectQueued = 1;
            slot2.performed += ctx => slotSelectQueued = 2;
            slot3.performed += ctx => slotSelectQueued = 3;
        }

        private void OnDisable()
        {
            if (playerMap == null) return;

            move.performed -= OnMove;
            move.canceled -= OnMove;

            look.performed -= OnLook;
            look.canceled -= OnLook;

            playerMap.Disable();
        }

        private void OnMove(InputAction.CallbackContext ctx) => moveAxis = ctx.ReadValue<Vector2>();
        private void OnLook(InputAction.CallbackContext ctx) => lookAxis = ctx.ReadValue<Vector2>();
    }
}
