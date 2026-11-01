using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Language;
using NUnit.Framework;

namespace GoalDrivenAI.Tests
{
    public sealed class LoopPlanTests
    {
        private sealed class Clock : IClock { public double Now { get; set; } }
        private sealed class Contexts : ICommandContextSource
        {
            public bool Current = true;
            public CommandContext Capture(string issuer, IReadOnlyList<string> recipients) => Context();
            public bool IsCurrent(CommandContext context) => Current;
        }
        private sealed class Validator : ICommandValidator
        {
            public bool ValidateAuthority(string issuer, OrderRequest request, out string reason) { reason = ""; return true; }
            public bool ValidateCapability(string recipient, OrderType type, out string reason) { reason = ""; return true; }
            public bool ValidateTarget(OrderRequest request, out string reason) { reason = ""; return true; }
            public bool ValidateRegion(OrderRequest request, out string reason) { reason = ""; return true; }
            public bool IsRegionWithin(string child, string parent) => child == parent;
            public bool VerifyCompletion(OrderRequest request, string evidence, out string reason) { reason = ""; return true; }
        }
        private static CommandContext Context() => new CommandContext("leader", "world", 1,
            new[] { "unit" }, new[] {
                new CatalogHandle("dock", HandleKind.Location, new Position(0, 0, 0), "dock"),
                new CatalogHandle("market", HandleKind.Location, new Position(10, 0, 0), "market") }, 0, requestId: "patrol-request");
        private static PlanCompiler Compiler(bool loops = true, bool repeatable = true) => new PlanCompiler(new CommandCatalog("v1", new[] {
            new OrderDefinition("Move", true, true, 20, PlanFailurePolicy.AbortPlan, repeatable)
        }, allowLooping: loops));
        private sealed class Fixture
        {
            public readonly Clock Clock = new Clock();
            public readonly Contexts Contexts = new Contexts();
            public readonly CommandService Commands;
            public readonly CommandServiceAdapter Adapter;
            public readonly BoundedPlanJournal Journal = new BoundedPlanJournal(32);
            public readonly ValidatedCommandPlan Plan;
            public Fixture(int iterations = 0, double dwell = 0)
            {
                Commands = new CommandService(Clock, new Validator());
                Commands.Hierarchy.SetRole("leader", AuthorityLevel.Player); Commands.Hierarchy.AddFriendly("unit");
                Plan = Compiler().CompilePatrol(Context(), new[] { "dock", "market" }, dwell, iterations: iterations).Plan!;
                Adapter = new CommandServiceAdapter(Commands, Contexts, Clock, new CapturedThreadScheduler(), Journal);
                Assert.That(Adapter.TryCommit(Plan, out var reason), Is.True, reason);
            }
            public OrderRecord Deliver()
            {
                Adapter.Pump();
                var order = Commands.Orders.Single(o => o.Status == OrderStatus.PendingDelivery);
                Assert.That(Commands.Deliver(order.Request.OrderId).Accepted, Is.True);
                Assert.That(Commands.Acknowledge(order.Request.OrderId).Accepted, Is.True);
                return Commands.GetOrder(order.Request.OrderId)!;
            }
            public void Complete(OrderRecord order) => Assert.That(Commands.ReportCompletion(order.Request.OrderId, "host arrived").Accepted, Is.True);
        }

