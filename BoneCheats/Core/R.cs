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

        /// <summary>Reads a property or field by name, public or not. Pass a Type for a static member, an
        /// instance for an instance member. Returns null on any failure (missing member, wrong binding,
        /// whatever) instead of throwing - callers treat null as "couldn't find it".</summary>
        public static object GetRaw(object o, string name)
        {
            if (o == null) return null;
            try
            {
                const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                Type t = o as Type;
                object target = t != null ? null : o;
                if (t == null) t = o.GetType();

                PropertyInfo p = t.GetProperty(name, bf);
                if (p != null) return p.GetValue(target);
                FieldInfo f = t.GetField(name, bf);
                if (f != null) return f.GetValue(target);
            }
            catch { }
            return null;
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
