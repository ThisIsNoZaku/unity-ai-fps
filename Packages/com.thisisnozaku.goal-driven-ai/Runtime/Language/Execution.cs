#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GoalDrivenAI.Language
{
    public enum PlanNodeStatus { Pending, Running, Succeeded, Failed, Blocked, Cancelled }
    public readonly struct NodeExecutionResult
    {
        public PlanNodeStatus Status { get; }
        public string Detail { get; }
        public NodeExecutionResult(PlanNodeStatus status, string detail = "")
        {
            if (status != PlanNodeStatus.Running && status != PlanNodeStatus.Succeeded && status != PlanNodeStatus.Failed)
                throw new ArgumentException("Runtime result must be running, succeeded, or failed.");
            Status = status; Detail = detail.Length <= 256 ? detail : detail.Substring(0, 256);
        }
    }
    public interface IPlanNodeRuntime
    {
        bool Revalidate(ValidatedCommandPlan plan, ValidatedPlanNode node, out string reason);
        // An IssueOrder succeeds when issued. Only WaitForResult observes completion/milestones.
        // A persistent order must never satisfy Completed merely because it was accepted.
        NodeExecutionResult Start(ValidatedCommandPlan plan, ValidatedPlanNode node);
        NodeExecutionResult Poll(ValidatedCommandPlan plan, ValidatedPlanNode node);
        void Cancel(ValidatedCommandPlan plan, ValidatedPlanNode node);
    }
    public sealed class PlanTrace
    {
        public string PlanHash { get; }
        public string NodeId { get; }
        public PlanNodeStatus Status { get; }
        public double Time { get; }
        public string Detail { get; }
        public PlanTrace(string hash, string node, PlanNodeStatus status, double time, string detail)
        {
            PlanHash = hash; NodeId = node; Status = status; Time = time;
            Detail = detail.Length <= 256 ? detail : detail.Substring(0, 256);
        }
    }
    public interface IPlanJournal
    {
        void RecordAccepted(ValidatedCommandPlan plan);
        void RecordOutcome(PlanTrace trace);
    }
    public interface IPlanJournalStorage
    {
        // Implementations persist snapshots transactionally in host-owned save storage.
        void Save(IReadOnlyList<ValidatedCommandPlan> accepted, IReadOnlyList<PlanTrace> outcomes);
    }
    public sealed class BoundedPlanJournal : IPlanJournal
    {
        private readonly Queue<ValidatedCommandPlan> plans = new Queue<ValidatedCommandPlan>();
        private readonly Queue<PlanTrace> traces = new Queue<PlanTrace>();
        private readonly int capacity;
        private readonly IPlanJournalStorage? storage;
        public IReadOnlyList<ValidatedCommandPlan> Accepted => Array.AsReadOnly(plans.ToArray());
        public IReadOnlyList<PlanTrace> Outcomes => Array.AsReadOnly(traces.ToArray());
        public string? PersistenceError { get; private set; }
        public BoundedPlanJournal(int capacity = 128, IPlanJournalStorage? storage = null)
        {
            if (capacity < 1 || capacity > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity; this.storage = storage;
        }
        public void Restore(IEnumerable<ValidatedCommandPlan> accepted, IEnumerable<PlanTrace> outcomes)
        {
            plans.Clear(); traces.Clear();
            foreach (var plan in accepted)
            {
                if (plans.Any(p => p.Hash == plan.Hash)) continue;
                while (plans.Count >= capacity) plans.Dequeue();
                plans.Enqueue(plan);
            }
            foreach (var trace in outcomes)
            {
                while (traces.Count >= capacity) traces.Dequeue();
                traces.Enqueue(trace);
            }
        }
        public void RecordAccepted(ValidatedCommandPlan plan)
        {
            if (plans.Any(p => p.Hash == plan.Hash)) return;
            while (plans.Count >= capacity) plans.Dequeue();
            plans.Enqueue(plan); Save();
        }
        public void RecordOutcome(PlanTrace trace)
        {
            while (traces.Count >= capacity) traces.Dequeue();
            traces.Enqueue(trace); Save();
        }
        private void Save()
        {
            try { storage?.Save(Accepted, Outcomes); PersistenceError = null; }
            catch (Exception) { PersistenceError = "Journal persistence failed."; }
        }
        // Replay deliberately never calls inference or accepts an unvalidated proposal.
        public bool Replay(string hash, ICommandPlanSink sink, out string reason)
        {
            var plan = plans.FirstOrDefault(p => p.Hash == hash);
            if (plan == null) { reason = "Accepted plan not retained."; return false; }
            return sink.TryCommit(plan, out reason);
        }
    }
    public sealed class DependencyPlanExecutor
    {
        private readonly ValidatedCommandPlan plan;
        private readonly IClock clock;
        private readonly IPlanNodeRuntime runtime;
        private readonly IMainThreadScheduler scheduler;
        private readonly IPlanJournal journal;
        private readonly Dictionary<string, PlanNodeStatus> states;
        private readonly Dictionary<string, double> deadlines = new Dictionary<string, double>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, PlanNodeStatus> States => new ReadOnlyDictionary<string, PlanNodeStatus>(states);
        public bool Finished => states.Values.All(s => s != PlanNodeStatus.Pending && s != PlanNodeStatus.Running);
        public DependencyPlanExecutor(ValidatedCommandPlan plan, IClock clock, IPlanNodeRuntime runtime,
            IMainThreadScheduler scheduler, IPlanJournal journal)
        {
            this.plan = plan; this.clock = clock; this.runtime = runtime; this.scheduler = scheduler; this.journal = journal;
            states = plan.Nodes.ToDictionary(n => n.Id, n => PlanNodeStatus.Pending, StringComparer.Ordinal);
        }
        public void Pump()
        {
            RequireMainThread();
            foreach (var node in plan.Nodes)
            {
                var status = states[node.Id];
                if (status != PlanNodeStatus.Pending && status != PlanNodeStatus.Running) continue;
                if (node.Dependencies.Any(d => states[d] == PlanNodeStatus.Failed || states[d] == PlanNodeStatus.Blocked || states[d] == PlanNodeStatus.Cancelled))
                { Change(node, PlanNodeStatus.Blocked, "Dependency did not succeed."); continue; }
                if (status == PlanNodeStatus.Pending && node.Dependencies.Any(d => states[d] != PlanNodeStatus.Succeeded)) continue;
                try
                {
                    NodeExecutionResult result;
                    if (status == PlanNodeStatus.Pending)
                    {
                        if (!runtime.Revalidate(plan, node, out var reason)) { Fail(node, reason); continue; }
                        deadlines[node.Id] = clock.Now + node.Timeout;
                        result = runtime.Start(plan, node);
                    }
                    else
                    {
                        if (clock.Now >= deadlines[node.Id])
                        {
                            runtime.Cancel(plan, node); Fail(node, "Node deadline."); continue;
                        }
                        result = runtime.Poll(plan, node);
                    }
                    if (result.Status == PlanNodeStatus.Failed) Fail(node, result.Detail);
                    else if (states[node.Id] != result.Status) Change(node, result.Status, result.Detail);
                }
                catch (Exception) { Fail(node, "Node runtime failed."); }
            }
        }
        private void Fail(ValidatedPlanNode node, string detail)
        {
            Change(node, PlanNodeStatus.Failed, detail);
            if (node.FailurePolicy == PlanFailurePolicy.AbortPlan)
                foreach (var other in plan.Nodes.Where(n => states[n.Id] == PlanNodeStatus.Pending || states[n.Id] == PlanNodeStatus.Running))
                {
                    if (states[other.Id] == PlanNodeStatus.Running) TryCancel(other);
                    Change(other, PlanNodeStatus.Blocked, "Plan aborted.");
                }
        }
        public void Cancel()
        {
            RequireMainThread();
            foreach (var node in plan.Nodes.Where(n => states[n.Id] == PlanNodeStatus.Pending || states[n.Id] == PlanNodeStatus.Running))
            {
                if (states[node.Id] == PlanNodeStatus.Running) TryCancel(node);
                Change(node, PlanNodeStatus.Cancelled, "Cancelled.");
            }
        }
        private void TryCancel(ValidatedPlanNode node)
        { try { runtime.Cancel(plan, node); } catch (Exception) { /* Cancellation must still block descendants. */ } }
        private void Change(ValidatedPlanNode node, PlanNodeStatus status, string detail)
        {
            states[node.Id] = status;
            journal.RecordOutcome(new PlanTrace(plan.Hash, node.Id, status, clock.Now, detail));
        }
        private void RequireMainThread()
        {
            if (!scheduler.IsMainThread) throw new InvalidOperationException("Use the main-thread pump.");
        }
    }
}
