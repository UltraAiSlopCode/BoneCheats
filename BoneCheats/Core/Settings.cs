using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace BoneCheats.Core
{
    /// <summary>All values the menu edits. Saved to UserData/BoneCheats.cfg.</summary>
    internal static class Settings
    {
        private static MelonPreferences_Category _c;
        private static readonly List<Action> Resets = new List<Action>();

        private sealed class Item { public string Id; public Func<string> Get; public Action<string> Set; }
        private static readonly List<Item> Items = new List<Item>();

        /// <summary>The file Save/Load Config use. (The live category below writes to a throw-away session file.)</summary>
        public static string ConfigPath { get { return Path.Combine(MelonEnvironment.UserDataDirectory, "BoneCheats.cfg"); } }

        private static MelonPreferences_Entry<T> E<T>(string id, T def, string label)
        {
            MelonPreferences_Entry<T> entry = _c.CreateEntry(id, def, label);
            Resets.Add(() => entry.Value = def);
            Items.Add(new Item
            {
                Id = id,
                Get = () => Convert.ToString(entry.Value, CultureInfo.InvariantCulture),
                Set = s => { try { entry.Value = (T)Convert.ChangeType(s, typeof(T), CultureInfo.InvariantCulture); } catch { } },
            });
            return entry;
        }

        // Colour indexes everywhere: 0 red 1 green 2 blue 3 yellow 4 magenta 5 cyan 6 white 7 orange 8 rainbow

        // ---- Visuals ----
        public static MelonPreferences_Entry<bool> Esp, EspNames, BulletTracers;
        public static MelonPreferences_Entry<int> EspCol, TargetCol, TracerCol;

        // ---- Charms ----
        public static MelonPreferences_Entry<bool> CharmSelf, CharmPlayers, CharmWeapons;
        public static MelonPreferences_Entry<int> CharmSelfCol, CharmPlayerCol, CharmWeaponCol;
        public static MelonPreferences_Entry<float> CharmAlpha;

        // ---- Combat ----
        public static MelonPreferences_Entry<int> AimMode;                  // 0 off, 1 silent aim, 2 bullet TP
        public static MelonPreferences_Entry<int> AimOrigin;                // 0 head, 1 hand / held gun
        public static MelonPreferences_Entry<float> AimFov;
        public static MelonPreferences_Entry<bool> NoRecoil, NoSpread, RapidFire, InfiniteAmmo;
        public static MelonPreferences_Entry<float> RapidFireMult, DamageMult;

        // ---- Movement ----
        public static MelonPreferences_Entry<bool> Fly, Noclip;
        public static MelonPreferences_Entry<float> FlySpeed, WalkSpeedMult;

        // ---- Player ----
        public static MelonPreferences_Entry<bool> GodMode, AntiRagdoll, AntiGrab, AntiGrabKill;

        // ---- Lobby ----

        public static void Init()
        {
            _c = MelonPreferences.CreateCategory("BoneCheats", "BoneCheats");
            _c.SetFilePath(Path.Combine(MelonEnvironment.UserDataDirectory, "BoneCheats.session.cfg"));   // not the saved config: that only changes when you press Save

            Esp = E("Esp", false, "ESP master switch");
            EspNames = E("EspNames", true, "ESP names");
            BulletTracers = E("BulletTracers", false, "Bullet tracers");
            EspCol = E("EspCol", 0, "ESP colour index");
            TargetCol = E("TargetCol", 3, "Target colour index (ESP + charms)");
            TracerCol = E("TracerCol", 5, "Tracer colour index");

            CharmSelf = E("CharmSelf", false, "Charms on yourself");
            CharmPlayers = E("CharmPlayers", false, "Charms on other players");
            CharmWeapons = E("CharmWeapons", false, "Charms on weapons");
            CharmSelfCol = E("CharmSelfCol", 5, "Self charm colour index");
            CharmPlayerCol = E("CharmPlayerCol", 1, "Player charm colour index");
            CharmWeaponCol = E("CharmWeaponCol", 3, "Weapon charm colour index");
            CharmAlpha = E("CharmAlpha", 0.6f, "Charm opacity");

            AimMode = E("AimMode", 0, "Aim mode 0 off / 1 silent / 2 bullet TP");
            AimOrigin = E("AimOrigin", 0, "Aim origin 0 head / 1 hand-gun");
            AimFov = E("AimFov", 45f, "Aim field of view (degrees around the aim origin)");
            NoRecoil = E("NoRecoil", false, "No recoil (cancels the kick on the gun after each shot)");
            NoSpread = E("NoSpread", false, "No spread (zeroes the cartridge spread angle)");
            RapidFire = E("RapidFire", false, "Rapid fire");
            InfiniteAmmo = E("InfiniteAmmo", false, "Infinite ammo");
            RapidFireMult = E("RapidFireMult", 5f, "Rapid fire multiplier");
            DamageMult = E("DamageMult", 1f, "Damage multiplier");

            Fly = E("Fly", false, "Fly");
            Noclip = E("Noclip", false, "Noclip");
            FlySpeed = E("FlySpeed", 8f, "Fly speed (m/s)");
            WalkSpeedMult = E("WalkSpeedMult", 1f, "Walk speed multiplier");

            GodMode = E("GodMode", false, "God mode");
            AntiRagdoll = E("AntiRagdoll", false, "Anti ragdoll");
            AntiGrab = E("AntiGrab", false, "Anti grab (other players can't grab you)");
            AntiGrabKill = E("AntiGrabKill", false, "...and kill whoever tried to grab you");

            ResetToDefaults();      // every launch starts clean; the saved config only loads when you press "Load Config"
        }

        /// <summary>Writes every setting to UserData/BoneCheats.cfg.</summary>
        public static bool Save()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[BoneCheats]");
                foreach (Item i in Items) sb.AppendLine(i.Id + " = " + i.Get());
                File.WriteAllText(ConfigPath, sb.ToString());
                return true;
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't save the config: " + e.Message); return false; }
        }

        /// <summary>Reads UserData/BoneCheats.cfg back into the settings. False if there is no saved config.</summary>
        public static bool Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return false;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(ConfigPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '[' || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"');
                }
                foreach (Item i in Items)
                    if (map.TryGetValue(i.Id, out string v)) i.Set(v);
                return true;
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't load the config: " + e.Message); return false; }
        }

        public static void ResetToDefaults()
        {
            foreach (Action r in Resets) r();
        }
    }
}
