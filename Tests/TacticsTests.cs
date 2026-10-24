using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI;
using GoalDrivenAI.Tactics;
using NUnit.Framework;

namespace GoalDrivenAI.Tests
{
    [TestFixture]
    public sealed class TacticsTests
    {
        private sealed class Clock : IClock { public double Now { get; set; } }
        private static readonly Fact Done = new Fact("done", true);
        private static readonly Fact Ready = new Fact("ready", true);

        [Test]
        public void EvidencePreservesUnknownAndOriginalTimestampAndExpires()
        {
            var clock = new Clock { Now = 10 };
            var store = new BeliefStore(clock);
            Assert.That(store.Query("visible", "enemy").Value, Is.EqualTo(Truth.Unknown));
            var report = new Evidence("r", "visible", "enemy", EvidenceKind.Vision, Truth.False, null, 9, 3);
            Assert.That(store.Add(report), Is.True);
            Assert.That(store.Query("visible", "enemy").Value, Is.EqualTo(Truth.False));
            Assert.That(store.Add(new Evidence("r", "visible", "enemy", EvidenceKind.Vision, Truth.True, null, 10, 20)), Is.False);
            Assert.That(store.Snapshot().Single(), Is.SameAs(report));
            clock.Now = 12;
            Assert.That(store.Query("visible", "enemy").Value, Is.EqualTo(Truth.Unknown));
            Assert.That(store.Add(report), Is.False);
        }

        [Test]
        public void HearingCannotIdentifyAnEmitterAndMemoryIsCopiedAndSearchable()
        {
            var clock = new Clock();
            var store = new BeliefStore(clock);
            var position = new Position(1, 2, 3);
            store.Add(new Evidence("sound", "noise", "enemy", EvidenceKind.Hearing, Truth.True, position, 0, 10));
            Assert.That(store.Query("noise", "enemy").Value, Is.EqualTo(Truth.Unknown));
            Assert.That(store.Query("noise").Report!.SubjectId, Is.Null);
            Assert.That(store.LastKnownPosition("enemy").Report, Is.Null);
            store.Add(new Evidence("seen", "visible", "enemy", EvidenceKind.Vision, Truth.True, position, 0, 10));
            position = new Position(99, 99, 99);
            clock.Now = 5;
            var memory = store.LastKnownPosition("enemy");
            Assert.That(memory.Report!.Position!.Value.X, Is.EqualTo(1));
            Assert.That(memory.Uncertainty, Is.EqualTo(.5).Within(.001));
            Assert.That(memory.CanSearch, Is.True);
            Assert.That(store.CompleteSearch("seen"), Is.True);
            Assert.That(store.LastKnownPosition("enemy").CanSearch, Is.False);
            Assert.That(store.LastKnownPosition("enemy").Report, Is.Not.Null);
        }

        [Test]
        public void EvidenceCapacityIsBoundedAndOldRelaysCannotEvictNewerObservations()
        {
            var clock = new Clock { Now = 5 };
            var store = new BeliefStore(clock, 2);
            Assert.That(store.Add(new Evidence("a", "x", null, EvidenceKind.Vision, Truth.True, null, 2, 10)), Is.True);
            store.Add(new Evidence("b", "x", null, EvidenceKind.Vision, Truth.True, null, 3, 10));
            Assert.That(store.Add(new Evidence("old", "x", null, EvidenceKind.Communication, Truth.True, null, 1, 10)), Is.False);
            store.Add(new Evidence("c", "x", null, EvidenceKind.Vision, Truth.False, null, 4, 10));
            Assert.That(store.Count, Is.EqualTo(2));
            Assert.That(store.Snapshot().Select(r => r.ReportId), Is.EquivalentTo(new[] { "b", "c" }));
        }

