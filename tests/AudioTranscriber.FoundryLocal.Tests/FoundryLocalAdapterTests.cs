using System.Text.Json;
using AudioTranscriber.FoundryLocal;
using AudioTranscriber.TranscriptProcessing;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.FoundryLocal.Tests;

public sealed class FoundryLocalAdapterTests
{
    [Fact]
    public void Prompts_are_stable_and_overall_prompt_uses_chapter_summaries_only()
    {
        var artifact = CreateArtifact(
            Segment(
                "first source",
                0,
                1,
                0,
                "segment-first",
                "source-first"),
            Segment(
                "second source",
                1,
                2,
                1,
                "segment-second",
                "source-second"));
        var chapter = artifact[0];
        var chapterPrompt = FoundryLocalPromptBuilder.BuildChapterPrompt(chapter);

        Assert.Equal(ChatRole.System, chapterPrompt[0].Role);
        Assert.Equal(ChatRole.User, chapterPrompt[1].Role);
        Assert.Contains(chapter.Id, chapterPrompt[1].Text);
        Assert.Contains("segment-first", chapterPrompt[1].Text);
        Assert.Contains("source-first", chapterPrompt[1].Text);
        Assert.Contains("00:00:01", chapterPrompt[1].Text);

        var overallRequest = new TranscriptOverallSummaryRequest(
            [
                new TranscriptChapterSummaryReference(
                    "chapter-one",
                    new TranscriptChapterSummary(
                        "first summary",
                        ["topic"])),
                new TranscriptChapterSummaryReference(
                    "chapter-two",
                    new TranscriptChapterSummary("second summary"))
            ],
            isPartial: false);
        var overallPrompt = FoundryLocalPromptBuilder.BuildOverallPrompt(overallRequest);

        Assert.Contains("chapter-one", overallPrompt[1].Text);
        Assert.Contains("first summary", overallPrompt[1].Text);
        Assert.DoesNotContain("first source", overallPrompt[1].Text);
        Assert.DoesNotContain("second source", overallPrompt[1].Text);
    }

