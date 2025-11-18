using UnityEngine;

namespace Characters.Config
{
    [CreateAssetMenu(menuName = "Config/Status/Bleeding Config", fileName = "BleedingConfig")]
    public sealed class BleedingConfig : ScriptableObject
    {
        [Header("Damage-Over-Time")]
        [Tooltip("Damage per second per stack.")]
        public float damagePerSecond = 3f;
        [Tooltip("Seconds each stack lasts.")]
        public float durationSeconds = 6f;

        [Header("Stacking")]
        [Tooltip("Allow multiple concurrent stacks.")]
        public bool stackable = true;
        [Tooltip("Max stacks if stackable is enabled.")]
        public int maxStacks = 5;
    }
}

