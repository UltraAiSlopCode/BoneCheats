using System.Collections.Generic;
using BoneCheats.Features;

namespace BoneCheats.Core
{
    internal static class FeatureManager
    {
        // To add a cheat: write a Feature subclass in /Features and list it here.
        private static readonly List<Feature> Features = new List<Feature>
        {
            new PlayerFeature(),     // god mode: invincible health + ragdoll/knockout guard, all one toggle
            new MovementFeature(),   // fly, noclip, walk speed
            new NoRecoilFeature(),   // no recoil guard (the hook itself lives in NoRecoil.cs)
            new CombatFeature(),     // aim assist targeting, gun tweaks (recoil, spread, rpm, ammo, damage)
            new EspFeature(),        // health / names of other players
            new SkeletonFeature(),   // stick-figure skeleton on other players
            new CharmFeature(),      // full-body charms (self, players, weapons)
            new TracerFeature(),     // bullet tracers
            new PlayerActionsFeature(), // Players page: kill / teleport to / annoy, keeps the list in sync
        };

        public static void OnSceneInitialized()
        {
            foreach (var f in Features) f.ClearFailures();
            ApplyAll();
        }

        public static void ApplyAll() { foreach (var f in Features) f.SafeApply(); }
        public static void ResetAll() { foreach (var f in Features) f.SafeReset(); }
        public static void Tick() { foreach (var f in Features) f.SafeTick(); }
        public static void FixedTick() { foreach (var f in Features) f.SafeFixedTick(); }
        public static void LateTick() { foreach (var f in Features) f.SafeLateTick(); }
    }
}