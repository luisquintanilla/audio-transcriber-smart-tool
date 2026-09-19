using System.Globalization;
using AudioTranscriber.TranscriptProcessing;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.FoundryLocal;

/// <summary>
/// Implements chapter enrichment and chapter-summary-only overall summaries.
/// </summary>
public sealed class FoundryLocalEnrichmentProvider
    : ITranscriptChapterEnricher, ITranscriptOverallSummaryAssembler, IAsyncDisposable
{
    private readonly FoundryLocalEnrichmentOptions options;
    private readonly IFoundryLocalRuntime runtime;
    private readonly object lifecycleGate = new();
    private readonly TaskCompletionSource disposalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FoundryLocalRuntimeSession? session;
    private Task<FoundryLocalRuntimeSession>? sessionInitialization;
    private int activeOperations;
    private bool disposeRequested;
    private bool finalizationStarted;

    public FoundryLocalEnrichmentProvider(
        FoundryLocalEnrichmentOptions options,
        IFoundryLocalRuntime runtime)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.options.Validate();
    }

    public TranscriptChapterEnrichmentOptions CreateProcessingOptions(
        TranscriptChapterEnrichmentFailurePolicy failurePolicy =
            TranscriptChapterEnrichmentFailurePolicy.FailFast,
        bool includeOverallSummary = false)
    {
        options.Validate();
        return new TranscriptChapterEnrichmentOptions
        {
            Algorithm = "chapter-enrichment-v1",
            Provider = "foundry-local",
            Model = options.ModelAlias,
            ProviderConfiguration = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["responseSchemaVersion"] =
                    FoundryLocalPromptBuilder.ResponseSchemaVersion,
                ["modelDownloadPolicy"] = options.AllowModelDownload
                    ? "explicit-opt-in"
                    : "disabled",
                ["executionPolicy"] = "in-process-local",
                ["requestTimeout"] = options.RequestTimeout.ToString(
                    "c",
                    CultureInfo.InvariantCulture),
                ["maxOutputTokens"] = options.MaxOutputTokens.ToString(
                    CultureInfo.InvariantCulture),
                ["temperature"] = options.Temperature.ToString(
                    "R",
                    CultureInfo.InvariantCulture)
            },
            FailurePolicy = failurePolicy,
            IncludeOverallSummary = includeOverallSummary
        };
    }

    public async ValueTask<TranscriptChapterSummary?> EnrichAsync(
        TranscriptChapterEnrichmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var operation = await AcquireOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var runtimeSession = operation.Session;
        var response = await GetResponseAsync(
                runtimeSession,
                FoundryLocalPromptBuilder.BuildChapterPrompt(request.Chapter),
                cancellationToken)
            .ConfigureAwait(false);
        return FoundryLocalResponseParser.ParseChapterSummary(
            response.Text,
            request.Chapter);
    }

    public async ValueTask<TranscriptOverallSummary?> AssembleAsync(
        TranscriptOverallSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var operation = await AcquireOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var runtimeSession = operation.Session;
        var response = await GetResponseAsync(
                runtimeSession,
                FoundryLocalPromptBuilder.BuildOverallPrompt(request),
                cancellationToken)
            .ConfigureAwait(false);
        return FoundryLocalResponseParser.ParseOverallSummary(
            response.Text,
            request);
    }

    public async ValueTask DisposeAsync()
    {
        var startFinalization = false;
        try
        {
            lock (lifecycleGate)
            {
                disposeRequested = true;
                if (!finalizationStarted && activeOperations == 0)
                {
                    finalizationStarted = true;
                    startFinalization = true;
                }
            }

            if (startFinalization)
            {
                _ = FinalizeDisposeAsync();
            }

            await disposalCompletion.Task.ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    private async Task<FoundryLocalOperationLease> AcquireOperationAsync(
        CancellationToken cancellationToken)
    {
        Task<FoundryLocalRuntimeSession>? initialization;
        lock (lifecycleGate)
        {
            if (disposeRequested)
            {
                throw new ObjectDisposedException(GetType().Name);
            }

            activeOperations++;
            if (session is not null)
            {
                return new FoundryLocalOperationLease(
                    session,
                    ReleaseOperation);
            }

            initialization = sessionInitialization ??= InitializeSessionAsync(
                cancellationToken);
        }

        try
        {
            var prepared = await initialization
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new FoundryLocalOperationLease(
                prepared,
                ReleaseOperation);
        }
        catch
        {
            ReleaseOperation();
            throw;
        }
    }

    private async Task<FoundryLocalRuntimeSession> InitializeSessionAsync(
        CancellationToken cancellationToken)
    {
        var prepared = await runtime
            .PrepareAsync(options, cancellationToken)
            .ConfigureAwait(false);
        lock (lifecycleGate)
        {
            session ??= prepared;
            return session;
        }
    }

    private CancellationTokenSource CreateRequestCancellation(
        CancellationToken cancellationToken)
    {
        var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(options.RequestTimeout);
        return timeoutCancellation;
    }

    private async Task<ChatResponse> GetResponseAsync(
        FoundryLocalRuntimeSession runtimeSession,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CreateRequestCancellation(
            cancellationToken);
        try
        {
            return await runtimeSession.ChatClient
                .GetResponseAsync(
                    messages,
                    CreateChatOptions(runtimeSession.ModelId),
                    timeoutCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Foundry Local did not respond within {options.RequestTimeout}.");
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private ChatOptions CreateChatOptions(string modelId) =>
        new()
        {
            ModelId = modelId,
            Temperature = (float)options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            ResponseFormat = ChatResponseFormat.Json
        };

    private void ReleaseOperation()
    {
        var startFinalization = false;
        lock (lifecycleGate)
        {
            activeOperations--;
            if (disposeRequested &&
                activeOperations == 0 &&
                !finalizationStarted)
            {
                finalizationStarted = true;
                startFinalization = true;
            }
        }

        if (startFinalization)
        {
            _ = FinalizeDisposeAsync();
        }
    }

    private async Task FinalizeDisposeAsync()
    {
        try
        {
            Task<FoundryLocalRuntimeSession>? initialization;
            lock (lifecycleGate)
            {
                initialization = sessionInitialization;
            }

            if (initialization is not null)
            {
                try
                {
                    await initialization.ConfigureAwait(false);
                }
                catch
                {
                    // A failed initialization leaves no runtime session to dispose.
                }
            }

            FoundryLocalRuntimeSession? current;
            lock (lifecycleGate)
            {
                current = session;
                session = null;
            }

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }

            disposalCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            disposalCompletion.TrySetException(exception);
        }
    }

    private sealed class FoundryLocalOperationLease : IAsyncDisposable
    {
        private readonly Action release;
        private int released;

        public FoundryLocalOperationLease(
            FoundryLocalRuntimeSession session,
            Action release)
        {
            Session = session;
            this.release = release;
        }

        public FoundryLocalRuntimeSession Session { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
