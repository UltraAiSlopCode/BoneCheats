using System;
using System.Collections.Generic;
using BoneCheats.Features;
using LabFusion.Entities;
using MelonLoader;
using UnityEngine;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Core
{
    /// <summary>
    /// One other player (Fusion NetworkPlayer + its rig). Position comes from Fusion's RigRefs.Head (with rig-side
    /// fallbacks), health from the pose Fusion receives, meshes are only gathered when charms ask for them.
    ///
    /// Health is deliberately tolerant: a player is only treated as dead after we have SEEN them alive and the pose
    /// then reads 0. A pose that has never carried a usable value (what the lobby host can look like) reads as
    /// "unknown": the aimbot still targets them and ESP draws a grey "?" bar instead of nothing.
    /// </summary>
    internal sealed class Plr
    {
        public readonly Component Rig;
        public NetworkPlayer Fusion { get; private set; }
        public string Name = "";

        private Renderer[] _renderers = new Renderer[0];
        private bool _scanned;
        private float _nextScan;
        private IntPtr _avatarPtr;

        private Transform _head;
        private float _nextHeadTry;
        private RigManager _rm;

        private int _frame = -1;
        private bool _culled;

        private float _hp = -1f;            // -1 unknown, 0 confirmed dead, else 0..1
        private bool _dead;
        private bool _everAlive;
        private float _nextHealth;
        private float _nextRigHealth;
        private float _rigFrac = -1f;
        private float _nextLog;

        private bool _valid;
        private float _validUntil;

        public Plr(Component rig, NetworkPlayer fusion)
        {
            Rig = rig;
            Fusion = fusion;
        }

        public IntPtr Id { get { return Rig.Pointer; } }

        /// <summary>Called by Players.Refresh: Fusion may hand us a new NetworkPlayer object for the same rig.</summary>
        public void SetFusion(NetworkPlayer np)
        {
            if (np == null || ReferenceEquals(np, Fusion)) return;
            Fusion = np;
            _head = null;
            _frame = -1;
            _nextHealth = 0f;
        }

        // Cached for a quarter second: ESP, skeleton, charms and aim all ask every frame.
        public bool Valid
        {
            get
            {
                float now = Time.unscaledTime;
                if (now < _validUntil) return _valid;
                _validUntil = now + 0.25f;
                try { _valid = Rig != null && Rig.Pointer != IntPtr.Zero && Rig.gameObject != null && Rig.gameObject.activeInHierarchy; }
                catch { _valid = false; }
                return _valid;
            }
        }

        private RigManager RM
        {
            get
            {
                if (_rm == null || _rm.Pointer == IntPtr.Zero) _rm = Rig.TryCast<RigManager>();
                return _rm;
            }
        }

        // Per-frame snapshot so ESP / aim / charms don't each re-read Fusion.
        private void Snapshot()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _culled = Fusion.IsCulled;
            ReadHealth(Time.unscaledTime);
        }

        private void ReadHealth(float now)
        {
            if (now < _nextHealth) return;
            _nextHealth = now + 0.1f;

            float poseFrac = -1f, ph = 0f, pm = 0f;
            try
            {
                RigPose pose = Fusion.RigPose;
                if (pose != null)
                {
                    ph = pose.Health; pm = pose.MaxHealth;
                    if (pm > 0f) poseFrac = Mathf.Clamp01(ph / pm);
                }
            }
            catch { }

            if (now >= _nextRigHealth)
            {
                _nextRigHealth = now + 0.5f;
                _rigFrac = RigSideHealth();
            }

            if (poseFrac > 0.001f)
            {
                _hp = poseFrac; _dead = false; _everAlive = true;
            }
            else if (poseFrac >= 0f)
            {
                // pose reads 0: dead only if we watched them go from alive to 0, otherwise the value is just unset
                if (_everAlive) { _hp = 0f; _dead = true; }
                else { _hp = -1f; _dead = false; }
            }
            else
            {
                // pose has no health at all: use the rig's own Player_Health when it has something
                _hp = _rigFrac > 0.001f ? _rigFrac : -1f;
                _dead = false;
            }

            if (_hp < 0f && now >= _nextLog)
            {
                _nextLog = now + 30f;
                MelonLogger.Msg("[Health] " + Name + ": pose " + ph + "/" + pm + ", rig " + _rigFrac + " -> no usable value, showing '?'");
            }
        }

        // Remote rigs carry a Player_Health too; whether Fusion drives it varies, so it is only a fallback. -1 = unavailable.
        private float RigSideHealth()
        {
            try
            {
                RigManager rm = RM;
                if (rm == null) return -1f;
                var h = rm.health;
                if (h == null) return -1f;
                var ph = h.TryCast<Il2CppSLZ.Marrow.Player_Health>();
                if (ph == null) return -1f;
                float cur = ToFloat(R.GetRaw(ph, "curr_Health"));
                float max = ToFloat(R.GetRaw(ph, "max_Health"));
                if (cur < 0f || max <= 0f) return -1f;
                return Mathf.Clamp01(cur / max);
            }
            catch { return -1f; }
        }

        private static float ToFloat(object o)
        {
            if (o == null) return -1f;
            try { return Convert.ToSingle(o); } catch { return -1f; }
        }

        /// <summary>0..1, or -1 when no usable health value exists (draw it as unknown).</summary>
        public float HealthFraction() { Snapshot(); return _hp; }

        /// <summary>False only for a player confirmed dead.</summary>
        public bool Alive { get { Snapshot(); return !_dead; } }

        /// <summary>False while Fusion has this player culled (frozen) and the anti-cull patch isn't active.</summary>
        public bool Live { get { Snapshot(); return !_culled || FusionCull.Active; } }

        public bool TryHead(out Vector3 head)
        {
            head = Vector3.zero;
            if (!Live) return false;
            if (_head == null)
            {
                float now = Time.unscaledTime;
                if (now < _nextHeadTry) return false;
                _nextHeadTry = now + 0.5f;
                _head = FindHead();
            }
            if (_head == null) return false;
            head = _head.position;
            return true;
        }

        private Transform FindHead()
        {
            try { RigRefs refs = Fusion.RigRefs; if (refs != null && refs.Head != null) return refs.Head; } catch { }
            try
            {
                RigManager rm = RM;
                if (rm != null && rm.physicsRig != null)
                {
                    var art = rm.physicsRig.artOutput;
                    if (art != null && art.artHead != null) return art.artHead;
                    Transform t = R.GetRaw(rm.physicsRig, "m_head") as Transform;
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        internal static bool IsMesh(Renderer r)
        {
            return r.TryCast<SkinnedMeshRenderer>() != null || r.TryCast<MeshRenderer>() != null;
        }

        private IntPtr AvatarPtr()
        {
            try
            {
                RigManager rm = RM;
                if (rm == null) return IntPtr.Zero;
                var a = rm.avatar;
                return a != null ? a.Pointer : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Meshes for charms. Only scanned when something asks (charms on), when the avatar changes, or every 30 s.
        /// <paramref name="budget"/> caps how many players get scanned in one tick so enabling charms in a full
        /// lobby doesn't hitch; a player over budget keeps its old list and is scanned on the next tick.
        /// </summary>
        public Renderer[] GetRenderers(float now, ref int budget)
        {
            IntPtr av = AvatarPtr();
            bool need = !_scanned || av != _avatarPtr || now >= _nextScan;
            if (need && budget > 0)
            {
                budget--;
                Scan(av, now);
            }
            return _renderers;
        }

        private void Scan(IntPtr av, float now)
        {
            _scanned = true;
            _avatarPtr = av;
            _nextScan = now + 30f;
            _head = null;
            var rends = new List<Renderer>();
            foreach (Renderer r in Rig.transform.GetComponentsInChildren<Renderer>(true))
                if (r != null && IsMesh(r)) rends.Add(r);
            _renderers = rends.ToArray();
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
                else p.SetFusion(e.Np);
                p.Name = e.Name ?? "Player";
            }

            Stale.Clear();
            foreach (var kv in Map) if (!Present.Contains(kv.Key) || !kv.Value.Valid) Stale.Add(kv.Key);
            foreach (IntPtr id in Stale) Map.Remove(id);

            All.Clear();
            foreach (Plr p in Map.Values) All.Add(p);

            if (All.Count != _lastCount)
            {
                _lastCount = All.Count;
                MelonLogger.Msg("[Players] tracking " + All.Count + " other player(s)");
            }
        }
    }
}
