using System;
using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Fly, noclip, walk speed multiplier.
    ///
    /// Why the old noclip felt like jelly, and what this does instead:
    ///
    ///  1. It switched the rig's colliders OFF. A Rigidbody's mass distribution (centre of mass and inertia tensor) is
    ///     computed from the colliders attached to it, so with the colliders gone every limb/hand body suddenly had the
    ///     wrong inertia, and the joint drives that hold the arms to your controllers turned sluggish and wobbly. They
    ///     also changed again when the colliders came back. Now NO collider is touched: noclip only flips the physics
    ///     layer-collision matrix (Physics.IgnoreLayerCollision) between the layers your rig lives on and every other
    ///     layer, and puts it back exactly as it was when noclip stops. Raycasts / overlap queries (grabbing, laser
    ///     pointers, UI) don't use that matrix, so interaction keeps working.
    ///
    ///  2. It wrote rb.position on every body each physics step. That teleports the bodies away from the joint targets
    ///     that are driven by the controllers, so the drives fought the teleport every step (lag + wobble in the arms).
    ///     Now bodies are only given a velocity, and it is the SAME velocity for every body of the rig: no relative
    ///     motion between them, so the joints have nothing to correct, and nothing is ever teleported. Angular velocity
    ///     is left alone so arm/hand rotation stays under the game's control. There is no input smoothing.
    ///
    ///  3. Gravity is switched off per body only while flying and restored per body afterwards.
    /// </summary>
    internal sealed class MovementFeature : Feature
    {
        private const float BaseWalkSpeed = 3.5f;
        private const float Deadzone = 0.15f;
        private const float ScanIdle = 2f;           // seconds between rig body/layer rescans

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private readonly Dictionary<int, bool> _gravity = new Dictionary<int, bool>();
        private readonly HashSet<int> _rigLayers = new HashSet<int>();

        // collision-matrix pairs this feature changed: (layerA, layerB) -> value they had before
        private readonly Dictionary<int, bool> _matrixBackup = new Dictionary<int, bool>();

        private float _nextScan;
        private IntPtr _rigPtr = IntPtr.Zero;
        private bool _wasFlying;
        private bool _noclipActive;

        public override string Name => "Movement";

        protected override void Reset() { StopFly(); RestoreCollisions(); }

        /// <summary>
        /// Runs the instant Noclip/Fly is switched off in the menu (FeatureManager.ApplyAll fires on every
        /// settings change), instead of waiting for the next FixedTick to notice. Combined with Feature.cs's
        /// per-slot failure pausing, this means turning the toggle off can no longer be delayed by an
        /// unrelated FixedTick hiccup that happened to trip this feature's fixed-tick pause - Apply() has its
        /// own pause slot and always runs.
        /// </summary>
        protected override void Apply()
        {
            if (!Settings.Noclip.Value && _noclipActive) RestoreCollisions();
            if (!Settings.Fly.Value && !Settings.Noclip.Value && _wasFlying) StopFly();
        }

        protected override void FixedTick()
        {
            Component rig = Game.Rig;
            if (rig == null) return;

            bool noclip = Settings.Noclip.Value;
            bool fly = Settings.Fly.Value || noclip;
            float walk = Settings.WalkSpeedMult.Value;
            bool boost = Mathf.Abs(walk - 1f) > 0.01f;

            if (rig.Pointer != _rigPtr)
            {
                // new rig (level load / respawn / avatar swap): forget the old bodies; layers are global so they stay restorable
                _rigPtr = rig.Pointer;
                _bodies.Clear(); _gravity.Clear(); _rigLayers.Clear();
                _wasFlying = false;
                _nextScan = 0f;
            }

            if (!noclip && _noclipActive) RestoreCollisions();
            if (!fly && _wasFlying) StopFly();
            if (!fly && !boost) return;

            float now = Time.unscaledTime;
            if (now >= _nextScan || _bodies.Count == 0)
            {
                _nextScan = now + ScanIdle;
                ScanRig(rig);
            }
            if (noclip) EnsureNoclip();

            Transform head = Game.Head;
            if (head == null) return;
            Vector2 left = Game.Stick(false);

            if (fly)
            {
                _wasFlying = true;
                SetGravity(false);

                Vector3 want = Vector3.zero;
                if (left.magnitude > Deadzone) want += head.forward * left.y + head.right * left.x;
                Vector2 rs = Game.Stick(true);
                if (Mathf.Abs(rs.y) > Deadzone) want += Vector3.up * rs.y;
                want *= Settings.FlySpeed.Value;

                // same velocity on every body: they move as one, so the joints stay relaxed
                foreach (Rigidbody rb in _bodies)
                    if (rb != null) rb.velocity = want;
            }
            else if (left.magnitude > Deadzone)
            {
                Vector3 dir = Game.Flat(head.forward) * left.y + Game.Flat(head.right) * left.x;
                foreach (Rigidbody rb in _bodies)
                {
                    if (rb == null) continue;
                    Vector3 cur = rb.velocity;
                    rb.velocity = new Vector3(dir.x * BaseWalkSpeed * walk, cur.y, dir.z * BaseWalkSpeed * walk);
                }
            }
        }

        // ---------------- rig scan ----------------

        private void ScanRig(Component rig)
        {
            _bodies.Clear();
            _rigLayers.Clear();
            foreach (Rigidbody rb in rig.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null || rb.isKinematic) continue;
                _bodies.Add(rb);
                _rigLayers.Add(rb.gameObject.layer);
            }
            foreach (Collider c in rig.GetComponentsInChildren<Collider>(true))
                if (c != null && !c.isTrigger) _rigLayers.Add(c.gameObject.layer);
        }

        // ---------------- noclip: collision matrix ----------------

        private static int Key(int a, int b) { return a < b ? a * 32 + b : b * 32 + a; }

        /// <summary>Makes every rig layer ignore every other layer (not itself, so the body keeps its own joints/contacts).</summary>
        private void EnsureNoclip()
        {
            if (!_noclipActive)
                MelonLoader.MelonLogger.Msg("[Noclip] rig physics layers: " + string.Join(", ", _rigLayers) +
                                            " (ignoring collisions with every other layer until noclip is switched off)");
            _noclipActive = true;
            foreach (int a in _rigLayers)
            {
                for (int b = 0; b < 32; b++)
                {
                    if (b == a || _rigLayers.Contains(b)) continue;
                    int key = Key(a, b);
                    if (!_matrixBackup.ContainsKey(key)) _matrixBackup[key] = Physics.GetIgnoreLayerCollision(a, b);
                    if (!Physics.GetIgnoreLayerCollision(a, b)) Physics.IgnoreLayerCollision(a, b, true);
                }
            }
        }

        private void RestoreCollisions()
        {
            foreach (var kv in _matrixBackup)
            {
                int a = kv.Key / 32, b = kv.Key % 32;
                Physics.IgnoreLayerCollision(a, b, kv.Value);
            }
            _matrixBackup.Clear();
            _noclipActive = false;
        }

        // ---------------- fly: gravity ----------------

        private void SetGravity(bool on)
        {
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null) continue;
                int id = rb.GetInstanceID();
                if (!on)
                {
                    if (!_gravity.ContainsKey(id)) _gravity[id] = rb.useGravity;
                    rb.useGravity = false;
                }
                else if (_gravity.TryGetValue(id, out bool orig)) rb.useGravity = orig;
            }
            if (on) _gravity.Clear();
        }

        private void StopFly()
        {
            SetGravity(true);
            foreach (Rigidbody rb in _bodies) if (rb != null) rb.velocity = Vector3.zero;
            _wasFlying = false;
        }
    }
}