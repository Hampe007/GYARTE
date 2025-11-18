using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InventorySystem
{
    [Serializable]
    public sealed class InventorySaveData
    {
        [Tooltip("Indices into the ItemRegistry for each stack in the inventory.")]
        public List<int> itemIndices = new();
        [Tooltip("Counts for each stack in the inventory (parallel to itemIndices).")]
        public List<int> counts = new();
        [Tooltip("Equipment slot names (Weapon/Armor/Trinket, etc.).")]
        public List<string> slots = new();
        [Tooltip("Indices into the ItemRegistry for each equipped slot (parallel to slots). -1 if empty.")]
        public List<int> equippedIndices = new();
    }

    public static class InventoryPersistence
    {
        public static string SavePath(string filename = "inventory.json")
            => Path.Combine(Application.persistentDataPath, filename);

        public static void Save(Inventory inv, EquipmentSlots eq, string filename = "inventory.json", ItemRegistry registry = null)
        {
            registry ??= ItemRegistry.LoadDefault();
            var data = new InventorySaveData();
            if (inv != null)
            {
                foreach (var s in inv.items)
                {
                    int idx = registry != null ? registry.GetIndex(s.item) : -1;
                    data.itemIndices.Add(idx);
                    data.counts.Add(Mathf.Max(0, s.count));
                }
            }
            if (eq != null)
            {
                foreach (var s in eq.slots)
                {
                    data.slots.Add(s.slotName);
                    int idx = registry != null ? registry.GetIndex(s.equipped) : -1;
                    data.equippedIndices.Add(idx);
                }
            }
            var json = JsonUtility.ToJson(data, prettyPrint: true);
            File.WriteAllText(SavePath(filename), json);
        }

        public static void Load(Inventory inv, EquipmentSlots eq, string filename = "inventory.json", ItemRegistry registry = null)
        {
            registry ??= ItemRegistry.LoadDefault();
            var path = SavePath(filename);
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<InventorySaveData>(json);
            if (inv != null)
            {
                inv.items.Clear();
                for (int i = 0; i < data.itemIndices.Count; i++)
                {
                    var def = registry != null ? registry.GetByIndex(data.itemIndices[i]) : null;
                    inv.items.Add(new ItemStack { item = def, count = i < data.counts.Count ? data.counts[i] : 1 });
                }
                inv.NotifyChanged();
            }
            if (eq != null)
            {
                eq.slots.Clear();
                for (int i = 0; i < data.slots.Count; i++)
                {
                    int idx = (i < data.equippedIndices.Count) ? data.equippedIndices[i] : -1;
                    var def = registry != null ? registry.GetByIndex(idx) : null;
                    eq.slots.Add(new EquipmentSlot { slotName = data.slots[i], equipped = def });
                }
                eq.NotifyChanged();
            }
        }
    }
}
