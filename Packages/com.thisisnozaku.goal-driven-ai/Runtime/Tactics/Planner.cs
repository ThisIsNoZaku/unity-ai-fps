using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Tactics
{
    public readonly struct FactKey : IEquatable<FactKey>
    {
        public string Name { get; }
        public string Binding { get; }
        public FactKey(string name, string binding = "")
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Fact name is required.", nameof(name));
            Name = name;
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        }
        public bool Equals(FactKey other) => Name == other.Name && Binding == other.Binding;
        public override bool Equals(object? obj) => obj is FactKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Name, Binding);
    }

    public readonly struct Fact
    {
        public FactKey Key { get; }
        public bool Value { get; }
        public Fact(FactKey key, bool value)
        {
            if (string.IsNullOrWhiteSpace(key.Name)) throw new ArgumentException("Fact key is required.", nameof(key));
            Key = key;
            Value = value;
        }
        public Fact(string name, bool value, string binding = "") : this(new FactKey(name, binding), value) { }
    }

    public sealed class WorldState : IEquatable<WorldState>
    {
        private readonly Dictionary<FactKey, bool> facts;
        private readonly int hash;
        public WorldState(IEnumerable<Fact>? facts = null)
        {
            this.facts = new Dictionary<FactKey, bool>();
            foreach (var fact in facts ?? Array.Empty<Fact>())
            {
                if (string.IsNullOrWhiteSpace(fact.Key.Name) || !this.facts.TryAdd(fact.Key, fact.Value))
                    throw new ArgumentException("Facts must have valid unique keys.", nameof(facts));
            }
            // Order-independent hash; equality still compares every bound fact.
            foreach (var pair in this.facts) hash ^= HashCode.Combine(pair.Key, pair.Value);
        }
        public Truth Get(FactKey key) => facts.TryGetValue(key, out var value) ? (value ? Truth.True : Truth.False) : Truth.Unknown;
        public bool Satisfies(IEnumerable<Fact> conditions) =>
            conditions.All(f => facts.TryGetValue(f.Key, out var value) && value == f.Value);
        public IReadOnlyList<Fact> Facts => Array.AsReadOnly(facts.Select(f => new Fact(f.Key, f.Value)).ToArray());
        public WorldState Apply(IEnumerable<Fact> effects)
        {
            var updated = new Dictionary<FactKey, bool>(facts);
            foreach (var effect in effects) updated[effect.Key] = effect.Value;
            return new WorldState(updated.Select(f => new Fact(f.Key, f.Value)));
        }
        public bool Equals(WorldState? other) => other != null && hash == other.hash && facts.Count == other.facts.Count &&
            facts.All(p => other.facts.TryGetValue(p.Key, out var value) && value == p.Value);
        public override bool Equals(object? obj) => obj is WorldState state && Equals(state);
        public override int GetHashCode() => hash;
    }

    public sealed class PlanningAction
    {
        public string Id { get; }
        public double Cost { get; }
        public IReadOnlyList<Fact> Preconditions { get; }
        public IReadOnlyList<Fact> Effects { get; }
        public PlanningAction(string id, IEnumerable<Fact> preconditions, IEnumerable<Fact> effects, double cost = 1)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Stable action ID is required.", nameof(id));
            if (!Evidence.Finite(cost) || cost < 0) throw new ArgumentOutOfRangeException(nameof(cost));
            Id = id;
            Cost = cost;
            Preconditions = CopyFacts(preconditions);
            Effects = CopyFacts(effects);
        }
        internal static IReadOnlyList<Fact> CopyFacts(IEnumerable<Fact> facts)
        {
            if (facts == null) throw new ArgumentNullException(nameof(facts));
            var copy = facts.ToArray();
            _ = new WorldState(copy);
            return Array.AsReadOnly(copy);
        }
    }

    public sealed class PlanningSnapshot
    {
        public long Generation { get; }
        public long Revision { get; }
        public string GoalId { get; }
        public WorldState State { get; }
        public PlanningSnapshot(long generation, long revision, string goalId, WorldState state)
        {
            if (string.IsNullOrWhiteSpace(goalId)) throw new ArgumentException("Goal ID is required.", nameof(goalId));
            Generation = generation; Revision = revision; GoalId = goalId;
            State = state ?? throw new ArgumentNullException(nameof(state));
        }
    }

    public enum PlanningStatus { InProgress, Succeeded, NoPlan, DepthLimit, ExpansionLimit, Stale }
    public sealed class PlanningResult
    {
        public PlanningStatus Status { get; }
        public PlanningSnapshot Snapshot { get; }
        public IReadOnlyList<PlanningAction> Plan { get; }
        public int Expanded { get; }
        public double Cost { get; }
        internal PlanningResult(PlanningStatus status, PlanningSnapshot snapshot, PlanningAction[] plan, int expanded, double cost)
        {
            Status = status; Snapshot = snapshot; Plan = Array.AsReadOnly(plan); Expanded = expanded; Cost = cost;
        }
        public bool IsCurrent(long generation, long revision, string goalId) =>
            Snapshot.Generation == generation && Snapshot.Revision == revision && Snapshot.GoalId == goalId;
    }

    public interface IIncrementalPlanner
    {
        PlanningResult? Result { get; }
        void Start(PlanningSnapshot snapshot, IEnumerable<Fact> goal, IEnumerable<PlanningAction> actions);
        PlanningResult Step(int expansionBudget);
        PlanningResult Step(int expansionBudget, long generation, long revision, string goalId);
    }

    /// <summary>Incremental uniform-cost forward search (zero heuristic), with Pareto state/depth pruning.</summary>
    public sealed class IncrementalPlanner : IIncrementalPlanner
    {
        private sealed class Node
        {
            internal readonly WorldState State;
            internal readonly Node? Parent;
            internal readonly PlanningAction? Action;
            internal readonly double Cost;
            internal readonly int Depth;
            internal Node(WorldState state, Node? parent = null, PlanningAction? action = null)
            { State = state; Parent = parent; Action = action; Cost = (parent?.Cost ?? 0) + (action?.Cost ?? 0); Depth = parent == null ? 0 : parent.Depth + 1; }
        }
        private readonly int maxDepth, maxExpanded;
        private readonly List<Node> frontier = new List<Node>();
        private readonly Dictionary<WorldState, List<Node>> visited = new Dictionary<WorldState, List<Node>>();
        private PlanningSnapshot? snapshot;
        private IReadOnlyList<Fact> goal = Array.Empty<Fact>();
        private PlanningAction[] actions = Array.Empty<PlanningAction>();
        private int expanded;
        private bool hitDepth;
        public PlanningResult? Result { get; private set; }
        public IncrementalPlanner(int maxDepth = 8, int maxExpanded = 128)
        {
            if (maxDepth < 0 || maxDepth > 8 || maxExpanded < 1 || maxExpanded > 128)
                throw new ArgumentOutOfRangeException(nameof(maxDepth));
            this.maxDepth = maxDepth; this.maxExpanded = maxExpanded;
        }
        public void Start(PlanningSnapshot snapshot, IEnumerable<Fact> goal, IEnumerable<PlanningAction> actions)
        {
            var copiedGoal = PlanningAction.CopyFacts(goal);
            var copiedActions = (actions ?? throw new ArgumentNullException(nameof(actions))).ToArray();
            if (copiedActions.Any(a => a == null) || copiedActions.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != copiedActions.Length)
                throw new ArgumentException("Action IDs must be unique.", nameof(actions));
            this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            this.goal = copiedGoal;
            this.actions = copiedActions.OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
            frontier.Clear(); visited.Clear(); expanded = 0; hitDepth = false;
            var root = new Node(snapshot.State);
            frontier.Add(root); visited.Add(root.State, new List<Node> { root });
            SetResult(PlanningStatus.InProgress);
        }
        public PlanningResult Step(int expansionBudget)
        {
            if (snapshot == null) throw new InvalidOperationException("Start a search first.");
            if (expansionBudget < 1) throw new ArgumentOutOfRangeException(nameof(expansionBudget));
            if (Result!.Status != PlanningStatus.InProgress) return Result;
            var spent = 0;
            while (frontier.Count > 0)
            {
                var index = 0;
                for (var i = 1; i < frontier.Count; ++i)
                    if (frontier[i].Cost < frontier[index].Cost ||
                        (frontier[i].Cost == frontier[index].Cost && frontier[i].Depth < frontier[index].Depth)) index = i;
                var node = frontier[index];
                if (!visited[node.State].Contains(node)) { frontier.RemoveAt(index); continue; }
                if (node.State.Satisfies(goal)) return SetResult(PlanningStatus.Succeeded, node);
                if (expanded >= maxExpanded) return SetResult(PlanningStatus.ExpansionLimit);
                if (spent >= expansionBudget) return SetResult(PlanningStatus.InProgress);
                frontier.RemoveAt(index);
                ++spent; ++expanded;
                if (node.Depth == maxDepth) { hitDepth = true; continue; }
                foreach (var action in actions)
                {
                    if (!node.State.Satisfies(action.Preconditions)) continue;
                    var child = new Node(node.State.Apply(action.Effects), node, action);
                    if (!Evidence.Finite(child.Cost)) continue;
                    if (!visited.TryGetValue(child.State, out var alternatives))
                    { alternatives = new List<Node>(); visited.Add(child.State, alternatives); }
                    if (alternatives.Any(n => n.Cost <= child.Cost && n.Depth <= child.Depth)) continue;
                    alternatives.RemoveAll(n => child.Cost <= n.Cost && child.Depth <= n.Depth);
                    alternatives.Add(child); frontier.Add(child);
                }
            }
            return SetResult(hitDepth ? PlanningStatus.DepthLimit : PlanningStatus.NoPlan);
        }
        public PlanningResult Step(int expansionBudget, long generation, long revision, string goalId)
        {
            if (Result == null) throw new InvalidOperationException("Start a search first.");
            if (!Result.IsCurrent(generation, revision, goalId)) return SetResult(PlanningStatus.Stale);
            return Step(expansionBudget);
        }
        private PlanningResult SetResult(PlanningStatus status, Node? node = null)
        {
            var plan = new List<PlanningAction>();
            for (var n = node; n?.Action != null; n = n.Parent) plan.Add(n.Action);
            plan.Reverse();
            return Result = new PlanningResult(status, snapshot!, plan.ToArray(), expanded, node?.Cost ?? 0);
        }
    }
}
