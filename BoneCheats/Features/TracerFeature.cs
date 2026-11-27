using System.Collections.Generic;
using BoneCheats.Core;
using UnityEngine;

namespace BoneCheats.Features
{
    /// <summary>Short-lived lines from the muzzle to the impact point, one per bullet actually fired (see GunHooks).</summary>
    internal sealed class TracerFeature : Feature
    {
        private const float Life = 0.6f;
        private const float MaxRange = 5000f;   // effectively unlimited

        private sealed class Tracer { public LineRenderer Line; public float Born; }

        private static readonly List<Tracer> Active = new List<Tracer>();
        private static readonly Stack<LineRenderer> Free = new Stack<LineRenderer>();

        public override string Name => "Tracers";

        public static void RecordRay(Vector3 origin, Vector3 dir)
        {
            Vector3 end = origin + dir * MaxRange;
            if (Physics.Raycast(origin + dir * 0.05f, dir, out RaycastHit hit, MaxRange, ~0, QueryTriggerInteraction.Ignore))
                end = hit.point;
            RecordSegment(origin, end);
        }

        public static void RecordSegment(Vector3 a, Vector3 b)
        {
            if (!Settings.BulletTracers.Value) return;
            LineRenderer lr = Free.Count > 0 ? Free.Pop() : Draw.NewLine("BC_Tracer", 2);
            lr.positionCount = 2;
            lr.SetPosition(0, a);
            lr.SetPosition(1, b);
            lr.enabled = true;
            Active.Add(new Tracer { Line = lr, Born = Time.unscaledTime });
        }

        protected override void Reset() { Clear(); }

        protected override void LateTick()
        {
            Color col = Colors.Get(Settings.TracerCol.Value);
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                Tracer t = Active[i];
                float age = Time.unscaledTime - t.Born;
                if (t.Line == null) { Active.RemoveAt(i); continue; }
                if (age >= Life)
                {
                    t.Line.enabled = false;
                    Free.Push(t.Line);
                    Active.RemoveAt(i);
                    continue;
                }
                col.a = 1f - age / Life;
                Draw.Style(t.Line, col, 0.012f);
            }
        }

        private static void Clear()
        {
            foreach (Tracer t in Active)
                if (t.Line != null) { t.Line.enabled = false; Free.Push(t.Line); }
            Active.Clear();
        }
    }
}
