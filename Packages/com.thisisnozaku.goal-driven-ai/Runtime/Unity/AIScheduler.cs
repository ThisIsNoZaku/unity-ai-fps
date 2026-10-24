using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public interface IScheduledAgent
    {
        void Sense(double now);
        void Decide(double now);
        void PlanStep();
    }

    public sealed class AIScheduler : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float frameBudgetMilliseconds = 3;
        [SerializeField, Min(0.1f)] private float planningBudgetMilliseconds = 1;
        private sealed class Entry
        {
            public IScheduledAgent Agent;
            public double NextSense;
            public double NextDecision;
            public double SenseInterval;
            public double DecisionInterval;
        }
        private readonly List<Entry> agents = new List<Entry>();
        private readonly Stopwatch watch = new Stopwatch();
        private int cursor;
        public double LastWorkMilliseconds { get; private set; }
        public double LastPlanningMilliseconds { get; private set; }

        public void Register(IScheduledAgent agent, double senseInterval = 0.1, double decisionInterval = 0.2)
        {
            if (agents.Exists(entry => ReferenceEquals(entry.Agent, agent))) return;
            double phase = (agents.Count % 10) / 10.0;
            agents.Add(new Entry { Agent = agent, SenseInterval = System.Math.Max(0.01, senseInterval),
                DecisionInterval = System.Math.Max(0.01, decisionInterval),
                NextSense = Time.timeAsDouble + phase * senseInterval,
                NextDecision = Time.timeAsDouble + phase * decisionInterval });
        }

        public void Unregister(IScheduledAgent agent)
        {
            agents.RemoveAll(entry => ReferenceEquals(entry.Agent, agent));
            cursor = agents.Count == 0 ? 0 : cursor % agents.Count;
        }

        private void Update()
        {
            watch.Restart();
            LastPlanningMilliseconds = 0;
            int remaining = agents.Count;
            while (remaining-- > 0 && agents.Count > 0 && watch.Elapsed.TotalMilliseconds < frameBudgetMilliseconds)
            {
                if (cursor >= agents.Count) cursor = 0;
                Entry entry = agents[cursor++];
                double now = Time.timeAsDouble;
                if (now >= entry.NextSense) { entry.Agent.Sense(now); entry.NextSense = now + entry.SenseInterval; }
                if (now >= entry.NextDecision) { entry.Agent.Decide(now); entry.NextDecision = now + entry.DecisionInterval; }
                if (LastPlanningMilliseconds < planningBudgetMilliseconds
                    && watch.Elapsed.TotalMilliseconds < frameBudgetMilliseconds)
                {
                    double start = watch.Elapsed.TotalMilliseconds;
                    entry.Agent.PlanStep();
                    LastPlanningMilliseconds += watch.Elapsed.TotalMilliseconds - start;
                }
            }
            LastWorkMilliseconds = watch.Elapsed.TotalMilliseconds;
            watch.Stop();
        }
    }
}
