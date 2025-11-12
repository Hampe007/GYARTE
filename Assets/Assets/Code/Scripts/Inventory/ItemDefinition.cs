using UnityEngine;

namespace InventorySystem
{
    [CreateAssetMenu(menuName = "Inventory/Item", fileName = "Item")]
    public sealed class ItemDefinition : ScriptableObject
    {
        [Header("Basics")]
        [Tooltip("Display name.")]
        public string displayName;
        [Tooltip("Long-form description shown in tooltips or details.")]
        [TextArea] public string description;
        [Tooltip("Inventory icon.")]
        public Sprite icon;

        [Header("Stacking & Weight")]
        [Tooltip("Whether multiple items stack in one slot/list entry.")]
        public bool stackable = true;
        [Tooltip("Maximum items per stack when stackable.")]
        public int maxStack = 99;
        [Tooltip("Item weight for encumbrance.")]
        public float weight = 0f;

        [Header("Equipment (optional)")]
        [Tooltip("If true, this item can be equipped in a slot.")]
        public bool equippable = false;
        [Tooltip("Slot name this item fits into (e.g., Weapon, Armor, Trinket). Leave empty for generic.")]
        public string equipmentSlot = ""; // e.g., Weapon, Armor, Trinket
    }
}
