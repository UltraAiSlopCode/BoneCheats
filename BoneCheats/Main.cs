using BoneCheats.Core;
using BoneCheats.Features;
using BoneCheats.UI;
using MelonLoader;

[assembly: MelonInfo(typeof(BoneCheats.Main), "BoneCheats", "1.1.0", "HoodRape")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace BoneCheats
{
    public class Main : MelonMod
    {
        public override void OnInitializeMelon()
        {
            Settings.Init();
            MenuBuilder.Build();
            GunHooks.TryInstall(HarmonyInstance);
            InstallFusionHooks();
            AntiRagdollFeature.TryInstall(HarmonyInstance);
            InstallAntiGrab();
            LoggerInstance.Msg("Loaded - look for 'Cheats' in BoneMenu.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Players.Clear();
            CombatFeature.OnSceneInit();
            InstallFusionHooks();                   // no-op once hooked
            AntiRagdollFeature.TryInstall(HarmonyInstance);
            InstallAntiGrab();
            GunHooks.TryInstall(HarmonyInstance);   // no-op once installed; retries if the game wasn't ready yet
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

        public override void OnUpdate() { FeatureManager.Tick(); }
        public override void OnLateUpdate() { FeatureManager.LateTick(); }
        public override void OnFixedUpdate() { FeatureManager.FixedTick(); }
    }
}
