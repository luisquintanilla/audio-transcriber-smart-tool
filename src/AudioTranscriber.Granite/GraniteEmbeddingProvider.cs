using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Granite;

public sealed class GraniteEmbeddingProvider : ITranscriptEmbeddingProvider, IDisposable
{
    private readonly GraniteModelConfiguration configuration;
    private readonly IGraniteTokenizer tokenizer;
    private readonly IGraniteInferenceRuntime runtime;

    public GraniteEmbeddingProvider(
        GraniteModelConfiguration configuration,
        IGraniteTokenizer tokenizer,
        IGraniteInferenceRuntime runtime,
        IGraniteCpuCapabilityProbe? capabilityProbe = null)
    {
        this.configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        this.tokenizer = tokenizer
            ?? throw new ArgumentNullException(nameof(tokenizer));
        this.runtime = runtime
            ?? throw new ArgumentNullException(nameof(runtime));

        var capabilities = (capabilityProbe ?? new SystemGraniteCpuCapabilityProbe()).Probe();
        configuration.ValidateCapabilities(capabilities);
        Provenance = configuration.Provenance;
    }

    public TranscriptProvenance Provenance { get; }

    public static async Task<GraniteEmbeddingProvider> CreateAsync(
        GraniteModelConfiguration? configuration = null,
        HttpClient? httpClient = null,
        IGraniteCpuCapabilityProbe? capabilityProbe = null,
        CancellationToken cancellationToken = default)
    {
        configuration ??= new GraniteModelConfiguration();
        var capabilities = (capabilityProbe ?? new SystemGraniteCpuCapabilityProbe()).Probe();
        configuration.ValidateCapabilities(capabilities);

        var assets = await new GraniteModelAssetCache(configuration, httpClient)
            .EnsureAssetsAsync(cancellationToken)
            .ConfigureAwait(false);
        var tokenizer = new GraniteSentencePieceTokenizer(
            assets.TokenizerPath,
            configuration.MaxTokens);
        var runtime = new GraniteOnnxInferenceRuntime(assets.ModelPath);

        return new GraniteEmbeddingProvider(
            configuration,
            tokenizer,
            runtime,
            capabilityProbe);
    }

    public async ValueTask<TranscriptEmbeddingResponse> EmbedAsync(
        TranscriptEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var input = await tokenizer
            .TokenizeAsync(request.Text, configuration.MaxTokens, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var output = await runtime
            .InferAsync(input, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var vector = GraniteEmbeddingPostProcessor.PoolClsAndNormalize(
            output,
            configuration.EmbeddingDimensions);
        return new TranscriptEmbeddingResponse(vector);
    }

    public void Dispose()
    {
        if (runtime is IDisposable disposableRuntime)
        {
            disposableRuntime.Dispose();
        }

        if (tokenizer is IDisposable disposableTokenizer)
        {
            disposableTokenizer.Dispose();
        }
    }
}
