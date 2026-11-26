using System;
using System.Collections.Generic;
using System.Reflection;
using BoneCheats.Core;
using MelonLoader;
using UnityEngine;
using AmmoInventory = Il2CppSLZ.Marrow.AmmoInventory;
using CartridgeData = Il2CppSLZ.Marrow.Data.CartridgeData;
using Gun = Il2CppSLZ.Marrow.Gun;
using FireMode = Il2CppSLZ.Marrow.Gun.FireMode;
using CartridgeStates = Il2CppSLZ.Marrow.Gun.CartridgeStates;
using InteractableHost = Il2CppSLZ.Marrow.InteractableHost;
using MagazineState = Il2CppSLZ.Marrow.MagazineState;
using ProjectileData = Il2CppSLZ.Marrow.Data.ProjectileData;

namespace BoneCheats.Features
{
    /// <summary>
    /// Everything about guns you hold: target picking for silent aim / bullet TP (other players' heads, no
    /// distance limit), and live tweaks to the held gun (no recoil, no spread, rapid fire, infinite ammo, damage). The
    /// aim cone (FOV) is measured from either your head or from the gun you're holding (or your hand if you're
    /// not holding one) - see Settings.AimOrigin.
    ///
    /// Everything here uses the real Il2CppSLZ.Marrow members:
    ///   Gun.roundsPerMinute / roundsPerSecond / fireDuration / fireMode   (rapid fire)
    ///   Gun.MagazineState.AmmoCount / magazineData.rounds                 (infinite ammo, in the gun)
    ///   AmmoInventory.Instance.AddCartridge(cartridge, count)              (infinite ammo, spare rounds)
    ///   CartridgeData.projectile.damageMultiplier / .angle                 (damage, no spread)
    /// Originals are remembered and restored when a cheat is switched off or the gun is let go of.
    /// </summary>
    internal sealed class CombatFeature : Feature
    {
        // ---- shared with GunHooks / ESP ----
        public static IntPtr TargetId = IntPtr.Zero;
        private static bool _hasTarget;
        private static Vector3 _targetPoint;
        private static readonly GameObject[] Held = new GameObject[2];
        private readonly Transform[] _gunFp = new Transform[2];

        /// <summary>Where the aim cone starts and which way it points (valid when AimOriginValid).</summary>
        public static bool AimOriginValid;
        public static Vector3 AimOriginPos;
        public static Vector3 AimOriginFwd = Vector3.forward;

        public static bool TryGetTargetPoint(out Vector3 p)
        {
            p = _targetPoint;
            return _hasTarget;
        }

        /// <summary>True if this Gun is the one in one of the player's hands.</summary>
        public static bool IsPlayerGun(Gun gun)
        {
            if (gun == null) return false;
            Transform gt = gun.transform;
            foreach (GameObject h in Held)
            {
                if (h == null) continue;
                Transform ht = h.transform;
                if (gt.IsChildOf(ht) || ht.IsChildOf(gt)) return true;
            }
            return false;
        }

        /// <summary>The transform bullets leave from.</summary>
        public static Transform FirePoint(Gun gun)
        {
            return gun != null ? gun.firePointTransform : null;
        }

        // ---- per-gun state (originals of the gun's own rate-of-fire fields) ----
        private sealed class GunState
        {
            public Gun Gun;
            public IntPtr Id;
            public bool RateSaved;
            public float OrigRpm, OrigRps, OrigDuration;
            public bool ModeSaved;
            public FireMode OrigMode;
        }

        // ---- per-projectile state: ProjectileData is shared by every gun using that cartridge ----
        private sealed class ProjState
        {
            public ProjectileData Data;
            public float OrigDamage, OrigAngle;
        }

        private readonly Dictionary<IntPtr, GunState> _guns = new Dictionary<IntPtr, GunState>();
        private readonly Dictionary<IntPtr, ProjState> _projs = new Dictionary<IntPtr, ProjState>();
        private readonly Dictionary<int, Gun> _gunComp = new Dictionary<int, Gun>();
        private readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private readonly HashSet<IntPtr> _seenProj = new HashSet<IntPtr>();
        private readonly List<IntPtr> _drop = new List<IntPtr>();

        public override string Name => "Combat";

