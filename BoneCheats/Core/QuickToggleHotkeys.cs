using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>
    /// Controller-only shortcut for Settings > Quick Toggles - no laser pointer or menu dive needed:
    ///
    ///   Hold BOTH grips           -> opens quick-select (a popup names the currently highlighted toggle)
    ///   Right stick up/down       -> moves the highlight through the list
    ///   Right trigger             -> flips the highlighted toggle on/off
    ///   Let go of either grip     -> closes it again
    ///
    /// The list is Core/QuickToggles.cs - the exact same one Settings > Quick Toggles renders - so this can
    /// never fall out of sync with the menu. Both grips have to stay held for any of this to do anything, so
    /// it can't fire by accident while you're just holding a gun or climbing with both hands.
    /// </summary>
    internal sealed class QuickToggleHotkeys : Feature
    {
        private const float MoveDeadzone = 0.6f;
        private const float MoveRepeat = 0.35f;   // seconds between cursor steps while the stick stays tilted

        private int _index;
        private bool _chordWasActive;
        private bool _prevTrigger;
        private float _nextMove;

        public override string Name => "QuickToggleHotkeys";

        protected override void Reset()
        {
            _chordWasActive = false;
            _prevTrigger = false;
        }

        protected override void Tick()
        {
            bool chord = ControllerInput.Grip(true) && ControllerInput.Grip(false);

            if (!chord)
            {
                _chordWasActive = false;
                _prevTrigger = false;
                return;
            }

            List<QuickToggle> list = QuickToggles.All;
            if (list.Count == 0) return;
            if (_index >= list.Count) _index = 0;

            if (!_chordWasActive)
            {
                _chordWasActive = true;
                _nextMove = 0f;
                Announce(list[_index], list[_index].Entry.Value ? "ON" : "OFF");
            }

            float now = Time.unscaledTime;
            Vector2 stick = Game.Stick(true);
            if (now >= _nextMove && Mathf.Abs(stick.y) > MoveDeadzone)
            {
                _nextMove = now + MoveRepeat;
                _index = stick.y > 0f
                    ? (_index - 1 + list.Count) % list.Count
                    : (_index + 1) % list.Count;
                Announce(list[_index], list[_index].Entry.Value ? "ON" : "OFF");
            }

            bool trigger = ControllerInput.Trigger(true);
            if (trigger && !_prevTrigger)
            {
                QuickToggle qt = list[_index];
                qt.Entry.Value = !qt.Entry.Value;
                FeatureManager.ApplyAll();
                Announce(qt, qt.Entry.Value ? "ON" : "OFF");
            }
            _prevTrigger = trigger;
        }

        private static void Announce(QuickToggle qt, string state)
        {
            Game.Notify(qt.Label + ": " + state);
        }
    }
}