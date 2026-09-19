using System.Numerics.Tensors;

namespace AudioTranscriber.Granite;

public sealed class GraniteTokenizedInput
{
    public GraniteTokenizedInput(
        IEnumerable<long> inputIds,
        IEnumerable<long> attentionMask)
    {
        ArgumentNullException.ThrowIfNull(inputIds);
        ArgumentNullException.ThrowIfNull(attentionMask);

        var ids = inputIds.ToArray();
        var mask = attentionMask.ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("Granite tokenized input cannot be empty.", nameof(inputIds));
        }

        if (ids.Length != mask.Length)
        {
            throw new ArgumentException(
                "Granite input IDs and attention mask must have the same length.",
                nameof(attentionMask));
        }

        if (mask.Any(value => value is not 0 and not 1))
        {
            throw new ArgumentException(
                "Granite attention masks must contain only zero or one values.",
                nameof(attentionMask));
        }

        InputIds = Array.AsReadOnly(ids);
        AttentionMask = Array.AsReadOnly(mask);
    }

    public IReadOnlyList<long> InputIds { get; }

    public IReadOnlyList<long> AttentionMask { get; }

    public int SequenceLength => InputIds.Count;
}

public interface IGraniteTokenizer
{
    ValueTask<GraniteTokenizedInput> TokenizeAsync(
        string text,
        int maxTokens,
        CancellationToken cancellationToken = default);
}

public sealed class GraniteInferenceOutput
{
    public GraniteInferenceOutput(
        int batchSize,
        int sequenceLength,
        int hiddenSize,
        IEnumerable<float> values)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        if (sequenceLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceLength));
        }

        if (hiddenSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hiddenSize));
        }

        ArgumentNullException.ThrowIfNull(values);
        var flattened = values.ToArray();
        var expectedLength = checked(batchSize * sequenceLength * hiddenSize);
        if (flattened.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Granite inference output has {flattened.Length} values; expected {expectedLength}.",
                nameof(values));
        }

        if (flattened.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException(
                "Granite inference output must contain only finite values.",
                nameof(values));
        }

        BatchSize = batchSize;
        SequenceLength = sequenceLength;
        HiddenSize = hiddenSize;
        Values = Array.AsReadOnly(flattened);
    }

    public int BatchSize { get; }

    public int SequenceLength { get; }

    public int HiddenSize { get; }

    public IReadOnlyList<float> Values { get; }
}

public interface IGraniteInferenceRuntime
{
    ValueTask<GraniteInferenceOutput> InferAsync(
        GraniteTokenizedInput input,
        CancellationToken cancellationToken = default);
}

public static class GraniteEmbeddingPostProcessor
{
    public static float[] PoolClsAndNormalize(
        GraniteInferenceOutput output,
        int expectedDimensions = GraniteModelMetadata.EmbeddingDimensions)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.BatchSize != 1)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.InvalidOutput,
                GraniteAssetKind.Model,
                "Granite inference must return exactly one output batch.");
        }

        if (output.HiddenSize != expectedDimensions)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.InvalidOutput,
                GraniteAssetKind.Model,
                $"Granite inference returned {output.HiddenSize} dimensions; " +
                $"expected {expectedDimensions}.");
        }

        var vector = new float[expectedDimensions];
        for (var dimension = 0; dimension < expectedDimensions; dimension++)
        {
            vector[dimension] = output.Values[dimension];
        }

        var norm = TensorPrimitives.Norm(vector);
        if (!float.IsFinite(norm) || norm <= float.Epsilon)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.InvalidOutput,
                GraniteAssetKind.Model,
                "Granite inference returned a zero CLS vector that cannot be normalized.");
        }

        var normalized = new float[vector.Length];
        TensorPrimitives.Divide(vector, norm, normalized);
        return normalized;
    }
}
