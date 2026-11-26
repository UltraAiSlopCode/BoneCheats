using System;
using BoneCheats.Core;
using LabFusion.Marrow.Pool;   // LocalAssetSpawner
using LabFusion.RPC;           // NetworkAssetSpawner
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// One-shot networked spawn for the Item Spawner menu (Guns / NPCs). Goes through the exact same Fusion
    /// path PropSpam already uses - LocalAssetSpawner.CreateSpawnable + NetworkAssetSpawner.Spawn - so a gun
    /// or NPC spawned from the menu is a real, replicated object every player in the lobby sees, not a
    /// local-only spawn that desyncs the moment someone else looks at it.
    ///
    /// Spawn permission is whatever LabFusion itself enforces (e.g. a host restricting who can spawn): this
    /// just asks the network spawner and does nothing further. If the request is declined or throws, it's
    /// logged once and swallowed - no exception reaches the menu, no crash, and no automatic retry. The menu
    /// itself never rebuilds or disables itself because of a failed spawn, so browsing/searching/paging keep
    /// working regardless of whether the player is allowed to spawn.
    /// </summary>
    internal static class Spawner
    {
        private const float SpawnDistance = 1.25f;

        public static void SpawnBarcode(string barcode)
        {
            if (string.IsNullOrEmpty(barcode)) return;

            try
            {
                GetSpawnPose(out Vector3 pos, out Quaternion rot);

                // 'var' on purpose, same as PropSpam: the Spawnable type's namespace differs between
                // game/Fusion versions, so it's never named - the compiler takes it from CreateSpawnable.
                var spawnable = LocalAssetSpawner.CreateSpawnable(barcode);

                var info = new NetworkAssetSpawner.SpawnRequestInfo
                {
                    Spawnable = spawnable,
                    Position = pos,
                    Rotation = rot,
                    SpawnEffect = true,
                    SpawnCallback = (Action<NetworkAssetSpawner.SpawnCallbackInfo>)(cb => { /* one-shot: nothing to track afterwards */ })
                };

                NetworkAssetSpawner.Spawn(info);
            }
            catch (Exception e)
            {
                // No permission, a bad/uninstalled barcode, or anything else the spawner can throw all land
                // here: log once, ignore, let the player keep browsing. Never thrown back at the menu code.
                R.WarnOnce("itemspawner-spawn", "[ItemSpawner] spawn request failed (missing permission, or the asset isn't installed): " + e.Message);
            }
        }

        private static void GetSpawnPose(out Vector3 pos, out Quaternion rot)
        {
            Transform head = Game.Head;
            if (head == null) { pos = Vector3.zero; rot = Quaternion.identity; return; }

            Vector3 fwd = Game.Flat(head.forward);
            pos = head.position + fwd * SpawnDistance;
            rot = Quaternion.LookRotation(fwd);
        }
    }
}
