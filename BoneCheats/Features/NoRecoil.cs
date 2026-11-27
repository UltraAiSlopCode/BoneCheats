using System;
using System.Collections.Generic;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Gun = Il2CppSLZ.Marrow.Gun;
using InteractableHost = Il2CppSLZ.Marrow.InteractableHost;
using Projectile = Il2CppSLZ.Marrow.Projectile;

namespace BoneCheats.Features
{
    /// <summary>
    /// No recoil, reworked.
    ///
    /// Why the old one never did anything: it zeroed float properties named "kick"/"recoil" on Gun, and Gun (in the
    /// installed Il2CppSLZ.Marrow.dll) has no such property - the log line said "nothing found". Its second trick,
    /// restoring the gun body's velocity after Fire(), can't work either: Unity applies AddForce at the next physics
    /// step, so the velocity it read back had not changed yet.
    ///
    /// What the game actually does: Gun.Fire() spawns a Projectile via Projectile.SetBulletObject(data, firePoint,
    /// position, rotation, Rigidbody firingBody, proxy), and the projectile pushes the firing body back with the
    /// bullet's momentum. So this mod does two things, both only for guns held by the player:
    ///
    ///  1. Harmony prefix on Projectile.SetBulletObject: if that firing body is one of the player's guns that just
    ///     fired, it is swapped for a kinematic dummy body. The push lands on the dummy (kinematic = ignores forces),
    ///     so the gun gets no kick, while the bullet itself is unchanged.
    ///  2. A short guard after each shot (FixedTick) that keeps the gun body's velocity and spin at what they were
    ///     right before the shot, in case something else adds a twist (slide / hammer motion).
    ///
    /// Nothing is left on the gun and nothing runs when the setting is off.
    /// </summary>
    internal static class NoRecoil
    {
        private const float WindowSeconds = 0.6f;   // a bullet spawned within this long after Fire() counts as ours
        private const float GuardSeconds = 0.12f;

        private sealed class Shot
        {
            public Rigidbody Body;
            public float Time;
            public Vector3 Vel, Ang;
        }

        // firing bodies of the player's guns, by native pointer
        private static readonly Dictionary<IntPtr, Shot> Recent = new Dictionary<IntPtr, Shot>();
        private static Rigidbody _sink;
        private static bool _installed;

        // set for the duration of one Gun.Fire() call of a player's gun
        private static bool _firing;
        private static int _firingFrame = -1;
        private static IntPtr _firingBody = IntPtr.Zero;

        // log counters, so the MelonLoader log shows what really happened without spamming
        private static int _logShots, _logSwaps, _logBlocks;

        // ---------------- install ----------------

