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
    /// highlights, sockets, grab pads, muzzle flashes and other transparent / effect meshes are skipped,
    /// which is what used to show up as a stray shape when your hands got near a gun.
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
        }

        private readonly Dictionary<int, Entry> _applied = new Dictionary<int, Entry>();
        private readonly Dictionary<int, KeyValuePair<Renderer, int>> _want = new Dictionary<int, KeyValuePair<Renderer, int>>();
        private readonly List<Renderer> _gunRends = new List<Renderer>();
        private readonly List<Renderer> _selfRends = new List<Renderer>();
        private readonly List<int> _tmp = new List<int>();
        private readonly Material[] _mats = new Material[4];
        private float _next, _nextGuns, _nextSelf, _nextCheck;
        private float _matsRetry;
        private string _lastReport = "";

        public override string Name => "Charms";

        protected override void Reset() { RestoreAll(); }

        private static bool Any()
        {
            return Settings.CharmSelf.Value || Settings.CharmPlayers.Value || Settings.CharmWeapons.Value;
        }

        protected override void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;

            if (!Any()) { if (_applied.Count > 0) RestoreAll(); return; }

            EnsureMaterials(now);
            if (_mats[0] == null) return;

            _want.Clear();
            if (Settings.CharmSelf.Value)
            {
                if (now >= _nextSelf) { _nextSelf = now + 3f; ScanSelf(); }
                foreach (Renderer r in _selfRends) Want(r, Self);
            }
            if (Settings.CharmPlayers.Value)
            {
                Players.Refresh(false);
                foreach (Plr p in Players.All)
                {
                    if (!p.Valid) continue;
                    int cat = p.Id == CombatFeature.TargetId ? Target : Player;
                    foreach (Renderer r in p.Renderers) Want(r, cat);
                }
            }
            if (Settings.CharmWeapons.Value)
            {
                if (now >= _nextGuns) { _nextGuns = now + 3f; ScanGuns(); }
                foreach (Renderer r in _gunRends) Want(r, Weapon);
            }

            Report();

            // release what's no longer wanted (or destroyed)
            _tmp.Clear();
            foreach (var kv in _applied)
                if (kv.Value.Rend == null || !_want.ContainsKey(kv.Key)) _tmp.Add(kv.Key);
            foreach (int id in _tmp) { Restore(_applied[id]); _applied.Remove(id); }

            // the game (or Fusion) may put materials back; re-check only every couple of seconds
            bool check = now >= _nextCheck;
            if (check) _nextCheck = now + 2f;

            foreach (var kv in _want)
            {
                Renderer r = kv.Value.Key;
                int cat = kv.Value.Value;
                if (r == null) continue;
                if (!_applied.TryGetValue(kv.Key, out Entry e))
                {
                    e = new Entry { Rend = r, Orig = r.sharedMaterials, Cat = cat };
                    _applied[kv.Key] = e;
                    SetMaterials(e);
                }
                else if (e.Cat != cat || (check && Overwritten(e)))
                {
                    e.Cat = cat;
                    SetMaterials(e);
                }
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

        private void SetColor(int cat, Color c, float a)
        {
            if (_mats[cat] == null) return;
            c.a = a;
            Draw.Tint(_mats[cat], c);
        }

        // ---------------- helpers ----------------

        private void EnsureMaterials(float now)
        {
            if (_mats[0] != null) return;
            if (now < _matsRetry) return;                            // build failed: don't spam, retry in a few seconds
            _matsRetry = now + 5f;

            RestoreAll();                                            // put originals back before the materials change
            for (int i = 0; i < _mats.Length; i++)
            {
                if (_mats[i] != null) UnityEngine.Object.Destroy(_mats[i]);
                _mats[i] = Draw.NewCharmMaterial();
            }
        }

        private void Report()
        {
            int s = 0, p = 0, w = 0;
            foreach (var kv in _want)
            {
                int c = kv.Value.Value;
                if (c == Self) s++; else if (c == Weapon) w++; else p++;
            }
            string r = s + "/" + p + "/" + w + "/" + Draw.CharmShaderName;
            if (r == _lastReport) return;
            _lastReport = r;
            MelonLogger.Msg("[Charms] charming " + s + " self, " + p + " player and " + w + " weapon meshes (shader " + Draw.CharmShaderName + ")");
        }

        private void ScanSelf()
        {
            _selfRends.Clear();
            Component rig = Game.Rig;
            if (rig == null) return;
            foreach (Renderer r in rig.GetComponentsInChildren<Renderer>(true))
                if (r != null) _selfRends.Add(r);
        }

        private void ScanGuns()
        {
            _gunRends.Clear();
            var found = UnityEngine.Object.FindObjectsOfType<Il2CppSLZ.Marrow.Gun>();
            if (found == null) return;
            foreach (Il2CppSLZ.Marrow.Gun gun in found)
            {
                if (gun == null) continue;
                foreach (Renderer r in gun.GetComponentsInChildren<Renderer>(true))
                    if (r != null) _gunRends.Add(r);
            }
        }

        private static bool LooksLikeEffect(Renderer r, Material first)
        {
            string n = r.gameObject.name.ToLowerInvariant();
            foreach (string w in EffectWords) if (n.Contains(w)) return true;
            if (first == null) return false;
            if (first.renderQueue >= 3000) return true;            // transparent / overlay materials are effects, not gun metal
            Shader sh = first.shader;
            string sn = sh != null ? sh.name.ToLowerInvariant() : "";
            foreach (string w in EffectWords) if (sn.Contains(w)) return true;
            return false;
        }

        private void Want(Renderer r, int cat)
        {
            if (r == null) return;
            if (!r.enabled || !r.gameObject.activeInHierarchy) return;   // hidden things are never charmed
            if (r.TryCast<SkinnedMeshRenderer>() == null && r.TryCast<MeshRenderer>() == null) return;   // skip particles, trails, lines
            int id = r.GetInstanceID();
            if (_want.ContainsKey(id)) return;                            // first category wins: self > players > weapons

            if (cat == Weapon)
            {
                Material first = _applied.TryGetValue(id, out Entry e) && e.Orig != null && e.Orig.Length > 0 ? e.Orig[0] : r.sharedMaterial;
                if (LooksLikeEffect(r, first)) return;
            }
            _want[id] = new KeyValuePair<Renderer, int>(r, cat);
        }

        private bool Overwritten(Entry e)
        {
            Material cur = e.Rend.sharedMaterial;
            Material mine = _mats[e.Cat];
            return cur == null || mine == null || cur.Pointer != mine.Pointer;
        }

        private void SetMaterials(Entry e)
        {
            Material m = _mats[e.Cat];
            if (m == null || e.Rend == null) return;
            int n = e.Orig != null && e.Orig.Length > 0 ? e.Orig.Length : 1;
            var arr = new Il2CppReferenceArray<Material>(n);
            for (int i = 0; i < n; i++) arr[i] = m;
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
