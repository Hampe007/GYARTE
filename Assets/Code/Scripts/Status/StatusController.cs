using System;
using System.Collections.Generic;
using UnityEngine;

namespace Characters.Status
{
    [AddComponentMenu("Status/Status Controller")]
    public sealed class StatusController : MonoBehaviour
    {
        private sealed class RuntimeEntry
        {
            public StatusEffect effect;
            public float remaining;
            public float tickTimer;
            public int stacks;
        }

        [Header("Runtime (read-only)")]
        [Tooltip("Names of currently active effects; for debugging/inspector only.")]
        [SerializeField] private List<string> activeEffects = new();

        private readonly List<RuntimeEntry> effects = new();
        [Tooltip("Raised when an effect is applied to this character.")] public event Action<StatusEffect> OnApplied;
        [Tooltip("Raised when an effect is removed from this character.")] public event Action<StatusEffect> OnRemoved;

        public void Apply(StatusEffect effect, int stacks = 1)
        {
            if (effect == null || stacks <= 0) return;
            var entry = effects.Find(e => e.effect == effect);
            if (entry == null)
            {
                entry = new RuntimeEntry { effect = effect, remaining = Mathf.Max(0f, effect.durationSeconds), stacks = Mathf.Max(1, stacks) };
                effects.Add(entry);
                effect.OnApplied(this, entry.stacks);
                OnApplied?.Invoke(effect);
            }
            else
            {
                // Refresh duration and stacks
                entry.remaining = Mathf.Max(entry.remaining, effect.durationSeconds);
                if (effect.maxStacks <= 1) entry.stacks = 1; else entry.stacks = Mathf.Min(effect.maxStacks, entry.stacks + stacks);
            }
            RefreshDebugList();
        }

        public void Remove(StatusEffect effect)
        {
            var entry = effects.Find(e => e.effect == effect);
            if (entry == null) return;
            effects.Remove(entry);
            entry.effect.OnRemoved(this);
            OnRemoved?.Invoke(entry.effect);
            RefreshDebugList();
        }

        private void Update()
        {
            if (effects.Count == 0) return;
            float dt = Time.deltaTime;
            for (int i = effects.Count - 1; i >= 0; i--)
            {
                var e = effects[i];
                if (e.effect.tickRate > 0f)
                {
                    e.tickTimer += dt;
                    float interval = 1f / e.effect.tickRate;
                    while (e.tickTimer >= interval)
                    {
                        e.effect.OnTick(this, interval);
                        e.tickTimer -= interval;
                    }
                }
                e.remaining -= dt;
                if (e.remaining <= 0f)
                {
                    Remove(e.effect);
                }
            }
        }

        private void RefreshDebugList()
        {
            activeEffects.Clear();
            foreach (var e in effects) activeEffects.Add(e.effect != null ? e.effect.name : "<null>");
        }

        public T GetComponentOnTarget<T>() where T : Component => GetComponent<T>();
    }
}
