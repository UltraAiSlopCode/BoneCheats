using System;
using System.Collections.Generic;
using BoneCheats.Core;
using Il2CppSLZ.Marrow.Combat;        // Attack
using Il2CppSLZ.Marrow.Data;          // AttackType
using LabFusion.Senders;              // PlayerSender
using MelonLoader;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Per-player actions for the Players page: Kill, Teleport To, Annoy.
    /// Players are addressed by Fusion's SmallID (byte), looked up fresh each time so a stale entry can't crash.
    /// </summary>
    internal static class PlayerActions
    {
        // ---- tuning ----
        private const float KillDamage = 99999f;
        private const float PokeDamage = 0.5f;     // annoy = tiny hits, not lethal on their own
        private const float PokeInterval = 0.25f;  // 4 pokes / second
        private const float AnnoyMaxSeconds = 15f; // auto-stops so it can't kill someone by accident
        private const float KillLoopInterval = 3f; // seconds between kills in Kill (loop)

        private sealed class AnnoyState { public float Next; public float Until; }
        private static readonly Dictionary<byte, AnnoyState> Annoying = new Dictionary<byte, AnnoyState>();
        private static bool _annoyAllEnabled;

        public static byte IdOf(Plr p) { return p.Fusion.PlayerID.SmallID; }

        public static Plr Find(byte id)
        {
            foreach (Plr p in Players.All)
            {
                try { if (IdOf(p) == id) return p; } catch { }
            }
            return null;
        }

        // ---------------- Kill ----------------

        public static void Kill(byte id)
        {
            Plr p = Find(id);
            if (p == null) { Game.Notify("Player isn't in the lobby anymore."); return; }
            SendHit(p, KillDamage);
            // no popup on purpose - silent. (Log line only.)
            MelonLogger.Msg("[Players] kill sent to " + p.Name);
        }

        /// <summary>Lobby > Kill All: sends the kill hit to every other player currently in the lobby, one by one.</summary>
        public static void KillAll()
        {
            int n = 0;
            foreach (Plr p in Players.All)
            {
                try { SendHit(p, KillDamage); n++; }
                catch { /* one bad entry shouldn't stop the rest */ }
            }
            MelonLogger.Msg("[Players] kill all sent to " + n + " player(s)");
        }

        // ---------------- Kill (loop) ----------------

        private static readonly Dictionary<byte, float> KillLoops = new Dictionary<byte, float>();
        private static bool _killLoopAllEnabled;

        public static bool IsKillLooping(byte id) { return KillLoops.ContainsKey(id); }

        public static void SetKillLoop(byte id, bool on)
        {
            if (on) KillLoops[id] = 0f; else KillLoops.Remove(id);
        }

        public static void ClearKillLoops() { KillLoops.Clear(); _killLoopAllEnabled = false; }

        /// <summary>Lobby > Kill Loop All toggle: while on, every other player in the lobby (including anyone
        /// who joins afterward) gets added to the kill loop. Switch it off and whoever is still looping keeps
        /// looping until their own per-player toggle is switched off too (same as Annoy All).</summary>
        public static bool IsKillLoopAllOn() { return _killLoopAllEnabled; }
        public static void SetKillLoopAll(bool on) { _killLoopAllEnabled = on; }

        private static readonly List<byte> LoopDone = new List<byte>();
        private static readonly List<byte> LoopIds = new List<byte>();

        /// <summary>Called every frame from PlayerActionsFeature.</summary>
        public static void TickKillLoop()
        {
            float now = Time.unscaledTime;

            if (_killLoopAllEnabled)
            {
                foreach (Plr p in Players.All)
                {
                    byte id;
                    try { id = IdOf(p); } catch { continue; }
                    if (!KillLoops.ContainsKey(id)) KillLoops[id] = 0f;   // fires on the very next tick
                }
            }

            if (KillLoops.Count == 0) return;
            LoopDone.Clear();
            LoopIds.Clear();
            foreach (byte id in KillLoops.Keys) LoopIds.Add(id);
            foreach (byte id in LoopIds)
            {
                Plr p = Find(id);
                if (p == null) { LoopDone.Add(id); continue; }      // they left
                if (now < KillLoops[id]) continue;
                KillLoops[id] = now + KillLoopInterval;
                SendHit(p, KillDamage);
            }
            foreach (byte id in LoopDone) KillLoops.Remove(id);
        }

        // ---------------- Teleport To ----------------

        public static void TeleportTo(byte id)
        {
            Plr p = Find(id);
            if (p == null) { Game.Notify("Player isn't in the lobby anymore."); return; }
            Vector3 head;
            if (!p.TryHead(out head)) { Game.Notify("Can't see " + p.Name + " right now."); return; }

            // Land ~1 m next to them (on the side we're coming from).
            Transform me = Game.Head;
            Vector3 away = me != null ? Game.Flat(me.position - head) : Vector3.back;
            Vector3 spot = head + away * 1f;

            // Find the floor instead of guessing from their head height: a dead / ragdolled player's head is
            // almost on the ground, so "head minus 1.5 m" would put our feet under the floor and we'd fall through.
            // The lowest thing hit within ~2 m under their head is the floor (their own ragdoll sits above it).
            float feetY = head.y;   // fallback if no floor is found: right at head height, we just drop a little
            RaycastHit[] hits = Physics.RaycastAll(spot + Vector3.up * 0.5f, Vector3.down, 2.4f, ~0, QueryTriggerInteraction.Ignore);
            bool found = false; float lowest = float.MaxValue;
            foreach (RaycastHit h in hits)
            {
                if (h.point.y < lowest) { lowest = h.point.y; found = true; }
            }
            if (found) feetY = lowest + 0.15f;

            if (!Game.TeleportTo(new Vector3(spot.x, feetY, spot.z))) Game.Notify("Teleport failed (no rig).");
        }

        // ---------------- Annoy (poke spam) ----------------

        public static bool IsAnnoying(byte id) { return Annoying.ContainsKey(id); }

        public static void SetAnnoy(byte id, bool on)
        {
            if (!on) { Annoying.Remove(id); return; }
            Annoying[id] = new AnnoyState { Next = 0f, Until = Time.unscaledTime + AnnoyMaxSeconds };
        }

        public static void ClearAnnoy() { Annoying.Clear(); _annoyAllEnabled = false; }

        /// <summary>Lobby > Annoy All toggle: while on, every other player in the lobby (including anyone who
        /// joins afterward) gets continuously poke-spammed, refreshed every tick so the normal 15 s auto-stop
        /// never kicks in. Switch it off and each player's own timer just runs out within 15 s, same as always.</summary>
        public static bool IsAnnoyAllOn() { return _annoyAllEnabled; }
        public static void SetAnnoyAll(bool on) { _annoyAllEnabled = on; }

        private static readonly List<byte> Done = new List<byte>();

        /// <summary>Called every frame from PlayerActionsFeature.</summary>
        public static void TickAnnoy()
        {
            float now = Time.unscaledTime;

            if (_annoyAllEnabled)
            {
                foreach (Plr p in Players.All)
                {
                    byte id;
                    try { id = IdOf(p); } catch { continue; }
                    AnnoyState st;
                    if (!Annoying.TryGetValue(id, out st)) { st = new AnnoyState { Next = 0f }; Annoying[id] = st; }
                    st.Until = now + AnnoyMaxSeconds;   // keep extending while the master toggle is on
                }
            }

            if (Annoying.Count == 0) return;
            Done.Clear();
            foreach (var kv in Annoying)
            {
                Plr p = Find(kv.Key);
                if (p == null || now >= kv.Value.Until) { Done.Add(kv.Key); continue; }
                if (now < kv.Value.Next) continue;
                kv.Value.Next = now + PokeInterval;
                SendHit(p, PokeDamage);
            }
            foreach (byte id in Done) Annoying.Remove(id);
        }

        // ---------------- shared ----------------

        /// <summary>
        /// Sends the same "you got hit" message Fusion sends when a bullet lands on another player.
        /// NOTE: the Attack members below (damage / origin / normal / attackType) are from Il2CppSLZ.Marrow.Combat.Attack.
        /// If the compiler complains about one of them, that's the one line to adjust (see the README note).
        /// </summary>
        private static void SendHit(Plr p, float damage)
        {
            Vector3 pos;
            if (!p.TryHead(out pos)) pos = Vector3.zero;

            Attack atk = new Attack();
            atk.damage = damage;
            atk.origin = pos;
            atk.normal = Vector3.up;
            atk.attackType = AttackType.Piercing;

            PlayerSender.SendPlayerDamage(IdOf(p), atk);
        }
    }

    /// <summary>Ticks the annoy spam and keeps the Players page in sync with who's in the lobby.</summary>
    internal sealed class PlayerActionsFeature : Feature
    {
        public override string Name { get { return "PlayerActions"; } }

        protected override void Tick()
        {
            Players.Refresh(false);
            PlayerActions.TickAnnoy();
            PlayerActions.TickKillLoop();
            PropSpam.Tick();
            UI.PlayersPage.SyncIfChanged();
        }

        protected override void Reset() { PlayerActions.ClearAnnoy(); PlayerActions.ClearKillLoops(); PropSpam.ClearAll(); }
    }
}
