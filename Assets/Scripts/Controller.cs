using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class Controller : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTransform;

    [Header("Settings")]
    [Range(0f, 1f)] public float sensitivityX = 01f;
    [Range(0f, 1f)] public float sensitivityY = 01f;
    public Vector2 pitchLimits = new Vector2(-85f, 85f);
    public float moveSpeed = 4.5f;
    public float jumpHeight = 0f;
    float gravity = -9.82f;

    CharacterController controller;
    PlayerInputSystem controls;
    Vector2 moveInput;
    Vector2 lookInput;
    float pitch;
    float verticalVelocity;
    public int sensitivityScale = 1000;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        controls = new PlayerInputSystem();

        controls.Player.Move.performed += ctx => moveInput = ctx.ReadValue<Vector2>();
        controls.Player.Move.canceled += _ => moveInput = Vector2.zero;

        controls.Player.Look.performed += ctx => lookInput = ctx.ReadValue<Vector2>();
        controls.Player.Look.canceled += _ => lookInput = Vector2.zero;

        controls.Player.Jump.performed += _ => TryJump();
    }

    void OnEnable() => controls.Enable();
    void OnDisable() => controls.Disable();

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        // mouse look
        float sx = sensitivityX * sensitivityScale * Time.deltaTime;
        float sy = sensitivityY * sensitivityScale * Time.deltaTime;

        float yaw = lookInput.x * sx;
        float pitchDelta = lookInput.y * sy;

        transform.Rotate(Vector3.up * yaw);

        pitch -= pitchDelta;
        pitch = Mathf.Clamp(pitch, pitchLimits.x, pitchLimits.y);
        cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);

        // movement
        Vector3 move = (transform.right * moveInput.x + transform.forward * moveInput.y) * moveSpeed;

        if (controller.isGrounded)
        {
            if (verticalVelocity < 0f) verticalVelocity = -2f;
        }
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = new Vector3(move.x, verticalVelocity, move.z);
        controller.Move(velocity * Time.deltaTime);
    }

    void TryJump()
    {
        if (controller.isGrounded && jumpHeight > 0f)
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
    }
}
