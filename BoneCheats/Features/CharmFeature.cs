using System;
using System.Collections.Generic;
using BoneCheats.Core;
using MelonLoader;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Full-body charms: every mesh on yourself, other players and weapons is swapped for a flat,
    /// see-through-walls colour material. The player you're targeting gets the target colour.
    /// Original materials are remembered and put back when a charm is switched off, the object goes
    /// away, or the mod resets.
    ///
    /// Weapons: only meshes that are on right now and look like solid gun parts are charmed. Hover
    /// highlights, sockets, grab pads, muzzle flashes and other transparent / effect meshes are skipped.
    ///
    /// Performance notes (this used to cost a lot of frame time):
    ///  - the expensive scans (whole self rig, FindObjectsOfType&lt;Gun&gt;, per-player renderer lists) run rarely and
    ///    only when something changed (new rig / avatar / gun), with a per-tick budget so they never pile up;
    ///  - "does this gun mesh look like an effect" is decided once per renderer, not every tick;
    ///  - no per-tick dictionaries, strings or material arrays are allocated: entries are stamped, material arrays
    ///    are shared per category/length, and colours are only pushed to the materials when they change.
    /// </summary>
    internal sealed class CharmFeature : Feature
    {
        private const int Self = 0, Player = 1, Weapon = 2, Target = 3;

        private static readonly string[] EffectWords =
        {
            "highlight", "hover", "outline", "glow", "select", "grab", "socket", "pad", "trail", "flash", "muzzle",
            "smoke", "fx", "hint", "hologram", "fresnel", "additive", "particle", "decal", "laser", "beam", "canvas",
        };

        private sealed class Entry
        {
            public Renderer Rend;
            public Il2CppReferenceArray<Material> Orig;
            public int Cat;
            public int Stamp;
        }

        private sealed class GunRends { public Renderer[] R; public float Expire; }

        private readonly Dictionary<int, Entry> _applied = new Dictionary<int, Entry>();
        private readonly List<Renderer> _gunRends = new List<Renderer>();
        private readonly List<Renderer> _selfRends = new List<Renderer>();
        private readonly Dictionary<IntPtr, GunRends> _gunCache = new Dictionary<IntPtr, GunRends>();
        private readonly HashSet<IntPtr> _gunSeen = new HashSet<IntPtr>();
        private readonly List<IntPtr> _gunDrop = new List<IntPtr>();
        private readonly Dictionary<int, bool> _effect = new Dictionary<int, bool>();
        private readonly Dictionary<int, Il2CppReferenceArray<Material>> _arrays = new Dictionary<int, Il2CppReferenceArray<Material>>();
        private readonly List<int> _tmp = new List<int>();
        private readonly Material[] _mats = new Material[4];
        private readonly Color[] _lastColor = new Color[4];
        private readonly bool[] _colorSet = new bool[4];

        private float _next, _nextGuns, _nextSelf, _nextCheck, _matsRetry;
        private int _stamp;
        private bool _check;
        private IntPtr _selfRig, _selfAvatar;
        private int _cs, _cp, _cw, _lastS = -1, _lastP = -1, _lastW = -1;

        public override string Name => "Charms";

        protected override void Reset() { RestoreAll(); ClearScans(); }

        private static bool Any()
        {
            return Settings.CharmSelf.Value || Settings.CharmPlayers.Value || Settings.CharmWeapons.Value;
        }

        private void ClearScans()
        {
            _selfRends.Clear(); _gunRends.Clear(); _gunCache.Clear(); _effect.Clear();
            _selfRig = _selfAvatar = IntPtr.Zero;
            _nextSelf = _nextGuns = 0f;
        }

        protected override void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;

            if (!Any()) { if (_applied.Count > 0) RestoreAll(); if (_gunCache.Count > 0 || _selfRends.Count > 0) ClearScans(); return; }

            EnsureMaterials(now);
            if (_mats[0] == null) return;

            _check = now >= _nextCheck;                 // the game (or Fusion) may put materials back; re-check every couple of seconds
            if (_check) _nextCheck = now + 2f;

            _stamp++;
            _cs = _cp = _cw = 0;

            if (Settings.CharmSelf.Value)
            {
                ScanSelfIfNeeded(now);
                for (int i = 0; i < _selfRends.Count; i++) Want(_selfRends[i], Self);
            }
            else if (_selfRends.Count > 0) { _selfRends.Clear(); _selfRig = _selfAvatar = IntPtr.Zero; }

            if (Settings.CharmPlayers.Value)
            {
                Players.Refresh(false);
                int budget = 2;                          // at most 2 players get their meshes (re)scanned per tick
                foreach (Plr p in Players.All)
                {
                    if (!p.Valid) continue;
                    int cat = p.Id == CombatFeature.TargetId ? Target : Player;
                    Renderer[] rs = p.GetRenderers(now, ref budget);
                    for (int i = 0; i < rs.Length; i++) Want(rs[i], cat);
                }
            }

            if (Settings.CharmWeapons.Value)
            {
                if (now >= _nextGuns) { _nextGuns = now + 5f; ScanGuns(now); }
                for (int i = 0; i < _gunRends.Count; i++) Want(_gunRends[i], Weapon);
            }
            else if (_gunCache.Count > 0) { _gunCache.Clear(); _gunRends.Clear(); _nextGuns = 0f; }

            Report();

            // release what's no longer wanted this tick (hidden, gone, switched off)
            _tmp.Clear();
            foreach (var kv in _applied)
                if (kv.Value.Rend == null || kv.Value.Stamp != _stamp) _tmp.Add(kv.Key);
            for (int i = 0; i < _tmp.Count; i++)
            {
                int id = _tmp[i];
                Restore(_applied[id]);
                _applied.Remove(id);
            }
        }

        protected override void LateTick()
        {
            if (_applied.Count == 0) return;
            float a = Mathf.Clamp01(Settings.CharmAlpha.Value);
            SetColor(Self, Colors.Get(Settings.CharmSelfCol.Value), a);
            SetColor(Player, Colors.Get(Settings.CharmPlayerCol.Value), a);
            SetColor(Weapon, Colors.Get(Settings.CharmWeaponCol.Value), a);
            SetColor(Target, Colors.Get(Settings.TargetCol.Value), a);
        }

        // Only touches the material when the colour actually changed (static colours cost nothing per frame).
        private void SetColor(int cat, Color c, float a)
        {
            if (_mats[cat] == null) return;
            c.a = a;
            if (_colorSet[cat] && _lastColor[cat] == c) return;
            _lastColor[cat] = c;
            _colorSet[cat] = true;
            Draw.Tint(_mats[cat], c);
        }

        // ---------------- helpers ----------------

        private void EnsureMaterials(float now)
        {
            if (_mats[0] != null) return;
            if (now < _matsRetry) return;                            // build failed: don't spam, retry in a few seconds
            _matsRetry = now + 5f;

            RestoreAll();                                            // put originals back before the materials change
            _arrays.Clear();
            for (int i = 0; i < _mats.Length; i++)
            {
                if (_mats[i] != null) UnityEngine.Object.Destroy(_mats[i]);
                _mats[i] = Draw.NewCharmMaterial();
                _colorSet[i] = false;
            }
        }

        private void Report()
        {
            if (_cs == _lastS && _cp == _lastP && _cw == _lastW) return;
            _lastS = _cs; _lastP = _cp; _lastW = _cw;
            MelonLogger.Msg("[Charms] charming " + _cs + " self, " + _cp + " player and " + _cw + " weapon meshes (shader " + Draw.CharmShaderName + ")");
        }

        // Own rig: rescanned only when the rig or avatar changed (or every 20 s as a safety net).
        private void ScanSelfIfNeeded(float now)
        {
            var rig = Game.Rig;
            if (rig == null)
            {
                if (_selfRends.Count > 0) _selfRends.Clear();
                _selfRig = _selfAvatar = IntPtr.Zero;
                return;
            }

            IntPtr avp = IntPtr.Zero;
            try { var av = rig.avatar; if (av != null) avp = av.Pointer; } catch { }

            if (rig.Pointer == _selfRig && avp == _selfAvatar && now < _nextSelf) return;
            _selfRig = rig.Pointer;
            _selfAvatar = avp;
            _nextSelf = now + 20f;

            _selfRends.Clear();
            foreach (Renderer r in rig.GetComponentsInChildren<Renderer>(true))
                if (r != null && Plr.IsMesh(r)) _selfRends.Add(r);
        }

        // Guns: ONE FindObjectsOfType every 5 s, but renderer lists are only built for guns we haven't seen
        // (and refreshed every 20 s per gun), and the effect/solid decision is made once per renderer.
        private void ScanGuns(float now)
        {
            var found = UnityEngine.Object.FindObjectsOfType<Il2CppSLZ.Marrow.Gun>();
            _gunSeen.Clear();
            if (found != null)
            {
                foreach (Il2CppSLZ.Marrow.Gun gun in found)
                {
                    if (gun == null) continue;
                    IntPtr id = gun.Pointer;
                    _gunSeen.Add(id);
                    if (!_gunCache.TryGetValue(id, out GunRends g) || now >= g.Expire)
                        _gunCache[id] = BuildGunRends(gun, now);
                }
            }

            _gunDrop.Clear();
            foreach (var kv in _gunCache) if (!_gunSeen.Contains(kv.Key)) _gunDrop.Add(kv.Key);
            for (int i = 0; i < _gunDrop.Count; i++) _gunCache.Remove(_gunDrop[i]);

            _gunRends.Clear();
            foreach (GunRends g in _gunCache.Values) _gunRends.AddRange(g.R);
            if (_effect.Count > 2000) _effect.Clear();
        }

        private GunRends BuildGunRends(Il2CppSLZ.Marrow.Gun gun, float now)
        {
            var list = new List<Renderer>();
            foreach (Renderer r in gun.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !Plr.IsMesh(r)) continue;
                int id = r.GetInstanceID();
                if (!_effect.TryGetValue(id, out bool fx))
                {
                    Material first = _applied.TryGetValue(id, out Entry e) && e.Orig != null && e.Orig.Length > 0 ? e.Orig[0] : r.sharedMaterial;
                    fx = LooksLikeEffect(r, first);
                    _effect[id] = fx;
                }
                if (!fx) list.Add(r);
            }
            return new GunRends { R = list.ToArray(), Expire = now + 20f };
        }

        private static bool Contains(string s, string word)
        {
            return s.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;     // no ToLower allocation
        }

        private static bool LooksLikeEffect(Renderer r, Material first)
        {
            string n = r.gameObject.name;
            foreach (string w in EffectWords) if (Contains(n, w)) return true;
            if (first == null) return false;
            if (first.renderQueue >= 3000) return true;            // transparent / overlay materials are effects, not gun metal
            Shader sh = first.shader;
            string sn = sh != null ? sh.name : "";
            foreach (string w in EffectWords) if (Contains(sn, w)) return true;
            return false;
        }

        private void Want(Renderer r, int cat)
        {
            if (r == null) return;
            int id = r.GetInstanceID();
            Entry e;
            bool have = _applied.TryGetValue(id, out e);
            if (have && e.Stamp == _stamp) return;                       // already claimed this tick: self > players > weapons
            if (!r.enabled || !r.gameObject.activeInHierarchy) return;   // hidden things are never charmed

            if (!have)
            {
                e = new Entry { Rend = r, Orig = r.sharedMaterials, Cat = cat, Stamp = _stamp };
                _applied[id] = e;
                SetMaterials(e);
            }
            else
            {
                e.Stamp = _stamp;
                if (e.Cat != cat) { e.Cat = cat; SetMaterials(e); }
                else if (_check && Overwritten(e)) SetMaterials(e);
            }

            if (cat == Self) _cs++; else if (cat == Weapon) _cw++; else _cp++;
        }

        private bool Overwritten(Entry e)
        {
            Material cur = e.Rend.sharedMaterial;
            Material mine = _mats[e.Cat];
            return cur == null || mine == null || cur.Pointer != mine.Pointer;
        }

        // One shared array per (category, material count): sharedMaterials copies it, so entries can reuse it.
        private void SetMaterials(Entry e)
        {
            Material m = _mats[e.Cat];
            if (m == null || e.Rend == null) return;
            int n = e.Orig != null && e.Orig.Length > 0 ? e.Orig.Length : 1;
            int key = e.Cat * 256 + n;
            if (!_arrays.TryGetValue(key, out Il2CppReferenceArray<Material> arr) || arr == null)
            {
                arr = new Il2CppReferenceArray<Material>(n);
                for (int i = 0; i < n; i++) arr[i] = m;
                _arrays[key] = arr;
            }
            e.Rend.sharedMaterials = arr;
        }

        private static void Restore(Entry e)
        {
            if (e.Rend == null || e.Orig == null) return;
            try { e.Rend.sharedMaterials = e.Orig; } catch { }
        }

        private void RestoreAll()
        {
            foreach (Entry e in _applied.Values) Restore(e);
            _applied.Clear();
        }
    }
}
