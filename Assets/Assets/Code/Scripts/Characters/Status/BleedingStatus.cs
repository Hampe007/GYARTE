using System.Collections.Generic;
using UnityEngine;

namespace Characters.Status
{
    // Minimal bleeding status manager; applies DoT to Health and supports stacking.
    [RequireComponent(typeof(Characters.Health.Health))]
    public sealed class BleedingStatus : MonoBehaviour
    {
        private sealed class Stack
        {
            public float remaining;
            public float dps;
        }

        [Header("Runtime (read-only)")]
        [SerializeField] private int activeStacks;
        [SerializeField] private float totalDps;

        private readonly List<Stack> stacks = new();
        private Characters.Health.Health health;

        private void Awake()
        {
            health = GetComponent<Characters.Health.Health>();
        }

        private void Update()
        {
            if (stacks.Count == 0) return;

            float dt = Time.deltaTime;
            totalDps = 0f;
            // Tick and apply damage bypassing i-frames
            for (int i = stacks.Count - 1; i >= 0; i--)
            {
                var s = stacks[i];
                s.remaining -= dt;
                totalDps += s.dps;
                float dmg = Mathf.Max(0f, s.dps) * dt;
                health.ApplyDamage(dmg, transform.position, Vector3.up, gameObject, ignoreIFrames: true);
                if (s.remaining <= 0f)
                    stacks.RemoveAt(i);
            }

            activeStacks = stacks.Count;
        }

        public void ApplyBleed(Characters.Config.BleedingConfig cfg)
        {
            if (cfg == null) return;

            // If non-stackable, refresh or replace single stack
            if (!cfg.stackable)
            {
                if (stacks.Count == 0)
                {
                    stacks.Add(new Stack { remaining = Mathf.Max(0f, cfg.durationSeconds), dps = Mathf.Max(0f, cfg.damagePerSecond) });
                }
                else
                {
                    stacks[0].remaining = Mathf.Max(stacks[0].remaining, cfg.durationSeconds);
                    stacks[0].dps = Mathf.Max(stacks[0].dps, cfg.damagePerSecond);
                }
            }
            else
            {
                // Stack up to max
                if (cfg.maxStacks <= 0 || stacks.Count < cfg.maxStacks)
                {
                    stacks.Add(new Stack { remaining = Mathf.Max(0f, cfg.durationSeconds), dps = Mathf.Max(0f, cfg.damagePerSecond) });
                }
                else
                {
                    // If at cap, refresh the oldest (lowest remaining)
                    int oldest = 0; float minRemain = stacks[0].remaining;
                    for (int i = 1; i < stacks.Count; i++)
                    {
                        if (stacks[i].remaining < minRemain) { minRemain = stacks[i].remaining; oldest = i; }
                    }
                    stacks[oldest].remaining = cfg.durationSeconds;
                    stacks[oldest].dps = cfg.damagePerSecond;
                }
            }

            activeStacks = stacks.Count;
        }
    }
}

