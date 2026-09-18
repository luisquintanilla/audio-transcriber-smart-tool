using System.Globalization;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

/// <summary>
/// Implements chapter enrichment and chapter-summary-only overall summaries.
/// </summary>
public sealed class FoundryLocalEnrichmentProvider
    : ITranscriptChapterEnricher, ITranscriptOverallSummaryAssembler, IAsyncDisposable
{
    private readonly FoundryLocalEnrichmentOptions options;
    private readonly IFoundryLocalRuntime runtime;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private FoundryLocalRuntimeSession? session;

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
        var runtimeSession = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var chatRequest = new FoundryLocalChatRequest(
            runtimeSession.ModelId,
            FoundryLocalPromptBuilder.BuildChapterPrompt(request.Chapter),
            options.Temperature,
            options.MaxOutputTokens);
        var response = await runtimeSession.ChatClient
            .CompleteAsync(
                chatRequest,
                options.RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return FoundryLocalResponseParser.ParseChapterSummary(
            response,
            request.Chapter);
    }

    public async ValueTask<TranscriptOverallSummary?> AssembleAsync(
        TranscriptOverallSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runtimeSession = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        var chatRequest = new FoundryLocalChatRequest(
            runtimeSession.ModelId,
            FoundryLocalPromptBuilder.BuildOverallPrompt(request),
            options.Temperature,
            options.MaxOutputTokens);
        var response = await runtimeSession.ChatClient
            .CompleteAsync(
                chatRequest,
                options.RequestTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return FoundryLocalResponseParser.ParseOverallSummary(response, request);
    }

    public async ValueTask DisposeAsync()
    {
        FoundryLocalRuntimeSession? current;
        await sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            current = session;
            session = null;
        }
        finally
        {
            sessionGate.Release();
        }

        if (current is not null)
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }

        sessionGate.Dispose();
    }

    private async Task<FoundryLocalRuntimeSession> GetSessionAsync(
        CancellationToken cancellationToken)
    {
        if (session is not null)
        {
            return session;
        }

        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session ??= await runtime
                .PrepareAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        finally
        {
            sessionGate.Release();
        }
    }
}
