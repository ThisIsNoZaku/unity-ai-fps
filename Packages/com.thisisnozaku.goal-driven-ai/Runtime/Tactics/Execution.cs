using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Tactics
{
    /// <summary>Atomic all-or-nothing claims. An expired owner cannot release a successor's lease.</summary>
    public sealed class LeaseStore
    {
        private sealed class Lease
        {
            internal readonly string Owner;
            internal double Until;
            internal Lease(string owner, double until) { Owner = owner; Until = until; }
        }
        private readonly object sync = new object();
        private readonly IClock clock;
        private readonly Dictionary<string, Lease> leases = new Dictionary<string, Lease>(StringComparer.Ordinal);
        public LeaseStore(IClock clock) { this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); }
        public bool TryAcquire(string owner, IEnumerable<string> resources, double duration)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
            if (!Evidence.Finite(duration) || duration <= 0 || !Evidence.Finite(clock.Now + duration))
                throw new ArgumentOutOfRangeException(nameof(duration));
            var keys = Keys(resources);
            lock (sync)
            {
                Expire();
                if (keys.Any(k => leases.TryGetValue(k, out var lease) && lease.Owner != owner)) return false;
                foreach (var key in keys) leases[key] = new Lease(owner, clock.Now + duration);
                return true;
            }
        }
        public bool Owns(string owner, IEnumerable<string> resources)
        {
            var keys = Keys(resources);
            lock (sync) { Expire(); return keys.All(k => leases.TryGetValue(k, out var lease) && lease.Owner == owner); }
        }
        public void ReleaseOwner(string owner)
        {
            lock (sync)
                foreach (var key in leases.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) leases.Remove(key);
        }
        internal static string[] Keys(IEnumerable<string> values)
        {
            var keys = (values ?? throw new ArgumentNullException(nameof(values))).ToArray();
            if (keys.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Resource IDs are required.", nameof(values));
            return keys.Distinct(StringComparer.Ordinal).ToArray();
        }
        private void Expire()
        {
            foreach (var key in leases.Where(p => p.Value.Until <= clock.Now).Select(p => p.Key).ToArray()) leases.Remove(key);
        }
    }

    public sealed class ChannelArbiter
    {
        private readonly LeaseStore channels;
        public ChannelArbiter(IClock clock) { channels = new LeaseStore(clock); }
        public bool TryAcquire(string owner, IEnumerable<string> names, double duration) => channels.TryAcquire(owner, names, duration);
        public bool Owns(string owner, IEnumerable<string> names) => channels.Owns(owner, names);
        public void ReleaseOwner(string owner) => channels.ReleaseOwner(owner);
    }

    public enum ActionStatus { Idle, Running, Cancelling, Succeeded, Failed, Cancelled, Rejected, TimedOut }
    public sealed class ActionContext
    {
        public Func<Fact, bool> VerifyEffect { get; }
        public ActionContext(Func<Fact, bool> verifyEffect)
        { VerifyEffect = verifyEffect ?? throw new ArgumentNullException(nameof(verifyEffect)); }
    }
    public readonly struct ActionTick
    {
        public ActionStatus Status { get; }
        public string Reason { get; }
        public ActionTick(ActionStatus status, string reason = "")
        {
            if (status != ActionStatus.Running && status != ActionStatus.Succeeded &&
                status != ActionStatus.Failed && status != ActionStatus.Cancelled)
                throw new ArgumentOutOfRangeException(nameof(status));
            Status = status; Reason = reason ?? "";
        }
    }
    public interface IRuntimeAction
    {
        string Id { get; }
        IReadOnlyCollection<string> Channels { get; }
        IReadOnlyCollection<string> Resources { get; }
        IReadOnlyList<Fact> ExpectedEffects { get; }
        bool CanStart(ActionContext context);
        void Begin(ActionContext context);
        ActionTick Tick(ActionContext context);
        void RequestCancel(ActionContext context);
        void Cleanup(ActionContext context);
    }
    public sealed class ActionExecutionResult
    {
        public ActionStatus Status { get; }
        public string Reason { get; }
        public IReadOnlyList<Fact> VerifiedEffects { get; }
        public bool IsTerminal => Status != ActionStatus.Idle && Status != ActionStatus.Running && Status != ActionStatus.Cancelling;
        internal ActionExecutionResult(ActionStatus status, string reason, IEnumerable<Fact>? effects = null)
        { Status = status; Reason = reason; VerifiedEffects = Array.AsReadOnly((effects ?? Array.Empty<Fact>()).ToArray()); }
    }

    public sealed class ActionRunner : IDisposable
    {
        private readonly IClock clock;
        private readonly ChannelArbiter channels;
        private readonly LeaseStore leases;
        private readonly double leaseDuration;
        private IRuntimeAction? action;
        private ActionContext? context;
        private string owner = "";
        private string[] channelKeys = Array.Empty<string>(), resourceKeys = Array.Empty<string>();
        private IReadOnlyList<Fact> effects = Array.Empty<Fact>();
        private double deadline, cancelAt, cancelDelay;
        private bool cancellationRequested, disposed, finishing;
        public ActionExecutionResult Result { get; private set; } = new ActionExecutionResult(ActionStatus.Idle, "");

        public ActionRunner(IClock clock, ChannelArbiter channels, LeaseStore leases, double leaseDuration = .5)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.channels = channels ?? throw new ArgumentNullException(nameof(channels));
            this.leases = leases ?? throw new ArgumentNullException(nameof(leases));
            if (!Evidence.Finite(leaseDuration) || leaseDuration <= 0) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            this.leaseDuration = leaseDuration;
        }
        public ActionExecutionResult Start(IRuntimeAction action, ActionContext context, double timeout = 10, double maxCancellationDelay = .25)
        {
            if (disposed) throw new ObjectDisposedException(nameof(ActionRunner));
            if (this.action != null || finishing) return new ActionExecutionResult(ActionStatus.Rejected, "Runner is busy.");
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!Evidence.Finite(timeout) || timeout <= 0 || !Evidence.Finite(clock.Now + timeout) ||
                !Evidence.Finite(maxCancellationDelay) || maxCancellationDelay < 0)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            this.action = action; this.context = context; owner = Guid.NewGuid().ToString("N");
            cancellationRequested = false; deadline = clock.Now + timeout; cancelDelay = maxCancellationDelay;
            Result = new ActionExecutionResult(ActionStatus.Running, "");
            try
            {
                if (string.IsNullOrWhiteSpace(action.Id)) return Finish(ActionStatus.Rejected, "Action ID is required.");
                channelKeys = LeaseStore.Keys(action.Channels);
                resourceKeys = LeaseStore.Keys(action.Resources);
                effects = PlanningAction.CopyFacts(action.ExpectedEffects);
                var canStart = action.CanStart(context);
                if (this.action == null) return Result;
                if (!canStart) return Finish(ActionStatus.Rejected, "CanStart rejected.");
                if (!channels.TryAcquire(owner, channelKeys, leaseDuration)) return Finish(ActionStatus.Rejected, "Channel is occupied.");
                if (!leases.TryAcquire(owner, resourceKeys, leaseDuration)) return Finish(ActionStatus.Rejected, "Resource is leased.");
                action.Begin(context);
                if (this.action == null) return Result;
                if (clock.Now >= deadline) return Finish(ActionStatus.TimedOut, "Action deadline exceeded.");
                if (!channels.Owns(owner, channelKeys) || !leases.Owns(owner, resourceKeys))
                    return Finish(ActionStatus.Failed, "Ownership expired or was lost.");
                return Result;
            }
            catch (Exception ex) { return Finish(ActionStatus.Failed, "Begin: " + ex.Message); }
        }
        public ActionExecutionResult Tick()
        {
            if (action == null) return Result;
            try
            {
                if (clock.Now >= deadline) return Finish(ActionStatus.TimedOut, "Action deadline exceeded.");
                if (cancellationRequested && clock.Now >= cancelAt) return Finish(ActionStatus.Cancelled, "Cancellation deadline exceeded.");
                if (!channels.Owns(owner, channelKeys) || !leases.Owns(owner, resourceKeys))
                    return Finish(ActionStatus.Failed, "Ownership expired or was lost.");
                if (!channels.TryAcquire(owner, channelKeys, leaseDuration) || !leases.TryAcquire(owner, resourceKeys, leaseDuration))
                    return Finish(ActionStatus.Failed, "Ownership renewal failed.");
                var tick = action.Tick(context!);
                if (action == null) return Result;
                if (clock.Now >= deadline) return Finish(ActionStatus.TimedOut, "Action deadline exceeded.");
                if (cancellationRequested && clock.Now >= cancelAt) return Finish(ActionStatus.Cancelled, "Cancellation deadline exceeded.");
                if (!channels.Owns(owner, channelKeys) || !leases.Owns(owner, resourceKeys))
                    return Finish(ActionStatus.Failed, "Ownership expired or was lost.");
                if (tick.Status == ActionStatus.Running)
                    return Result = new ActionExecutionResult(cancellationRequested ? ActionStatus.Cancelling : ActionStatus.Running, tick.Reason);
                if (cancellationRequested) return Finish(ActionStatus.Cancelled, tick.Reason);
                if (tick.Status == ActionStatus.Succeeded)
                {
                    var verified = effects.All(context!.VerifyEffect);
                    if (action == null) return Result;
                    if (clock.Now >= deadline) return Finish(ActionStatus.TimedOut, "Action deadline exceeded.");
                    if (cancellationRequested) return Finish(ActionStatus.Cancelled, "Cancellation requested.");
                    if (!verified) return Finish(ActionStatus.Failed, "Expected effects were not verified.");
                    return Finish(ActionStatus.Succeeded, tick.Reason, effects);
                }
                if (tick.Status != ActionStatus.Failed && tick.Status != ActionStatus.Cancelled)
                    return Finish(ActionStatus.Failed, "Invalid tick status.");
                return Finish(tick.Status, tick.Reason);
            }
            catch (Exception ex) { return Finish(ActionStatus.Failed, "Tick: " + ex.Message); }
        }
        public ActionExecutionResult RequestCancel()
        {
            if (action == null || cancellationRequested) return Result;
            cancellationRequested = true; cancelAt = Math.Min(deadline, clock.Now + cancelDelay);
            Result = new ActionExecutionResult(ActionStatus.Cancelling, "Cancellation requested.");
            try
            {
                action.RequestCancel(context!);
                if (action == null) return Result;
                return clock.Now >= cancelAt ? Finish(ActionStatus.Cancelled, "Cancellation deadline exceeded.") : Result;
            }
            catch (Exception ex) { return Finish(ActionStatus.Failed, "Cancel: " + ex.Message); }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (action != null)
            {
                RequestCancel();
                if (action != null) Finish(ActionStatus.Cancelled, "Runner disposed.");
            }
        }
        private ActionExecutionResult Finish(ActionStatus status, string reason, IEnumerable<Fact>? verified = null)
        {
            var finishingAction = action;
            var finishingOwner = owner;
            var finishingContext = context;
            finishing = true;
            action = null;
            try { finishingAction?.Cleanup(finishingContext!); }
            catch (Exception ex) { status = ActionStatus.Failed; reason = "Cleanup: " + ex.Message; verified = null; }
            finally { channels.ReleaseOwner(finishingOwner); leases.ReleaseOwner(finishingOwner); finishing = false; }
            return Result = new ActionExecutionResult(status, reason, verified);
        }
    }
}
