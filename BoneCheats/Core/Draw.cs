using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoneCheats.Core
{
    /// <summary>
    /// World-space line drawing for ESP and tracers. IMGUI doesn't render in VR, so everything is
    /// LineRenderers parented to one hidden root object that survives level loads.
    /// </summary>
    internal static class Draw
    {
        private static GameObject _root;
        private static Material _mat;
        private static bool _matTried;

        public static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject("BoneCheats_Draw");
                    UnityEngine.Object.DontDestroyOnLoad(_root);
                }
                return _root.transform;
            }
        }

        private static Material Mat()
        {
            if (_mat != null || _matTried) return _mat;
            _matTried = true;

            foreach (string name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "UI/Default" })
            {
                Shader s = Shader.Find(name);
                if (s == null) continue;
                _mat = new Material(s);
                MelonLogger.Msg("[Draw] line shader: " + name);
                break;
            }

            if (_mat == null)
            {
                MelonLogger.Warning("[Draw] No usable line shader found; ESP/tracers will be invisible.");
                return null;
            }

            _mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _mat.renderQueue = 5000;
            // Draw through walls when the shader exposes these (Internal-Colored does).
            if (_mat.HasProperty("_ZTest")) _mat.SetInt("_ZTest", 8);
            if (_mat.HasProperty("_ZWrite")) _mat.SetInt("_ZWrite", 0);
            if (_mat.HasProperty("_Cull")) _mat.SetInt("_Cull", 0);
            if (_mat.HasProperty("_Color")) _mat.SetColor("_Color", Color.white);
            return _mat;
        }

        /// <summary>Shaders tried for charms, in order: the first one this game has is used (no setting needed).</summary>
        public static readonly string[] CharmShaders =
        {
            "Hidden/Internal-Colored",      // the one ESP lines already render with, so it is known to work in this game
            "UI/Default",
            "Sprites/Default",
            "Unlit/Color",
            "Universal Render Pipeline/Unlit",
        };

        public static string CharmShaderName;

        /// <summary>
        /// A flat colour material for body charms using the first shader from <see cref="CharmShaders"/> that this
        /// game contains. See-through-walls where the shader supports it (_ZTest / unity_GUIZTestMode).
        /// </summary>
        public static Material NewCharmMaterial()
        {
            foreach (string name in CharmShaders)
            {
                Shader s = Shader.Find(name);
                if (s == null) continue;
                var m = new Material(s);
                m.hideFlags = HideFlags.DontUnloadUnusedAsset;
                m.renderQueue = 4000;
                m.SetInt("unity_GUIZTestMode", 8);                          // UI/Default: always pass
                if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 8);        // Internal-Colored
                if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
                if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                CharmShaderName = name;
                MelonLogger.Msg("[Draw] charm shader: " + name);
                return m;
            }
            R.WarnOnce("charm-noshader", "[Draw] No usable charm shader found; charms are unavailable.");
            return null;
        }

        /// <summary>Tint a charm material (the colour property differs per shader).</summary>
        public static void Tint(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
        }

        // ---------------- flat UI shapes (health bar) ----------------

        /// <summary>
        /// A flat, vertex-coloured, see-through-walls material for UI-style shapes. Each layer of a shape gets its own
        /// render queue so the draw order is fixed (higher queue = drawn later = on top).
        /// </summary>
        public static Material NewFlatMaterial(int queue)
        {
            foreach (string name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "UI/Default" })
            {
                Shader s = Shader.Find(name);
                if (s == null) continue;
                var m = new Material(s);
                m.hideFlags = HideFlags.DontUnloadUnusedAsset;
                m.renderQueue = queue;
                m.SetInt("unity_GUIZTestMode", 8);
                if (m.HasProperty("_ZTest")) m.SetInt("_ZTest", 8);
                if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
                if (m.HasProperty("_Cull")) m.SetInt("_Cull", 0);
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
                return m;
            }
            return null;
        }

        private const int CornerSegments = 6;

        /// <summary>
        /// Fills <paramref name="mesh"/> with a rounded rectangle ("pill") in the XY plane, centred vertically on y = 0 and
        /// starting at x = <paramref name="left"/>. The colour fades from <paramref name="bottom"/> to <paramref name="top"/>
        /// (a soft vertical gradient). The corner radius shrinks automatically when the shape is very short or narrow.
        /// </summary>
        public static void BuildPill(Mesh mesh, float left, float width, float height, float radius, Color top, Color bottom)
        {
            float hh = height * 0.5f;
            float r = Mathf.Max(0.0001f, Mathf.Min(radius, Mathf.Min(width * 0.5f, hh)));
            float right = left + width;

            // corner centres, counter-clockwise from the bottom-right corner
            float[] cx = { right - r, right - r, left + r, left + r };
            float[] cy = { -hh + r, hh - r, hh - r, -hh + r };
            float[] a0 = { -90f, 0f, 90f, 180f };

            int perCorner = CornerSegments + 1;
            int n = 4 * perCorner;
            var verts = new Vector3[n + 1];
            var cols = new Color[n + 1];
            var tris = new int[n * 3];

            int k = 0;
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i <= CornerSegments; i++)
                {
                    float a = (a0[c] + 90f * i / CornerSegments) * Mathf.Deg2Rad;
                    float x = cx[c] + Mathf.Cos(a) * r;
                    float y = cy[c] + Mathf.Sin(a) * r;
                    verts[k] = new Vector3(x, y, 0f);
                    cols[k] = Color.Lerp(bottom, top, Mathf.InverseLerp(-hh, hh, y));
                    k++;
                }
            }
            verts[n] = new Vector3(left + width * 0.5f, 0f, 0f);
            cols[n] = Color.Lerp(bottom, top, 0.5f);

            for (int i = 0; i < n; i++)
            {
                tris[i * 3] = n;
                tris[i * 3 + 1] = i;
                tris[i * 3 + 2] = (i + 1) % n;
            }

            mesh.Clear();
            mesh.vertices = new Il2CppStructArray<Vector3>(verts);
            mesh.colors = new Il2CppStructArray<Color>(cols);
            mesh.triangles = new Il2CppStructArray<int>(tris);
            mesh.RecalculateBounds();
        }

        public static LineRenderer NewLine(string name, int positions)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root, false);
            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = positions;
            lr.numCapVertices = 0;
            lr.numCornerVertices = 0;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            Material m = Mat();
            if (m != null) lr.sharedMaterial = m;
            Style(lr, Color.white, 0.01f);
            return lr;
        }

        public static void Style(LineRenderer lr, Color c, float width)
        {
            lr.startColor = c;
            lr.endColor = c;
            lr.startWidth = width;
            lr.endWidth = width;
        }
    }
}
