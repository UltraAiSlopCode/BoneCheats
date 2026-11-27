using System;
using BoneCheats.Core;
using LabFusion.Entities;      // EntitySource
using LabFusion.Marrow.Pool;   // LocalAssetSpawner
using LabFusion.RPC;           // NetworkAssetSpawner
using LabFusion.Scene;         // NetworkSceneManager
using UnityEngine;
using AssetSpawner = Il2CppSLZ.Marrow.Pool.AssetSpawner;
using MarrowSpawnable = Il2CppSLZ.Marrow.Data.Spawnable;
using MarrowSpawnPolicyData = Il2CppSLZ.Marrow.Data.SpawnPolicyData;
using MarrowCrateRef = Il2CppSLZ.Marrow.Warehouse.SpawnableCrateReference;

namespace BoneCheats.Features
{
    /// <summary>
    /// One-shot spawn for the Item Spawner menu (Guns / NPCs).
    ///
    /// Two paths, same idea SpawnLab uses (checked against its DLL and the installed LabFusion / Marrow DLLs):
    ///  - In a Fusion lobby (NetworkSceneManager.IsLevelNetworked): ask Fusion's NetworkAssetSpawner, with
    ///    SpawnSource = EntitySource.Player. The old code never set SpawnSource, so it defaulted to None and the
    ///    request wasn't treated as a player spawn.
    ///  - Offline / single player: Fusion's network spawner has nothing to talk to, so the crate is spawned
    ///    straight through the game's own AssetSpawner. The old code always used the network path, which is why
    ///    nothing happened outside a lobby.
    /// Any failure is logged once and swallowed; the menu keeps working.
    /// </summary>
    internal static class Spawner
    {
        private const float SpawnDistance = 1.25f;

        public static void SpawnBarcode(string barcode)
        {
            if (string.IsNullOrEmpty(barcode)) return;

            Transform head = Game.Head;
            if (head == null || Game.Rig == null) { Game.Notify("Load into a level before spawning an item."); return; }

            GetSpawnPose(head, out Vector3 pos, out Quaternion rot);

            bool networked = false;
            try { networked = NetworkSceneManager.IsLevelNetworked; }
            catch (Exception e) { R.WarnOnce("itemspawner-net", "[ItemSpawner] couldn't read Fusion's network state, spawning locally: " + e.Message); }

            try
            {
                if (networked) SpawnNetworked(barcode, pos, rot);
                else SpawnLocal(barcode, pos, rot);
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning("[ItemSpawner] spawn of '" + barcode + "' failed (" + (networked ? "networked" : "local") + "): " + e);
                Game.Notify("Spawn failed - see the MelonLoader log.");
            }
        }

        private static void SpawnNetworked(string barcode, Vector3 pos, Quaternion rot)
        {
            var spawnable = LocalAssetSpawner.CreateSpawnable(barcode);
            if (spawnable == null) throw new InvalidOperationException("Fusion returned no spawnable for '" + barcode + "'");

            var info = new NetworkAssetSpawner.SpawnRequestInfo
            {
                Spawnable = spawnable,
                Position = pos,
                Rotation = rot,
                SpawnEffect = true,
                SpawnSource = EntitySource.Player,
                SpawnCallback = (Action<NetworkAssetSpawner.SpawnCallbackInfo>)(cb => { /* one-shot */ })
            };

            NetworkAssetSpawner.Spawn(info);
        }

        private static void SpawnLocal(string barcode, Vector3 pos, Quaternion rot)
        {
            var spawnable = new MarrowSpawnable
            {
                crateRef = new MarrowCrateRef(barcode),
                policyData = new MarrowSpawnPolicyData()
            };

            AssetSpawner.Register(spawnable);
            AssetSpawner.Spawn(spawnable, pos, rot,
                new Il2CppSystem.Nullable<Vector3>(), null, false,
                new Il2CppSystem.Nullable<int>(), null, null);
        }

        private static void GetSpawnPose(Transform head, out Vector3 pos, out Quaternion rot)
        {
            Vector3 fwd = Game.Flat(head.forward);
            pos = head.position + fwd * SpawnDistance;
            rot = Quaternion.LookRotation(fwd);
        }
    }
}
