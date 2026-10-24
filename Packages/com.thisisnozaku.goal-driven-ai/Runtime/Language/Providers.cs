#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoalDrivenAI.Language
{
    public enum ModelStatus { Success, NeedsClarification, Unsupported, ProviderError, Unavailable, Disabled, Timeout, Cancelled, InvalidOutput, Failed }
    public sealed class ModelCapabilities
    {
        public bool Available { get; }
        public bool IsCloud { get; }
        public int MaxOutputCharacters { get; }
        public ModelCapabilities(bool available, bool isCloud, int maxOutputCharacters = PlanCompiler.MaxOutputCharacters)
        {
            if (maxOutputCharacters <= 0 || maxOutputCharacters > PlanCompiler.MaxOutputCharacters)
                throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
            Available = available; IsCloud = isCloud; MaxOutputCharacters = maxOutputCharacters;
        }
    }
    public sealed class LanguageModelRequest
    {
        public string Transcript { get; }
        public CommandContext Context { get; }
        public string RequestId => Context.RequestId;
        public double Deadline { get; }
        public string? RepairError { get; }
        public CommandCatalog? Catalog { get; }
        public int SchemaVersion => ValidatedCommandPlan.SchemaVersion;
        public LanguageModelRequest(string transcript, CommandContext context, double deadline, string? repairError = null,
            CommandCatalog? catalog = null)
        {
            if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 4096 ||
                !OrderDefinition.Finite(deadline)) throw new ArgumentException("Invalid request.");
            Transcript = transcript; Context = context; Deadline = deadline;
            Catalog = catalog;
            RepairError = repairError == null ? null : repairError.Substring(0, Math.Min(512, repairError.Length));
        }
    }
    public sealed class ModelResult
    {
        public ModelStatus Status { get; }
        public string? Json { get; }
        public string? Detail { get; }
        public IReadOnlyList<string> CandidateHandles { get; }
        public ModelResult(ModelStatus status, string? json = null, string? detail = null, IEnumerable<string>? candidateHandles = null)
        {
            Status = status; Json = status == ModelStatus.Success ? json : null;
            Detail = detail == null ? null : detail.Substring(0, Math.Min(256, detail.Length));
            CandidateHandles = Array.AsReadOnly((candidateHandles ?? Array.Empty<string>()).Take(8).ToArray());
        }
    }
    public interface ILanguageCommandModel
    {
        ModelCapabilities GetCapabilities();
        Task<ModelResult> InterpretAsync(LanguageModelRequest request, CancellationToken cancellationToken);
    }
    public sealed class FinalTranscript
    {
        public string Text { get; }
        public FinalTranscript(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new ArgumentException("Invalid final transcript.");
            Text = text;
        }
    }
    public interface ISpeechRecognizer
    {
        // Implementations must not publish interim hypotheses through this contract.
        Task<FinalTranscript?> RecognizeFinalAsync(CancellationToken cancellationToken);
    }
    public sealed class FixedCommandModel : ILanguageCommandModel
    {
        private readonly ModelResult result;
        public FixedCommandModel(string json) { result = new ModelResult(ModelStatus.Success, json); }
        public FixedCommandModel(ModelResult result) { this.result = result; }
        public ModelCapabilities GetCapabilities() => new ModelCapabilities(true, false);
        public Task<ModelResult> InterpretAsync(LanguageModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(cancellationToken.IsCancellationRequested ? new ModelResult(ModelStatus.Cancelled) : result);
    }
    public interface ILanguageModelTransport
    {
        bool IsHealthy { get; }
        // Transport must enforce the limit while reading, not after buffering an unbounded body.
        Task<ModelResult> SendAsync(LanguageModelRequest request, int maximumOutputCharacters, CancellationToken cancellationToken);
    }
    public abstract class TransportModelProvider : ILanguageCommandModel
    {
        private readonly ILanguageModelTransport transport;
        private readonly TimeSpan timeout;
        protected abstract bool Enabled { get; }
        protected abstract bool Cloud { get; }
        protected TransportModelProvider(ILanguageModelTransport transport, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
            this.transport = transport; this.timeout = timeout;
        }
        public ModelCapabilities GetCapabilities() => new ModelCapabilities(Enabled && transport.IsHealthy, Cloud);
        public async Task<ModelResult> InterpretAsync(LanguageModelRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return new ModelResult(ModelStatus.Cancelled);
            if (!Enabled) return new ModelResult(ModelStatus.Disabled);
            if (!transport.IsHealthy) return new ModelResult(ModelStatus.Unavailable);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timerCancellation = new CancellationTokenSource();
            try
            {
                var send = transport.SendAsync(request, PlanCompiler.MaxOutputCharacters, linked.Token);
                var delay = Task.Delay(timeout, timerCancellation.Token);
                var cancelled = Task.Delay(Timeout.Infinite, linked.Token);
                var winner = await Task.WhenAny(send, delay, cancelled).ConfigureAwait(false);
                if (winner != send)
                {
                    linked.Cancel();
                    _ = send.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    return new ModelResult(cancellationToken.IsCancellationRequested ? ModelStatus.Cancelled : ModelStatus.Timeout);
                }
                timerCancellation.Cancel();
                var result = await send.ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) return new ModelResult(ModelStatus.Cancelled);
                if (result.Status == ModelStatus.Success &&
                    (string.IsNullOrWhiteSpace(result.Json) || result.Json!.Length > PlanCompiler.MaxOutputCharacters))
                    return new ModelResult(ModelStatus.InvalidOutput);
                return result;
            }
            catch (OperationCanceledException) { return new ModelResult(cancellationToken.IsCancellationRequested ? ModelStatus.Cancelled : ModelStatus.Timeout); }
            catch (Exception) { return new ModelResult(ModelStatus.Failed, detail: "Transport failed."); }
            finally { timerCancellation.Cancel(); linked.Cancel(); }
        }
    }
    public sealed class LocalModelProvider : TransportModelProvider
    {
        protected override bool Enabled => true;
        protected override bool Cloud => false;
        public LocalModelProvider(ILanguageModelTransport transport, TimeSpan timeout) : base(transport, timeout) { }
    }
    public sealed class CloudModelProvider : TransportModelProvider
    {
        private readonly bool optedIn;
        private readonly bool backendTransport;
        protected override bool Enabled => optedIn && backendTransport;
        protected override bool Cloud => true;
        // Only an application backend transport belongs here. Provider keys never belong in the client.
        public CloudModelProvider(ILanguageModelTransport backend, TimeSpan timeout, bool explicitOptIn, bool isApplicationBackend)
            : base(backend, timeout) { optedIn = explicitOptIn; backendTransport = isApplicationBackend; }
    }
}
