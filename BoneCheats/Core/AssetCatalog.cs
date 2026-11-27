using System;
using System.Collections.Generic;
using MelonLoader;
using AssetWarehouse = Il2CppSLZ.Marrow.Warehouse.AssetWarehouse;
using SpawnableCrate = Il2CppSLZ.Marrow.Warehouse.SpawnableCrate;

namespace BoneCheats.Core
{
    /// <summary>One spawnable entry for the Item Spawner menu: a display name and the barcode that spawns it.</summary>
    internal readonly struct SpawnEntry
    {
        public readonly string Name;
        public readonly string Barcode;
        public SpawnEntry(string name, string barcode) { Name = name; Barcode = barcode; }
    }

    /// <summary>
    /// Reads every spawnable crate already registered in the game's own asset warehouse (SLZ.Marrow.Warehouse) -
    /// the same place the base game's spawn gun, BoneLib's spawn menus, and mods like SpawnLab pull their lists
    /// from - and splits them into Guns / NPCs for the Item Spawner tab. This reads whatever is actually loaded:
    /// base-game content AND every installed mod pallet that registers spawnables with the warehouse (that's
    /// what "registered in the warehouse" means - there's no base-game-only filter anywhere in this file).
    ///
    /// REWRITE: the previous version used reflection to guess at the warehouse's shape, and guessed wrong -
    /// it looked for the crate list in a property/field (it's actually only reachable through the method
    /// GetCrates&lt;T&gt;) and classified Gun vs NPC through a "Category" member (it's actually a "Tags" list).
    /// Both of those are now confirmed against two other published, decompiled BONELAB mods' real source
    /// ("Port a Warehouse" calls AssetWarehouse.Instance.GetCrates&lt;SpawnableCrate&gt;(null); "TheJanitor" tells
    /// crate kinds apart with spawnableCrate.Tags.Contains("NPC") / "Gun" / "Melee" / "Prop" / "Decal").
    /// So this is now written as normal typed C# against Il2CppSLZ.Marrow.Warehouse (already referenced by the
    /// project's Il2Cpp*.dll wildcard reference) instead of reflection: if a name here is still wrong for your
    /// game/SDK version, the build will fail with a compiler error naming the exact member - send that error
    /// back and it's a one-line fix, instead of a silent empty list with no way to tell what went wrong.
    /// </summary>
    internal static class AssetCatalog
    {
        public static readonly List<SpawnEntry> Guns = new List<SpawnEntry>();
        public static readonly List<SpawnEntry> Npcs = new List<SpawnEntry>();

        private static bool _everBuilt;

        /// <summary>Builds the catalog once. Safe to call repeatedly - a no-op after the first successful or failed attempt.</summary>
        public static void EnsureBuilt()
        {
            if (_everBuilt) return;
            RebuildNow();
        }

        /// <summary>Throws the lists away and reads the warehouse again (the menu's Refresh List button).</summary>
        public static void ForceRebuild() { RebuildNow(); }

        private static float _nextPoll;

        /// <summary>
        /// Called every frame from OnUpdate. The catalog is first built while the menu is created, which happens
        /// BEFORE the game's AssetWarehouse exists (the log showed "AssetWarehouse.Instance was null" and 0 guns /
        /// 0 NPCs), and nothing ever asked again. This quietly retries every 2 s until the warehouse is there and
        /// returns true once, on the call that finally filled the lists, so the menu can redraw.
        /// </summary>
        public static bool PollRefresh()
        {
            if (Guns.Count > 0 || Npcs.Count > 0) return false;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextPoll) return false;
            _nextPoll = now + 2f;

            try { if (AssetWarehouse.Instance == null) return false; }
            catch { return false; }

            RebuildNow();
            return Guns.Count > 0 || Npcs.Count > 0;
        }

        /// <summary>Re-reads the warehouse only if both lists are still empty (e.g. the first attempt ran before the game had loaded any crates in).</summary>
        public static void RebuildIfEmpty()
        {
            if (Guns.Count > 0 || Npcs.Count > 0) return;
            RebuildNow();
        }

