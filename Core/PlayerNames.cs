using System;
using System.Collections.Generic;
using LabFusion.Entities;
using UnityEngine;

namespace BoneCheats.Core
{
    /// <summary>One other player as Fusion reports it.</summary>
    internal sealed class FusionEntry
    {
        public Component Rig;
        public string Name;
        public NetworkPlayer Np;
    }

    /// <summary>
    /// Reads Fusion's player list: NetworkPlayer.Players (static) -> each player's PlayerID.IsMe,
    /// RigRefs.RigManager and Username. Real LabFusion.dll types.
    /// </summary>
    internal static class PlayerNames
    {
        public static readonly List<FusionEntry> Entries = new List<FusionEntry>();
        private static float _next;

        public static void Clear() { Entries.Clear(); _next = 0f; }

        public static void Refresh(bool force)
        {
            float now = Time.unscaledTime;
            if (!force && now < _next) return;
            _next = now + 1f;
            Entries.Clear();

            foreach (NetworkPlayer np in NetworkPlayer.Players)
            {
                try
                {
                    if (np == null || np.PlayerID == null || np.PlayerID.IsMe) continue;
                    RigRefs refs = np.RigRefs;
                    if (refs == null || refs.RigManager == null) continue;
                    Entries.Add(new FusionEntry { Rig = refs.RigManager, Np = np, Name = Clean(np.Username) });
                }
                catch (Exception e) { R.WarnOnce("fusion-entry", "[Players] couldn't read a Fusion player: " + e.Message); }
            }
        }

        // Usernames can contain <color=...> rich text; strip it so the label stays readable.
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var sb = new System.Text.StringBuilder();
            bool tag = false;
            foreach (char ch in s)
            {
                if (ch == '<') tag = true;
                else if (ch == '>' && tag) tag = false;
                else if (!tag) sb.Append(ch);
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? s.Trim() : r;
        }
    }
}
