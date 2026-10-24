using System;
using System.Linq;
using GoalDrivenAI;
using GoalDrivenAI.Commands;
using NUnit.Framework;

namespace GoalDrivenAI.Tests
{
    [TestFixture]
    public sealed class OrderTests
    {
        private sealed class Clock : IClock { public double Now { get; set; } }
        private sealed class Validator : ICommandValidator
        {
            public bool Capable = true;
            public bool TargetValid = true;
            public bool RegionValid = true;
            public bool Verified;
            public bool AuthorityValid = true;
            public bool ValidateAuthority(string issuer, OrderRequest request, out string reason)
            { reason = "Authority"; return AuthorityValid; }
            public bool ValidateCapability(string recipient, OrderType type, out string reason)
            { reason = "Capability"; return Capable; }
            public bool ValidateTarget(OrderRequest request, out string reason)
            { reason = "Target"; return TargetValid; }
            public bool ValidateRegion(OrderRequest request, out string reason)
            { reason = "Region"; return RegionValid; }
            public bool IsRegionWithin(string childRegion, string parentRegion) =>
                childRegion.StartsWith(parentRegion + "/", StringComparison.Ordinal);
            public bool VerifyCompletion(OrderRequest request, string evidence, out string reason)
            { reason = Verified ? "World state confirmed." : "Unverified prediction."; return Verified; }
        }

        private Clock clock = null!;
        private Validator validator = null!;
        private AuthorityGraph graph = null!;
        private CommandService service = null!;

        [SetUp]
        public void Setup()
        {
            clock = new Clock();
            validator = new Validator();
            graph = new AuthorityGraph();
            graph.SetRole("player", AuthorityLevel.Player);
            graph.SetRole("leader", AuthorityLevel.Leader);
            graph.SetRole("a", AuthorityLevel.Delegated);
            graph.SetRole("b", AuthorityLevel.Delegated);
            graph.SetRole("c", AuthorityLevel.Delegated);
            graph.SetRole("d", AuthorityLevel.Autonomous);
            graph.AddHierarchy("leader", "a");
            graph.AddHierarchy("a", "b");
            graph.AddHierarchy("b", "c");
            graph.AddHierarchy("c", "d");
            service = new CommandService(clock, validator, graph);
        }

        private OrderRequest Request(string id = "one", string recipient = "a",
            OrderConstraints? constraints = null, OrderDisposition disposition = OrderDisposition.Replace,
            string? parent = null, bool allowDelegation = false) => new OrderRequest(id, recipient, OrderType.Move,
                new Position(1, 2, 3), constraints: constraints, parentOrderId: parent, disposition: disposition,
                allowDelegation: allowDelegation);

        private void Activate(OrderRequest request, string caller = "player")
        {
            Assert.That(service.Submit(caller, request).Accepted, Is.True);
            Assert.That(service.Deliver(request.OrderId).Accepted, Is.True);
            Assert.That(service.Acknowledge(request.OrderId).Accepted, Is.True);
        }

