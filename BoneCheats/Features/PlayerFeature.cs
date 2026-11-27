using System;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using MelonLoader;
using Health = Il2CppSLZ.Marrow.Health;
using HealthMode = Il2CppSLZ.Marrow.Health.HealthMode;
using PhysicsRig = Il2CppSLZ.Marrow.PhysicsRig;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Features
{
    /// <summary>
    /// God mode. Anti Ragdoll used to be its own separate toggle - it now lives entirely in here, because
    /// "invincible but still gets knocked out/knocked down" never felt like god mode in the first place.
    /// Flipping God Mode on now does all three things at once:
    ///   1. Health.healthMode = Invincible on the local rig (as before).
    ///   2. Harmony prefixes block PhysicsRig.RagdollRig() / ShutdownRig() for the LOCAL rig only, so getting
    ///      hit hard or grabbed can't knock you into the ragdoll/black-screen state in the first place.
    ///   3. A once-a-second safety net: if the rig is already shut down for any reason (a state it was in
    ///      before God Mode was turned on, or one that slipped past the prefix), it's revived with
    ///      UnRagdollRig() + TurnOnRig().
    ///
    /// Why it looked broken before: Apply() - the only place healthMode got (re)set - only ran on
    /// FeatureManager.ApplyAll() (menu toggle / scene load) and from Tick() when rig.Pointer changed. Between
    /// those, nothing re-asserted healthMode, and nothing stopped RagdollRig()/ShutdownRig() at all - Invincible
    /// blocks the health bar hitting zero, it does NOT stop the rig from being ragdolled by a hit, an explosion,
    /// or a grab, which reads exactly like "god mode doesn't work". Tick() now re-asserts healthMode AND runs
    /// the ragdoll guard every single tick while God Mode is on, not just on a rig swap, so nothing else in the
    /// game gets a window to flip it back or knock you down unnoticed.
    /// </summary>
    internal sealed class PlayerFeature : Feature
    {
        private IntPtr _lastRig = IntPtr.Zero;
        private static bool _hooksInstalled;
        private float _nextRagdollCheck;

        public override string Name => "Player";

        /// <summary>Installs the RagdollRig/ShutdownRig blockers. Call once at startup and once per scene load
        /// (both calls are no-ops after the first success) - same pattern every other Harmony hook in this
        /// mod uses, since the target methods can be unavailable the very first time this runs.</summary>
        public static void TryInstallHooks(HarmonyLib.Harmony harmony)
        {
            if (_hooksInstalled) return;
            _hooksInstalled = true;
            var prefix = new HarmonyMethod(typeof(PlayerFeature).GetMethod(nameof(BlockRagdoll), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in new[] { "RagdollRig", "ShutdownRig" })
            {
                try
                {
                    MethodInfo m = typeof(PhysicsRig).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (m == null) { MelonLogger.Warning("[Player] PhysicsRig." + name + "() not found."); continue; }
                    harmony.Patch(m, prefix: prefix);
                    MelonLogger.Msg("[Player] hooked PhysicsRig." + name + "() for God Mode's ragdoll guard");
                }
                catch (Exception e) { MelonLogger.Warning("[Player] couldn't hook " + name + ": " + e.Message); }
            }
        }

        // Returning false skips the original method (RagdollRig/ShutdownRig never runs).
        private static bool BlockRagdoll(PhysicsRig __instance)
        {
            if (!Settings.GodMode.Value) return true;
            RigManager rig = Game.Rig;
            if (rig == null || rig.physicsRig == null) return true;
            return __instance.Pointer != rig.physicsRig.Pointer;   // only protect the local rig
        }

        protected override void Apply()
        {
            RigManager rig = Game.Rig;
            if (rig == null) { _lastRig = IntPtr.Zero; return; }
            _lastRig = rig.Pointer;
            SetHealthMode(rig, Settings.GodMode.Value);
        }

        protected override void Reset()
        {
            SetHealthMode(Game.Rig, false);
        }

        protected override void Tick()
        {
            RigManager rig = Game.Rig;
            if (rig == null) return;

            // The rig is rebuilt on respawn/avatar swap; re-apply when we see a new one.
            if (rig.Pointer != _lastRig) { Apply(); return; }

            if (!Settings.GodMode.Value) return;

            // Re-assert every tick instead of only on a rig swap - whatever flips healthMode back (damage
            // handling, a respawn path that doesn't replace the rig, anything else) gets overridden immediately
            // instead of getting a window where the local player is mortal again without the menu saying so.
            SetHealthMode(rig, true);

            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextRagdollCheck) return;
            _nextRagdollCheck = now + 1f;

            PhysicsRig phys = rig.physicsRig;
            if (phys == null || !phys.shutdown) return;   // only act when the rig is actually down
            phys.UnRagdollRig();
            phys.TurnOnRig();
        }

        private static void SetHealthMode(RigManager rig, bool invincible)
        {
            Health health = rig != null ? rig.health : null;
            if (health != null) health.healthMode = invincible ? HealthMode.Invincible : HealthMode.Mortal;
        }
    }
}
