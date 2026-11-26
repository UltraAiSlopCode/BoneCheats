using System;
using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// ESP on other players, drawn in world space above the head (no distance limit):
    ///   - a name label with a soft drop shadow
    ///   - a health bar under it: rounded dark plate with a thin outline, a fill that shifts green -> yellow -> red,
    ///     a pale "damage trail" that drains behind the fill when health drops, and a small percentage readout.
    /// The player you're aiming at gets the target colour on the name and the bar outline.
    /// Built from the head position only, players behind you are skipped, and text/meshes are only touched when
    /// something actually changed.
    /// </summary>
    internal sealed class EspFeature : Feature
    {
        // ---- look & size (label-local units, multiplied by the distance scale below) ----
        private const float NameCharSize = 0.016f;      // was 0.012: roughly a third bigger, still compact
        private const float PctCharSize = 0.0085f;
        private const float BarWidth = 0.44f;
        private const float BarHeight = 0.04f;
        private const float BarBorder = 0.008f;
        private const float BarY = -0.036f;             // bar centre, just under the baseline of the name
        private const float BarRadius = 0.02f;

        private static readonly Color PlateColor = new Color(0.04f, 0.04f, 0.06f, 0.78f);
        private static readonly Color OutlineIdle = new Color(1f, 1f, 1f, 0.28f);
        private static readonly Color TrailColor = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color PctColor = new Color(1f, 1f, 1f, 0.92f);

        private sealed class Vis
        {
            public GameObject Label;
            public GameObject NameGo, BarGo;
            public TextMesh Text, Shadow, Pct;
            public Mesh OutlineMesh, PlateMesh, TrailMesh, FillMesh;
            public MeshRenderer TrailRenderer, FillRenderer;
            public string LastText;
            public Color LastColor;
            public int Frame;

            // health bar state
            public bool BarBuilt;
            public float Trail = 1f;
            public float BuiltFill = -1f, BuiltTrail = -1f;
            public bool BuiltTarget;
            public int LastPct = -1;
        }

        private readonly Dictionary<IntPtr, Vis> _vis = new Dictionary<IntPtr, Vis>();
        private readonly List<IntPtr> _drop = new List<IntPtr>();
        private static Font _font;
        private static Material _fontMat, _shadowMat;
        private static Material _outlineMat, _plateMat, _trailMat, _fillMat;
        private static bool _fontTried;
        private bool _wasOn;
        private int _frame;

        public override string Name => "ESP";

        protected override void Reset() { DestroyAll(); }

        protected override void LateTick()
        {
            bool names = Settings.EspNames.Value;
            bool health = Settings.EspHealth.Value;
            if (!Settings.Esp.Value || (!names && !health))
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
            float dt = Time.unscaledDeltaTime;

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
                bool isTarget = p.Id == CombatFeature.TargetId;
                DrawLabel(v, p, headPos, toHead, dist, isTarget ? target : normal, isTarget, names, health, dt);
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

        // ---------------- drawing ----------------

        private static void DrawLabel(Vis v, Plr p, Vector3 headPos, Vector3 toHead, float dist, Color c, bool isTarget,
                                      bool showName, bool showHealth, float dt)
        {
            if (v.Label == null) return;

            // Grows gently with distance so it stays readable but never gets huge.
            float scale = Mathf.Clamp(0.6f + dist * 0.05f, 0.65f, 3.4f);

            float hp = showHealth ? p.HealthFraction() : -1f;
            bool bar = showHealth && hp >= 0f && v.BarGo != null;

            // With a bar the label sits a bit higher so the bar clears the top of the head at any scale.
            float lift = bar ? 0.26f + 0.06f * scale : 0.32f;

            Transform t = v.Label.transform;
            t.position = headPos + Vector3.up * lift;
            t.rotation = Quaternion.LookRotation(toHead);
            t.localScale = Vector3.one * scale;

            // name
            if (v.NameGo != null && v.NameGo.activeSelf != showName) v.NameGo.SetActive(showName);
            if (showName)
            {
                string text = p.Name;
                if (text != v.LastText)
                {
                    v.Text.text = text;
                    if (v.Shadow != null) v.Shadow.text = text;
                    v.LastText = text;
                }
                if (c != v.LastColor) { v.Text.color = c; v.LastColor = c; }
            }

            // health bar
            if (v.BarGo != null && v.BarGo.activeSelf != bar) v.BarGo.SetActive(bar);
            if (bar) UpdateBar(v, hp, isTarget, dt);

            if (!v.Label.activeSelf) v.Label.SetActive(true);
        }

        private static void UpdateBar(Vis v, float hp, bool isTarget, float dt)
        {
            if (!v.BarBuilt)
            {
                v.Trail = hp;
                v.BuiltFill = v.BuiltTrail = -1f;
                v.BuiltTarget = !isTarget;                 // forces the outline to be built below
                Draw.BuildPill(v.PlateMesh, -BarWidth * 0.5f, BarWidth, BarHeight, BarRadius, PlateColor, PlateColor);
                v.BarBuilt = true;
            }

            // outline: only the colour changes with targeting
            if (isTarget != v.BuiltTarget)
            {
                Color o = isTarget ? Colors.Get(Settings.TargetCol.Value) : OutlineIdle;
                Draw.BuildPill(v.OutlineMesh, -BarWidth * 0.5f - BarBorder, BarWidth + BarBorder * 2f, BarHeight + BarBorder * 2f,
                               BarRadius + BarBorder, o, o);
                v.BuiltTarget = isTarget;
            }

            // trail: jumps up with healing, drains smoothly after damage
            if (hp >= v.Trail) v.Trail = hp;
            else v.Trail = Mathf.Lerp(v.Trail, hp, 1f - Mathf.Exp(-3f * dt));
            if (v.Trail - hp < 0.003f) v.Trail = hp;

            if (Mathf.Abs(hp - v.BuiltFill) > 0.004f)
            {
                BuildPart(v.FillMesh, v.FillRenderer, hp, FillColor(hp));
                v.BuiltFill = hp;
            }
            if (Mathf.Abs(v.Trail - v.BuiltTrail) > 0.004f)
            {
                BuildPart(v.TrailMesh, v.TrailRenderer, v.Trail, TrailColor);
                v.BuiltTrail = v.Trail;
            }

            int pct = Mathf.RoundToInt(hp * 100f);
            if (pct != v.LastPct && v.Pct != null) { v.Pct.text = pct + "%"; v.LastPct = pct; }
        }

        /// <summary>A bar segment from the left end of the plate, <paramref name="fraction"/> of the full width.</summary>
        private static void BuildPart(Mesh mesh, MeshRenderer mr, float fraction, Color baseColor)
        {
            float w = BarWidth * Mathf.Clamp01(fraction);
            bool visible = w > 0.004f;
            if (mr != null && mr.enabled != visible) mr.enabled = visible;
            if (!visible) return;

            Color top = Color.Lerp(baseColor, Color.white, 0.22f);      // soft gloss on the upper half
            top.a = baseColor.a;
            Draw.BuildPill(mesh, -BarWidth * 0.5f, w, BarHeight, BarRadius, top, baseColor);
        }

        /// <summary>Green at full health, yellow around the middle, red when low.</summary>
        private static Color FillColor(float hp)
        {
            Color green = new Color(0.22f, 0.88f, 0.42f, 1f);
            Color yellow = new Color(1f, 0.82f, 0.2f, 1f);
            Color red = new Color(1f, 0.25f, 0.25f, 1f);
            if (hp >= 0.5f) return Color.Lerp(yellow, green, Mathf.InverseLerp(0.5f, 0.85f, hp));
            return Color.Lerp(red, yellow, Mathf.InverseLerp(0.15f, 0.5f, hp));
        }

        // ---------------- objects ----------------

        private Vis GetVis(IntPtr id)
        {
            if (_vis.TryGetValue(id, out Vis v) && v.Label != null) return v;

            v = new Vis();
            try { CreateLabel(v); }
            catch (Exception e) { R.WarnOnce("esp-label", "[ESP] name label unavailable: " + e.Message); }
            if (v.Label == null) return null;
            try { CreateBar(v); }
            catch (Exception e) { R.WarnOnce("esp-bar", "[ESP] health bar unavailable: " + e.Message); }
            _vis[id] = v;
            return v;
        }

        private static TextMesh NewText(GameObject go, Material mat, float charSize, TextAnchor anchor)
        {
            TextMesh tm = go.AddComponent<TextMesh>();
            tm.font = _font;
            tm.fontSize = 64;
            tm.characterSize = charSize;         // small and crisp (high fontSize, low characterSize)
            tm.anchor = anchor;
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

            var root = new GameObject("BC_Label");
            root.transform.SetParent(Draw.Root, false);

            var nameGo = new GameObject("BC_Name");
            nameGo.transform.SetParent(root.transform, false);
            v.Text = NewText(nameGo, _fontMat, NameCharSize, TextAnchor.LowerCenter);

            var sh = new GameObject("BC_Shadow");
            sh.transform.SetParent(nameGo.transform, false);
            sh.transform.localPosition = new Vector3(0.016f, -0.016f, 0.004f);   // down-right, slightly behind
            v.Shadow = NewText(sh, _shadowMat, NameCharSize, TextAnchor.LowerCenter);
            v.Shadow.color = new Color(0f, 0f, 0f, 0.85f);

            root.SetActive(false);
            v.Label = root;
            v.NameGo = nameGo;
        }

        private static void CreateBar(Vis v)
        {
            if (_outlineMat == null)
            {
                // fixed draw order: outline, plate, trail, fill, then the percentage text (queue 5000) on top
                _outlineMat = Draw.NewFlatMaterial(4992);
                _plateMat = Draw.NewFlatMaterial(4994);
                _trailMat = Draw.NewFlatMaterial(4996);
                _fillMat = Draw.NewFlatMaterial(4998);
            }
            if (_outlineMat == null || _plateMat == null || _trailMat == null || _fillMat == null) return;

            var bar = new GameObject("BC_Bar");
            bar.transform.SetParent(v.Label.transform, false);
            bar.transform.localPosition = new Vector3(0f, BarY, 0.002f);

            MeshRenderer ignored;
            v.OutlineMesh = NewPart(bar.transform, "Outline", _outlineMat, out ignored);
            v.PlateMesh = NewPart(bar.transform, "Plate", _plateMat, out ignored);
            v.TrailMesh = NewPart(bar.transform, "Trail", _trailMat, out v.TrailRenderer);
            v.FillMesh = NewPart(bar.transform, "Fill", _fillMat, out v.FillRenderer);

            var pct = new GameObject("BC_Pct");
            pct.transform.SetParent(bar.transform, false);
            pct.transform.localPosition = new Vector3(BarWidth * 0.5f + BarBorder + 0.025f, 0f, 0f);
            v.Pct = NewText(pct, _fontMat, PctCharSize, TextAnchor.MiddleLeft);
            v.Pct.color = PctColor;

            bar.SetActive(false);
            v.BarGo = bar;
        }

        private static Mesh NewPart(Transform parent, string name, Material mat, out MeshRenderer renderer)
        {
            var go = new GameObject("BC_" + name);
            go.transform.SetParent(parent, false);
            var mesh = new Mesh();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return mesh;
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
            if (v.OutlineMesh != null) UnityEngine.Object.Destroy(v.OutlineMesh);
            if (v.PlateMesh != null) UnityEngine.Object.Destroy(v.PlateMesh);
            if (v.TrailMesh != null) UnityEngine.Object.Destroy(v.TrailMesh);
            if (v.FillMesh != null) UnityEngine.Object.Destroy(v.FillMesh);
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
