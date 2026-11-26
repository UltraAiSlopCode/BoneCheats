using System;
using System.Collections.Generic;
using System.Reflection;
using BoneCheats.Core;
using BoneCheats.Features;
using BoneLib.BoneMenu;
using MelonLoader;
using UnityEngine;

namespace BoneCheats.UI
{
    /// <summary>
    /// Builds BoneMenu > Cheats > Visuals | Charms | Combat | Movement | Player | Lobby.
    /// Written against the BoneLib 3.x menu API (Page.CreatePage / CreateBool / CreateFloat / CreateInt / CreateFunction).
    /// </summary>
    internal static class MenuBuilder
    {
        // After "Reset Everything" every element is pushed back to the (now default) setting value.
        private static readonly List<Action> Sync = new List<Action>();

        // A setting can have more than one switch (its own page + Settings > Quick Toggles); flipping one updates the others.
        private static readonly Dictionary<object, List<Action>> Mirrors = new Dictionary<object, List<Action>>();
        private static bool _syncing;

        private static Page _configsPage;          // Settings > Saved Configs: one button per saved config
        private static string _pendingName = "";   // what was typed into the Save Config keyboard

        public static void Build()
        {
            Page root = Page.Root.CreatePage("BoneCheats", Color.red);

            BuildVisuals(root);
            BuildCharms(root);
            BuildCombat(root);
            BuildMovement(root);
            BuildPlayer(root);
            BuildLobby(root);
            ItemSpawnerPage.Build(root);
            BuildSettings(root);

        }

        // ---------------- pages ----------------

        private static void BuildVisuals(Page root)
        {
            Page p = root.CreatePage("Visuals", Color.cyan);
            Toggle(p, "ESP (master)", Color.green, Settings.Esp);
            Toggle(p, "Names", Color.white, Settings.EspNames);
            Toggle(p, "Health Bars", Color.white, Settings.EspHealth);
            Toggle(p, "Skeleton", Color.white, Settings.Skeleton);
            Toggle(p, "Bullet Tracers", Color.cyan, Settings.BulletTracers);

            Page colors = p.CreatePage("Colors", Color.magenta);
            ColorSlider(colors, "ESP colour", Settings.EspCol);
            ColorSlider(colors, "Target colour", Settings.TargetCol);
            ColorSlider(colors, "Tracer colour", Settings.TracerCol);

        }

        private static void BuildCharms(Page root)
        {
            Page p = root.CreatePage("Charms", Color.magenta);
            Toggle(p, "Charm: Myself", Color.green, Settings.CharmSelf);
            Toggle(p, "Charm: Other Players", Color.green, Settings.CharmPlayers);
            Toggle(p, "Charm: Weapons", Color.green, Settings.CharmWeapons);

            Page colors = p.CreatePage("Colors", Color.magenta);
            ColorSlider(colors, "Self colour", Settings.CharmSelfCol);
            ColorSlider(colors, "Player colour", Settings.CharmPlayerCol);
            ColorSlider(colors, "Weapon colour", Settings.CharmWeaponCol);
            Slider(p, "Charm Opacity", Color.yellow, Settings.CharmAlpha, 0.1f, 0.1f, 1f);
        }

        private static void BuildCombat(Page root)
        {
            Page p = root.CreatePage("Combat", Color.red);
            IntSlider(p, "Aim: 0 off / 1 silent / 2 bullet TP", Color.red, Settings.AimMode, 0, 2);
            IntSlider(p, "Aim from: 0 head / 1 hand-gun", Color.red, Settings.AimOrigin, 0, 1);
            Slider(p, "Aim FOV (deg)", Color.yellow, Settings.AimFov, 5f, 5f, 180f);
            Toggle(p, "No Recoil", Color.green, Settings.NoRecoil);
            Toggle(p, "No Spread", Color.green, Settings.NoSpread);
            Toggle(p, "Rapid Fire", Color.green, Settings.RapidFire);
            Slider(p, "Rapid Fire Multiplier", Color.yellow, Settings.RapidFireMult, 5f, 1f, 100f);
            Toggle(p, "Infinite Ammo", Color.green, Settings.InfiniteAmmo);
            Slider(p, "Damage Multiplier", Color.yellow, Settings.DamageMult, 10f, 1f, 1000f);
        }

        private static void BuildMovement(Page root)
        {
            Page p = root.CreatePage("Movement", Color.green);
            Toggle(p, "Fly (L stick move, R stick up/down)", Color.green, Settings.Fly);
            Toggle(p, "Noclip (implies fly)", Color.green, Settings.Noclip);
            Slider(p, "Fly Speed (m/s)", Color.yellow, Settings.FlySpeed, 2f, 1f, 200f);
            Slider(p, "Walk Speed Multiplier", Color.yellow, Settings.WalkSpeedMult, 0.5f, 0.5f, 25f);
        }

