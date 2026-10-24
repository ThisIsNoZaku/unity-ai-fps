#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoalDrivenAI.Language
{
    public interface ICommandContextSource
    {
        CommandContext Capture(string issuer, IReadOnlyList<string> recipients);
        bool IsCurrent(CommandContext snapshot);
    }
    public interface ICommandPlanSink
    {
        // Must atomically validate authority and accept the entire plan, or leave existing orders unchanged.
        bool TryCommit(ValidatedCommandPlan plan, out string reason);
    }
    public interface IMainThreadScheduler { bool IsMainThread { get; } }
    public sealed class CapturedThreadScheduler : IMainThreadScheduler
    {
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == threadId;
    }
    public enum InterpretationStatus { Pending, Accepted, Rejected, Cancelled, Stale, Expired, NeedsClarification, Unsupported, ProviderError, ProviderFailed }
    public sealed class InterpretationOutcome
    {
        public long Generation { get; }
        public InterpretationStatus Status { get; }
        public string? PlanHash { get; }
        public string Detail { get; }
        public CommandContext? Context { get; }
        public IReadOnlyList<string> CandidateHandles { get; }
        public InterpretationOutcome(long generation, InterpretationStatus status, string? hash, string detail,
            CommandContext? context = null, IReadOnlyList<string>? candidates = null)
        { Generation = generation; Status = status; PlanHash = hash; Detail = detail; Context = context;
            CandidateHandles = Array.AsReadOnly((candidates ?? Array.Empty<string>()).ToArray()); }
    }
    public sealed class CommandInterpretationService : IDisposable
    {
        private sealed class Pending
        {
            internal long Generation;
            internal CommandContext Context = null!;
            internal LanguageModelRequest Request = null!;
            internal CancellationTokenSource Cancellation = null!;
            internal Task<ModelResult> Task = null!;
            internal bool Repaired;
        }
        private readonly IClock clock;
        private readonly ICommandContextSource contexts;
        private readonly ILanguageCommandModel model;
        private readonly PlanCompiler compiler;
        private readonly ICommandPlanSink sink;
        private readonly IMainThreadScheduler scheduler;
        private readonly IPlanJournal journal;
        private readonly bool allowRepair;
        private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.Ordinal);
        private readonly Queue<InterpretationOutcome> outcomes = new Queue<InterpretationOutcome>();
        private readonly HashSet<string> accepted = new HashSet<string>(StringComparer.Ordinal);
        private long generation;
        private bool disposed;
        public IReadOnlyList<InterpretationOutcome> Outcomes => new ReadOnlyCollection<InterpretationOutcome>(outcomes.ToArray());

        public CommandInterpretationService(IClock clock, ICommandContextSource contexts, ILanguageCommandModel model,
            PlanCompiler compiler, ICommandPlanSink sink, IMainThreadScheduler scheduler, IPlanJournal journal, bool allowRepair = true)
        {
            this.clock = clock; this.contexts = contexts; this.model = model; this.compiler = compiler;
            this.sink = sink; this.scheduler = scheduler; this.journal = journal; this.allowRepair = allowRepair;
        }
        public long Begin(FinalTranscript transcript, string issuer, IReadOnlyList<string> recipients, double timeout = 2.5, string? requestId = null)
        {
            RequireMainThread();
            if (!OrderDefinition.Finite(timeout) || timeout <= 0 || timeout > 300) throw new ArgumentOutOfRangeException(nameof(timeout));
            var context = contexts.Capture(issuer, recipients).WithRequestId(requestId ?? Guid.NewGuid().ToString("N"));
            if (pending.Count >= 32 && !pending.ContainsKey(context.SelectionKey))
                throw new InvalidOperationException("Too many active interpretations.");
            if (pending.TryGetValue(context.SelectionKey, out var previous))
                Finish(previous, InterpretationStatus.Cancelled, "Replaced.");
            var request = new LanguageModelRequest(transcript.Text, context, clock.Now + timeout, catalog: compiler.Catalog);
            var item = new Pending { Generation = ++generation, Context = context, Request = request,
                Cancellation = new CancellationTokenSource() };
            pending.Add(context.SelectionKey, item);
            item.Task = CallModel(request, item.Cancellation.Token);
            return item.Generation;
        }
        private async Task<ModelResult> CallModel(LanguageModelRequest request, CancellationToken token)
        {
            try
            {
                if (!model.GetCapabilities().Available) return new ModelResult(ModelStatus.Unavailable);
                return await model.InterpretAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return new ModelResult(ModelStatus.Cancelled); }
            catch (Exception) { return new ModelResult(ModelStatus.Failed); }
        }
        public void Cancel(string issuer, IReadOnlyList<string> recipients)
        {
            RequireMainThread();
            var key = issuer + ":" + string.Join(",", recipients.OrderBy(x => x, StringComparer.Ordinal));
            if (pending.TryGetValue(key, out var item)) Finish(item, InterpretationStatus.Cancelled, "Cancelled.");
        }
        // No task continuation touches Unity, contexts, the clock, or the sink.
        public void Pump()
        {
            RequireMainThread();
            foreach (var item in pending.Values.ToArray())
            {
                if (!pending.TryGetValue(item.Context.SelectionKey, out var active) || active != item) continue;
                if (clock.Now >= item.Request.Deadline) { Finish(item, InterpretationStatus.Expired, "Deadline."); continue; }
                if (!contexts.IsCurrent(item.Context)) { Finish(item, InterpretationStatus.Stale, "Context changed."); continue; }
                if (item.Cancellation.IsCancellationRequested) { Finish(item, InterpretationStatus.Cancelled, "Cancelled."); continue; }
                if (!item.Task.IsCompleted) continue;
                var result = item.Task.GetAwaiter().GetResult();
                if (result.Status != ModelStatus.Success)
                {
                    var status = result.Status == ModelStatus.NeedsClarification ? InterpretationStatus.NeedsClarification :
                        result.Status == ModelStatus.Unsupported ? InterpretationStatus.Unsupported : InterpretationStatus.ProviderError;
                    Finish(item, status, result.Status.ToString(), candidates:
                        result.CandidateHandles.Where(h => h != null && item.Context.Handles.ContainsKey(h)).ToArray());
                    continue;
                }
                var compiled = compiler.Compile(result.Json ?? "", item.Context);
                if (!compiled.Success)
                {
                    if (allowRepair && !item.Repaired)
                    {
                        item.Repaired = true;
                        item.Request = new LanguageModelRequest(item.Request.Transcript, item.Context, item.Request.Deadline,
                            compiled.Error, compiler.Catalog);
                        item.Task = CallModel(item.Request, item.Cancellation.Token);
                    }
                    else Finish(item, InterpretationStatus.Rejected, compiled.Error ?? "Invalid proposal.");
                    continue;
                }
                // Check again immediately before the only authoritative side effect.
                if (clock.Now >= item.Request.Deadline) { Finish(item, InterpretationStatus.Expired, "Deadline."); continue; }
                if (!contexts.IsCurrent(item.Context)) { Finish(item, InterpretationStatus.Stale, "Context changed."); continue; }
                var plan = compiled.Plan!;
                if (accepted.Contains(plan.Context.RequestKey)) { Finish(item, InterpretationStatus.Rejected, "Already accepted."); continue; }
                if (accepted.Count >= 4096) { Finish(item, InterpretationStatus.Rejected, "Request ledger capacity."); continue; }
                if (sink.TryCommit(plan, out var reason))
                {
                    accepted.Add(plan.Context.RequestKey);
                    Finish(item, InterpretationStatus.Accepted, "", plan.Hash);
                    journal.RecordAccepted(plan);
                }
                else Finish(item, InterpretationStatus.Rejected, reason);
            }
        }
        private void Finish(Pending item, InterpretationStatus status, string detail, string? hash = null, IReadOnlyList<string>? candidates = null)
        {
            if (pending.TryGetValue(item.Context.SelectionKey, out var active) && active == item)
                pending.Remove(item.Context.SelectionKey);
            item.Cancellation.Cancel(); item.Cancellation.Dispose();
            while (outcomes.Count >= 128) outcomes.Dequeue();
            outcomes.Enqueue(new InterpretationOutcome(item.Generation, status, hash,
                detail.Substring(0, Math.Min(detail.Length, 256)), item.Context, candidates));
        }
        private void RequireMainThread()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CommandInterpretationService));
            if (!scheduler.IsMainThread) throw new InvalidOperationException("Use the main-thread pump.");
        }
        public void Dispose()
        {
            RequireMainThread();
            foreach (var item in pending.Values.ToArray()) Finish(item, InterpretationStatus.Cancelled, "Disposed.");
            disposed = true;
        }
    }
}
