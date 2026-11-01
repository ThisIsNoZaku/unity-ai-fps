using System;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    // Add another component derived from this type to extend the demo with a new sense.
    public abstract class MazeSense : MonoBehaviour
    {
        [Min(0)] public float range = 12;
        [Range(0, 3)] public float sensitivity = 1;
        public abstract bool Observe(MazeGuard observer, MazePlayer player, out MazeObservation observation);
    }


}
