using System;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    public sealed class MazeHearing : MazeSense
    {
        private MazeObservation? pending;
        public void Receive(MazeGuard observer, Vector3 origin, float radius, MazeObservation report)
        {
            float strength = (float)Audibility.Strength(Vector3.Distance(observer.transform.position, origin), radius, range, sensitivity);
            // A fresh direct sighting takes priority in the guard. Preserve report identity/time across communication.
            if (strength <= 0 || (pending.HasValue && pending.Value.Time > report.Time)) return;
            pending = new MazeObservation(report.Position, report.Time, report.Kind, strength, report.Id);
        }
        public override bool Observe(MazeGuard observer, MazePlayer player, out MazeObservation observation)
        {
            observation = pending.GetValueOrDefault();
            bool has = pending.HasValue; pending = null; return has;
        }
        public void Clear() => pending = null;
    }
}
