using System.Text;
using BoneCheats.Core;
using BoneCheats.Features;
using BoneLib.BoneMenu;
using UnityEngine;

namespace BoneCheats.UI
{
    /// <summary>
    /// BoneMenu > Cheats > Players. One sub-page per player in the lobby with Kill / Teleport To / Annoy.
    /// BoneMenu pages are static, so the page is emptied and refilled whenever the player list changes
    /// (checked twice a second) or when you press "Refresh".
    /// </summary>
    internal static class PlayersPage
    {
        private static Page _page;
        private static string _signature = null;
        private static float _next;

        public static void Build(Page root)
        {
            _page = root.CreatePage("Players", Color.yellow);
            Rebuild();
        }

        public static void SyncIfChanged()
        {
            if (_page == null) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.5f;
            if (Signature() != _signature) Rebuild();
        }

        private static string Signature()
        {
            var sb = new StringBuilder();
            foreach (Plr p in Players.All)
            {
                try { sb.Append(PlayerActions.IdOf(p)).Append(':').Append(p.Name).Append(';'); } catch { }
            }
            return sb.ToString();
        }

        private static void Rebuild()
        {
            _signature = Signature();
            _page.RemoveAll();
            _page.CreateFunction("Refresh", Color.green, Rebuild);

            if (Players.All.Count == 0)
            {
                _page.CreateFunction("(no other players)", Color.gray, () => { });
                return;
            }

            foreach (Plr p in Players.All)
            {
                byte id; string name;
                try { id = PlayerActions.IdOf(p); name = string.IsNullOrEmpty(p.Name) ? "Player " + id : p.Name; }
                catch { continue; }

                Page pp = _page.CreatePage(name, Color.white);
                pp.CreateFunction("Kill", Color.red, () => PlayerActions.Kill(id));
                pp.CreateBool("Kill (loop)", Color.red, PlayerActions.IsKillLooping(id), v => PlayerActions.SetKillLoop(id, v));
                pp.CreateFunction("Clone Avatar", Color.magenta, () => PlayerActions.CloneAvatar(id));
                pp.CreateFunction("Teleport To", Color.cyan, () => PlayerActions.TeleportTo(id));
                pp.CreateBool("Annoy (Poke spam)", Color.yellow, PlayerActions.IsAnnoying(id), v => PlayerActions.SetAnnoy(id, v));
            }
        }
    }
}
