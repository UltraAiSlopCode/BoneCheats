using System;
using System.Collections.Generic;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using PhysicsRig = Il2CppSLZ.Marrow.PhysicsRig;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Features
{
    /// <summary>
    /// Anti ragdoll: stops YOUR rig from being knocked out (the "lying on the ground with a black screen"
    /// state). Harmony prefixes block PhysicsRig.RagdollRig() and PhysicsRig.ShutdownRig(), but only when the
    /// rig is the local player's, never another player's puppet. While the feature is on, a rig that is already
    /// shut down (PhysicsRig.shutdown) is switched back on with UnRagdollRig() + TurnOnRig() once a second.
    /// </summary>
    internal sealed class AntiRagdollFeature : Feature
    {
        private static bool _installed;
        private float _next;

        public override string Name => "AntiRagdoll";

        public static void TryInstall(HarmonyLib.Harmony harmony)
        {
            if (_installed) return;
            _installed = true;
            var prefix = new HarmonyMethod(typeof(AntiRagdollFeature).GetMethod(nameof(Block), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in new[] { "RagdollRig", "ShutdownRig" })
            {
                try
                {
                    MethodInfo m = typeof(PhysicsRig).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (m == null) { MelonLogger.Warning("[AntiRagdoll] PhysicsRig." + name + "() not found."); continue; }
                    harmony.Patch(m, prefix: prefix);
                    MelonLogger.Msg("[AntiRagdoll] hooked PhysicsRig." + name + "()");
                }
                catch (Exception e) { MelonLogger.Warning("[AntiRagdoll] couldn't hook " + name + ": " + e.Message); }
            }
        }

        // Returning false skips the original method.
        private static bool Block(PhysicsRig __instance)
        {
            if (!Settings.AntiRagdoll.Value) return true;
            RigManager rig = Game.Rig;
            if (rig == null || rig.physicsRig == null) return true;
            return __instance.Pointer != rig.physicsRig.Pointer;      // only protect the local rig
        }

        protected override void Tick()
        {
            if (!Settings.AntiRagdoll.Value) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 1f;

            RigManager rig = Game.Rig;
            PhysicsRig phys = rig != null ? rig.physicsRig : null;
            if (phys == null || !phys.shutdown) return;      // only act when the rig is down, so a healthy rig is never poked

            phys.UnRagdollRig();
            phys.TurnOnRig();
        }
    }
}