        protected override void Reset()
        {
            foreach (GunState g in _guns.Values) RestoreGun(g);
            _guns.Clear();
            RestoreAllProjectiles();
            RestoreKick();
            ClearTarget();
            AimOriginValid = false;
        }

        public static void OnSceneInit()
        {
            _carts.Clear();
            _kick.Clear();
            _recRbs.Clear();
            _ammoWasOn = false;
            ClearTarget();
            AimOriginValid = false;
            Held[0] = Held[1] = null;
        }

        private static void ClearTarget()
        {
            _hasTarget = false;
            TargetId = IntPtr.Zero;
        }

        private static bool NeedsGunWork()
        {
            return Settings.AimMode.Value != 0 || Settings.NoRecoil.Value || Settings.NoSpread.Value || Settings.RapidFire.Value
                || Settings.InfiniteAmmo.Value || Math.Abs(Settings.DamageMult.Value - 1f) > 0.01f;
        }

        // ---- no recoil ----
        // Layer 1 (main): the kick is data on the gun. Every float on the Gun, its cartridge and its projectile whose
        //   name contains "kick" or "recoil" is set to 0 while No Recoil is on and put back when it is turned off.
        //   The names it finds are written to the MelonLoader log once ("[No Recoil] found on ...").
        // Layer 2 (fallback): the game also shoves the gun's rigidbodies when it fires, so right before each of YOUR shots
        //   their velocities are remembered and put back on the next few frames AND the next few physics ticks.
        private sealed class KickSaved { public object Obj; public PropertyInfo Prop; public float Orig; }
        private static readonly Dictionary<string, KickSaved> _kick = new Dictionary<string, KickSaved>();
        private static readonly Dictionary<Type, PropertyInfo[]> _kickProps = new Dictionary<Type, PropertyInfo[]>();

        private static PropertyInfo[] KickProps(Type t)
        {
            if (_kickProps.TryGetValue(t, out PropertyInfo[] cached)) return cached;
            var list = new List<PropertyInfo>();
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (pi.PropertyType != typeof(float) || !pi.CanRead || !pi.CanWrite) continue;
                string n = pi.Name.ToLowerInvariant();
                if (n.Contains("kick") || n.Contains("recoil")) list.Add(pi);
            }
            PropertyInfo[] arr = list.ToArray();
            _kickProps[t] = arr;
            string names = arr.Length == 0 ? "(nothing)" : string.Join(", ", Array.ConvertAll(arr, x => x.Name));
            MelonLogger.Msg("[No Recoil] found on " + t.Name + ": " + names);
            return arr;
        }

        private static void ZeroKick(object obj, IntPtr ptr)
        {
            if (obj == null || ptr == IntPtr.Zero) return;
            foreach (PropertyInfo pi in KickProps(obj.GetType()))
            {
                string key = ptr + ":" + pi.Name;
                float cur = (float)pi.GetValue(obj);
                if (!_kick.ContainsKey(key)) _kick[key] = new KickSaved { Obj = obj, Prop = pi, Orig = cur };
                if (cur != 0f) pi.SetValue(obj, 0f);
            }
        }

        private static void RestoreKick()
        {
            foreach (KickSaved k in _kick.Values)
            {
                try { k.Prop.SetValue(k.Obj, k.Orig); } catch { }
            }
            _kick.Clear();
        }

        private static void ApplyNoKick(Gun gun)
        {
            if (!Settings.NoRecoil.Value) { if (_kick.Count > 0) RestoreKick(); return; }
            ZeroKick(gun, gun.Pointer);
            CartridgeData cart = CartridgeOf(gun);
            if (cart == null) return;
            ZeroKick(cart, cart.Pointer);
            ProjectileData pd = cart.projectile;
            if (pd != null) ZeroKick(pd, pd.Pointer);
        }

        private sealed class RbSaved { public Rigidbody Rb; public Vector3 Vel, Ang; }
        private static readonly List<RbSaved> _recRbs = new List<RbSaved>();
        private static int _recFrames, _recFixed;

        private static void AddRb(Rigidbody rb)
        {
            if (rb == null) return;
            foreach (RbSaved r in _recRbs) if (r.Rb.Pointer == rb.Pointer) return;
            _recRbs.Add(new RbSaved { Rb = rb, Vel = rb.velocity, Ang = rb.angularVelocity });
        }

