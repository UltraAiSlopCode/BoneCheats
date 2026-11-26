using System.Collections.Generic;
using MelonLoader;

namespace BoneCheats.Core
{
    /// <summary>One entry on the Quick Toggles page: its label there, and the setting it flips.</summary>
    internal readonly struct QuickToggle
    {
        public readonly string Label;
        public readonly MelonPreferences_Entry<bool> Entry;
        public QuickToggle(string label, MelonPreferences_Entry<bool> entry) { Label = label; Entry = entry; }
    }

    /// <summary>
    /// The flat list behind Settings > Quick Toggles - same order, same labels, same settings - built once
    /// right after Settings.Init() has created every entry. MenuBuilder.BuildQuickToggles() renders this list
    /// as menu buttons, and QuickToggleHotkeys cycles through this exact same list for the controller chord,
    /// so the menu and the hotkey can never drift out of sync with each other.
    /// </summary>
    internal static class QuickToggles
    {
        private static List<QuickToggle> _all;

        public static List<QuickToggle> All
        {
            get
            {
                if (_all == null) Build();
                return _all;
            }
        }

        private static void Build()
        {
            _all = new List<QuickToggle>
            {
                new QuickToggle("God Mode", Settings.GodMode),
                new QuickToggle("Fly", Settings.Fly),
                new QuickToggle("No Clip", Settings.Noclip),
                new QuickToggle("Anti Ragdoll", Settings.AntiRagdoll),
                new QuickToggle("Anti Grab", Settings.AntiGrab),
                new QuickToggle("No Recoil", Settings.NoRecoil),
                new QuickToggle("No Spread", Settings.NoSpread),
                new QuickToggle("Rapid Fire", Settings.RapidFire),
                new QuickToggle("Infinite Ammo", Settings.InfiniteAmmo),
                new QuickToggle("ESP", Settings.Esp),
                new QuickToggle("ESP Names", Settings.EspNames),
                new QuickToggle("ESP Health Bars", Settings.EspHealth),
                new QuickToggle("Skeleton", Settings.Skeleton),
                new QuickToggle("Bullet Tracers", Settings.BulletTracers),
            };
        }
    }
}