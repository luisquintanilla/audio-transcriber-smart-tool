using AudioTranscriber.Granite;

namespace AudioTranscriber.Tests;

public sealed class GraniteAssetIntegrityTests
{
    [Fact]
    public void Provenance_ReportsModelSha256()
    {
        using var temporary = new GraniteTestDirectory();
        var provenance = new GraniteModelConfiguration(temporary.Path).Provenance;

        Assert.Contains(GraniteModelMetadata.ModelSha256, provenance.Source);
        Assert.Equal(GraniteModelMetadata.ModelId, provenance.Model);
        Assert.Equal(
            GraniteModelMetadata.Revision,
            provenance.Source!
                .Split(';')
                .Single(item => item.StartsWith("revision=", StringComparison.Ordinal))
                .Split('=', 2)[1]);
    }

    [Fact]
    public void Provenance_ReportsTokenizerSha256()
    {
        using var temporary = new GraniteTestDirectory();
        var provenance = new GraniteModelConfiguration(temporary.Path).Provenance;

        Assert.Contains(GraniteModelMetadata.TokenizerSha256, provenance.Source);
        Assert.Contains("pooling=CLS", provenance.Source);
        Assert.Contains("normalization=L2", provenance.Source);
    }

    [Fact]
    public async Task AssetVerification_MismatchIncludesModelDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);
        var path = Path.Combine(temporary.Path, "wrong-model.onnx");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);

        var exception = await Assert.ThrowsAsync<GraniteModelAssetException>(
            () => GraniteModelAssetCache.VerifyAssetAsync(path, configuration.ModelAsset));

        Assert.Equal(GraniteDiagnosticCode.HashMismatch, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Model, exception.AssetKind);
        Assert.Equal(GraniteModelMetadata.ModelSha256, exception.ExpectedSha256);
        Assert.Equal(64, exception.ActualSha256!.Length);
        Assert.Contains("model SHA-256", exception.Message);
    }

    [Fact]
    public async Task AssetVerification_MismatchIncludesTokenizerDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);
        var path = Path.Combine(temporary.Path, "wrong-tokenizer.model");
        await File.WriteAllBytesAsync(path, [4, 5, 6]);

        var exception = await Assert.ThrowsAsync<GraniteModelAssetException>(
            () => GraniteModelAssetCache.VerifyAssetAsync(path, configuration.TokenizerAsset));

        Assert.Equal(GraniteDiagnosticCode.HashMismatch, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Tokenizer, exception.AssetKind);
        Assert.Equal(GraniteModelMetadata.TokenizerSha256, exception.ExpectedSha256);
        Assert.Contains("tokenizer SHA-256", exception.Message);
    }

    [Fact]
    public async Task AssetVerification_MissingAssetReportsStableDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);

        var exception = await Assert.ThrowsAsync<GraniteModelAssetException>(
            () => new GraniteModelAssetCache(configuration).EnsureAssetsAsync());

        Assert.Equal(GraniteDiagnosticCode.MissingAsset, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Model, exception.AssetKind);
        Assert.Contains("external cache", exception.Message);
        Assert.DoesNotContain(configuration.CacheDirectory, exception.Message);
    }

    [Fact]
    public void AssetVerification_IncompatibleAssetReportsStableDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var path = Path.Combine(temporary.Path, GraniteModelMetadata.TokenizerFileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteSentencePieceTokenizer(path));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Tokenizer, exception.AssetKind);
        Assert.Contains("SentencePiece", exception.Message);
    }
}