        [Test]
        public void PatrolExpansionUsesOrderedNavigationAndCompletionWaitsAndRestoresLoopMetadata()
        {
            var compiler = Compiler();
            var result = compiler.CompilePatrol(Context(), new[] { "market", "dock" }, dwellSeconds: 2);
            Assert.That(result.Success, Is.True, result.Error);
            var plan = result.Plan!;
            Assert.That(plan.Iterations, Is.Zero);
            Assert.That(plan.Nodes.Count, Is.EqualTo(6));
            Assert.That(plan.Nodes.Where(n => n.Kind == PlanNodeKind.IssueOrder).Select(n => n.Target!.Id), Is.EqualTo(new[] { "market", "dock" }));
            Assert.That(plan.Nodes.Count(n => n.Predicate == ResultPredicate.Completed), Is.EqualTo(2));
            Assert.That(plan.Nodes.Count(n => n.Predicate == ResultPredicate.TimerElapsed), Is.EqualTo(2));
            Assert.That(compiler.Restore(plan.CanonicalJson, plan.Hash).Plan!.Hash, Is.EqualTo(plan.Hash));
            Assert.That(compiler.Restore(plan.CanonicalJson.Replace("\"iterations\":0", "\"iterations\":2"), plan.Hash).Success, Is.False);
        }
        [Test]
        public void CatalogMustAuthorizeLoopsAndOrderRepetitionAndLocations()
        {
            Assert.That(Compiler(loops: false).CompilePatrol(Context(), new[] { "dock" }).Success, Is.False);
            Assert.That(Compiler(repeatable: false).CompilePatrol(Context(), new[] { "dock" }).Success, Is.False);
            Assert.That(Compiler().CompilePatrol(Context(), new[] { "hidden" }).Success, Is.False);
            Assert.That(Compiler().CompilePatrol(Context(), new string[0]).Success, Is.False);
            Assert.That(Compiler().CompilePatrol(Context(), Enumerable.Repeat("dock", 6), dwellSeconds: 1).Success, Is.False);
            Assert.That(Compiler().CompilePatrol(Context(), new[] { "dock" }, iterations: -1).Success, Is.False);
            var incomplete = "{\"schemaVersion\":1,\"catalogVersion\":\"v1\",\"iterations\":0,\"nodes\":[{\"id\":\"a\",\"type\":\"IssueOrder\",\"dependsOn\":[],\"recipients\":[\"unit\"],\"order\":\"Move\",\"target\":\"dock\"}]}";
            Assert.That(Compiler().Compile(incomplete, Context()).Success, Is.False);
            Assert.That(Compiler().Compile(incomplete.Replace("\"iterations\":0", "\"iterations\":true"), Context()).Success, Is.False);
        }
        [Test]
        public void RepetitionWaitsForRealCompletionUsesFreshIdsAndRejectsLateResultsAndReplays()
        {
            var f = new Fixture(); var first = f.Deliver();
            Assert.That(first.Request.TargetId, Is.EqualTo("dock"));
            for (int i = 0; i < 4; i++) f.Adapter.Pump();
            Assert.That(f.Commands.Orders.Count, Is.EqualTo(1), "Issuance is not navigation completion");
            f.Complete(first); var second = f.Deliver();
            Assert.That(second.Request.TargetId, Is.EqualTo("market")); f.Complete(second); f.Adapter.Pump();
            Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.CompletedIterations, Is.EqualTo(1));
            Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.Finished, Is.False);
            Assert.That(f.Commands.Orders, Is.Empty, "Completed iteration history should be retired");
            var next = f.Deliver(); Assert.That(next.Request.TargetId, Is.EqualTo("dock"));
            Assert.That(next.Request.OrderId, Is.Not.EqualTo(first.Request.OrderId));
            Assert.That(f.Commands.ReportCompletion(first.Request.OrderId, "late").Accepted, Is.False);
            Assert.That(f.Commands.Submit("leader", first.Request).Accepted, Is.False);
            var restored = new CommandService(f.Clock, new Validator()); restored.Restore(f.Commands.Save());
            Assert.That(restored.Submit("leader", first.Request).Accepted, Is.False, "Replay fence must survive save/restore");
        }
        [Test]
        public void FinitePlansStopAfterRequestedCyclesAndLongLoopsKeepBoundedState()
        {
            foreach (int iterations in new[] { 2, 0 })
            {
                var f = new Fixture(iterations); int cycles = iterations == 0 ? 40 : iterations;
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    f.Complete(f.Deliver()); f.Complete(f.Deliver()); f.Adapter.Pump();
                    Assert.That(f.Commands.Orders.Count, Is.Zero);
                    Assert.That(f.Commands.Feedback.Count, Is.Zero);
                    Assert.That(f.Commands.Save().RetiredIterations.Count, Is.EqualTo(1));
                }
                Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.CompletedIterations, Is.EqualTo(cycles));
                Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.Finished, Is.EqualTo(iterations != 0));
                Assert.That(f.Commands.Save().Batches.Count, Is.EqualTo(1));
                Assert.That(f.Journal.Outcomes.Count, Is.LessThanOrEqualTo(32));
            }
        }
        [Test]
        public void DwellRunsAfterArrivalAndCancelBetweenOrDuringIterationsStopsFutureDispatch()
        {
            var f = new Fixture(dwell: 2); var first = f.Deliver(); f.Complete(first); f.Adapter.Pump();
            Assert.That(f.Commands.Orders.Count(o => o.Status == OrderStatus.PendingDelivery), Is.Zero);
            f.Clock.Now = 1; f.Adapter.Pump();
            Assert.That(f.Commands.Orders.Count(o => o.Status == OrderStatus.PendingDelivery), Is.Zero);
            f.Clock.Now = 2; f.Adapter.Pump();
            var moving = f.Deliver();
            Assert.That(f.Adapter.Cancel(f.Plan.Hash), Is.True);
            for (int i = 0; i < 3; i++) f.Adapter.Pump();
            Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.Finished, Is.True);
            Assert.That(f.Commands.GetPrimary("unit"), Is.Null);
            Assert.That(f.Commands.ReportCompletion(moving.Request.OrderId, "late").Accepted, Is.False);
            var between = new Fixture(); between.Complete(between.Deliver()); between.Complete(between.Deliver()); between.Adapter.Pump();
            between.Adapter.Cancel(between.Plan.Hash); between.Adapter.Pump(); Assert.That(between.Commands.Orders, Is.Empty);
        }
        [Test]
        public void ReplacementBetweenCyclesStopsTheOldLoopWithoutOverridingTheReplacement()
        {
            var f = new Fixture(); f.Complete(f.Deliver()); f.Complete(f.Deliver()); f.Adapter.Pump();
            var old = f.Adapter.GetExecution(f.Plan.Hash)!;
            var context = new CommandContext("leader", "world", 1, new[] { "unit" }, Context().Handles.Values, 0, requestId: "replacement");
            var replacement = Compiler().CompilePatrol(context, new[] { "market" }, dwellSeconds: 0, iterations: 1).Plan!;
            Assert.That(f.Adapter.TryCommit(replacement, out _), Is.True);
            Assert.That(old.Finished, Is.True);
            f.Adapter.Pump();
            Assert.That(f.Commands.Orders.Single().Request.TargetId, Is.EqualTo("market"));
        }

        [Test]
        public void LowerAuthorityCannotReplaceLoopIntentBetweenItsIterations()
        {
            var f = new Fixture(); f.Complete(f.Deliver()); f.Complete(f.Deliver()); f.Adapter.Pump();
            f.Commands.Hierarchy.SetRole("unit", AuthorityLevel.Autonomous);
            var context = new CommandContext("unit", "world", 1, new[] { "unit" }, Context().Handles.Values, 0, requestId: "lower-priority");
            var replacement = Compiler().CompilePatrol(context, new[] { "market" }, dwellSeconds: 0, iterations: 1).Plan!;
            Assert.That(f.Adapter.TryCommit(replacement, out _), Is.False);
            Assert.That(f.Commands.Save().Batches.Count, Is.EqualTo(1));
            Assert.That(f.Deliver().Request.TargetId, Is.EqualTo("dock"));
        }
        [Test]
        public void NavigationTimeoutCancelsIssuedWorkAndStopsRepetition()
        {
            var f = new Fixture(); var active = f.Deliver(); f.Clock.Now = 120;
            f.Adapter.Pump(); f.Adapter.Pump();
            Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.Finished, Is.True);
            Assert.That(f.Commands.GetPrimary("unit"), Is.Null);
            Assert.That(f.Commands.ReportCompletion(active.Request.OrderId, "late").Accepted, Is.False);
        }

        [Test]
        public void FailedNavigationStaleContextAndRevokedAuthorityDoNotRestartTheLoop()
        {
            var failed = new Fixture(); var order = failed.Deliver();
            failed.Commands.ReportFailure(order.Request.OrderId, "unreachable"); failed.Adapter.Pump(); failed.Adapter.Pump();
            Assert.That(failed.Adapter.GetExecution(failed.Plan.Hash)!.Finished, Is.True);
            Assert.That(failed.Commands.GetPrimary("unit"), Is.Null);
            foreach (bool revoke in new[] { false, true })
            {
                var f = new Fixture(); f.Complete(f.Deliver()); f.Complete(f.Deliver()); f.Adapter.Pump();
                if (revoke) f.Commands.Hierarchy.SetRole("leader", AuthorityLevel.None); else f.Contexts.Current = false;
                f.Adapter.Pump(); f.Adapter.Pump();
                Assert.That(f.Adapter.GetExecution(f.Plan.Hash)!.Finished, Is.True);
                Assert.That(f.Commands.Orders, Is.Empty);
            }
        }
    }
}
