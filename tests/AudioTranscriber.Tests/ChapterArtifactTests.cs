using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class ChapterArtifactTests
{
    [Fact]
    public void Generate_SameChunkResult_ProducesEquivalentArtifacts()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment(
                    "opening chapter",
                    0,
                    1.5,
                    0,
                    id: "segment-opening",
                    sourceId: "source-opening",
                    metadata: new Dictionary<string, string>
                    {
                        ["channel-opening"] = "left"
                    }),
                Segment(
                    "closing chapter",
                    1.5,
                    3,
                    1,
                    id: "segment-closing",
                    sourceId: "source-closing",
                    metadata: new Dictionary<string, string>
                    {
                        ["channel-closing"] = "right"
                    })),
            CreateOptions());
        var generator = new Processing.TranscriptChapterArtifactGenerator();

        var first = generator.Generate(chunkResult);
        var second = generator.Generate(chunkResult);

        Assert.Equal(first.Select(chapter => chapter.Id), second.Select(chapter => chapter.Id));
        Assert.Equal(first.Select(chapter => chapter.Title), second.Select(chapter => chapter.Title));
        Assert.Equal(first.Select(chapter => chapter.Text), second.Select(chapter => chapter.Text));
        Assert.Equal(first.Select(chapter => chapter.Start), second.Select(chapter => chapter.Start));
        Assert.Equal(first.Select(chapter => chapter.End), second.Select(chapter => chapter.End));
        Assert.Equal(
            first.Select(chapter => string.Join("\u001f", chapter.SourceSegmentIds)),
            second.Select(chapter => string.Join("\u001f", chapter.SourceSegmentIds)));
        Assert.Equal(first.Select(chapter => chapter.Score), second.Select(chapter => chapter.Score));
        Assert.Equal(
            first.Select(chapter => string.Join(
                "\u001f",
                chapter.SourceMetadata
                    .OrderBy(pair => pair.SourceSegmentId)
                    .ThenBy(pair => pair.Key)
                    .Select(pair => $"{pair.SourceSegmentId}:{pair.Key}={pair.Value}"))),
            second.Select(chapter => string.Join(
                "\u001f",
                chapter.SourceMetadata
                    .OrderBy(pair => pair.SourceSegmentId)
                    .ThenBy(pair => pair.Key)
                    .Select(pair => $"{pair.SourceSegmentId}:{pair.Key}={pair.Value}"))));
        Assert.Equal(
            first.Count,
            first.Select(chapter => chapter.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(first, chapter => Assert.False(string.IsNullOrWhiteSpace(chapter.Id)));
    }

    [Fact]
    public void Generate_UsesVersionedArtifactContract()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment("versioned chapter", 0, 1, 0, id: "segment-versioned")),
            CreateOptions());

        var artifacts = new Processing.TranscriptChapterArtifactGenerator()
            .Generate(chunkResult);

        Assert.Equal("1.0", artifacts.SchemaVersion);
        Assert.Equal("1.0", Assert.Single(artifacts).SchemaVersion);
        Assert.Contains("\"schemaVersion\": \"1.0\"", new Processing.TranscriptChapterArtifactGenerator()
            .Serialize(artifacts), StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_PreservesChunkOrder()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment(
                    "first chunk content",
                    0,
                    1,
                    0,
                    id: "segment-first",
                    sourceId: "source-first"),
                Segment(
                    "second chunk content",
                    1,
                    2,
                    1,
                    id: "segment-second",
                    sourceId: "source-second")),
            CreateOptions());
        var artifacts = new Processing.TranscriptChapterArtifactGenerator()
            .Generate(chunkResult);

        Assert.Equal(
            ["first chunk content", "second chunk content"],
            artifacts.Select(chapter => chapter.Text));
        Assert.Equal(
            ["segment-first", "segment-second"],
            artifacts.SelectMany(chapter => chapter.SourceSegmentIds));
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(1)],
            artifacts.Select(chapter => chapter.Start));
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            artifacts.Select(chapter => chapter.End));
    }

    [Fact]
    public void Serialize_SameArtifacts_ProducesIdenticalOutput()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment(
                    "stable serialized content",
                    0,
                    1,
                    0,
                    id: "segment-stable",
                    sourceId: "source-stable",
                    metadata: new Dictionary<string, string>
                    {
                        ["speaker"] = "host"
                    })),
            CreateOptions());
        var generator = new Processing.TranscriptChapterArtifactGenerator();
        var artifacts = generator.Generate(chunkResult);

        var first = generator.Serialize(artifacts);
        var second = generator.Serialize(artifacts);

        Assert.Equal(first, second);
        Assert.Contains("stable serialized content", first, StringComparison.Ordinal);
        Assert.Contains("segment-stable", first, StringComparison.Ordinal);
        Assert.Contains("source-stable", first, StringComparison.Ordinal);
        Assert.Contains("host", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EmptyChunkResult_ProducesDeterministicEmptyArtifact()
    {
        var emptyChunkResult = CreateChunkBuilder().Build(
            CreateDocument(),
            CreateOptions());
        var generator = new Processing.TranscriptChapterArtifactGenerator();

        var first = generator.Generate(emptyChunkResult);
        var second = generator.Generate(emptyChunkResult);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(generator.Serialize(first), generator.Serialize(second));
    }

    [Fact]
    public void Generate_PreservesSourceIdsAndMetadata()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment(
                    "left source",
                    0,
                    1,
                    0,
                    id: "segment-left",
                    sourceId: "asr-left",
                    metadata: new Dictionary<string, string>
                    {
                        ["source-left"] = "left-value"
                    }),
                Segment(
                    "right source",
                    1,
                    2,
                    1,
                    id: "segment-right",
                    sourceId: "asr-right",
                    metadata: new Dictionary<string, string>
                    {
                        ["source-right"] = "right-value"
                    })),
            CreateOptions());

        var artifacts = new Processing.TranscriptChapterArtifactGenerator()
            .Generate(chunkResult);

        Assert.Equal(
            ["segment-left", "segment-right"],
            artifacts.SelectMany(chapter => chapter.SourceSegmentIds));
        Assert.Equal(
            ["left-value", "right-value"],
            artifacts
                .SelectMany(
                    chapter => chapter.SourceMetadata.OrderBy(
                        pair => pair.SourceSegmentId))
                .Select(pair => pair.Value));
        Assert.Equal(
            ["left source", "right source"],
            artifacts.Select(chapter => chapter.Text));
    }

    [Fact]
    public void Generate_PreservesCollidingMetadataKeysWithoutSyntheticKeyCollisions()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment(
                    "first metadata",
                    0,
                    1,
                    0,
                    id: "segment-one",
                    metadata: new Dictionary<string, string>
                    {
                        ["k"] = "first-k",
                        ["segment-two:k"] = "first-prefixed"
                    }),
                Segment(
                    "second metadata",
                    1,
                    2,
                    1,
                    id: "segment-two",
                    metadata: new Dictionary<string, string>
                    {
                        ["k"] = "second-k"
                    })),
            CreateOptions());

        var chapters = new Processing.TranscriptChapterArtifactGenerator()
            .Generate(chunkResult);

        Assert.Equal(
            [
                ("segment-one", "k", "first-k"),
                ("segment-one", "segment-two:k", "first-prefixed"),
                ("segment-two", "k", "second-k")
            ],
            chapters.SelectMany(chapter => chapter.SourceMetadata).Select(
                entry => (entry.SourceSegmentId, entry.Key, entry.Value)));
    }

    [Fact]
    public async Task GenerateAsync_CancellationIsPropagated()
    {
        var chunkResult = CreateChunkBuilder().Build(
            CreateDocument(
                Segment("cancellable chapter", 0, 1, 0, id: "segment-cancellable")),
            CreateOptions());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => new Processing.TranscriptChapterArtifactGenerator()
                .GenerateAsync(chunkResult, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static Processing.TranscriptChunkBuilder CreateChunkBuilder()
    {
        return new Processing.TranscriptChunkBuilder(
            new DeterministicEmbeddingProvider(),
            new DeterministicChunkScoringProvider());
    }

    private static Processing.TranscriptDocument CreateDocument(
        params Processing.TranscriptSegment[] segments)
    {
        return new Processing.TranscriptDocument(
            "chapter-artifact-fixture.wav",
            new Processing.TranscriptProvenance(
                "fixture-provider",
                "fixture-model",
                metadata: new Dictionary<string, string>
                {
                    ["fixture"] = "phase-3"
                }),
            segments);
    }

    private static Processing.TranscriptChunkingOptions CreateOptions()
    {
        return new Processing.TranscriptChunkingOptions
        {
            MinimumDuration = TimeSpan.FromMilliseconds(500),
            MaximumDuration = TimeSpan.FromSeconds(1)
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
            id: id,
            sourceId: sourceId,
            sourceMetadata: metadata);
    }

    private sealed class DeterministicEmbeddingProvider
        : Processing.ITranscriptEmbeddingProvider
    {
        public ValueTask<Processing.TranscriptEmbeddingResponse> EmbedAsync(
            Processing.TranscriptEmbeddingRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                new Processing.TranscriptEmbeddingResponse([0.125f, -0.25f, 0.5f]));
        }
    }

    private sealed class DeterministicChunkScoringProvider
        : Processing.ITranscriptChunkScoringProvider
    {
        public ValueTask<double> ScoreAsync(
            Processing.TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0.875d);
        }
    }
}
