using System;
using System.Collections.Generic;
using GoalDrivenAI.Characters;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public static class SoundBus
    {
        public readonly struct Sound
        {
            public readonly string Id;
            public readonly Vector3 Position;
            public readonly float Radius;
            public readonly double ObservedAt;
            public readonly AudioClip Clip;
            public readonly float Volume;
            public readonly float Pitch;
            public Sound(Vector3 position, float radius, double observedAt) : this(position, radius, observedAt, null) { }
            public Sound(Vector3 position, float radius, double observedAt, AudioClip clip, float volume = 1, float pitch = 1)
                : this(Guid.NewGuid().ToString("N"), position, radius, observedAt, clip, volume, pitch) { }
            public Sound(string id, Vector3 position, float radius, double observedAt, AudioClip clip, float volume = 1, float pitch = 1)
            {
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256) throw new ArgumentException("Invalid sound ID.", nameof(id));
                position.ToPosition();
                if (float.IsNaN(radius) || float.IsInfinity(radius) || radius <= 0 || double.IsNaN(observedAt) || double.IsInfinity(observedAt) ||
                    float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0 || volume > 1 ||
                    float.IsNaN(pitch) || float.IsInfinity(pitch) || pitch <= 0 || pitch > 3)
                    throw new ArgumentOutOfRangeException(nameof(radius));
                Clip = clip; Volume = volume; Pitch = pitch;
                Id = id;
                Position = position;
                Radius = radius;
                ObservedAt = observedAt;
            }
        }

        public static event Action<Sound> Emitted;
        public static void Publish(Vector3 position, float radius, double observedAt)
        {
            Publish(position, radius, observedAt, null);
        }
        public static void Publish(Sound sound)
        {
            if (sound.Radius > 0 && !string.IsNullOrEmpty(sound.Id)) Emitted?.Invoke(sound);
        }
        public static void Publish(Vector3 position, float radius, double observedAt, AudioClip clip, float volume = 1, float pitch = 1)
        {
            if (radius > 0 && !float.IsInfinity(radius) && !float.IsNaN(radius))
                Publish(new Sound(position, radius, observedAt, clip, volume, pitch));
        }
    }

    [RequireComponent(typeof(Actor))]
    public sealed class PerceptionSensor : MonoBehaviour, IAgentPerspectiveSource
    {
        [SerializeField] private Transform eye;
        [SerializeField, Min(1)] private float range = 30;
        [SerializeField, Range(1, 360)] private float fieldOfView = 110;
        [SerializeField, Min(1)] private int rayBudget = 8;
        [SerializeField, Min(0.1f)] private float memorySeconds = 12;
        [SerializeField] private LayerMask candidateMask = ~0;
        [SerializeField] private LayerMask occlusionMask = ~0;
        private readonly Collider[] candidates = new Collider[64];
        private readonly HashSet<string> sampled = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> visible = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> seenThisTick = new HashSet<string>(StringComparer.Ordinal);
        private Actor actor;
        private int offset;
        public Transform Eye => eye != null ? eye : transform;
        public bool VisionAvailable => isActiveAndEnabled && actor != null && actor.IsAlive;
        public float VisionRange => range;
        public float VisionFieldOfView => fieldOfView;
        public int VisionMask => occlusionMask.value;
        public event Action<PerceivedSound> Heard;
        public event Action<SensedContact> Observed;
        public event Action<string> Lost;
        public event Action<string> ConfirmedNeutralization;
        public event Action<string, Position> AllyObserved;

        private void Awake()
        {
            actor = GetComponent<Actor>();
            if (eye == null) eye = transform;
        }

        private void OnEnable() => SoundBus.Emitted += Hear;
        private void OnDisable()
        {
            SoundBus.Emitted -= Hear;
            foreach (string id in visible) Lost?.Invoke(id);
            visible.Clear();
        }

        public void Sense(double now)
        {
            if (!isActiveAndEnabled || !actor.IsAlive) return;
            int count = Physics.OverlapSphereNonAlloc(eye.position, range, candidates, candidateMask,
                QueryTriggerInteraction.Ignore);
            int rays = 0;
            sampled.Clear();
            seenThisTick.Clear();
            for (int i = 0; i < count && rays < rayBudget; i++)
            {
                Collider candidate = candidates[(i + offset) % count];
                Actor target = candidate.GetComponentInParent<Actor>();
                if (target == null || target == actor
                    || !sampled.Add(target.Id)) continue;
                Vector3 center = candidate.bounds.center;
                Vector3 direction = center - eye.position;
                if (direction.sqrMagnitude > range * range
                    || Vector3.Angle(eye.forward, direction) > fieldOfView * 0.5f) continue;
                bool canSee = VisibleSample(center, target, ref rays);
                if (!canSee && rays < rayBudget)
                    canSee = VisibleSample(center + Vector3.up * candidate.bounds.extents.y * 0.7f, target, ref rays);
                if (!canSee) continue;
                if (target.Faction == actor.Faction)
                {
                    if (target.IsAlive) AllyObserved?.Invoke(target.Id, center.ToPosition());
                    continue;
                }
                if (!target.IsAlive) { ConfirmedNeutralization?.Invoke(target.Id); continue; }
                seenThisTick.Add(target.Id);
                Observed?.Invoke(new SensedContact(Guid.NewGuid().ToString("N"), target.Id, actor.Id,
                    center.ToPosition(), now, now + memorySeconds, 1, 0, true));
            }
            if (count > 0) offset = (offset + Math.Max(1, rayBudget / 2)) % count;
            // Unrefreshed contacts are not visible; their last observed positions remain in memory.
            foreach (string id in visible)
                if (!seenThisTick.Contains(id)) Lost?.Invoke(id);
            visible.Clear();
            foreach (string id in seenThisTick) visible.Add(id);
        }

        private bool VisibleSample(Vector3 sample, Actor target, ref int rays)
        {
            rays++;
            Vector3 delta = sample - eye.position;
            return Physics.Raycast(eye.position, delta.normalized, out RaycastHit hit, delta.magnitude + 0.05f,
                occlusionMask, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<Actor>() == target;
        }

        private void Hear(SoundBus.Sound sound)
        {
            if (actor == null || !actor.IsAlive) return;
            double distance = Vector3.Distance(transform.position, sound.Position);
            if (distance > sound.Radius || Time.timeAsDouble - sound.ObservedAt > memorySeconds) return;
            double confidence = Math.Max(0.1, 1 - distance / sound.Radius);
            double uncertainty = Math.Max(1, distance * 0.2);
            Heard?.Invoke(new PerceivedSound(sound, (float)confidence));
            // Sound never supplies a source actor identity.
            Observed?.Invoke(new SensedContact(sound.Id, null, "sound", sound.Position.ToPosition(),
                sound.ObservedAt, sound.ObservedAt + memorySeconds, confidence, uncertainty, false));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, range);
        }
    }
}
