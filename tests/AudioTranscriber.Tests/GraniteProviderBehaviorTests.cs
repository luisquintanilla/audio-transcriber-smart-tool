using AudioTranscriber.Granite;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class GraniteProviderBehaviorTests
{
    [Fact]
    public async Task Provider_CancellationBeforeExecution_Propagates()
    {
        using var temporary = new GraniteTestDirectory();
        var tokenizer = new CountingGraniteTokenizer();
        var runtime = new CountingGraniteRuntime();
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            tokenizer,
            runtime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.EmbedAsync(
                    new TranscriptEmbeddingRequest("cancelled"),
                    cancellation.Token)
                .AsTask());

        Assert.Equal(0, tokenizer.Calls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Provider_CancellationDuringExecution_Propagates()
    {
        using var temporary = new GraniteTestDirectory();
        using var cancellation = new CancellationTokenSource();
        var runtime = new DelegateGraniteRuntime((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GraniteTestOutputs.Create());
        });
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            new FixedGraniteTokenizer(),
            runtime);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.EmbedAsync(
                    new TranscriptEmbeddingRequest("cancel during runtime"),
                    cancellation.Token)
                .AsTask());
    }

    [Fact]
    public async Task Provider_Failure_IsPropagatedWithout_FabricatedEmbedding()
    {
        using var temporary = new GraniteTestDirectory();
        var failure = new InvalidOperationException("sentinel runtime failure");
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            new FixedGraniteTokenizer(),
            new DelegateGraniteRuntime((_, _) =>
                ValueTask.FromException<GraniteInferenceOutput>(failure)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync(new TranscriptEmbeddingRequest("fails")).AsTask());

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task DefaultSuite_DoesNotExecuteRealModel()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);
        var runtime = new CountingGraniteRuntime();
        using var provider = new GraniteEmbeddingProvider(
            configuration,
            new FixedGraniteTokenizer(),
            runtime);

        await provider.EmbedAsync(new TranscriptEmbeddingRequest("offline"));

        Assert.False(configuration.AllowNetworkDownload);
        Assert.Equal(1, runtime.Calls);
        Assert.False(Directory.Exists(configuration.CacheDirectory));
    }

    [Fact]
    public async Task RealModelExecution_RequiresExplicitOptIn()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);

        var exception = await Assert.ThrowsAsync<GraniteModelAssetException>(
            () => GraniteEmbeddingProvider.CreateAsync(configuration));

        Assert.Equal(GraniteDiagnosticCode.MissingAsset, exception.DiagnosticCode);
        Assert.False(configuration.AllowNetworkDownload);
    }
}

internal sealed class GraniteTestDirectory : IDisposable
{
    public GraniteTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"audio-transcriber-granite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class FixedGraniteTokenizer : IGraniteTokenizer
{
    public ValueTask<GraniteTokenizedInput> TokenizeAsync(
        string text,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GraniteTokenizedInput([0, 1], [1, 1]));
    }
}

internal sealed class FixedGraniteRuntime : IGraniteInferenceRuntime
{
    private readonly GraniteInferenceOutput output;

    public FixedGraniteRuntime(GraniteInferenceOutput output)
    {
        this.output = output;
    }

    public ValueTask<GraniteInferenceOutput> InferAsync(
        GraniteTokenizedInput input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(output);
    }
}

internal sealed class CountingGraniteTokenizer : IGraniteTokenizer
{
    public int Calls { get; private set; }

    public ValueTask<GraniteTokenizedInput> TokenizeAsync(
        string text,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GraniteTokenizedInput([0, 1], [1, 1]));
    }
}

internal sealed class CountingGraniteRuntime : IGraniteInferenceRuntime
{
    public int Calls { get; private set; }

    public ValueTask<GraniteInferenceOutput> InferAsync(
        GraniteTokenizedInput input,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GraniteTestOutputs.Create());
    }
}

internal sealed class DelegateGraniteRuntime : IGraniteInferenceRuntime
{
    private readonly Func<GraniteTokenizedInput, CancellationToken, ValueTask<GraniteInferenceOutput>> callback;

    public DelegateGraniteRuntime(
        Func<GraniteTokenizedInput, CancellationToken, ValueTask<GraniteInferenceOutput>> callback)
    {
        this.callback = callback;
    }

    public ValueTask<GraniteInferenceOutput> InferAsync(
        GraniteTokenizedInput input,
        CancellationToken cancellationToken = default) =>
        callback(input, cancellationToken);
}

internal sealed class FixedGraniteCapabilityProbe : IGraniteCpuCapabilityProbe
{
    private readonly bool avx2Supported;

    public FixedGraniteCapabilityProbe(bool avx2Supported)
    {
        this.avx2Supported = avx2Supported;
    }

    public GraniteRuntimeCapabilities Probe() =>
        new(System.Runtime.InteropServices.Architecture.X64, avx2Supported);
}

internal static class GraniteTestOutputs
{
    public static GraniteInferenceOutput Create()
    {
        var values = new float[GraniteModelMetadata.EmbeddingDimensions * 2];
        values[0] = 3;
        values[1] = 4;
        return new GraniteInferenceOutput(
            batchSize: 1,
            sequenceLength: 2,
            hiddenSize: GraniteModelMetadata.EmbeddingDimensions,
            values);
    }
}
