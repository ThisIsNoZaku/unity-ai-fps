using System;
using GoalDrivenAI.Language;
using GoalDrivenAI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace GoalDrivenAI.Unity
{
    public enum NavigationProgress { Unavailable, Moving, Arrived, Partial }

    public sealed class NavMeshPathOptions
    {
        public float RepathSeconds { get; }
        public float TargetChangeDistance { get; }
        public float SourceSampleRadius { get; }
        public float TargetSampleRadius { get; }
        public float CornerTolerance { get; }
        public float ArrivalTolerance { get; }
        public bool Horizontal { get; }
        public NavMeshPathOptions(float repathSeconds = 1, float targetChangeDistance = .25f,
            float sourceSampleRadius = .6f, float targetSampleRadius = 3,
            float cornerTolerance = .06f, float arrivalTolerance = .1f, bool horizontal = false)
        {
            foreach (float value in new[] { repathSeconds, targetChangeDistance, sourceSampleRadius,
                targetSampleRadius, cornerTolerance, arrivalTolerance })
                if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(repathSeconds));
            if (cornerTolerance > arrivalTolerance) throw new ArgumentException("Corner tolerance exceeds arrival tolerance.");
            RepathSeconds = repathSeconds; TargetChangeDistance = targetChangeDistance;
            SourceSampleRadius = sourceSampleRadius; TargetSampleRadius = targetSampleRadius;
            CornerTolerance = cornerTolerance; ArrivalTolerance = arrivalTolerance; Horizontal = horizontal;
        }
    }

    /// <summary>Produces steering deltas; the game's existing motor retains movement/collision ownership.</summary>
    public sealed class NavMeshPathFollower
    {
        private readonly NavMeshQueryFilter filter;
        private readonly NavMeshPathOptions options;
        private readonly IClock clock;
        private NavMeshPath path;
        private Vector3[] corners;
        private Vector3 destination;
        private int corner;
        private double repathAt;
        private bool valid;
        public NavigationProgress State { get; private set; }

        public NavMeshPathFollower(NavMeshQueryFilter filter, NavMeshPathOptions options = null, IClock clock = null)
        { this.filter = filter; this.options = options ?? new NavMeshPathOptions(); this.clock = clock ?? new UnityClock(); }
        public void Clear() { valid = false; corners = null; State = NavigationProgress.Unavailable; }
        public NavigationProgress Next(Vector3 position, LocationRegistry locations, string locationId, out Vector3 delta)
        {
            if (locations == null) throw new ArgumentNullException(nameof(locations));
            return Next(position, locations[locationId], out delta);
        }
        public NavigationProgress Next(Vector3 position, CatalogHandle location, out Vector3 delta)
        {
            if (location == null || location.Kind != HandleKind.Location)
                throw new ArgumentException("Path destination must be a named location.", nameof(location));
            return Next(position, location.Position.ToVector(), out delta);
        }
        public NavigationProgress Next(Vector3 position, Vector3 target, out Vector3 delta)
        {
            // Validate copied coordinates before passing them to Unity's native queries.
            position.ToPosition(); target.ToPosition();
            if (path == null) path = new NavMeshPath();
            delta = Vector3.zero;
            double now = clock.Now;
            if (double.IsNaN(now) || double.IsInfinity(now)) throw new InvalidOperationException("Navigation clock must be finite.");
            if (!valid || (target - destination).sqrMagnitude > options.TargetChangeDistance * options.TargetChangeDistance || now >= repathAt)
            {
                destination = target; repathAt = now + options.RepathSeconds;
                valid = true; corner = 0; corners = null;
                if (NavMesh.SamplePosition(position, out var start, options.SourceSampleRadius, filter) &&
                    NavMesh.SamplePosition(target, out var end, options.TargetSampleRadius, filter) &&
                    NavMesh.CalculatePath(start.position, end.position, filter, path) &&
                    path.status != NavMeshPathStatus.PathInvalid)
                    corners = path.corners;
            }
            if (corners == null || corners.Length == 0) return State = NavigationProgress.Unavailable;
            while (corner < corners.Length)
            {
                delta = Difference(corners[corner], position);
                if (delta.magnitude > options.CornerTolerance) return State = NavigationProgress.Moving;
                corner++;
            }
            delta = Vector3.zero;
            // A small moving-target change may not cross the regular repath threshold.
            // Once the cached path ends, refresh it instead of calling a reachable target partial.
            if (path.status == NavMeshPathStatus.PathComplete && Difference(target, destination).sqrMagnitude > .000001f &&
                Difference(target, position).magnitude > options.ArrivalTolerance)
            { valid = false; return State = NavigationProgress.Moving; }
            return State = path.status == NavMeshPathStatus.PathComplete &&
                Difference(target, position).magnitude <= options.ArrivalTolerance
                ? NavigationProgress.Arrived : NavigationProgress.Partial;
        }
        private Vector3 Difference(Vector3 target, Vector3 position)
        { var delta = target - position; if (options.Horizontal) delta.y = 0; return delta; }
    }
}
