using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI.Characters;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Tactics;
using UnityEngine;
using UnityEngine.AI;

namespace GoalDrivenAI.Unity
{
    [RequireComponent(typeof(Actor), typeof(NavMeshMotor), typeof(PerceptionSensor))]
    [RequireComponent(typeof(AimController), typeof(WeaponAdapter))]
    public sealed class AIBrain : MonoBehaviour, IScheduledAgent
    {
        [SerializeField] private AIProfile profile;
        [SerializeField] private AIScheduler scheduler;
        [SerializeField] private CommandHost commands;
        [SerializeField] private CoverSlot[] coverSlots = Array.Empty<CoverSlot>();
        private static readonly LeaseStore CoverLeases = new LeaseStore(new UnityClock());
        private readonly UnityClock clock = new UnityClock();
        private readonly Dictionary<string, Position> visible = new Dictionary<string, Position>(StringComparer.Ordinal);
        private readonly HashSet<string> neutralized = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Position> allies = new Dictionary<string, Position>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> allySeenAt = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Queue<PlanningAction> plan = new Queue<PlanningAction>();
        private readonly Dictionary<string, Func<RuntimeAction>> factories = new Dictionary<string, Func<RuntimeAction>>(StringComparer.Ordinal);
        private Actor actor;
        private NavMeshMotor motor;
        private AimController aim;
        private WeaponAdapter weapon;
        private PerceptionSensor sensor;
        private BeliefStore beliefs;
        private GoalSelector selector;
        private IncrementalPlanner planner;
        private ActionRunner runner;
        private RuntimeAction running;
        private GoalCandidate selected;
        private long generation, revision;
        private string activeOrder;
        private string searchedOrder;
        private double retryAt;
        private double failureWindow;
        private int failures;
        private bool safe, emergency, initialized;
        private Position home;
        private CoverSlot pendingCover, occupiedCover;
        public string ActorId => actor == null ? GetComponent<Actor>().Id : actor.Id;
        public string GoalId => selected?.Id;
        public string LastFailure { get; private set; }
        public BeliefStore Beliefs => beliefs;

        private void Awake()
        {
            actor = GetComponent<Actor>();
            motor = GetComponent<NavMeshMotor>();
            aim = GetComponent<AimController>();
            weapon = GetComponent<WeaponAdapter>();
            sensor = GetComponent<PerceptionSensor>();
            if (profile == null || scheduler == null || commands == null)
            {
                Debug.LogError("AIBrain requires a profile, scheduler and command host.", this);
                enabled = false;
                return;
            }
            initialized = true;
        }

        private void OnEnable()
        {
            if (!initialized) return;
            home = motor.Position;
            beliefs = new BeliefStore(clock);
            selector = new GoalSelector(clock);
            planner = new IncrementalPlanner();
            runner = new ActionRunner(clock, new ChannelArbiter(clock), CoverLeases);
            sensor.Observed += Observe;
            sensor.Lost += Lose;
            sensor.ConfirmedNeutralization += ConfirmNeutralized;
            sensor.AllyObserved += ObserveAlly;
            weapon.ConfirmedNeutralization += ConfirmNeutralized;
            actor.Damaged += OnDamage;
            actor.Died += OnDeath;
            actor.Incapacitated += Interrupt;
            weapon.AmmunitionChanged += SelfChanged;
            weapon.FirePermission = CanFire;
            commands.Register(this);
            scheduler.Register(this, profile.senseInterval, profile.decisionInterval);
        }

        private void OnDisable()
        {
            generation++;
            if (!initialized) return;
            sensor.Observed -= Observe;
            sensor.Lost -= Lose;
            sensor.ConfirmedNeutralization -= ConfirmNeutralized;
            sensor.AllyObserved -= ObserveAlly;
            weapon.ConfirmedNeutralization -= ConfirmNeutralized;
            actor.Damaged -= OnDamage;
            actor.Died -= OnDeath;
            actor.Incapacitated -= Interrupt;
            weapon.AmmunitionChanged -= SelfChanged;
            runner?.Dispose();
            CoverLeases.ReleaseOwner("occupant-" + actor.Id);
            pendingCover = occupiedCover = null;
            motor.Stop();
            aim.Clear();
            commands.Unregister(this);
            scheduler.Unregister(this);
            visible.Clear();
            neutralized.Clear();
            allies.Clear();
            allySeenAt.Clear();
            plan.Clear();
            factories.Clear();
            selected = null;
            running = null;
            activeOrder = searchedOrder = null;
            safe = emergency = false;
            failures = 0;
            retryAt = 0;
        }

