using System;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Gun = Il2CppSLZ.Marrow.Gun;

namespace BoneCheats.Features
{
    /// <summary>
    /// Harmony hook on Gun.Fire(): silent aim, bullet TP, infinite ammo, no recoil and bullet tracers.
    ///
    /// Silent aim: for the duration of the Fire() call the gun's fire point is rotated at the target,
    ///             then put back, so the gun in your hand never visibly moves.
    /// Bullet TP:  same, but the fire point is also moved to just in front of the target.
    /// Infinite ammo: the magazine is topped up right before Fire() and again right after, so it can't run dry.
    /// No recoil:  see NoRecoil - the firing body of the bullet is swapped for a dummy while the gun fires.
    /// Tracers:    Fire() can be called while the trigger is merely held (or the gun is empty), so a tracer is
    ///             only drawn when the gun's state shows that a round really left it (cartridge state changed,
    ///             chamber changed or the magazine count dropped).
    /// </summary>
    internal static class GunHooks
    {
        public sealed class FireState
        {
            public Gun Gun;
            public Transform Fp;
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Moved;
            public bool HasTarget;
            public Vector3 Target;
            public CombatFeature.ShotSnapshot Before;
        }

        private static bool _installed;

        public static void TryInstall(HarmonyLib.Harmony harmony)
        {
            if (_installed) return;
            try
            {
                MethodInfo fire = typeof(Gun).GetMethod("Fire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (fire == null) { MelonLogger.Warning("[GunHooks] Gun.Fire() not found."); return; }

                var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                harmony.Patch(fire,
                    new HarmonyMethod(typeof(GunHooks).GetMethod(nameof(Prefix), flags)),
                    new HarmonyMethod(typeof(GunHooks).GetMethod(nameof(Postfix), flags)));
                _installed = true;
                MelonLogger.Msg("[GunHooks] hooked Gun.Fire()");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[GunHooks] install failed: " + e.Message);
            }
        }

        private static void Prefix(Gun __instance, out FireState __state)
        {
            __state = null;
            try
            {
                Gun gun = __instance;
                if (gun == null || !CombatFeature.IsPlayerGun(gun)) return;

                if (Settings.InfiniteAmmo.Value) CombatFeature.RefillGun(gun);

                var st = new FireState { Gun = gun, Before = CombatFeature.Snapshot(gun) };
                __state = st;       // set before anything below can throw, so Postfix always runs and restores what Begin changed
                if (Settings.NoRecoil.Value) NoRecoil.OnPlayerFire(gun);
                Transform fp = CombatFeature.FirePoint(gun);
                st.Fp = fp;

                if (fp != null)
                {
                    st.Pos = fp.position;
                    st.Rot = fp.rotation;
                    int mode = Settings.AimMode.Value;

                    if (mode != 0 && CombatFeature.TryGetTargetPoint(out Vector3 target))
                    {
                        Vector3 dir = (target - st.Pos).normalized;
                        fp.rotation = Quaternion.LookRotation(dir);
                        if (mode == 2) fp.position = target - dir * 0.35f;
                        st.Moved = true;
                        st.HasTarget = true;
                        st.Target = target;
                    }
                }

                __state = st;
            }
            catch (Exception e)
            {
                R.WarnOnce("prefix", "[GunHooks] prefix error: " + e.Message);
            }
        }

        private static void Postfix(FireState __state)
        {
            NoRecoil.OnFireDone();
            FireState st = __state;
            if (st == null) return;

            try
            {
                // 1) did a round actually leave the gun?
                if (Settings.BulletTracers.Value && st.Fp != null && DidFire(st))
                {
                    if (st.HasTarget) TracerFeature.RecordSegment(st.Pos, st.Target);
                    else TracerFeature.RecordRay(st.Pos, st.Fp.forward);
                }
            }
            catch (Exception e) { R.WarnOnce("postfix-tracer", "[GunHooks] tracer error: " + e.Message); }

            try
            {
                // 2) put the fire point back
                if (st.Moved && st.Fp != null)
                {
                    st.Fp.position = st.Pos;
                    st.Fp.rotation = st.Rot;
                }
            }
            catch (Exception e) { R.WarnOnce("postfix-restore", "[GunHooks] restore error: " + e.Message); }

            try
            {
                // 3) keep the magazine full (after the shot was measured above)
                if (Settings.InfiniteAmmo.Value) CombatFeature.RefillGun(st.Gun);
            }
            catch (Exception e) { R.WarnOnce("postfix-ammo", "[GunHooks] ammo error: " + e.Message); }
        }

        private static bool DidFire(FireState st)
        {
            return CombatFeature.Fired(st.Before, CombatFeature.Snapshot(st.Gun));
        }
    }
}