        public static void TryInstall(HarmonyLib.Harmony harmony)
        {
            if (_installed) return;
            try
            {
                MethodInfo m = typeof(Projectile).GetMethod("SetBulletObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (m == null) { MelonLogger.Warning("[No Recoil] Projectile.SetBulletObject not found - no recoil can't hook."); return; }

                harmony.Patch(m, new HarmonyMethod(typeof(NoRecoil).GetMethod(nameof(SetBulletObjectPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                _installed = true;
                MelonLogger.Msg("[No Recoil] hooked Projectile.SetBulletObject");

                // second layer: any physics push applied to the gun's own body while its Fire() runs is dropped
                int n = 0;
                var block = new HarmonyMethod(typeof(NoRecoil).GetMethod(nameof(BlockForcePrefix), BindingFlags.Static | BindingFlags.NonPublic));
                string[] names = { "AddForce", "AddForceAtPosition", "AddRelativeForce", "AddTorque", "AddRelativeTorque" };
                foreach (MethodInfo rm in typeof(Rigidbody).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (Array.IndexOf(names, rm.Name) < 0 || rm.IsGenericMethod) continue;
                    try { harmony.Patch(rm, block); n++; }
                    catch (Exception e) { MelonLogger.Warning("[No Recoil] couldn't hook Rigidbody." + rm.Name + ": " + e.Message); }
                }
                MelonLogger.Msg("[No Recoil] hooked " + n + " Rigidbody force method(s)");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[No Recoil] install failed: " + e.Message);
            }
        }

        // ---------------- called from GunHooks.Prefix (a player gun is about to fire) ----------------

        public static void OnPlayerFire(Gun gun)
        {
            InteractableHost host = gun.host;
            Rigidbody rb = host != null ? host.Rb : null;
            if (rb == null || rb.Pointer == IntPtr.Zero) return;

            Recent[rb.Pointer] = new Shot { Body = rb, Time = Time.unscaledTime, Vel = rb.velocity, Ang = rb.angularVelocity };

            _firing = true;
            _firingFrame = Time.frameCount;
            _firingBody = rb.Pointer;

            if (_logShots < 3)
            {
                _logShots++;
                MelonLogger.Msg("[No Recoil] player gun fired - body '" + rb.name + "' (no-recoil is ON)");
            }
        }

        /// <summary>Called from GunHooks.Postfix: the Fire() call is over.</summary>
        public static void OnFireDone() { _firing = false; }

        // drops forces on the firing gun's own body while its Fire() call runs; false = skip the original method
        private static bool BlockForcePrefix(Rigidbody __instance)
        {
            if (!_firing) return true;
            try
            {
                if (_firingFrame != Time.frameCount) { _firing = false; return true; }
                if (!Settings.NoRecoil.Value) return true;
                if (__instance == null || __instance.Pointer != _firingBody) return true;

                if (_logBlocks < 3) { _logBlocks++; MelonLogger.Msg("[No Recoil] blocked a physics push on the gun during Fire()"); }
                return false;
            }
            catch { return true; }
        }

        // ---------------- the hook ----------------

        // __4 = the 5th parameter of SetBulletObject: the Rigidbody that fires the bullet
        private static void SetBulletObjectPrefix(ref Rigidbody __4)
        {
            try
            {
                if (!Settings.NoRecoil.Value) return;
                Rigidbody rb = __4;
                if (rb == null || rb.Pointer == IntPtr.Zero) return;
                if (!Recent.TryGetValue(rb.Pointer, out Shot shot)) return;
                if (Time.unscaledTime - shot.Time > WindowSeconds) return;

                Rigidbody sink = Sink();
                if (sink != null)
                {
                    __4 = sink;
                    if (_logSwaps < 3) { _logSwaps++; MelonLogger.Msg("[No Recoil] swapped the bullet's firing body for the dummy"); }
                }
            }
            catch (Exception e) { R.WarnOnce("norecoil-prefix", "[No Recoil] hook error: " + e.Message); }
        }

        private static Rigidbody Sink()
        {
            if (_sink != null) return _sink;
            var go = new GameObject("BoneCheats_RecoilSink");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _sink = go.AddComponent<Rigidbody>();
            _sink.isKinematic = true;
            _sink.useGravity = false;
            return _sink;
        }

        // ---------------- guard (FixedUpdate) ----------------

        public static void FixedTick()
        {
            if (Recent.Count == 0) return;

            float now = Time.unscaledTime;
            List<IntPtr> dead = null;
            foreach (var kv in Recent)
            {
                Shot s = kv.Value;
                float age = now - s.Time;
                bool gone = s.Body == null || age > WindowSeconds;
                if (gone) { (dead ?? (dead = new List<IntPtr>())).Add(kv.Key); continue; }

                if (Settings.NoRecoil.Value && age <= GuardSeconds && !s.Body.isKinematic)
                {
                    s.Body.velocity = s.Vel;
                    s.Body.angularVelocity = s.Ang;
                }
            }
            if (dead != null) foreach (IntPtr k in dead) Recent.Remove(k);
        }
    }

    /// <summary>Only exists to give NoRecoil its own FixedUpdate slot.</summary>
    internal sealed class NoRecoilFeature : Feature
    {
        public override string Name => "NoRecoil";
        protected override void FixedTick() { NoRecoil.FixedTick(); }
    }
}
