using UnityEngine;

namespace Characters.Status
{
    public abstract class StatusEffect : ScriptableObject
    {
        [Header("Basics")]
        [Tooltip("Display name for UI.")] public string displayName;
        [Tooltip("Max stacks (0 or 1 = non-stackable).")] public int maxStacks = 1;
        [Tooltip("Base duration seconds per stack.")] public float durationSeconds = 5f;
        [Tooltip("Tick rate per second for OnTick; 0 disables ticking.")] public float tickRate = 0f;

        [Tooltip("Called when effect is applied; stacks is the new stack count.")]
        public virtual void OnApplied(StatusController controller, int stacks) {}
        [Tooltip("Called when the effect expires or is removed.")]
        public virtual void OnRemoved(StatusController controller) {}
        [Tooltip("Called every tick if tickRate > 0; deltaTime is tick interval.")]
        public virtual void OnTick(StatusController controller, float deltaTime) {}
    }
}
