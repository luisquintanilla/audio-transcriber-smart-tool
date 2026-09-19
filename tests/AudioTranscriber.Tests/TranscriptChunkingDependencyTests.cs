using Microsoft.Extensions.AI;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class TranscriptChunkingDependencyTests
{
    [Fact]
    public async Task EmbeddingFake_ImplementsMicrosoftExtensionsAiContract()
    {
        var fake = new DeterministicEmbeddingProvider();
        IEmbeddingGenerator<string, Embedding<float>> provider = fake;

        var response = await provider.GenerateAsync(
            ["opening statement"],
            cancellationToken: CancellationToken.None);
        var embedding = Assert.Single(response);

        Assert.Equal([0.125f, -0.25f, 0.5f], embedding.Vector.ToArray());
        Assert.Equal("opening statement", fake.LastRequest);
        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task ScoringFake_ImplementsTheNarrowChunkScoringContract()
    {
        var fake = new DeterministicChunkScoringProvider();
        Processing.ITranscriptChunkScoringProvider scorer = fake;
        var request = CreateScoringRequest();

        var score = await scorer.ScoreAsync(request, CancellationToken.None);

        Assert.Equal(0.875, score);
        Assert.Same(request, fake.LastRequest);
        var lastRequest = fake.LastRequest!;
        Assert.Equal("candidate window", lastRequest.Text);
        Assert.Equal(["segment-1"], lastRequest.Segments.Select(segment => segment.Id));
        Assert.Equal(TimeSpan.FromSeconds(1), lastRequest.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), lastRequest.End);
        Assert.Equal("left", lastRequest.Segments.Single().SourceMetadata["channel"]);
        Assert.Equal(
            [0.25f, -0.5f],
            lastRequest.Embedding!.Vector.ToArray());
    }

    [Fact]
    public async Task ChunkingDependencies_AcceptCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        var embeddingFake = new DeterministicEmbeddingProvider();
        var scoringFake = new DeterministicChunkScoringProvider();

        IEmbeddingGenerator<string, Embedding<float>> embedding = embeddingFake;
        Processing.ITranscriptChunkScoringProvider scorer = scoringFake;

        await embedding.GenerateAsync(
            ["cancelled window"],
            cancellationToken: token);
        await scorer.ScoreAsync(CreateScoringRequest(), token);

        Assert.True(embeddingFake.ReceivedCancellation);
        Assert.True(scoringFake.ReceivedCancellation);
        Assert.Equal(token, embeddingFake.LastCancellationToken);
        Assert.Equal(token, scoringFake.LastCancellationToken);
    }

    private static Processing.TranscriptChunkScoringRequest CreateScoringRequest()
    {
        var segment = new Processing.TranscriptSegment(
            "candidate window",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            originalOrdinal: 4,
            id: "segment-1",
            sourceId: "source-1",
            sourceMetadata: new Dictionary<string, string>
            {
                ["channel"] = "left"
            });

        return new Processing.TranscriptChunkScoringRequest(
            "candidate window",
            [segment],
            segment.Start,
            segment.End,
            new Embedding<float>(new float[] { 0.25f, -0.5f }));
    }

    private sealed class DeterministicEmbeddingProvider
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public string? LastRequest { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public bool ReceivedCancellation => LastCancellationToken.IsCancellationRequested;

        public int CallCount { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = Assert.Single(values);
            LastCancellationToken = cancellationToken;
            CallCount++;
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(new float[] { 0.125f, -0.25f, 0.5f })
                ]));
        }

        public object? GetService(Type serviceType, object? serviceKey) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class DeterministicChunkScoringProvider
        : Processing.ITranscriptChunkScoringProvider
    {
        public Processing.TranscriptChunkScoringRequest? LastRequest { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public bool ReceivedCancellation => LastCancellationToken.IsCancellationRequested;

        public ValueTask<double> ScoreAsync(
            Processing.TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastCancellationToken = cancellationToken;
            return ValueTask.FromResult(0.875d);
        }
    }
}
