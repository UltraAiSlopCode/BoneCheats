using System;
using System.Collections.Generic;
using BoneCheats.Core;
using Il2CppSLZ.Marrow.Pool;   // Poolee (proper despawn so it replicates)
using LabFusion.Entities;      // EntitySource
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
    /// Tuning is the block of constants below, pushed to the actual hard ceiling: no per-frame issue cap at
    /// all (every accumulated spawn for every player is fired the same frame), an absolute cap of 100,000
    /// props alive lobby-wide, and a spawn rate per player high enough that the real limiter is MaxAlive, not
    /// the rate. This is genuinely as much replicated physics as NetworkAssetSpawner can be made to emit from
    /// one client - every single prop is sent to and simulated by the host and every other client, for every
    /// player in the lobby, continuously, with nothing left throttled on purpose. A weak host, a weak client's
    /// rig, or the connection itself WILL choke on this, likely well before MaxAlive is reached, and so will
    /// the caller's own machine (CreateSpawnable + Spawn is issued synchronously per prop, so a frame that
    /// issues thousands of requests will itself hitch) - LabFusion's own issue tracker already documents
    /// NetworkAssetSpawner being able to crash a server or a client's game with no built-in clamp of its own,
    /// and this feature leans directly on that with every governor removed. If MaxAlive is ever actually
    /// reached on real hardware that's the ceiling doing its job, not a bug. Drop MaxAlive first if anything
    /// needs to come back down - it's the only knob left that still limits anything.
    ///
    /// Run length: there's no more per-prop lifetime - nothing despawns on its own just for existing. Instead
    /// the whole run is time-boxed: RunDuration seconds after SetAll(true) is called, the feature auto-stops
    /// AND wipes every prop it spawned in one go (same as hitting the toggle off), then stays off until
    /// toggled on again. While it's running, props pile up and stay exactly where they land/spin until either
    /// MaxAlive is hit (spawning pauses, nothing already out there is touched) or the run timer expires.
    ///
    /// Why the 20s clear could silently miss props (worse on a low-fps machine): every spawn's confirmation
    /// is a SpawnCallback delegate crossing the Il2Cpp boundary, and nothing on the native side keeps a
    /// managed reference to that delegate while the spawn/asset-load is in flight. The .NET GC was free to
    /// collect a pending callback before it ever fired - more likely the longer that window is, and a low
    /// frame rate stretches real time (and GC passes) between "requested" and "confirmed". A collected
    /// callback never runs, so that prop was never added to Alive and the 20s clear had nothing to despawn
    /// it with: it just sat there forever. _liveCallbacks below roots every pending callback so the GC can't
    /// touch it until it actually fires, and MaxPending caps how many can be in flight at once so a slow
    /// machine doesn't pile up thousands of them between frames.
    /// </summary>
    internal static class PropSpam
    {
        public const string AkmBarcode = "c1534c5a-a6b5-4177-beb8-04d947756e41"; // base game AKM

        // ---- tuning: TRUE MAX, with the one throttle that actually has to stay. ----
        private const int MaxAlive = 100000;        // props alive at once, across every player in the lobby
        private const int SpawnsPerSecond = 1000000; // spawn rate PER PLAYER - effectively unbounded; MaxAlive governs
        private const int MaxPerFrame = 250;        // spawn REQUESTS issued in a single frame - see note below.
                                                     // int.MaxValue here was tried: the instant the feature is
                                                     // enabled, every accumulated spawn (up to MaxAlive, so
                                                     // potentially 100,000) got issued SYNCHRONOUSLY in that one
                                                     // frame - CreateSpawnable + NetworkAssetSpawner.Spawn is a
                                                     // blocking Il2Cpp/RPC call per prop, so one frame turned into
                                                     // a multi-second hang on the calling thread. That hang is long
                                                     // enough that the client stops acking the server entirely,
                                                     // which reads as a dead connection and gets you kicked before
                                                     // most of the requests even finish - the freeze IS the kick,
                                                     // not a side effect of it. 250/frame still ramps to MaxAlive
                                                     // in under half a second at 60fps and is high enough to still
                                                     // reliably wreck a session; push it higher if your rig can
                                                     // eat a bigger per-frame hitch without losing the connection,
                                                     // but int.MaxValue here will keep getting you kicked before
                                                     // the spam can do its job.
        private const float RunDuration = 20f;     // seconds the whole run lasts before auto-stop + full clear
        private const float LaunchSpeed = 5f;
        private const float SpinSpeed = 24f;       // rad/s, re-applied every frame so they keep spinning
        private const float SpawnHeight = 2.5f;    // above the target's head
        private const float SpawnRadius = 16f;      // horizontal scatter around each target

        private sealed class PerPlayer { public float Accum; }
        private sealed class Prop { public GameObject Go; public Rigidbody Rb; public Vector3 Spin; }

        private static bool _enabled;
        private static float _enabledAt;
        private static int _generation;   // bumped every time a run ends - see the SpawnCallback note in Spawn()
        private static readonly Dictionary<byte, PerPlayer> Accum = new Dictionary<byte, PerPlayer>();
        private static readonly List<Prop> Alive = new List<Prop>();
        private static readonly List<byte> Gone = new List<byte>();
        private static int _pending;   // requested but not yet confirmed by the callback
        private const int MaxPending = 400;   // in-flight cap - see _liveCallbacks note below
        private static float _lastTick;

        // Every SpawnCallback is an Action<T> closure handed across the Il2Cpp boundary. Nothing on the
        // native side keeps a managed reference to it, so the .NET GC is free to collect it while the native
        // spawn/asset-load is still in flight - and it will, especially on a low-fps machine, because a low
        // frame rate means more real time (and more GC passes) elapse between "request issued" and "native
        // code finally calls back". When that happens the callback simply never runs: the prop still gets
        // created in the world (the native side doesn't care that the callback vanished), but it's never
        // added to Alive, so SetAll(false)/ClearAll has nothing to despawn it with - exactly "doesn't clear
        // after 20 seconds". Rooting every pending callback here until it actually fires is what the original
        // code was missing; this list is the fix, not cosmetic bookkeeping.
        private static readonly List<object> _liveCallbacks = new List<object>();

        public static bool IsOn() { return _enabled; }

        /// <summary>Turns prop spam on/off for the whole lobby at once. Switching off (by the menu, or by the
        /// RunDuration timer) despawns everything already spawned right away. Turning on starts the
        /// RunDuration clock fresh.</summary>
        public static void SetAll(bool on)
        {
            _enabled = on;
            if (on)
            {
                _enabledAt = Time.unscaledTime;
            }
            else
            {
                _generation++;   // anything still in flight from this run gets despawned on arrival, not kept
                Accum.Clear();
                for (int i = 0; i < Alive.Count; i++) Remove(Alive[i]);
                Alive.Clear();
                // _pending and _liveCallbacks are NOT cleared here on purpose: requests already in flight
                // still need to land and run the gen-check above so they despawn themselves. Clearing the
                // counter/list here would just make this run's leftovers look like a fresh run's pending work.
            }
        }

        public static void ClearAll()
        {
            _enabled = false;
            _generation++;
            Accum.Clear();
            for (int i = 0; i < Alive.Count; i++) Remove(Alive[i]);
            Alive.Clear();
        }

        private static void Remove(Prop p) { RemoveGameObject(p.Go); }

        private static void RemoveGameObject(GameObject go)
        {
            if (go == null) return;
            try
            {
                Poolee poolee = go.GetComponentInParent<Poolee>();
                if (poolee != null) { poolee.Despawn(); return; }
            }
            catch { /* fall through to Destroy */ }
            UnityEngine.Object.Destroy(go);
        }

        /// <summary>Called every frame from PlayerActionsFeature.</summary>
        public static void Tick()
        {
            float now = Time.unscaledTime;
            float dt = Mathf.Min(now - _lastTick, 0.1f);
            _lastTick = now;

            if (_enabled && now - _enabledAt >= RunDuration)
            {
                SetAll(false); // run timer expired: stop and wipe everything spawned this run
                return;
            }

            if (_enabled)
            {
                int budget = MaxPerFrame;
                Gone.Clear();
                foreach (byte id in Accum.Keys) Gone.Add(id);               // stale ids (player left) cleaned below
                foreach (byte id in Gone) if (PlayerActions.Find(id) == null) Accum.Remove(id);

                // MaxPerFrame is split evenly across every player in the lobby instead of being one pool that
                // Players.All drains in iteration order. A shared pool meant whichever player happened to be
                // first in the list ate the entire frame's budget before the rest got a single spawn - in a
                // lobby of 4+ that reads as "everyone but the first target barely gets any props," which is
                // exactly the uneven lag that was reported. Each player now gets their own slice every frame,
                // so the spam (and the lag it causes) lands on everyone, not just whoever iterates first.
                int playerCount = Players.All.Count;
                int perPlayerBudget = playerCount > 0 ? Math.Max(1, MaxPerFrame / playerCount) : 0;

                foreach (Plr p in Players.All)
                {
                    byte id;
                    try { id = PlayerActions.IdOf(p); } catch { continue; }

                    PerPlayer pa;
                    if (!Accum.TryGetValue(id, out pa)) { pa = new PerPlayer(); Accum[id] = pa; }

                    pa.Accum += SpawnsPerSecond * dt;
                    int localBudget = perPlayerBudget;
                    while (pa.Accum >= 1f && localBudget > 0 && budget > 0)
                    {
                        pa.Accum -= 1f;
                        if (Alive.Count + _pending >= MaxAlive) { pa.Accum = 0f; break; }
                        if (_pending >= MaxPending) { pa.Accum = 0f; break; }   // too many in flight already - let them land before adding more
                        if (Spawn(p)) { localBudget--; budget--; }
                    }
                    if (pa.Accum > 3f) pa.Accum = 3f; // don't build up a huge backlog per player
                }
            }

            // keep them spinning - nothing here despawns for age anymore; only SetAll(false)
            // (menu toggle or the RunDuration timer above) ever removes a prop.
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Prop pr = Alive[i];
                if (pr.Go == null) { Alive.RemoveAt(i); continue; }
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

            // Captured now, compared against the live _generation once the callback actually fires. Spawning
            // is a network round trip - NetworkAssetSpawner.Spawn() doesn't call back the same frame it's
            // issued, it calls back whenever Fusion finishes the request, which can land well after the 20s
            // RunDuration timer (or the menu toggle) has already fired SetAll(false)/ClearAll(). The callback
            // used to add straight to Alive with no check at all, so every prop whose request was in flight
            // at the moment of a clear just appeared afterwards anyway, with nothing left to ever remove it
            // now that there's no per-prop lifetime - that's the "it kept spawning them after it stopped" bug.
            // Comparing generations tells a late callback its run is over and despawns the prop the instant it
            // lands instead of silently adding it to a list a finished run no longer owns.
            int gen = _generation;

            _pending++;
            object callbackHandle = null;   // set below; keeps the delegate rooted until it fires (see _liveCallbacks)
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
                    // Spawner.cs hit this exact bug already: leaving SpawnSource unset defaults it to None,
                    // and a None-sourced request isn't treated as a player spawn. Fusion's own permission
                    // system gates spawns (and, since an un-sourced object was never properly "yours" to
                    // begin with, its despawn too) on who's allowed to spawn what - a client without spawn
                    // permission got silently refused, and a prop that WAS let through but never got sourced
                    // as a player spawn could just as easily get its despawn ignored, which is almost
                    // certainly why some props were surviving the 20s clear. Matching Spawner.cs's fix here.
                    SpawnSource = EntitySource.Player,
                    SpawnCallback = (Action<NetworkAssetSpawner.SpawnCallbackInfo>)(cb =>
                    {
                        _pending = Math.Max(0, _pending - 1);
                        if (callbackHandle != null) _liveCallbacks.Remove(callbackHandle);   // safe to GC again now

                        GameObject go = cb.Spawned;   // cb is a struct, no null check
                        if (go == null) return;

                        if (gen != _generation)
                        {
                            // this run ended before the network finished spawning it - kill it on arrival
                            // instead of leaving it to spin forever as a prop nothing tracks anymore.
                            RemoveGameObject(go);
                            return;
                        }

                        Rigidbody rb = go.GetComponentInChildren<Rigidbody>();
                        if (rb != null)
                        {
                            rb.velocity = launchVel;
                            rb.angularVelocity = spin;
                        }
                        Alive.Add(new Prop { Go = go, Rb = rb, Spin = spin });
                    })
                };

                callbackHandle = info.SpawnCallback;
                _liveCallbacks.Add(callbackHandle);   // rooted: see the _liveCallbacks field comment

                NetworkAssetSpawner.Spawn(info);
                return true;
            }
            catch (Exception e)
            {
                _pending = Math.Max(0, _pending - 1);
                if (callbackHandle != null) _liveCallbacks.Remove(callbackHandle);   // Spawn() threw: it's never going to call back
                R.WarnOnce("propspam-spawn", "[PropSpam] spawn failed (is the AKM barcode installed?): " + e.Message);
                return false;
            }
        }
    }
}