using System;
using System.Collections.Generic;
using System.Linq;
using GoalDrivenAI.Commands;
using UnityEngine;

namespace GoalDrivenAI.Unity
{
    public sealed class CommandHost : MonoBehaviour, ICommandValidator
    {
        [SerializeField] private string playerIdentity = "player";
        [SerializeField] private string playerFaction = "ally";
        [SerializeField] private Actor[] actors = Array.Empty<Actor>();
        [SerializeField] private CommandRegion[] regions = Array.Empty<CommandRegion>();
        [SerializeField, Min(0)] private float radioDelay;
        [SerializeField] private bool communicationAvailable = true;
        private readonly Dictionary<string, AIBrain> brains = new Dictionary<string, AIBrain>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> knownTargets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        public CommandService Service { get; private set; }
        public string PlayerIdentity => playerIdentity;

        private void Awake()
        {
            Service = new CommandService(new UnityClock(), this);
            Service.Hierarchy.SetRole(playerIdentity, AuthorityLevel.Player);
            foreach (Actor actor in actors)
                if (actor != null) Service.Hierarchy.SetRole(actor.Id, AuthorityLevel.Autonomous);
        }

        public void Register(AIBrain brain)
        {
            if (brains.TryGetValue(brain.ActorId, out var other) && other != brain)
                throw new InvalidOperationException("Duplicate actor ID: " + brain.ActorId);
            brains[brain.ActorId] = brain;
        }
        public void Unregister(AIBrain brain) => brains.Remove(brain.ActorId);
        public Actor FindActor(string id) => actors.FirstOrDefault(actor => actor != null && actor.Id == id);
        public bool InRegion(string id, Vector3 point) => regions.Any(r => r != null && r.Id == id && r.Contains(point));

        public void RecordKnownTarget(string issuer, string targetId)
        {
            if (!knownTargets.TryGetValue(issuer, out var known))
                knownTargets[issuer] = known = new HashSet<string>(StringComparer.Ordinal);
            known.Add(targetId);
        }

        private void Update()
        {
            Service.Tick();
            if (!communicationAvailable) return;
            foreach (OrderRecord order in Service.Orders)
            {
                if (order.Status == OrderStatus.PendingDelivery && Time.timeAsDouble >= order.IssuedAt + radioDelay)
                    Service.Deliver(order.Request.OrderId);
                if (Service.GetOrder(order.Request.OrderId)?.Status == OrderStatus.Delivered)
                    Service.Acknowledge(order.Request.OrderId);
            }
        }

        public bool ValidateAuthority(string issuer, OrderRequest request, out string reason)
        {
            Actor recipient = FindActor(request.Recipient);
            Actor issuingActor = FindActor(issuer);
            string issuerFaction = issuer == playerIdentity ? playerFaction : issuingActor?.Faction;
            reason = "Recipient must be alive and in the issuer's faction.";
            return recipient != null && recipient.IsAlive && issuerFaction == recipient.Faction;
        }

        public bool ValidateCapability(string recipient, OrderType type, out string reason)
        {
            reason = "This guard supports Move, HoldArea, Attack, Follow and SearchArea.";
            return brains.ContainsKey(recipient) && (type == OrderType.Move || type == OrderType.HoldArea
                || type == OrderType.Attack || type == OrderType.Follow || type == OrderType.SearchArea);
        }

        public bool ValidateTarget(OrderRequest request, out string reason)
        {
            reason = "Missing or impermissible target/location.";
            if (request.Type == OrderType.HoldArea && request.Constraints.RegionId == null)
            {
                reason = "HoldArea requires an authored region boundary.";
                return false;
            }
            if (request.Type == OrderType.Move || request.Type == OrderType.HoldArea || request.Type == OrderType.SearchArea)
                return request.Position.HasValue;
            Actor target = request.TargetId == null ? null : FindActor(request.TargetId);
            Actor recipient = FindActor(request.Recipient);
            if (target == null || recipient == null) return false;
            if (request.Type == OrderType.Follow) return target.IsAlive && target.Faction == recipient.Faction;
            // An attack binds a reported contact, never an undisclosed scene entity.
            return request.Type == OrderType.Attack && target.Faction != recipient.Faction
                && knownTargets.TryGetValue(recipient.Id, out var known) && known.Contains(target.Id);
        }

        public bool ValidateRegion(OrderRequest request, out string reason)
        {
            reason = "Location is outside the allowed region.";
            string id = request.Constraints.RegionId;
            if (id == null) return true;
            return regions.Any(r => r != null && r.Id == id)
                && (!request.Position.HasValue || InRegion(id, request.Position.Value.ToVector()));
        }

        public bool IsRegionWithin(string childRegion, string parentRegion)
        {
            CommandRegion region = regions.FirstOrDefault(r => r != null && r.Id == childRegion);
            var seen = new HashSet<CommandRegion>();
            while (region != null && seen.Add(region))
            {
                if (region.Id == parentRegion) return true;
                region = region.Parent;
            }
            return false;
        }

        public bool VerifyCompletion(OrderRequest request, string evidence, out string reason)
        {
            reason = "Guard has not verified the requested outcome.";
            return brains.TryGetValue(request.Recipient, out var brain) && brain.Verifies(request, evidence);
        }

        public OrderResult IssuePlayerOrder(string recipient, OrderType type, Position? position = null,
            string targetId = null, bool holdFire = false, string regionId = null)
        {
            var constraints = new OrderConstraints(holdFire, regionId, completion:
                type == OrderType.HoldArea || type == OrderType.Follow ? CompletionMode.Persistent : CompletionMode.Once);
            return Service.Submit(playerIdentity, new OrderRequest(Guid.NewGuid().ToString("N"), recipient,
                type, position, targetId, constraints));
        }

        public OrderResult StopPlayerOrder(string recipient)
        {
            OrderRecord primary = Service.GetPrimary(recipient);
            OrderResult result = new OrderResult(false, "No applicable orders.");
            foreach (OrderRecord order in Service.Orders.Where(order => !order.IsTerminal
                && order.Request.Recipient == recipient
                && (order.Issuer == playerIdentity || order.Request.OrderId == primary?.Request.OrderId))
                .OrderByDescending(order => order.Sequence))
                result = Service.Cancel(playerIdentity, order.Request.OrderId);
            if (result.Accepted && brains.TryGetValue(recipient, out var brain)) brain.Interrupt();
            return result;
        }
    }
}
