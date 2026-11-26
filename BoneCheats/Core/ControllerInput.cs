using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using BaseController = Il2CppSLZ.Marrow.BaseController;

namespace BoneCheats.Core
{
    /// <summary>
    /// Reads the Grip/Trigger state of a VR controller (Game.ControllerOf) for the Quick Toggle hotkey chord.
    /// Reached by reflection, same reasoning as AssetCatalog: Game.Stick already proves BaseController exposes
    /// button-ish accessors (GetThumbStickAxis), but the exact name of a bool "is grip held" method isn't
    /// something this file can confirm without a compiler/game install, so it searches for the closest match
    /// by name instead of hard-coding one and risking a straight compile failure if the name is slightly off.
    /// A held-state accessor is preferred over an edge ("...Down"/"...Up") one, because QuickToggleHotkeys
    /// does its own per-frame edge detection and polls this every tick either way.
    /// </summary>
    internal static class ControllerInput
    {
        private static readonly Dictionary<string, MethodInfo> Cache = new Dictionary<string, MethodInfo>();

        public static bool Grip(bool right) => Button(right, "Grip");
        public static bool Trigger(bool right) => Button(right, "Trigger");

        private static bool Button(bool right, string which)
        {
            BaseController c = Game.ControllerOf(right);
            if (c == null) return false;

            MethodInfo m = Find(c.GetType(), which);
            if (m == null) return false;
            try { return (bool)m.Invoke(c, null); }
            catch { return false; }
        }

        private static MethodInfo Find(Type t, string which)
        {
            string key = t.FullName + "::" + which;
            if (Cache.TryGetValue(key, out MethodInfo cached)) return cached;

            MethodInfo best = null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.ReturnType != typeof(bool) || m.GetParameters().Length != 0) continue;
                if (m.Name.IndexOf(which, StringComparison.OrdinalIgnoreCase) < 0) continue;

                bool isEdge = m.Name.IndexOf("Down", StringComparison.OrdinalIgnoreCase) >= 0
                           || m.Name.IndexOf("Up", StringComparison.OrdinalIgnoreCase) >= 0;
                if (best == null) { best = m; continue; }
                bool bestIsEdge = best.Name.IndexOf("Down", StringComparison.OrdinalIgnoreCase) >= 0
                               || best.Name.IndexOf("Up", StringComparison.OrdinalIgnoreCase) >= 0;
                if (bestIsEdge && !isEdge) best = m;   // prefer a plain held-state accessor if one turns up later
            }

            Cache[key] = best;
            if (best == null)
                R.WarnOnce("ctrlinput-" + key, "[QuickToggles] no '" + which + "' button accessor found on " + t.Name + " - the controller hotkey won't respond for it. Check Core/ControllerInput.cs.");
            return best;
        }
    }
}