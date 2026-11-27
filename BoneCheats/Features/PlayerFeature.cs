using System;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using MelonLoader;
using Health = Il2CppSLZ.Marrow.Health;
using HealthMode = Il2CppSLZ.Marrow.Health.HealthMode;
using PhysicsRig = Il2CppSLZ.Marrow.PhysicsRig;
using PlayerHealth = Il2CppSLZ.Marrow.Player_Health;
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
    ///   4. Harmony prefixes block Player_Health.MakeVignette() and StartHealthRegnerationAndVignette(float) on
    ///      the LOCAL player's Player_Health only, so the red/black damage vignette never fires either, on top
    ///      of the ragdoll guard above. NOTE: this does not touch CoFadeDeathVignette (the near-death fade) -
    ///      that one returns an IEnumerator, which Harmony can't cleanly skip the same way, but since
    ///      healthMode=Invincible already stops health from ever reaching the value that triggers it, it
    ///      shouldn't fire anyway. Tell me if you still see a fade-out and I'll hook that one properly.
    ///
    /// Why it looked broken before: Apply() - the only place healthMode got (re)set - only ran on
    /// FeatureManager.ApplyAll() (menu toggle / scene load) and from Tick() when rig.Pointer changed. Between
    /// those, nothing re-asserted healthMode, and nothing stopped RagdollRig()/ShutdownRig() (or the vignette
    /// methods) at all - Invincible blocks the health bar hitting zero, it does NOT stop the rig from being
    /// ragdolled by a hit, an explosion, or a grab, and it does NOT stop the damage vignette either, which reads
    /// exactly like "god mode doesn't work". Tick() now re-asserts healthMode AND runs the ragdoll guard every
    /// single tick while God Mode is on, not just on a rig swap, so nothing else in the game gets a window to
    /// flip it back or knock you down (or flash the screen) unnoticed.
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

            var ragdollPrefix = new HarmonyMethod(typeof(PlayerFeature).GetMethod(nameof(BlockRagdoll), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string name in new[] { "RagdollRig", "ShutdownRig" })
            {
                try
                {
                    MethodInfo m = typeof(PhysicsRig).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (m == null) { MelonLogger.Warning("[Player] PhysicsRig." + name + "() not found."); continue; }
                    harmony.Patch(m, prefix: ragdollPrefix);
                    MelonLogger.Msg("[Player] hooked PhysicsRig." + name + "() for God Mode's ragdoll guard");
                }
                catch (Exception e) { MelonLogger.Warning("[Player] couldn't hook " + name + ": " + e.Message); }
            }

            var vignettePrefix = new HarmonyMethod(typeof(PlayerFeature).GetMethod(nameof(BlockVignette), BindingFlags.Static | BindingFlags.NonPublic));
            PatchVignetteMethod(harmony, vignettePrefix, "MakeVignette", Type.EmptyTypes);
            PatchVignetteMethod(harmony, vignettePrefix, "StartHealthRegnerationAndVignette", new[] { typeof(float) });
        }

        private static void PatchVignetteMethod(HarmonyLib.Harmony harmony, HarmonyMethod prefix, string name, Type[] args)
        {
            try
            {
                MethodInfo m = typeof(PlayerHealth).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null);
                if (m == null) { MelonLogger.Warning("[Player] Player_Health." + name + "(...) not found."); return; }
                harmony.Patch(m, prefix: prefix);
                MelonLogger.Msg("[Player] hooked Player_Health." + name + "(...) for God Mode's vignette guard");
            }
            catch (Exception e) { MelonLogger.Warning("[Player] couldn't hook " + name + ": " + e.Message); }
        }

        // Returning false skips the original method (RagdollRig/ShutdownRig never runs).
        private static bool BlockRagdoll(PhysicsRig __instance)
        {
            if (!Settings.GodMode.Value) return true;
            RigManager rig = Game.Rig;
            if (rig == null || rig.physicsRig == null) return true;
            return __instance.Pointer != rig.physicsRig.Pointer;   // only protect the local rig
        }

        // Same idea, for the damage vignette (the red/black flash on taking a hit): skip it entirely for the
        // local player's Player_Health while God Mode is on. Works on both patched methods since both take no
        // meaningful __instance-independent state we need - we only care whose Player_Health this is.
        private static bool BlockVignette(PlayerHealth __instance)
        {
            if (!Settings.GodMode.Value) return true;
            RigManager rig = Game.Rig;
            Health health = rig != null ? rig.health : null;
            if (health == null) return true;
            PlayerHealth mine = health.TryCast<PlayerHealth>();
            if (mine == null) return true;
            return __instance.Pointer != mine.Pointer;   // only protect the local player's own Player_Health
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