        private static void BuildPlayer(Page root)
        {
            Page p = root.CreatePage("Player", Color.magenta);
            Toggle(p, "God Mode", Color.green, Settings.GodMode);
            Toggle(p, "Anti Ragdoll", Color.green, Settings.AntiRagdoll);
            Toggle(p, "Anti Grab", Color.green, Settings.AntiGrab);
            Toggle(p, "  -> also kill the grabber", Color.red, Settings.AntiGrabKill);
        }

        private static void BuildLobby(Page root)
        {
            Page p = root.CreatePage("Lobby", Color.yellow);
            p.CreateBool("Prop Spam (Crash Lobby)", Color.magenta, PropSpam.IsOn(), v => PropSpam.SetAll(v));
            p.CreateFunction("Kill All", Color.red, () => PlayerActions.KillAll());
            p.CreateBool("Kill Loop All", Color.red, PlayerActions.IsKillLoopAllOn(), v => PlayerActions.SetKillLoopAll(v));
            p.CreateBool("Annoy All", Color.yellow, PlayerActions.IsAnnoyAllOn(), v => PlayerActions.SetAnnoyAll(v));
            PlayersPage.Build(p);       // Lobby > Players > <player> > Kill / Kill (loop) / Teleport To / Annoy
        }

        private static void BuildSettings(Page root)
        {
            Page p = root.CreatePage("Settings", Color.white);

            BuildQuickToggles(p);
            BuildSaveConfig(p);

            _configsPage = p.CreatePage("Saved Configs", Color.cyan);
            RebuildConfigButtons();

            p.CreateFunction("Reset Everything", Color.red, ResetEverything);
        }

        /// <summary>
        /// Settings > Quick Toggles: the on/off abilities in one flat list, so a controller can flip them with the same
        /// point-and-click as everywhere else without hunting through sub-pages. Built from Core/QuickToggles.cs - the
        /// same list the Grip+Grip controller hotkey (QuickToggleHotkeys) cycles through - so the two can't drift apart.
        /// </summary>
        private static void BuildQuickToggles(Page settings)
        {
            Page p = settings.CreatePage("Quick Toggles", Color.green);
            p.CreateFunction("Hotkey: hold both Grips, Right Stick to pick, Right Trigger to flip", Color.gray, () => { });
            foreach (QuickToggle qt in QuickToggles.All) Toggle(p, qt.Label, Color.green, qt.Entry);
        }

        // ---------------- saved configs ----------------

        /// <summary>
        /// Settings > Save Config: opens a page with a text field (BoneMenu's on-screen keyboard) and a Confirm button.
        /// Saving writes UserData/BoneCheats/Configs/&lt;name&gt;.cfg and immediately adds a button for it under Saved Configs.
        /// If this BoneLib has no text field element, the page has a single Save button that names the config "Config 1", "Config 2", ...
        /// </summary>
        private static void BuildSaveConfig(Page settings)
        {
            Page p = settings.CreatePage("Save Config", Color.green);

            object field = TryCreateTextField(p, "Name: (point here to type)", Color.white, "", typed =>
            {
                _pendingName = typed ?? "";
            });

            if (field == null)
            {
                R.WarnOnce("no-textfield", "[Menu] This BoneLib has no text field element - Save Config will auto-name configs.");
                p.CreateFunction("Save (auto-named)", Color.green, () => SaveNamed(NextAutoName()));
                return;
            }

            p.CreateFunction("Confirm Save", Color.green, () =>
            {
                if (SaveNamed(_pendingName))
                {
                    _pendingName = "";
                    R.SetRaw(field, "Value", "");
                    R.SetRaw(field, "ElementName", "Name: (point here to type)");
                }
            });
        }

        private static bool SaveNamed(string typed)
        {
            string clean = Settings.CleanName(typed);
            if (clean == null) { Game.Notify("Type a name first (letters, numbers, spaces)."); return false; }

            bool existed = Settings.Exists(clean);
            string saved = Settings.SaveAs(clean);
            if (saved == null) { Game.Notify("Couldn't save the config (see the log)."); return false; }

            RebuildConfigButtons();
            Game.Notify(existed ? "Config '" + saved + "' updated." : "Config '" + saved + "' saved - find it under Saved Configs.");
            return true;
        }

        private static string NextAutoName()
        {
            for (int i = 1; i < 1000; i++)
            {
                string n = "Config " + i;
                if (!Settings.Exists(n)) return n;
            }
            return "Config " + Environment.TickCount;
        }

        /// <summary>Empties and refills Settings > Saved Configs so it always matches the files on disk (alphabetical).</summary>
        private static void RebuildConfigButtons()
        {
            if (_configsPage == null) return;
            _configsPage.RemoveAll();

            List<string> names = Settings.ListConfigs();
            if (names.Count == 0)
            {
                _configsPage.CreateFunction("(none saved yet - use Save Config)", Color.gray, () => { });
                return;
            }
            foreach (string n in names)
            {
                string name = n;
                _configsPage.CreateFunction(name, Color.cyan, () => LoadNamed(name));
            }
        }

