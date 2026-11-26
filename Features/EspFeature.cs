using System;
using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// ESP on other players: a small name label with a soft drop shadow above the head. No distance limit;
    /// the player you're aiming at is drawn in the target colour. Built from the head position only, players
    /// behind you are skipped, and text/objects are only touched when something changed.
    /// </summary>
    internal sealed class EspFeature : Feature
    {
        private sealed class Vis
        {
            public GameObject Label;
            public TextMesh Text, Shadow;
            public string LastText;
            public Color LastColor;
            public int Frame;
        }

        private readonly Dictionary<IntPtr, Vis> _vis = new Dictionary<IntPtr, Vis>();
        private readonly List<IntPtr> _drop = new List<IntPtr>();
        private static Font _font;
        private static Material _fontMat, _shadowMat;
        private static bool _fontTried;
        private bool _wasOn;
        private int _frame;

        public override string Name => "ESP";

        protected override void Reset() { DestroyAll(); }

        protected override void LateTick()
        {
            if (!Settings.Esp.Value || !Settings.EspNames.Value)
            {
                if (_wasOn) { HideAll(); _wasOn = false; }
                return;
            }
            _wasOn = true;
            _frame++;

            Transform head = Game.Head;
            if (head == null) return;
            Players.Refresh(false);

            Vector3 eye = head.position;
            Vector3 eyeFwd = head.forward;
            Color normal = Colors.Get(Settings.EspCol.Value);
            Color target = Colors.Get(Settings.TargetCol.Value);

            foreach (Plr p in Players.All)
            {
                if (!p.Valid || !p.TryHead(out Vector3 headPos)) continue;

                Vector3 toHead = headPos - eye;
                float dist = toHead.magnitude;
                if (dist < 0.05f) continue;
                if (Vector3.Dot(eyeFwd, toHead) < -0.3f * dist) continue;    // well behind you: nothing to draw

                Vis v = GetVis(p.Id);
                if (v == null) continue;
                v.Frame = _frame;
                DrawLabel(v, p, headPos, toHead, dist, p.Id == CombatFeature.TargetId ? target : normal);
            }

            // Anything not drawn this frame is hidden; visuals of players that left are destroyed.
            _drop.Clear();
            foreach (var kv in _vis)
            {
                if (kv.Value.Frame == _frame) continue;
                Hide(kv.Value);
                if (!Players.Has(kv.Key)) _drop.Add(kv.Key);
            }
            foreach (IntPtr id in _drop) { Destroy(_vis[id]); _vis.Remove(id); }
        }

        // ---------------- name label ----------------

        private static void DrawLabel(Vis v, Plr p, Vector3 headPos, Vector3 toHead, float dist, Color c)
        {
            if (v.Label == null) return;

            Transform t = v.Label.transform;
            t.position = headPos + Vector3.up * 0.32f;
            t.rotation = Quaternion.LookRotation(toHead);
            // Small up close, grows gently with distance so it stays readable but never gets huge.
            t.localScale = Vector3.one * Mathf.Clamp(0.45f + dist * 0.045f, 0.5f, 3f);

            string text = p.Name;
            if (text != v.LastText)
            {
                v.Text.text = text;
                if (v.Shadow != null) v.Shadow.text = text;
                v.LastText = text;
            }
            if (c != v.LastColor) { v.Text.color = c; v.LastColor = c; }
            if (!v.Label.activeSelf) v.Label.SetActive(true);
        }

        // ---------------- objects ----------------

        private Vis GetVis(IntPtr id)
        {
            if (_vis.TryGetValue(id, out Vis v) && v.Label != null) return v;

            v = new Vis();
            try { CreateLabel(v); }
            catch (Exception e) { R.WarnOnce("esp-label", "[ESP] name label unavailable: " + e.Message); }
            if (v.Label == null) return null;
            _vis[id] = v;
            return v;
        }

        private static TextMesh NewText(GameObject go, Material mat)
        {
            TextMesh tm = go.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 64;
            tm.characterSize = 0.012f;           // small and crisp (high fontSize, low characterSize)
            tm.anchor = TextAnchor.LowerCenter;
            tm.alignment = TextAlignment.Center;
            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = mat != null ? mat : _font.material;
            return tm;
        }

        private static void CreateLabel(Vis v)
        {
            if (!_fontTried)
            {
                _fontTried = true;
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (_font != null && _font.material != null)
                {
                    _fontMat = MakeFontMat(5000);
                    _shadowMat = MakeFontMat(4999);      // drawn just before the text so it sits behind it
                }
            }
            if (_font == null) return;

            var go = new GameObject("BC_Label");
            go.transform.SetParent(Draw.Root, false);
            v.Text = NewText(go, _fontMat);

            var sh = new GameObject("BC_Shadow");
            sh.transform.SetParent(go.transform, false);
            sh.transform.localPosition = new Vector3(0.012f, -0.012f, 0.004f);   // down-right, slightly behind
            v.Shadow = NewText(sh, _shadowMat);
            v.Shadow.color = new Color(0f, 0f, 0f, 0.85f);

            go.SetActive(false);
            v.Label = go;
        }

        private static Material MakeFontMat(int queue)
        {
            var m = new Material(_font.material);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            m.renderQueue = queue;
            if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 8);
            m.SetInt("unity_GUIZTestMode", 8);
            return m;
        }

        private static void Hide(Vis v)
        {
            if (v.Label != null && v.Label.activeSelf) v.Label.SetActive(false);
        }

        private static void Destroy(Vis v)
        {
            if (v.Label != null) UnityEngine.Object.Destroy(v.Label);
        }

        private void HideAll() { foreach (Vis v in _vis.Values) Hide(v); }

        private void DestroyAll()
        {
            foreach (Vis v in _vis.Values) Destroy(v);
            _vis.Clear();
        }
    }
}
