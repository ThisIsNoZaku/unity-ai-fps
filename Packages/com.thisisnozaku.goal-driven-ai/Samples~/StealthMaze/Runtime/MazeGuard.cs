using System;
using System.Collections.Generic;
using GoalDrivenAI;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Tactics;
using GoalDrivenAI.Unity;
using UnityEngine;

namespace GoalDrivenAI.Samples.StealthMaze
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class MazeGuard : MonoBehaviour
    {
        [Min(.05f)] public float SenseInterval = .1f;
        [Min(.1f)] public float patrolSpeed = 2;
        [Min(.1f)] public float chaseSpeed = 3.2f;
        [Min(.1f)] public float memorySeconds = 10;
        [Min(0)] public float shoutRadius = 16;
        [Min(.1f)] public float shoutCooldown = 2;
        [Min(0)] public float searchDwell = 2;
        public string Goal { get; private set; } = "patrol";
        public bool Shouting => Time.time < shoutUntil;
        private MazeDemo world;
        private MazeSense[] senses;
        private CharacterController motor;
        private BeliefStore beliefs;
        private GoalSelector goals;
        private Renderer body;
        private Vector3 home;
        private Vector3[] patrol;
        private int patrolIndex, routeIndex;
        private IReadOnlyList<(int x, int y)> route;
        private (int x, int y) routeTarget = (-1, -1);
        private float nextSense, nextShout, shoutUntil, dwellUntil = -1;
        private Evidence target;
        private bool seeing;

        public void Initialize(MazeDemo demo, Vector3[] points)
        {
            world = demo; patrol = points; home = transform.position;
            motor = GetComponent<CharacterController>(); body = GetComponentInChildren<Renderer>();
            senses = GetComponents<MazeSense>(); ResetState();
        }
        public void ResetState()
        {
            motor.enabled = false; transform.SetPositionAndRotation(home, Quaternion.identity); motor.enabled = true;
            var clock = new UnityClock(); beliefs = new BeliefStore(clock, 32); goals = new GoalSelector(clock);
            target = null; seeing = false; patrolIndex = 0; route = null; routeTarget = (-1, -1);
            nextSense = 0; nextShout = 0; shoutUntil = 0; dwellUntil = -1; Goal = "patrol";
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
            if (world == null || world.Won) return;
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
                    new GoalCandidate("patrol", .2),
                    new GoalCandidate("investigate", .7, _ => target != null),
                    new GoalCandidate("chase", 1, _ => seeing && target != null, emergency: true)
                }, new GoalContext(0)).GoalId ?? "patrol";
                body.sharedMaterial.color = seeing ? new Color(1, .2f, .15f) : target != null ? new Color(1, .65f, .12f) : new Color(.25f, .65f, .9f);
            }
            Vector3 destination = Goal != "patrol" && target?.Position != null
                ? new Vector3(target.Position.Value.X, 0, target.Position.Value.Z) : patrol[patrolIndex];
            bool arrived = MoveToward(destination, Goal == "chase" ? chaseSpeed : patrolSpeed);
            if (arrived && !seeing)
            {
                if (Goal == "patrol") { patrolIndex = (patrolIndex + 1) % patrol.Length; route = null; }
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
            var cell = world.Cell(destination);
            if (route == null || cell != routeTarget)
            {
                routeTarget = cell;
                var start = world.Cell(transform.position);
                var steps = new List<(int x, int y)> { start };
                steps.AddRange(world.Map.Route(start, cell));
                route = steps; routeIndex = 0;
            }
            if (routeIndex >= route.Count)
            {
                // Finish at the observed point within the cell, rather than stopping at its center.
                // A crawl-only destination remains inaccessible even while it is visible.
                if (!world.Map.Walkable(cell.x, cell.y)) return true;
                Vector3 finalDelta = destination - transform.position; finalDelta.y = 0;
                if (finalDelta.magnitude < .1f) return true;
                Step(finalDelta, speed); return false;
            }
            Vector3 point = world.Point(route[routeIndex]);
            Vector3 delta = point - transform.position; delta.y = 0;
            if (delta.magnitude < .08f) { routeIndex++; return false; }
            Step(delta, speed); return false;
        }
        private void Step(Vector3 delta, float speed)
        {
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(delta), 280 * Time.deltaTime);
            motor.Move(Vector3.ClampMagnitude(delta, speed * Time.deltaTime) + Vector3.down * 2 * Time.deltaTime);
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
