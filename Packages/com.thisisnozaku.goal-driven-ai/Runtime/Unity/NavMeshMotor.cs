using GoalDrivenAI.Characters;
using UnityEngine;
using UnityEngine.AI;

namespace GoalDrivenAI.Unity
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class NavMeshMotor : MonoBehaviour, INavigationMotor
    {
        [SerializeField, Min(0.1f)] private float progressTimeout = 3;
        [SerializeField, Min(0.01f)] private float progressThreshold = 0.1f;
        private NavMeshAgent agent;
        private Vector3 destination;
        private Vector3 progressPosition;
        private double lastProgress;
        private double requestedAt;
        private bool requested;
        private int settledFrames;

        public Position Position => transform.position.ToPosition();
        public RouteState State { get; private set; }

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            agent.updatePosition = true;
            agent.updateRotation = false;
        }

        public bool RequestDestination(Position point, float stoppingDistance)
        {
            if (!isActiveAndEnabled || !agent.isOnNavMesh || stoppingDistance < 0
                || float.IsNaN(stoppingDistance) || float.IsInfinity(stoppingDistance)) return false;
            Vector3 next = point.ToVector();
            if (requested && Vector3.Distance(destination, next) < progressThreshold) return true;
            destination = next;
            agent.stoppingDistance = stoppingDistance;
            agent.isStopped = false;
            if (!agent.SetDestination(destination))
            {
                State = RouteState.Unavailable;
                return false;
            }
            requested = true;
            requestedAt = lastProgress = Time.timeAsDouble;
            progressPosition = transform.position;
            settledFrames = 0;
            State = RouteState.Pending;
            return true;
        }

        private void Update()
        {
            if (!requested) return;
            if (!agent.isOnNavMesh) { Fail(RouteState.Unavailable); return; }
            if (agent.pathPending)
            {
                if (Time.timeAsDouble - requestedAt > progressTimeout) Fail(RouteState.NoProgress);
                return;
            }
            if (agent.pathStatus != NavMeshPathStatus.PathComplete)
            {
                Fail(agent.pathStatus == NavMeshPathStatus.PathPartial ? RouteState.Partial : RouteState.Unavailable);
                return;
            }
            if (Vector3.Distance(transform.position, destination) <= agent.stoppingDistance + 0.05f
                && agent.remainingDistance <= agent.stoppingDistance + 0.05f
                && agent.velocity.sqrMagnitude <= 0.01f)
            {
                if (++settledFrames >= 2) { State = RouteState.Arrived; requested = false; agent.isStopped = true; }
                return;
            }
            settledFrames = 0;
            State = RouteState.Moving;
            if (Vector3.Distance(progressPosition, transform.position) >= progressThreshold)
            {
                progressPosition = transform.position;
                lastProgress = Time.timeAsDouble;
            }
            if (Time.timeAsDouble - lastProgress > progressTimeout) Fail(RouteState.NoProgress);
        }

        private void Fail(RouteState reason)
        {
            Stop();
            State = reason;
        }

        public void Stop()
        {
            bool arrived = State == RouteState.Arrived;
            requested = false;
            settledFrames = 0;
            if (agent != null && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.ResetPath();
            }
            State = arrived ? RouteState.Arrived : RouteState.Idle;
        }

        private void OnDisable() => Stop();
    }
}
