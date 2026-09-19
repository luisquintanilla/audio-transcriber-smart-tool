using AudioTranscriber.Granite;
using Microsoft.Extensions.AI;

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
            () => provider.GenerateAsync(
                [new TextContent("cancelled")],
                cancellationToken: cancellation.Token));

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
            () => provider.GenerateAsync(
                [new TextContent("cancel during runtime")],
                cancellationToken: cancellation.Token));
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
            () => provider.GenerateAsync([new TextContent("fails")]));

        Assert.Same(failure, exception);
    }

    [Fact]
    public async Task Provider_GeneratesStandardEmbeddingsInInputOrder()
    {
        using var temporary = new GraniteTestDirectory();
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            new FixedGraniteTokenizer(),
            new CountingGraniteRuntime());

        var generated = await provider.GenerateAsync(
            [new TextContent("first"), new TextContent("second")]);

        Assert.IsType<GeneratedEmbeddings<Embedding<float>>>(generated);
        Assert.Equal(2, generated.Count);
        Assert.All(generated, embedding =>
            Assert.Equal(GraniteModelMetadata.EmbeddingDimensions, embedding.Dimensions));
    }

    [Fact]
    public async Task Provider_RejectsUnsupportedRequestedDimensions()
    {
        using var temporary = new GraniteTestDirectory();
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            new FixedGraniteTokenizer(),
            new CountingGraniteRuntime());

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => provider.GenerateAsync(
                [new TextContent("unsupported dimensions")],
                new EmbeddingGenerationOptions { Dimensions = 384 }));

        Assert.Equal("options", exception.ParamName);
        Assert.Contains("768", exception.Message);
    }

    [Fact]
    public void Provider_ImplementsStandardEmbeddingGeneratorContract()
    {
        using var temporary = new GraniteTestDirectory();
        using var provider = new GraniteEmbeddingProvider(
            new GraniteModelConfiguration(temporary.Path),
            new FixedGraniteTokenizer(),
            new CountingGraniteRuntime());

        Assert.Same(provider, provider.GetService(
            typeof(IEmbeddingGenerator<TextContent, Embedding<float>>)));
        Assert.Null(provider.GetService(
            typeof(IEmbeddingGenerator<TextContent, Embedding<float>>),
            "named"));
        Assert.Null(provider.GetService(typeof(string)));
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

        await provider.GenerateAsync([new TextContent("offline")]);

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
