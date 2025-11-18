using System.Collections.Generic;
using UnityEngine;

namespace Characters.Combat
{
    [RequireComponent(typeof(Collider))]
    public sealed class WeaponHitbox : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Layer mask for valid hit targets.")]
        [SerializeField] private LayerMask hitMask = ~0;
        [Tooltip("Owner root used to avoid self-hits.")]
        [SerializeField] private Transform owner;
        [Tooltip("Damage type applied by this hitbox/swing.")]
        [SerializeField] private DamageType damageType;

        private readonly HashSet<Collider> alreadyHit = new();
        private Collider col;
        private bool active;

        public void Initialize(LayerMask mask, Transform ownerRoot)
        {
            hitMask = mask;
            owner = ownerRoot;
        }

        private void Awake()
        {
            col = GetComponent<Collider>();
            col.isTrigger = true;
            col.enabled = false; // controlled by CharacterCombat
        }

        public void BeginSwing()
        {
            alreadyHit.Clear();
            active = true;
            if (col != null) col.enabled = true;
        }

        public void EndSwing()
        {
            active = false;
            if (col != null) col.enabled = false;
            alreadyHit.Clear();
        }

        private void OnDrawGizmosSelected()
        {
            var c = GetComponent<Collider>();
            if (!c) return;
            Gizmos.color = active ? new Color(1f, 0.2f, 0.2f, 0.5f) : new Color(1f, 0.2f, 0.2f, 0.2f);
            var b = c.bounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!active) return;
            if (owner != null && (other.transform == owner || other.transform.IsChildOf(owner))) return;
            if (((1 << other.gameObject.layer) & hitMask) == 0) return;

            // Require a LivingHitBox component to qualify (formalizes target surfaces)
            var hurtbox = other.GetComponentInParent<LivingHitBox>();
            if (hurtbox == null) return;
            if (!alreadyHit.Add(other)) return;

            var health = hurtbox.Health;
            var damageable = health != null ? (IDamageable)health : other.GetComponentInParent<IDamageable>();
            if (damageable != null)
            {
                // Hit data: approximate with collider center
                Vector3 point = other.bounds.center;
                Vector3 normal = (point - transform.position).normalized;
                float finalDamage = pendingDamage * hurtbox.DamageMultiplier;
                if (health != null)
                {
                    // Use extended overload to pass damage type
                    health.ApplyDamage(finalDamage, point, normal, owner ? owner.gameObject : gameObject, ignoreIFrames: false, damageType: damageType);
                }
                else
                {
                    damageable.ApplyDamage(finalDamage, point, normal, owner ? owner.gameObject : gameObject);
                }
                // Optional: apply simple knockback if rigidbody present
                if (pendingImpulse > 0f)
                {
                    var rb = other.attachedRigidbody ?? other.GetComponentInParent<Rigidbody>();
                    if (rb != null && !rb.isKinematic)
                    {
                        Vector3 dir = normal;
                        dir.y = 0f; dir.Normalize();
                        rb.AddForce(dir * pendingImpulse, ForceMode.VelocityChange);
                    }
                }

                // Apply status effects via StatusController if present (provided by CharacterCombat)
                if (pendingStatuses != null && pendingStatuses.Length > 0)
                {
                    var statusCtrl = hurtbox.GetComponentInParent<Characters.Status.StatusController>();
                    if (statusCtrl != null)
                    {
                        foreach (var ps in pendingStatuses)
                        {
                            if (ps.effect == null) continue;
                            if (Random.value <= Mathf.Clamp01(ps.chance))
                                statusCtrl.Apply(ps.effect, Mathf.Max(1, ps.stacks));
                        }
                    }
                }

                // Fire hit confirmed event on owner's CharacterCombat if present
                if (owner != null)
                {
                    var combat = owner.GetComponentInParent<CharacterCombat>();
                    if (combat != null)
                    {
                        combat.NotifyHitConfirmed(other.gameObject, finalDamage);
                    }
                }
            }
        }

        // Set by CharacterCombat for each swing
        private float pendingDamage;
        public void SetPendingDamage(float amount) => pendingDamage = amount;
        private float pendingImpulse;
        public void SetPendingImpulse(float impulse) => pendingImpulse = impulse;

        // Optional bleeding parameters for this swing
        public void SetDamageType(DamageType type) => damageType = type;

        // New status effects from AttackProfile
        private OnHitStatus[] pendingStatuses;
        public void SetPendingStatuses(OnHitStatus[] statuses) => pendingStatuses = statuses;
    }
}