        private void SelfChanged() { revision++; }
        private void OnDamage(float damage, Vector3 position) { safe = false; revision++; }
        private void OnDeath()
        {
            OrderRecord order = commands.Service.GetPrimary(actor.Id);
            if (order != null) commands.Service.ReportFailure(order.Request.OrderId, "Recipient died.");
            runner.Dispose();
            plan.Clear();
            motor.Stop();
            aim.Clear();
            enabled = false;
        }

        private void Observe(SensedContact contact)
        {
            string fact = contact.Visual ? "contact" : "sound";
            var evidence = new Evidence(contact.EvidenceId, fact, contact.SubjectId,
                contact.Visual ? EvidenceKind.Vision : EvidenceKind.Hearing, Truth.True, contact.Position,
                contact.ObservedAt, contact.ExpiresAt - contact.ObservedAt, contact.Confidence,
                contact.SourceId, clock.Now, contact.Uncertainty);
            beliefs.Add(evidence);
            if (contact.Visual && contact.SubjectId != null)
            {
                bool changed = !visible.TryGetValue(contact.SubjectId, out var previous)
                    || previous.DistanceTo(contact.Position) > 1;
                visible[contact.SubjectId] = contact.Position;
                commands.RecordKnownTarget(actor.Id, contact.SubjectId);
                if (changed) revision++;
            }
        }

        private void Lose(string id) { if (visible.Remove(id)) revision++; }
        private void ObserveAlly(string id, Position position)
        {
            if (!allies.TryGetValue(id, out var last) || last.DistanceTo(position) > 1) revision++;
            allies[id] = position;
            allySeenAt[id] = clock.Now;
        }
        private void ConfirmNeutralized(string id)
        {
            if (neutralized.Add(id)) revision++;
            visible.Remove(id);
        }
        public void Sense(double now) => sensor.Sense(now);

        public void Decide(double now)
        {
            if (!actor.CanAct) return;
            OrderRecord order = commands.Service.GetPrimary(actor.Id);
            string orderId = order?.Request.OrderId;
            if (orderId != activeOrder)
            {
                activeOrder = orderId;
                revision++;
                Invalidate();
            }
            bool danger = actor.HealthFraction < 0.25f && !safe;
            if (danger && !emergency)
            {
                emergency = true;
                commands.Service.SuspendForEmergency(actor.Id, "Seeking refuge after critical damage.");
            }
            else if (!danger && emergency)
            {
                emergency = false;
                commands.Service.ResumeAfterEmergency(actor.Id);
            }
            var candidates = new List<GoalCandidate>();
            string command = orderId;
            if (danger) candidates.Add(new GoalCandidate("StayAlive", 1, emergency: true));
            if (weapon.Magazine == 0 && weapon.Reserve > 0)
                candidates.Add(new GoalCandidate("RestoreReadiness", 1, requiredCommandId: command));
            string target = SelectThreat(order);
            if (target != null && CanFire())
                candidates.Add(new GoalCandidate("EngageThreat", profile.aggression, requiredCommandId: command));
            Evidence contact = SearchEvidence(order);
            if (order?.Request.Type == OrderType.Attack && target == null && contact == null
                && !neutralized.Contains(order.Request.TargetId ?? ""))
            {
                commands.Service.ReportFailure(order.Request.OrderId, "Contact unresolved after bounded search or evidence expiry.");
                Invalidate();
                return;
            }
            if (contact != null && !danger)
                candidates.Add(new GoalCandidate("InvestigateContact", 0.5, requiredCommandId: command));
            if (order != null && order.Status == OrderStatus.Active
                && (order.Request.Type != OrderType.Attack || neutralized.Contains(order.Request.TargetId ?? "")))
                candidates.Add(new GoalCandidate("Assignment", order.Request.Type == OrderType.HoldArea ? 0.25 : 0.6,
                    requiredCommandId: command));
            else if (order == null)
                candidates.Add(new GoalCandidate("DefendObjective", 0.2));
            GoalDecision decision = selector.Select(candidates, new GoalContext(revision, command));
            if (decision.Changed)
            {
                Invalidate();
                selected = decision.Goal;
            }
            if (running == null && plan.Count == 0 && now >= retryAt
                && (planner.Result == null || planner.Result.Status != PlanningStatus.InProgress))
                BuildPlan(order, target, contact);
            if (order != null && Verifies(order.Request, "verified"))
            {
                if (order.Request.Constraints.Completion == CompletionMode.Once || order.Milestones == 0)
                    commands.Service.ReportCompletion(order.Request.OrderId, "verified");
            }
        }

