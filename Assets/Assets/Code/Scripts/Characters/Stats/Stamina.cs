using System;
using UnityEngine;
using Characters.Config;

namespace Characters.Stats
{
    public sealed class Stamina : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private StaminaConfig config;

        [Header("Runtime State")]
        [SerializeField] private float current;
        private float regenResumeTime;

        public event Action<float, float> OnStaminaChanged; // (current, max)

        public float Current => current;
        public float Max
        {
            get
            {
                float baseMax = config != null ? Mathf.Max(1f, config.maxStamina) : 100f;
                var attr = GetComponent<Characters.Attributes.CharacterAttributes>();
                return attr != null ? baseMax * Mathf.Max(0.1f, attr.StaminaScale) : baseMax;
            }
        }
        public bool IsDepleted => current <= 0.0001f;
        public StaminaConfig Config => config;

        private void Awake()
        {
            current = Max;
        }

        private void Update()
        {
            if (config == null) return;
            if (Time.time >= regenResumeTime && config.regenPerSecond > 0f && current < Max)
            {
                float old = current;
                current = Mathf.Min(Max, current + config.regenPerSecond * Time.deltaTime);
                if (!Mathf.Approximately(old, current))
                    OnStaminaChanged?.Invoke(current, Max);
            }
        }

        public bool TryConsume(float amount)
        {
            amount = Mathf.Max(0f, amount);
            if (amount <= 0f) return true;
            if (current < amount) return false;
            float old = current;
            current -= amount;
            regenResumeTime = Time.time + (config != null ? config.regenDelaySeconds : 0f);
            if (!Mathf.Approximately(old, current))
                OnStaminaChanged?.Invoke(current, Max);
            return true;
        }

        public float ConsumeContinuous(float perSecond)
        {
            if (perSecond <= 0f) return 0f;
            float amount = perSecond * Time.deltaTime;
            float taken = Mathf.Min(amount, current);
            float old = current;
            current -= taken;
            regenResumeTime = Time.time + (config != null ? config.regenDelaySeconds : 0f);
            if (!Mathf.Approximately(old, current))
                OnStaminaChanged?.Invoke(current, Max);
            return taken;
        }

        public bool Has(float amount) => current >= amount;
    }
}