    [Fact]
    public void Parser_preserves_exact_evidence_and_rejects_unknown_segments()
    {
        var artifact = CreateArtifact(
            Segment(
                "source",
                0,
                1,
                0,
                "segment-source",
                "source-id"));
        var chapter = Assert.Single(artifact);
        var response = JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1.0",
                kind = "chapter",
                chapterId = chapter.Id,
                summary = "A source-linked summary.",
                title = "A title",
                keywords = new[] { "zeta", "alpha" },
                evidence = new[]
                {
                    new
                    {
                        sourceSegmentId = "segment-source",
                        sourceId = "source-id",
                        start = "00:00:00.0000000",
                        end = "00:00:01.0000000"
                    }
                }
            });

        var parsed = FoundryLocalResponseParser.ParseChapterSummary(
            response,
            chapter);

        Assert.Equal("A source-linked summary.", parsed.Summary);
        Assert.Equal(["alpha", "zeta"], parsed.Keywords);
        var evidence = Assert.Single(parsed.Evidence);
        Assert.Equal("segment-source", evidence.SourceSegmentId);
        Assert.Equal("source-id", evidence.SourceId);
        Assert.Equal(TimeSpan.Zero, evidence.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), evidence.End);

        var invalid = response.Replace(
            "\"sourceSegmentId\":\"segment-source\"",
            "\"sourceSegmentId\":\"not-a-source\"",
            StringComparison.Ordinal);
        var exception = Assert.Throws<FoundryLocalResponseException>(
            () => FoundryLocalResponseParser.ParseChapterSummary(invalid, chapter));
        Assert.Equal("unknown_evidence_segment", exception.Code);
    }

    [Fact]
    public async Task Provider_passes_explicit_model_timeout_and_provenance()
    {
        var artifact = CreateArtifact(
            Segment("source", 0, 1, 0, "segment-source"));
        var chat = new FakeChatClient(
            (_, _, _) =>
            {
                var chapter = Assert.Single(artifact);
                return Task.FromResult(
                    ChatResponseFor(ChapterResponse(chapter, "summary")));
            });
        var runtime = new FakeRuntime(chat);
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model")
            {
                RequestTimeout = TimeSpan.FromSeconds(9),
                MaxOutputTokens = 321,
                Temperature = 0.25
            },
            runtime);

        var options = provider.CreateProcessingOptions(
            TranscriptChapterEnrichmentFailurePolicy.PreservePartial,
            includeOverallSummary: true);
        var summary = await provider.EnrichAsync(
            new TranscriptChapterEnrichmentRequest(Assert.Single(artifact)));

        Assert.Equal("summary", summary!.Summary);
        Assert.Equal(1, runtime.PrepareCount);
        var chatOptions = Assert.Single(chat.Options);
        Assert.NotNull(chatOptions);
        Assert.Equal("model-id", chatOptions.ModelId);
        Assert.Equal(321, chatOptions.MaxOutputTokens);
        Assert.Equal(0.25f, chatOptions.Temperature);
        Assert.Equal("foundry-local", options.Provider);
        Assert.Equal("chosen-model", options.Model);
        Assert.Equal(
            "disabled",
            options.ProviderConfiguration!["modelDownloadPolicy"]);
        Assert.Equal(
            "in-process-local",
            options.ProviderConfiguration!["executionPolicy"]);

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task PreservePartial_keeps_successes_and_overall_summary_uses_successful_ids()
    {
        var artifact = CreateArtifact(
            Segment("first", 0, 1, 0, "segment-first"),
            Segment("second", 1, 2, 1, "segment-second"),
            Segment("third", 2, 3, 2, "segment-third"));
        var chat = new FakeChatClient(
            (request, _, _) =>
            {
                if (request[1].Text.Contains(
                        $"chapterId: {artifact[1].Id}",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated provider failure");
                }

                if (request[1].Text.Contains(
                        "chapterSummaries:",
                        StringComparison.Ordinal))
                {
                    return Task.FromResult(
                        ChatResponseFor(
                            OverallResponse(
                                [artifact[0].Id, artifact[2].Id],
                                "partial overall")));
                }

                var chapter = artifact.Single(
                    candidate => request[1].Text.Contains(
                        $"chapterId: {candidate.Id}",
                        StringComparison.Ordinal));
                return Task.FromResult(
                    ChatResponseFor(
                        ChapterResponse(
                            chapter,
                            $"summary-{chapter.Id}")));
            });
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model"),
            new FakeRuntime(chat));

        var document = await new TranscriptChapterEnrichmentOrchestrator(
                provider,
                provider)
            .EnrichAsync(
                artifact,
                provider.CreateProcessingOptions(
                    TranscriptChapterEnrichmentFailurePolicy.PreservePartial,
                    includeOverallSummary: true));

        Assert.Equal(
            [
                TranscriptChapterEnrichmentStatus.Succeeded,
                TranscriptChapterEnrichmentStatus.Failed,
                TranscriptChapterEnrichmentStatus.Succeeded
            ],
            document.Select(chapter => chapter.Status));
        Assert.Equal("provider_failure", document[1].Failure!.Code);
        Assert.Equal("partial overall", document.OverallSummary!.Summary);
        Assert.Equal(
            [artifact[0].Id, artifact[2].Id],
            document.OverallSummary.ChapterIds);

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task FailFast_surfaces_provider_failure_without_fallback()
    {
        var artifact = CreateArtifact(
            Segment("first", 0, 1, 0, "segment-first"),
            Segment("second", 1, 2, 1, "segment-second"));
        var chat = new FakeChatClient(
            (request, _, _) =>
            {
                if (request[1].Text.Contains(
                        $"chapterId: {artifact[1].Id}",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("simulated provider failure");
                }

                var chapter = artifact.Single(
                    candidate => request[1].Text.Contains(
                        $"chapterId: {candidate.Id}",
                        StringComparison.Ordinal));
                return Task.FromResult(
                    ChatResponseFor(ChapterResponse(chapter, "first summary")));
            });
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model"),
            new FakeRuntime(chat));

        var exception = await Assert.ThrowsAsync<TranscriptChapterEnrichmentException>(
            () => new TranscriptChapterEnrichmentOrchestrator(provider)
                .EnrichAsync(
                    artifact,
                    provider.CreateProcessingOptions()));

        Assert.Equal(artifact[1].Id, exception.ChapterId);
        Assert.Equal("provider_failure", exception.Code);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_the_chat_client()
    {
        var artifact = CreateArtifact(
            Segment("blocking", 0, 1, 0, "segment-blocking"));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new FakeChatClient(
            async (_, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ChatResponseFor(string.Empty);
            });
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model"),
            new FakeRuntime(chat));
        using var cancellation = new CancellationTokenSource();

        var task = provider.EnrichAsync(
            new TranscriptChapterEnrichmentRequest(Assert.Single(artifact)),
            cancellation.Token)
            .AsTask();
        await started.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_initialization_waiter_does_not_poison_later_retry()
    {
        var artifact = CreateArtifact(
            Segment("retry", 0, 1, 0, "segment-retry"));
        var chat = new FakeChatClient(
            (_, _, _) => Task.FromResult(
                ChatResponseFor(ChapterResponse(artifact[0], "retried"))));
        var runtime = new RetryingRuntime(chat);
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model"),
            runtime);
        using var cancellation = new CancellationTokenSource();

        var first = provider.EnrichAsync(
            new TranscriptChapterEnrichmentRequest(artifact[0]),
            cancellation.Token)
            .AsTask();
        await runtime.FirstAttemptStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        runtime.ReleaseFirstAttempt.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runtime.FirstAttemptTask!);

        var retried = await provider.EnrichAsync(
            new TranscriptChapterEnrichmentRequest(artifact[0]));

        Assert.Equal("retried", retried!.Summary);
        Assert.Equal(2, runtime.PrepareCount);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_waits_for_inflight_operations_and_is_idempotent()
    {
        var artifact = CreateArtifact(
            Segment("blocking", 0, 1, 0, "segment-blocking"));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new FakeChatClient(
            (_, _, _) =>
            {
                started.SetResult();
                return release.Task;
            });
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("chosen-model"),
            new FakeRuntime(chat));

        var enrichment = provider.EnrichAsync(
            new TranscriptChapterEnrichmentRequest(Assert.Single(artifact)))
            .AsTask();
        await started.Task;

        var disposal = provider.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(
            disposal,
            Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(disposal, completed);

        release.SetResult(
            ChatResponseFor(
                ChapterResponse(
                    Assert.Single(artifact),
                    "completed after disposal request")));
        await enrichment;
        await disposal;
        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.EnrichAsync(
                    new TranscriptChapterEnrichmentRequest(Assert.Single(artifact)))
                .AsTask());
    }

    [Fact]
    public void Cache_directory_validation_rejects_package_root_and_descendants()
    {
        var packageRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var descendant = Path.Combine(packageRoot, "external-cache");
        var unrelated = Path.Combine(
            Path.GetTempPath(),
            $"audio-transcriber-foundry-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentException>(
            () => new FoundryLocalEnrichmentOptions("model")
            {
                ModelCacheDirectory = packageRoot
            }.Validate());
        Assert.Throws<ArgumentException>(
            () => new FoundryLocalEnrichmentOptions("model")
            {
                ModelCacheDirectory = descendant
            }.Validate());
        new FoundryLocalEnrichmentOptions("model")
        {
            ModelCacheDirectory = unrelated
        }.Validate();
    }

    [Fact]
    public async Task Model_leases_reference_count_adapter_loads_and_never_unloads_external_loads()
    {
        var registry = new FoundryLocalModelLeaseRegistry();
        var model = new FakeModelLifecycle("model-id", isLoaded: false);

        var first = await registry.AcquireAsync(model, CancellationToken.None);
        var second = await registry.AcquireAsync(model, CancellationToken.None);
        Assert.Equal(1, model.LoadCount);
        Assert.Equal(0, model.UnloadCount);

        await first.DisposeAsync();
        Assert.Equal(0, model.UnloadCount);
        await second.DisposeAsync();
        Assert.Equal(1, model.UnloadCount);

        var externallyLoaded = new FakeModelLifecycle("external-model", isLoaded: true);
        var externalLease = await registry.AcquireAsync(
            externallyLoaded,
            CancellationToken.None);
        await externalLease.DisposeAsync();
        Assert.Equal(0, externallyLoaded.LoadCount);
        Assert.Equal(0, externallyLoaded.UnloadCount);
    }

    [Fact]
    public async Task Manager_configuration_is_serialized_and_conflicts_are_rejected()
    {
        var host = new FakeManagerHost();
        var registry = new FoundryLocalManagerConfigurationRegistry(host);
        var options = new FoundryLocalEnrichmentOptions("model")
        {
            ApplicationName = "app",
            ModelCacheDirectory = Path.Combine(
                Path.GetTempPath(),
                "foundry-local-cache")
        };

        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => registry.EnsureCompatibleAsync(
                    options,
                    CancellationToken.None)));
        await registry.EnsureCompatibleAsync(options, CancellationToken.None);
        Assert.Equal(1, host.InitializeCount);

        var exception = await Assert.ThrowsAsync<FoundryLocalProviderException>(
            () => registry.EnsureCompatibleAsync(
                options with { ApplicationName = "different-app" },
                CancellationToken.None));
        Assert.Equal(
            FoundryLocalDiagnosticCode.InvalidConfiguration,
            exception.DiagnosticCode);
    }

    [Fact]
    public async Task Manager_configuration_rejects_unverifiable_external_initialization()
    {
        var registry = new FoundryLocalManagerConfigurationRegistry(
            new FakeManagerHost(isInitialized: true));

        var exception = await Assert.ThrowsAsync<FoundryLocalProviderException>(
            () => registry.EnsureCompatibleAsync(
                new FoundryLocalEnrichmentOptions("model"),
                CancellationToken.None));

        Assert.Equal(
            FoundryLocalDiagnosticCode.InvalidConfiguration,
            exception.DiagnosticCode);
    }

    [Fact]
    public void Transferred_request_items_are_not_disposed_by_the_transfer_helper()
    {
        var item = new TrackingDisposable();
        var owner = new FakeRequestItemOwner<TrackingDisposable>();

        FoundryLocalRequestOwnership.TransferToRequest(item, owner.Add);

        Assert.Equal(0, item.DisposeCount);
        owner.Dispose();
        Assert.Equal(1, item.DisposeCount);
    }

    [Fact]
    public async Task Readiness_failures_are_exposed_without_cloud_fallback()
    {
        var artifact = CreateArtifact(
            Segment("source", 0, 1, 0, "segment-source"));
        var runtime = new FailingRuntime(
            new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.ModelNotReady,
                "missing-model",
                "The requested model is not cached."));
        var provider = new FoundryLocalEnrichmentProvider(
            new FoundryLocalEnrichmentOptions("missing-model"),
            runtime);

        var exception = await Assert.ThrowsAsync<FoundryLocalProviderException>(
            () => provider.EnrichAsync(
                new TranscriptChapterEnrichmentRequest(Assert.Single(artifact)))
                .AsTask());

        Assert.Equal(FoundryLocalDiagnosticCode.ModelNotReady, exception.DiagnosticCode);
        Assert.Equal("missing-model", exception.ModelAlias);
        Assert.Equal(1, runtime.PrepareCount);
        await provider.DisposeAsync();
    }

    private static string ChapterResponse(
        TranscriptChapterArtifact chapter,
        string summary)
    {
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1.0",
                kind = "chapter",
                chapterId = chapter.Id,
                summary,
                title = (string?)null,
                keywords = new[] { "topic" },
                evidence = chapter.SourceSegments.Select(
                    segment => new
                    {
                        sourceSegmentId = segment.Id,
                        sourceId = segment.SourceId,
                        start = segment.Start.ToString("c"),
                        end = segment.End.ToString("c")
                    })
            });
    }

    private static string OverallResponse(
        IReadOnlyList<string> chapterIds,
        string summary)
    {
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1.0",
                kind = "overall",
                summary,
                chapterIds
            });
    }

    private static ChatResponse ChatResponseFor(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));

    private static TranscriptChapterArtifactDocument CreateArtifact(
        params TranscriptSegment[] segments)
    {
        var document = new TranscriptDocument(
            "fixture.wav",
            new TranscriptProvenance("fixture", "fixture-model"),
            segments);
        var chunks = new TranscriptChunkBuilder(
                new DeterministicEmbeddingProvider(),
                new DeterministicScoringProvider())
            .Build(
                TranscriptIngestionAdapter.ToIngestionDocument(document),
                new TranscriptChunkingOptions
                {
                    MinimumDuration = TimeSpan.FromMilliseconds(500),
                    MaximumDuration = TimeSpan.FromSeconds(1)
                });
        return new TranscriptChapterArtifactGenerator().Generate(chunks);
    }

    private static TranscriptSegment Segment(
        string text,
        double startSeconds,
        double endSeconds,
        int ordinal,
        string id,
        string? sourceId = null) =>
        new(
            text,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            ordinal,
            sourceId,
            id);

    private sealed class DeterministicEmbeddingProvider
        : IEmbeddingGenerator<TextContent, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<TextContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(new float[] { 1f })
                ]));
        }

        public object? GetService(Type serviceType, object? serviceKey) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class DeterministicScoringProvider : ITranscriptChunkScoringProvider
    {
        public ValueTask<double> ScoreAsync(
            TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(1d);
        }
    }

    private sealed class FakeManagerHost : IFoundryLocalManagerHost
    {
        public FakeManagerHost(bool isInitialized = false)
        {
            IsInitialized = isInitialized;
        }

        public bool IsInitialized { get; private set; }

        public int InitializeCount { get; private set; }

        public Task InitializeAsync(
            FoundryLocalEnrichmentOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCount++;
            IsInitialized = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModelLifecycle : IFoundryLocalModelLifecycle
    {
        private bool isLoaded;

        public FakeModelLifecycle(string modelId, bool isLoaded)
        {
            ModelId = modelId;
            this.isLoaded = isLoaded;
        }

        public string ModelId { get; }

        public int LoadCount { get; private set; }

        public int UnloadCount { get; private set; }

        public Task<bool> IsLoadedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(isLoaded);
        }

        public Task LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            isLoaded = true;
            return Task.CompletedTask;
        }

        public Task UnloadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnloadCount++;
            isLoaded = false;
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeRequestItemOwner<TItem> : IDisposable
        where TItem : IDisposable
    {
        private readonly List<TItem> items = [];

        public void Add(TItem item) => items.Add(item);

        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }

            items.Clear();
        }
    }

    private sealed class FakeRuntime : IFoundryLocalRuntime
    {
        private readonly IChatClient chatClient;

        public FakeRuntime(IChatClient chatClient)
        {
            this.chatClient = chatClient;
        }

        public int PrepareCount { get; private set; }

        public Task<FoundryLocalRuntimeSession> PrepareAsync(
            FoundryLocalEnrichmentOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCount++;
            return Task.FromResult(
                new FoundryLocalRuntimeSession(
                    options.ModelAlias,
                    "model-id",
                    new Uri("http://127.0.0.1:5000/v1"),
                    "external-cache",
                    chatClient));
        }
    }

    private sealed class RetryingRuntime : IFoundryLocalRuntime
    {
        private readonly IChatClient chatClient;
        private int attempt;

        public RetryingRuntime(IChatClient chatClient)
        {
            this.chatClient = chatClient;
        }

        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstAttempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FoundryLocalRuntimeSession>? FirstAttemptTask { get; private set; }

        public int PrepareCount { get; private set; }

        public Task<FoundryLocalRuntimeSession> PrepareAsync(
            FoundryLocalEnrichmentOptions options,
            CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            if (Interlocked.Increment(ref attempt) == 1)
            {
                FirstAttemptStarted.SetResult();
                return FirstAttemptTask = FailFirstAttemptAsync();
            }

            return Task.FromResult(
                new FoundryLocalRuntimeSession(
                    options.ModelAlias,
                    "model-id",
                    new Uri("http://127.0.0.1:5000/v1"),
                    "external-cache",
                    chatClient));
        }

        private async Task<FoundryLocalRuntimeSession> FailFirstAttemptAsync()
        {
            await ReleaseFirstAttempt.Task.ConfigureAwait(false);
            throw new OperationCanceledException();
        }
    }

    private sealed class FailingRuntime : IFoundryLocalRuntime
    {
        private readonly Exception exception;

        public FailingRuntime(Exception exception)
        {
            this.exception = exception;
        }

        public int PrepareCount { get; private set; }

        public Task<FoundryLocalRuntimeSession> PrepareAsync(
            FoundryLocalEnrichmentOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCount++;
            return Task.FromException<FoundryLocalRuntimeSession>(exception);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<
            IReadOnlyList<ChatMessage>,
            ChatOptions?,
            CancellationToken,
            Task<ChatResponse>> handler;

        public FakeChatClient(
            Func<
                IReadOnlyList<ChatMessage>,
                ChatOptions?,
                CancellationToken,
                Task<ChatResponse>> handler)
        {
            this.handler = handler;
        }

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Requests.Add(snapshot);
            Options.Add(options);
            return handler(snapshot, options, cancellationToken);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(
                    messages,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
