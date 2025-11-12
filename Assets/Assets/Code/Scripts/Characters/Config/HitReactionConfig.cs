using UnityEngine;

namespace Characters.Config
{
    [CreateAssetMenu(menuName = "Config/Hit Reaction Config", fileName = "HitReactionConfig")]
    public sealed class HitReactionConfig : ScriptableObject
    {
        [Header("Stun")]
        [Tooltip("Seconds the character is stunned on taking damage.")]
        public float stunDuration = 0.25f;

        [Header("Knockback")]
        [Tooltip("Horizontal knockback velocity applied on hit (m/s). 0 disables.")]
        public float knockbackVelocity = 2.0f;
        [Tooltip("Upward extra velocity added on hit (m/s).")]
        public float knockbackUpward = 0.0f;

        [Header("Animator")]
        [Tooltip("Animator trigger parameter fired on hit (optional).")]
        public string hitTriggerParam = "Hit";
    }
}

