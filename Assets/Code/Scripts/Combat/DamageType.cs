using UnityEngine;

namespace Characters.Combat
{
    [CreateAssetMenu(menuName = "Combat/Damage Type", fileName = "DamageType")]
    public sealed class DamageType : ScriptableObject
    {
        [Header("Visuals")]
        [Tooltip("Display color for UI/FX when showing this damage type.")]
        public Color color = Color.white;
        [Tooltip("Optional short code (e.g., PHY, FIRE, ICE) for UI overlays.")]
        public string shortCode = "";
    }
}
