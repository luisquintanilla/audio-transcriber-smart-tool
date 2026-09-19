using System.Runtime.InteropServices;
using System.Numerics.Tensors;
using AudioTranscriber.Granite;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.Tests;

public sealed class GraniteRuntimeTests
{
    [Fact]
    public void Capabilities_ReportAvx2Availability()
    {
        var capabilities = new SystemGraniteCpuCapabilityProbe().Probe();

        Assert.Equal(RuntimeInformation.ProcessArchitecture, capabilities.Architecture);
        Assert.Equal(
            capabilities.Architecture is Architecture.X86 or Architecture.X64 &&
            System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            capabilities.Avx2Supported);
    }

    [Fact]
    public void Capabilities_ReportUnsupportedCpuWithoutLoadingAssets()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(
            temporary.Path,
            requireAvx2: true);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteEmbeddingProvider(
                configuration,
                new FixedGraniteTokenizer(),
                new FixedGraniteRuntime(CreateOutput()),
                new FixedGraniteCapabilityProbe(avx2Supported: false)));

        Assert.Equal(GraniteDiagnosticCode.UnsupportedCpu, exception.DiagnosticCode);
        Assert.False(File.Exists(
            Path.Combine(configuration.CacheDirectory, GraniteModelMetadata.ModelFileName)));
    }

    [Fact]
    public void Runtime_IncompatibleModelReportsRedactedDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var modelPath = Path.Combine(temporary.Path, "invalid-model.onnx");
        File.WriteAllBytes(modelPath, [1, 2, 3]);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteOnnxInferenceRuntime(modelPath));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            modelPath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runtime_ExistingUnreadableModelReportsReadDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var modelPath = Path.Combine(temporary.Path, "unreadable-model.onnx");
        Directory.CreateDirectory(modelPath);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteOnnxInferenceRuntime(modelPath));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.DoesNotContain(
            modelPath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FakeRuntime_UsesClsPooling()
    {
        var output = new GraniteInferenceOutput(
            batchSize: 1,
            sequenceLength: 2,
            hiddenSize: 3,
            values: [3, 4, 0, 100, 100, 100]);

        var vector = GraniteEmbeddingPostProcessor.PoolClsAndNormalize(output, 3);

        Assert.Equal(0.6f, vector[0], precision: 5);
        Assert.Equal(0.8f, vector[1], precision: 5);
        Assert.Equal(0f, vector[2]);
    }

    [Fact]
    public void FakeRuntime_ReturnsExpectedOutputShape()
    {
        var output = CreateOutput();

        Assert.Equal(1, output.BatchSize);
        Assert.Equal(2, output.SequenceLength);
        Assert.Equal(GraniteModelMetadata.EmbeddingDimensions, output.HiddenSize);
        Assert.Equal(
            output.BatchSize * output.SequenceLength * output.HiddenSize,
            output.Values.Count);
    }

    [Fact]
    public void TokenizerBoundary_PreservesBosAndEosWhenTruncating()
    {
        var truncated = GraniteOnnxInferenceRuntime.GraniteTokenizerBoundaries
            .TruncatePreservingSpecialTokens([101, 10, 11, 12, 102], 4);

        Assert.Equal([101, 10, 11, 102], truncated);
    }

    [Fact]
    public void FakeRuntime_L2NormalizesOutput()
    {
        var vector = GraniteEmbeddingPostProcessor.PoolClsAndNormalize(
            new GraniteInferenceOutput(
                batchSize: 1,
                sequenceLength: 1,
                hiddenSize: 3,
                values: [3, 4, 0]),
            expectedDimensions: 3);

        var norm = TensorPrimitives.Norm(vector.AsSpan());

        Assert.Equal(1f, norm, precision: 6);
    }

    [Fact]
    public async Task FakeRuntime_IsDeterministicForIdenticalInput()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);
        using var provider = new GraniteEmbeddingProvider(
            configuration,
            new FixedGraniteTokenizer(),
            new FixedGraniteRuntime(CreateOutput()));

        var first = await provider.GenerateAsync([new TextContent("same input")]);
        var second = await provider.GenerateAsync([new TextContent("same input")]);

        Assert.Equal(first[0].Vector.ToArray(), second[0].Vector.ToArray());
    }

    private static GraniteInferenceOutput CreateOutput()
    {
        var values = new float[
            GraniteModelMetadata.EmbeddingDimensions * 2];
        values[0] = 3;
        values[1] = 4;
        return new GraniteInferenceOutput(
            batchSize: 1,
            sequenceLength: 2,
            hiddenSize: GraniteModelMetadata.EmbeddingDimensions,
            values);
    }
}
