using UnityEngine;

namespace Characters.Config
{
    [CreateAssetMenu(menuName = "Config/Movement Config", fileName = "MovementConfig")]
    public sealed class MovementConfig : ScriptableObject
    {
        [Header("Speeds (m/s)")]
        [Tooltip("Base walking speed on ground.")]
        public float walkSpeed = 3.5f;
        [Tooltip("Sprinting speed on ground.")]
        public float sprintSpeed = 6.5f;
        [Tooltip("Swimming speed in water.")]
        public float swimSpeed = 3.0f;

        [Header("Acceleration")]
        [Tooltip("Grounded acceleration.")]
        public float groundAcceleration = 35f;
        [Tooltip("Airborne acceleration.")]
        public float airAcceleration = 10f;
        [Tooltip("Water acceleration while swimming.")]
        public float waterAcceleration = 12f;

        [Header("Friction/Drag")]
        [Tooltip("Friction applied when grounded.")]
        public float groundFriction = 6f;
        [Tooltip("Drag while airborne.")]
        public float airDrag = 0.2f;
        [Tooltip("Drag while swimming.")]
        public float waterDrag = 1.5f;

        [Header("Grounding & Surfaces")]
        [Tooltip("Maximum walkable slope angle in degrees. Surfaces steeper will cause sliding.")]
        [Range(10f, 85f)] public float slopeLimitDegrees = 50f;
        [Tooltip("Ground check ray/sphere cast distance from feet.")]
        public float groundCheckDistance = 0.25f;
        [Tooltip("Extra snap-to-ground range to keep character grounded over small bumps.")]
        public float stickToGroundDistance = 0.2f;
        [Tooltip("Step height the character can climb over when moving.")]
        public float stepOffsetHeight = 0.3f;
        [Tooltip("Forward distance for step test when moving.")]
        public float stepCheckDistance = 0.25f;

        [Header("Gravity & Jumping")]
        [Tooltip("Multiplier applied to global gravity.")]
        public float gravityScale = 1.0f;
        [Tooltip("Upward impulse applied when jumping.")]
        public float jumpForce = 5.5f;
        [Tooltip("Time after leaving ground where jump still allowed (seconds).")]
        public float coyoteTime = 0.1f;

        [Header("Air Control")]
        [Range(0f, 1f)]
        [Tooltip("How much steering is allowed in air (0–1).")]
        public float airControl = 0.25f;

        [Header("Dodge/Roll")]
        [Tooltip("Horizontal impulse applied on dodge/roll.")]
        public float dodgeImpulse = 8f;
        [Tooltip("Cooldown between dodges (seconds).")]
        public float dodgeCooldown = 0.5f;
        [Tooltip("Invulnerability window during dodge (seconds). Prevents damage from hits.")]
        public float dodgeIFrameDuration = 0.25f;

        [Header("Attack Lockouts")]
        [Tooltip("Seconds after dodge start during which movement input is ignored, preserving the dodge direction.")]
        public float dodgeDirectionLockSeconds = 0.1f;
        [Range(0f, 1f)]
        [Tooltip("Speed multiplier while attacking (applies during Startup/Active/Recovery).")]
        public float attackSpeedMultiplier = 0.9f;
        [Range(0f, 1f)]
        [Tooltip("Acceleration multiplier while attacking (applies during Startup/Active/Recovery).")]
        public float attackAccelerationMultiplier = 0.7f;

        [Header("Status / Hit Reactions")]
        [Range(0f, 1f)]
        [Tooltip("Movement speed multiplier while stunned (0..1). 1 = no slow.")]
        public float stunnedSpeedMultiplier = 0.5f;
        [Tooltip("Block sprint input while stunned.")]
        public bool blockSprintWhenStunned = true;
        [Tooltip("Block dodge input while stunned.")]
        public bool blockDodgeWhenStunned = true;

        [Header("Water / Drowning")]
        [Tooltip("Stamina drain per second while swimming.")]
        public float swimStaminaDrainPerSec = 5f;
        [Tooltip("Upward swim speed when holding Jump.")]
        public float swimRiseSpeed = 3.0f;
        [Tooltip("Downward swim speed when holding Descend (uses Dodge by default).")]
        public float swimSinkSpeed = 2.5f;
        [Tooltip("Acceleration applied to reach target vertical swim speeds.")]
        public float swimVerticalAcceleration = 8f;
        [Tooltip("Gravity scale while in water (reduces gravity for buoyant feel). 0 = weightless, 1 = normal.")]
        public float waterGravityScale = 0.25f;
        [Tooltip("Seconds without ground contact at 0 stamina before drowning starts.")]
        public float drowningNoGroundSeconds = 2.0f;
        [Tooltip("Damage per second applied while drowning.")]
        public float drownDamagePerSec = 15f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of capsule height to enter Swim (chest ~0.6).")]
        public float swimEnterHeightRatio = 0.6f;
        [Range(0f, 1f)]
        [Tooltip("Fraction of capsule height to exit Swim when grounded (knee ~0.2).")]
        public float swimExitHeightRatio = 0.2f;
        [Header("Drowning Rules")]
        [Tooltip("If true, drowning only occurs when not grounded. If false, drowning can occur while grounded if fully submerged.")]
        public bool drownRequiresUngrounded = true;
        [Range(0f, 1f)]
        [Tooltip("Submersion ratio required to count as fully submerged for drowning when grounded.")]
        public float drownSubmersionThreshold = 0.9f;

        [Header("Drowning Damage Curve")]
        [Tooltip("When enabled, scales Drown Damage Per Sec by this curve over the given duration.")]
        public bool useDrownDamageCurve = false;
        [Tooltip("Animation curve evaluated from 0..1 over Drown Curve Duration seconds. Value multiplies Drown Damage Per Sec. Can exceed 1 for exponential growth.")]
        public AnimationCurve drownDamageCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [Tooltip("Seconds for the curve time to reach 1. Damage scale = curve(t / duration).")]
        public float drownCurveDuration = 5f;

        [Header("Input Buffering (ms)")]
        [Tooltip("Generic input buffer window for jump/dodge/etc in milliseconds.")]
        public int inputBufferMs = 120;
    }
}

#if UNITY_EDITOR
namespace Characters.Config
{
    // Minimal custom inspector to show an info box; leaves layout to default.
    [UnityEditor.CustomEditor(typeof(MovementConfig))]
    public sealed class MovementConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            UnityEditor.EditorGUILayout.Space();
            UnityEditor.EditorGUILayout.HelpBox(
                "Final DPS = Base Drown Damage/s × Curve(t). Set curve flat at 1 for constant damage, or ramp for increasing danger.",
                UnityEditor.MessageType.Info);
        }
    }
}
#endif
