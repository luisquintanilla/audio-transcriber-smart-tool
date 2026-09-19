using Microsoft.Extensions.AI;

namespace AudioTranscriber.Granite;

public sealed class GraniteEmbeddingProvider :
    IEmbeddingGenerator<TextContent, Embedding<float>>
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

    internal GraniteModelConfiguration.GraniteModelProvenance Provenance { get; }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

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

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<TextContent> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var inputs = values.ToArray();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(inputs.Length);
        foreach (var value in inputs)
        {
            ArgumentNullException.ThrowIfNull(value);
            cancellationToken.ThrowIfCancellationRequested();
            embeddings.Add(
                await GenerateOneAsync(value.Text, cancellationToken)
                    .ConfigureAwait(false));
        }

        return embeddings;
    }

    private async Task<Embedding<float>> GenerateOneAsync(
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Granite embedding text cannot be empty.",
                nameof(text));
        }

        var input = await tokenizer
            .TokenizeAsync(text.Trim(), configuration.MaxTokens, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var output = await runtime
            .InferAsync(input, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var vector = GraniteEmbeddingPostProcessor.PoolClsAndNormalize(
            output,
            configuration.EmbeddingDimensions);
        return new Embedding<float>(vector);
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
