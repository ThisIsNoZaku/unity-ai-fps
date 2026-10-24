#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI.Commands;

namespace GoalDrivenAI.Language
{
    public sealed class CommandServiceAdapter : ICommandPlanSink, IPlanNodeRuntime
    {
        private readonly CommandService commands;
        private readonly ICommandContextSource contexts;
        private readonly IClock clock;
        private readonly IMainThreadScheduler scheduler;
        private readonly IPlanJournal journal;
        private readonly Func<OrderRecord, ResultPredicate, bool>? milestone;
        private readonly Action<string>? report;
        private readonly Func<CommandContext, ValidatedPlanNode, bool>? signal;
        private readonly Dictionary<string, double> timers = new Dictionary<string, double>();
        private readonly Dictionary<string, DependencyPlanExecutor> executions = new Dictionary<string, DependencyPlanExecutor>();
        private readonly HashSet<string> accepted = new HashSet<string>(StringComparer.Ordinal);
        public CommandServiceAdapter(CommandService commands, ICommandContextSource contexts, IClock clock,
            IMainThreadScheduler scheduler, IPlanJournal journal,
            Func<OrderRecord, ResultPredicate, bool>? milestone = null, Action<string>? report = null,
            Func<CommandContext, ValidatedPlanNode, bool>? signal = null)
        {
            this.commands = commands; this.contexts = contexts; this.clock = clock; this.scheduler = scheduler;
            this.journal = journal; this.milestone = milestone; this.report = report;
            this.signal = signal;
        }
        public bool TryCommit(ValidatedCommandPlan plan, out string reason)
        {
            MainThread();
            if (accepted.Contains(plan.Context.RequestKey)) { reason = "Request already accepted."; return true; }
            if (commands.Save().Batches.Any(b => b.RequestId == plan.Context.RequestKey))
            { reason = "Request already recorded."; return false; }
            foreach (var key in executions.Where(p => p.Value.Finished).Select(p => p.Key).ToArray()) executions.Remove(key);
            if (executions.Count >= 32 || accepted.Count >= 4096) { reason = "Execution capacity."; return false; }
            if (!contexts.IsCurrent(plan.Context)) { reason = "Stale context."; return false; }
            try
            {
                var orders = plan.Nodes.Where(n => n.Kind == PlanNodeKind.IssueOrder)
                    .SelectMany(n => n.Recipients.Select(r => Order(plan, n, r))).ToArray();
                var policies = plan.Nodes.Where(n => n.Kind == PlanNodeKind.SetPolicy)
                    .SelectMany(n => n.Recipients.Select(r => Policy(plan, n, r))).ToArray();
                var accepted = commands.CommitBatch(plan.Context.Issuer, plan.Context.RequestKey, orders, policies);
                reason = accepted.Reason;
                if (!accepted.Accepted) return false;
                this.accepted.Add(plan.Context.RequestKey);
                executions.Add(plan.Hash, new DependencyPlanExecutor(plan, clock, this, scheduler, journal));
                return true;
            }
            catch (ArgumentException) { reason = "Unsupported command or policy."; return false; }
        }
        public void Pump()
        {
            MainThread();
            foreach (var execution in executions.Values.ToArray()) execution.Pump();
        }
        public bool Revalidate(ValidatedCommandPlan plan, ValidatedPlanNode node, out string reason)
        {
            MainThread();
            reason = "";
            if (!contexts.IsCurrent(plan.Context)) { reason = "Stale context."; return false; }
            if (node.Kind == PlanNodeKind.IssueOrder)
                foreach (var recipient in node.Recipients)
                    if (!commands.CanSubmit(plan.Context.Issuer, Order(plan, node, recipient), out reason)) return false;
            if (node.Kind == PlanNodeKind.SetPolicy)
                return commands.ValidateBatch(plan.Context.Issuer, plan.Hash + "_" + node.Id,
                    Array.Empty<OrderRequest>(), node.Recipients.Select(r => Policy(plan, node, r))).Accepted;
            return true;
        }
        public NodeExecutionResult Start(ValidatedCommandPlan plan, ValidatedPlanNode node)
        {
            MainThread();
            // Recheck the whole recipient set atomically before dispatching this node.
            if (node.Kind == PlanNodeKind.IssueOrder || node.Kind == PlanNodeKind.SetPolicy)
            {
                var orders = node.Kind == PlanNodeKind.IssueOrder ? node.Recipients.Select(r => Order(plan, node, r)).ToArray() : Array.Empty<OrderRequest>();
                var policies = node.Kind == PlanNodeKind.SetPolicy ? node.Recipients.Select(r => Policy(plan, node, r)).ToArray() : Array.Empty<PolicyRequest>();
                var check = commands.ValidateBatch(plan.Context.Issuer, plan.Hash + "_start_" + node.Id, orders, policies);
                if (!check.Accepted) return new NodeExecutionResult(PlanNodeStatus.Failed, check.Reason);
                foreach (var order in orders)
                    if (!commands.Submit(plan.Context.Issuer, order).Accepted) return new NodeExecutionResult(PlanNodeStatus.Failed, "Order rejected.");
                foreach (var policy in policies)
                    if (!commands.SetPolicy(plan.Context.Issuer, policy).Accepted) return new NodeExecutionResult(PlanNodeStatus.Failed, "Policy rejected.");
            }
            if (node.Kind == PlanNodeKind.WaitForResult)
            {
                if (node.Predicate == ResultPredicate.TimerElapsed) timers[plan.Hash + node.Id] = clock.Now + node.Duration!.Value;
                return Poll(plan, node);
            }
            if (node.Kind == PlanNodeKind.ReportResult) report?.Invoke(node.Report!);
            return new NodeExecutionResult(PlanNodeStatus.Succeeded);
        }
        public NodeExecutionResult Poll(ValidatedCommandPlan plan, ValidatedPlanNode node)
        {
            MainThread();
            if (node.Kind != PlanNodeKind.WaitForResult) return new NodeExecutionResult(PlanNodeStatus.Succeeded);
            if (node.Predicate == ResultPredicate.TimerElapsed)
                return new NodeExecutionResult(clock.Now >= timers[plan.Hash + node.Id] ? PlanNodeStatus.Succeeded : PlanNodeStatus.Running);
            if (node.Predicate == ResultPredicate.SignalReceived)
                return new NodeExecutionResult(signal?.Invoke(plan.Context, node) == true ? PlanNodeStatus.Succeeded : PlanNodeStatus.Running);
            var observed = plan.Nodes.First(n => n.Id == node.ObservedNode);
            bool all = true;
            foreach (var recipient in observed.Recipients)
            {
                var order = commands.GetOrder(Id(plan, observed, recipient));
                if (order == null || order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Superseded)
                    return new NodeExecutionResult(PlanNodeStatus.Failed, "Observed order unavailable or failed.");
                bool failed = order.IsTerminal && order.Status != OrderStatus.Completed;
                if (failed && node.Predicate != ResultPredicate.OrderFailed)
                    return new NodeExecutionResult(PlanNodeStatus.Failed, "Observed order failed.");
                bool satisfied = node.Predicate == ResultPredicate.OrderFailed ? failed :
                    node.Predicate == ResultPredicate.Completed || node.Predicate == ResultPredicate.OrderCompleted ?
                        order.Status == OrderStatus.Completed && order.Request.Constraints.Completion == CompletionMode.Once :
                        (order.Status == OrderStatus.Active || order.Status == OrderStatus.Completed) &&
                        (milestone?.Invoke(order, node.Predicate!.Value) ??
                            (node.Predicate == ResultPredicate.HoldPositionEstablished &&
                             order.Request.Type == OrderType.HoldArea && order.Milestones > 0));
                all &= satisfied;
            }
            return new NodeExecutionResult(all ? PlanNodeStatus.Succeeded : PlanNodeStatus.Running);
        }
        public void Cancel(ValidatedCommandPlan plan, ValidatedPlanNode node)
        {
            MainThread();
            // A timed-out wait does not revoke an independently persistent accepted order.
            if (node.Kind == PlanNodeKind.IssueOrder)
                foreach (var recipient in node.Recipients) commands.Cancel(plan.Context.Issuer, Id(plan, node, recipient));
        }
        private static string Id(ValidatedCommandPlan plan, ValidatedPlanNode node, string recipient) =>
            plan.Hash + "_" + node.Id + "_" + recipient;
        private static OrderRequest Order(ValidatedCommandPlan plan, ValidatedPlanNode node, string recipient)
        {
            if (!Enum.GetNames(typeof(OrderType)).Contains(node.Order!.Name)) throw new ArgumentException("Unknown native order.");
            var type = (OrderType)Enum.Parse(typeof(OrderType), node.Order.Name);
            var completion = type == OrderType.HoldArea || type == OrderType.Follow || type == OrderType.CoverAlly ?
                CompletionMode.Persistent : CompletionMode.Once;
            return new OrderRequest(Id(plan, node, recipient), recipient, type,
                node.Target?.Position, node.Target?.Kind == HandleKind.Actor ? node.Target.Id : null,
                new OrderConstraints(completion: completion), deliveryTimeout: node.Timeout);
        }
        private static PolicyRequest Policy(ValidatedCommandPlan plan, ValidatedPlanNode node, string recipient)
        {
            if (node.Policy != CommandPolicy.Engagement || node.Value != PolicyValue.HoldFire)
                throw new ArgumentException("Native runtime does not implement this policy.");
            return new PolicyRequest(Id(plan, node, recipient), recipient,
                new OrderConstraints(holdFire: true));
        }
        private void MainThread()
        {
            if (!scheduler.IsMainThread) throw new InvalidOperationException("Use the main-thread pump.");
        }
    }
}
