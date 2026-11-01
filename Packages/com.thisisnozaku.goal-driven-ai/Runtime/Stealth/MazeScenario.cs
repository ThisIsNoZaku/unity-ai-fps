using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI.Commands;

namespace GoalDrivenAI.Stealth
{
    public enum MazeRole { Infiltrator, Commander, Observer }

    /// <summary>Host-owned objectives and commander authority, independent of rendering and input.</summary>
    public sealed class MazeScenario : ICommandValidator
    {
        public const string InfiltratorFaction = "infiltrator";
        public const string GuardsFaction = "guards";
        public const string InfiltratorId = "infiltrator-1";
        public const string CommanderId = "guard-commander";
        private readonly Position[] checkpoints;
        private readonly string[] guardIds;
        public IReadOnlyList<Position> Checkpoints { get; }
        public IReadOnlyList<string> GuardIds { get; }
        public int Reached { get; private set; }
        public bool AllReached => Reached == checkpoints.Length;
        public CommandService Commands { get; }
        public Position Exit { get; }
        public const float CellSize = 3;
        public const double VisitRadius = 1.1;

        public MazeScenario(IClock clock, MazeMap map, int guardCount = 4, int checkpointCount = 3)
        {
            if (clock == null || map == null) throw new ArgumentNullException();
            if (guardCount < 1 || guardCount > 6) throw new ArgumentOutOfRangeException(nameof(guardCount));
            var path = map.Route((1, 1), (map.Width - 2, map.Height - 2));
            if (checkpointCount < 2 || checkpointCount >= path.Count - 1) throw new ArgumentOutOfRangeException(nameof(checkpointCount));
            checkpoints = new Position[checkpointCount];
            for (int i = 0; i < checkpointCount; i++)
            {
                var cell = path[(i + 1) * (path.Count - 1) / (checkpointCount + 1)];
                checkpoints[i] = new Position(cell.x * CellSize, .02f, cell.y * CellSize);
            }
            Exit = new Position((map.Width - 2) * CellSize, .02f, (map.Height - 2) * CellSize);
            Checkpoints = Array.AsReadOnly(checkpoints);
            guardIds = Enumerable.Range(1, guardCount).Select(i => "guard-" + i).ToArray();
            GuardIds = Array.AsReadOnly(guardIds);
            Commands = new CommandService(clock, this);
            Commands.Hierarchy.SetRole(CommanderId, AuthorityLevel.Leader);
            foreach (string id in guardIds)
            {
                Commands.Hierarchy.SetRole(id, AuthorityLevel.Autonomous);
                Commands.Hierarchy.AddHierarchy(CommanderId, id);
            }
            Reset();
        }

        public static string FactionOf(string identity) => identity == InfiltratorId ? InfiltratorFaction :
            identity == CommanderId || (identity != null && identity.StartsWith("guard-", StringComparison.Ordinal)) ? GuardsFaction : "";

        public bool Protect(string issuer, int guard, int checkpoint)
        {
            if (guard < 0 || guard >= guardIds.Length || checkpoint < 0 || checkpoint >= checkpoints.Length) return false;
            var request = new OrderRequest(Guid.NewGuid().ToString("N"), guardIds[guard], OrderType.HoldArea,
                checkpoints[checkpoint], CheckpointId(checkpoint), new OrderConstraints(regionId: CheckpointId(checkpoint)));
            if (!Commands.Submit(issuer, request).Accepted) return false;
            // This demo's command link is reliable and immediate; delivery remains explicit.
            return Commands.Deliver(request.OrderId).Accepted && Commands.Acknowledge(request.OrderId).Accepted;
        }
        public int DefenseFor(int guard)
        {
            if (guard < 0 || guard >= guardIds.Length) return -1;
            var order = Commands.GetPrimary(guardIds[guard]);
            return order == null ? -1 : Array.FindIndex(checkpoints, p => order.Request.Position.HasValue && p.Equals(order.Request.Position.Value));
        }
        public void Reset()
        {
            Reached = 0;
            foreach (var order in Commands.Orders.Where(o => !o.IsTerminal)) Commands.Cancel(CommanderId, order.Request.OrderId);
            for (int i = 0; i < guardIds.Length; i++)
            {
                Commands.ResumeAfterEmergency(guardIds[i]);
                Protect(CommanderId, i, i % checkpoints.Length);
            }
        }
        public bool Visit(string actor, Position position)
        {
            if (actor != InfiltratorId || AllReached || checkpoints[Reached].DistanceTo(position) > VisitRadius) return false;
            Reached++; return true;
        }
        public bool CanEscape(string actor, Position position) => actor == InfiltratorId && AllReached && Exit.DistanceTo(position) <= VisitRadius;
        private static string CheckpointId(int index) => "checkpoint-" + (index + 1);
        private int IndexOf(OrderRequest request) => Array.FindIndex(checkpoints, p =>
            request.Position.HasValue && request.Position.Value.Equals(p));
        public bool ValidateAuthority(string issuer, OrderRequest request, out string reason)
        {
            reason = "Only the guards' commander may assign guard defenses.";
            return issuer == CommanderId && guardIds.Contains(request.Recipient) && FactionOf(issuer) == FactionOf(request.Recipient);
        }
        public bool ValidateCapability(string recipient, OrderType type, out string reason)
        { reason = "Demo guards accept checkpoint HoldArea orders."; return guardIds.Contains(recipient) && type == OrderType.HoldArea; }
        public bool ValidateTarget(OrderRequest request, out string reason)
        { reason = "Order must bind an existing checkpoint."; int i = IndexOf(request); return i >= 0 && request.TargetId == CheckpointId(i); }
        public bool ValidateRegion(OrderRequest request, out string reason)
        { reason = "Defense region must match the checkpoint."; int i = IndexOf(request); return i >= 0 && request.Constraints.RegionId == CheckpointId(i); }
        public bool IsRegionWithin(string childRegion, string parentRegion) => childRegion == parentRegion;
        public bool VerifyCompletion(OrderRequest request, string evidence, out string reason)
        { reason = "Checkpoint defense is persistent until replaced or cancelled."; return false; }
    }
}