        [Test]
        public void RelayedEvidencePreservesSourceAndObservationAgeForSpatialUncertainty()
        {
            var clock = new Clock { Now = 5 };
            var store = new BeliefStore(clock);
            var report = new Evidence("relay", "lastSeen", "enemy", EvidenceKind.Communication,
                Truth.True, new Position(1, 0, 2), 1, 10, .8,
                sourceId: "scout", receiptAt: 5, initialUncertaintyRadius: 2, uncertaintyGrowthRate: .5);
            Assert.That(store.Add(report), Is.True);
            var belief = store.LastKnownPosition("enemy");
            Assert.That(belief.Report!.SourceId, Is.EqualTo("scout"));
            Assert.That(belief.Report.ReceiptAt, Is.EqualTo(5));
            Assert.That(belief.UncertaintyRadius, Is.EqualTo(4));
            clock.Now = 7;
            Assert.That(store.LastKnownPosition("enemy").UncertaintyRadius, Is.EqualTo(5));
            Assert.That(store.Query("unseen").UncertaintyRadius, Is.EqualTo(double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Evidence("bad", "x", null,
                EvidenceKind.Hearing, Truth.True, null, 2, 10, receiptAt: 1));
        }

        [Test]
        public void GoalSelectionUsesStableIdsCommitmentHysteresisAndEmergencyBypass()
        {
            var clock = new Clock();
            var selector = new GoalSelector(clock);
            var context = new GoalContext(1);
            Assert.That(selector.Select(new[] { new GoalCandidate("b", .5), new GoalCandidate("a", .5) }, context).GoalId, Is.EqualTo("a"));
            clock.Now = .5;
            Assert.That(selector.Select(new[] { new GoalCandidate("a", .5), new GoalCandidate("b", 1) }, context).GoalId, Is.EqualTo("a"));
            clock.Now = 1;
            Assert.That(selector.Select(new[] { new GoalCandidate("a", .5), new GoalCandidate("b", .64) }, context).GoalId, Is.EqualTo("a"));
            Assert.That(selector.Select(new[] { new GoalCandidate("a", .5), new GoalCandidate("b", .66) }, context).GoalId, Is.EqualTo("b"));
            Assert.That(selector.Select(new[] { new GoalCandidate("b", 1), new GoalCandidate("escape", .1, emergency: true) }, context).GoalId, Is.EqualTo("escape"));
            Assert.That(new GoalCandidate("clamped", 5).Utility, Is.EqualTo(1));
        }

        [Test]
        public void GoalCommandAndEligibilityGatesCannotBeOverriddenAndInfeasibleCacheIsBrief()
        {
            var clock = new Clock();
            var selector = new GoalSelector(clock);
            var calls = 0;
            var feasible = false;
            var candidates = new[]
            {
                new GoalCandidate("command", 1, requiredCommandId: "move"),
                new GoalCandidate("disabled", 1, eligible: _ => false, emergency: true),
                new GoalCandidate("work", .8, feasible: _ => { ++calls; return feasible; }),
                new GoalCandidate("idle", .1)
            };
            Assert.That(selector.Select(candidates, new GoalContext(1)).GoalId, Is.EqualTo("idle"));
            feasible = true;
            Assert.That(selector.Select(candidates, new GoalContext(1)).GoalId, Is.EqualTo("idle"));
            Assert.That(calls, Is.EqualTo(1));
            clock.Now = 1;
            Assert.That(selector.Select(candidates, new GoalContext(1)).GoalId, Is.EqualTo("work"));
            clock.Now = 2;
            Assert.That(selector.Select(candidates, new GoalContext(2, "move")).GoalId, Is.EqualTo("command"));
            Assert.That(selector.Select(Array.Empty<GoalCandidate>(), new GoalContext(3)).Goal, Is.Null);
        }

        [Test]
        public void PlannerIsIncrementalChoosesLowestCostAndDoesNotExposePrefixes()
        {
            var planner = new IncrementalPlanner();
            var actions = new[]
            {
                new PlanningAction("expensive", Array.Empty<Fact>(), new[] { Done }, 5),
                new PlanningAction("prepare", Array.Empty<Fact>(), new[] { Ready }, 1),
                new PlanningAction("finish", new[] { Ready }, new[] { Done }, 1),
                new PlanningAction("loop", Array.Empty<Fact>(), Array.Empty<Fact>(), 0)
            };
            planner.Start(new PlanningSnapshot(1, 2, "goal", new WorldState()), new[] { Done }, actions);
            var pending = planner.Step(1);
            Assert.That(pending.Status, Is.EqualTo(PlanningStatus.InProgress));
            Assert.That(pending.Expanded, Is.EqualTo(1));
            Assert.That(pending.Plan, Is.Empty);
            var result = planner.Step(1);
            Assert.That(result.Status, Is.EqualTo(PlanningStatus.Succeeded));
            Assert.That(result.Plan.Select(a => a.Id), Is.EqualTo(new[] { "prepare", "finish" }));
            Assert.That(result.Cost, Is.EqualTo(2));
        }

        [Test]
        public void PlannerKeepsBindingsAndUnknownSeparateFromFalseAndFreezesInput()
        {
            var source = new[] { new Fact("loaded", true, "ally") };
            var state = new WorldState(source);
            source[0] = new Fact("loaded", true, "enemy");
            Assert.That(state.Get(new FactKey("loaded", "enemy")), Is.EqualTo(Truth.Unknown));
            Assert.That(state.Satisfies(new[] { new Fact("loaded", false, "enemy") }), Is.False);
            var planner = new IncrementalPlanner();
            planner.Start(new PlanningSnapshot(1, 1, "goal", state), new[] { Done }, new[]
            { new PlanningAction("fire", new[] { new Fact("loaded", true, "enemy") }, new[] { Done }) });
            Assert.That(planner.Step(128).Status, Is.EqualTo(PlanningStatus.NoPlan));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlanningAction("negative", Array.Empty<Fact>(), new[] { Done }, -1));
        }

