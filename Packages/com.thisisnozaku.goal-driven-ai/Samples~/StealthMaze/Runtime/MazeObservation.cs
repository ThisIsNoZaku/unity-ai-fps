using System;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    public readonly struct MazeObservation
    {
        public readonly Vector3 Position;
        public readonly double Time;
        public readonly string Id;
        public readonly EvidenceKind Kind;
        public readonly float Confidence;
        public MazeObservation(Vector3 position, double time, EvidenceKind kind, float confidence = 1, string id = null)
        { Position = position; Time = time; Kind = kind; Confidence = confidence; Id = id ?? Guid.NewGuid().ToString("N"); }
    }


}
