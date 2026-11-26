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

        /// <summary>Named configs live here, one "<name>.cfg" each. (The live category below writes to a throw-away session file.)</summary>
        public static string ConfigDir { get { return Path.Combine(MelonEnvironment.UserDataDirectory, "BoneCheats", "Configs"); } }

        /// <summary>The single-file config of older versions; imported once as the config "Legacy".</summary>
        private static string LegacyPath { get { return Path.Combine(MelonEnvironment.UserDataDirectory, "BoneCheats.cfg"); } }

        public const int MaxNameLength = 24;

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
        public static MelonPreferences_Entry<bool> Esp, EspNames, EspHealth, Skeleton, BulletTracers;
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
            EspHealth = E("EspHealth", true, "ESP health bars");
            Skeleton = E("Skeleton", false, "ESP skeleton (stick figure)");
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

            ResetToDefaults();      // every launch starts clean; a saved config only loads when you press its button
            MigrateLegacy();
        }

        // ---------------- named configs ----------------

        private static readonly string[] ReservedNames =
            { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };

        /// <summary>Turns whatever was typed into a safe file/button name, or null if nothing usable is left.</summary>
        public static string CleanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var sb = new System.Text.StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char ch in raw.Trim())
            {
                if (Array.IndexOf(bad, ch) >= 0 || char.IsControl(ch)) continue;
                sb.Append(ch);
            }
            string name = sb.ToString().Trim().TrimEnd('.');
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength).TrimEnd();
            if (name.Length == 0) return null;
            if (Array.IndexOf(ReservedNames, name.ToUpperInvariant()) >= 0) name += "_";
            return name;
        }

        private static string PathFor(string cleanName) { return Path.Combine(ConfigDir, cleanName + ".cfg"); }

        public static bool Exists(string name)
        {
            string c = CleanName(name);
            return c != null && File.Exists(PathFor(c));
        }

        /// <summary>Names of every saved config, alphabetical.</summary>
        public static List<string> ListConfigs()
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(ConfigDir)) return list;
                foreach (string f in Directory.GetFiles(ConfigDir, "*.cfg")) list.Add(Path.GetFileNameWithoutExtension(f));
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't list configs: " + e.Message); }
            return list;
        }

        /// <summary>Writes every setting to ConfigDir/&lt;name&gt;.cfg. Returns the cleaned name actually used (null on failure).</summary>
        public static string SaveAs(string name)
        {
            string clean = CleanName(name);
            if (clean == null) return null;
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("[BoneCheats]");
                foreach (Item i in Items) sb.AppendLine(i.Id + " = " + i.Get());
                File.WriteAllText(PathFor(clean), sb.ToString());
                return clean;
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't save the config: " + e.Message); return null; }
        }

        /// <summary>Applies a saved config: everything goes back to defaults first, then the file's values are set on top,
        /// so a config always produces the same result no matter what was on before. False if it can't be read.</summary>
        public static bool LoadNamed(string name)
        {
            string clean = CleanName(name);
            if (clean == null) return false;
            try
            {
                string path = PathFor(clean);
                if (!File.Exists(path)) return false;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '[' || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"');
                }
                ResetToDefaults();
                foreach (Item i in Items)
                    if (map.TryGetValue(i.Id, out string v)) i.Set(v);
                return true;
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't load the config: " + e.Message); return false; }
        }

        /// <summary>Carries the old single BoneCheats.cfg over as a config called "Legacy" (once, never overwrites).</summary>
        private static void MigrateLegacy()
        {
            try
            {
                if (!File.Exists(LegacyPath)) return;
                Directory.CreateDirectory(ConfigDir);
                string target = PathFor("Legacy");
                if (!File.Exists(target)) File.Copy(LegacyPath, target);
            }
            catch (Exception e) { MelonLogger.Warning("[Settings] couldn't import the old config: " + e.Message); }
        }

        public static void ResetToDefaults()
        {
            foreach (Action r in Resets) r();
        }
    }
}
