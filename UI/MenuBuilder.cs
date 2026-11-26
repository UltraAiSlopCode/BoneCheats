using System;
using System.Collections.Generic;
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

        public static void Build()
        {
            Page root = Page.Root.CreatePage("BoneCheats", Color.red);

            BuildVisuals(root);
            BuildCharms(root);
            BuildCombat(root);
            BuildMovement(root);
            BuildPlayer(root);
            BuildLobby(root);
            BuildSettings(root);

        }

        // ---------------- pages ----------------

        private static void BuildVisuals(Page root)
        {
            Page p = root.CreatePage("Visuals", Color.cyan);
            Toggle(p, "ESP (master)", Color.green, Settings.Esp);
            Toggle(p, "Names", Color.white, Settings.EspNames);
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
            p.CreateFunction("Save Config", Color.green, SaveConfig);
            p.CreateFunction("Load Config", Color.cyan, LoadConfig);
            p.CreateFunction("Reset Everything", Color.red, ResetEverything);
        }

        private static void SaveConfig()
        {
            Game.Notify(Settings.Save() ? "Config saved." : "Couldn't save the config (see the log).");
        }

        private static void LoadConfig()
        {
            if (!Settings.Load()) { Game.Notify("No saved config yet - press Save Config first."); return; }
            foreach (Action a in Sync) a();
            FeatureManager.ApplyAll();
            Game.Notify("Config loaded.");
        }

        // ---------------- element helpers ----------------

        private static void Toggle(Page page, string name, Color color, MelonPreferences_Entry<bool> entry)
        {
            BoolElement el = page.CreateBool(name, color, entry.Value, v => Changed(entry, v));
            Sync.Add(() => el.Value = entry.Value);
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
            FeatureManager.ApplyAll();
        }

        private static void ResetEverything()
        {
            Settings.ResetToDefaults();
            foreach (Action a in Sync) a();
            FeatureManager.ApplyAll();
            Game.Notify("Everything reset to defaults (your saved config is untouched).");
        }

        // Slider increments accumulate float error (0.30000001...); keep two decimals.
        private static float Round(float v) { return Mathf.Round(v * 100f) / 100f; }
    }
}
