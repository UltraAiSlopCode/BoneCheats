using System;
using MelonLoader;
using UnityEngine;

namespace BoneCheats.Core
{
    /// <summary>
    /// Base class for one cheat. Every call into game code goes through the Safe* wrappers, so one broken
    /// feature logs a warning instead of taking the whole mod down. After 5 failures in a row a feature
    /// pauses for 10 seconds and then tries again (it is never switched off for good, so a hiccup while a
    /// Fusion lobby loads can't permanently kill it).
    /// </summary>
    internal abstract class Feature
    {
        private const int MaxFailures = 5;
        private readonly int[] _failures = new int[5];
        private float _pausedUntil;

        public abstract string Name { get; }

        protected virtual void Apply() { }
        protected virtual void Reset() { }
        protected virtual void Tick() { }
        protected virtual void LateTick() { }
        protected virtual void FixedTick() { }

        public void ClearFailures() { for (int i = 0; i < _failures.Length; i++) _failures[i] = 0; _pausedUntil = 0f; }

        public void SafeApply() => Run(Apply, "apply", 0);
        public void SafeReset() => Run(Reset, "reset", 1);
        public void SafeTick() => Run(Tick, "update", 2);
        public void SafeLateTick() => Run(LateTick, "late update", 3);
        public void SafeFixedTick() => Run(FixedTick, "fixed update", 4);

        private void Run(Action action, string what, int slot)
        {
            if (Time.unscaledTime < _pausedUntil) return;
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
                    _pausedUntil = Time.unscaledTime + 10f;
                    MelonLogger.Error($"[{Name}] {what} keeps failing - paused for 10 s, then it will retry. Last error: {e.Message}");
                }
            }
        }
    }
}
