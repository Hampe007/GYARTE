using UnityEngine;
using Characters.Config;
using Characters.Inputs;

namespace Characters.Movement
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public sealed class CharacterMovement : MonoBehaviour
    {
        public enum MovementState
        {
            Idle,
            Walk,
            Sprint,
            Jump,
            Fall,
            Swim,
            Dodge,
            Slide
        }

        public enum FacingMode
        {
            FaceMoveDirection,
            FaceCameraYaw
        }

        [Header("References")]
        [SerializeField] private InputReader input;
        [SerializeField] private MovementConfig config;
        [SerializeField] private Animator animator; // Upper-body layer reserved for sword later
        [Tooltip("Optional: Camera transform for camera-relative movement.")]
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private Characters.Stats.Stamina stamina; // optional
        [SerializeField] private Characters.Health.Health health; // optional, for drowning
        [SerializeField] private Characters.Combat.CharacterHitReaction hitReaction; // optional
        [SerializeField] private Characters.Combat.CharacterCombat combat; // optional

        [Header("Facing")]
        [Tooltip("How the character chooses facing. FaceCameraYaw = mouse-look (Fallout-style strafing).")]
        [SerializeField] private FacingMode facingMode = FacingMode.FaceMoveDirection;
        [Tooltip("Rotation interpolation factor (0-1) per FixedUpdate.")]
        [Range(0f,1f)] [SerializeField] private float rotationLerp = 0.2f;

        [Header("Grounding")]
        [Tooltip("Layers considered as ground for simple ground check.")]
        [SerializeField] private LayerMask groundLayers = ~0;

        [Header("Runtime State")]
        [SerializeField] private MovementState state = MovementState.Idle;
        [SerializeField] private bool isGrounded;
        [SerializeField] private bool inWater; // TODO: hook up to water volumes
        [SerializeField, Tooltip("World-space facing direction (XZ). Updated when moving.")]
        private Vector3 facing = Vector3.forward;
        [SerializeField, Tooltip("Fraction of capsule height currently submerged (0..1).")]
        private float submersionRatio;
        private float drowningTimer;
        [SerializeField, Tooltip("Current ground normal (debug).")]
        private Vector3 groundNormal = Vector3.up;
        [SerializeField, Tooltip("Current ground slope angle in degrees (debug).")]
        private float groundAngleDeg;

        private Rigidbody rb;
        private CapsuleCollider capsule;
        private float jumpBufferedUntil;
        private float lastGroundedTime;

        // Animator param hashes (parameter-driven)
        private static readonly int HashSpeed = Animator.StringToHash("Speed");
        private static readonly int HashIsGrounded = Animator.StringToHash("IsGrounded");
        private static readonly int HashState = Animator.StringToHash("State");
        private static readonly int HashStunned = Animator.StringToHash("Stunned");

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();

            // Rigidbody baseline for character
            rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.useGravity = true;

            // Auto-wire optional components if not assigned
            if (stamina == null) TryGetComponent(out stamina);
            if (health == null) TryGetComponent(out health);
            if (hitReaction == null) TryGetComponent(out hitReaction);
            if (combat == null) TryGetComponent(out combat);
        }

        private void FixedUpdate()
        {
            // Robust ground check (multi-probe + slope info)
            isGrounded = CheckGrounded();
            if (isGrounded)
                lastGroundedTime = Time.time;

            // State priority: Dodge > Jump > Swim > Sprint > Move > Idle
            var moveInput = input != null ? input.MoveAxis : Vector2.zero;
            var hasMove = moveInput.sqrMagnitude > 0.01f;

            // Buffer jump input for a short window
            if (input != null && input.ConsumeJump())
            {
                float bufferSec = (config != null ? config.inputBufferMs : 120) / 1000f;
                jumpBufferedUntil = Time.time + bufferSec;
            }

            bool stunned = hitReaction != null && hitReaction.IsStunned;
            if (!inWater && input != null && input.ConsumeDodge() && !(stunned && (config == null || config.blockDodgeWhenStunned)))
            {
                // Stamina gate for dodge if configured
                bool canDodge = true;
                var staminaConfig = GetStaminaConfig();
                if (stamina != null && staminaConfig != null)
                    canDodge = stamina.TryConsume(Mathf.Max(0f, staminaConfig.dodgeCost));
                // Cooldown gate
                bool offCooldown = Time.time >= lastDodgeTime + (config != null ? Mathf.Max(0f, config.dodgeCooldown) : 0.5f);
                if (canDodge && offCooldown)
                    TransitionTo(MovementState.Dodge);
            }
            else if (!inWater && Time.time <= jumpBufferedUntil && (isGrounded || Time.time <= lastGroundedTime + (config != null ? config.coyoteTime : 0.1f)))
            {
                TransitionTo(MovementState.Jump);
                jumpBufferedUntil = 0f; // consume buffer on jump
            }
            else if (inWater)
            {
                TransitionTo(MovementState.Swim);
            }
            else if (isGrounded && groundAngleDeg > (config != null ? config.slopeLimitDegrees : 50f) + 0.5f)
            {
                TransitionTo(MovementState.Slide);
            }
            else if (hasMove && input != null && input.SprintHeld && !(stunned && (config == null || config.blockSprintWhenStunned)))
            {
                TransitionTo(MovementState.Sprint);
            }
            else if (hasMove)
            {
                TransitionTo(MovementState.Walk);
            }
            else
            {
                TransitionTo(MovementState.Idle);
            }

            if (!isGrounded && state != MovementState.Jump && state != MovementState.Swim)
            {
                // Falling catch-all
                TransitionTo(MovementState.Fall);
            }

            // Apply movement according to state (simple acceleration model)
            ApplyMovement(moveInput);

            // Continuous stamina drains and drowning
            HandleStaminaAndDrowning(hasMove);

            // Drive animator (parameter-driven; movement application added later)
            if (animator != null)
            {
                animator.SetBool(HashIsGrounded, isGrounded);
                var horizVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude;
                animator.SetFloat(HashSpeed, horizVel);
                animator.SetInteger(HashState, (int)state);
                animator.SetBool(HashStunned, stunned);
            }
        }

        private float lastDodgeTime;
        private float dodgeLockUntil;
        private void TransitionTo(MovementState newState)
        {
            if (state == newState) return;
            state = newState;

            switch (state)
            {
                case MovementState.Jump:
                    // Minimal jump impulse; refined later using config and buffering/coyote.
                    var v = rb.linearVelocity;
                    v.y = 0f;
                    rb.linearVelocity = v;
                    rb.AddForce(Vector3.up * (config != null ? config.jumpForce : 5.5f), ForceMode.VelocityChange);
                    break;
                case MovementState.Dodge:
                    // Directional dodge based on current move input
                    Vector3 dir = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                    if (input != null)
                    {
                        var m = input.MoveAxis;
                        var camFwd = transform.forward;
                        var camRight = transform.right;
                        dir = (camFwd * m.y + camRight * m.x).normalized;
                        if (dir.sqrMagnitude < 0.01f)
                            dir = transform.forward;
                    }
                    float impulse = config != null ? config.dodgeImpulse : 8f;
                    rb.AddForce(dir * impulse, ForceMode.VelocityChange);
                    lastDodgeTime = Time.time;
                    dodgeLockUntil = Time.time + (config != null ? Mathf.Max(0f, config.dodgeDirectionLockSeconds) : 0.1f);
                    // Activate dodge i-frames via DamageShield if present
                    var shield = GetComponent<Characters.Combat.DamageShield>();
                    if (shield != null && config != null)
                    {
                        shield.Activate(Mathf.Max(0f, config.dodgeIFrameDuration));
                    }
                    break;
            }
        }

        private bool CheckGrounded()
        {
            groundNormal = Vector3.up;
            groundAngleDeg = 0f;

            float radius = Mathf.Max(0.05f, capsule.radius * 0.95f);
            float checkDist = config != null ? Mathf.Max(0.02f, config.groundCheckDistance) : 0.25f;

            // Probe positions: center + 4 offsets around the base circle
            Vector3 basePos = transform.position + Vector3.up * (radius + 0.01f);
            Vector3[] offsets = new Vector3[5];
            offsets[0] = Vector3.zero;
            float ring = radius * 0.5f;
            offsets[1] = new Vector3(ring, 0f, 0f);
            offsets[2] = new Vector3(-ring, 0f, 0f);
            offsets[3] = new Vector3(0f, 0f, ring);
            offsets[4] = new Vector3(0f, 0f, -ring);

            bool hitAny = false;
            Vector3 avgNormal = Vector3.zero;
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 origin = basePos + offsets[i];
                if (Physics.SphereCast(origin, radius, Vector3.down, out var hit, checkDist, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    hitAny = true;
                    avgNormal += hit.normal;
                }
            }

            if (hitAny)
            {
                groundNormal = avgNormal.normalized;
                groundAngleDeg = Vector3.Angle(groundNormal, Vector3.up);
                // Stick to ground: if very close below and moving downward, nudge onto ground
                float stickDist = config != null ? Mathf.Max(0f, config.stickToGroundDistance) : 0.2f;
                if (Physics.SphereCast(basePos, radius, Vector3.down, out var hit, stickDist, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    if (rb.linearVelocity.y <= 0.1f)
                    {
                        // Project velocity onto ground plane to avoid tiny bounces
                        Vector3 v = rb.linearVelocity;
                        Vector3 along = Vector3.ProjectOnPlane(new Vector3(v.x, 0f, v.z), groundNormal);
                        rb.linearVelocity = new Vector3(along.x, Mathf.Min(v.y, 0f), along.z);
                    }
                }
            }

            return hitAny;
        }

        private void ApplyMovement(Vector2 moveInput)
        {
            // Camera-relative input to world-space direction
            Vector3 worldDir = GetWorldDirection(moveInput);
            bool hasMove = worldDir.sqrMagnitude > 0.0001f;
            if (state == MovementState.Dodge && Time.time < dodgeLockUntil)
            {
                hasMove = false; // lock direction during early dodge window
            }

            // Determine target speed by state
            float targetSpeed = 0f;
            float accel = 0f;
            float drag = 0f;
            switch (state)
            {
                case MovementState.Sprint:
                    targetSpeed = config != null ? config.sprintSpeed : 6.5f;
                    accel = config != null ? config.groundAcceleration : 35f;
                    drag = config != null ? config.groundFriction : 6f;
                    break;
                case MovementState.Walk:
                    targetSpeed = config != null ? config.walkSpeed : 3.5f;
                    accel = config != null ? config.groundAcceleration : 35f;
                    drag = config != null ? config.groundFriction : 6f;
                    break;
                case MovementState.Jump:
                case MovementState.Fall:
                    targetSpeed = (config != null ? config.walkSpeed : 3.5f) * (config != null ? config.airControl : 0.25f);
                    accel = config != null ? config.airAcceleration : 10f;
                    drag = config != null ? config.airDrag : 0.2f;
                    break;
                case MovementState.Swim:
                    targetSpeed = config != null ? config.swimSpeed : 3f;
                    accel = config != null ? config.waterAcceleration : 12f;
                    drag = config != null ? config.waterDrag : 1.5f;
                    break;
                case MovementState.Slide:
                    targetSpeed = (config != null ? config.sprintSpeed : 6.5f);
                    accel = (config != null ? config.groundAcceleration : 35f) * 0.5f;
                    drag = (config != null ? config.groundFriction : 6f) * 0.25f; // low friction on slide
                    break;
                default:
                    accel = config != null ? config.groundAcceleration : 35f;
                    drag = isGrounded ? (config != null ? config.groundFriction : 6f) : (config != null ? config.airDrag : 0.2f);
                    break;
            }

            // Apply stun speed multiplier
            if (hitReaction != null && hitReaction.IsStunned && config != null)
            {
                targetSpeed *= Mathf.Clamp01(config.stunnedSpeedMultiplier);
            }

            // Attack locomotion slowdowns
            if (combat != null && combat.CurrentPhase != Characters.Combat.CharacterCombat.AttackPhase.Idle && config != null)
            {
                targetSpeed *= Mathf.Clamp01(config.attackSpeedMultiplier);
                accel *= Mathf.Clamp01(config.attackAccelerationMultiplier);
            }

            // Attributes scaling (DEX) on acceleration
            var attrs = GetComponent<Characters.Attributes.CharacterAttributes>();
            if (attrs != null)
            {
                accel *= Mathf.Max(0.1f, attrs.AccelScale);
            }

            // Determine desired facing
            Vector3 desiredFacing = Vector3.zero;
            if (facingMode == FacingMode.FaceCameraYaw && cameraTransform != null)
            {
                desiredFacing = cameraTransform.forward; desiredFacing.y = 0f; desiredFacing.Normalize();
            }
            else if (hasMove)
            {
                desiredFacing = worldDir;
            }

            // Apply facing
            if (desiredFacing.sqrMagnitude > 0.0001f)
            {
                facing = desiredFacing;
                Quaternion targetRot = Quaternion.LookRotation(facing, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationLerp);
            }

            // Horizontal velocity control
            Vector3 vel = rb.linearVelocity;
            Vector3 horiz = new Vector3(vel.x, 0f, vel.z);

            if (hasMove)
            {
                Vector3 desired = worldDir * Mathf.Max(targetSpeed, 0f);
                Vector3 delta = desired - horiz;
                float maxChange = accel * Time.fixedDeltaTime;
                Vector3 change = Vector3.ClampMagnitude(delta, maxChange);
                rb.linearVelocity += new Vector3(change.x, 0f, change.z);
            }
            else
            {
                // Apply simple friction when no input
                float friction = Mathf.Max(0f, drag);
                float drop = friction * Time.fixedDeltaTime;
                float newMag = Mathf.Max(0f, horiz.magnitude - drop);
                if (horiz.sqrMagnitude > 0.0001f)
                {
                    Vector3 newHoriz = horiz.normalized * newMag;
                    Vector3 change = newHoriz - horiz;
                    rb.linearVelocity += new Vector3(change.x, 0f, change.z);
                }
            }

            // Sliding down slope when on Slide state
            if (state == MovementState.Slide)
            {
                // Gravity along plane
                Vector3 downslope = Vector3.ProjectOnPlane(Vector3.down, groundNormal).normalized;
                float slideAccel = (config != null ? config.groundAcceleration : 35f) * Mathf.Clamp01(groundAngleDeg / Mathf.Max(1f, (config != null ? config.slopeLimitDegrees : 50f)));
                rb.linearVelocity += downslope * slideAccel * Time.fixedDeltaTime;
            }

            // Vertical control in Swim
            if (state == MovementState.Swim)
            {
                float riseSpeed = config != null ? config.swimRiseSpeed : 3f;
                float sinkSpeed = config != null ? config.swimSinkSpeed : 2.5f;
                float vAccel = config != null ? config.swimVerticalAcceleration : 8f;
                float targetVy = 0f;
                bool up = input != null && input.JumpHeld;
                bool down = input != null && input.DodgeHeld; // reuse Dodge as descend in water
                if (up) targetVy = riseSpeed;
                else if (down) targetVy = -sinkSpeed;
                // Move current y toward target y-speed
                float newVy = Mathf.MoveTowards(rb.linearVelocity.y, targetVy, vAccel * Time.fixedDeltaTime);
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, newVy, rb.linearVelocity.z);
            }

            // Try simple step up when moving into a low obstacle
            if (hasMove && isGrounded && (config != null ? config.stepOffsetHeight : 0.3f) > 0f)
            {
                TryStepUp(worldDir);
            }

            // Extra gravity scaling
            if (!inWater)
            {
                float gScale = config != null ? Mathf.Max(0f, config.gravityScale) : 1f;
                if (gScale != 1f)
                {
                    Vector3 extraG = Physics.gravity * (gScale - 1f) * Time.fixedDeltaTime;
                    rb.linearVelocity += extraG;
                }
            }
            else
            {
                // Reduced gravity in water for buoyancy-like feel
                float wg = config != null ? Mathf.Clamp(config.waterGravityScale, 0f, 1f) : 0.25f;
                if (wg != 1f)
                {
                    Vector3 scaledG = Physics.gravity * (wg - 1f) * Time.fixedDeltaTime; // negative if wg<1 -> adds upward to counter gravity
                    rb.linearVelocity += scaledG;
                }
            }
        }

        private Vector3 GetWorldDirection(Vector2 move)
        {
            if (move.sqrMagnitude < 0.0001f)
                return Vector3.zero;

            Vector3 fwd, right;
            if (cameraTransform != null)
            {
                fwd = cameraTransform.forward; fwd.y = 0f; fwd.Normalize();
                right = cameraTransform.right; right.y = 0f; right.Normalize();
            }
            else
            {
                fwd = transform.forward; fwd.y = 0f; fwd.Normalize();
                right = transform.right; right.y = 0f; right.Normalize();
            }
            Vector3 dir = fwd * move.y + right * move.x;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            return dir;
        }

        private void TryStepUp(Vector3 worldDir)
        {
            float stepHeight = config != null ? Mathf.Max(0f, config.stepOffsetHeight) : 0.3f;
            float checkDist = config != null ? Mathf.Max(0.05f, config.stepCheckDistance) : 0.25f;
            float radius = Mathf.Max(0.05f, capsule.radius * 0.95f);

            // Cast forward at feet level to see if there's an obstacle
            Vector3 origin = transform.position + Vector3.up * (radius + 0.02f);
            if (Physics.SphereCast(origin, radius * 0.9f, worldDir, out var hit, checkDist, groundLayers, QueryTriggerInteraction.Ignore))
            {
                // Check if stepping up clears the obstacle
                Vector3 stepOrigin = origin + Vector3.up * stepHeight;
                if (!Physics.SphereCast(stepOrigin, radius * 0.9f, worldDir, out var hitUpper, checkDist, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    // Lift by stepHeight smoothly
                    transform.position += Vector3.up * stepHeight;
                }
            }
        }

        private Characters.Config.StaminaConfig GetStaminaConfig() => stamina != null ? stamina.Config : null;

        private bool warnedNoHealth;
        private void HandleStaminaAndDrowning(bool hasMove)
        {
            var staminaCfg = GetStaminaConfig();
            if (stamina != null && staminaCfg != null)
            {
                // Sprint drain
                if (state == MovementState.Sprint && input != null && input.SprintHeld && hasMove)
                {
                    stamina.ConsumeContinuous(Mathf.Max(0f, staminaCfg.sprintDrainPerSec));
                    if (stamina.IsDepleted)
                    {
                        // Fall back to walk if depleted
                        state = MovementState.Walk;
                    }
                }
            }

            // Swim drain and drowning
            if (state == MovementState.Swim && stamina != null && config != null)
            {
                stamina.ConsumeContinuous(Mathf.Max(0f, config.swimStaminaDrainPerSec));
                bool drowningEligible;
                if (config.drownRequiresUngrounded)
                {
                    drowningEligible = stamina.IsDepleted && !isGrounded;
                }
                else
                {
                    // Allow drowning while grounded if fully submerged
                    drowningEligible = stamina.IsDepleted && submersionRatio >= Mathf.Max(0f, config.drownSubmersionThreshold);
                }

                if (drowningEligible)
                {
                    drowningTimer += Time.fixedDeltaTime;
                    if (drowningTimer >= Mathf.Max(0f, config.drowningNoGroundSeconds))
                    {
                        if (health != null)
                        {
                            // Compute damage scale using optional curve
                            float basePerSec = Mathf.Max(0f, config.drownDamagePerSec);
                            float perSec = basePerSec;
                            if (config.useDrownDamageCurve && config.drownCurveDuration > 0.0001f)
                            {
                                float elapsedSinceStart = drowningTimer - Mathf.Max(0f, config.drowningNoGroundSeconds);
                                float t = Mathf.Clamp01(elapsedSinceStart / config.drownCurveDuration);
                                float scale = config.drownDamageCurve != null ? config.drownDamageCurve.Evaluate(t) : 1f;
                                perSec = basePerSec * Mathf.Max(0f, scale);
                            }
                            float dmg = perSec * Time.fixedDeltaTime;
                            // Bypass i-frames so DoT applies smoothly per second
                            health.ApplyDamage(dmg, transform.position, Vector3.up, gameObject, ignoreIFrames: true);
                        }
                        else if (!warnedNoHealth)
                        {
                            Debug.LogWarning("CharacterMovement: Drowning active but no Health component is assigned/found on the player. Add Health to receive drown damage.", this);
                            warnedNoHealth = true;
                        }
                    }
                }
                else
                {
                    drowningTimer = 0f;
                }
            }
            else
            {
                drowningTimer = 0f;
            }
        }

        private void OnTriggerStay(Collider other)
        {
            var water = other.GetComponent<Environment.Water.WaterVolume>();
            if (water == null) return;

            float feetY = transform.position.y;
            float height = Mathf.Max(0.01f, capsule.height);
            float waterSurfaceY = water.SurfaceY;

            float depth = waterSurfaceY - feetY; // meters above feet
            submersionRatio = Mathf.Clamp01(depth / height);

            // Enter swim when above chest
            float enterRatio = (config != null ? config.swimEnterHeightRatio : 0.6f);
            float exitRatio = (config != null ? config.swimExitHeightRatio : 0.2f);
            if (submersionRatio >= enterRatio)
            {
                inWater = true;
            }
            else if (isGrounded && submersionRatio < exitRatio)
            {
                inWater = false;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            var water = other.GetComponent<Environment.Water.WaterVolume>();
            if (water == null) return;
            inWater = false;
            submersionRatio = 0f;
        }
    }
}
