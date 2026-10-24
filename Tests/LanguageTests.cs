using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GoalDrivenAI.Language;
using GoalDrivenAI.Commands;
using NUnit.Framework;

namespace GoalDrivenAI.Tests
{
    public class LanguageTests
    {
        private sealed class Clock : IClock { public double Now { get; set; } }
        private static readonly string Issue = "{\"id\":\"a\",\"type\":\"IssueOrder\",\"dependsOn\":[],\"recipients\":[\"bot\"],\"order\":\"Move\",\"target\":\"marker\"}";
        private static string Proposal(params string[] nodes) =>
            "{\"schemaVersion\":1,\"catalogVersion\":\"v1\",\"nodes\":[" + string.Join(",", nodes) + "]}";
        private static CommandContext Context(long revision = 1) => new CommandContext("player", "session", revision,
            new[] { "bot" }, new[] { new CatalogHandle("marker", HandleKind.Location, new Position(1, 2, 3), "region") }, 0, markerHandle: "marker", requestId: "request");
        private static PlanCompiler Compiler() => new PlanCompiler(new CommandCatalog("v1", new[] {
            new OrderDefinition("Move", true, true, 20, PlanFailurePolicy.BlockDependents),
            new OrderDefinition("Hold", false, true, 20, PlanFailurePolicy.BlockDependents)
        }));
        private sealed class Contexts : ICommandContextSource
        {
            public long Revision = 1;
            public CommandContext Capture(string issuer, IReadOnlyList<string> recipients) => Context(Revision);
            public bool IsCurrent(CommandContext snapshot) => snapshot.Revision == Revision;
        }
        private sealed class Sink : ICommandPlanSink
        {
            public int Commits;
            public bool TryCommit(ValidatedCommandPlan plan, out string reason) { Commits++; reason = ""; return true; }
        }
        private sealed class DeferredModel : ILanguageCommandModel
        {
            public readonly List<TaskCompletionSource<ModelResult>> Requests = new List<TaskCompletionSource<ModelResult>>();
            public ModelCapabilities GetCapabilities() => new ModelCapabilities(true, false);
            public Task<ModelResult> InterpretAsync(LanguageModelRequest request, CancellationToken cancellationToken)
            {
                // Deliberately ignores cancellation to exercise late-response guards.
                var result = new TaskCompletionSource<ModelResult>();
                Requests.Add(result); return result.Task;
            }
        }
        private sealed class Runtime : IPlanNodeRuntime
        {
            public bool FailIssue;
            public bool Authorized = true;
            public readonly List<string> Started = new List<string>();
            public bool Revalidate(ValidatedCommandPlan plan, ValidatedPlanNode node, out string reason)
            { reason = "No authority."; return Authorized; }
            public NodeExecutionResult Start(ValidatedCommandPlan plan, ValidatedPlanNode node)
            {
                Started.Add(node.Id);
                return new NodeExecutionResult(node.Kind == PlanNodeKind.WaitForResult ? PlanNodeStatus.Running :
                    FailIssue && node.Kind == PlanNodeKind.IssueOrder ? PlanNodeStatus.Failed : PlanNodeStatus.Succeeded);
            }
            public NodeExecutionResult Poll(ValidatedCommandPlan plan, ValidatedPlanNode node) => new NodeExecutionResult(PlanNodeStatus.Running);
            public void Cancel(ValidatedCommandPlan plan, ValidatedPlanNode node) { }
        }
        private sealed class Validator : ICommandValidator
        {
            public bool ValidateAuthority(string issuer, OrderRequest request, out string reason) { reason = ""; return true; }
            public bool ValidateCapability(string recipient, OrderType type, out string reason) { reason = ""; return true; }
            public bool ValidateTarget(OrderRequest request, out string reason) { reason = ""; return true; }
            public bool ValidateRegion(OrderRequest request, out string reason) { reason = ""; return true; }
            public bool IsRegionWithin(string childRegion, string parentRegion) => childRegion == parentRegion;
            public bool VerifyCompletion(OrderRequest request, string evidence, out string reason) { reason = ""; return true; }
        }
        [Test]
        public void NativeAdapterAcceptsAtomicallyThenRevalidatesAndDoesNotFakeDelivery()
        {
            foreach (bool revoke in new[] { false, true })
            {
                var clock = new Clock(); var journal = new BoundedPlanJournal(); var scheduler = new CapturedThreadScheduler();
                var commands = new CommandService(clock, new Validator());
                commands.Hierarchy.SetRole("player", AuthorityLevel.Player); commands.Hierarchy.AddFriendly("bot");
                var adapter = new CommandServiceAdapter(commands, new Contexts(), clock, scheduler, journal);
                var plan = Compiler().Compile(Proposal(Issue), Context()).Plan!;
                Assert.That(adapter.TryCommit(plan, out _), Is.True);
                Assert.That(commands.Orders.Count, Is.Zero);
                if (revoke) commands.Hierarchy.SetRole("player", AuthorityLevel.None);
                adapter.Pump(); adapter.Pump();
                Assert.That(commands.Orders.Count, Is.EqualTo(revoke ? 0 : 1));
                if (!revoke) Assert.That(commands.Orders[0].Status, Is.EqualTo(OrderStatus.PendingDelivery));
                Assert.That(adapter.TryCommit(plan, out _), Is.True);
                adapter.Pump();
                Assert.That(commands.Orders.Count, Is.EqualTo(revoke ? 0 : 1));
            }
        }
        [Test]
        public void PersistentNativeHoldEstablishesMilestoneWithoutCompletingOrder()
        {
            var clock = new Clock(); var journal = new BoundedPlanJournal(); var scheduler = new CapturedThreadScheduler();
            var commands = new CommandService(clock, new Validator());
            commands.Hierarchy.SetRole("player", AuthorityLevel.Player); commands.Hierarchy.AddFriendly("bot");
            var compiler = new CommandPlanCompiler(new CommandCatalog("v1", new[] {
                new OrderDefinition("HoldArea", true, true, 20, PlanFailurePolicy.BlockDependents) }));
            var plan = compiler.Compile(Proposal(Issue.Replace("\"Move\"", "\"HoldArea\""), Wait("HoldPositionEstablished")), Context()).Plan!;
            var adapter = new CommandServiceAdapter(commands, new Contexts(), clock, scheduler, journal);
            Assert.That(adapter.TryCommit(plan, out _), Is.True); adapter.Pump();
            var id = commands.Orders.Single().Request.OrderId;
            commands.Deliver(id); commands.Acknowledge(id); adapter.Pump();
            Assert.That(journal.Outcomes.Last().Status, Is.EqualTo(PlanNodeStatus.Running));
            commands.ReportCompletion(id, "verified hold location"); adapter.Pump();
            Assert.That(commands.GetOrder(id)!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(journal.Outcomes.Last().Status, Is.EqualTo(PlanNodeStatus.Succeeded));
        }
        [Test]
        public void NativeHoldFireAndMovePreserveCapturedRegionAndEffectiveRestrictions()
        {
            var clock = new Clock(); var journal = new BoundedPlanJournal(); var scheduler = new CapturedThreadScheduler();
            var commands = new CommandService(clock, new Validator());
            commands.Hierarchy.SetRole("player", AuthorityLevel.Player); commands.Hierarchy.AddFriendly("bot");
            commands.SetPolicy("player", new PolicyRequest("existing", "bot", new OrderConstraints(regionId: "region", maxRisk: .3)));
            var policy = "{\"id\":\"policy\",\"type\":\"SetPolicy\",\"dependsOn\":[],\"recipients\":[\"bot\"],\"policy\":\"Engagement\",\"value\":\"HoldFire\"}";
            var move = Issue.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"policy\"]");
            var plan = Compiler().Compile(Proposal(policy, move), Context()).Plan!;
            var adapter = new CommandServiceAdapter(commands, new Contexts(), clock, scheduler, journal);
            Assert.That(adapter.TryCommit(plan, out var reason), Is.True, reason); adapter.Pump();
            var order = commands.Orders.Single().Request;
            Assert.That(order.Constraints.HoldFire, Is.True);
            Assert.That(order.Constraints.RegionId, Is.EqualTo("region"));
            Assert.That(order.Constraints.MaxRisk, Is.EqualTo(.3));
            Assert.That(order.Position!.Value, Is.EqualTo(new Position(1, 2, 3)));
        }
        [TestCase("```json\n{}\n```")]
        [TestCase("{\"schemaVersion\":1,\"schemaVersion\":1,\"catalogVersion\":\"v1\",\"nodes\":[]}")]
        [TestCase("{'schemaVersion':1,'catalogVersion':'v1','nodes':[]}")]
        [TestCase("{schemaVersion:1,\"catalogVersion\":\"v1\",\"nodes\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"catalogVersion\":\"v1\",\"nodes\":[],}")]
        [TestCase("{\"schemaVersion\":1,\"catalogVersion\":\"v1\",\"nodes\":[]}//comment")]
        public void RejectsHostileOrNonJsonOutput(string input)
        { Assert.That(Compiler().Compile(input, Context()).Success, Is.False); }

