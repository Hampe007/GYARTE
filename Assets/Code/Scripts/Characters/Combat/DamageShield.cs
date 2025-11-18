using UnityEngine;

namespace Characters.Combat
{
    // Simple timed damage shield. When Active, Health will ignore incoming damage
    // unless the caller explicitly bypasses i-frames (e.g., drowning DoT).
    public sealed class DamageShield : MonoBehaviour
    {
        [Tooltip("Whether the shield is currently active (read-only at runtime).")]
        [SerializeField] private bool active;
        [Tooltip("Optional debug: remaining time of the current shield.")]
        [SerializeField] private float remaining;

        public bool Active => active;

        private void Update()
        {
            if (!active) return;
            remaining -= Time.deltaTime;
            if (remaining <= 0f)
            {
                active = false;
                remaining = 0f;
            }
        }

        public void Activate(float seconds)
        {
            if (seconds <= 0f) return;
            active = true;
            remaining = Mathf.Max(remaining, seconds);
        }
    }
}

