using System;
using System.Collections.Generic;
using BoneCheats.Core;
using Il2CppSLZ.Marrow.Pool;   // Poolee (proper despawn so it replicates)
using LabFusion.Marrow.Pool;   // LocalAssetSpawner
using LabFusion.RPC;           // NetworkAssetSpawner (SpawnRequestInfo / SpawnCallbackInfo are nested in it)
using MelonLoader;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Prop spam: one Lobby-wide toggle (no longer per player) that floods EVERY other player in the lobby with
    /// real, networked, spinning AKMs at once. Everything goes through Fusion's own spawner
    /// (NetworkAssetSpawner.Spawn), so the host and every other player sees and simulates the props.
    ///
    /// Spin: the spawned rigidbody is owned by us, so we re-apply angularVelocity every frame and Fusion's
    /// rigidbody sync carries the rotation to everyone. Props are despawned through Poolee.Despawn (the pool's
    /// own path, which Fusion replicates) instead of a local Destroy, so they actually disappear for others too.
    ///
    /// Barcode: the base game AKM spawnable barcode. Swap AkmBarcode for any other installed barcode.
    ///
    /// Tuning is the block of constants below, pushed to "max ultra": 1000 spawns/sec per player, 2000 alive at
    /// once. This is genuinely a lot of replicated physics objects - every single one is sent to and simulated
    /// by the host and every other client, for every player in the lobby, continuously. A weak host, a weak
    /// client's rig, or the connection itself can choke on this well before MaxAlive is even reached, and
    /// LabFusion's own issue tracker already documents NetworkAssetSpawner being able to crash a server or a
    /// client's game with no built-in clamp of its own - this feature leans directly on that limit. Test it in
    /// a small lobby first. If anything chugs, drop SpawnsPerSecond and MaxAlive together, in that order.
    /// </summary>
    internal static class PropSpam
    {
        public const string AkmBarcode = "c1534c5a-a6b5-4177-beb8-04d947756e41"; // base game AKM

        // ---- tuning: MAX ULTRA. this is the ceiling - there is no setting above this one. ----
        private const int MaxAlive = 3000;         // props alive at once, across every player in the lobby
        private const int SpawnsPerSecond = 2300;  // spawn rate PER PLAYER - as fast as the request queue allows
        private const int MaxPerFrame = 100;        // spawn requests issued in a single frame (avoids frame spikes)
        private const float PropLifetime = 12f;    // shorter than before on purpose - at this rate, old props need
                                                   // to clear out fast or MaxAlive is hit almost instantly and new
                                                   // spawns stop until some expire
        private const float LaunchSpeed = 5f;
        private const float SpinSpeed = 24f;       // rad/s, re-applied every frame so they keep spinning
        private const float SpawnHeight = 2.5f;    // above the target's head
        private const float SpawnRadius = 16f;      // horizontal scatter around each target

        private sealed class PerPlayer { public float Accum; }
        private sealed class Prop { public GameObject Go; public Rigidbody Rb; public Vector3 Spin; public float DieAt; }

        private static bool _enabled;
        private static readonly Dictionary<byte, PerPlayer> Accum = new Dictionary<byte, PerPlayer>();
        private static readonly List<Prop> Alive = new List<Prop>();
        private static readonly List<byte> Gone = new List<byte>();
        private static int _pending;   // requested but not yet confirmed by the callback
        private static float _lastTick;

        public static bool IsOn() { return _enabled; }

        /// <summary>Turns prop spam on/off for the whole lobby at once. Switching off despawns everything
        /// already spawned right away, instead of leaving it to spin for up to PropLifetime more seconds.</summary>
        public static void SetAll(bool on)
        {
            _enabled = on;
            if (!on)
            {
                Accum.Clear();
                for (int i = 0; i < Alive.Count; i++) Remove(Alive[i]);
                Alive.Clear();
                _pending = 0;
            }
        }

        public static void ClearAll()
        {
            _enabled = false;
            Accum.Clear();
            for (int i = 0; i < Alive.Count; i++) Remove(Alive[i]);
            Alive.Clear();
            _pending = 0;
        }

        private static void Remove(Prop p)
        {
            if (p.Go == null) return;
            try
            {
                Poolee poolee = p.Go.GetComponentInParent<Poolee>();
                if (poolee != null) { poolee.Despawn(); return; }
            }
            catch { /* fall through to Destroy */ }
            UnityEngine.Object.Destroy(p.Go);
        }

        /// <summary>Called every frame from PlayerActionsFeature.</summary>
        public static void Tick()
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Min(now - _lastTick, 0.1f);
            _lastTick = now;

            if (_enabled)
            {
                int budget = MaxPerFrame;
                Gone.Clear();
                foreach (byte id in Accum.Keys) Gone.Add(id);               // stale ids (player left) cleaned below
                foreach (byte id in Gone) if (PlayerActions.Find(id) == null) Accum.Remove(id);

                foreach (Plr p in Players.All)
                {
                    byte id;
                    try { id = PlayerActions.IdOf(p); } catch { continue; }

                    PerPlayer pa;
                    if (!Accum.TryGetValue(id, out pa)) { pa = new PerPlayer(); Accum[id] = pa; }

                    pa.Accum += SpawnsPerSecond * dt;
                    while (pa.Accum >= 1f && budget > 0)
                    {
                        pa.Accum -= 1f;
                        if (Alive.Count + _pending >= MaxAlive) { pa.Accum = 0f; break; }
                        if (Spawn(p)) budget--;
                    }
                    if (pa.Accum > 3f) pa.Accum = 3f; // don't build up a huge backlog per player
                }
            }

            // keep them spinning + expire old ones
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Prop pr = Alive[i];
                if (pr.Go == null) { Alive.RemoveAt(i); continue; }
                if (now >= pr.DieAt) { Remove(pr); Alive.RemoveAt(i); continue; }
                if (pr.Rb != null) pr.Rb.angularVelocity = pr.Spin;
            }
        }

        private static bool Spawn(Plr p)
        {
            if (!p.TryHead(out Vector3 head)) return false;

            Vector2 ring = UnityEngine.Random.insideUnitCircle * SpawnRadius;
            Vector3 pos = head + new Vector3(ring.x, SpawnHeight + UnityEngine.Random.value * 1.5f, ring.y);
            Quaternion rot = UnityEngine.Random.rotation;
            Vector3 launchVel = (head - pos).normalized * LaunchSpeed + UnityEngine.Random.insideUnitSphere * 1.5f;
            Vector3 spin = UnityEngine.Random.onUnitSphere * SpinSpeed;

            _pending++;
            try
            {
                // 'var' on purpose: the Spawnable type's namespace differs between game/Fusion versions
                // (and it may be a struct), so we never name it - the compiler takes it from CreateSpawnable.
                var spawnable = LocalAssetSpawner.CreateSpawnable(AkmBarcode);

                var info = new NetworkAssetSpawner.SpawnRequestInfo
                {
                    Spawnable = spawnable,
                    Position = pos,
                    Rotation = rot,
                    SpawnEffect = false,
                    SpawnCallback = (Action<NetworkAssetSpawner.SpawnCallbackInfo>)(cb =>
                    {
                        _pending = Math.Max(0, _pending - 1);
                        GameObject go = cb.Spawned;   // cb is a struct, no null check
                        if (go == null) return;

                        Rigidbody rb = go.GetComponentInChildren<Rigidbody>();
                        if (rb != null)
                        {
                            rb.velocity = launchVel;
                            rb.angularVelocity = spin;
                        }
                        Alive.Add(new Prop { Go = go, Rb = rb, Spin = spin, DieAt = Time.unscaledTime + PropLifetime });
                    })
                };

                NetworkAssetSpawner.Spawn(info);
                return true;
            }
            catch (Exception e)
            {
                _pending = Math.Max(0, _pending - 1);
                R.WarnOnce("propspam-spawn", "[PropSpam] spawn failed (is the AKM barcode installed?): " + e.Message);
                return false;
            }
        }
    }
}