        [Test]
        public void PlannerReturnsExplicitLimitsAndStaleResultsWithoutPartialPlans()
        {
            var actions = Enumerable.Range(0, 9).Select(i => new PlanningAction("step" + i,
                i == 0 ? Array.Empty<Fact>() : new[] { new Fact("step" + (i - 1), true) },
                new[] { new Fact("step" + i, true) })).ToArray();
            var snapshot = new PlanningSnapshot(4, 7, "advance", new WorldState());
            var planner = new IncrementalPlanner();
            planner.Start(snapshot, new[] { new Fact("step8", true) }, actions);
            var limited = planner.Step(128);
            Assert.That(limited.Status, Is.AnyOf(PlanningStatus.DepthLimit, PlanningStatus.ExpansionLimit));
            Assert.That(limited.Plan, Is.Empty);
            planner.Start(snapshot, new[] { Done }, Array.Empty<PlanningAction>());
            Assert.That(planner.Step(1, 4, 8, "advance").Status, Is.EqualTo(PlanningStatus.Stale));
            var tiny = new IncrementalPlanner(maxExpanded: 1);
            tiny.Start(snapshot, new[] { Done }, new[] { new PlanningAction("prepare", Array.Empty<Fact>(), new[] { Ready }) });
            Assert.That(tiny.Step(128).Status, Is.EqualTo(PlanningStatus.ExpansionLimit));
            Assert.That(tiny.Result!.Plan, Is.Empty);
            var shallow = new IncrementalPlanner(maxDepth: 1);
            shallow.Start(snapshot, new[] { Done }, new[]
            {
                new PlanningAction("prepare", Array.Empty<Fact>(), new[] { Ready }),
                new PlanningAction("finish", new[] { Ready }, new[] { Done })
            });
            Assert.That(shallow.Step(128).Status, Is.EqualTo(PlanningStatus.DepthLimit));
        }

        [Test]
        public void PlannerDoesNotPruneCostlierShallowPathsNeededForDepthBound()
        {
            var planner = new IncrementalPlanner(maxDepth: 2);
            planner.Start(new PlanningSnapshot(1, 1, "goal",
                new WorldState(new[] { new Fact("a", false), new Fact("ready", false) })), new[] { Done }, new[]
            {
                new PlanningAction("prepare", new[] { new Fact("a", false), new Fact("ready", false) }, new[] { new Fact("a", true) }, 0),
                new PlanningAction("via", new[] { new Fact("a", true) }, new[] { new Fact("a", false), Ready }, 0),
                new PlanningAction("direct", Array.Empty<Fact>(), new[] { new Fact("a", false), Ready }, 2),
                new PlanningAction("finish", new[] { Ready }, new[] { Done }, 1)
            });
            var result = planner.Step(128);
            Assert.That(result.Status, Is.EqualTo(PlanningStatus.Succeeded));
            Assert.That(result.Plan.Select(a => a.Id), Is.EqualTo(new[] { "direct", "finish" }));
        }

