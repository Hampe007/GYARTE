using System.Collections.Generic;
using UnityEngine;
using Mirror;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class GamePlayerController : NetworkBehaviour
{
    // ===== Movement Tuning =====
    [Header("Movement")]
    [Tooltip("Base move speed in m/s.")]
    [SerializeField] private float moveSpeed = 4.6f;

    [Tooltip("Sprint speed multiplier.")]
    [SerializeField] private float sprintMultiplier = 1.5f;

    [Tooltip("Crouch speed multiplier.")]
    [SerializeField] private float crouchMultiplier = 0.6f;

    [Tooltip("Gravity in m/s^2.")]
    [SerializeField] private float gravity = 19.62f;

    [Tooltip("Jump velocity in m/s (impulse applied on jump).")]
    [SerializeField] private float jumpVelocity = 5.8f;

    [Tooltip("How quickly we rotate body towards move direction (deg/s).")]
    [SerializeField] private float rotationSpeed = 720f;

    [Header("Input")]
    [Tooltip("Input actions asset containing the 'Player' map and actions.")]
    [SerializeField] private InputActionAsset inputActions;

    [Tooltip("Deadzone to ignore tiny stick drift (0..1).")]
    [SerializeField, Range(0f, 0.5f)] private float moveDeadzone = 0.1f;

    [Header("Camera")]
    [Tooltip("Root transform that yaws around the player for the follow camera.")]
    [SerializeField] private Transform cameraOrbitRoot;
    [Tooltip("Child transform that pitches up/down for the follow camera.")]
    [SerializeField] private Transform cameraPitchPivot;
    [Tooltip("Mouse/controller sensitivity multiplier for look input.")]
    [SerializeField] private float lookSensitivity = 120f;
    [Tooltip("Min/max pitch in degrees.")]
    [SerializeField] private Vector2 pitchLimits = new Vector2(-65f, 75f);
    [Tooltip("Invert vertical look input.")]
    [SerializeField] private bool invertY;

    [Header("Cinemachine (Optional)")]
    [Tooltip("Owner-specific Cinemachine camera to hand off control to the local player.")]
    [SerializeField] private CinemachineCamera ownerCamera;
    [Tooltip("Override follow target for the Cinemachine camera.")]
    [SerializeField] private Transform cameraFollowTarget;
    [Tooltip("Override look-at target for the Cinemachine camera.")]
    [SerializeField] private Transform cameraLookAtTarget;
    [Tooltip("Priority to force this camera to the top when owned.")]
    [SerializeField] private int ownerCameraPriority = 200;
    [Tooltip("If true, disable the Cinemachine camera object for non-owners.")]
    [SerializeField] private bool disableCameraForNonOwners = true;
    [Tooltip("Crosshair to toggle alongside the cursor state.")]
    [SerializeField] private GameObject crosshair;

    [Header("Prediction")]
    [Tooltip("If error > this, snap to server (meters). Otherwise, smooth.")]
    [SerializeField] private float snapThreshold = 0.5f;

    [Tooltip("Max history states to keep for resimulation.")]
    [SerializeField] private int historySize = 32;

    private CharacterController _cc;

    // Authoritative state (server truth)
    struct ServerState { public Vector3 pos; public Vector3 vel; public float yaw; public uint ackSeq; }

    // Client input packet
    struct InputPacket
    {
        public uint seq;
        public float dt;
        public Vector2 move;    // -1..1
        public bool jump;
        public bool sprint;
        public bool crouch;
        public float yaw;       // camera yaw (deg)
        public Vector2 look;    // raw look delta (for local camera only)
    }

    private Vector3 _vel;    // server and predicted velocity
    private float _yaw;      // server and predicted yaw for body facing
    private bool _isCrouching;

    // Prediction buffers
    private readonly Queue<InputPacket> _pending = new();         // inputs sent but not yet acked by server
    private readonly Dictionary<uint, (Vector3 pos, Vector3 vel, float yaw)> _history = new();

    private InputAction _moveA, _lookA, _jumpA, _sprintA, _crouchA, _interactA;
    private uint _nextSeq;
    private bool _isGrounded;
    private float _cameraYaw;
    private float _cameraPitch;
    private bool _cursorLockRequested;
    private bool _cameraAttached;
    private bool _cinemachineActive;
    private int _initialCameraPriority;
    private bool _capturedInitialPriority;

    // ========= Unity / Mirror lifecycle =========
    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        EnsureCameraRigTransforms();

        if (crosshair != null)
            crosshair.SetActive(false);

        if (ownerCamera != null)
        {
            _initialCameraPriority = ownerCamera.Priority.Value;
            _capturedInitialPriority = true;

            if (disableCameraForNonOwners)
                ownerCamera.gameObject.SetActive(false);
        }
    }

    public override void OnStartAuthority()
    {
        try
        {
            BindInputs(true);
            // start facing forward
            _yaw = transform.eulerAngles.y;
            InitializeCameraOrientation();
            EnsureLocalCamera();
            RequestCursorLock(true);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    public override void OnStopAuthority()
    {
        try
        {
            BindInputs(false);
            _pending.Clear();
            _history.Clear();
            RequestCursorLock(false);
            DeactivateLocalCamera();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    public override void OnStopClient()
    {
        base.OnStopClient();

        if (isOwned)
        {
            RequestCursorLock(false);
            DeactivateLocalCamera();
        }
    }

    private void BindInputs(bool enable)
    {
        try
        {
            if (inputActions == null) return;
            var map = inputActions.FindActionMap("Player", throwIfNotFound: false);
            if (map == null) return;

            _moveA = map.FindAction("Move", false);
            _lookA = map.FindAction("Look", false);
            _jumpA = map.FindAction("Jump", false);
            _sprintA = map.FindAction("Sprint", false);
            _crouchA = map.FindAction("Crouch", false);
            _interactA = map.FindAction("Interact", false);

            if (enable)
            {
                inputActions.Enable();
            }
            else
            {
                inputActions.Disable();
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    private void Update()
    {
        // Local owner: gather inputs, run prediction immediately for responsiveness,
        // send to server for authoritative sim.
        if (!isOwned) return;

        try
        {
            float dt = Time.deltaTime;

            Vector2 mv = Vector2.zero;
            if (_moveA != null) mv = _moveA.ReadValue<Vector2>();
            if (mv.sqrMagnitude < moveDeadzone * moveDeadzone) mv = Vector2.zero;

            Vector2 look = Vector2.zero;
            if (_lookA != null) look = _lookA.ReadValue<Vector2>();

            EnsureLocalCamera();
            HandleLook(look, dt);

            bool jump = _jumpA != null && _jumpA.WasPressedThisFrame();
            bool sprint = _sprintA != null && _sprintA.IsPressed();
            bool crouchToggle = _crouchA != null && _crouchA.WasPressedThisFrame();
            if (crouchToggle) _isCrouching = !_isCrouching;

            // Build packet & push to pending
            var pkt = new InputPacket
            {
                seq = _nextSeq++,
                dt = dt,
                move = mv,
                jump = jump,
                sprint = sprint,
                crouch = _isCrouching,
                yaw = _cameraYaw,
                look = look
            };
            _pending.Enqueue(pkt);

            // Predict locally using the same integrator as server
            Simulate(pkt, ref _vel, ref _yaw, dt, isServerAuthoritative:false);
            ApplyTransform(_cc, transform, ref _vel, ref _yaw, dt);

            // Store history snapshot for possible resim
            _history[pkt.seq] = (transform.position, _vel, _yaw);
            if (_history.Count > historySize)
            {
                // drop oldest
                uint oldest = pkt.seq - (uint)historySize;
                _history.Remove(oldest);
            }

            // send to server
            CmdSubmitInput(pkt);

            ApplyCameraOrientation();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    // ========= Server-side authoritative simulation =========

    [Command]
    private void CmdSubmitInput(InputPacket pkt, NetworkConnectionToClient sender = null)
    {
        try
        {
            float dt = Mathf.Clamp(pkt.dt, 0f, 0.1f);
            // Drive same integrator with provided inputs
            Simulate(pkt, ref _vel, ref _yaw, dt, isServerAuthoritative:true);
            ApplyTransform(_cc, transform, ref _vel, ref _yaw, dt);

            // Ack to this client (authoritative state)
            var state = new ServerState
            {
                pos = transform.position,
                vel = _vel,
                yaw = _yaw,
                ackSeq = pkt.seq
            };
            TargetReceiveState(sender, state);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    [TargetRpc]
    private void TargetReceiveState(NetworkConnection target, ServerState s)
    {
        if (!isOwned) return;

        try
        {
            // Reconciliation
            // 1) Snap if large error, else soft-correct
            Vector3 err = s.pos - transform.position;
            if (err.sqrMagnitude > snapThreshold * snapThreshold)
            {
                _cc.enabled = false;               // prevent unwanted moves
                transform.position = s.pos;
                transform.rotation = Quaternion.Euler(0f, s.yaw, 0f);
                _cc.enabled = true;
                _vel = s.vel;
            }
            else
            {
                // light correction
                transform.position += err * 0.5f;
                _yaw = Mathf.LerpAngle(_yaw, s.yaw, 0.5f);
                _vel = Vector3.Lerp(_vel, s.vel, 0.5f);
            }

            // 2) Drop all packets up to ackSeq
            while (_pending.Count > 0 && _pending.Peek().seq <= s.ackSeq)
                _pending.Dequeue();

            // 3) Resimulate remaining packets from the acknowledged state forward
            if (_history.TryGetValue(s.ackSeq, out var snap))
            {
                _cc.enabled = false;
                transform.position = s.pos;
                transform.rotation = Quaternion.Euler(0f, s.yaw, 0f);
                _vel = s.vel;
                _cc.enabled = true;

                foreach (var pkt in _pending)
                {
                    float dt = Mathf.Clamp(pkt.dt, 0f, 0.1f);
                    Simulate(pkt, ref _vel, ref _yaw, dt, isServerAuthoritative:false);
                    ApplyTransform(_cc, transform, ref _vel, ref _yaw, dt);
                }
            }

            ApplyCameraOrientation();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    // ========= Shared integrator (server & client) =========

    private void Simulate(InputPacket pkt, ref Vector3 vel, ref float yaw, float dt, bool isServerAuthoritative)
    {
        _isCrouching = pkt.crouch;

        // Derive desired world-space move direction from camera yaw + stick
        Vector3 fwd = Quaternion.Euler(0f, pkt.yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, pkt.yaw, 0f) * Vector3.right;
        Vector3 wishDir = (right * pkt.move.x + fwd * pkt.move.y);
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        float speed = moveSpeed;
        if (pkt.sprint && !_isCrouching) speed *= sprintMultiplier;
        if (_isCrouching) speed *= crouchMultiplier;

        Vector3 horizVel = wishDir * speed;
        vel.x = horizVel.x;
        vel.z = horizVel.z;

        // Gravity + jumping
        bool grounded = _cc.isGrounded; // NOTE: CharacterController.isGrounded is only meaningful after a Move()
        if (grounded && vel.y < 0f) vel.y = -2f; // small stick to ground

        if (pkt.jump && grounded && !_isCrouching)
            vel.y = jumpVelocity;

        vel.y -= gravity * dt;

        // Body facing aligns to movement direction (if any), otherwise face camera yaw
        Vector3 faceDir = wishDir.sqrMagnitude > 0.001f ? wishDir : (Quaternion.Euler(0f, pkt.yaw, 0f) * Vector3.forward);
        float targetYaw = Mathf.Atan2(faceDir.x, faceDir.z) * Mathf.Rad2Deg;
        yaw = Mathf.MoveTowardsAngle(yaw, targetYaw, rotationSpeed * dt);
    }

    private static void ApplyTransform(CharacterController cc, Transform tr, ref Vector3 vel, ref float yaw, float dt)
    {
        try
        {
            if (cc != null)
                cc.Move(vel * dt);
            tr.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, cc);
        }
    }

    // ========= Camera & cursor helpers =========

    private void EnsureCameraRigTransforms()
    {
        if (cameraOrbitRoot == null)
        {
            var orbitRoot = new GameObject("CameraOrbit");
            orbitRoot.transform.SetParent(transform, false);
            cameraOrbitRoot = orbitRoot.transform;
        }

        if (cameraPitchPivot == null)
        {
            var pitchPivot = new GameObject("CameraPivot");
            pitchPivot.transform.SetParent(cameraOrbitRoot, false);
            cameraPitchPivot = pitchPivot.transform;
        }

        if (cameraFollowTarget == null)
            cameraFollowTarget = cameraPitchPivot;

        if (cameraLookAtTarget == null)
            cameraLookAtTarget = cameraPitchPivot;
    }

    private void EnsureLocalCamera()
    {
        if (!isOwned)
            return;

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

        ownerCamera.gameObject.SetActive(true);
        ownerCamera.Follow = cameraFollowTarget != null ? cameraFollowTarget : GetDefaultFollowTarget();
        ownerCamera.LookAt = cameraLookAtTarget != null ? cameraLookAtTarget : GetDefaultLookAtTarget();
        ownerCamera.Priority.Value = priority;
        ownerCamera.Prioritize();

        _cinemachineActive = true;
    }

    private void DeactivateLocalCamera()
    {
        if (ownerCamera != null)
        {
            if (_capturedInitialPriority)
                ownerCamera.Priority.Value = _initialCameraPriority;

            if (disableCameraForNonOwners)
                ownerCamera.gameObject.SetActive(false);

            _cinemachineActive = false;
        }

        DetachFallbackCamera();
    }

    private Transform GetDefaultFollowTarget()
    {
        if (cameraPitchPivot != null)
            return cameraPitchPivot;
        if (cameraOrbitRoot != null)
            return cameraOrbitRoot;
        return transform;
    }

    private Transform GetDefaultLookAtTarget()
    {
        if (cameraPitchPivot != null)
            return cameraPitchPivot;
        if (cameraOrbitRoot != null)
            return cameraOrbitRoot;
        return transform;
    }

    private void AttachFallbackCamera()
    {
        if (cameraPitchPivot == null || _cameraAttached)
            return;

        var mainCam = Camera.main;
        if (mainCam == null)
            return;

        mainCam.transform.SetParent(cameraPitchPivot, false);
        mainCam.transform.localPosition = Vector3.zero;
        mainCam.transform.localRotation = Quaternion.identity;
        _cameraAttached = true;
    }

    private void DetachFallbackCamera()
    {
        if (!_cameraAttached)
            return;

        var mainCam = Camera.main;
        if (mainCam != null && cameraPitchPivot != null && mainCam.transform.parent == cameraPitchPivot)
            mainCam.transform.SetParent(null);

        _cameraAttached = false;
    }

    private void InitializeCameraOrientation()
    {
        EnsureCameraRigTransforms();

        _cameraYaw = cameraOrbitRoot != null
            ? cameraOrbitRoot.rotation.eulerAngles.y
            : transform.eulerAngles.y;

        if (cameraPitchPivot != null)
            _cameraPitch = NormalizeAngle(cameraPitchPivot.localEulerAngles.x);
        else
            _cameraPitch = 0f;

        _cameraPitch = Mathf.Clamp(_cameraPitch, pitchLimits.x, pitchLimits.y);
        ApplyCameraOrientation();
    }

    private void HandleLook(Vector2 lookDelta, float dt)
    {
        if (!_cursorLockRequested)
            return;

        if (lookDelta.sqrMagnitude > Mathf.Epsilon)
        {
            float yawDelta = lookDelta.x * lookSensitivity * dt;
            float pitchDelta = lookDelta.y * lookSensitivity * dt;

            _cameraYaw = NormalizeAngle(_cameraYaw + yawDelta);
            float signedPitch = invertY ? pitchDelta : -pitchDelta;
            _cameraPitch = Mathf.Clamp(_cameraPitch + signedPitch, pitchLimits.x, pitchLimits.y);
        }
    }

    private void ApplyCameraOrientation()
    {
        if (cameraOrbitRoot != null)
        {
            float playerYaw = transform.eulerAngles.y;
            float localYaw = NormalizeAngle(_cameraYaw - playerYaw);
            cameraOrbitRoot.localRotation = Quaternion.Euler(0f, localYaw, 0f);
        }

        if (cameraPitchPivot != null)
            cameraPitchPivot.localRotation = Quaternion.Euler(_cameraPitch, 0f, 0f);
    }

    private void ApplyCursorState()
    {
        if (!Application.isPlaying)
            return;

        Cursor.lockState = _cursorLockRequested ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !_cursorLockRequested;

        if (crosshair != null)
            crosshair.SetActive(_cursorLockRequested);
    }

    private void SetCursorLockInternal(bool locked)
    {
        _cursorLockRequested = locked;
        ApplyCursorState();
    }

    public void RequestCursorLock(bool locked)
    {
        if (!isOwned)
            return;

        SetCursorLockInternal(locked);
    }

    public bool IsCursorLocked => _cursorLockRequested;

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!isOwned)
            return;

        if (hasFocus)
            ApplyCursorState();
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        if (angle < -180f) angle += 360f;
        return angle;
    }
}