        private static void RestoreRbs()
        {
            foreach (RbSaved r in _recRbs)
            {
                if (r.Rb == null) continue;
                r.Rb.velocity = r.Vel;
                r.Rb.angularVelocity = r.Ang;
            }
        }

        public static void BeforeShot(Gun gun)
        {
            if (!Settings.NoRecoil.Value) return;
            _recRbs.Clear();
            InteractableHost host = gun.host;
            if (host != null) AddRb(host.Rb);
            foreach (Rigidbody rb in gun.GetComponentsInChildren<Rigidbody>()) AddRb(rb);
            _recFrames = 3;
            _recFixed = 3;
        }

        protected override void LateTick()
        {
            if (_recFrames <= 0) return;
            _recFrames--;
            if (Settings.NoRecoil.Value) RestoreRbs();
        }

        protected override void FixedTick()
        {
            if (_recFixed <= 0) return;
            _recFixed--;
            if (Settings.NoRecoil.Value) RestoreRbs();
        }

        private int _idleTicks;

        protected override void Tick()
        {
            // Nothing enabled and nothing left to restore: skip all per-frame hand/gun lookups.
            // (Held[] is still refreshed ~4x/s so GunHooks.IsPlayerGun stays correct for tracers.)
            if (!NeedsGunWork() && _guns.Count == 0 && _projs.Count == 0 && _kick.Count == 0 && !Settings.BulletTracers.Value && !_ammoWasOn)
            {
                if (++_idleTicks % 15 != 0) return;
            }

            Held[0] = Game.Held(false);
            Held[1] = Game.Held(true);
            _gunFp[0] = _gunFp[1] = null;

            _seen.Clear();
            _seenProj.Clear();
            for (int i = 0; i < 2; i++)
            {
                Gun gun = GunIn(Held[i]);
                if (gun == null) continue;
                GunState g = StateFor(gun);
                _seen.Add(g.Id);
                ApplyRate(g);
                ApplyProjectile(gun);
                ApplyNoKick(gun);
                if (Settings.InfiniteAmmo.Value) { RefillGun(gun); TopUpInventory(gun); }
                _gunFp[i] = FirePoint(gun);
            }

            if (Settings.InfiniteAmmo.Value)
            {
                if (!_ammoWasOn) { _ammoWasOn = true; _carts.Clear(); _nextInv = 0f; }   // fresh start each time it is switched on
                TopUpInventory(null);
            }
            else _ammoWasOn = false;

            // Guns that were let go of get their original values back.
            _drop.Clear();
            foreach (var kv in _guns) if (!_seen.Contains(kv.Key)) _drop.Add(kv.Key);
            foreach (IntPtr id in _drop) { RestoreGun(_guns[id]); _guns.Remove(id); }

            // Projectile data that no held gun uses any more (or whose cheat is off) goes back to normal.
            _drop.Clear();
            foreach (var kv in _projs) if (!_seenProj.Contains(kv.Key)) _drop.Add(kv.Key);
            foreach (IntPtr id in _drop) { RestoreProjectile(_projs[id]); _projs.Remove(id); }

            UpdateTarget();
        }

        private Gun GunIn(GameObject held)
        {
            if (held == null) return null;
            int hid = held.GetInstanceID();
            if (!_gunComp.TryGetValue(hid, out Gun comp) || comp == null)
            {
                comp = held.GetComponentInParent<Gun>();
                if (comp == null) comp = held.GetComponentInChildren<Gun>();
                if (_gunComp.Count > 64) _gunComp.Clear();
                _gunComp[hid] = comp;
            }
            return comp;
        }

        private GunState StateFor(Gun gun)
        {
            IntPtr id = gun.Pointer;
            if (_guns.TryGetValue(id, out GunState existing)) return existing;
            var g = new GunState { Gun = gun, Id = id };
            _guns[id] = g;
            return g;
        }

        // ---------------- tweaks ----------------

