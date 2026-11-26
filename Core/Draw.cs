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
