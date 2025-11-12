using System;
using UnityEngine;
using Characters.Config;
using Characters.Combat;

namespace Characters.Health
{
    // Minimal health component implementing IDamageable for local testing.
    public sealed class Health : MonoBehaviour, IDamageable
    {
        [Header("Config")]
        [SerializeField] private HealthConfig config;

        [Header("Runtime State")]
        [SerializeField] private float currentHealth;
        [SerializeField] private bool isDead;

        public event Action<float, float> OnHealthChanged; // (current, max)
        public event Action<float, Vector3, Vector3, GameObject> OnDamaged; // (amount, point, normal, source)
        public event Action<GameObject> OnDied; // (source)

        private float lastDamageTime = -999f;
        private float regenResumeTime = 0f;

        public float Current => currentHealth;
        public float Max
        {
            get
            {
                float baseMax = config != null ? Mathf.Max(1f, config.maxHealth) : 100f;
                var attr = GetComponent<Characters.Attributes.CharacterAttributes>();
                return attr != null ? baseMax * Mathf.Max(0.1f, attr.HealthScale) : baseMax;
            }
        }
        public HealthConfig Config => config;

        private void Reset()
        {
            // Try to ease setup by creating a default config if missing (editor-time only recommendation in inspector)
        }

        private void Awake()
        {
            currentHealth = Max;
        }

        private void Update()
        {
            if (isDead) return;

            // Passive regen
            if (config != null && config.regenPerSecond > 0f && Time.time >= regenResumeTime)
            {
                float old = currentHealth;
                currentHealth = Mathf.Min(Max, currentHealth + config.regenPerSecond * Time.deltaTime);
                if (!Mathf.Approximately(old, currentHealth))
                    OnHealthChanged?.Invoke(currentHealth, Max);
            }
        }

        public void ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject source)
            => ApplyDamage(amount, hitPoint, hitNormal, source, ignoreIFrames: false);

        // Overload to allow damage that bypasses post-hit invulnerability (e.g., DoT like drowning)
        public void ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject source, bool ignoreIFrames)
        {
            if (isDead) return;
            if (!ignoreIFrames)
            {
                var shield = GetComponent<Characters.Combat.DamageShield>();
                if (shield != null && shield.Active) return; // dodge i-frames or similar
            }
            if (!ignoreIFrames && config != null && config.postHitInvulnerableSeconds > 0f)
            {
                if (Time.time < lastDamageTime + config.postHitInvulnerableSeconds)
                    return; // i-frames
            }

            lastDamageTime = Time.time;
            regenResumeTime = Time.time + (config != null ? config.regenDelaySeconds : 0f);

            float old = currentHealth;
            currentHealth = Mathf.Max(0f, currentHealth - Mathf.Max(0f, amount));
            OnDamaged?.Invoke(amount, hitPoint, hitNormal, source);
            if (!Mathf.Approximately(old, currentHealth))
                OnHealthChanged?.Invoke(currentHealth, Max);

            if (currentHealth <= 0f)
            {
                isDead = true;
                OnDied?.Invoke(source);
                // Minimal death handling: disable collider + rigidbody movement
                var col = GetComponent<Collider>(); if (col) col.enabled = false;
                var rb = GetComponent<Rigidbody>(); if (rb) rb.isKinematic = true;
            }
        }

        // Extended overload including damage type and resistance application.
        public void ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject source, bool ignoreIFrames, Characters.Combat.DamageType damageType)
        {
            // Apply resistances before delegating
            var resist = GetComponent<Characters.Combat.DamageResistances>();
            if (resist != null)
            {
                amount *= Mathf.Max(0f, resist.GetMultiplier(damageType));
            }
            ApplyDamage(amount, hitPoint, hitNormal, source, ignoreIFrames);
        }
    }
}
