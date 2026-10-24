using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Commands
{
    /// <summary>Host-owned, single-threaded command authority. Delivery and completion are trusted host events.</summary>
    public sealed class CommandService
    {
        public const int QueueCapacity = 8;
        public const int MaxChildren = 8;
        public const int MaxDelegationDepth = 3;
        private readonly IClock clock;
        private readonly ICommandValidator validator;
        private readonly AuthorityGraph hierarchy;
        private readonly Dictionary<string, OrderRecord> orders = new Dictionary<string, OrderRecord>();
        private readonly Dictionary<string, PolicyRecord> policies = new Dictionary<string, PolicyRecord>();
        private readonly Dictionary<string, BatchResult> batches = new Dictionary<string, BatchResult>();
        private readonly List<OrderFeedback> feedback = new List<OrderFeedback>();
        private readonly HashSet<string> emergency = new HashSet<string>();
        private long nextSequence;

        public CommandService(IClock clock, ICommandValidator validator, AuthorityGraph? hierarchy = null)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
            this.hierarchy = hierarchy ?? new AuthorityGraph();
        }

        public AuthorityGraph Hierarchy => hierarchy;
        public IReadOnlyList<OrderFeedback> Feedback => Array.AsReadOnly(feedback.ToArray());
        public IReadOnlyList<OrderRecord> Orders => Array.AsReadOnly(orders.Values.ToArray());
        public OrderRecord? GetOrder(string orderId) => orders.TryGetValue(orderId, out var record) ? record : null;
        public OrderRecord? GetPrimary(string recipient) => orders.Values.FirstOrDefault(x =>
            x.Request.Recipient == recipient && (x.Status == OrderStatus.Active ||
            (x.Status == OrderStatus.Suspended && x.ResumeStatus == OrderStatus.Active)));

        public bool CanSubmit(string caller, OrderRequest request, out string reason)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (orders.TryGetValue(request.OrderId, out var known))
            {
                reason = known.Issuer == caller ? known.Reason : "Order ID belongs to another issuer.";
                return known.Issuer == caller && known.Status != OrderStatus.Rejected;
            }
            return Validate(caller, request, out _, out reason);
        }

        public OrderResult Submit(string caller, OrderRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (orders.TryGetValue(request.OrderId, out var known))
                return new OrderResult(known.Issuer == caller && known.Status != OrderStatus.Rejected,
                    known.Issuer == caller ? known.Reason : "Order ID belongs to another issuer.", known);
            bool accepted = Validate(caller, request, out var authority, out var reason);
            var record = new OrderRecord(request, caller, authority,
                accepted ? OrderStatus.PendingDelivery : OrderStatus.Rejected,
                clock.Now, clock.Now, clock.Now + request.DeliveryTimeout, reason, sequence: ++nextSequence);
            orders.Add(request.OrderId, record);
            feedback.Add(new OrderFeedback(request.OrderId, record.Status, clock.Now, reason));
            return new OrderResult(accepted, reason, record);
        }

        private bool Validate(string caller, OrderRequest request, out AuthorityLevel authority, out string reason)
        {
            authority = hierarchy.GetAuthority(caller, request.Recipient);
            var level = authority;
            reason = "";
            if (authority == AuthorityLevel.None) return Reject("Issuer is outside the friendly authority scope.", out reason);
            if (!validator.ValidateAuthority(caller, request, out reason) ||
                !validator.ValidateCapability(request.Recipient, request.Type, out reason) ||
                !validator.ValidateTarget(request, out reason) || !validator.ValidateRegion(request, out reason)) return false;
            if (request.Constraints.ExpiresAt.HasValue && request.Constraints.ExpiresAt.Value <= clock.Now)
                return Reject("Order has expired.", out reason);
            if (!Enum.IsDefined(typeof(CompletionMode), request.Constraints.Completion))
                return Reject("Invalid completion mode.", out reason);
            foreach (var policy in ActivePolicies(request.Recipient))
                if (!Narrows(request.Constraints, policy.Request.Constraints))
                    return Reject("Order relaxes a persistent policy.", out reason);
            var existing = LiveOrders(request.Recipient).Where(x => x.Request.OrderId != request.OrderId).ToArray();
            if (request.Disposition == OrderDisposition.Replace && existing.Any(x => x.Authority > level))
                return Reject("A higher-authority order cannot be replaced.", out reason);
            if (request.Disposition == OrderDisposition.Replace &&
                orders.TryGetValue(request.OrderId, out var issued) &&
                existing.Any(x => x.Authority == level && x.Sequence > issued.Sequence &&
                    x.Request.Disposition == OrderDisposition.Replace && (x.Status == OrderStatus.Active ||
                    (x.Status == OrderStatus.Suspended && x.ResumeStatus == OrderStatus.Active))))
                return Reject("A newer same-authority primary order has already activated.", out reason);
            foreach (var higher in existing.Where(x => x.Authority > level))
                if (!Narrows(request.Constraints, higher.Request.Constraints) ||
                    (higher.Request.Constraints.Completion != CompletionMode.Once &&
                    higher.Request.Constraints.Completion != request.Constraints.Completion))
                    return Reject("Lower authority cannot relax existing constraints.", out reason);
            if (request.Disposition == OrderDisposition.Append &&
                existing.Count(x => x.Request.Disposition == OrderDisposition.Append || x.Status == OrderStatus.Queued) >= QueueCapacity)
                return Reject("Recipient queue is full.", out reason);
            if (request.ParentOrderId != null && !ValidateParent(caller, request, authority, out reason)) return false;
            reason = "Awaiting explicit communication delivery.";
            return true;
        }

        private bool ValidateParent(string caller, OrderRequest request, AuthorityLevel authority, out string reason)
        {
            reason = "";
            if (!orders.TryGetValue(request.ParentOrderId!, out var parent) || parent.IsTerminal ||
                (parent.Request.Constraints.ExpiresAt.HasValue && parent.Request.Constraints.ExpiresAt <= clock.Now))
                return Reject("Parent order is not live.", out reason);
            if (!parent.Request.AllowDelegation)
                return Reject("Parent order does not permit delegation.", out reason);
            if (caller != parent.Request.Recipient)
                return Reject("Only the parent's recipient may delegate its task.", out reason);
            if (authority == AuthorityLevel.None || authority > parent.Authority ||
                !Narrows(request.Constraints, parent.Request.Constraints))
                return Reject("Delegation must remain in scope and narrow parent constraints.", out reason);
            if (orders.Values.Count(x => x.Request.ParentOrderId == parent.Request.OrderId &&
                x.Request.OrderId != request.OrderId && x.Status != OrderStatus.Rejected) >= MaxChildren)
                return Reject("Parent child bound exceeded.", out reason);
            var recipients = new HashSet<string> { request.Recipient };
            int depth = 0;
            while (true)
            {
                if (!recipients.Add(parent.Request.Recipient))
                    return Reject("Delegation recipient ancestry cycle.", out reason);
                if (++depth > MaxDelegationDepth) return Reject("Delegation depth exceeded.", out reason);
                if (parent.Request.ParentOrderId == null) break;
                if (!orders.TryGetValue(parent.Request.ParentOrderId, out var ancestor))
                    return Reject("Missing delegation ancestor.", out reason);
                parent = ancestor;
            }
            return true;
        }

        private bool Narrows(OrderConstraints child, OrderConstraints parent) =>
            (!parent.HoldFire || child.HoldFire) && child.MaxRisk <= parent.MaxRisk &&
            (parent.RegionId == null || (child.RegionId != null &&
                (child.RegionId == parent.RegionId || validator.IsRegionWithin(child.RegionId, parent.RegionId)))) &&
            (!parent.ExpiresAt.HasValue || (child.ExpiresAt.HasValue && child.ExpiresAt <= parent.ExpiresAt));

        public OrderResult Deliver(string orderId)
        {
            Tick();
            if (!orders.TryGetValue(orderId, out var record)) return Missing();
            if (record.Status != OrderStatus.PendingDelivery)
                return new OrderResult(!record.IsTerminal, record.Reason, record);
            if (!Validate(record.Issuer, record.Request, out var authority, out var reason))
            {
                Terminate(record, OrderStatus.Failed, "Delivery revalidation: " + reason);
                return new OrderResult(false, reason, orders[orderId]);
            }
            record = RefreshAuthority(record, authority);
            Transition(record, OrderStatus.Delivered, "Communication delivered; awaiting acknowledgement.");
            return Success(orderId);
        }

        public OrderResult Acknowledge(string orderId)
        {
            Tick();
            if (!orders.TryGetValue(orderId, out var record)) return Missing();
            if (record.Status != OrderStatus.Delivered)
                return new OrderResult(!record.IsTerminal && record.Status != OrderStatus.PendingDelivery, record.Reason, record);
            if (!Validate(record.Issuer, record.Request, out var authority, out var reason))
            {
                Terminate(record, OrderStatus.Failed, "Acknowledgement revalidation: " + reason);
                return new OrderResult(false, reason, orders[orderId]);
            }
            record = RefreshAuthority(record, authority);
            Transition(record, OrderStatus.Acknowledged, "Acknowledged, not completed.");
            record = orders[orderId];
            if (record.Request.Disposition == OrderDisposition.Replace)
            {
                foreach (var old in LiveOrders(record.Request.Recipient).Where(x => x.Request.OrderId != orderId &&
                    (x.Authority < record.Authority || x.Sequence < record.Sequence)).ToArray())
                    Terminate(old, OrderStatus.Superseded, "Replaced by " + orderId, false);
            }
            if (GetPrimary(record.Request.Recipient) != null)
                Transition(record, OrderStatus.Queued, "Appended to recipient queue.");
            else Activate(record);
            return Success(orderId);
        }

        public OrderResult Cancel(string caller, string orderId)
        {
            if (!orders.TryGetValue(orderId, out var record)) return Missing();
            if (hierarchy.GetAuthority(caller, record.Request.Recipient) < record.Authority)
                return new OrderResult(false, "Lower authority cannot cancel this order.", record);
            if (!validator.ValidateAuthority(caller, record.Request, out var reason))
                return new OrderResult(false, reason, record);
            if (!record.IsTerminal) Terminate(record, OrderStatus.Cancelled, "Cancelled by " + caller);
            return Success(orderId);
        }

        public OrderResult ReportCompletion(string orderId, string evidence)
        {
            Tick();
            if (!orders.TryGetValue(orderId, out var record)) return Missing();
            if (record.IsTerminal) return new OrderResult(record.Status == OrderStatus.Completed, record.Reason, record);
            if (record.Status != OrderStatus.Active)
                return new OrderResult(false, "Only an active order may report completion.", record);
            if (!record.Request.AllowPartialChildCompletion && orders.Values.Any(x =>
                x.Request.ParentOrderId == orderId && x.Status != OrderStatus.Rejected && x.Status != OrderStatus.Completed))
                return new OrderResult(false, "All accepted child orders must complete successfully first.", record);
            if (!validator.VerifyCompletion(record.Request, evidence, out var reason))
                return new OrderResult(false, reason, record);
            if (record.Request.Constraints.Completion != CompletionMode.Once)
            {
                Transition(record, OrderStatus.Active, "Verified persistent milestone: " + reason,
                    milestones: record.Milestones + 1);
                return Success(orderId);
            }

            Terminate(record, OrderStatus.Completed, "Verified completion: " + reason);
            return Success(orderId);
        }

        public OrderResult ReportFailure(string orderId, string reason)
        {
            OrderRequest.Required(reason, nameof(reason));
            Tick();
            if (!orders.TryGetValue(orderId, out var record)) return Missing();
            if (record.IsTerminal) return new OrderResult(record.Status == OrderStatus.Failed, record.Reason, record);
            Terminate(record, OrderStatus.Failed, "Trusted host failure: " + reason);
            return Success(orderId);
        }

        public void SuspendForEmergency(string recipient, string reason)
        {
            emergency.Add(recipient);
            foreach (var record in LiveOrders(recipient).Where(x => x.Status == OrderStatus.Active ||
                x.Status == OrderStatus.Queued).ToArray())
                Transition(record, OrderStatus.Suspended, reason, record.Status);
        }

        public void ResumeAfterEmergency(string recipient)
        {
            emergency.Remove(recipient);
            Tick();
            foreach (var record in LiveOrders(recipient).Where(x => x.Status == OrderStatus.Suspended).ToArray())
            {
                if (!Validate(record.Issuer, record.Request, out var authority, out var reason))
                    Terminate(record, OrderStatus.Failed, "Resume revalidation: " + reason, false);
                else Transition(RefreshAuthority(record, authority), record.ResumeStatus ?? OrderStatus.Queued,
                    "Emergency ended; resumed.");
            }
            Promote(recipient);
        }

        public void Tick()
        {
            foreach (var record in orders.Values.Where(x => !x.IsTerminal).ToArray())
            {
                if (orders[record.Request.OrderId].IsTerminal) continue;
                if (record.Request.Constraints.ExpiresAt.HasValue && clock.Now >= record.Request.Constraints.ExpiresAt)
                    Terminate(record, OrderStatus.Expired, "Order expiry reached.");
                else if ((record.Status == OrderStatus.PendingDelivery || record.Status == OrderStatus.Delivered) &&
                    clock.Now >= record.DeliveryDeadline)
                    Terminate(record, OrderStatus.DeliveryExpired, "Communication delivery/acknowledgement deadline missed.");
            }
        }

        public OrderResult SetPolicy(string caller, PolicyRequest request)
        {
            if (policies.TryGetValue(request.PolicyId, out var known))
                return new OrderResult(known.Issuer == caller,
                    known.Issuer == caller ? "Policy already recorded." : "Policy ID belongs to another issuer.");
            if (!CanSetPolicy(caller, request, out var authority, out var reason))
                return new OrderResult(false, reason);
            policies.Add(request.PolicyId, new PolicyRecord(request, caller, authority, clock.Now));
            return new OrderResult(true, "Persistent policy installed.");
        }

        public bool CanSetPolicy(string caller, PolicyRequest request, out string reason)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (policies.TryGetValue(request.PolicyId, out var known))
            {
                reason = known.Issuer == caller ? "Policy already recorded." : "Policy ID belongs to another issuer.";
                return known.Issuer == caller;
            }
            return CanSetPolicy(caller, request, out _, out reason);
        }

        private bool CanSetPolicy(string caller, PolicyRequest request, out AuthorityLevel authority, out string reason)
        {
            var probe = new OrderRequest(request.PolicyId, request.Recipient, OrderType.HoldArea, constraints: request.Constraints);
            authority = hierarchy.GetAuthority(caller, request.Recipient);
            reason = "";
            if (authority == AuthorityLevel.None || !validator.ValidateAuthority(caller, probe, out reason))
                return Reject("Policy issuer is outside authority scope. " + reason, out reason);
            if (!validator.ValidateRegion(probe, out reason)) return false;
            if (request.Constraints.ExpiresAt.HasValue && request.Constraints.ExpiresAt <= clock.Now)
                return Reject("Policy has expired.", out reason);
            foreach (var existing in ActivePolicies(request.Recipient))
                if (authority < existing.Authority && !Narrows(request.Constraints, existing.Request.Constraints))
                    return Reject("Lower authority cannot relax policy constraints.", out reason);
            if (ActivePolicies(request.Recipient).Any(x => !Compatible(x.Request.Constraints, request.Constraints)))
                return Reject("Policy regions conflict.", out reason);
            return true;
        }

        public bool RemovePolicy(string caller, string policyId, out string reason)
        {
            if (!policies.TryGetValue(policyId, out var record)) return Reject("Unknown policy.", out reason);
            if (hierarchy.GetAuthority(caller, record.Request.Recipient) < record.Authority)
                return Reject("Lower authority cannot remove policy.", out reason);
            var probe = new OrderRequest(policyId, record.Request.Recipient, OrderType.HoldArea,
                constraints: record.Request.Constraints);
            if (!validator.ValidateAuthority(caller, probe, out reason)) return false;
            policies.Remove(policyId);
            reason = "Policy removed.";
            return true;
        }

        public OrderConstraints GetEffectiveConstraints(string recipient)
        {
            var constraints = ActivePolicies(recipient).Select(x => x.Request.Constraints).ToList();
            var primary = GetPrimary(recipient);
            if (primary != null) constraints.Add(primary.Request.Constraints);
            bool hold = constraints.Any(x => x.HoldFire);
            double risk = constraints.Count == 0 ? 1 : constraints.Min(x => x.MaxRisk);
            double? expiry = constraints.Where(x => x.ExpiresAt.HasValue).Select(x => x.ExpiresAt).Min();
            string? region = null;
            foreach (var item in constraints.Where(x => x.RegionId != null))
                if (region == null || validator.IsRegionWithin(item.RegionId!, region)) region = item.RegionId;
            return new OrderConstraints(hold, region, risk, expiry,
                primary?.Request.Constraints.Completion ?? CompletionMode.Once);
        }

        private bool Compatible(OrderConstraints a, OrderConstraints b) => a.RegionId == null || b.RegionId == null ||
            a.RegionId == b.RegionId || validator.IsRegionWithin(a.RegionId, b.RegionId) ||
            validator.IsRegionWithin(b.RegionId, a.RegionId);

        private IEnumerable<PolicyRecord> ActivePolicies(string recipient) => policies.Values.Where(x =>
            x.Request.Recipient == recipient && (!x.Request.Constraints.ExpiresAt.HasValue ||
            clock.Now < x.Request.Constraints.ExpiresAt.Value));

        private IEnumerable<OrderRecord> LiveOrders(string recipient) =>
            orders.Values.Where(x => !x.IsTerminal && x.Request.Recipient == recipient);

        private void Activate(OrderRecord record)
        {
            if (emergency.Contains(record.Request.Recipient))
                Transition(record, OrderStatus.Suspended, "Emergency active.", OrderStatus.Active);
            else Transition(record, OrderStatus.Active, "Primary order activated.");
        }

        private void Promote(string recipient)
        {
            if (GetPrimary(recipient) != null || emergency.Contains(recipient)) return;
            foreach (var queued in LiveOrders(recipient).Where(x => x.Status == OrderStatus.Queued)
                .OrderBy(x => x.Sequence).ThenBy(x => x.IssuedAt).ToArray())
            {
                if (!Validate(queued.Issuer, queued.Request, out var authority, out var reason))
                { Terminate(queued, OrderStatus.Failed, "Queue revalidation: " + reason, false); continue; }
                Activate(RefreshAuthority(queued, authority));
                break;
            }
        }

        private void Terminate(OrderRecord record, OrderStatus status, string reason, bool promote = true)
        {
            Transition(record, status, reason);
            foreach (var child in orders.Values.Where(x => !x.IsTerminal &&
                x.Request.ParentOrderId == record.Request.OrderId).ToArray())
                Terminate(child, status == OrderStatus.Superseded ? OrderStatus.Superseded : OrderStatus.Cancelled,
                    "Parent terminated: " + record.Request.OrderId, promote);
            if (promote) Promote(record.Request.Recipient);
        }

        private void Transition(OrderRecord record, OrderStatus status, string reason,
            OrderStatus? resumeStatus = null, int? milestones = null)
        {
            orders[record.Request.OrderId] = new OrderRecord(record.Request, record.Issuer, record.Authority,
                status, record.IssuedAt, clock.Now, record.DeliveryDeadline, reason, resumeStatus,
                milestones ?? record.Milestones, record.Sequence);
            feedback.Add(new OrderFeedback(record.Request.OrderId, status, clock.Now, reason));
        }

        private OrderRecord RefreshAuthority(OrderRecord record, AuthorityLevel authority)
        {
            var refreshed = new OrderRecord(record.Request, record.Issuer, authority, record.Status,
                record.IssuedAt, record.UpdatedAt, record.DeliveryDeadline, record.Reason,
                record.ResumeStatus, record.Milestones, record.Sequence);
            orders[record.Request.OrderId] = refreshed;
            return refreshed;
        }

        public BatchResult ValidateBatch(string caller, string requestId, IEnumerable<OrderRequest> proposedOrders,
            IEnumerable<PolicyRequest>? proposedPolicies = null)
        {
            OrderRequest.Required(requestId, nameof(requestId));
            if (batches.TryGetValue(requestId, out var previous)) return BatchReplay(caller, previous);
            var orderList = proposedOrders.ToArray();
            var policyList = (proposedPolicies ?? Array.Empty<PolicyRequest>()).ToArray();
            var copy = new CommandService(clock, validator, hierarchy);
            copy.Restore(Save());
            var ids = new HashSet<string>();
            foreach (var policy in policyList)
            {
                if (!ids.Add(policy.PolicyId)) return Batch(false, "Duplicate proposal node ID.");
                var result = copy.SetPolicy(caller, policy);
                if (!result.Accepted) return Batch(false, result.Reason);
            }
            // Validate orders against the complete policy set, not only each policy's insertion prefix.
            foreach (var order in orderList)
                foreach (var policy in copy.ActivePolicies(order.Recipient))
                    if (!copy.Narrows(order.Constraints, policy.Request.Constraints))
                        return Batch(false, "Order relaxes a persistent policy.");
            var pending = orderList.ToList();
            while (pending.Count > 0)
            {
                var ready = pending.FirstOrDefault(x => x.ParentOrderId == null || copy.orders.ContainsKey(x.ParentOrderId));
                if (ready == null) return Batch(false, "Missing parent or delegation cycle.");
                if (!ids.Add(ready.OrderId)) return Batch(false, "Duplicate proposal node ID.");
                var result = copy.Submit(caller, ready);
                if (!result.Accepted) return Batch(false, result.Reason);
                pending.Remove(ready);
            }
            return Batch(true, "Validated atomically; submit nodes individually to execute.");

            BatchResult Batch(bool accepted, string reason) =>
                new BatchResult(requestId, caller, accepted, reason, orderList, policyList);
        }

        public BatchResult CommitBatch(string caller, string requestId, IEnumerable<OrderRequest> proposedOrders,
            IEnumerable<PolicyRequest>? proposedPolicies = null)
        {
            if (batches.TryGetValue(requestId, out var previous)) return BatchReplay(caller, previous);
            var result = ValidateBatch(caller, requestId, proposedOrders, proposedPolicies);
            batches.Add(requestId, result);
            return result;
        }

        private static BatchResult BatchReplay(string caller, BatchResult previous) =>
            caller == previous.Issuer ? previous : new BatchResult(previous.RequestId, caller, false,
                "Request ID belongs to another issuer.", Array.Empty<OrderRequest>(), Array.Empty<PolicyRequest>());

        public CommandState Save() => new CommandState(orders.Values, policies.Values, batches.Values, feedback, emergency);

        public void Restore(CommandState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var restoredOrders = state.Orders.ToDictionary(x => x.Request.OrderId);
            var restoredPolicies = state.Policies.ToDictionary(x => x.Request.PolicyId);
            var restoredBatches = state.Batches.ToDictionary(x => x.RequestId);
            if (restoredOrders.Values.Where(x => x.Status == OrderStatus.Active ||
                (x.Status == OrderStatus.Suspended && x.ResumeStatus == OrderStatus.Active))
                .GroupBy(x => x.Request.Recipient).Any(x => x.Count() > 1))
                throw new ArgumentException("Snapshot has multiple primary orders.", nameof(state));
            orders.Clear(); policies.Clear(); batches.Clear(); feedback.Clear(); emergency.Clear();
            foreach (var item in restoredOrders) orders.Add(item.Key, item.Value);
            nextSequence = restoredOrders.Count == 0 ? 0 : restoredOrders.Values.Max(x => x.Sequence);
            foreach (var item in restoredPolicies) policies.Add(item.Key, item.Value);
            foreach (var item in restoredBatches) batches.Add(item.Key, item.Value);
            feedback.AddRange(state.Feedback);
            foreach (var item in state.EmergencyRecipients) emergency.Add(item);
        }

        private OrderResult Success(string id) => new OrderResult(true, orders[id].Reason, orders[id]);
        private static OrderResult Missing() => new OrderResult(false, "Unknown order.");
        private static bool Reject(string message, out string reason) { reason = message; return false; }
    }
}