        /// <summary>Rapid fire: rounds per minute, rounds per second and the time between shots, all scaled together.</summary>
        private static void ApplyRate(GunState g)
        {
            Gun gun = g.Gun;
            if (Settings.RapidFire.Value)
            {
                float rf = Mathf.Max(1f, Settings.RapidFireMult.Value);
                if (!g.RateSaved)
                {
                    g.OrigRpm = gun.roundsPerMinute;
                    g.OrigRps = gun.roundsPerSecond;
                    g.OrigDuration = gun.fireDuration;
                    g.RateSaved = true;
                }
                gun.roundsPerMinute = g.OrigRpm * rf;
                gun.roundsPerSecond = g.OrigRps * rf;
                gun.fireDuration = g.OrigDuration / rf;

                if (!g.ModeSaved) { g.OrigMode = gun.fireMode; g.ModeSaved = true; }
                gun.fireMode = FireMode.AUTOMATIC;
            }
            else RestoreGun(g);
        }

        private static void RestoreGun(GunState g)
        {
            Gun gun = g.Gun;
            if (gun == null) { g.RateSaved = g.ModeSaved = false; return; }
            if (g.RateSaved)
            {
                gun.roundsPerMinute = g.OrigRpm;
                gun.roundsPerSecond = g.OrigRps;
                gun.fireDuration = g.OrigDuration;
                g.RateSaved = false;
            }
            if (g.ModeSaved) { gun.fireMode = g.OrigMode; g.ModeSaved = false; }
        }

        /// <summary>Damage multiplier and no spread both live on the cartridge's ProjectileData.</summary>
        private void ApplyProjectile(Gun gun)
        {
            ProjectileData data = ProjectileOf(gun);
            if (data == null) return;
            IntPtr id = data.Pointer;
            _seenProj.Add(id);

            float dmg = Settings.DamageMult.Value;
            bool wantDamage = Math.Abs(dmg - 1f) > 0.01f;
            bool wantSpread = Settings.NoSpread.Value;

            if (!wantDamage && !wantSpread)
            {
                if (_projs.TryGetValue(id, out ProjState old)) { RestoreProjectile(old); _projs.Remove(id); }
                return;
            }

            if (!_projs.TryGetValue(id, out ProjState ps))
            {
                ps = new ProjState { Data = data, OrigDamage = data.damageMultiplier, OrigAngle = data.angle };
                _projs[id] = ps;
            }
            data.damageMultiplier = wantDamage ? ps.OrigDamage * dmg : ps.OrigDamage;
            data.angle = wantSpread ? 0f : ps.OrigAngle;
        }

        private static void RestoreProjectile(ProjState ps)
        {
            if (ps.Data == null) return;
            ps.Data.damageMultiplier = ps.OrigDamage;
            ps.Data.angle = ps.OrigAngle;
        }

        private void RestoreAllProjectiles()
        {
            foreach (ProjState ps in _projs.Values) RestoreProjectile(ps);
            _projs.Clear();
        }

        // ---------------- ammo ----------------

        /// <summary>The cartridge this gun fires: the loaded magazine's, else the one in the chamber, else the gun's default.</summary>
        private static CartridgeData CartridgeOf(Gun gun)
        {
            MagazineState ms = gun.MagazineState;
            if (ms != null && ms.cartridgeData != null) return ms.cartridgeData;
            if (gun.chamberedCartridge != null) return gun.chamberedCartridge;
            return gun.defaultCartridge;
        }

        private static ProjectileData ProjectileOf(Gun gun)
        {
            CartridgeData c = CartridgeOf(gun);
            return c != null ? c.projectile : null;
        }

        /// <summary>Rounds currently in the magazine, or -1 if there is no magazine state.</summary>
        public static int AmmoCount(Gun gun)
        {
            MagazineState ms = gun.MagazineState;
            return ms != null ? ms.AmmoCount : -1;
        }

        /// <summary>
        /// Tops the gun's magazine back up to full. Called every frame AND right before and after each shot
        /// (GunHooks) so the magazine can never run dry, even with rapid fire.
        /// </summary>
        public static void RefillGun(Gun gun)
        {
            MagazineState ms = gun.MagazineState;
            if (ms == null) return;

            int cap = 0;
            if (ms.magazineData != null) cap = ms.magazineData.rounds;
            if (cap <= 0 && gun.defaultMagazine != null) cap = gun.defaultMagazine.rounds;
            if (cap <= 0) return;

            if (ms.AmmoCount < cap) ms.Refill();      // AmmoCount is read-only; Refill() is the game's own "fill the magazine"
        }

        // ---- ammo INVENTORY (the spare ammo you carry, as opposed to the rounds in the gun) ----
        // With Infinite Ammo on, every cartridge type of a gun you have held gets topped up to InvTarget about once a second.

