using UnityEngine;

namespace Characters.Attributes
{
    [CreateAssetMenu(menuName = "Config/Attributes Config", fileName = "AttributesConfig")]
    public sealed class AttributesConfig : ScriptableObject
    {
        [Header("Derived Scaling (per point)")]
        [Tooltip("Max Health scale per STR: MaxHP = base * (1 + STR * this)")]
        public float hpPerSTR = 0.05f;
        [Tooltip("Max Stamina scale per DEX: MaxSTA = base * (1 + DEX * this)")]
        public float staminaPerDEX = 0.04f;
        [Tooltip("Movement acceleration multiplier per DEX: Accel = base * (1 + DEX * this)")]
        public float accelPerDEX = 0.02f;
        [Tooltip("Damage scale per STR for melee: Damage = base * (1 + STR * this)")]
        public float damagePerSTR = 0.05f;
    }
}

