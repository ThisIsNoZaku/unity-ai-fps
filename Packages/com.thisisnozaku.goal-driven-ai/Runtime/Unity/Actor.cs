using System;
using System.Collections.Generic;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    [DisallowMultipleComponent]
    public sealed class Actor : MonoBehaviour
    {
        [SerializeField] private string entityId;
        [SerializeField] private string faction = "enemy";
        [SerializeField, Min(1)] private float maximumHealth = 100;
        private float health;
        private double stunnedUntil;
        private static readonly Dictionary<string, Actor> identities = new Dictionary<string, Actor>(StringComparer.Ordinal);

        public string Id
        {
            get
            {
                if (string.IsNullOrWhiteSpace(entityId) ||
                    (identities.TryGetValue(entityId, out var owner) && owner != null && owner != this))
                    entityId = Guid.NewGuid().ToString("N");
                identities[entityId] = this;
                return entityId;
            }
        }
        public string Faction => faction;
        public float Health => health;
        public float HealthFraction => health / maximumHealth;
        public bool IsAlive => health > 0;
        public bool CanAct => IsAlive && Time.timeAsDouble >= stunnedUntil;
        public event Action<float, Vector3> Damaged;
        public event Action Died;
        public event Action Incapacitated;

        private void Awake() { _ = Id; health = maximumHealth; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetIdentities() => identities.Clear();

        private void OnDestroy()
        {
            if (entityId != null && identities.TryGetValue(entityId, out var owner) && owner == this)
                identities.Remove(entityId);
        }

        public void ApplyDamage(float amount, Vector3 hitPosition)
        {
            if (!IsAlive || amount <= 0 || float.IsNaN(amount) || float.IsInfinity(amount)) return;
            health = Mathf.Max(0, health - amount);
            Damaged?.Invoke(amount, hitPosition);
            if (!IsAlive) Died?.Invoke();
        }

        public void Stun(float seconds)
        {
            if (!IsAlive || seconds <= 0 || float.IsNaN(seconds) || float.IsInfinity(seconds)) return;
            stunnedUntil = Math.Max(stunnedUntil, Time.timeAsDouble + seconds);
            Incapacitated?.Invoke();
        }

        public void ResetForPool() { health = maximumHealth; stunnedUntil = 0; }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(entityId)) entityId = Guid.NewGuid().ToString("N");
        }
    }

    public sealed class UnityClock : IClock
    {
        public double Now => Time.timeAsDouble;
    }

    internal static class Coordinates
    {
        public static Position ToPosition(this Vector3 value) => new Position(value.x, value.y, value.z);
        public static Vector3 ToVector(this Position value) => new Vector3(value.X, value.Y, value.Z);
    }
}
