using UnityEngine;

namespace GoalDrivenAI.Unity
{
    [CreateAssetMenu(menuName = "Goal Driven AI/Profile")]
    public sealed class AIProfile : ScriptableObject
    {
        [SerializeField] private string profileId = "guard";
        [Min(0.1f)] public float senseInterval = 0.1f;
        [Min(0.1f)] public float decisionInterval = 0.2f;
        [Min(1)] public float engagementRange = 20;
        [Min(0)] public float searchDwellSeconds = 1;
        [Min(1)] public float searchTimeoutSeconds = 12;
        [Range(0, 1)] public float readinessWeight = 0.6f;
        [Range(0, 1)] public float aggression = 0.7f;
        [Range(0, 1)] public float caution = 0.5f;
        [Min(1)] public int plannerExpansionsPerStep = 8;
        public string Id => profileId;
    }
}
