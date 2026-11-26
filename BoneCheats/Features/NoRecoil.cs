using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using Gun = Il2CppSLZ.Marrow.Gun;
using InteractableHost = Il2CppSLZ.Marrow.InteractableHost;

namespace BoneCheats.Features
{
    /// <summary>
    /// No recoil for the gun in your hand. Runs entirely inside the Gun.Fire() hook (GunHooks), so it only ever touches
    /// the gun that is actually firing and has nothing to clean up later:
    ///
    ///   Begin() - just before Fire(): every kick/recoil value on that Gun is zeroed (the originals are kept in the
    ///             returned state), so the game has no kick to apply, and the gun body's velocity is noted.
    ///   End()   - right after Fire(): the originals are written back and the gun body's velocity is put back to what
    ///             it was, which also removes any instant impulse the shot may still have applied.
    ///
    /// There is nothing left running between shots: no per-frame restoring, no state kept on the gun, no timers.
    /// Which fields are zeroed is decided by name (anything float named *kick* or *recoil* on the Gun type) and logged
    /// once per type, so the MelonLoader log shows exactly what was cut: "[No Recoil] zeroing on Gun: ...".
    /// </summary>
    internal static class NoRecoil
    {
        public sealed class State
        {
            public Gun Gun;
            public PropertyInfo[] Props;
            public float[] Originals;
            public Rigidbody Body;
            public Vector3 Vel, Ang;
        }

        private static readonly Dictionary<Type, PropertyInfo[]> Cache = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo[] KickProps(Type t)
        {
            if (Cache.TryGetValue(t, out PropertyInfo[] cached)) return cached;

            var list = new List<PropertyInfo>();
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (pi.PropertyType != typeof(float) || !pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length != 0) continue;
                string n = pi.Name.ToLowerInvariant();
                if (n.Contains("kick") || n.Contains("recoil")) list.Add(pi);
            }

            PropertyInfo[] arr = list.ToArray();
            Cache[t] = arr;
            MelonLogger.Msg("[No Recoil] zeroing on " + t.Name + ": " +
                            (arr.Length == 0 ? "(nothing found - please send this log line)" : string.Join(", ", Array.ConvertAll(arr, x => x.Name))));
            return arr;
        }

        public static State Begin(Gun gun)
        {
            PropertyInfo[] props = KickProps(gun.GetType());
            var st = new State { Gun = gun, Props = props, Originals = new float[props.Length] };

            int done = 0;
            try
            {
                for (; done < props.Length; done++)
                {
                    st.Originals[done] = (float)props[done].GetValue(gun);
                    props[done].SetValue(gun, 0f);
                }
            }
            catch
            {
                // half-zeroed: put back what was changed so the gun is never left with a broken value
                for (int i = 0; i < done; i++) { try { props[i].SetValue(gun, st.Originals[i]); } catch { } }
                throw;
            }

            InteractableHost host = gun.host;
            Rigidbody rb = host != null ? host.Rb : null;
            if (rb != null)
            {
                st.Body = rb;
                st.Vel = rb.velocity;
                st.Ang = rb.angularVelocity;
            }
            return st;
        }

        public static void End(State st)
        {
            if (st == null) return;

            try
            {
                if (st.Gun != null)
                    for (int i = 0; i < st.Props.Length; i++) st.Props[i].SetValue(st.Gun, st.Originals[i]);
            }
            finally
            {
                // Done even if restoring a field failed, so the gun never keeps a velocity kick from this shot.
                if (st.Body != null)
                {
                    st.Body.velocity = st.Vel;
                    st.Body.angularVelocity = st.Ang;
                }
            }
        }
    }
}
