using System;
using GoalDrivenAI.Characters;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    [RequireComponent(typeof(Actor), typeof(AimController))]
    public sealed class WeaponAdapter : MonoBehaviour, IWeaponAdapter
    {
        [SerializeField, Min(1)] private int magazineCapacity = 12;
        [SerializeField, Min(0)] private int initialReserve = 36;
        [SerializeField, Min(0.01f)] private float shotInterval = 0.15f;
        [SerializeField, Min(0.01f)] private float reloadSeconds = 1.5f;
        [SerializeField, Min(0)] private float damage = 15;
        [SerializeField, Min(1)] private float range = 40;
        [SerializeField, Range(0, 10)] private float inaccuracyDegrees = 1;
        [SerializeField] private LayerMask firingMask = ~0;
        private Actor actor;
        private AimController aim;
        private WeaponState state;
        public Func<bool> FirePermission { get; set; } = () => false;
        public int Magazine => state?.Magazine ?? 0;
        public int Reserve => state?.Reserve ?? 0;
        public bool IsReloading => state != null && state.ReloadCompletesAt.HasValue;
        public event Action AmmunitionChanged;
        public event Action<string> ConfirmedNeutralization;

        private void Awake()
        {
            actor = GetComponent<Actor>();
            aim = GetComponent<AimController>();
            state = new WeaponState(magazineCapacity, magazineCapacity, initialReserve, shotInterval, reloadSeconds);
            state.Changed += () => AmmunitionChanged?.Invoke();
        }

        private void Update()
        {
            if (actor.IsAlive) state.Tick(Time.timeAsDouble);
            else state.CancelReload();
        }

        public bool TryFire()
        {
            if (!actor.CanAct || !aim.IsAligned || !FirePermission()) return false;
            Vector2 spread = UnityEngine.Random.insideUnitCircle * inaccuracyDegrees;
            Vector3 direction = Quaternion.Euler(spread.y, spread.x, 0) * aim.Forward;
            bool hit = Physics.Raycast(aim.Origin, direction, out RaycastHit result, range, firingMask,
                QueryTriggerInteraction.Ignore);
            // Check the actual dispersed ray, not just the ideal aim ray.
            Actor victim = hit ? result.collider.GetComponentInParent<Actor>() : null;
            bool safe = victim == null || (victim != actor && victim.Faction != actor.Faction);
            if (!state.TrySpendShot(Time.timeAsDouble, true, safe)) return false;
            if (victim != null)
            {
                bool wasAlive = victim.IsAlive;
                victim.ApplyDamage(damage, result.point);
                if (wasAlive && !victim.IsAlive) ConfirmedNeutralization?.Invoke(victim.Id);
            }
            SoundBus.Publish(aim.Origin, range, Time.timeAsDouble);
            return true;
        }

        public bool TryReload() => actor.CanAct && state.BeginReload(Time.timeAsDouble);
        public void CancelReload() => state?.CancelReload();
        private void OnDisable() { CancelReload(); FirePermission = () => false; }
    }
}
