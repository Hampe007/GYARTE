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

    // ========= Unity / Mirror lifecycle =========
    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
    }

    public override void OnStartAuthority()
    {
        try
        {
            BindInputs(true);
            // start facing forward
            _yaw = transform.eulerAngles.y;
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
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
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
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    // ========= Shared integrator (server & client) =========

    private void Simulate(InputPacket pkt, ref Vector3 vel, ref float yaw, float dt, bool isServerAuthoritative)
    {
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
}