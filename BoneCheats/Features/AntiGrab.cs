using System;
using System.Reflection;
using BoneCheats.Core;
using HarmonyLib;
using LabFusion.Senders;      // only used to find the LabFusion assembly
using MelonLoader;
using UnityEngine;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Features
{
    /// <summary>
    /// Anti grab: other players can't latch onto your body.
    ///
    /// When another player grabs something, Fusion sends everybody a PlayerRepGrabMessage and your game runs
    /// PlayerRepGrabMessage.ApplyGrip(sender, data), which attaches that player's puppet hand to the grip. If that grip
    /// is part of YOUR rig (your avatar's grips are network entities too), this prefix skips the call, so their hand
    /// never attaches to you and you can't be held or dragged. Grabs on anything else go through untouched.
    ///
    /// Everything is looked up by name at runtime, so if a Fusion update renames something you get one warning in the
    /// log and the toggle simply does nothing - it can't break the mod.
    ///
    /// "Kill grabber" (separate toggle): when a grab on you is blocked, the player who tried it also gets hit with
    /// PlayerActions.Kill - same silent damage message the Players page uses. Fires at most once per 2 seconds per
    /// grabber, so a button mashing their grab input doesn't spam it.
    /// </summary>
    internal static class AntiGrab
    {
        private static bool _installed;
        private static MethodInfo _getGrip;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static void TryInstall(HarmonyLib.Harmony harmony)
        {
            if (_installed) return;
            try
            {
                Type msg = typeof(PlayerSender).Assembly.GetType("LabFusion.Network.PlayerRepGrabMessage");
                MethodInfo apply = msg != null ? msg.GetMethod("ApplyGrip", Any) : null;
                if (apply == null) { MelonLogger.Warning("[AntiGrab] PlayerRepGrabMessage.ApplyGrip not found - Anti Grab won't work with this Fusion version."); return; }

                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(AntiGrab).GetMethod(nameof(Block), BindingFlags.Static | BindingFlags.NonPublic)));
                _installed = true;
                MelonLogger.Msg("[AntiGrab] hooked PlayerRepGrabMessage.ApplyGrip()");
            }
            catch (Exception e) { MelonLogger.Warning("[AntiGrab] install failed: " + e.Message); }
        }

        private static readonly System.Collections.Generic.Dictionary<byte, float> _lastKill = new System.Collections.Generic.Dictionary<byte, float>();

        // __0 = sender (byte, the grabbing player's small ID). __1 = the PlayerRepGrabData argument.
        // Returning false skips the original.
        private static bool Block(byte __0, object __1)
        {
            if (!Settings.AntiGrab.Value || __1 == null) return true;
            try
            {
                RigManager rig = Game.Rig;
                if (rig == null) return true;

                if (_getGrip == null) _getGrip = __1.GetType().GetMethod("GetGrip", Any);
                if (_getGrip == null) return true;

                Component grip = _getGrip.Invoke(__1, null) as Component;
                if (grip == null) return true;

                // a grip that belongs to the local player's rig -> refuse the grab
                if (!grip.transform.IsChildOf(rig.transform)) return true;

                if (Settings.AntiGrabKill.Value)
                {
                    float now = Time.unscaledTime;
                    if (!_lastKill.TryGetValue(__0, out float next) || now >= next)
                    {
                        _lastKill[__0] = now + 2f;
                        PlayerActions.Kill(__0);
                    }
                }
                return false;
            }
            catch (Exception e) { R.WarnOnce("antigrab-block", "[AntiGrab] check failed: " + e.Message); }
            return true;
        }
    }
}
