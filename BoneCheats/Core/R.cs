using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace BoneCheats.Core
{
    /// <summary>Two small helpers: warn-once logging, and setting a property by name (BoneMenu's element label / BoneLib's notification).</summary>
    internal static class R
    {
        private static readonly HashSet<string> Warned = new HashSet<string>();

        public static void WarnOnce(string key, string msg)
        {
            if (Warned.Add(key)) MelonLogger.Warning(msg);
        }

        public static bool SetRaw(object o, string name, object value)
        {
            if (o == null) return false;
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                PropertyInfo p = o.GetType().GetProperty(name, bf);
                if (p != null && p.CanWrite) { p.SetValue(o, value); return true; }
                FieldInfo f = o.GetType().GetField(name, bf);
                if (f != null && !f.IsInitOnly) { f.SetValue(o, value); return true; }
            }
            catch { }
            return false;
        }
    }
}