        private static void RebuildNow()
        {
            _everBuilt = true;
            Guns.Clear();
            Npcs.Clear();

            try { Build(); }
            catch (Exception e) { MelonLogger.Error("[ItemSpawner] couldn't read the asset warehouse: " + e); }

            Guns.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Npcs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            MelonLogger.Msg("[ItemSpawner] catalog built: " + Guns.Count + " gun(s), " + Npcs.Count + " NPC(s).");
            if (Guns.Count == 0) MelonLogger.Warning("[ItemSpawner] found no guns in the warehouse (yet). Try again from in-game, after a level has finished loading.");
            if (Npcs.Count == 0) MelonLogger.Warning("[ItemSpawner] found no NPCs in the warehouse (yet). Try again from in-game, after a level has finished loading.");
        }

        private static readonly string[] NpcWords = { "npc", "enemy", "nullbody", "crablet", "zombie", "omni turret", "security guard", "agent", "peasant", "cultist", "fordcyber", "ford " };
        private static readonly string[] GunWords = { "rifle", "pistol", "gun", "shotgun", "smg", "revolver", "launcher", "sword", "knife", "blade", "axe", "crowbar", "dagger", "spear", "hammer", "katana", "mace" };

        private static bool ContainsAny(string hay, params string[] words)
        {
            foreach (string w in words) if (hay.Contains(w)) return true;
            return false;
        }

        private static bool HasTag(SpawnableCrate crate, string tag)
        {
            var tags = crate.Tags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++)
            {
                string t = tags[i];
                if (t != null && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void Build()
        {
            if (AssetWarehouse.Instance == null) { MelonLogger.Warning("[ItemSpawner] AssetWarehouse.Instance was null - the warehouse hasn't initialized yet."); return; }

            // null filter = every registered SpawnableCrate: base game AND every installed mod pallet, exactly
            // like the base-game spawn gun and BoneLib's own spawn menus see them.
            var crates = AssetWarehouse.Instance.GetCrates<SpawnableCrate>(null);
            if (crates == null) { MelonLogger.Warning("[ItemSpawner] AssetWarehouse.GetCrates<SpawnableCrate>(null) returned null."); return; }

            int seen = 0, skippedNoTitle = 0, skippedNoBarcode = 0, skippedNoTag = 0;

            foreach (SpawnableCrate crate in crates)
            {
                if (crate == null) continue;
                seen++;

                string title = crate.Title;
                if (string.IsNullOrEmpty(title)) { skippedNoTitle++; continue; }

                string barcode = crate.Barcode.ID;
                if (string.IsNullOrEmpty(barcode)) { skippedNoBarcode++; continue; }

                // tags first (case-insensitive), then keywords in the title / barcode like SpawnLab does, because plenty
                // of mod crates carry no (or odd) tags.
                bool tagNpc = HasTag(crate, "NPC");
                bool tagGun = HasTag(crate, "Gun") || HasTag(crate, "Melee");
                string hay = (title + " " + barcode).ToLowerInvariant();
                bool isAmmo = ContainsAny(hay, "magazine", "ammo", "cartridge");

                bool isNpc = tagNpc || (!tagGun && !isAmmo && ContainsAny(hay, NpcWords));
                bool isGun = !isNpc && !isAmmo && (tagGun || ContainsAny(hay, GunWords));

                if (isNpc) Npcs.Add(new SpawnEntry(title, barcode));
                else if (isGun) Guns.Add(new SpawnEntry(title, barcode));
                else skippedNoTag++;   // prop / decal / ammo / something else - not what either tab shows
            }

            MelonLogger.Msg("[ItemSpawner] warehouse scan: " + seen + " spawnable crate(s) seen, "
                + skippedNoTitle + " skipped (no title), " + skippedNoBarcode + " skipped (no barcode), "
                + skippedNoTag + " skipped (tagged neither Gun/Melee nor NPC).");
        }
    }
}
