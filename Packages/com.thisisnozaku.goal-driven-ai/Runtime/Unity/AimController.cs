using GoalDrivenAI.Characters;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public sealed class AimController : MonoBehaviour, IAimController
    {
        [SerializeField] private Transform pivot;
        [SerializeField, Min(1)] private float degreesPerSecond = 180;
        [SerializeField, Min(0)] private float reactionSeconds = 0.2f;
        [SerializeField, Range(0.1f, 20)] private float toleranceDegrees = 3;
        private Vector3 target;
        private bool hasTarget;
        private double reactAt;
        public bool IsAligned { get; private set; }
        public Vector3 Forward => pivot.forward;
        public Vector3 Origin => pivot.position;

        private void Awake() { if (pivot == null) pivot = transform; }

        public void AimAt(Position observedPosition)
        {
            if (!hasTarget) reactAt = Time.timeAsDouble + reactionSeconds;
            target = observedPosition.ToVector();
            hasTarget = true;
        }

        private void Update()
        {
            IsAligned = false;
            if (!hasTarget || Time.timeAsDouble < reactAt) return;
            Vector3 direction = target - pivot.position;
            if (direction.sqrMagnitude < 0.001f) return;
            Quaternion rotation = Quaternion.LookRotation(direction);
            pivot.rotation = Quaternion.RotateTowards(pivot.rotation, rotation, degreesPerSecond * Time.deltaTime);
            IsAligned = Quaternion.Angle(pivot.rotation, rotation) <= toleranceDegrees;
        }

        public void Clear() { hasTarget = false; IsAligned = false; }
        private void OnDisable() => Clear();
    }
}
