using Microsoft.Extensions.AI;
using DataIngestion = Microsoft.Extensions.DataIngestion;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class TranscriptChunkingBehaviorTests
{
    [Fact]
    public async Task BuildAsync_CancellationBeforeWork_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var embedding = new ControlledEmbeddingProvider();
        var scoring = new ControlledChunkScoringProvider();
        var builder = CreateBuilder(embedding, scoring);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildAsync(
                CreateDocument(Segment("cancel before work", 0, 2, 0)),
                CreateOptions(),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, embedding.CallCount);
        Assert.Equal(0, scoring.CallCount);
    }

    [Fact]
    public async Task BuildAsync_CancellationDuringEmbedding_StopsAndThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        var embedding = new ControlledEmbeddingProvider
        {
            CancelSourceOnCall = cancellation
        };
        var scoring = new ControlledChunkScoringProvider();
        var builder = CreateBuilder(embedding, scoring);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildAsync(
                CreateDocument(Segment("cancel during embedding", 0, 2, 0)),
                CreateOptions(),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(0, scoring.CallCount);
    }

    [Fact]
    public async Task BuildAsync_CancellationDuringScoring_StopsAndThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource();
        var embedding = new ControlledEmbeddingProvider();
        var scoring = new ControlledChunkScoringProvider
        {
            CancelSourceOnCall = cancellation
        };
        var builder = CreateBuilder(embedding, scoring);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildAsync(
                CreateDocument(Segment("cancel during scoring", 0, 2, 0)),
                CreateOptions(),
                cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(1, scoring.CallCount);
    }

    [Fact]
    public async Task BuildAsync_PropagatesEmbeddingProviderError()
    {
        var sentinel = new InvalidOperationException("embedding-provider-sentinel");
        var embedding = new ControlledEmbeddingProvider
        {
            Failure = sentinel
        };
        var scoring = new ControlledChunkScoringProvider();
        var builder = CreateBuilder(embedding, scoring);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.BuildAsync(
                CreateDocument(Segment("embedding failure", 0, 2, 0)),
                CreateOptions(),
                CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.Equal("embedding-provider-sentinel", exception.Message);
        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(0, scoring.CallCount);
    }

    [Fact]
    public async Task BuildAsync_PropagatesChunkScoringError()
    {
        var sentinel = new InvalidOperationException("chunk-scoring-sentinel");
        var embedding = new ControlledEmbeddingProvider();
        var scoring = new ControlledChunkScoringProvider
        {
            Failure = sentinel
        };
        var builder = CreateBuilder(embedding, scoring);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.BuildAsync(
                CreateDocument(Segment("scoring failure", 0, 2, 0)),
                CreateOptions(),
                CancellationToken.None));

        Assert.Same(sentinel, exception);
        Assert.Equal("chunk-scoring-sentinel", exception.Message);
        Assert.Equal(1, embedding.CallCount);
        Assert.Equal(1, scoring.CallCount);
    }

    [Fact]
    public async Task BuildAsync_PreservesDeterministicOutputForDeterministicDependencies()
    {
        var document = CreateDocument(
            Segment(
                "deterministic first",
                0,
                1.5,
                0,
                id: "segment-deterministic-first",
                sourceId: "source-deterministic-first",
                metadata: new Dictionary<string, string> { ["channel"] = "left" }),
            Segment(
                "deterministic second",
                2,
                3.5,
                1,
                id: "segment-deterministic-second",
                sourceId: "source-deterministic-second",
                metadata: new Dictionary<string, string> { ["channel"] = "right" }));
        var options = CreateOptions(
            minimumDuration: TimeSpan.FromSeconds(1),
            maximumDuration: TimeSpan.FromSeconds(5),
            requestedStart: TimeSpan.Zero,
            requestedEnd: TimeSpan.FromSeconds(3.5));
        var firstEmbedding = new ControlledEmbeddingProvider();
        var firstScoring = new ControlledChunkScoringProvider();
        var secondEmbedding = new ControlledEmbeddingProvider();
        var secondScoring = new ControlledChunkScoringProvider();

        var first = await CreateBuilder(
                firstEmbedding,
                firstScoring)
            .BuildAsync(document, options, CancellationToken.None);
        var second = await CreateBuilder(
                secondEmbedding,
                secondScoring)
            .BuildAsync(document, options, CancellationToken.None);

        Assert.NotEmpty(firstEmbedding.Requests);
        Assert.NotEmpty(firstScoring.Requests);
        Assert.Equal(
            firstEmbedding.Requests,
            secondEmbedding.Requests);
        Assert.Equal(
            firstScoring.Requests.Select(request => (request.Text, request.Start, request.End)),
            secondScoring.Requests.Select(request => (request.Text, request.Start, request.End)));

        var firstScoringRequests = firstScoring.Requests.ToArray();
        var secondScoringRequests = secondScoring.Requests.ToArray();
        foreach (var (firstRequest, secondRequest) in firstScoringRequests.Zip(secondScoringRequests))
        {
            Assert.Equal(
                firstRequest.Segments.Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))),
                secondRequest.Segments.Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))));
        }

        Assert.Equal(
            first.Windows.Select(window => (window.Start, window.End, window.Text, window.IsGap)),
            second.Windows.Select(window => (window.Start, window.End, window.Text, window.IsGap)));
        Assert.Equal(
            first.Windows.SelectMany(window => window.SourceSegmentIds),
            second.Windows.SelectMany(window => window.SourceSegmentIds));
        Assert.Equal(
            first.Windows
                .SelectMany(window => window.SourceSegments)
                .Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))),
            second.Windows
                .SelectMany(window => window.SourceSegments)
                .Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))));

        Assert.Equal(
            first.Windows.Count(),
            second.Windows.Count());
        foreach (var (firstWindow, secondWindow) in first.Windows.Zip(second.Windows))
        {
            Assert.Equal(firstWindow.SourceSegmentIds, secondWindow.SourceSegmentIds);
            Assert.Equal(
                firstWindow.SourceSegments.Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))),
                secondWindow.SourceSegments.Select(segment => (
                    segment.Id,
                    segment.SourceId,
                    segment.OriginalOrdinal,
                    segment.Start,
                    segment.End,
                    segment.Text,
                    segment.Speaker,
                    segment.Confidence,
                    Metadata: string.Join(
                        "\u001f",
                        segment.SourceMetadata
                            .OrderBy(pair => pair.Key)
                            .Select(pair => $"{pair.Key}={pair.Value}")))));
        }
    }

    [Fact]
    public async Task BuildAsync_UsesStandardEmbeddingVectorsForCosineSimilarity()
    {
        var embedding = new SequenceEmbeddingProvider();
        var result = await CreateBuilder(
                embedding,
                new ControlledChunkScoringProvider())
            .BuildAsync(
                CreateDocument(
                    Segment("first semantic window", 0, 1, 0),
                    Segment("second semantic window", 1, 2, 1)),
                CreateOptions(
                    minimumDuration: TimeSpan.FromMilliseconds(500),
                    maximumDuration: TimeSpan.FromSeconds(1)),
                CancellationToken.None);

        Assert.Equal(
            ["first semantic window", "second semantic window"],
            embedding.Requests);
        Assert.Equal(2, result.Windows.Count);
        Assert.Null(result.Windows[0].SemanticSimilarity);
        Assert.Equal(
            0d,
            result.Windows[1].SemanticSimilarity!.Value,
            precision: 6);
    }

    private static Processing.TranscriptChunkBuilder CreateBuilder(
        IEmbeddingGenerator<TextContent, Embedding<float>> embedding,
        ControlledChunkScoringProvider scoring)
    {
        return new Processing.TranscriptChunkBuilder(embedding, scoring);
    }

    private static DataIngestion.IngestionDocument CreateDocument(
        params Processing.TranscriptSegment[] segments)
    {
        return Processing.TranscriptIngestionAdapter.ToIngestionDocument(
            new Processing.TranscriptDocument(
                "chunking-behavior-fixture.wav",
                new Processing.TranscriptProvenance(
                    "fixture-provider",
                    "fixture-model"),
                segments));
    }

    private static Processing.TranscriptChunkingOptions CreateOptions(
        TimeSpan? minimumDuration = null,
        TimeSpan? maximumDuration = null,
        TimeSpan? requestedStart = null,
        TimeSpan? requestedEnd = null)
    {
        return new Processing.TranscriptChunkingOptions
        {
            MinimumDuration = minimumDuration ?? TimeSpan.FromSeconds(1),
            MaximumDuration = maximumDuration ?? TimeSpan.FromSeconds(10),
            RequestedStart = requestedStart,
            RequestedEnd = requestedEnd
        };
    }

    private static Processing.TranscriptSegment Segment(
        string text,
        double startSeconds,
        double endSeconds,
        int ordinal,
        string? id = null,
        string? sourceId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new Processing.TranscriptSegment(
            text,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            ordinal,
            sourceId: sourceId,
            id: id,
            sourceMetadata: metadata);
    }

    private sealed class ControlledEmbeddingProvider
        : IEmbeddingGenerator<TextContent, Embedding<float>>
    {
        public List<string> Requests { get; } = [];

        public int CallCount { get; private set; }

        public Exception? Failure { get; init; }

        public CancellationTokenSource? CancelSourceOnCall { get; init; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<TextContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(Assert.Single(values).Text);
            CancelSourceOnCall?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is not null)
            {
                throw Failure;
            }

            await Task.Yield();
            return
            [
                new Embedding<float>(new float[] { 0.125f, -0.25f, 0.5f })
            ];
        }

        public object? GetService(Type serviceType, object? serviceKey) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class SequenceEmbeddingProvider
        : IEmbeddingGenerator<TextContent, Embedding<float>>
    {
        public List<string> Requests { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<TextContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(Assert.Single(values).Text);
            var vector = Requests.Count == 1
                ? new float[] { 1, 0 }
                : new float[] { 0, 1 };
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(vector)
                ]));
        }

        public object? GetService(Type serviceType, object? serviceKey) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ControlledChunkScoringProvider
        : Processing.ITranscriptChunkScoringProvider
    {
        public List<Processing.TranscriptChunkScoringRequest> Requests { get; } = [];

        public int CallCount { get; private set; }

        public Exception? Failure { get; init; }

        public CancellationTokenSource? CancelSourceOnCall { get; init; }

        public async ValueTask<double> ScoreAsync(
            Processing.TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Requests.Add(request);
            CancelSourceOnCall?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            if (Failure is not null)
            {
                throw Failure;
            }

            await Task.Yield();
            return 0.875d;
        }
    }
}
