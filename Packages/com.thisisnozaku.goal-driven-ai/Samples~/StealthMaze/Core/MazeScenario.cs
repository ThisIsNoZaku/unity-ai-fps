using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Language;
using GoalDrivenAI.Navigation;

namespace GoalDrivenAI.Stealth
{
    public enum MazeRole { Infiltrator, Commander, Observer }

    /// <summary>Host-owned objectives and commander authority, independent of rendering and input.</summary>
    public sealed class MazeScenario : ICommandValidator, ICommandContextSource
    {
        public const string InfiltratorFaction = "infiltrator";
        public const string GuardsFaction = "guards";
        public const string InfiltratorId = "infiltrator-1";
        public const string CommanderId = "guard-commander";
        private readonly CatalogHandle[] checkpointLocations;
        private readonly string[] guardIds;
        private readonly IClock clock;
        private readonly CommandServiceAdapter plans;
        private readonly PlanCompiler compiler;
        private readonly BoundedPlanJournal journal = new BoundedPlanJournal();
        private readonly Dictionary<int, ValidatedCommandPlan> patrolPlans = new Dictionary<int, ValidatedCommandPlan>();
        private readonly Dictionary<string, Position> verifiedArrivals = new Dictionary<string, Position>();
        private readonly int[] patrolVisits;
        private readonly string session = Guid.NewGuid().ToString("N");
        private long generation;
        // Immutable named locations are shared by objectives, commands, rendering and pathing.
        public LocationRegistry Locations { get; }
        public IReadOnlyList<CatalogHandle> CheckpointLocations { get; }
        public CatalogHandle ExitLocation { get; }
        public CatalogHandle NextObjective => AllReached ? ExitLocation : checkpointLocations[Reached];
        public IReadOnlyList<Position> Checkpoints { get; }
        public IReadOnlyList<string> GuardIds { get; }
        public int Reached { get; private set; }
        public bool AllReached => Reached == checkpointLocations.Length;
        public CommandService Commands { get; }
        public Position Exit => ExitLocation.Position;
        public const float CellSize = 3;
        public const double VisitRadius = 1.1;

        public MazeScenario(IClock clock, MazeMap map, int guardCount = 4, int checkpointCount = 3)
        {
            if (clock == null || map == null) throw new ArgumentNullException();
            this.clock = clock;
            if (guardCount < 1 || guardCount > 6) throw new ArgumentOutOfRangeException(nameof(guardCount));
            var path = map.Route((1, 1), (map.Width - 2, map.Height - 2));
            if (checkpointCount < 2 || checkpointCount >= path.Count - 1) throw new ArgumentOutOfRangeException(nameof(checkpointCount));
            checkpointLocations = new CatalogHandle[checkpointCount];
            for (int i = 0; i < checkpointCount; i++)
            {
                var cell = path[(i + 1) * (path.Count - 1) / (checkpointCount + 1)];
                string id = CheckpointId(i);
                checkpointLocations[i] = new CatalogHandle(id, HandleKind.Location,
                    new Position(cell.x * CellSize, .02f, cell.y * CellSize), regionId: id);
            }
            ExitLocation = new CatalogHandle("exit", HandleKind.Location,
                new Position((map.Width - 2) * CellSize, .02f, (map.Height - 2) * CellSize));
            CheckpointLocations = Array.AsReadOnly(checkpointLocations);
            Locations = new LocationRegistry(checkpointLocations.Concat(new[] { ExitLocation }));
            // Preserve the positional API for existing clients; location handles own the values.
            Checkpoints = Array.AsReadOnly(checkpointLocations.Select(location => location.Position).ToArray());
            guardIds = Enumerable.Range(1, guardCount).Select(i => "guard-" + i).ToArray();
            GuardIds = Array.AsReadOnly(guardIds);
            patrolVisits = new int[guardCount];
            Commands = new CommandService(clock, this);
            Commands.Hierarchy.SetRole(CommanderId, AuthorityLevel.Leader);
            foreach (string id in guardIds)
            {
                Commands.Hierarchy.SetRole(id, AuthorityLevel.Autonomous);
                Commands.Hierarchy.AddHierarchy(CommanderId, id);
            }
            compiler = new PlanCompiler(new CommandCatalog("maze-plans-v1", new[] {
                new OrderDefinition("Move", true, true, 120, PlanFailurePolicy.AbortPlan, repeatable: true)
            }, allowLooping: true));
            plans = new CommandServiceAdapter(Commands, this, clock, new CapturedThreadScheduler(), journal);
            Reset();
        }

