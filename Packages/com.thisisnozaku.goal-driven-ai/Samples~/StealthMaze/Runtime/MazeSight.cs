using System;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    public sealed class MazeSight : MazeSense
    {
        [Range(1, 360)] public float fieldOfView = 100;
        [Min(0.01f)] public float recognitionSeconds = 0.35f;
        private float exposure;
        public override bool Observe(MazeGuard observer, MazePlayer player, out MazeObservation observation)
        {
            observation = default;
            if (observer.Faction == player.Faction) return false;
            Vector3 origin = observer.transform.position + Vector3.up * 1.55f;
            Vector3 target = player.transform.position + Vector3.up * (player.Crouching ? 0.65f : 1.5f);
            Vector3 delta = target - origin;
            bool visible = sensitivity > 0 && delta.magnitude <= range &&
                Vector3.Angle(observer.transform.forward, delta) <= fieldOfView * 0.5f &&
                Physics.Raycast(origin, delta.normalized, out var hit, delta.magnitude + .05f, ~0, QueryTriggerInteraction.Ignore) &&
                hit.collider.GetComponentInParent<MazePlayer>() == player;
            exposure = Mathf.Clamp01(exposure + (visible ? sensitivity : -2) * observer.SenseInterval / Mathf.Max(.01f, recognitionSeconds));
            if (!visible || exposure < 1) return false;
            observation = new MazeObservation(player.transform.position, Time.timeAsDouble, EvidenceKind.Vision);
            return true;
        }
        public void Clear() => exposure = 0;
    }


}
