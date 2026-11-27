using System;
using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Fly, noclip, walk speed multiplier. Deliberately simple.
    ///
    /// Noclip = set detectCollisions = false on every rigidbody of YOUR rig. Nothing global is touched (no
    /// layer-collision matrix, no colliders, no other object in the scene), so it can't leak onto spawned items
    /// or other players, and switching it off is just detectCollisions = true again on the same bodies.
    /// The previous versions changed the global layer matrix; props that shared a layer with the rig (spawned guns,
    /// held items) kept falling through the floor, and the matrix could end up stuck.
    ///
    /// Fly = every rig body gets the same velocity (so the joints stay relaxed) and gravity is switched off per
    /// body, then restored per body.
    /// </summary>
    internal sealed class MovementFeature : Feature
    {
        private const float BaseWalkSpeed = 3.5f;
        private const float Deadzone = 0.15f;

        private readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        private readonly Dictionary<int, bool> _gravity = new Dictionary<int, bool>();
        private readonly Dictionary<int, bool> _detect = new Dictionary<int, bool>();   // original detectCollisions per body

        // objects held in a hand while noclip is on: they go through everything too, and are put back when let go
        private readonly Dictionary<int, Rigidbody> _heldBodies = new Dictionary<int, Rigidbody>();

        private IntPtr _rigPtr = IntPtr.Zero;
        private bool _noclipOn;
        private bool _flyOn;
        private float _nextScan;

        public override string Name => "Movement";

        protected override void Reset() { SetNoclip(false); SetFly(false); }

        // runs the moment a setting changes in the menu, so switching off is instant
        protected override void Apply()
        {
            if (!Settings.Noclip.Value && _noclipOn) SetNoclip(false);
            if (!Settings.Fly.Value && !Settings.Noclip.Value && _flyOn) SetFly(false);
        }

        protected override void FixedTick()
        {
            Component rig = Game.Rig;
            if (rig == null) { ForgetRig(); return; }

            // new rig (level load / respawn): old bodies are gone, just forget them
            if (rig.Pointer != _rigPtr) { ForgetRig(); _rigPtr = rig.Pointer; }

            bool noclip = Settings.Noclip.Value;
            bool fly = Settings.Fly.Value || noclip;
            float walk = Settings.WalkSpeedMult.Value;
            bool boost = Mathf.Abs(walk - 1f) > 0.01f;

            if (!noclip && _noclipOn) SetNoclip(false);
            if (!fly && _flyOn) SetFly(false);
            if (!fly && !boost) return;

            if (Time.unscaledTime >= _nextScan || _bodies.Count == 0)
            {
                _nextScan = Time.unscaledTime + 1f;
                Scan(rig);
            }

            if (noclip && !_noclipOn) SetNoclip(true);
            if (fly && !_flyOn) SetFly(true);
            if (noclip) ApplyNoclipToNewBodies();

            Transform head = Game.Head;
            if (head == null) return;
            Vector2 left = Game.Stick(false);

            if (fly)
            {
                Vector3 want = Vector3.zero;
                if (left.magnitude > Deadzone) want += head.forward * left.y + head.right * left.x;
                Vector2 rs = Game.Stick(true);
                if (Mathf.Abs(rs.y) > Deadzone) want += Vector3.up * rs.y;
                want *= Settings.FlySpeed.Value;

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

        // ---------------- rig bodies ----------------

        private void Scan(Component rig)
        {
            _bodies.Clear();
            foreach (Rigidbody rb in rig.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null) _bodies.Add(rb);
        }

        private void ForgetRig()
        {
            _bodies.Clear(); _gravity.Clear(); _detect.Clear(); _heldBodies.Clear();
            _noclipOn = false; _flyOn = false; _rigPtr = IntPtr.Zero;
        }

        // ---------------- noclip ----------------

        private void SetNoclip(bool on)
        {
            _noclipOn = on;
            if (on)
            {
                ApplyNoclipToNewBodies();
                MelonLoader.MelonLogger.Msg("[Noclip] on (" + _bodies.Count + " rig bodies)");
            }
            else
            {
                int n = 0;
                foreach (Rigidbody rb in _bodies)
                {
                    if (rb == null) continue;
                    if (_detect.TryGetValue(rb.GetInstanceID(), out bool orig)) { rb.detectCollisions = orig; n++; }
                }
                foreach (var kv in _heldBodies)
                    if (kv.Value != null && _detect.TryGetValue(kv.Key, out bool orig)) { kv.Value.detectCollisions = orig; n++; }
                _heldBodies.Clear();
                _detect.Clear();
                MelonLoader.MelonLogger.Msg("[Noclip] off - collisions restored on " + n + " rig bodies");
            }
        }

        /// <summary>Turns collisions off on every rig body and on whatever is held in a hand (remembering each one's original value). Safe to call repeatedly.</summary>
        private void ApplyNoclipToNewBodies()
        {
            foreach (Rigidbody rb in _bodies) NoCollide(rb);

            // what is in the hands right now
            var nowHeld = new HashSet<int>();
            for (int h = 0; h < 2; h++)
            {
                GameObject go = Game.Held(h == 1);
                if (go == null) continue;
                Rigidbody root = go.GetComponentInParent<Rigidbody>();
                if (root == null) continue;
                foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    int id = rb.GetInstanceID();
                    nowHeld.Add(id);
                    _heldBodies[id] = rb;
                    NoCollide(rb);
                }
            }

            // let go of something: give it its collisions back
            List<int> dropped = null;
            foreach (var kv in _heldBodies)
                if (!nowHeld.Contains(kv.Key)) (dropped ?? (dropped = new List<int>())).Add(kv.Key);
            if (dropped != null)
                foreach (int id in dropped)
                {
                    Rigidbody rb = _heldBodies[id];
                    if (rb != null && _detect.TryGetValue(id, out bool orig)) rb.detectCollisions = orig;
                    _detect.Remove(id);
                    _heldBodies.Remove(id);
                }
        }

        private void NoCollide(Rigidbody rb)
        {
            if (rb == null) return;
            int id = rb.GetInstanceID();
            if (!_detect.ContainsKey(id)) _detect[id] = rb.detectCollisions;
            rb.detectCollisions = false;
        }

        // ---------------- fly ----------------

        private void SetFly(bool on)
        {
            _flyOn = on;
            foreach (Rigidbody rb in _bodies)
            {
                if (rb == null) continue;
                int id = rb.GetInstanceID();
                if (on)
                {
                    if (!_gravity.ContainsKey(id)) _gravity[id] = rb.useGravity;
                    rb.useGravity = false;
                }
                else
                {
                    if (_gravity.TryGetValue(id, out bool orig)) rb.useGravity = orig;
                    rb.velocity = Vector3.zero;
                }
            }
            if (!on) _gravity.Clear();
        }
    }
}