        public static string FactionOf(string identity) => identity == InfiltratorId ? InfiltratorFaction :
            identity == CommanderId || (identity != null && identity.StartsWith("guard-", StringComparison.Ordinal)) ? GuardsFaction : "";

        public bool Protect(string issuer, int guard, int checkpoint)
        {
            if (guard < 0 || guard >= guardIds.Length || checkpoint < 0 || checkpoint >= checkpointLocations.Length) return false;
            var location = checkpointLocations[checkpoint];
            var request = new OrderRequest(Guid.NewGuid().ToString("N"), guardIds[guard], OrderType.HoldArea,
                location.Position, location.Id, new OrderConstraints(regionId: location.RegionId));
            if (!Commands.CanSubmit(issuer, request, out _)) return false;
            StopPatrol(guard);
            if (!Commands.Submit(issuer, request).Accepted) return false;
            // This demo's command link is reliable and immediate; delivery remains explicit.
            return Commands.Deliver(request.OrderId).Accepted && Commands.Acknowledge(request.OrderId).Accepted;
        }
        public bool Patrol(string issuer, int guard, double dwellSeconds = 1.5)
        {
            if (issuer != CommanderId || guard < 0 || guard >= guardIds.Length) return false;
            if (IsPatrolling(guard)) return true;
            var context = Capture(issuer, new[] { guardIds[guard] });
            int first = guard % checkpointLocations.Length;
            var stops = checkpointLocations.Skip(first).Concat(checkpointLocations.Take(first)).Select(location => location.Id);
            var compiled = compiler.CompilePatrol(context, stops, dwellSeconds);
            if (!compiled.Success || !plans.TryCommit(compiled.Plan!, out _)) return false;
            var old = Commands.GetPrimary(guardIds[guard]);
            if (old != null) Commands.Cancel(issuer, old.Request.OrderId);
            StopPatrol(guard);
            patrolPlans[guard] = compiled.Plan!; patrolVisits[guard] = 0;
            journal.RecordAccepted(compiled.Plan!); PumpPlans(); return true;
        }
        public bool IsPatrolling(int guard) => patrolPlans.TryGetValue(guard, out var plan) &&
            plans.GetExecution(plan.Hash)?.Finished == false;
        public int PatrolVisits(int guard) => guard >= 0 && guard < patrolVisits.Length ? patrolVisits[guard] : 0;
        public int PatrolCheckpoint(int guard)
        {
            if (guard < 0 || guard >= guardIds.Length) return -1;
            var active = Commands.GetPrimary(guardIds[guard]);
            if (active?.Request.Type == OrderType.Move) return IndexOf(active.Request);
            if (patrolPlans.TryGetValue(guard, out var plan))
            {
                var execution = plans.GetExecution(plan.Hash);
                var next = plan.Nodes.FirstOrDefault(n => n.Kind == PlanNodeKind.IssueOrder &&
                    execution?.States[n.Id] == PlanNodeStatus.Pending) ?? plan.Nodes.First(n => n.Kind == PlanNodeKind.IssueOrder);
                return Array.FindIndex(checkpointLocations, location => location.Id == next.Target?.Id);
            }
            return guard % checkpointLocations.Length;
        }
        private void StopPatrol(int guard)
        {
            if (patrolPlans.TryGetValue(guard, out var plan)) plans.Cancel(plan.Hash);
            patrolPlans.Remove(guard);
        }
        public void PumpPlans()
        {
            Commands.Tick(); plans.Pump();
            foreach (var order in Commands.Orders.Where(o => o.Status == OrderStatus.PendingDelivery))
                if (Commands.Deliver(order.Request.OrderId).Accepted) Commands.Acknowledge(order.Request.OrderId);
        }
        public bool CompleteNavigation(int guard, Position actualPosition)
        {
            if (!IsPatrolling(guard)) return false;
            var order = Commands.GetPrimary(guardIds[guard]);
            if (order?.Status != OrderStatus.Active || order.Request.Type != OrderType.Move) return false;
            verifiedArrivals[order.Request.OrderId] = actualPosition;
            try
            {
                if (!Commands.ReportCompletion(order.Request.OrderId, "Host verified navigation arrival").Accepted) return false;
                patrolVisits[guard]++; return true;
            }
            finally { verifiedArrivals.Remove(order.Request.OrderId); }
        }
        public CommandContext Capture(string issuer, IReadOnlyList<string> recipients) =>
            new CommandContext(issuer, session, generation, recipients, Locations.Values, clock.Now);
        public bool IsCurrent(CommandContext context) => context.Session == session && context.Revision == generation;
        public int DefenseFor(int guard)
        {
            if (guard < 0 || guard >= guardIds.Length) return -1;
            var order = Commands.GetPrimary(guardIds[guard]);
            return order?.Request.Type != OrderType.HoldArea ? -1 : IndexOf(order.Request);
        }
        public void Reset()
        {
            generation++; Reached = 0;
            foreach (int guard in patrolPlans.Keys.ToArray()) StopPatrol(guard);
            Array.Clear(patrolVisits, 0, patrolVisits.Length); verifiedArrivals.Clear();
            foreach (var order in Commands.Orders.Where(o => !o.IsTerminal)) Commands.Cancel(CommanderId, order.Request.OrderId);
            for (int i = 0; i < guardIds.Length; i++)
            {
                Commands.ResumeAfterEmergency(guardIds[i]);
                Protect(CommanderId, i, i % checkpointLocations.Length);
            }
        }
        public bool Visit(string actor, Position position)
        {
            if (actor != InfiltratorId || AllReached || NextObjective.Position.DistanceTo(position) > VisitRadius) return false;
            Reached++; return true;
        }
        public bool CanEscape(string actor, Position position) => actor == InfiltratorId && AllReached && Exit.DistanceTo(position) <= VisitRadius;
        private static string CheckpointId(int index) => "checkpoint-" + (index + 1);
        private int IndexOf(OrderRequest request) => Array.FindIndex(checkpointLocations, location =>
            location.Id == request.TargetId && request.Position.HasValue && request.Position.Value.Equals(location.Position));
        public bool ValidateAuthority(string issuer, OrderRequest request, out string reason)
        {
            reason = "Only the guards' commander may assign guard defenses.";
            return issuer == CommanderId && guardIds.Contains(request.Recipient) && FactionOf(issuer) == FactionOf(request.Recipient);
        }
        public bool ValidateCapability(string recipient, OrderType type, out string reason)
        { reason = "Demo guards accept checkpoint Move and HoldArea orders."; return guardIds.Contains(recipient) && (type == OrderType.HoldArea || type == OrderType.Move); }
        public bool ValidateTarget(OrderRequest request, out string reason)
        { reason = "Order must bind an existing checkpoint."; int i = IndexOf(request); return i >= 0 && request.TargetId == checkpointLocations[i].Id; }
        public bool ValidateRegion(OrderRequest request, out string reason)
        { reason = "Defense region must match the checkpoint."; int i = IndexOf(request); return i >= 0 && request.Constraints.RegionId == checkpointLocations[i].RegionId; }
        public bool IsRegionWithin(string childRegion, string parentRegion) => childRegion == parentRegion;
        public bool VerifyCompletion(OrderRequest request, string evidence, out string reason)
        {
            reason = "Only host-verified navigation arrival completes a Move; checkpoint defense remains persistent.";
            return request.Type == OrderType.Move && IndexOf(request) >= 0 &&
                verifiedArrivals.TryGetValue(request.OrderId, out var actual) &&
                request.Position!.Value.DistanceTo(actual) <= .3;
        }
    }
}