        private void Invalidate()
        {
            generation++;
            plan.Clear();
            runner?.RequestCancel();
        }
        public void Interrupt()
        {
            Invalidate();
            motor.Stop();
            aim.Clear();
            retryAt = clock.Now + profile.decisionInterval;
        }

        private string SelectThreat(OrderRecord order)
        {
            if (order?.Request.Type == OrderType.Attack)
                return order.Request.TargetId != null && visible.ContainsKey(order.Request.TargetId)
                    ? order.Request.TargetId : null;
            if (order != null && order.Request.Type != OrderType.HoldArea) return null;
            return visible.Keys.Where(id => !neutralized.Contains(id) && Allowed(visible[id]))
                .OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        }

        private Evidence SearchEvidence(OrderRecord order)
        {
            if (order != null && order.Request.Type != OrderType.Attack) return null;
            return beliefs.Snapshot().Where(e => e.Position.HasValue && !neutralized.Contains(e.SubjectId ?? "")
                    && (e.SubjectId == null || !visible.ContainsKey(e.SubjectId))
                    && (order == null || e.SubjectId == order.Request.TargetId)
                    && !beliefs.Query(e.Fact, e.SubjectId).SearchComplete && Allowed(e.Position.Value))
                .OrderByDescending(e => e.ObservedAt).ThenBy(e => e.ReportId, StringComparer.Ordinal).FirstOrDefault();
        }

