using System;

namespace GoalDrivenAI.Characters
{
    public enum RouteState { Idle, Pending, Moving, Arrived, Partial, Unavailable, NoProgress }

    public interface INavigationMotor
    {
        Position Position { get; }
        RouteState State { get; }
        bool RequestDestination(Position destination, float stoppingDistance);
        void Stop();
    }

    public interface IAimController
    {
        bool IsAligned { get; }
        void AimAt(Position observedPosition);
        void Clear();
    }

    public interface IWeaponAdapter
    {
        int Magazine { get; }
        int Reserve { get; }
        bool IsReloading { get; }
        bool TryFire();
        bool TryReload();
        void CancelReload();
        event Action? AmmunitionChanged;
    }

    public interface IInteractionAdapter
    {
        string Id { get; }
        bool TryRequest(string actorId, string requestedState);
        bool Confirms(string requestedState);
    }

    public readonly struct SensedContact
    {
        public readonly string EvidenceId;
        public readonly string? SubjectId;
        public readonly string SourceId;
        public readonly Position Position;
        public readonly double ObservedAt;
        public readonly double ExpiresAt;
        public readonly double Confidence;
        public readonly double Uncertainty;
        public readonly bool Visual;

        public SensedContact(string evidenceId, string? subjectId, string sourceId, Position position,
            double observedAt, double expiresAt, double confidence, double uncertainty, bool visual)
        {
            EvidenceId = evidenceId;
            SubjectId = subjectId;
            SourceId = sourceId;
            Position = position;
            ObservedAt = observedAt;
            ExpiresAt = expiresAt;
            Confidence = confidence;
            Uncertainty = uncertainty;
            Visual = visual;
        }
    }

    public sealed class WeaponState
    {
        public int Magazine { get; private set; }
        public int Reserve { get; private set; }
        public int Capacity { get; }
        public double NextShotAt { get; private set; }
        public double? ReloadCompletesAt { get; private set; }
        public double ShotInterval { get; }
        public double ReloadDuration { get; }
        public event Action? Changed;

        public WeaponState(int capacity, int magazine, int reserve, double shotInterval, double reloadDuration)
        {
            if (capacity < 1 || magazine < 0 || magazine > capacity || reserve < 0
                || !PositiveFinite(shotInterval) || !PositiveFinite(reloadDuration))
                throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            Magazine = magazine;
            Reserve = reserve;
            ShotInterval = shotInterval;
            ReloadDuration = reloadDuration;
        }

        public bool TrySpendShot(double now, bool permitted, bool corridorSafe)
        {
            if (!Finite(now) || !permitted || !corridorSafe || ReloadCompletesAt.HasValue
                || Magazine == 0 || now < NextShotAt) return false;
            Magazine--;
            NextShotAt = now + ShotInterval;
            Changed?.Invoke();
            return true;
        }

        public bool BeginReload(double now)
        {
            if (!Finite(now) || ReloadCompletesAt.HasValue || Reserve == 0 || Magazine == Capacity) return false;
            ReloadCompletesAt = now + ReloadDuration;
            return true;
        }

        public bool Tick(double now)
        {
            if (!Finite(now) || !ReloadCompletesAt.HasValue || now < ReloadCompletesAt.Value) return false;
            int transfer = Math.Min(Capacity - Magazine, Reserve);
            Magazine += transfer;
            Reserve -= transfer;
            ReloadCompletesAt = null;
            Changed?.Invoke();
            return true;
        }

        public void CancelReload() => ReloadCompletesAt = null;
        private static bool PositiveFinite(double value) => value > 0 && Finite(value);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
