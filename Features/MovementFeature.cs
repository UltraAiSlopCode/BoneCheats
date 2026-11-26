using System;
using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Fly, noclip, walk speed multiplier.
    ///
    /// Noclip "jelly" fix: the old version set the same velocity on every rig rigidbody and low-passed it,
    /// so the joints/spring drives fought each other (wobble) and the smoothing made input lag. Now the
    /// whole rig is translated by one identical delta per physics step (all bodies keep their relative
    /// offsets, so the joints have nothing to correct), velocities are zeroed, gravity is off, and there is
    /// no input smoothing. Colliders are re-scanned often so ones the game re-enables can't catch you, and
    /// every collider/gravity flag is restored exactly when fly or noclip stops.
    /// </summary>
    internal sealed class MovementFeature : Feature
    {
        private const float BaseWalkSpeed = 3.5f;
        private const float Deadzone = 0.15f;

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private readonly Dictionary<int, bool> _gravity = new Dictionary<int, bool>();
        private readonly List<Collider> _colliders = new List<Collider>();
        private readonly List<Collider> _disabled = new List<Collider>();
        private float _nextScan;
        private IntPtr _rigPtr = IntPtr.Zero;
        private bool _wasFlying;

        public override string Name => "Movement";

        protected override void Reset() { StopFly(); RestoreColliders(); }

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
                _rigPtr = rig.Pointer;
                _bodies.Clear(); _gravity.Clear(); _colliders.Clear(); _disabled.Clear();
                _wasFlying = false;
                _nextScan = 0f;
            }

            if (!noclip && _disabled.Count > 0) RestoreColliders();
            if (!fly && _wasFlying) StopFly();
            if (!fly && !boost) return;

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                // bodies/colliders rarely change; colliders are re-checked faster while noclip is on
                _nextScan = now + (noclip ? 0.25f : 2f);
                if (_bodies.Count == 0 || !noclip) ScanBodies(rig);
                if (noclip) DisableColliders(rig);
            }

            Transform head = Game.Head;
            if (head == null) return;
            Vector2 left = Game.Stick(false);
            float dt = Time.fixedDeltaTime;

            if (fly)
            {
                _wasFlying = true;
                SetGravity(false);

                Vector3 want = Vector3.zero;
                if (left.magnitude > Deadzone) want += head.forward * left.y + head.right * left.x;
                Vector2 rs = Game.Stick(true);
                if (Mathf.Abs(rs.y) > Deadzone) want += Vector3.up * rs.y;
                want *= Settings.FlySpeed.Value;

                // one identical step for every body: no relative motion, so no jelly
                Vector3 step = want * dt;
                foreach (Rigidbody rb in _bodies)
                {
                    if (rb == null) continue;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    if (step.sqrMagnitude > 0f) rb.position += step;
                }
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
            foreach (Rigidbody rb in _bodies) if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            _wasFlying = false;
        }

        private void ScanBodies(Component rig)
        {
            _bodies.Clear();
            foreach (Rigidbody rb in rig.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null && !rb.isKinematic) _bodies.Add(rb);
        }

        private void DisableColliders(Component rig)
        {
            foreach (Collider c in rig.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                c.enabled = false;
                _disabled.Add(c);
            }
        }

        private void RestoreColliders()
        {
            foreach (Collider c in _disabled) if (c != null) c.enabled = true;
            _disabled.Clear();
        }
    }
}