        private bool Allowed(Position position)
        {
            string region = commands.Service.GetEffectiveConstraints(actor.Id).RegionId;
            return region == null || commands.InRegion(region, position.ToVector());
        }
        private bool CanFire() => actor.CanAct && !commands.Service.GetEffectiveConstraints(actor.Id).HoldFire;
        private bool AllowedRoute(Position destination)
        {
            if (!Allowed(destination)) return false;
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(transform.position, destination.ToVector(), NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete
                && path.corners.All(corner => Allowed(corner.ToPosition()));
        }

        private void BuildPlan(OrderRecord order, string target, Evidence contact)
        {
            if (selected == null) return;
            factories.Clear();
            var actions = new List<PlanningAction>();
            var done = new Fact("episode", true, selected.Id);
            if (selected.Id == "RestoreReadiness")
            {
                Add(actions, "Reload", new[] { new Fact("ready", false) }, new[] { done }, () =>
                    new RuntimeAction("Reload", new[] { "hands" }, Array.Empty<string>(), () => weapon.Reserve > 0,
                        () => weapon.TryReload(), () => weapon.IsReloading ? ActionStatus.Running :
                            weapon.Magazine > 0 ? ActionStatus.Succeeded : ActionStatus.Failed, weapon.CancelReload));
            }
            else if (selected.Id == "EngageThreat" && target != null)
            {
                Position observed = visible[target];
                if (motor.Position.DistanceTo(observed) <= profile.engagementRange)
                    AddBurst(actions, target, done, Array.Empty<Fact>());
                else
                {
                    foreach (Position point in AttackPositions(observed).Take(3))
                    {
                        string id = "approach-" + actions.Count;
                        var at = new Fact("attack-position", true, id);
                        AddMove(actions, id, point, new[] { at },
                            () => visible.TryGetValue(target, out var latest) && latest.DistanceTo(observed) <= 2);
                        AddBurst(actions, target, done, new[] { at }, id);
                    }
                }
            }
            else if (selected.Id == "StayAlive")
            {
                Position? threat = visible.Count > 0 ? visible.OrderBy(p => p.Key, StringComparer.Ordinal).First().Value
                    : beliefs.Snapshot().Where(e => e.Kind == EvidenceKind.Vision && e.Position.HasValue)
                        .OrderByDescending(e => e.ObservedAt).FirstOrDefault()?.Position;
                foreach (CoverSlot cover in coverSlots.Where(c => c != null && c != occupiedCover
                    && c.isActiveAndEnabled && (!threat.HasValue || c.UsableFrom(threat.Value.ToVector())))
                    .Where(c => CoverLeases.IsAvailable(new[] { "cover-" + c.Id }))
                    .Where(c => Allowed(c.Refuge.ToPosition()) && Reachable(c.Refuge))
                    .OrderBy(c => Vector3.Distance(transform.position, c.Refuge)).ThenBy(c => c.Id, StringComparer.Ordinal).Take(4))
                {
                    int coverRevision = cover.Revision;
                    AddMove(actions, "cover-" + cover.Id, cover.Refuge.ToPosition(), new[] { done },
                        () => cover != null && cover.isActiveAndEnabled && cover.Revision == coverRevision
                            && (!threat.HasValue || cover.UsableFrom(threat.Value.ToVector())),
                        "cover-" + cover.Id, () => { pendingCover = cover; safe = true; });
                }
            }
            else if (selected.Id == "InvestigateContact" && contact != null)
                AddSearch(actions, contact.Position.Value, done, () => beliefs.CompleteSearch(contact.ReportId));
            else if (selected.Id == "Assignment" && order != null)
            {
                if (order.Request.Type == OrderType.HoldArea && order.Milestones > 0
                    && order.Request.Position.HasValue && motor.Position.DistanceTo(order.Request.Position.Value) < 0.35)
                {
                    retryAt = clock.Now + 0.5;
                    return;
                }
                if (order.Request.Type == OrderType.SearchArea)
                    AddSearch(actions, order.Request.Position.Value, done, () => searchedOrder = order.Request.OrderId);
                else if (order.Request.Type == OrderType.Follow && order.Request.TargetId != null
                    && allies.TryGetValue(order.Request.TargetId, out var leader)
                    && clock.Now - allySeenAt[order.Request.TargetId] < 2)
                {
                    double distance = motor.Position.DistanceTo(leader);
                    if (distance > 4 || distance < 2)
                    {
                        Vector3 away = transform.position - leader.ToVector();
                        away.y = 0;
                        if (away.sqrMagnitude < 0.01f) away = transform.forward;
                        away.Normalize();
                        AddMove(actions, "follow", (leader.ToVector() + away * 3).ToPosition(), new[] { done });
                    }
                }
                else if ((order.Request.Type == OrderType.Move || order.Request.Type == OrderType.HoldArea)
                    && order.Request.Position.HasValue)
                    AddMove(actions, "assignment", order.Request.Position.Value, new[] { done });
                else if (order.Request.Type == OrderType.Attack && neutralized.Contains(order.Request.TargetId ?? ""))
                    commands.Service.ReportCompletion(order.Request.OrderId, "verified");
            }
            else if (selected.Id == "DefendObjective")
                AddMove(actions, "return", home, new[] { done });
            if (actions.Count == 0)
            {
                selector.MarkInfeasible(selected.Id);
                retryAt = clock.Now + 0.5;
                return;
            }
            planner.Start(new PlanningSnapshot(generation, revision, selected.Id,
                new WorldState(new[] { new Fact("ready", weapon.Magazine > 0) })), new[] { done }, actions);
        }

        private IEnumerable<Position> AttackPositions(Position observed)
        {
            Vector3 away = transform.position - observed.ToVector();
            away.y = 0;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            foreach (float angle in new[] { 0f, 30f, -30f })
            {
                Vector3 point = observed.ToVector() + Quaternion.Euler(0, angle, 0) * away * profile.engagementRange * 0.8f;
                if (NavMesh.SamplePosition(point, out NavMeshHit hit, 2, NavMesh.AllAreas)
                    && Allowed(hit.position.ToPosition()) && Reachable(hit.position)) yield return hit.position.ToPosition();
            }
        }
        private bool Reachable(Vector3 point)
        {
            var path = new NavMeshPath();
            return NavMesh.CalculatePath(transform.position, point, NavMesh.AllAreas, path)
                && path.status == NavMeshPathStatus.PathComplete;
        }
        private void Add(List<PlanningAction> actions, string id, Fact[] preconditions, Fact[] effects, Func<RuntimeAction> create)
        {
            actions.Add(new PlanningAction(id, preconditions, effects));
            factories[id] = create;
        }
        private void AddMove(List<PlanningAction> actions, string id, Position point, Fact[] effects,
            Func<bool> invariant = null, string resource = null, Action completed = null)
        {
            Add(actions, id, Array.Empty<Fact>(), effects, () =>
            {
                string region = commands.Service.GetEffectiveConstraints(actor.Id).RegionId;
                return new RuntimeAction(id,
                new[] { "locomotion" }, resource == null ? Array.Empty<string>() : new[] { resource },
                () => AllowedRoute(point) && (invariant == null || invariant()),
                () => motor.RequestDestination(point, 0.3f),
                () =>
                {
                    string currentRegion = commands.Service.GetEffectiveConstraints(actor.Id).RegionId;
                    if (currentRegion != region)
                    {
                        region = currentRegion;
                        if (!AllowedRoute(point)) return ActionStatus.Failed;
                    }
                    if (!Allowed(point) || !Allowed(motor.Position) || (invariant != null && !invariant())) return ActionStatus.Failed;
                    if (motor.State == RouteState.Arrived) { completed?.Invoke(); return ActionStatus.Succeeded; }
                    return motor.State == RouteState.Pending || motor.State == RouteState.Moving
                        ? ActionStatus.Running : ActionStatus.Failed;
                }, motor.Stop);
            });
        }
        private void AddBurst(List<PlanningAction> actions, string target, Fact done, Fact[] preconditions, string suffix = "")
        {
            Add(actions, "burst" + suffix, preconditions, new[] { done }, () =>
            {
                int shots = 0;
                return new RuntimeAction("burst", new[] { "aim", "hands" }, Array.Empty<string>(),
                    () => CanFire() && visible.TryGetValue(target, out var position)
                        && motor.Position.DistanceTo(position) <= profile.engagementRange && weapon.Magazine > 0,
                    () => true,
                    () =>
                    {
                        if (!CanFire() || !visible.TryGetValue(target, out var position)
                            || motor.Position.DistanceTo(position) > profile.engagementRange) return ActionStatus.Failed;
                        aim.AimAt(position);
                        if (weapon.TryFire()) shots++;
                        return shots >= 3 || weapon.Magazine == 0 || neutralized.Contains(target)
                            ? ActionStatus.Succeeded : ActionStatus.Running;
                    }, aim.Clear);
            });
        }
        private void AddSearch(List<PlanningAction> actions, Position point, Fact done, Action completed)
        {
            Add(actions, "search", Array.Empty<Fact>(), new[] { done }, () =>
            {
                double arrived = -1;
                string region = commands.Service.GetEffectiveConstraints(actor.Id).RegionId;
                return new RuntimeAction("search", new[] { "locomotion", "aim" }, Array.Empty<string>(),
                    () => AllowedRoute(point), () => motor.RequestDestination(point, 0.3f),
                    () =>
                    {
                        string currentRegion = commands.Service.GetEffectiveConstraints(actor.Id).RegionId;
                        if (currentRegion != region)
                        {
                            region = currentRegion;
                            if (!AllowedRoute(point)) return ActionStatus.Failed;
                        }
                        if (!Allowed(point) || !Allowed(motor.Position)) return ActionStatus.Failed;
                        if (motor.State == RouteState.Arrived)
                        {
                            if (arrived < 0) arrived = clock.Now;
                            if (clock.Now - arrived >= profile.searchDwellSeconds)
                            { completed(); return ActionStatus.Succeeded; }
                            aim.AimAt(new Position(point.X + Mathf.Sin((float)(clock.Now * 2)) * 3, point.Y + 1, point.Z + 3));
                        }
                        else if (motor.State != RouteState.Pending && motor.State != RouteState.Moving) return ActionStatus.Failed;
                        return ActionStatus.Running;
                    }, () => { motor.Stop(); aim.Clear(); });
            });
        }
        public void PlanStep()
        {
            if (selected == null || planner.Result?.Status != PlanningStatus.InProgress) return;
            PlanningResult result = planner.Step(profile.plannerExpansionsPerStep, generation, revision, selected.Id);
            if (result.Status == PlanningStatus.InProgress) return;
            if (result.Status == PlanningStatus.Succeeded)
                foreach (PlanningAction action in result.Plan) plan.Enqueue(action);
            else
            {
                selector.MarkInfeasible(selected.Id);
                LastFailure = result.Status.ToString();
                retryAt = clock.Now + 0.5;
            }
        }
        private void Update()
        {
            if (!initialized || !actor.IsAlive) return;
            if (!actor.CanAct) { runner.RequestCancel(); return; }
            if (occupiedCover != null)
            {
                string owner = "occupant-" + actor.Id;
                if (!occupiedCover.isActiveAndEnabled || Vector3.Distance(transform.position, occupiedCover.Refuge) > 0.6f
                    || !CoverLeases.TryAcquire(owner, new[] { "cover-" + occupiedCover.Id }, 0.5))
                {
                    CoverLeases.ReleaseOwner(owner);
                    occupiedCover = null;
                    safe = false;
                }
            }
            if (running != null)
            {
                ActionExecutionResult result = runner.Tick();
                if (!result.IsTerminal) return;
                running = null;
                if (pendingCover != null)
                {
                    if (result.Status == ActionStatus.Succeeded && CoverLeases.TryAcquire("occupant-" + actor.Id,
                        new[] { "cover-" + pendingCover.Id }, 0.5)) occupiedCover = pendingCover;
                    else safe = false;
                    pendingCover = null;
                }
                if (result.Status != ActionStatus.Succeeded)
                {
                    plan.Clear();
                    LastFailure = result.Reason;
                    if (clock.Now - failureWindow > 5) { failureWindow = clock.Now; failures = 0; }
                    if (++failures >= 3)
                    {
                        retryAt = clock.Now + 5;
                        selector.MarkInfeasible(selected?.Id ?? "none");
                        if (activeOrder != null) commands.Service.ReportFailure(activeOrder, LastFailure);
                    }
                }
            }
            if (running == null && plan.Count > 0)
            {
                PlanningAction next = plan.Dequeue();
                if (!factories.TryGetValue(next.Id, out var create)) { plan.Clear(); return; }
                running = create();
                running.ExpectedEffects = next.Effects;
                runner.Start(running, new ActionContext(_ => running != null && running.Verified),
                    profile.searchTimeoutSeconds, 0);
            }
        }
        public bool Verifies(OrderRequest order, string evidence)
        {
            if (evidence != "verified") return false;
            if (order.Type == OrderType.Attack) return order.TargetId != null && neutralized.Contains(order.TargetId);
            if (order.Type == OrderType.SearchArea) return searchedOrder == order.OrderId;
            if (order.Type == OrderType.Follow)
                return order.TargetId != null && allies.TryGetValue(order.TargetId, out var leader)
                    && clock.Now - allySeenAt[order.TargetId] < 2 && motor.Position.DistanceTo(leader) >= 2
                    && motor.Position.DistanceTo(leader) <= 4;
            return order.Position.HasValue && motor.Position.DistanceTo(order.Position.Value) <= 0.35
                && motor.State == RouteState.Arrived;
        }
        private sealed class RuntimeAction : IRuntimeAction
        {
            private readonly Func<bool> canStart, begin;
            private readonly Func<ActionStatus> tick;
            private readonly Action cleanup;
            public string Id { get; }
            public IReadOnlyCollection<string> Channels { get; }
            public IReadOnlyCollection<string> Resources { get; }
            public IReadOnlyList<Fact> ExpectedEffects { get; set; } = Array.Empty<Fact>();
            public bool Verified { get; private set; }
            public RuntimeAction(string id, string[] channels, string[] resources, Func<bool> canStart,
                Func<bool> begin, Func<ActionStatus> tick, Action cleanup)
            { Id = id; Channels = channels; Resources = resources; this.canStart = canStart; this.begin = begin; this.tick = tick; this.cleanup = cleanup; }
            public bool CanStart(ActionContext context) => canStart();
            public void Begin(ActionContext context) { if (!begin()) throw new InvalidOperationException("Adapter rejected start."); }
            public ActionTick Tick(ActionContext context)
            {
                ActionStatus status = tick();
                Verified = status == ActionStatus.Succeeded;
                return new ActionTick(status, status == ActionStatus.Failed ? "Adapter invariant or progress failed." : "");
            }
            public void RequestCancel(ActionContext context) => cleanup();
            public void Cleanup(ActionContext context) => cleanup();
        }
    }
}