        [Test]
        public void DeliveryAndAcknowledgementAreDistinctFromCompletion()
        {
            service.Submit("player", Request());
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.PendingDelivery));
            Assert.That(service.Acknowledge("one").Accepted, Is.False);
            Assert.That(service.GetPrimary("a"), Is.Null);
            service.Deliver("one");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Delivered));
            service.Acknowledge("one");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(service.Feedback.Select(x => x.Status), Does.Contain(OrderStatus.Acknowledged));
            Assert.That(service.ReportCompletion("one", "agent predicts success").Accepted, Is.False);
            validator.Verified = true;
            Assert.That(service.ReportCompletion("one", "host evidence").Accepted, Is.True);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Completed));
        }

        [Test]
        public void RetransmissionPreservesDeadlineAndOriginalIssueTimeEvenAfterRejection()
        {
            service.Submit("player", Request());
            clock.Now = 20;
            service.Submit("player", Request());
            Assert.That(service.GetOrder("one")!.IssuedAt, Is.Zero);
            Assert.That(service.GetOrder("one")!.DeliveryDeadline, Is.EqualTo(30));
            clock.Now = 30;
            service.Tick();
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.DeliveryExpired));
            service.Submit("player", Request());
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.DeliveryExpired));
            validator.Capable = false;
            service.Submit("player", Request("rejected"));
            validator.Capable = true;
            Assert.That(service.Submit("player", Request("rejected")).Accepted, Is.False);
            Assert.That(service.Submit("leader", Request()).Accepted, Is.False);
        }

        [Test]
        public void AuthorityUsesCallerIdentityScopeAndCycleFreeFriendlyGraph()
        {
            Assert.That(service.Submit("outsider", Request()).Accepted, Is.False);
            Assert.That(service.Submit("player", Request("enemy", "hostile")).Accepted, Is.False);
            Assert.That(service.Submit("d", Request("outside", "a")).Accepted, Is.False);
            Assert.That(graph.AddHierarchy("d", "leader"), Is.False);
            Assert.That(service.Submit("leader", Request("scoped", "d")).Accepted, Is.True);
            validator.AuthorityValid = false;
            Assert.That(service.Submit("player", Request("denied")).Accepted, Is.False);
        }

        [Test]
        public void LowerAuthorityCannotReplaceCancelOrRelaxPlayerConstraints()
        {
            Activate(Request(constraints: new OrderConstraints(true, "room", .2, 100)));
            Assert.That(service.Submit("leader", Request("replace")).Accepted, Is.False);
            Assert.That(service.Cancel("leader", "one").Accepted, Is.False);
            Assert.That(service.Submit("leader", Request("relax", disposition: OrderDisposition.Append)).Accepted, Is.False);
            Assert.That(service.Submit("leader", Request("narrow",
                constraints: new OrderConstraints(true, "room/corner", .1, 50),
                disposition: OrderDisposition.Append)).Accepted, Is.True);
        }

        [Test]
        public void SameAuthorityReplacementCancelsDescendantsButNotAtDispatch()
        {
            Activate(Request(allowDelegation: true));
            Activate(Request("child", "b", parent: "one"), "a");
            service.Submit("player", Request("replacement"));
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
            service.Deliver("replacement");
            service.Acknowledge("replacement");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Superseded));
            Assert.That(service.GetOrder("child")!.Status, Is.EqualTo(OrderStatus.Superseded));
            Assert.That(service.GetPrimary("a")!.Request.OrderId, Is.EqualTo("replacement"));
        }

        [Test]
        public void AppendQueueIsBoundedAndPromotesExactlyOnePrimary()
        {
            Activate(Request());
            for (int i = 0; i < CommandService.QueueCapacity; i++)
                Activate(Request("q" + i, disposition: OrderDisposition.Append));
            Assert.That(service.GetPrimary("a")!.Request.OrderId, Is.EqualTo("one"));
            Assert.That(service.Submit("player", Request("overflow", disposition: OrderDisposition.Append)).Accepted, Is.False);
            validator.Verified = true;
            service.ReportCompletion("one", "done");
            Assert.That(service.GetPrimary("a")!.Request.OrderId, Is.EqualTo("q0"));
            Assert.That(service.Orders.Count(x => x.Status == OrderStatus.Active), Is.EqualTo(1));
        }

        [Test]
        public void EmergencySuspendsAndResumesWithoutRestartingOrder()
        {
            Activate(Request());
            Activate(Request("next", disposition: OrderDisposition.Append));
            clock.Now = 10;
            service.SuspendForEmergency("a", "grenade");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Suspended));
            Assert.That(service.ReportCompletion("one", "prediction").Accepted, Is.False);
            clock.Now = 15;
            service.ResumeAfterEmergency("a");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(service.GetOrder("next")!.Status, Is.EqualTo(OrderStatus.Queued));
            Assert.That(service.GetOrder("one")!.IssuedAt, Is.Zero);
        }

        [Test]
        public void ExpiryDuringEmergencyCannotResumeAndPolicyExpiryIsIndependent()
        {
            service.SetPolicy("player", new PolicyRequest("policy", "a", new OrderConstraints(true, expiresAt: 50)));
            Activate(Request(constraints: new OrderConstraints(true, expiresAt: 10)));
            service.SuspendForEmergency("a", "emergency");
            clock.Now = 11;
            service.ResumeAfterEmergency("a");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Expired));
            Assert.That(service.GetEffectiveConstraints("a").HoldFire, Is.True);
            clock.Now = 50;
            Assert.That(service.GetEffectiveConstraints("a").HoldFire, Is.False);
        }

        [TestCase(CompletionMode.Persistent)]
        [TestCase(CompletionMode.UntilCancelled)]
        public void PersistentOrdersRecordMilestonesWithoutCompleting(CompletionMode mode)
        {
            Activate(Request(constraints: new OrderConstraints(completion: mode)));
            validator.Verified = true;
            service.ReportCompletion("one", "cleared");
            service.ReportCompletion("one", "cleared again");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(service.GetOrder("one")!.Milestones, Is.EqualTo(2));
            service.Cancel("player", "one");
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Cancelled));
        }

        [Test]
        public void PolicyCannotBeRelaxedByLowerAuthorityAndSurvivesReplacement()
        {
            var policy = new PolicyRequest("policy", "a", new OrderConstraints(true, "room", .3));
            Assert.That(service.SetPolicy("player", policy).Accepted, Is.True);
            Assert.That(service.RemovePolicy("leader", "policy", out _), Is.False);
            Assert.That(service.SetPolicy("leader", new PolicyRequest("weak", "a", new OrderConstraints())).Accepted, Is.False);
            Assert.That(service.Submit("player", Request()).Accepted, Is.False);
            Activate(Request("safe", constraints: new OrderConstraints(true, "room/corner", .2)));
            service.Cancel("player", "safe");
            Assert.That(service.GetEffectiveConstraints("a").HoldFire, Is.True);
            Assert.That(service.RemovePolicy("player", "policy", out _), Is.True);
        }

        [Test]
        public void DelegationNarrowsConstraintsAndIsBoundedByDepthAndRecipientAncestry()
        {
            var safe = new OrderConstraints(true, "room", .2, 100);
            Activate(Request(constraints: safe, allowDelegation: true));
            Assert.That(service.Submit("a", Request("unsafe", "b", parent: "one")).Accepted, Is.False);
            Assert.That(service.Submit("a", Request("cycle", "a", safe, parent: "one")).Accepted, Is.False);
            Activate(Request("child", "b", safe, parent: "one", allowDelegation: true), "a");
            Activate(Request("grandchild", "c", safe, parent: "child", allowDelegation: true), "b");
            Activate(Request("third", "d", safe, parent: "grandchild", allowDelegation: true), "c");
            graph.SetRole("d", AuthorityLevel.Delegated);
            graph.AddHierarchy("d", "e");
            Assert.That(service.Submit("d", Request("fourth", "e", safe, parent: "third")).Accepted, Is.False);
            Assert.That(service.Submit("player", Request("forged", "b", safe, parent: "one")).Accepted, Is.False);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
        }

        [Test]
        public void DelegationChildrenAreBoundedIncludingHistoricalNodes()
        {
            Activate(Request(allowDelegation: true));
            for (int i = 0; i < CommandService.MaxChildren; i++)
            {
                graph.AddHierarchy("a", "unit" + i);
                Assert.That(service.Submit("a", Request("child" + i, "unit" + i, parent: "one")).Accepted, Is.True);
            }
            graph.AddHierarchy("a", "extra");
            Assert.That(service.Submit("a", Request("extra", "extra", parent: "one")).Accepted, Is.False);
            service.Cancel("player", "one");
            Assert.That(service.Orders.Where(x => x.Request.ParentOrderId == "one" && x.Status != OrderStatus.Rejected)
                .All(x => x.Status == OrderStatus.Cancelled), Is.True);
        }

        [Test]
        public void BatchRejectsAtomicallyDeduplicatesAndRevalidatesExecution()
        {
            validator.TargetValid = false;
            var bad = service.CommitBatch("player", "batch", new[] { Request(), Request("two") });
            Assert.That(bad.Accepted, Is.False);
            Assert.That(service.Orders, Is.Empty);
            validator.TargetValid = true;
            Assert.That(service.CommitBatch("player", "batch", new[] { Request() }).Accepted, Is.False);
            Assert.That(service.ValidateBatch("player", "fresh", new[] { Request() }).Accepted, Is.True);
            Assert.That(service.Orders, Is.Empty);
            Assert.That(service.CommitBatch("player", "fresh", new[] { Request() }).Accepted, Is.True);
            Assert.That(service.Orders, Is.Empty);
            validator.Capable = false;
            Assert.That(service.Submit("player", Request()).Accepted, Is.False);
            Assert.That(service.CommitBatch("leader", "fresh", Array.Empty<OrderRequest>()).Accepted, Is.False);
        }

        [Test]
        public void BatchPolicyValidationIsAtomicAndChecksAllNodes()
        {
            var policy = new PolicyRequest("policy", "a", new OrderConstraints(true));
            Assert.That(service.CommitBatch("player", "bad", new[] { Request() }, new[] { policy }).Accepted, Is.False);
            Assert.That(service.GetEffectiveConstraints("a").HoldFire, Is.False);
            Assert.That(service.CommitBatch("player", "duplicate", new[] { Request(), Request() }).Accepted, Is.False);
        }

        [Test]
        public void SaveRestorePreservesCompletedPendingRejectedAndBatchIdsWithoutReplay()
        {
            Activate(Request());
            validator.Verified = true;
            service.ReportCompletion("one", "done");
            clock.Now = 5;
            service.Submit("player", Request("pending"));
            service.Submit("stranger", Request("rejected"));
            service.CommitBatch("player", "batch", new[] { Request("future") });
            var state = service.Save();
            var restored = new CommandService(clock, validator, graph);
            restored.Restore(state);
            clock.Now = 10;
            restored.Submit("player", Request());
            restored.Submit("player", Request("pending"));
            Assert.That(restored.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Completed));
            Assert.That(restored.GetOrder("pending")!.IssuedAt, Is.EqualTo(5));
            Assert.That(restored.GetOrder("pending")!.DeliveryDeadline, Is.EqualTo(35));
            Assert.That(restored.GetOrder("rejected")!.Status, Is.EqualTo(OrderStatus.Rejected));
            Assert.That(restored.CommitBatch("player", "batch", Array.Empty<OrderRequest>()).Orders.Count, Is.EqualTo(1));
            Assert.That(state.Orders.Count, Is.EqualTo(3));
            Assert.That(restored.Feedback.Count, Is.EqualTo(state.Feedback.Count));
        }

        [Test]
        public void DeliveryAndResumeRevalidateWorldCapability()
        {
            service.Submit("player", Request());
            validator.Capable = false;
            Assert.That(service.Deliver("one").Accepted, Is.False);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Failed));
            validator.Capable = true;
            Activate(Request("second"));
            service.SuspendForEmergency("a", "emergency");
            validator.RegionValid = false;
            service.ResumeAfterEmergency("a");
            Assert.That(service.GetOrder("second")!.Status, Is.EqualTo(OrderStatus.Failed));
        }

        [Test]
        public void EmergencyAndOriginalLifecycleSurviveRestore()
        {
            Activate(Request());
            service.SuspendForEmergency("a", "incoming fire");
            var restored = new CommandService(clock, validator, graph);
            restored.Restore(service.Save());
            clock.Now = 9;
            Assert.That(restored.GetOrder("one")!.ResumeStatus, Is.EqualTo(OrderStatus.Active));
            restored.ResumeAfterEmergency("a");
            Assert.That(restored.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(restored.GetOrder("one")!.IssuedAt, Is.Zero);
        }

        [Test]
        public void LowerAuthorityCannotShortenUntilCancelledCompletionCondition()
        {
            Activate(Request(constraints: new OrderConstraints(completion: CompletionMode.UntilCancelled)));
            Assert.That(service.Submit("leader", Request("short",
                disposition: OrderDisposition.Append)).Accepted, Is.False);
            Assert.That(service.Submit("leader", Request("persistent",
                constraints: new OrderConstraints(completion: CompletionMode.UntilCancelled),
                disposition: OrderDisposition.Append)).Accepted, Is.True);
        }

        [Test]
        public void DelegationRequiresExplicitPermissionOnParent()
        {
            Activate(Request());
            Assert.That(service.Submit("a", Request("child", "b", parent: "one")).Accepted, Is.False);
        }

        [TestCase(OrderType.HoldArea)]
        [TestCase(OrderType.Follow)]
        [TestCase(OrderType.CoverAlly)]
        public void AuthoredPersistentOrderTypesCannotCompleteFromDefaultConstraints(OrderType type)
        {
            Activate(new OrderRequest("persistent", "a", type, constraints: new OrderConstraints(true)));
            validator.Verified = true;
            service.ReportCompletion("persistent", "milestone");
            Assert.That(service.GetOrder("persistent")!.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(service.GetOrder("persistent")!.Request.Constraints.HoldFire, Is.True);
            Assert.That(service.GetOrder("persistent")!.Milestones, Is.EqualTo(1));
        }

        [Test]
        public void TrustedHostFailureCancelsDescendantsAndPromotesQueue()
        {
            Activate(Request(allowDelegation: true));
            Activate(Request("child", "b", parent: "one"), "a");
            Activate(Request("next", disposition: OrderDisposition.Append));
            Assert.That(service.ReportFailure("one", "Destination unreachable.").Accepted, Is.True);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Failed));
            Assert.That(service.GetOrder("child")!.Status, Is.EqualTo(OrderStatus.Cancelled));
            Assert.That(service.GetPrimary("a")!.Request.OrderId, Is.EqualTo("next"));
            Assert.That(service.ReportFailure("one", "duplicate").Accepted, Is.True);
            Assert.That(service.GetOrder("one")!.Reason, Does.Contain("Destination unreachable."));
        }

        [Test]
        public void ParentCompletionRequiresSuccessfulChildrenDespitePositiveWorldValidator()
        {
            Activate(Request(allowDelegation: true));
            Activate(Request("child", "b", parent: "one"), "a");
            validator.Verified = true;
            Assert.That(service.ReportCompletion("one", "predicted clear").Accepted, Is.False);
            service.ReportCompletion("child", "child done");
            Assert.That(service.ReportCompletion("one", "verified parent done").Accepted, Is.True);
        }

        [Test]
        public void FailedChildrenBlockParentUnlessPartialCompletionWasExplicitlyAuthored()
        {
            Activate(Request(allowDelegation: true));
            Activate(Request("child", "b", parent: "one"), "a");
            service.ReportFailure("child", "blocked");
            validator.Verified = true;
            Assert.That(service.ReportCompletion("one", "goal appears satisfied").Accepted, Is.False);
            service.Cancel("player", "one");
            Activate(new OrderRequest("partial", "a", OrderType.Move, allowDelegation: true,
                allowPartialChildCompletion: true));
            Activate(Request("failed", "b", parent: "partial"), "a");
            service.ReportFailure("failed", "optional child blocked");
            Assert.That(service.ReportCompletion("partial", "authored partial success").Accepted, Is.True);
        }

        [Test]
        public void SameTickReplacementPreservesNewerPendingOrderAndRejectsLateOlderDelivery()
        {
            service.Submit("player", Request("older"));
            service.Submit("player", Request("newer"));
            service.Deliver("older");
            service.Acknowledge("older");
            Assert.That(service.GetOrder("newer")!.Status, Is.EqualTo(OrderStatus.PendingDelivery));
            service.Deliver("newer");
            service.Acknowledge("newer");
            Assert.That(service.Deliver("older").Accepted, Is.False);
            Assert.That(service.GetPrimary("a")!.Request.OrderId, Is.EqualTo("newer"));
            Assert.That(service.GetOrder("newer")!.Sequence, Is.GreaterThan(service.GetOrder("older")!.Sequence));
            var restored = new CommandService(clock, validator, graph);
            restored.Restore(service.Save());
            restored.Submit("player", Request("latest"));
            Assert.That(restored.GetOrder("latest")!.Sequence, Is.GreaterThan(service.GetOrder("newer")!.Sequence));
        }

        [Test]
        public void DeliveryCapturesCurrentAuthorityAndRevocationBeforeAcknowledgementFails()
        {
            service.Submit("leader", Request());
            graph.SetRole("leader", AuthorityLevel.Delegated);
            Assert.That(service.Deliver("one").Accepted, Is.True);
            Assert.That(service.GetOrder("one")!.Authority, Is.EqualTo(AuthorityLevel.Delegated));
            graph.SetRole("leader", AuthorityLevel.None);
            Assert.That(service.Acknowledge("one").Accepted, Is.False);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Failed));
            Assert.That(service.GetPrimary("a"), Is.Null);
        }

        [Test]
        public void RevokedIssuerCannotDeliverPreviouslyAcceptedOrder()
        {
            service.Submit("leader", Request());
            graph.SetRole("leader", AuthorityLevel.None);
            Assert.That(service.Deliver("one").Accepted, Is.False);
            Assert.That(service.GetOrder("one")!.Status, Is.EqualTo(OrderStatus.Failed));
        }
    }
}