        [Test]
        public void LeaseClaimsAreAtomicExpireAndCannotBeReleasedByAnOldOwner()
        {
            var clock = new Clock();
            var store = new LeaseStore(clock);
            Assert.That(store.TryAcquire("a", new[] { "cover" }, 1), Is.True);
            Assert.That(store.TryAcquire("b", new[] { "weapon", "cover" }, 1), Is.False);
            Assert.That(store.TryAcquire("c", new[] { "weapon" }, 1), Is.True);
            clock.Now = 1;
            Assert.That(store.TryAcquire("b", new[] { "cover" }, 1), Is.True);
            store.ReleaseOwner("a");
            Assert.That(store.Owns("b", new[] { "cover" }), Is.True);
        }

        private sealed class TestAction : IRuntimeAction
        {
            public string Id => "test";
            public IReadOnlyCollection<string> Channels { get; set; } = new[] { "locomotion" };
            public IReadOnlyCollection<string> Resources { get; set; } = new[] { "cover" };
            public IReadOnlyList<Fact> ExpectedEffects { get; set; } = new[] { Done };
            public bool Allowed = true;
            public int Cleanups, Cancels, Begins;
            public string? ThrowAt;
            public Action? CleanupHook, TickHook;
            public ActionStatus Next = ActionStatus.Running;
            public bool CanStart(ActionContext context) { Throw("can"); return Allowed; }
            public void Begin(ActionContext context) { ++Begins; Throw("begin"); }
            public ActionTick Tick(ActionContext context) { TickHook?.Invoke(); Throw("tick"); return new ActionTick(Next); }
            public void RequestCancel(ActionContext context) { ++Cancels; Throw("cancel"); }
            public void Cleanup(ActionContext context) { ++Cleanups; CleanupHook?.Invoke(); Throw("cleanup"); }
            private void Throw(string stage) { if (ThrowAt == stage) throw new InvalidOperationException(stage); }
        }

