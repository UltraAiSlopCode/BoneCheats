using System;
using System.Collections.Generic;
using BoneCheats.Core;
using Il2CppSLZ.Marrow.Combat;        // Attack
using Il2CppSLZ.Marrow.Data;          // AttackType
using LabFusion.Player;               // LocalPlayer
using LabFusion.Senders;              // PlayerSender
using MelonLoader;
using UnityEngine;
using Barcode = Il2CppSLZ.Marrow.Warehouse.Barcode;
using RigManager = Il2CppSLZ.Marrow.RigManager;

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
        private const float KillLoopInterval = 1f; // seconds between kills in Kill (loop)

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
        private static readonly HashSet<byte> KillLoopExcluded = new HashSet<byte>();   // see SetKillLoop below
        private static bool _killLoopAllEnabled;

        public static bool IsKillLooping(byte id) { return KillLoops.ContainsKey(id); }

        /// <summary>
        /// Turns one player's kill loop on/off. BUGFIX: while "Kill Loop All" is on, this used to get silently
        /// undone a frame later - TickKillLoop's "add anyone missing" pass for the All toggle would see the
        /// player you just turned off, find them missing from KillLoops, and add them right back, so turning an
        /// individual player off looked like it did nothing (or "stopped for a moment, then kept killing"). Now
        /// turning a player off while All is on remembers that as an exclusion, so the All pass leaves them alone
        /// until All itself is switched off and on again (see SetKillLoopAll).
        /// </summary>
        public static void SetKillLoop(byte id, bool on)
        {
            if (on) { KillLoopExcluded.Remove(id); KillLoops[id] = 0f; }
            else { KillLoopExcluded.Add(id); KillLoops.Remove(id); }
        }

        public static void ClearKillLoops() { KillLoops.Clear(); KillLoopExcluded.Clear(); _killLoopAllEnabled = false; }

        /// <summary>Lobby > Kill Loop All toggle: while on, every other player in the lobby (including anyone
        /// who joins afterward) gets added to the kill loop, except anyone individually turned off since (see
        /// SetKillLoop). Switching it off stops everyone immediately - it clears every loop this toggle is
        /// responsible for, not just new ones - and switching it back on clears the exclusion list, so All once
        /// again covers the whole lobby.</summary>
        public static bool IsKillLoopAllOn() { return _killLoopAllEnabled; }
        public static void SetKillLoopAll(bool on)
        {
            _killLoopAllEnabled = on;
            KillLoopExcluded.Clear();
            if (!on) KillLoops.Clear();
        }

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
                    if (KillLoopExcluded.Contains(id)) continue;          // you turned this one off - leave them be
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

        // ---------------- Clone Name ----------------

        private static string _savedUsername;      // your real name, remembered the first time you clone one
        private static bool _nameCloned;

        /// <summary>
        /// Sets your own displayed name to this player's name. Goes through LabFusion.Player.LocalPlayer.Username,
        /// which is the game's own "change my username" path (it has its own OnUsernameChanged event, so Fusion
        /// is already set up to re-sync it to everyone, the same as if you'd typed a new name yourself).
        /// Your real name is remembered the first time so Restore Name can put it back.
        /// </summary>
        public static void CloneName(byte id)
        {
            Plr p = Find(id);
            if (p == null) return;                                  // they left: nothing to do, silently
            string name = p.Name;
            if (string.IsNullOrEmpty(name)) return;

            if (!_nameCloned) { _savedUsername = LocalPlayer.Username; _nameCloned = true; }

            LocalPlayer.Username = name;
            MelonLogger.Msg("[Players] cloned " + name + "'s name.");
        }

        /// <summary>Puts your own name back the way it was before the first Clone Name.</summary>
        public static void RestoreName()
        {
            if (!_nameCloned) return;
            LocalPlayer.Username = _savedUsername;
            _nameCloned = false;
            MelonLogger.Msg("[Players] name restored.");
        }

        // ---------------- Clone Avatar ----------------

        /// <summary>
        /// Puts the same avatar on you that this player is wearing. The avatar's barcode comes from Fusion
        /// (NetworkPlayer.AvatarSetter.AvatarBarcode; falls back to their rig's avatarID), and the swap is the
        /// game's own RigManager.SwapAvatarCrate, so Fusion syncs it to everyone like a normal avatar change.
        /// If the avatar isn't installed on your PC the game can't load it and you keep your current one.
        ///
        /// Restore My Avatar used to do one swap call and forget about it, using whatever RigManager.avatarID said
        /// at clone time. Now: the real avatar is tracked while you are NOT cloned (so it is known before any swap
        /// starts), it is read from the rig's AvatarCrate first, and a restore keeps going until the rig reports
        /// the original avatar again (re-sending the swap every 2.5 s, up to 4 times) instead of firing once.
        /// </summary>
        private static string _savedAvatar;      // your real avatar's barcode, remembered the first time you clone one
        private static bool _avatarCloned;
        private static string _baselineAvatar;   // last avatar seen on you while not cloned
        private static float _nextBaseline;
        private static string _restoreTarget;    // non-null while a restore is in progress
        private static int _restoreTries;
        private static float _restoreNext;
        private static float _restoreUntil;

        /// <summary>The barcode of the avatar currently on YOUR rig (AvatarCrate first, RigManager.avatarID as fallback).</summary>
        private static string OwnAvatarId(RigManager me)
        {
            if (me == null) return null;
            try
            {
                object crate = R.GetRaw(me, "AvatarCrate");
                object barcode = R.GetRaw(crate, "Barcode");
                string id = R.GetRaw(barcode, "ID") as string;
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch { }
            try { if (!string.IsNullOrEmpty(me.avatarID)) return me.avatarID; } catch { }
            return null;
        }

        /// <summary>Called every tick: remembers your real avatar while you aren't cloned, and drives a pending restore.</summary>
        public static void TickAvatar()
        {
            float now = Time.unscaledTime;

            if (_restoreTarget != null) { StepRestore(now); return; }
            if (_avatarCloned || now < _nextBaseline) return;
            _nextBaseline = now + 1f;

            string id = OwnAvatarId(Game.Rig);
            if (!string.IsNullOrEmpty(id)) _baselineAvatar = id;
        }

        public static void CloneAvatar(byte id)
        {
            Plr p = Find(id);
            if (p == null) return;                                  // they left: nothing to do, silently

            string barcode = null;
            try
            {
                LabFusion.Entities.RigAvatarSetter setter = p.Fusion.AvatarSetter;
                if (setter != null) barcode = setter.AvatarBarcode;
            }
            catch { }
            if (string.IsNullOrEmpty(barcode))
            {
                RigManager theirs = p.Rig.TryCast<RigManager>();
                if (theirs != null) barcode = theirs.avatarID;
            }
            if (string.IsNullOrEmpty(barcode)) { MelonLogger.Msg("[Players] couldn't read " + p.Name + "'s avatar."); return; }

            RigManager me = Game.Rig;
            if (me == null) return;

            string current = OwnAvatarId(me);
            if (current == barcode) return;

            _restoreTarget = null;                                   // a new clone cancels any pending restore
            if (!_avatarCloned)
            {
                // the tracked baseline was read while no swap was running, so it beats reading the rig mid-swap
                string real = !string.IsNullOrEmpty(_baselineAvatar) ? _baselineAvatar : current;
                if (!string.IsNullOrEmpty(real)) { _savedAvatar = real; _avatarCloned = true; }
                else MelonLogger.Warning("[Players] couldn't read your current avatar - Restore My Avatar won't know what to go back to.");
            }

            me.SwapAvatarCrate(new Barcode(barcode), false, null);
            MelonLogger.Msg("[Players] cloning " + p.Name + "'s avatar: " + barcode);
        }

        /// <summary>Puts your own avatar back the way it was before the first Clone Avatar.</summary>
        public static void RestoreAvatar()
        {
            string target = !string.IsNullOrEmpty(_savedAvatar) && _avatarCloned ? _savedAvatar : _baselineAvatar;
            if (string.IsNullOrEmpty(target)) { Game.Notify("No original avatar on record yet."); return; }

            RigManager me = Game.Rig;
            if (me == null) { Game.Notify("No rig to restore onto."); return; }

            if (!_avatarCloned && OwnAvatarId(me) == target) { Game.Notify("You're already on your own avatar."); return; }

            _restoreTarget = target;
            _restoreTries = 0;
            _restoreNext = 0f;
            _restoreUntil = Time.unscaledTime + 20f;
            StepRestore(Time.unscaledTime);
        }

        private static void StepRestore(float now)
        {
            if (_restoreTarget == null || now < _restoreNext) return;

            RigManager me = Game.Rig;
            if (me == null)
            {
                if (now > _restoreUntil) { _restoreTarget = null; }     // no rig for 20 s (loading screen?): give up quietly
                return;
            }

            // done: the rig reports the original avatar again
            if (_restoreTries > 0 && OwnAvatarId(me) == _restoreTarget)
            {
                _restoreTarget = null;
                _avatarCloned = false;
                _baselineAvatar = OwnAvatarId(me);
                MelonLogger.Msg("[Players] avatar restored.");
                Game.Notify("Avatar restored.");
                return;
            }

            if (_restoreTries >= 4 || now > _restoreUntil)
            {
                MelonLogger.Warning("[Players] restore sent " + _restoreTries + " time(s) but the rig never reported the original avatar - is it installed? Press Restore again to retry.");
                _restoreTarget = null;                                   // _avatarCloned stays set so another press can retry
                return;
            }

            try { me.SwapAvatarCrate(new Barcode(_restoreTarget), false, null); }
            catch (Exception e) { MelonLogger.Warning("[Players] restore swap failed: " + e.Message); }
            _restoreTries++;
            _restoreNext = now + 2.5f;
            MelonLogger.Msg("[Players] restoring avatar (attempt " + _restoreTries + "): " + _restoreTarget);
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

        private static readonly HashSet<byte> AnnoyExcluded = new HashSet<byte>();   // see SetAnnoy below

        public static bool IsAnnoying(byte id) { return Annoying.ContainsKey(id); }

        /// <summary>Same exclusion fix as SetKillLoop: while "Annoy All" is on, turning one player off is
        /// remembered so the All pass below doesn't add them straight back on the next tick.</summary>
        public static void SetAnnoy(byte id, bool on)
        {
            if (on) { AnnoyExcluded.Remove(id); Annoying[id] = new AnnoyState { Next = 0f, Until = Time.unscaledTime + AnnoyMaxSeconds }; }
            else { AnnoyExcluded.Add(id); Annoying.Remove(id); }
        }

        public static void ClearAnnoy() { Annoying.Clear(); AnnoyExcluded.Clear(); _annoyAllEnabled = false; }

        /// <summary>Lobby > Annoy All toggle: while on, every other player in the lobby (including anyone who
        /// joins afterward) gets continuously poke-spammed, except anyone individually turned off since (see
        /// SetAnnoy); their state is refreshed every tick so the normal 15 s auto-stop never kicks in for them.
        /// Switching All off stops everyone immediately instead of waiting out that 15 s, and switching it back
        /// on clears the exclusion list, so All once again covers the whole lobby.</summary>
        public static bool IsAnnoyAllOn() { return _annoyAllEnabled; }
        public static void SetAnnoyAll(bool on)
        {
            _annoyAllEnabled = on;
            AnnoyExcluded.Clear();
            if (!on) Annoying.Clear();
        }

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
                    if (AnnoyExcluded.Contains(id)) continue;             // you turned this one off - leave them be
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
            PlayerActions.TickAvatar();
            PropSpam.Tick();
            UI.MenuBuilder.SyncPropSpamToggle();
            UI.PlayersPage.SyncIfChanged();
        }

        protected override void Reset() { PlayerActions.ClearAnnoy(); PlayerActions.ClearKillLoops(); PropSpam.ClearAll(); }
    }
}