using System;
using BoneCheats.Core;
using Health = Il2CppSLZ.Marrow.Health;
using HealthMode = Il2CppSLZ.Marrow.Health.HealthMode;
using RigManager = Il2CppSLZ.Marrow.RigManager;

namespace BoneCheats.Features
{
    /// <summary>God mode. (Infinite Health was removed - God Mode already sets HealthMode.Invincible, which
    /// makes a health top-up loop redundant on top of it.)</summary>
    internal sealed class PlayerFeature : Feature
    {
        private IntPtr _lastRig = IntPtr.Zero;

        public override string Name => "Player";

        protected override void Apply()
        {
            RigManager rig = Game.Rig;
            if (rig == null) { _lastRig = IntPtr.Zero; return; }
            _lastRig = rig.Pointer;

            Health health = rig.health;
            if (health != null) health.healthMode = Settings.GodMode.Value ? HealthMode.Invincible : HealthMode.Mortal;
        }

        protected override void Reset()
        {
            RigManager rig = Game.Rig;
            Health health = rig != null ? rig.health : null;
            if (health != null) health.healthMode = HealthMode.Mortal;
        }

        protected override void Tick()
        {
            RigManager rig = Game.Rig;
            if (rig == null) return;

            // The rig is rebuilt on respawn/avatar swap; re-apply when we see a new one.
            if (rig.Pointer != _lastRig) Apply();
        }
    }
}
