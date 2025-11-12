using UnityEngine;
using System;

namespace Characters.Combat
{
    [Serializable]
    public sealed class OnHitStatus
    {
        public Characters.Status.StatusEffect effect;
        [Range(0f,1f)] public float chance = 1f;
        [Min(1)] public int stacks = 1;
    }
    [CreateAssetMenu(menuName = "Config/Combat/Attack Profile", fileName = "AttackProfile")]
    public sealed class AttackProfile : ScriptableObject
    {
        [Header("Timings (seconds)")]
        [Tooltip("Time before the hitbox becomes active.")]
        public float startup = 0.12f;
        [Tooltip("Time while the hitbox is active and can hit.")]
        public float active = 0.18f;
        [Tooltip("Recovery time after the active window.")]
        public float recovery = 0.30f;

        [Header("Damage")]
        [Tooltip("Base damage dealt by this attack before scaling.")]
        public float damage = 25f;
        [Tooltip("Impulse applied to targets on hit (knockback).")]
        public float knockbackImpulse = 4f;
        [Tooltip("Damage type for this attack.")]
        public DamageType damageType;

        [Header("On-Hit Status Effects")]
        [Tooltip("Status effects applied to targets on hit.")]
        public OnHitStatus[] onHitEffects;

        [Header("Hitbox Shape (local)")]
        [Tooltip("Center of the hitbox in local space.")]
        public Vector3 localCenter = new Vector3(0.0f, 1.0f, 0.8f);
        [Tooltip("Size of the hitbox (BoxCollider) in meters.")]
        public Vector3 size = new Vector3(0.3f, 0.8f, 1.2f);

        [Header("Filters")]
        [Tooltip("Layer mask for valid hit targets.")]
        public LayerMask hitMask = ~0;
    }
}

