#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GoalDrivenAI.Language
{
    // Share one wrapper across issuers. Cancellation never releases an active provider's permit early.
    public sealed class ScheduledCommandModel : ILanguageCommandModel
    {
        private readonly ILanguageCommandModel provider;
        private readonly SemaphoreSlim permits;
        private readonly int capacity;
        private int outstanding;
        public ScheduledCommandModel(ILanguageCommandModel provider, int concurrency = 2, int maxQueued = 16)
        {
            if (concurrency < 1 || concurrency > 32 || maxQueued < 0 || maxQueued > 1024)
                throw new ArgumentOutOfRangeException(nameof(concurrency));
            this.provider = provider; permits = new SemaphoreSlim(concurrency, concurrency);
            capacity = concurrency + maxQueued;
        }
        public ModelCapabilities GetCapabilities() => provider.GetCapabilities();
        public async Task<ModelResult> InterpretAsync(LanguageModelRequest request, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return new ModelResult(ModelStatus.Cancelled);
            if (Interlocked.Increment(ref outstanding) > capacity)
            {
                Interlocked.Decrement(ref outstanding);
                return new ModelResult(ModelStatus.Unavailable, detail: "Inference queue is full.");
            }
            try { await permits.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Interlocked.Decrement(ref outstanding);
                return new ModelResult(ModelStatus.Cancelled);
            }
            var running = RunProvider(request, cancellationToken);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            if (await Task.WhenAny(running, cancelled.Task).ConfigureAwait(false) != running)
                return new ModelResult(ModelStatus.Cancelled);
            var result = await running.ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested ? new ModelResult(ModelStatus.Cancelled) : result;
        }
        private async Task<ModelResult> RunProvider(LanguageModelRequest request, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await provider.InterpretAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return new ModelResult(ModelStatus.Cancelled); }
            catch (Exception) { return new ModelResult(ModelStatus.ProviderError); }
            finally { permits.Release(); Interlocked.Decrement(ref outstanding); }
        }
    }
}
