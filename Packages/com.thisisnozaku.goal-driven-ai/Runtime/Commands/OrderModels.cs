using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Commands
{
    public enum OrderType { Move, HoldArea, Attack, Follow, SearchArea, CoverAlly, Interact, SecureRoom }
    public enum AuthorityLevel { None, Autonomous, Delegated, Leader, Player }
    public enum OrderDisposition { Replace, Append }
    public enum CompletionMode { Once, Persistent, UntilCancelled }
    public enum OrderStatus
    {
        PendingDelivery, Delivered, Acknowledged, Queued, Active, Suspended,
        Completed, Cancelled, Superseded, Rejected, Expired, Failed, DeliveryExpired
    }

    public sealed class OrderConstraints
    {
        public bool HoldFire { get; }
        public string? RegionId { get; }
        public double MaxRisk { get; }
        public double? ExpiresAt { get; }
        public CompletionMode Completion { get; }

        public OrderConstraints(bool holdFire = false, string? regionId = null, double maxRisk = 1,
            double? expiresAt = null, CompletionMode completion = CompletionMode.Once)
        {
            if (double.IsNaN(maxRisk) || double.IsInfinity(maxRisk) || maxRisk < 0 || maxRisk > 1)
                throw new ArgumentOutOfRangeException(nameof(maxRisk));
            if (expiresAt.HasValue && (double.IsNaN(expiresAt.Value) || double.IsInfinity(expiresAt.Value)))
                throw new ArgumentOutOfRangeException(nameof(expiresAt));
            HoldFire = holdFire;
            RegionId = regionId;
            MaxRisk = maxRisk;
            ExpiresAt = expiresAt;
            Completion = completion;
        }
    }

    public sealed class OrderRequest
    {
        public string OrderId { get; }
        public string Recipient { get; }
        public OrderType Type { get; }
        public Position? Position { get; }
        public string? TargetId { get; }
        public OrderConstraints Constraints { get; }
        public string? ParentOrderId { get; }
        public OrderDisposition Disposition { get; }
        public double DeliveryTimeout { get; }
        public bool AllowDelegation { get; }
        public bool AllowPartialChildCompletion { get; }

        public OrderRequest(string orderId, string recipient, OrderType type, Position? position = null,
            string? targetId = null, OrderConstraints? constraints = null, string? parentOrderId = null,
            OrderDisposition disposition = OrderDisposition.Replace, double deliveryTimeout = 30,
            bool allowDelegation = false, bool allowPartialChildCompletion = false)
        {
            OrderId = Required(orderId, nameof(orderId));
            Recipient = Required(recipient, nameof(recipient));
            if (!Enum.IsDefined(typeof(OrderType), type)) throw new ArgumentOutOfRangeException(nameof(type));
            if (!Enum.IsDefined(typeof(OrderDisposition), disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
            if (double.IsNaN(deliveryTimeout) || double.IsInfinity(deliveryTimeout) || deliveryTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(deliveryTimeout));
            Type = type;
            Position = position;
            TargetId = targetId;
            var requestedConstraints = constraints ?? new OrderConstraints();
            Constraints = requestedConstraints.Completion == CompletionMode.Once &&
                (type == OrderType.HoldArea || type == OrderType.Follow || type == OrderType.CoverAlly)
                ? new OrderConstraints(requestedConstraints.HoldFire, requestedConstraints.RegionId,
                    requestedConstraints.MaxRisk, requestedConstraints.ExpiresAt, CompletionMode.Persistent)
                : requestedConstraints;
            ParentOrderId = parentOrderId;
            Disposition = disposition;
            DeliveryTimeout = deliveryTimeout;
            AllowDelegation = allowDelegation;
            AllowPartialChildCompletion = allowPartialChildCompletion;
        }

        internal static string Required(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A stable identifier is required.", name) : value;
    }

    public sealed class PolicyRequest
    {
        public string PolicyId { get; }
        public string Recipient { get; }
        public OrderConstraints Constraints { get; }
        public PolicyRequest(string policyId, string recipient, OrderConstraints constraints)
        {
            PolicyId = OrderRequest.Required(policyId, nameof(policyId));
            Recipient = OrderRequest.Required(recipient, nameof(recipient));
            Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        }
    }

    public interface ICommandValidator
    {
        bool ValidateAuthority(string issuer, OrderRequest request, out string reason);
        bool ValidateCapability(string recipient, OrderType type, out string reason);
        bool ValidateTarget(OrderRequest request, out string reason);
        bool ValidateRegion(OrderRequest request, out string reason);
        bool IsRegionWithin(string childRegion, string parentRegion);
        bool VerifyCompletion(OrderRequest request, string evidence, out string reason);
    }

    /// <summary>Authority comes from host-owned identities and a friendly, directed hierarchy.</summary>
    public sealed class AuthorityGraph
    {
        private readonly Dictionary<string, AuthorityLevel> roles = new Dictionary<string, AuthorityLevel>();
        private readonly Dictionary<string, HashSet<string>> reports = new Dictionary<string, HashSet<string>>();
        private readonly HashSet<string> friendlies = new HashSet<string>();

        public void AddFriendly(string identity) => friendlies.Add(OrderRequest.Required(identity, nameof(identity)));

        public void SetRole(string identity, AuthorityLevel role)
        {
            OrderRequest.Required(identity, nameof(identity));
            if (!Enum.IsDefined(typeof(AuthorityLevel), role)) throw new ArgumentOutOfRangeException(nameof(role));
            roles[identity] = role;
            friendlies.Add(identity);
        }

        public bool AddHierarchy(string superior, string subordinate)
        {
            OrderRequest.Required(superior, nameof(superior));
            OrderRequest.Required(subordinate, nameof(subordinate));
            if (superior == subordinate || InScope(subordinate, superior)) return false;
            if (!reports.TryGetValue(superior, out var set)) reports[superior] = set = new HashSet<string>();
            friendlies.Add(superior);
            friendlies.Add(subordinate);
            return set.Add(subordinate);
        }

        public AuthorityLevel GetAuthority(string issuer, string recipient)
        {
            if (!roles.TryGetValue(issuer, out var role) || !friendlies.Contains(recipient)) return AuthorityLevel.None;
            if (role == AuthorityLevel.Player || issuer == recipient || InScope(issuer, recipient)) return role;
            return AuthorityLevel.None;
        }

        private bool InScope(string superior, string subordinate)
        {
            var seen = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(superior);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (current == subordinate) return true;
                if (reports.TryGetValue(current, out var children))
                    foreach (var child in children) pending.Push(child);
            }
            return false;
        }
    }

    public sealed class OrderFeedback
    {
        public string OrderId { get; }
        public OrderStatus Status { get; }
        public double Timestamp { get; }
        public string Reason { get; }
        public OrderFeedback(string orderId, OrderStatus status, double timestamp, string reason)
        { OrderId = orderId; Status = status; Timestamp = timestamp; Reason = reason; }
    }

    public sealed class OrderRecord
    {
        public OrderRequest Request { get; }
        public string Issuer { get; }
        public AuthorityLevel Authority { get; }
        public OrderStatus Status { get; }
        public double IssuedAt { get; }
        public double UpdatedAt { get; }
        public double DeliveryDeadline { get; }
        public string Reason { get; }
        public OrderStatus? ResumeStatus { get; }
        public int Milestones { get; }
        public long Sequence { get; }
        public bool IsTerminal => Status == OrderStatus.Completed || Status == OrderStatus.Cancelled ||
            Status == OrderStatus.Superseded || Status == OrderStatus.Rejected || Status == OrderStatus.Expired ||
            Status == OrderStatus.Failed || Status == OrderStatus.DeliveryExpired;

        public OrderRecord(OrderRequest request, string issuer, AuthorityLevel authority, OrderStatus status,
            double issuedAt, double updatedAt, double deliveryDeadline, string reason,
            OrderStatus? resumeStatus = null, int milestones = 0, long sequence = 0)
        {
            Request = request; Issuer = issuer; Authority = authority; Status = status;
            IssuedAt = issuedAt; UpdatedAt = updatedAt; DeliveryDeadline = deliveryDeadline;
            Reason = reason; ResumeStatus = resumeStatus; Milestones = milestones;
            Sequence = sequence;
        }
    }

    public sealed class PolicyRecord
    {
        public PolicyRequest Request { get; }
        public string Issuer { get; }
        public AuthorityLevel Authority { get; }
        public double IssuedAt { get; }
        public PolicyRecord(PolicyRequest request, string issuer, AuthorityLevel authority, double issuedAt)
        { Request = request; Issuer = issuer; Authority = authority; IssuedAt = issuedAt; }
    }

    public sealed class OrderResult
    {
        public bool Accepted { get; }
        public string Reason { get; }
        public OrderRecord? Record { get; }
        public OrderResult(bool accepted, string reason, OrderRecord? record = null)
        { Accepted = accepted; Reason = reason; Record = record; }
    }

    public sealed class BatchResult
    {
        public string RequestId { get; }
        public string Issuer { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public IReadOnlyList<OrderRequest> Orders { get; }
        public IReadOnlyList<PolicyRequest> Policies { get; }
        public BatchResult(string requestId, string issuer, bool accepted, string reason,
            IEnumerable<OrderRequest> orders, IEnumerable<PolicyRequest> policies)
        {
            RequestId = requestId; Issuer = issuer; Accepted = accepted; Reason = reason;
            Orders = Array.AsReadOnly(orders.ToArray());
            Policies = Array.AsReadOnly(policies.ToArray());
        }
    }

    /// <summary>Immutable, serializer-friendly snapshot. Restore before processing new host events.</summary>
    public sealed class CommandState
    {
        public IReadOnlyList<OrderRecord> Orders { get; }
        public IReadOnlyList<PolicyRecord> Policies { get; }
        public IReadOnlyList<BatchResult> Batches { get; }
        public IReadOnlyList<OrderFeedback> Feedback { get; }
        public IReadOnlyList<string> EmergencyRecipients { get; }
        public CommandState(IEnumerable<OrderRecord> orders, IEnumerable<PolicyRecord> policies,
            IEnumerable<BatchResult> batches, IEnumerable<OrderFeedback> feedback, IEnumerable<string> emergencyRecipients)
        {
            Orders = Array.AsReadOnly(orders.ToArray()); Policies = Array.AsReadOnly(policies.ToArray());
            Batches = Array.AsReadOnly(batches.ToArray()); Feedback = Array.AsReadOnly(feedback.ToArray());
            EmergencyRecipients = Array.AsReadOnly(emergencyRecipients.ToArray());
        }
    }
}
