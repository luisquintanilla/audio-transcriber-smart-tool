using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AudioTranscriber.Granite;

public sealed class GraniteSentencePieceTokenizer : IGraniteTokenizer, IDisposable
{
    private readonly SentencePieceTokenizer tokenizer;

    public GraniteSentencePieceTokenizer(string tokenizerPath, int maxTokens = GraniteModelMetadata.MaxTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenizerPath);
        if (!File.Exists(tokenizerPath))
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.MissingAsset,
                GraniteAssetKind.Tokenizer,
                "The pinned Granite tokenizer asset is missing from the external cache.");
        }

        try
        {
            using var stream = File.OpenRead(tokenizerPath);
            tokenizer = SentencePieceTokenizer.Create(
                stream,
                true,
                true);
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidDataException or
            ArgumentException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                GraniteAssetKind.Tokenizer,
                "The pinned Granite tokenizer asset is incompatible with SentencePiece.");
        }

        if (maxTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        MaxTokens = maxTokens;
    }

    public int MaxTokens { get; }

    public ValueTask<GraniteTokenizedInput> TokenizeAsync(
        string text,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (maxTokens <= 0 || maxTokens > MaxTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ids = tokenizer
            .EncodeToIds(text)
            .Take(maxTokens)
            .Select(static id => (long)id)
            .ToArray();
        if (ids.Length == 0)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                GraniteAssetKind.Tokenizer,
                "The Granite tokenizer returned no token IDs for the input text.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new GraniteTokenizedInput(ids, Enumerable.Repeat(1L, ids.Length)));
    }

    public void Dispose()
    {
    }
}

public sealed class GraniteOnnxInferenceRuntime : IGraniteInferenceRuntime, IDisposable
{
    private readonly InferenceSession session;

    public GraniteOnnxInferenceRuntime(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.MissingAsset,
                GraniteAssetKind.Model,
                "The pinned Granite model asset is missing from the external cache.");
        }

        try
        {
            session = CreateSession(modelPath);
        }
        catch (GraniteModelAssetException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OnnxRuntimeException or
            IOException or
            InvalidDataException or
            ArgumentException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            TypeInitializationException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                GraniteAssetKind.Model,
                "The pinned Granite ONNX model asset is incompatible with the configured ONNX Runtime.");
        }
    }

    public ValueTask<GraniteInferenceOutput> InferAsync(
        GraniteTokenizedInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var shape = new[] { 1, input.SequenceLength };
            var inputIds = new DenseTensor<long>(input.InputIds.ToArray(), shape);
            var attentionMask = new DenseTensor<long>(input.AttentionMask.ToArray(), shape);
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            };
            if (session.InputMetadata.ContainsKey("token_type_ids"))
            {
                inputs.Add(
                    NamedOnnxValue.CreateFromTensor(
                        "token_type_ids",
                        new DenseTensor<long>(new long[input.SequenceLength], shape)));
            }

            using var results = session.Run(inputs);
            var result = results.FirstOrDefault(item => item.Name == "last_hidden_state")
                ?? results.FirstOrDefault()
                ?? throw new GraniteModelAssetException(
                    GraniteDiagnosticCode.IncompatibleAsset,
                    GraniteAssetKind.Model,
                    "The Granite ONNX model returned no tensor output.");
            var tensor = result.AsTensor<float>();
            if (tensor.Rank != 3)
            {
                throw new GraniteModelAssetException(
                    GraniteDiagnosticCode.IncompatibleAsset,
                    GraniteAssetKind.Model,
                    "The Granite ONNX model output must have batch, sequence, and hidden dimensions.");
            }

            var dimensions = tensor.Dimensions.ToArray();
            var output = new GraniteInferenceOutput(
                dimensions[0],
                dimensions[1],
                dimensions[2],
                tensor.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(output);
        }
        catch (GraniteModelAssetException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OnnxRuntimeException or
            InvalidCastException or
            InvalidOperationException or
            ArgumentException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                GraniteAssetKind.Model,
                "The Granite ONNX model could not produce a compatible hidden-state tensor.");
        }
    }

    public void Dispose() => session.Dispose();

    private static InferenceSession CreateSession(string modelPath)
    {
        var candidate = new InferenceSession(modelPath);
        try
        {
            ValidateInputs(candidate);
            return candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private static void ValidateInputs(InferenceSession session)
    {
        if (!session.InputMetadata.ContainsKey("input_ids") ||
            !session.InputMetadata.ContainsKey("attention_mask"))
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                GraniteAssetKind.Model,
                "The Granite ONNX model must expose input_ids and attention_mask inputs.");
        }
    }
}
