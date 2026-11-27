using BoneCheats.Core;
using BoneCheats.Features;
using BoneCheats.UI;
using MelonLoader;

[assembly: MelonInfo(typeof(BoneCheats.Main), "BoneCheats", BoneCheats.Core.Updater.CurrentVersion, "HoodRape")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace BoneCheats
{
    public class Main : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Settings.Init();
            try { Updater.Start(); } catch { }   // the updater must never keep the mod from loading
            MenuBuilder.Build();
            GunHooks.TryInstall(HarmonyInstance);
            NoRecoil.TryInstall(HarmonyInstance);
            InstallFusionHooks();
            PlayerFeature.TryInstallHooks(HarmonyInstance);
            InstallAntiGrab();
            LoggerInstance.Msg("Loaded - look for 'Cheats' in BoneMenu.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Players.Clear();
            CombatFeature.OnSceneInit();
            InstallFusionHooks();                   // no-op once hooked
            PlayerFeature.TryInstallHooks(HarmonyInstance);
            InstallAntiGrab();
            GunHooks.TryInstall(HarmonyInstance);   // no-op once installed; retries if the game wasn't ready yet
            NoRecoil.TryInstall(HarmonyInstance);
            FeatureManager.OnSceneInitialized();
        }

        // LabFusion is a hard reference now: if it isn't installed this logs one line instead of killing the mod.
        private void InstallFusionHooks()
        {
            try { FusionCull.TryInstall(HarmonyInstance); }
            catch (System.Exception e) { R.WarnOnce("fusion-missing", "[BoneCheats] LabFusion not available: " + e.Message); }
        }

        // Needs LabFusion; if it isn't installed this logs one line instead of killing the mod.
        private void InstallAntiGrab()
        {
            try { AntiGrab.TryInstall(HarmonyInstance); }
            catch (System.Exception e) { R.WarnOnce("antigrab-missing", "[BoneCheats] Anti Grab unavailable: " + e.Message); }
        }

        public override void OnUpdate() { FeatureManager.Tick(); ItemSpawnerPage.Tick(); Updater.Tick(); }
        public override void OnLateUpdate() { FeatureManager.LateTick(); }
        public override void OnFixedUpdate() { FeatureManager.FixedTick(); }
    }
}
