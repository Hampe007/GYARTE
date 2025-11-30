using Mirror;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// Minimal owner-driven movement that is enough for play-testing additive scene loading.
/// Attach alongside a CharacterController on the in-game player prefab.
/// Supports a basic pivot+main camera setup or a Cinemachine third-person follow that reuses the single main camera.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class SimpleNetworkPlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4.5f;
    [SerializeField, Min(1f)] private float sprintMultiplier = 1.4f;
    [SerializeField] private float gravity = 20f;
    [SerializeField] private float jumpVelocity = 5.5f;

    [Header("Look")]
    [SerializeField] private Transform cameraPivot;
    [SerializeField] private float lookSensitivity = 2f;
    [SerializeField] private Vector2 pitchLimits = new Vector2(-75f, 80f);

    [Header("Cinemachine (Optional)")]
    [SerializeField] private CinemachineCamera ownerCamera;
    [SerializeField] private Transform cameraFollowTarget;
    [SerializeField] private Transform cameraLookAtTarget;
    [SerializeField] private int ownerCameraPriority = 100;
    [SerializeField] private bool disableCameraForNonOwners = true;

    private CharacterController _controller;
    private Vector2 _cachedMoveInput;
    private bool _cachedSprint;
    private bool _cachedJump;

    private float _clientVerticalVelocity;
    private float _serverVerticalVelocity;
    private float _yaw;
    private float _pitch;
    private bool _cameraAttached;
    private bool _cinemachineActive;
    private int _initialCameraPriority;
    private bool _capturedInitialPriority;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();

        if (ownerCamera != null)
        {
            _initialCameraPriority = ownerCamera.Priority.Value;
            _capturedInitialPriority = true;

            // Start with all vcams disabled; local owner will enable in OnStartAuthority.
            SetCameraActive(false);
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (!isOwned)
            DisableRemoteCameraRig(); // make sure only the owning client keeps its camera active
    }

    public override void OnStartAuthority()
    {
        _yaw = transform.eulerAngles.y;
        ActivateLocalCamera();
        _clientVerticalVelocity = 0f;
    }

    public override void OnStopAuthority()
    {
        DeactivateLocalCamera();
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        if (isOwned)
            DeactivateLocalCamera();
    }

    private void Update()
    {
        if (!isOwned)
            return;

        EnsureLocalCamera();
        CacheInput();
        HandleLook();
    }

    private void FixedUpdate()
    {
        if (!isOwned)
            return;

        float dt = Time.fixedDeltaTime;
        Vector2 moveInput = _cachedMoveInput;
        bool wantsSprint = _cachedSprint;
        bool wantsJump = _cachedJump;
        _cachedJump = false;

        if (isServer)
        {
            Simulate(moveInput, wantsSprint, wantsJump, dt, ref _serverVerticalVelocity);
            RpcSyncState(transform.position, _yaw, _serverVerticalVelocity);
        }
        else
        {
            Simulate(moveInput, wantsSprint, wantsJump, dt, ref _clientVerticalVelocity);
            CmdMove(moveInput, wantsSprint, wantsJump, _yaw, dt);
        }
    }

    private void CacheInput()
    {
        float horizontal = Input.GetAxisRaw("Horizontal");
        float vertical = Input.GetAxisRaw("Vertical");
        Vector2 move = new Vector2(horizontal, vertical);
        if (move.sqrMagnitude > 1f)
            move.Normalize();

        _cachedMoveInput = move;
        _cachedSprint = Input.GetKey(KeyCode.LeftShift);
        if (Input.GetButtonDown("Jump"))
            _cachedJump = true;
    }

    private void HandleLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * lookSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * lookSensitivity;

        _yaw += mouseX;
        _pitch = Mathf.Clamp(_pitch - mouseY, pitchLimits.x, pitchLimits.y);

        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    private void Simulate(Vector2 moveInput, bool sprint, bool jump, float dt, ref float verticalVelocity)
    {
        Vector3 moveDir = transform.forward * moveInput.y + transform.right * moveInput.x;
        if (moveDir.sqrMagnitude > 1f)
            moveDir.Normalize();

        float speed = moveSpeed * (sprint ? sprintMultiplier : 1f);
        Vector3 horizontalVelocity = moveDir * speed;

        if (_controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        if (jump && _controller.isGrounded)
            verticalVelocity = jumpVelocity;

        verticalVelocity -= gravity * dt;

        Vector3 velocity = horizontalVelocity;
        velocity.y = verticalVelocity;

        _controller.Move(velocity * dt);
    }

    [Command]
    private void CmdMove(Vector2 moveInput, bool sprint, bool jump, float yaw, float dt)
    {
        _yaw = yaw;
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

        Simulate(moveInput, sprint, jump, dt, ref _serverVerticalVelocity);
        RpcSyncState(transform.position, _yaw, _serverVerticalVelocity);
    }

    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcSyncState(Vector3 position, float yaw, float verticalVelocity)
    {
        if (isOwned)
        {
            _clientVerticalVelocity = verticalVelocity;

            if ((transform.position - position).sqrMagnitude > 0.25f)
            {
                _controller.enabled = false;
                transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
                _controller.enabled = true;
            }

            return;
        }

        _yaw = yaw;
        _controller.enabled = false;
        transform.SetPositionAndRotation(position, Quaternion.Euler(0f, _yaw, 0f));
        _controller.enabled = true;
    }

    private void EnsureLocalCamera()
    {
        if (ownerCamera != null)
        {
            if (!_cinemachineActive || !ownerCamera.gameObject.activeInHierarchy)
                ActivateLocalCamera();
        }
        else
        {
            AttachFallbackCamera();
        }
    }

    private void ActivateLocalCamera()
    {
        if (ownerCamera == null)
        {
            AttachFallbackCamera();
            return;
        }

        DetachFallbackCamera();

        int priority = ownerCameraPriority;
        if (_capturedInitialPriority && priority <= _initialCameraPriority)
            priority = _initialCameraPriority + 1;

        SetCameraActive(true);
        ownerCamera.Follow = cameraFollowTarget != null ? cameraFollowTarget : GetDefaultFollowTarget();
        ownerCamera.LookAt = cameraLookAtTarget != null ? cameraLookAtTarget : GetDefaultLookAtTarget();
        ownerCamera.Priority.Value = priority;
        ownerCamera.Prioritize();

        _cinemachineActive = true;
        
        // Lock and hide cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void DeactivateLocalCamera()
    {
        if (ownerCamera != null)
        {
            if (_capturedInitialPriority)
                ownerCamera.Priority.Value = _initialCameraPriority;

            SetCameraActive(false);

            _cinemachineActive = false;
        }
        else
        {
            DetachFallbackCamera();
        }
        
        // Unlock and show cursor
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private Transform GetDefaultFollowTarget()
    {
        if (cameraFollowTarget != null)
            return cameraFollowTarget;
        if (cameraPivot != null)
            return cameraPivot;
        return transform;
    }

    private Transform GetDefaultLookAtTarget()
    {
        if (cameraLookAtTarget != null)
            return cameraLookAtTarget;
        if (cameraPivot != null)
            return cameraPivot;
        return transform;
    }

    private void AttachFallbackCamera()
    {
        if (cameraPivot == null || _cameraAttached)
            return;

        var mainCam = Camera.main;
        if (mainCam == null)
            return;

        mainCam.transform.SetParent(cameraPivot, false);
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;
        _cameraAttached = true;
    }

    private void DetachFallbackCamera()
    {
        if (!_cameraAttached || cameraPivot == null)
            return;

        var mainCam = Camera.main;
        if (mainCam != null && mainCam.transform.parent == cameraPivot)
            mainCam.transform.SetParent(null);

        _cameraAttached = false;
    }

    private void DisableRemoteCameraRig()
    {
        if (ownerCamera != null)
        {
            if (_capturedInitialPriority)
                ownerCamera.Priority.Value = _initialCameraPriority;

            ownerCamera.Priority.Value = int.MinValue; // make sure it never wins
            SetCameraActive(false);
        }

        DetachFallbackCamera();
        _cinemachineActive = false;
    }

    private void SetCameraActive(bool active)
    {
        if (ownerCamera == null)
            return;

        ownerCamera.enabled = active;
        ownerCamera.gameObject.SetActive(active);
    }
}