        [Test]
        public void ActionsRequireVerifiedEffectsAndReleaseAllClaimsOnSuccessAndFailure()
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            var runner = new ActionRunner(clock, channels, leases);
            var action = new TestAction { Next = ActionStatus.Succeeded };
            runner.Start(action, new ActionContext(_ => false));
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Failed));
            Assert.That(runner.Result.VerifiedEffects, Is.Empty);
            Assert.That(action.Cleanups, Is.EqualTo(1));
            var next = new TestAction { Next = ActionStatus.Succeeded };
            Assert.That(runner.Start(next, new ActionContext(_ => true)).Status, Is.EqualTo(ActionStatus.Running));
            Assert.That(runner.Tick().VerifiedEffects.Single().Key, Is.EqualTo(Done.Key));
            Assert.That(next.Cleanups, Is.EqualTo(1));
            Assert.That(channels.TryAcquire("probe", next.Channels, 1), Is.True);
            Assert.That(leases.TryAcquire("probe", next.Resources, 1), Is.True);
        }

        [TestCase("can")]
        [TestCase("begin")]
        [TestCase("tick")]
        [TestCase("cancel")]
        [TestCase("cleanup")]
        public void ExceptionsAlwaysCleanUpAndReleaseOwnership(string stage)
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            var runner = new ActionRunner(clock, channels, leases);
            var action = new TestAction { ThrowAt = stage, Next = stage == "cleanup" ? ActionStatus.Succeeded : ActionStatus.Running };
            runner.Start(action, new ActionContext(_ => true));
            if (stage == "tick" || stage == "cleanup") runner.Tick();
            if (stage == "cancel") runner.RequestCancel();
            Assert.That(runner.Result.Status, Is.EqualTo(ActionStatus.Failed));
            Assert.That(action.Cleanups, Is.EqualTo(1));
            Assert.That(channels.TryAcquire("probe", action.Channels, 1), Is.True);
            Assert.That(leases.TryAcquire("probe", action.Resources, 1), Is.True);
        }

        [Test]
        public void ExclusiveChannelsRejectCompetitorsWithoutDisturbingIncumbent()
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            using var first = new ActionRunner(clock, channels, leases);
            using var second = new ActionRunner(clock, channels, leases);
            first.Start(new TestAction(), new ActionContext(_ => true));
            var contender = new TestAction();
            Assert.That(second.Start(contender, new ActionContext(_ => true)).Status, Is.EqualTo(ActionStatus.Rejected));
            Assert.That(contender.Begins, Is.Zero);
            Assert.That(contender.Cleanups, Is.EqualTo(1));
            Assert.That(first.Tick().Status, Is.EqualTo(ActionStatus.Running));
        }

        [Test]
        public void CleanupCannotReenterStartAndLeakOrReleaseTheWrongOwnership()
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            using var runner = new ActionRunner(clock, channels, leases);
            ActionExecutionResult? reentrant = null;
            var context = new ActionContext(_ => true);
            var action = new TestAction { Next = ActionStatus.Succeeded };
            action.CleanupHook = () => reentrant = runner.Start(new TestAction(), context);
            runner.Start(action, context);
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Succeeded));
            Assert.That(reentrant!.Status, Is.EqualTo(ActionStatus.Rejected));
            Assert.That(channels.TryAcquire("probe", action.Channels, 1), Is.True);
            Assert.That(leases.TryAcquire("probe", action.Resources, 1), Is.True);
        }

        [Test]
        public void SynchronousCancellationInsideTickIsTerminalAndCleansUpOnlyOnce()
        {
            var clock = new Clock();
            using var runner = new ActionRunner(clock, new ChannelArbiter(clock), new LeaseStore(clock));
            var action = new TestAction { TickHook = () => runner.RequestCancel() };
            runner.Start(action, new ActionContext(_ => true), maxCancellationDelay: 0);
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Cancelled));
            Assert.That(action.Cleanups, Is.EqualTo(1));
        }

        [Test]
        public void EffectVerificationExceptionsAreTerminalAndReleaseClaims()
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            using var runner = new ActionRunner(clock, channels, leases);
            var action = new TestAction { Next = ActionStatus.Succeeded };
            runner.Start(action, new ActionContext(_ => throw new InvalidOperationException("verification")));
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Failed));
            Assert.That(runner.Result.VerifiedEffects, Is.Empty);
            Assert.That(action.Cleanups, Is.EqualTo(1));
            Assert.That(channels.TryAcquire("probe", action.Channels, 1), Is.True);
            Assert.That(leases.TryAcquire("probe", action.Resources, 1), Is.True);
        }

        [Test]
        public void CancellationHasABoundedDelayAndDeadlinesAndExpiredLeasesAreTerminal()
        {
            var clock = new Clock();
            var channels = new ChannelArbiter(clock);
            var leases = new LeaseStore(clock);
            using var runner = new ActionRunner(clock, channels, leases);
            var action = new TestAction();
            runner.Start(action, new ActionContext(_ => true), maxCancellationDelay: .2);
            Assert.That(runner.RequestCancel().Status, Is.EqualTo(ActionStatus.Cancelling));
            runner.RequestCancel();
            Assert.That(action.Cancels, Is.EqualTo(1));
            clock.Now = .2;
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Cancelled));
            Assert.That(action.Cleanups, Is.EqualTo(1));
            runner.Start(new TestAction(), new ActionContext(_ => true), timeout: .1);
            clock.Now = .31;
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.TimedOut));
            runner.Start(new TestAction(), new ActionContext(_ => true));
            clock.Now = 1;
            Assert.That(runner.Tick().Status, Is.EqualTo(ActionStatus.Failed));
            Assert.That(runner.Result.Reason, Does.Contain("Ownership"));
        }
    }
}
