using System;
using System.Collections.Generic;
using BoneCheats.Core;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using ArtRig = Il2CppSLZ.Marrow.ArtRig;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Features
{
    /// <summary>
    /// Skeleton ESP: a plain stick figure per player (head-neck-chest-hips, both arms, both legs, plus a short line
    /// hanging between the legs; no fingers, toes or head circle). Cheap on purpose:
    ///  - ONE LineRenderer per player. The skeleton is a tree, so a single line walks it and backtracks over the
    ///    shared joints (29 points); no extra objects, no per-bone GameObjects.
    ///  - Bone transforms are looked up once per player (and again only if the avatar was swapped), not every frame.
    ///  - The position array is reused, colour/width are only written when they change, and players behind you
    ///    or that Fusion has frozen are not touched at all.
    /// Bones come from the avatar's ArtRig (PhysicsRig.artOutput); if a bone is missing there, the avatar's
    /// humanoid Animator is used for that bone instead.
    /// </summary>
    internal sealed class SkeletonFeature : Feature
    {
        // Bone slots.
        private const int Head = 0, Neck = 1, Chest = 2, Hips = 3,
            ShL = 4, UaL = 5, LaL = 6, HandL = 7,
            ShR = 8, UaR = 9, LaR = 10, HandR = 11,
            UlL = 12, LlL = 13, FootL = 14,
            UlR = 15, LlR = 16, FootR = 17,
            BoneCount = 18;

        // Virtual points (not bones): computed per frame from the pelvis. Crotch = midpoint of the hip joints, Dong = the tip of the extra line.
        private const int Crotch = 18, Dong = 19;

        // One continuous line over the whole tree (backtracks over shared joints).
        private static readonly int[] Path =
        {
            Head, Neck,
            ShL, UaL, LaL, HandL, LaL, UaL, ShL, Neck,
            ShR, UaR, LaR, HandR, LaR, UaR, ShR, Neck,
            Chest, Hips,
            Crotch, Dong, Crotch, Hips,
            UlL, LlL, FootL, LlL, UlL, Hips,
            UlR, LlR, FootR,
        };

        private sealed class Vis
        {
            public LineRenderer Line;
            public readonly Transform[] Bones = new Transform[BoneCount];
            public readonly Vector3[] Pts = new Vector3[BoneCount];     // every bone position read once per frame
            public Il2CppStructArray<Vector3> Pos = new Il2CppStructArray<Vector3>(Path.Length);
            public bool HasBones;
            public float NextBuild;
            public Color LastColor;
            public float LastWidth;
            public int Frame;
        }

        private readonly Dictionary<IntPtr, Vis> _vis = new Dictionary<IntPtr, Vis>();
        private readonly List<IntPtr> _drop = new List<IntPtr>();
        private bool _wasOn;
        private int _frame;

        public override string Name => "Skeleton";

        protected override void Reset() { DestroyAll(); }

        protected override void LateTick()
        {
            if (!Settings.Esp.Value || !Settings.Skeleton.Value)
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
            float now = Time.unscaledTime;
            Color normal = Colors.Get(Settings.EspCol.Value);
            Color target = Colors.Get(Settings.TargetCol.Value);

            foreach (Plr p in Players.All)
            {
                if (!p.Valid || !p.TryHead(out Vector3 headPos)) continue;

                Vector3 toHead = headPos - eye;
                float dist = toHead.magnitude;
                if (dist < 0.05f) continue;
                if (Vector3.Dot(eyeFwd, toHead) < -0.3f * dist) continue;      // well behind you: nothing to draw

                Vis v = GetVis(p.Id);
                if (v == null) continue;

                try
                {
                    if (!v.HasBones || v.Bones[Head] == null || v.Bones[Hips] == null || v.Bones[FootL] == null)
                    {
                        v.HasBones = false;
                        if (now < v.NextBuild) continue;
                        v.NextBuild = now + 2f;                                // look the bones up at most every 2 s
                        v.HasBones = BuildBones(v, p);
                        if (!v.HasBones) continue;
                    }

                    // read each bone once (the old code read hips / legs several times per frame)
                    Vector3[] pts = v.Pts;
                    for (int i = 0; i < BoneCount; i++) pts[i] = v.Bones[i].position;

                    // the extra line between the legs: starts at the midpoint of the hip joints, hangs down and a bit forward
                    Vector3 hips = pts[Hips];
                    Vector3 crotch = (pts[UlL] + pts[UlR]) * 0.5f;
                    Vector3 down = (hips - pts[Chest]).normalized;
                    Vector3 side = pts[UlR] - pts[UlL];
                    Vector3 fwd = Vector3.Cross(side, -down).normalized;
                    float scale = Mathf.Max(0.3f, (pts[Neck] - hips).magnitude / 0.5f);   // follows avatar size
                    Vector3 dong = crotch + down * (0.10f * scale) + fwd * (0.08f * scale);

                    for (int i = 0; i < Path.Length; i++)
                    {
                        int b = Path[i];
                        v.Pos[i] = b == Crotch ? crotch : b == Dong ? dong : pts[b];
                    }
                    v.Line.SetPositions(v.Pos);

                    Color c = p.Id == CombatFeature.TargetId ? target : normal;
                    if (c != v.LastColor) { Draw.Style(v.Line, c, v.LastWidth); v.LastColor = c; }

                    float w = Mathf.Clamp(0.012f + dist * 0.0012f, 0.012f, 0.12f);     // stays visible at range
                    if (Mathf.Abs(w - v.LastWidth) > 0.002f) { v.Line.startWidth = w; v.Line.endWidth = w; v.LastWidth = w; }

                    if (!v.Line.enabled) v.Line.enabled = true;
                    v.Frame = _frame;
                }
                catch (Exception)
                {
                    v.HasBones = false;                                        // avatar was swapped/destroyed: rebuild next time
                }
            }

            // Anything not drawn this frame is hidden; lines of players that left are destroyed.
            _drop.Clear();
            foreach (var kv in _vis)
            {
                if (kv.Value.Frame == _frame) continue;
                Hide(kv.Value);
                if (!Players.Has(kv.Key)) _drop.Add(kv.Key);
            }
            foreach (IntPtr id in _drop) { Destroy(_vis[id]); _vis.Remove(id); }
        }

        // ---------------- bones ----------------

        private static bool BuildBones(Vis v, Plr p)
        {
            RigManager rig = p.Rig.TryCast<RigManager>();
            if (rig == null) return false;

            ArtRig art = rig.physicsRig != null ? rig.physicsRig.artOutput : null;
            Animator anim = rig.avatar != null ? rig.avatar.animator : null;
            if (art == null && anim == null) return false;

            Transform[] b = v.Bones;
            b[Head]  = Pick(art != null ? art.artHead : null, anim, HumanBodyBones.Head);
            b[Neck]  = Pick(art != null ? art.artNeck : null, anim, HumanBodyBones.Neck);
            b[Chest] = Pick(art != null ? art.artChest : null, anim, HumanBodyBones.Chest);
            b[Hips]  = Pick(art != null ? art.artHips : null, anim, HumanBodyBones.Hips);

            b[ShL]   = Pick(art != null ? art.artShoulderLf : null, anim, HumanBodyBones.LeftShoulder);
            b[UaL]   = Pick(art != null ? art.artUpperArmLf : null, anim, HumanBodyBones.LeftUpperArm);
            b[LaL]   = Pick(art != null ? art.artLowerArmLf : null, anim, HumanBodyBones.LeftLowerArm);
            b[HandL] = Pick(art != null ? art.artHandLf : null, anim, HumanBodyBones.LeftHand);

            b[ShR]   = Pick(art != null ? art.artShoulderRt : null, anim, HumanBodyBones.RightShoulder);
            b[UaR]   = Pick(art != null ? art.artUpperArmRt : null, anim, HumanBodyBones.RightUpperArm);
            b[LaR]   = Pick(art != null ? art.artLowerArmRt : null, anim, HumanBodyBones.RightLowerArm);
            b[HandR] = Pick(art != null ? art.artHandRt : null, anim, HumanBodyBones.RightHand);

            b[UlL]   = Pick(art != null ? art.artUpperLegLf : null, anim, HumanBodyBones.LeftUpperLeg);
            b[LlL]   = Pick(art != null ? art.artLowerLegLf : null, anim, HumanBodyBones.LeftLowerLeg);
            b[FootL] = Pick(art != null ? art.artFootLf : null, anim, HumanBodyBones.LeftFoot);

            b[UlR]   = Pick(art != null ? art.artUpperLegRt : null, anim, HumanBodyBones.RightUpperLeg);
            b[LlR]   = Pick(art != null ? art.artLowerLegRt : null, anim, HumanBodyBones.RightLowerLeg);
            b[FootR] = Pick(art != null ? art.artFootRt : null, anim, HumanBodyBones.RightFoot);

            for (int i = 0; i < BoneCount; i++) if (b[i] == null) return false;
            return true;
        }

        private static Transform Pick(Transform art, Animator anim, HumanBodyBones id)
        {
            if (art != null) return art;
            return anim != null ? anim.GetBoneTransform(id) : null;
        }

        // ---------------- objects ----------------

        private Vis GetVis(IntPtr id)
        {
            if (_vis.TryGetValue(id, out Vis v) && v.Line != null) return v;
            v = new Vis();
            v.Line = Draw.NewLine("BC_Skeleton", Path.Length);
            v.Line.enabled = false;
            v.LastWidth = -1f;
            _vis[id] = v;
            return v;
        }

        private static void Hide(Vis v)
        {
            if (v.Line != null && v.Line.enabled) v.Line.enabled = false;
        }

        private static void Destroy(Vis v)
        {
            if (v.Line != null) UnityEngine.Object.Destroy(v.Line.gameObject);
        }

        private void HideAll() { foreach (Vis v in _vis.Values) Hide(v); }

        private void DestroyAll()
        {
            foreach (Vis v in _vis.Values) Destroy(v);
            _vis.Clear();
        }
    }
}