        private const int InvTarget = 9999;
        private static float _nextInv;
        private static readonly List<CartridgeData> _carts = new List<CartridgeData>();
        private static bool _ammoWasOn;
        private static bool _inventoryReported;

        private static void Remember(CartridgeData cart)
        {
            if (cart == null) return;
            IntPtr id = cart.Pointer;
            foreach (CartridgeData c in _carts) if (c.Pointer == id) return;
            _carts.Add(cart);
        }

        public static void TopUpInventory(Gun gun)
        {
            float now = Time.unscaledTime;
            if (now < _nextInv) return;
            _nextInv = now + 1f;

            if (gun != null) Remember(CartridgeOf(gun));
            if (_carts.Count == 0) return;                 // nothing known yet: waits until you hold a gun

            AmmoInventory inv = AmmoInventory.Instance;
            if (inv == null) return;                       // not in a level yet

            foreach (CartridgeData cart in _carts)
            {
                int have = inv.GetCartridgeCount(cart);
                if (have < InvTarget) inv.AddCartridge(cart, InvTarget - have);
            }

            if (!_inventoryReported)
            {
                _inventoryReported = true;
                MelonLogger.Msg("[Infinite Ammo] topped up the ammo inventory for " + _carts.Count + " cartridge type(s).");
            }
        }

        // ---- shot detection (used by GunHooks so tracers only fire when a bullet really left the gun) ----

        public struct ShotSnapshot
        {
            public CartridgeStates Cart;
            public IntPtr Chamber;
            public int Ammo;
        }

        public static ShotSnapshot Snapshot(Gun gun)
        {
            CartridgeData ch = gun.chamberedCartridge;
            return new ShotSnapshot
            {
                Cart = gun.cartridgeState,
                Chamber = ch != null ? ch.Pointer : IntPtr.Zero,
                Ammo = AmmoCount(gun),
            };
        }

        /// <summary>True if the gun's state changed in a way that means a round was fired.</summary>
        public static bool Fired(ShotSnapshot before, ShotSnapshot after)
        {
            if (before.Cart != after.Cart) return true;
            if (before.Chamber != after.Chamber) return true;
            return before.Ammo >= 0 && after.Ammo >= 0 && after.Ammo < before.Ammo;
        }

        // ---------------- target picking ----------------

        /// <summary>Where the aim cone starts: the head, or the held gun's muzzle, or the hand.</summary>
        private bool ComputeAimRay(out Vector3 origin, out Vector3 fwd)
        {
            origin = Vector3.zero;
            fwd = Vector3.forward;

            if (Settings.AimOrigin.Value == 1)
            {
                for (int i = 1; i >= 0; i--)   // right hand first
                {
                    Transform fp = _gunFp[i];
                    if (fp == null) continue;
                    origin = fp.position;
                    fwd = fp.forward;
                    return true;
                }
                for (int i = 1; i >= 0; i--)
                {
                    Transform hand = Game.HandTransform(i == 1);
                    if (hand == null) continue;
                    origin = hand.position;
                    fwd = hand.forward;
                    return true;
                }
            }

            Transform head = Game.Head;
            if (head == null) return false;
            origin = head.position;
            fwd = head.forward;
            return true;
        }

        private void UpdateTarget()
        {
            ClearTarget();
            AimOriginValid = false;

            bool aiming = Settings.AimMode.Value != 0;
            if (!aiming) return;

            if (!ComputeAimRay(out Vector3 origin, out Vector3 fwd)) return;
            AimOriginValid = true;
            AimOriginPos = origin;
            AimOriginFwd = fwd;

            Players.Refresh(false);

            float fov = Settings.AimFov.Value;
            float best = float.MaxValue;

            foreach (Plr n in Players.All)
            {
                if (!n.Valid || !n.Alive) continue;
                if (!n.TryHead(out Vector3 p)) continue;           // frozen / unknown position: don't aim at a ghost
                Vector3 d = p - origin;
                if (d.magnitude < 0.3f) continue;                    // no distance limit, just ignore anything inside the gun

                float angle = Vector3.Angle(fwd, d);
                if (angle > fov || angle >= best) continue;

                best = angle;
                _targetPoint = p;
                _hasTarget = true;
                TargetId = n.Id;
            }
        }
    }
}