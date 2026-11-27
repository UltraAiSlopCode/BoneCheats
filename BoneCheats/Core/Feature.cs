using System;
using MelonLoader;
using UnityEngine;

namespace BoneCheats.Core
{
    /// <summary>
    /// Base class for one cheat. Every call into game code goes through the Safe* wrappers, so one broken
    /// feature logs a warning instead of taking the whole mod down. After 5 failures in a row THAT SAME SLOT
    /// (apply/reset/tick/late-tick/fixed-tick) pauses for 10 seconds and then tries again (a feature is never
    /// switched off for good, so a hiccup while a Fusion lobby loads can't permanently kill it).
    ///
    /// The pause is per-slot, not per-feature: each of the 5 slots has its own failure count AND its own
    /// pause timer. Earlier this was one shared timer for all 5 - so a FixedTick that kept throwing (e.g.
    /// noclip scanning a rig mid-respawn) would also pause Apply() for the same 10 s, which is exactly the
    /// call a menu toggle makes to switch a feature off. That looked like "I turned it off and it kept going
    /// for a few seconds anyway" - the toggle really did nothing for up to 10 s, waiting on an unrelated
    /// slot's pause. Per-slot timers mean flipping a setting off always calls Apply() immediately, regardless
    /// of whether Tick/FixedTick happen to be mid-pause.
    /// </summary>
    internal abstract class Feature
    {
        private const int MaxFailures = 5;
        private readonly int[] _failures = new int[5];
        private readonly float[] _pausedUntil = new float[5];

        public abstract string Name { get; }

        protected virtual void Apply() { }
        protected virtual void Reset() { }
        protected virtual void Tick() { }
        protected virtual void LateTick() { }
        protected virtual void FixedTick() { }

        public void ClearFailures()
        {
            for (int i = 0; i < _failures.Length; i++) { _failures[i] = 0; _pausedUntil[i] = 0f; }
        }

        public void SafeApply() => Run(Apply, "apply", 0);
        public void SafeReset() => Run(Reset, "reset", 1);
        public void SafeTick() => Run(Tick, "update", 2);
        public void SafeLateTick() => Run(LateTick, "late update", 3);
        public void SafeFixedTick() => Run(FixedTick, "fixed update", 4);

        private void Run(Action action, string what, int slot)
        {
            if (Time.unscaledTime < _pausedUntil[slot]) return;
            try
            {
                action();
                _failures[slot] = 0;
            }
            catch (Exception e)
            {
                _failures[slot]++;
                if (_failures[slot] <= 2)
                    MelonLogger.Warning($"[{Name}] {what} failed ({_failures[slot]}/{MaxFailures}): {e.Message}");
                if (_failures[slot] >= MaxFailures)
                {
                    _failures[slot] = 0;
                    _pausedUntil[slot] = Time.unscaledTime + 10f;
                    MelonLogger.Error($"[{Name}] {what} keeps failing - paused for 10 s, then it will retry. Last error: {e.Message}");
                }
            }
        }
    }
}