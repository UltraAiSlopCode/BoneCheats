using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

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
    /// the same place the base game's spawn gun and BoneLib's spawn menus pull their lists from - and splits
    /// them into Guns / NPCs for the Item Spawner tab. Nothing here is a hardcoded or placeholder asset; every
    /// entry is a crate the game itself reports as installed.
    ///
    /// Everything below is reached through reflection rather than a typed reference, on purpose: the exact
    /// shape of the warehouse can differ between game/SDK versions, and this file has no compiler or game
    /// install available to confirm it against while being written. A renamed or missing member here logs a
    /// warning and leaves a list empty instead of taking the whole mod down - the same trade-off Game.Notify
    /// already makes for BoneLib's notification API.
    ///
    /// BUGFIX (was always reporting "none installed"): the first version of this file only ever looked for the
    /// crate list sitting in a *property or field* on AssetWarehouse.Instance, and only ever classified Gun vs
    /// NPC through a "Category" member. Neither exists on the real warehouse - the crate list is only reachable
    /// through the generic method AssetWarehouse.Instance.GetCrates&lt;SpawnableCrate&gt;(null) (confirmed against
    /// other published BONELAB mods' decompiled source, e.g. "Port a Warehouse"), and a crate's Gun/NPC/Prop/
    /// Decal/Melee classification lives in a "Tags" list you check with Tags.Contains("NPC") etc. (confirmed
    /// against "TheJanitor"'s decompiled source, which does exactly that to tell crate kinds apart), not a
    /// Category field at all. TryGetCratesViaMethod + HasTag below are the fix; the old property/field scan and
    /// Category check are kept underneath as a fallback for older/newer SDK builds that might still have them.
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
            catch (Exception e) { R.WarnOnce("catalog-build", "[ItemSpawner] couldn't read the asset warehouse: " + e.Message); }

            Guns.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Npcs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            if (Guns.Count == 0) R.WarnOnce("catalog-guns-empty", "[ItemSpawner] found no guns in the warehouse (yet). Try again from in-game, after a level has finished loading.");
            if (Npcs.Count == 0) R.WarnOnce("catalog-npcs-empty", "[ItemSpawner] found no NPCs in the warehouse (yet). Try again from in-game, after a level has finished loading.");
        }

        private static void Build()
        {
            Type tWarehouse = FindType("Il2CppSLZ.Marrow.Warehouse.AssetWarehouse");
            if (tWarehouse == null) { MelonLogger.Warning("[ItemSpawner] AssetWarehouse type not found - is the game/SDK version this was built against still current?"); return; }

            object warehouse = GetMember(tWarehouse, null, "Instance");
            if (warehouse == null) { MelonLogger.Warning("[ItemSpawner] AssetWarehouse.Instance was null."); return; }

            // Preferred path: the real API is a generic method, GetCrates<SpawnableCrate>(ICrateFilter<SpawnableCrate>),
            // called with a null filter to mean "everything". This already hands back only SpawnableCrate-typed
            // entries, so no further type-name filtering is needed on this path.
            IEnumerable crates = TryGetCratesViaMethod(tWarehouse, warehouse, "Il2CppSLZ.Marrow.Warehouse.SpawnableCrate");
            bool preFiltered = crates != null;

            // Fallback: older/newer SDK builds that don't expose that overload - scan properties/fields for
            // anything that looks like a list of crates (what this file originally, and only, tried).
            if (crates == null) crates = FindCrateList(warehouse);

            if (crates == null) { MelonLogger.Warning("[ItemSpawner] couldn't find a crate list on AssetWarehouse.Instance (checked GetCrates<T>(...) and every property/field)."); return; }

            bool anyTagOrCategory = false;
            int seen = 0;

            foreach (object crate in crates)
            {
                if (crate == null) continue;
                seen++;
                if (!preFiltered && crate.GetType().Name.IndexOf("Spawnable", StringComparison.OrdinalIgnoreCase) < 0) continue;

                string title = GetMember(null, crate, "Title") as string;
                if (string.IsNullOrEmpty(title)) title = GetMember(null, crate, "Name") as string;
                if (string.IsNullOrEmpty(title)) continue;

                string barcode = BarcodeToString(GetMember(null, crate, "Barcode"));
                if (string.IsNullOrEmpty(barcode)) continue;

                // 1) Preferred: the crate's own Tags list (List<string>-like; contains entries such as
                //    "NPC", "Gun", "Melee", "Prop", "Decal"). Skip anything that's neither a weapon nor an NPC
                //    (props, decals, ammo, ...) so those don't clutter either tab.
                bool haveTags;
                bool isNpc = HasTag(crate, "NPC", out haveTags);
                bool isGun = !isNpc && (HasTag(crate, "Gun", out _) || HasTag(crate, "Melee", out _));
                bool classified = isNpc || isGun;
                if (haveTags) anyTagOrCategory = true;

                // 2) Fallback: an SDK build with a "Category" member instead of Tags.
                if (!classified && !haveTags)
                {
                    object category = GetMember(null, crate, "Category");
                    if (category != null)
                    {
                        anyTagOrCategory = true;
                        string cat = category.ToString();
                        isNpc = cat.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0
                             || cat.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0
                             || cat.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0;
                        isGun = !isNpc && (cat.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0
                                        || cat.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0
                                        || cat.IndexOf("Item", StringComparison.OrdinalIgnoreCase) >= 0);
                        classified = isNpc || isGun;
                        if (!classified) continue; // some other category (props, ammo, ...) - not what either tab shows
                    }
                }

                // 3) Last resort: neither Tags nor Category tell us anything - guess from the crate/pallet name
                //    rather than dropping the item entirely.
                if (!classified)
                {
                    string palletTitle = PalletTitle(crate);
                    string typeName = crate.GetType().Name;
                    isNpc = typeName.IndexOf("Npc", StringComparison.OrdinalIgnoreCase) >= 0
                         || (palletTitle != null && palletTitle.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0);
                }

                if (isNpc) Npcs.Add(new SpawnEntry(title, barcode));
                else Guns.Add(new SpawnEntry(title, barcode));
            }

            if (!anyTagOrCategory && seen > 0)
                R.WarnOnce("catalog-no-tags", "[ItemSpawner] crates have neither a Tags list nor a Category member in this SDK build - Guns/NPCs were split by crate/pallet name instead, which is less reliable. Check AssetCatalog.Build() if a list looks wrong.");
        }

        /// <summary>
        /// Calls AssetWarehouse.Instance.GetCrates&lt;T&gt;(null) by reflection, where T is the type named by
        /// elementTypeName. Finds a public instance method called "GetCrates" that is an open generic method
        /// with exactly one generic parameter and exactly one (reference-type) parameter - matching the real
        /// GetCrates&lt;T&gt;(ICrateFilter&lt;T&gt; filter) signature without needing to name ICrateFilter&lt;T&gt;
        /// itself, since passing null for the filter means "no filtering".
        /// </summary>
        private static IEnumerable TryGetCratesViaMethod(Type tWarehouse, object warehouse, string elementTypeName)
        {
            try
            {
                Type tElement = FindType(elementTypeName);
                if (tElement == null) return null;

                foreach (MethodInfo m in tWarehouse.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "GetCrates" || !m.IsGenericMethodDefinition) continue;
                    if (m.GetGenericArguments().Length != 1) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 1 || ps[0].ParameterType.IsValueType) continue;

                    MethodInfo closed = m.MakeGenericMethod(tElement);
                    object result = closed.Invoke(warehouse, new object[] { null });
                    if (result is IEnumerable e && !(result is string)) return e;
                }
            }
            catch (Exception e)
            {
                R.WarnOnce("catalog-getcrates-method", "[ItemSpawner] AssetWarehouse.GetCrates<T>(...) lookup failed, falling back to a plain property/field scan: " + e.Message);
            }
            return null;
        }

        /// <summary>True if the crate's Tags collection (however it's typed - List&lt;string&gt;, an Il2Cpp
        /// list, an array, ...) contains the given tag, case-insensitively. haveTags reports whether a Tags
        /// member was found at all, so Build() can tell "no NPC tag" apart from "no Tags member on this crate".</summary>
        private static bool HasTag(object crate, string tag, out bool haveTags)
        {
            haveTags = false;
            object tags = GetMember(null, crate, "Tags");
            if (tags == null || !(tags is IEnumerable e) || tags is string) return false;
            haveTags = true;
            foreach (object item in e)
            {
                if (item != null && string.Equals(item.ToString(), tag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string PalletTitle(object crate)
        {
            object pallet = GetMember(null, crate, "Pallet");
            return pallet != null ? GetMember(null, pallet, "Title") as string : null;
        }

        /// <summary>Finds the first public instance member on the warehouse whose value is a list/array of crate-like objects.</summary>
        private static IEnumerable FindCrateList(object warehouse)
        {
            const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance;
            foreach (PropertyInfo p in warehouse.GetType().GetProperties(bf))
            {
                IEnumerable e = TryAsCrateList(p.GetValue(warehouse));
                if (e != null) return e;
            }
            foreach (FieldInfo f in warehouse.GetType().GetFields(bf))
            {
                IEnumerable e = TryAsCrateList(f.GetValue(warehouse));
                if (e != null) return e;
            }
            // Last resort: a zero-argument method (e.g. a non-generic GetCrates()) that returns every crate.
            foreach (MethodInfo m in warehouse.GetType().GetMethods(bf))
            {
                if (m.GetParameters().Length != 0 || m.ReturnType == typeof(void) || m.IsGenericMethodDefinition) continue;
                object value;
                try { value = m.Invoke(warehouse, null); } catch { continue; }
                IEnumerable e = TryAsCrateList(value);
                if (e != null) return e;
            }
            return null;
        }

        private static IEnumerable TryAsCrateList(object value)
        {
            if (!(value is IEnumerable e) || value is string) return null;
            foreach (object item in e)
            {
                if (item == null) continue;
                // A dictionary of crates enumerates KeyValuePairs - unwrap to the value if so.
                object candidate = item;
                Type it = item.GetType();
                if (it.IsGenericType && it.Name.StartsWith("KeyValuePair"))
                {
                    PropertyInfo valueProp = it.GetProperty("Value");
                    candidate = valueProp != null ? valueProp.GetValue(item) : item;
                }
                return candidate != null && candidate.GetType().Name.IndexOf("Crate", StringComparison.OrdinalIgnoreCase) >= 0
                    ? Rewrap(e)
                    : null;
            }
            return null;
        }

        // Re-enumerates so Build() sees the same sequence TryAsCrateList just peeked at (IEnumerable
        // without a reset-safe iterator would otherwise have its first item already consumed).
        private static IEnumerable Rewrap(IEnumerable e)
        {
            var list = new List<object>();
            foreach (object item in e)
            {
                Type it = item?.GetType();
                if (it != null && it.IsGenericType && it.Name.StartsWith("KeyValuePair"))
                {
                    PropertyInfo valueProp = it.GetProperty("Value");
                    list.Add(valueProp != null ? valueProp.GetValue(item) : item);
                }
                else list.Add(item);
            }
            return list;
        }

        private static string BarcodeToString(object barcode)
        {
            if (barcode == null) return null;
            if (barcode is string s) return s;
            object id = GetMember(null, barcode, "ID") ?? GetMember(null, barcode, "Id") ?? GetMember(null, barcode, "Value");
            return (id as string) ?? barcode.ToString();
        }

        private static object GetMember(Type staticType, object instance, string name)
        {
            try
            {
                Type t = staticType ?? instance.GetType();
                BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | (staticType != null ? BindingFlags.Static : BindingFlags.Instance);
                PropertyInfo p = t.GetProperty(name, flags);
                if (p != null) return p.GetValue(staticType != null ? null : instance);
                FieldInfo f = t.GetField(name, flags);
                if (f != null) return f.GetValue(staticType != null ? null : instance);
            }
            catch { }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
