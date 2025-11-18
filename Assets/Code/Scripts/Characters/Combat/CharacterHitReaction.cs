using UnityEngine;
using Characters.Config;

namespace Characters.Combat
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class CharacterHitReaction : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private HitReactionConfig config;

        [Header("References")]
        [SerializeField] private Characters.Health.Health health;
        [SerializeField] private Animator animator;

        private Rigidbody rb;
        private int hitTriggerHash;

        public bool IsStunned => Time.time < stunUntil;
        private float stunUntil = 0f;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (health == null) TryGetComponent(out health);
            if (animator == null) TryGetComponent(out animator);
            if (config != null && !string.IsNullOrEmpty(config.hitTriggerParam))
                hitTriggerHash = Animator.StringToHash(config.hitTriggerParam);
        }

        private void OnEnable()
        {
            if (health != null) health.OnDamaged += OnDamaged;
        }

        private void OnDisable()
        {
            if (health != null) health.OnDamaged -= OnDamaged;
        }

        private void OnDamaged(float amount, Vector3 point, Vector3 normal, GameObject source)
        {
            if (config == null) return;
            // Stun
            stunUntil = Mathf.Max(stunUntil, Time.time + Mathf.Max(0f, config.stunDuration));

            // Knockback (horizontal)
            if (rb != null && !rb.isKinematic)
            {
                Vector3 horiz = new Vector3(normal.x, 0f, normal.z).normalized;
                Vector3 v = horiz * Mathf.Max(0f, config.knockbackVelocity) + Vector3.up * Mathf.Max(0f, config.knockbackUpward);
                rb.linearVelocity += v;
            }

            // Animator trigger
            if (animator != null && hitTriggerHash != 0)
            {
                animator.SetTrigger(hitTriggerHash);
            }
        }
    }
}
