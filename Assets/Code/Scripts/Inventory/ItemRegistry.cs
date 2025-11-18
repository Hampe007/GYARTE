using System.Collections.Generic;
using UnityEngine;

namespace InventorySystem
{
    [CreateAssetMenu(menuName = "Inventory/Item Registry", fileName = "ItemRegistry")]
    public sealed class ItemRegistry : ScriptableObject
    {
        [Tooltip("All item definitions known to the game. Order determines saved indices.")]
        public List<ItemDefinition> items = new();

        public int GetIndex(ItemDefinition item)
        {
            if (item == null) return -1;
            return items != null ? items.IndexOf(item) : -1;
        }

        public ItemDefinition GetByIndex(int index)
        {
            if (items == null || index < 0 || index >= items.Count) return null;
            return items[index];
        }

        public static ItemRegistry LoadDefault()
        {
            // Tries Resources/ItemRegistry by default
            return Resources.Load<ItemRegistry>("ItemRegistry");
        }
    }
}

