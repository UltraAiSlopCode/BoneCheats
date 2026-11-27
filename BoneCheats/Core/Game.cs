using System;
using System.Reflection;
using BoneLib;
using MelonLoader;
using UnityEngine;
using BaseController = Il2CppSLZ.Marrow.BaseController;
using ControllerRig = Il2CppSLZ.Marrow.ControllerRig;
using Hand = Il2CppSLZ.Marrow.Hand;
using PhysicsRig = Il2CppSLZ.Marrow.PhysicsRig;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Core
{
    /// <summary>
    /// Every place the mod touches BoneLib / the player rig is in this file (typed against Il2CppSLZ.Marrow). If a game or BoneLib
    /// update renames something, this is the one file to fix.
    /// </summary>
    internal static class Game
    {
        /// <summary>The player's RigManager (null between levels).</summary>
        public static RigManager Rig
        {
            get
            {
                try
                {
                    RigManager r = Player.RigManager;
                    return r != null && r.Pointer != IntPtr.Zero ? r : null;
                }
                catch { return null; }
            }
        }

        /// <summary>Moves the local player's whole rig to a world position.</summary>
        public static bool TeleportTo(Vector3 pos)
        {
            RigManager rig = Rig;
            if (rig == null) return false;
            rig.Teleport(pos, true);
            return true;
        }

        /// <summary>The VR camera = the player's head.</summary>
        private static Camera _cam;
        public static Transform Head
        {
            get
            {
                if (_cam == null) _cam = Camera.main;      // looked up again only when the old one is gone
                return _cam != null ? _cam.transform : null;
            }
        }

        private static Hand HandOf(bool right)
        {
            RigManager rig = Rig;
            if (rig == null) return null;
            PhysicsRig phys = rig.physicsRig;
            if (phys == null) return null;
            return right ? phys.rightHand : phys.leftHand;
        }

        /// <summary>Object currently grabbed by a hand (null if none).</summary>
        public static GameObject Held(bool right)
        {
            Hand hand = HandOf(right);
            return hand != null ? hand.m_CurrentAttachedGO : null;
        }

        /// <summary>The transform of a hand (null if unavailable).</summary>
        public static Transform HandTransform(bool right)
        {
            Hand hand = HandOf(right);
            return hand != null ? hand.transform : null;
        }

        /// <summary>The controller for one hand (null if there is no rig).</summary>
        public static BaseController ControllerOf(bool right)
        {
            RigManager rig = Rig;
            if (rig == null) return null;
            ControllerRig cr = rig.controllerRig;
            if (cr == null) return null;
            return right ? cr.rightController : cr.leftController;
        }

        /// <summary>Thumbstick of a controller, (0,0) when there is no rig.</summary>
        public static Vector2 Stick(bool right)
        {
            BaseController c = ControllerOf(right);
            return c != null ? c.GetThumbStickAxis() : Vector2.zero;
        }

        public static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-6f ? Vector3.forward : v.normalized;
        }

        /// <summary>
        /// Shows a popup in the headset through BoneLib's notification system (looked up by reflection,
        /// so a BoneLib change can't break the build) and always writes the message to the log.
        /// </summary>
        public static void Notify(string message)
        {
            MelonLogger.Msg("[BoneCheats] " + message);
            try
            {
                Assembly bl = typeof(Player).Assembly;
                Type tNotification = bl.GetType("BoneLib.Notifications.Notification");
                Type tText = bl.GetType("BoneLib.Notifications.NotificationText");
                Type tNotifier = bl.GetType("BoneLib.Notifications.Notifier");
                if (tNotification == null || tText == null || tNotifier == null) return;

                object n = Activator.CreateInstance(tNotification);
                R.SetRaw(n, "Title", ToText(tText, "BoneCheats"));
                R.SetRaw(n, "Message", ToText(tText, message));
                R.SetRaw(n, "ShowPopup", true);
                R.SetRaw(n, "PopupLength", 3f);

                MethodInfo send = tNotifier.GetMethod("Send", BindingFlags.Public | BindingFlags.Static);
                if (send != null) send.Invoke(null, new[] { n });
            }
            catch { /* the log line above is enough */ }
        }

        private static object ToText(Type textType, string s)
        {
            foreach (MethodInfo m in textType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "op_Implicit" || m.ReturnType != textType) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return m.Invoke(null, new object[] { s });
            }
            return null;
        }
    }
}