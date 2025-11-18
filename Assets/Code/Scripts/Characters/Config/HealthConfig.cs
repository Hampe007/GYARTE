using UnityEngine;

namespace Characters.Config
{
    [CreateAssetMenu(menuName = "Config/Health Config", fileName = "HealthConfig")]
    public sealed class HealthConfig : ScriptableObject
    {
        [Header("Health")]
        [Tooltip("Maximum hit points.")]
        public float maxHealth = 100f;

        [Header("Damage Handling")]
        [Tooltip("Invulnerability window after taking damage (seconds). 0 disables.")]
        public float postHitInvulnerableSeconds = 0.1f;

        [Header("Regen (optional)")]
        [Tooltip("Health regen per second. 0 disables.")]
        public float regenPerSecond = 0f;
        [Tooltip("Delay in seconds after damage before regen starts.")]
        public float regenDelaySeconds = 5f;

        [Header("Default Hit Zone Multipliers")]
        [Tooltip("Fallback multiplier when no specific zone applies or when using defaults.")]
        public float genericMultiplier = 1.0f;
        [Tooltip("Damage multiplier for head hurtboxes when using defaults.")]
        public float headMultiplier = 2.0f;
        [Tooltip("Damage multiplier for torso hurtboxes when using defaults.")]
        public float torsoMultiplier = 1.0f;
        [Tooltip("Damage multiplier for arm hurtboxes when using defaults.")]
        public float armMultiplier = 0.75f;
        [Tooltip("Damage multiplier for leg hurtboxes when using defaults.")]
        public float legMultiplier = 0.75f;
        [Tooltip("Damage multiplier for hand hurtboxes when using defaults.")]
        public float handMultiplier = 0.5f;
        [Tooltip("Damage multiplier for foot hurtboxes when using defaults.")]
        public float footMultiplier = 0.5f;
        [Tooltip("Damage multiplier for tail hurtboxes when using defaults.")]
        public float tailMultiplier = 0.75f;
    }
}

