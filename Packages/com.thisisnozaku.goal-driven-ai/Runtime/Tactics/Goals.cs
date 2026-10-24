using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Tactics
{
    public sealed class GoalContext
    {
        public long Revision { get; }
        public string? CommandId { get; }
        public GoalContext(long revision, string? commandId = null) { Revision = revision; CommandId = commandId; }
    }

    public sealed class GoalCandidate
    {
        public string Id { get; }
        public double Utility { get; }
        public bool Emergency { get; }
        public string? RequiredCommandId { get; }
        public Func<GoalContext, bool> Eligible { get; }
        public Func<GoalContext, bool> Feasible { get; }

        public GoalCandidate(string id, double utility, Func<GoalContext, bool>? eligible = null,
            Func<GoalContext, bool>? feasible = null, bool emergency = false, string? requiredCommandId = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Stable goal ID is required.", nameof(id));
            if (!Evidence.Finite(utility)) throw new ArgumentOutOfRangeException(nameof(utility));
            Id = id;
            Utility = Math.Max(0, Math.Min(1, utility));
            Eligible = eligible ?? (_ => true);
            Feasible = feasible ?? (_ => true);
            Emergency = emergency;
            RequiredCommandId = requiredCommandId;
        }
    }

    public readonly struct GoalDecision
    {
        public GoalCandidate? Goal { get; }
        public string? GoalId => Goal?.Id;
        public bool Changed { get; }
        internal GoalDecision(GoalCandidate? goal, bool changed) { Goal = goal; Changed = changed; }
    }

    public interface IGoalSelector
    {
        GoalDecision Select(IEnumerable<GoalCandidate> candidates, GoalContext context);
        void MarkInfeasible(string goalId);
    }

    public sealed class GoalSelector : IGoalSelector
    {
        private readonly IClock clock;
        private readonly double commitment, hysteresis, infeasibleDuration;
        private readonly Dictionary<string, double> infeasible = new Dictionary<string, double>(StringComparer.Ordinal);
        private long revision;
        private string? command, current;
        private double selectedAt;
        public GoalSelector(IClock clock, double commitment = 1, double hysteresis = .15, double infeasibleDuration = .25)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (!Evidence.Finite(commitment) || commitment < 0 || !Evidence.Finite(hysteresis) ||
                hysteresis < 0 || hysteresis > 1 || !Evidence.Finite(infeasibleDuration) || infeasibleDuration < 0)
                throw new ArgumentOutOfRangeException(nameof(commitment));
            this.commitment = commitment;
            this.hysteresis = hysteresis;
            this.infeasibleDuration = infeasibleDuration;
        }

        public GoalDecision Select(IEnumerable<GoalCandidate> candidates, GoalContext context)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (context == null) throw new ArgumentNullException(nameof(context));
            var all = candidates.ToArray();
            if (all.Any(g => g == null) || all.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != all.Length)
                throw new ArgumentException("Goal IDs must be unique.", nameof(candidates));
            if (revision != context.Revision || command != context.CommandId)
            {
                infeasible.Clear();
                revision = context.Revision;
                command = context.CommandId;
            }
            foreach (var id in infeasible.Where(p => p.Value <= clock.Now || !all.Any(g => g.Id == p.Key)).Select(p => p.Key).ToArray())
                infeasible.Remove(id);
            var available = new List<GoalCandidate>();
            foreach (var goal in all)
            {
                if ((goal.RequiredCommandId != null && goal.RequiredCommandId != context.CommandId) || !Evaluate(goal.Eligible, context))
                    continue;
                if (infeasible.ContainsKey(goal.Id)) continue;
                if (!Evaluate(goal.Feasible, context)) { infeasible[goal.Id] = clock.Now + infeasibleDuration; continue; }
                available.Add(goal);
            }
            var best = available.OrderByDescending(g => g.Emergency).ThenByDescending(g => g.Utility)
                .ThenBy(g => g.Id, StringComparer.Ordinal).FirstOrDefault();
            var incumbent = available.FirstOrDefault(g => g.Id == current);
            if (incumbent != null && best != null && best.Id != incumbent.Id && !best.Emergency)
            {
                if (incumbent.Emergency || clock.Now - selectedAt < commitment ||
                    best.Utility < incumbent.Utility + hysteresis)
                    best = incumbent;
            }
            var changed = current != best?.Id;
            if (changed) { current = best?.Id; selectedAt = clock.Now; }
            return new GoalDecision(best, changed);
        }

        public void MarkInfeasible(string goalId)
        {
            if (string.IsNullOrWhiteSpace(goalId)) throw new ArgumentException("Goal ID is required.", nameof(goalId));
            infeasible[goalId] = clock.Now + infeasibleDuration;
        }

        private static bool Evaluate(Func<GoalContext, bool> evaluate, GoalContext context)
        {
            try { return evaluate(context); }
            catch (Exception) { return false; }
        }
    }
}
