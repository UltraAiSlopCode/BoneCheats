using System;
using System.Reflection;
using HarmonyLib;
using LabFusion.Entities;
using MelonLoader;

namespace BoneCheats.Features
{
    /// <summary>
    /// Fusion "culls" players that are far away: it freezes their rig and hides their meshes (IsCulled = True /
    /// IsHidden = True at ~57 m). That is why ESP stayed stuck at the spot where a player was last close, and why
    /// charms never showed on far players. This patch makes NetworkPlayer.OnEntityCull(bool) always receive
    /// "not culled", so far players keep moving and stay visible.
    /// </summary>
    internal static class FusionCull
    {
        private static bool _done;
        public static bool Active;

        public static void TryInstall(HarmonyLib.Harmony harmony)
        {
            if (_done) return;
            _done = true;
            try
            {
                // Typed reference into LabFusion.dll: public virtual void NetworkPlayer.OnEntityCull(bool isInactive).
                // nameof() + typeof() mean a renamed/removed member is a build error instead of a missing-by-name warning.
                MethodInfo target = AccessTools.Method(typeof(NetworkPlayer), nameof(NetworkPlayer.OnEntityCull), new[] { typeof(bool) });
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(FusionCull).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                Active = true;
                MelonLogger.Msg("[NoCull] hooked NetworkPlayer.OnEntityCull");
            }
            catch (Exception e) { MelonLogger.Warning("[NoCull] couldn't hook Fusion's culling: " + e.Message); }
        }

        // Parameter name matches LabFusion's own (OnEntityCull(bool isInactive)); Harmony binds it by that name.
        private static void Prefix(ref bool isInactive)
        {
            isInactive = false;     // never cull: far players keep moving and stay visible
        }
    }
}
