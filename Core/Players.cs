using System;
using System.Collections.Generic;
using BoneCheats.Features;
using LabFusion.Entities;
using MelonLoader;
using UnityEngine;

namespace BoneCheats.Core
{
    /// <summary>
    /// One other player (Fusion NetworkPlayer + its rig). Position comes from Fusion's RigRefs.Head, health
    /// from the pose Fusion receives (NetworkPlayer.RigPose.Health / MaxHealth), meshes are only gathered for charms.
    /// </summary>
    internal sealed class Plr
    {
        public readonly Component Rig;
        public readonly NetworkPlayer Fusion;
        public string Name = "";
        public Renderer[] Renderers = new Renderer[0];
        public float NextScan;
        private Transform _head;
        private int _frame = -1;
        private bool _culled;
        private float _hp = -1f;

        public Plr(Component rig, NetworkPlayer fusion)
        {
            Rig = rig;
            Fusion = fusion;
            Rescan();
        }

        public IntPtr Id { get { return Rig.Pointer; } }

        public bool Valid
        {
            get
            {
                try { return Rig != null && Rig.Pointer != IntPtr.Zero && Rig.gameObject != null && Rig.gameObject.activeInHierarchy; }
                catch { return false; }
            }
        }

        // Per-frame snapshot so ESP / aim / charms don't each re-read Fusion.
        private void Snapshot()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _culled = Fusion.IsCulled;

            RigPose pose = Fusion.RigPose;
            _hp = -1f;
            if (pose != null && pose.MaxHealth > 0f)
                _hp = Mathf.Clamp01(pose.Health / pose.MaxHealth);
        }

        /// <summary>0..1, or -1 when Fusion hasn't given a health value yet.</summary>
        public float HealthFraction() { Snapshot(); return _hp; }

        public bool Alive { get { float h = HealthFraction(); return h < 0f || h > 0.001f; } }

        /// <summary>False while Fusion has this player culled (frozen) and the anti-cull patch isn't active.</summary>
        public bool Live { get { Snapshot(); return !_culled || FusionCull.Active; } }

        public bool TryHead(out Vector3 head)
        {
            head = Vector3.zero;
            if (!Live) return false;
            if (_head == null) { RigRefs refs = Fusion.RigRefs; if (refs != null) _head = refs.Head; }
            if (_head == null) return false;
            head = _head.position;
            return true;
        }

        private static bool IsMesh(Renderer r)
        {
            return r.TryCast<SkinnedMeshRenderer>() != null || r.TryCast<MeshRenderer>() != null;
        }

        public void Rescan()
        {
            NextScan = Time.unscaledTime + 10f;
            _head = null;
            var rends = new List<Renderer>();
            foreach (Renderer r in Rig.transform.GetComponentsInChildren<Renderer>(true))
                if (r != null && IsMesh(r)) rends.Add(r);
            Renderers = rends.ToArray();
        }
    }

    /// <summary>All other players in the lobby, straight from Fusion's list.</summary>
    internal static class Players
    {
        public static readonly List<Plr> All = new List<Plr>();
        private static readonly Dictionary<IntPtr, Plr> Map = new Dictionary<IntPtr, Plr>();
        private static readonly HashSet<IntPtr> Present = new HashSet<IntPtr>();
        private static readonly List<IntPtr> Stale = new List<IntPtr>();
        private static float _next;
        private static int _lastCount = -1;

        public static bool Has(IntPtr id) { return Map.ContainsKey(id); }

        public static void Clear()
        {
            Map.Clear(); All.Clear(); Present.Clear();
            _next = 0f; _lastCount = -1;
            PlayerNames.Clear();
        }

        public static void Refresh(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < _next) return;
            _next = now + 0.5f;

            PlayerNames.Refresh(force);
            Present.Clear();
            foreach (FusionEntry e in PlayerNames.Entries)
            {
                IntPtr id = e.Rig.Pointer;
                if (id == IntPtr.Zero) continue;
                Present.Add(id);
                if (!Map.TryGetValue(id, out Plr p))
                {
                    try { Map[id] = p = new Plr(e.Rig, e.Np); }
                    catch (Exception ex) { R.WarnOnce("plr-new", "[Players] couldn't set up a player rig: " + ex.Message); continue; }
                }
                p.Name = e.Name ?? "Player";
            }

            Stale.Clear();
            foreach (var kv in Map) if (!Present.Contains(kv.Key) || !kv.Value.Valid) Stale.Add(kv.Key);
            foreach (IntPtr id in Stale) Map.Remove(id);

            int rescans = 0;          // mesh rescans are spread out: at most 2 per refresh
            All.Clear();
            foreach (Plr p in Map.Values)
            {
                if (now >= p.NextScan && rescans < 2) { p.Rescan(); rescans++; }
                All.Add(p);
            }

            if (All.Count != _lastCount)
            {
                _lastCount = All.Count;
                MelonLogger.Msg("[Players] tracking " + All.Count + " other player(s)");
            }
        }
    }
}
