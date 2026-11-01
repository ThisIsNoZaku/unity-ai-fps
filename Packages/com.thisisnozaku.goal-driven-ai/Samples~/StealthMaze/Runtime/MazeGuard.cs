using System;
using GoalDrivenAI;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Language;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Tactics;
using GoalDrivenAI.Unity;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class MazeGuard : MonoBehaviour, IAgentPerspectiveSource
    {
        [Min(.05f)] public float SenseInterval = .1f;
        [Min(.1f)] public float patrolSpeed = 2;
        [Min(.1f)] public float chaseSpeed = 3.2f;
        [Min(0)] public float patrolDwell = 1.5f;
        [Min(.1f)] public float memorySeconds = 10;
        [Min(0)] public float shoutRadius = 16;
        [Min(.1f)] public float shoutCooldown = 2;
        [Min(0)] public float searchDwell = 2;
        public string Goal { get; private set; } = "defend";
        public string Faction => MazeScenario.GuardsFaction;
        public int GuardIndex { get; private set; }
        public string Identity => world.Scenario.GuardIds[GuardIndex];
        public int PatrolCheckpointIndex => world?.Scenario.PatrolCheckpoint(GuardIndex) ?? 0;
        public int PatrolVisits => world?.Scenario.PatrolVisits(GuardIndex) ?? 0;
        public void SetIdentity(int index)
        { GuardIndex = index; Goal = "defend"; }
        private Transform perspectiveEye;
        public Transform Eye => perspectiveEye != null ? perspectiveEye : transform;
        private MazeSight Sight;
        public bool VisionAvailable => isActiveAndEnabled && Sight != null && Sight.isActiveAndEnabled && Sight.sensitivity > 0;
        public float VisionRange => Sight != null ? Sight.range : 0;
        public float VisionFieldOfView => Sight != null ? Sight.fieldOfView : 1;
        public int VisionMask => ~0;
        public event Action<PerceivedSound> Heard;
        public void PublishHeard(Vector3 origin, float radius, MazeObservation report, float gain)
        {
            if (report.Clip != null) Heard?.Invoke(new PerceivedSound(
                new SoundBus.Sound(report.Id, origin, radius, Time.timeAsDouble, report.Clip, report.Volume), Mathf.Clamp01(gain)));
        }
        public bool Shouting => Time.time < shoutUntil;
        private MazeDemo world;
        private MazeSense[] senses;
        private CharacterController motor;
        private BeliefStore beliefs;
        private GoalSelector goals;
        private Renderer body;
        private Vector3 home;
        private NavMeshPathFollower navigation;
        private float nextSense, nextShout, shoutUntil, dwellUntil = -1;
        private Evidence target;
        private bool seeing;

        public void Initialize(MazeDemo demo)
        {
            world = demo; home = transform.position;
            perspectiveEye = new GameObject("Guard sense eye").transform;
            perspectiveEye.SetParent(transform, false); perspectiveEye.localPosition = Vector3.up * 1.55f;
            navigation = new NavMeshPathFollower(world.GuardNavigation.Filter, new NavMeshPathOptions(horizontal: true));
            motor = GetComponent<CharacterController>(); body = GetComponentInChildren<Renderer>();
            senses = GetComponents<MazeSense>(); Sight = GetComponent<MazeSight>(); ResetState();
        }
        public void ResetState()
        {
            motor.enabled = false; transform.SetPositionAndRotation(home, Quaternion.identity); motor.enabled = true;
            var clock = new UnityClock(); beliefs = new BeliefStore(clock, 32); goals = new GoalSelector(clock);
            target = null; seeing = false;
            navigation.Clear();
            nextSense = 0; nextShout = 0; shoutUntil = 0; dwellUntil = -1; Goal = "defend";
            foreach (var sense in senses)
            { if (sense is MazeSight sight) sight.Clear(); if (sense is MazeHearing hearing) hearing.Clear(); }
        }
        public void Hear(Vector3 origin, float radius, MazeObservation observation)
        {
            foreach (var sense in senses)
                if (sense.isActiveAndEnabled && sense is MazeHearing hearing) hearing.Receive(this, origin, radius, observation);
        }
        private void Update()
        {
            if (world == null) return;
            if (!world.Running) return;
            if (Time.time >= nextSense)
            {
                nextSense = Time.time + SenseInterval; seeing = false;
                MazeObservation? best = null;
                foreach (var sense in senses)
                    if (sense.isActiveAndEnabled && sense.Observe(this, world.Player, out var observation))
                    {
                        if (observation.Kind == EvidenceKind.Vision) seeing = true;
                        if (!best.HasValue || observation.Kind == EvidenceKind.Vision ||
                            (best.Value.Kind != EvidenceKind.Vision && observation.Time > best.Value.Time)) best = observation;
                    }
                if (best.HasValue)
                {
                    var o = best.Value;
                    var evidence = new Evidence(o.Id, "interest", null, o.Kind, Truth.True,
                        new Position(o.Position.x, o.Position.y, o.Position.z), o.Time, memorySeconds, o.Confidence,
                        receiptAt: Time.timeAsDouble, initialUncertaintyRadius: o.Kind == EvidenceKind.Hearing ? 1 : 0);
                    if (beliefs.Add(evidence)) { target = evidence; dwellUntil = -1; }
                    if (seeing && Time.time >= nextShout)
                    {
                        nextShout = Time.time + shoutCooldown; shoutUntil = Time.time + .6f;
                        world.Shout(this, new MazeObservation(o.Position, o.Time, EvidenceKind.Communication, 1, o.Id));
                    }
                }
                var interest = beliefs.Query("interest");
                target = interest.CanSearch ? interest.Report : null;
                Goal = goals.Select(new[] {
                    new GoalCandidate("idle", .1),
                    new GoalCandidate("patrol", .2, _ => world.Scenario.IsPatrolling(GuardIndex)),
                    new GoalCandidate("defend", .5, _ => world.Scenario.DefenseFor(GuardIndex) >= 0),
                    new GoalCandidate("investigate", .7, _ => target != null),
                    new GoalCandidate("chase", 1, _ => seeing && target != null, emergency: true)
                }, new GoalContext(0)).GoalId ?? "patrol";
                if (Goal == "chase" || Goal == "investigate")
                    world.Scenario.Commands.SuspendForEmergency(Identity, "Responding to sensory evidence");
                else world.Scenario.Commands.ResumeAfterEmergency(Identity);
                body.sharedMaterial.color = seeing ? new Color(1, .2f, .15f) : target != null ? new Color(1, .65f, .12f) : new Color(.25f, .65f, .9f);
            }
            var order = world.Scenario.Commands.GetPrimary(Identity);
            float speed = Goal == "chase" ? chaseSpeed : patrolSpeed;
            bool arrived;
            if ((Goal == "chase" || Goal == "investigate") && target?.Position != null)
                arrived = MoveToward(new Vector3(target.Position.Value.X, 0, target.Position.Value.Z), speed);
            else if (order?.Request.TargetId != null && world.Scenario.Locations.TryGetValue(order.Request.TargetId, out var location))
                arrived = MoveToward(location, speed);
            else arrived = Step(true, Vector3.zero, speed);
            if (arrived && !seeing)
            {
                if (Goal == "patrol")
                {
                    transform.Rotate(0, 60 * Time.deltaTime, 0);
                    if (order?.Request.Type == OrderType.Move && world.Scenario.CompleteNavigation(GuardIndex, transform.position.ToPosition()))
                        navigation.Clear();
                }
                else if (Goal == "defend") transform.Rotate(0, 60 * Time.deltaTime, 0);
                else if (target != null)
                {
                    if (dwellUntil < 0) dwellUntil = Time.time + searchDwell;
                    transform.Rotate(0, 90 * Time.deltaTime, 0);
                    if (Time.time >= dwellUntil) { beliefs.CompleteSearch(target.ReportId); dwellUntil = -1; }
                }
            }
        }
        private bool MoveToward(Vector3 destination, float speed)
        {
            var progress = navigation.Next(transform.position, destination, out var delta);
            // Investigations may finish at the accessible edge of an observed location.
            return Step(progress == NavigationProgress.Arrived || progress == NavigationProgress.Partial, delta, speed);
        }
        private bool MoveToward(CatalogHandle location, float speed)
        {
            var progress = navigation.Next(transform.position, location, out var delta);
            return Step(progress == NavigationProgress.Arrived, delta, speed);
        }
        private bool Step(bool arrived, Vector3 delta, float speed)
        {
            if (delta.sqrMagnitude > .000001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(delta), 280 * Time.deltaTime);
            motor.Move(Vector3.ClampMagnitude(delta, speed * Time.deltaTime) + Vector3.down * 2 * Time.deltaTime);
            return arrived;
        }
        private void OnControllerColliderHit(ControllerColliderHit hit)
        { if (hit.collider.GetComponent<MazePlayer>() != null) world.Capture(); }
        private void OnDrawGizmosSelected()
        {
            var sight = GetComponent<MazeSight>(); var hearing = GetComponent<MazeHearing>();
            if (sight != null) { Gizmos.color = Color.yellow; Gizmos.DrawWireSphere(transform.position, sight.range); }
            if (hearing != null) { Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(transform.position, hearing.range); }
            if (target?.Position != null) { var p = target.Position.Value; Gizmos.color = Color.red; Gizmos.DrawLine(transform.position, new Vector3(p.X, p.Y, p.Z)); }
        }
    }
}
