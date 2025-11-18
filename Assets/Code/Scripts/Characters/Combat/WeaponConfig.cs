using UnityEngine;

namespace Characters.Combat
{
    [CreateAssetMenu(menuName = "Config/Combat/Weapon Config", fileName = "WeaponConfig")]
    public sealed class WeaponConfig : ScriptableObject
    {
        [Header("Weapon Stats")]
        [Tooltip("Base damage scalar applied to attacks done with this weapon.")]
        public float baseDamage = 1.0f;
        [Tooltip("Stamina cost per primary attack.")]
        public float staminaCost = 10f; // Hook to stamina later
        [Tooltip("Monetary value (for inventory/economy later).")]
        public int value = 100;
    }
}

