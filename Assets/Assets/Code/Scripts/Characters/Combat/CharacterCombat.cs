using UnityEngine;
using Characters.Inputs;

namespace Characters.Combat
{
    public sealed class CharacterCombat : MonoBehaviour
    {
        public enum AttackPhase { Idle, Startup, Active, Recovery }

        [Header("References")]
        [SerializeField] private InputReader input;
        [SerializeField] private Animator animator; // Upper body layer reserved for later sword anims
        [SerializeField] private WeaponHitbox weaponHitbox; // Child trigger collider
        [SerializeField] private Characters.Stats.Stamina stamina; // optional
        [SerializeField] private CharacterHitReaction hitReaction; // optional

        [Header("Configs")]
        [SerializeField] private WeaponConfig weaponConfig;
        [SerializeField] private AttackProfile attackProfile;

        [Header("Animator Parameters")]
        [Tooltip("Animator int parameter representing attack phase: 0 Idle, 1 Startup, 2 Active, 3 Recovery.")]
        [SerializeField] private string attackStateParam = "AttackState";
        [Tooltip("Animator float parameter for normalized attack progress (0..1).")]
        [SerializeField] private string attackNormParam = "AttackNorm";

        private AttackPhase phase = AttackPhase.Idle;
        private float phaseTimer;
        private float totalDuration;
        private int hashAttackState;
        private int hashAttackNorm;
        [SerializeField] private bool useAnimatorEvents = true;

        // Event: attacker, target, damage
        public event System.Action<GameObject, GameObject, float> OnHitConfirmed;
        public AttackPhase CurrentPhase => phase;

        private void Awake()
        {
            hashAttackState = Animator.StringToHash(attackStateParam);
            hashAttackNorm = Animator.StringToHash(attackNormParam);
            if (weaponHitbox != null && attackProfile != null)
            {
                weaponHitbox.Initialize(attackProfile.hitMask, transform);
            }
        }

        private void Update()
        {
            // Start attack
            if (phase == AttackPhase.Idle && input != null && input.ConsumePrimary())
            {
                if (hitReaction != null && hitReaction.IsStunned) return; // blocked by stun
                // optional stamina gate
                float cost = weaponConfig ? Mathf.Max(0f, weaponConfig.staminaCost) : 0f;
                if (stamina == null || stamina.TryConsume(cost))
                {
                    BeginAttack();
                }
            }

            // Advance attack timeline (only if not using animator events)
            if (!useAnimatorEvents && phase != AttackPhase.Idle)
            {
                phaseTimer += Time.deltaTime;
                float norm = Mathf.Clamp01(phaseTimer / Mathf.Max(0.0001f, totalDuration));
                if (animator) animator.SetFloat(hashAttackNorm, norm);

                switch (phase)
                {
                    case AttackPhase.Startup:
                        if (phaseTimer >= attackProfile.startup)
                        {
                            EnterActive();
                        }
                        break;
                    case AttackPhase.Active:
                        if (phaseTimer >= attackProfile.startup + attackProfile.active)
                        {
                            EnterRecovery();
                        }
                        break;
                    case AttackPhase.Recovery:
                        if (phaseTimer >= totalDuration)
                        {
                            EndAttack();
                        }
                        break;
                }
            }
        }

        private void BeginAttack()
        {
            phase = AttackPhase.Startup;
            phaseTimer = 0f;
            totalDuration = attackProfile.startup + attackProfile.active + attackProfile.recovery;
            if (animator) animator.SetInteger(hashAttackState, (int)AttackPhase.Startup);
            if (animator) animator.SetBool("IsAttacking", true);
            PrepPendingHit();
        }

        // Allow equipment system to swap configs at runtime
        public void SetConfigs(WeaponConfig weapon, AttackProfile attack)
        {
            if (weapon != null) weaponConfig = weapon;
            if (attack != null)
            {
                attackProfile = attack;
                if (weaponHitbox != null)
                {
                    weaponHitbox.Initialize(attackProfile.hitMask, transform);
                }
            }
        }

        private void EnterActive()
        {
            phase = AttackPhase.Active;
            if (animator) animator.SetInteger(hashAttackState, (int)AttackPhase.Active);
            if (weaponHitbox) weaponHitbox.BeginSwing();
        }

        private void EnterRecovery()
        {
            phase = AttackPhase.Recovery;
            if (animator) animator.SetInteger(hashAttackState, (int)AttackPhase.Recovery);
            if (weaponHitbox) weaponHitbox.EndSwing();
        }

        private void EndAttack()
        {
            phase = AttackPhase.Idle;
            phaseTimer = 0f;
            if (animator) animator.SetInteger(hashAttackState, (int)AttackPhase.Idle);
            if (weaponHitbox) weaponHitbox.EndSwing();
            if (animator) animator.SetFloat(hashAttackNorm, 0f);
            if (animator) animator.SetBool("IsAttacking", false);
        }

        // Animation event hooks
        // Place events on the attack animation clips: HitboxOn at swing start, HitboxOff at swing end,
        // AttackEnd at the clip end to return to Idle.
        public void Anim_HitboxOn() { if (!useAnimatorEvents) return; EnterActive(); }
        public void Anim_HitboxOff() { if (!useAnimatorEvents) return; EnterRecovery(); }
        public void Anim_AttackEnd() { if (!useAnimatorEvents) return; EndAttack(); }

        private void PrepPendingHit()
        {
            if (!weaponHitbox || attackProfile == null) return;
            float dmg = attackProfile.damage * (weaponConfig ? weaponConfig.baseDamage : 1f);
            var attrs = GetComponent<Characters.Attributes.CharacterAttributes>();
            if (attrs != null) dmg *= Mathf.Max(0.1f, attrs.MeleeDamageScale);
            weaponHitbox.SetPendingDamage(dmg);
            weaponHitbox.SetPendingImpulse(Mathf.Max(0f, attackProfile.knockbackImpulse));
            if (attackProfile.damageType != null)
            {
                weaponHitbox.SetDamageType(attackProfile.damageType);
            }
            if (attackProfile.onHitEffects != null)
            {
                weaponHitbox.SetPendingStatuses(attackProfile.onHitEffects);
            }
            if (attackProfile.onHitEffects != null)
                weaponHitbox.SetPendingStatuses(attackProfile.onHitEffects);
        }

        // Called by WeaponHitbox when a hit is confirmed
        public void NotifyHitConfirmed(GameObject target, float damage)
        {
            OnHitConfirmed?.Invoke(gameObject, target, damage);
        }
    }
}
