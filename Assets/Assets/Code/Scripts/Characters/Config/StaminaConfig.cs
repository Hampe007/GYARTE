using UnityEngine;

namespace Characters.Config
{
    [CreateAssetMenu(menuName = "Config/Stamina Config", fileName = "StaminaConfig")]
    public sealed class StaminaConfig : ScriptableObject
    {
        [Header("Stamina Pool")]
        [Tooltip("Maximum stamina.")]
        public float maxStamina = 100f;
        [Tooltip("Regeneration per second when allowed.")]
        public float regenPerSecond = 12f;
        [Tooltip("Delay after spending before regen resumes (seconds).")]
        public float regenDelaySeconds = 0.8f;

        [Header("Costs / Drains")]
        [Tooltip("Drain per second while sprinting.")]
        public float sprintDrainPerSec = 12f;
        [Tooltip("Cost for a dodge/roll.")]
        public float dodgeCost = 25f;
        [Tooltip("Minimum stamina required to start sprinting. 0 to disable gate.")]
        public float minToSprint = 0f;
    }
}

