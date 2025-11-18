using System;
using UnityEngine;

namespace Characters.Attributes
{
    [AddComponentMenu("Characters/Character Attributes")]
    public sealed class CharacterAttributes : MonoBehaviour
    {
        [Header("Base Attributes")]
        [Tooltip("Strength: scales Health and melee damage.")]
        [Min(0)] public int STR = 5;
        [Tooltip("Dexterity: scales Stamina and movement acceleration.")]
        [Min(0)] public int DEX = 5;
        [Tooltip("Intelligence: reserved for future systems (encumbrance/magic).")]
        [Min(0)] public int INT = 5;

        [Header("Config")] 
        [Tooltip("Scaling coefficients applied per attribute point.")]
        public AttributesConfig config;

        public event Action OnChanged;

        [Tooltip("Multiplier applied to base Max Health from STR.")]
        public float HealthScale => 1f + (config != null ? STR * config.hpPerSTR : 0f);
        [Tooltip("Multiplier applied to base Max Stamina from DEX.")]
        public float StaminaScale => 1f + (config != null ? DEX * config.staminaPerDEX : 0f);
        [Tooltip("Multiplier applied to movement acceleration from DEX.")]
        public float AccelScale => 1f + (config != null ? DEX * config.accelPerDEX : 0f);
        [Tooltip("Multiplier applied to melee damage from STR.")]
        public float MeleeDamageScale => 1f + (config != null ? STR * config.damagePerSTR : 0f);

        public void SetSTR(int value) { STR = Mathf.Max(0, value); OnChanged?.Invoke(); }
        public void SetDEX(int value) { DEX = Mathf.Max(0, value); OnChanged?.Invoke(); }
        public void SetINT(int value) { INT = Mathf.Max(0, value); OnChanged?.Invoke(); }
    }
}