        private static void LoadNamed(string name)
        {
            if (!Settings.LoadNamed(name)) { Game.Notify("Couldn't load '" + name + "' (file missing or unreadable)."); RebuildConfigButtons(); return; }
            SyncAll();
            Game.Notify("Config '" + name + "' loaded.");
        }

        /// <summary>
        /// Adds a text input to a page using BoneLib's string element (an on-screen keyboard in VR). BoneLib is reached by
        /// reflection so an older BoneLib without it can't stop the mod from loading. Returns the element, or null.
        /// </summary>
        internal static object TryCreateTextField(Page page, string label, Color color, string initial, Action<string> onChanged)
        {
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
                var candidates = new List<MethodInfo>(typeof(Page).GetMethods(bf));
                try
                {
                    foreach (Type t in typeof(Page).Assembly.GetTypes())
                        if (t.IsAbstract && t.IsSealed) candidates.AddRange(t.GetMethods(BindingFlags.Public | BindingFlags.Static));
                }
                catch { /* some types couldn't load; the Page's own methods above are still searched */ }

                foreach (MethodInfo m in candidates)
                {
                    if (m.Name != "CreateString") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    int skip = m.IsStatic ? 1 : 0;                      // static = extension method: first parameter is the page
                    if (m.IsStatic && (ps.Length == 0 || !ps[0].ParameterType.IsAssignableFrom(typeof(Page)))) continue;

                    var args = new object[ps.Length];
                    if (m.IsStatic) args[0] = page;
                    int strings = 0;
                    bool ok = true, hasCallback = false;
                    for (int i = skip; i < ps.Length; i++)
                    {
                        Type pt = ps[i].ParameterType;
                        if (pt == typeof(string)) args[i] = strings++ == 0 ? label : initial;
                        else if (pt == typeof(Color)) args[i] = color;
                        else if (pt == typeof(Action<string>)) { args[i] = onChanged; hasCallback = true; }
                        else if (ps[i].IsOptional) args[i] = Type.Missing;
                        else { ok = false; break; }
                    }
                    if (!ok || !hasCallback) continue;

                    return m.Invoke(m.IsStatic ? null : page, args);
                }
            }
            catch (Exception e) { R.WarnOnce("textfield", "[Menu] couldn't create a text field: " + e.Message); }
            return null;
        }

        // ---------------- element helpers ----------------

        private static void Toggle(Page page, string name, Color color, MelonPreferences_Entry<bool> entry)
        {
            BoolElement el = null;
            el = page.CreateBool(name, color, entry.Value, v => Changed(entry, v));
            Action refresh = () => { if (el.Value != entry.Value) el.Value = entry.Value; };
            Sync.Add(refresh);
            if (!Mirrors.TryGetValue(entry, out List<Action> list)) Mirrors[entry] = list = new List<Action>();
            list.Add(refresh);
        }

        private static void Slider(Page page, string name, Color color, MelonPreferences_Entry<float> entry, float step, float min, float max)
        {
            FloatElement el = page.CreateFloat(name, color, entry.Value, step, min, max, v => Changed(entry, Round(v)));
            Sync.Add(() => el.Value = entry.Value);
        }

        /// <summary>Colour picker: a 0-8 slider whose label shows the colour's name (Red, Green, ...), not a number key.</summary>
        private static void ColorSlider(Page page, string name, MelonPreferences_Entry<int> entry)
        {
            IntElement el = null;
            el = page.CreateInt(name + ": " + Colors.Name(entry.Value), Color.white, entry.Value, 1, 0, 8, v =>
            {
                Changed(entry, v);
                R.SetRaw(el, "ElementName", name + ": " + Colors.Name(v));
            });
            Sync.Add(() => { el.Value = entry.Value; R.SetRaw(el, "ElementName", name + ": " + Colors.Name(entry.Value)); });
        }

        private static void IntSlider(Page page, string name, Color color, MelonPreferences_Entry<int> entry, int min, int max)
        {
            IntElement el = page.CreateInt(name, color, entry.Value, 1, min, max, v => Changed(entry, v));
            Sync.Add(() => el.Value = entry.Value);
        }

        private static void Changed<T>(MelonPreferences_Entry<T> entry, T value)
        {
            entry.Value = value;
            if (_syncing) return;           // a mirror/sync update: the one ApplyAll comes from whoever started it

            if (Mirrors.TryGetValue(entry, out List<Action> others))
            {
                _syncing = true;
                try { foreach (Action a in others) a(); }
                finally { _syncing = false; }
            }
            FeatureManager.ApplyAll();
        }

        /// <summary>Pushes every setting back into its menu element(s), then applies once.</summary>
        private static void SyncAll()
        {
            _syncing = true;
            try { foreach (Action a in Sync) a(); }
            finally { _syncing = false; }
            FeatureManager.ApplyAll();
        }

        private static void ResetEverything()
        {
            Settings.ResetToDefaults();
            SyncAll();
            Game.Notify("Everything reset to defaults (your saved configs are untouched).");
        }

        // Slider increments accumulate float error (0.30000001...); keep two decimals.
        private static float Round(float v) { return Mathf.Round(v * 100f) / 100f; }
    }
}