        [Test]
        public void RejectsUnknownFieldsEnumsUnauthorizedRecipientsAndHandles()
        {
            foreach (var node in new[] {
                Issue.Replace("\"order\":\"Move\"", "\"order\":1"),
                Issue.Replace("\"Move\"", "\"ExecuteScript\""),
                Issue.Replace("\"bot\"", "\"stranger\""),
                Issue.Replace("\"target\":\"marker\"", "\"target\":\"../../secrets\""),
                Issue.Replace("\"id\":\"a\"", "\"script\":\"danger\",\"id\":\"a\""),
                Issue.Replace("\"type\":\"IssueOrder\"", "\"type\":0"),
                Issue.Replace("\"id\":\"a\"", "\"id\":\"a\",\"id\":\"b\""),
                Issue.Replace("\"id\":\"a\"", "\"timeout\":0,\"id\":\"a\""),
                Issue.Replace("\"id\":\"a\"", "\"failurePolicy\":\"AbortPlan\",\"id\":\"a\"")
            }) Assert.That(Compiler().Compile(Proposal(node), Context()).Success, Is.False, node);
            Assert.That(Compiler().Compile(new string('x', PlanCompiler.MaxOutputCharacters + 1), Context()).Success, Is.False);
        }
        [Test]
        public void RejectsDuplicateDanglingCyclicAndConflictingNodes()
        {
            Assert.That(Compiler().Compile(Proposal(Issue, Issue), Context()).Success, Is.False);
            Assert.That(Compiler().Compile(Proposal(Issue.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"missing\"]")), Context()).Success, Is.False);
            var second = Issue.Replace("\"id\":\"a\"", "\"id\":\"b\"");
            Assert.That(Compiler().Compile(Proposal(Issue, second), Context()).Success, Is.False);
            Assert.That(Compiler().Compile(Proposal(
                Issue.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"b\"]"),
                second.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"a\"]")), Context()).Success, Is.False);
            Assert.That(Compiler().Compile(Proposal(Issue,
                second.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"a\"]")), Context()).Success, Is.False);
            Assert.That(Compiler().Compile(Proposal(Issue, Wait("OrderCompleted"),
                second.Replace("\"dependsOn\":[]", "\"dependsOn\":[\"wait\"]")), Context()).Success, Is.True);
            Assert.That(Compiler().Compile(Proposal(Enumerable.Repeat(Issue, 17).ToArray()), Context()).Success, Is.False);
        }
        [Test]
        public void CanonicalHashIsStableButBoundToCapturedContext()
        {
            var compiler = Compiler();
            var first = compiler.Compile(Proposal(Issue), Context()).Plan!;
            var reordered = compiler.Compile(Proposal(Issue.Replace("\"id\":\"a\",\"type\":\"IssueOrder\"", "\"type\":\"IssueOrder\",\"id\":\"a\"")), Context()).Plan!;
            Assert.That(first.Hash, Is.EqualTo(reordered.Hash));
            Assert.That(first.Hash, Is.Not.EqualTo(compiler.Compile(Proposal(Issue), Context(2)).Plan!.Hash));
            Assert.That(first.Nodes[0].Target!.Position.X, Is.EqualTo(1));
            Assert.That(first.Nodes[0].Timeout, Is.EqualTo(20));
            Assert.That(compiler.Restore(first.CanonicalJson, first.Hash).Plan!.Hash, Is.EqualTo(first.Hash));
            Assert.That(compiler.Restore(first.CanonicalJson, "wrong").Success, Is.False);
        }
        [Test]
        public void PumpCommitsOnceAndReplacementIgnoresLateOutput()
        {
            var model = new DeferredModel(); var sink = new Sink();
            using var service = new CommandInterpretationService(new Clock(), new Contexts(), model, Compiler(), sink,
                new CapturedThreadScheduler(), new BoundedPlanJournal());
            service.Begin(new FinalTranscript("move"), "player", new[] { "bot" });
            service.Begin(new FinalTranscript("hold"), "player", new[] { "bot" });
            model.Requests[0].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            service.Pump();
            Assert.That(sink.Commits, Is.Zero);
            model.Requests[1].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            service.Pump(); service.Pump();
            Assert.That(sink.Commits, Is.EqualTo(1));
            Assert.That(service.Outcomes.Last().Status, Is.EqualTo(InterpretationStatus.Accepted));
        }
        [Test]
        public void StaleAndExpiredResponsesNeverCommit()
        {
            foreach (bool expired in new[] { false, true })
            {
                var clock = new Clock(); var contexts = new Contexts(); var sink = new Sink(); var model = new DeferredModel();
                using var service = new CommandInterpretationService(clock, contexts, model, Compiler(), sink,
                    new CapturedThreadScheduler(), new BoundedPlanJournal());
                service.Begin(new FinalTranscript("move"), "player", new[] { "bot" }, 1);
                if (expired) clock.Now = 1; else contexts.Revision++;
                model.Requests[0].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
                service.Pump();
                Assert.That(sink.Commits, Is.Zero);
                Assert.That(service.Outcomes.Last().Status, Is.EqualTo(expired ? InterpretationStatus.Expired : InterpretationStatus.Stale));
            }
        }
        [Test]
        public void RequestIdsPermitRepeatedUtterancesButNotDuplicateRequests()
        {
            var sink = new Sink();
            using var service = new CommandInterpretationService(new Clock(), new Contexts(), new FixedCommandModel(Proposal(Issue)),
                Compiler(), sink, new CapturedThreadScheduler(), new BoundedPlanJournal());
            foreach (var id in new[] { "first", "first", "second" })
            {
                service.Begin(new FinalTranscript("move"), "player", new[] { "bot" }, requestId: id);
                service.Pump();
            }
            Assert.That(sink.Commits, Is.EqualTo(2));
            Assert.That(service.Outcomes[0].Context!.RequestId, Is.EqualTo("first"));
            Assert.That(service.Outcomes[2].Context!.RequestId, Is.EqualTo("second"));
        }
        [TestCase(ModelStatus.NeedsClarification, InterpretationStatus.NeedsClarification)]
        [TestCase(ModelStatus.Unsupported, InterpretationStatus.Unsupported)]
        [TestCase(ModelStatus.ProviderError, InterpretationStatus.ProviderError)]
        public void TypedNonSuccessRetainsSnapshotAndOnlyAuthorizedCandidates(ModelStatus modelStatus, InterpretationStatus expected)
        {
            var sink = new Sink();
            var model = new FixedCommandModel(new ModelResult(modelStatus, candidateHandles: new[] { "marker", "unknown" }));
            using var service = new CommandInterpretationService(new Clock(), new Contexts(), model, Compiler(), sink,
                new CapturedThreadScheduler(), new BoundedPlanJournal());
            service.Begin(new FinalTranscript("go there"), "player", new[] { "bot" }, requestId: "clarify");
            service.Pump();
            var outcome = service.Outcomes.Single();
            Assert.That(outcome.Status, Is.EqualTo(expected));
            Assert.That(outcome.Context!.RequestId, Is.EqualTo("clarify"));
            Assert.That(outcome.CandidateHandles, Is.EqualTo(new[] { "marker" }));
            Assert.That(sink.Commits, Is.Zero);
        }
        [Test]
        public async Task ScheduledModelBoundsInflightAndQueueAndCancelsQueuedRequests()
        {
            var provider = new DeferredModel();
            var model = new ScheduledCommandModel(provider, concurrency: 2, maxQueued: 1);
            var request = new LanguageModelRequest("move", Context(), 10);
            var first = model.InterpretAsync(request, CancellationToken.None);
            var second = model.InterpretAsync(request, CancellationToken.None);
            using var queuedCancellation = new CancellationTokenSource();
            var queued = model.InterpretAsync(request, queuedCancellation.Token);
            Assert.That(provider.Requests.Count, Is.EqualTo(2));
            Assert.That((await model.InterpretAsync(request, CancellationToken.None)).Status, Is.EqualTo(ModelStatus.Unavailable));
            queuedCancellation.Cancel();
            Assert.That((await queued).Status, Is.EqualTo(ModelStatus.Cancelled));
            Assert.That(provider.Requests.Count, Is.EqualTo(2));
            provider.Requests[0].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            provider.Requests[1].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            Assert.That((await first).Status, Is.EqualTo(ModelStatus.Success));
            Assert.That((await second).Status, Is.EqualTo(ModelStatus.Success));
        }
        [Test]
        public async Task ScheduledModelKeepsPermitUntilCancellationIgnoringProviderReturns()
        {
            var provider = new DeferredModel();
            var model = new ScheduledCommandModel(provider, concurrency: 1, maxQueued: 1);
            var request = new LanguageModelRequest("move", Context(), 10);
            using var cancellation = new CancellationTokenSource();
            var first = model.InterpretAsync(request, cancellation.Token);
            cancellation.Cancel();
            Assert.That((await first).Status, Is.EqualTo(ModelStatus.Cancelled));
            using var queuedCancellation = new CancellationTokenSource();
            var queued = model.InterpretAsync(request, queuedCancellation.Token);
            Assert.That(provider.Requests.Count, Is.EqualTo(1));
            Assert.That((await model.InterpretAsync(request, CancellationToken.None)).Status, Is.EqualTo(ModelStatus.Unavailable));
            queuedCancellation.Cancel(); await queued;
            provider.Requests[0].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            var next = model.InterpretAsync(request, CancellationToken.None);
            Assert.That(provider.Requests.Count, Is.EqualTo(2));
            provider.Requests[1].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue)));
            Assert.That((await next).Status, Is.EqualTo(ModelStatus.Success));
        }
        [Test]
        public void RepairIsLimitedToOneAttemptAndOriginalDeadline()
        {
            var clock = new Clock(); var model = new DeferredModel(); var sink = new Sink();
            using var service = new CommandInterpretationService(clock, new Contexts(), model, Compiler(), sink,
                new CapturedThreadScheduler(), new BoundedPlanJournal());
            service.Begin(new FinalTranscript("move"), "player", new[] { "bot" }, 1);
            model.Requests[0].SetResult(new ModelResult(ModelStatus.Success, "{}")); service.Pump();
            Assert.That(model.Requests.Count, Is.EqualTo(2));
            model.Requests[1].SetResult(new ModelResult(ModelStatus.Success, "{}")); service.Pump();
            Assert.That(model.Requests.Count, Is.EqualTo(2));
            Assert.That(service.Outcomes.Last().Status, Is.EqualTo(InterpretationStatus.Rejected));
            service.Begin(new FinalTranscript("move"), "player", new[] { "bot" }, 1);
            model.Requests[2].SetResult(new ModelResult(ModelStatus.Success, "{}")); service.Pump();
            clock.Now = 1;
            model.Requests[3].SetResult(new ModelResult(ModelStatus.Success, Proposal(Issue))); service.Pump();
            Assert.That(sink.Commits, Is.Zero);
        }
        private static string Wait(string predicate = "Completed") =>
            "{\"id\":\"wait\",\"type\":\"WaitForResult\",\"dependsOn\":[\"a\"],\"observedNode\":\"a\",\"predicate\":\"" + predicate + "\",\"timeout\":5}";
        [Test]
        public void FailedDependencyBlocksDescendantsAndRevalidatesBeforeStart()
        {
            var after = Issue.Replace("\"id\":\"a\"", "\"id\":\"search\"").Replace("\"dependsOn\":[]", "\"dependsOn\":[\"wait\"]");
            var plan = Compiler().Compile(Proposal(Issue, Wait("OrderCompleted"), after), Context()).Plan!;
            foreach (bool authorized in new[] { true, false })
            {
                var runtime = new Runtime { FailIssue = true, Authorized = authorized };
                var executor = new DependencyPlanExecutor(plan, new Clock(), runtime, new CapturedThreadScheduler(), new BoundedPlanJournal());
                executor.Pump();
                Assert.That(executor.States["a"], Is.EqualTo(PlanNodeStatus.Failed));
                Assert.That(executor.States["wait"], Is.EqualTo(PlanNodeStatus.Blocked));
                Assert.That(executor.States["search"], Is.EqualTo(PlanNodeStatus.Blocked));
                Assert.That(runtime.Started.Contains("wait"), Is.False);
                Assert.That(runtime.Started.Count, Is.EqualTo(authorized ? 1 : 0));
            }
        }
        [Test]
        public void PersistentAcceptanceDoesNotCompleteMilestoneWaitAndJournalIsBounded()
        {
            var hold = Issue.Replace("\"Move\",\"target\":\"marker\"", "\"Hold\"");
            var plan = Compiler().Compile(Proposal(hold, Wait("TargetVisible")), Context()).Plan!;
            var runtime = new Runtime(); var clock = new Clock(); var journal = new BoundedPlanJournal(2);
            journal.RecordAccepted(plan);
            var executor = new DependencyPlanExecutor(plan, clock, runtime, new CapturedThreadScheduler(), journal);
            executor.Pump(); executor.Pump();
            Assert.That(executor.States["a"], Is.EqualTo(PlanNodeStatus.Succeeded));
            Assert.That(executor.States["wait"], Is.EqualTo(PlanNodeStatus.Running));
            clock.Now = 5; executor.Pump();
            Assert.That(executor.States["wait"], Is.EqualTo(PlanNodeStatus.Failed));
            Assert.That(journal.Outcomes.Count, Is.EqualTo(2));
            var sink = new Sink();
            Assert.That(journal.Replay(plan.Hash, sink, out _), Is.True);
            Assert.That(sink.Commits, Is.EqualTo(1));
        }
    }
}
