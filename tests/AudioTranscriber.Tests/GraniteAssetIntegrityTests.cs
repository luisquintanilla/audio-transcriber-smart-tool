using System.Net;
using System.Security.Cryptography;
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

    [Fact]
    public void Tokenizer_PermissionFailureReportsStableDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var path = Path.Combine(temporary.Path, GraniteModelMetadata.TokenizerFileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteSentencePieceTokenizer(
                path,
                GraniteModelMetadata.MaxTokens,
                _ => throw new UnauthorizedAccessException(path)));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Tokenizer, exception.AssetKind);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tokenizer_SecurityFailureReportsStableDiagnostic()
    {
        using var temporary = new GraniteTestDirectory();
        var path = Path.Combine(temporary.Path, GraniteModelMetadata.TokenizerFileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var exception = Assert.Throws<GraniteModelAssetException>(
            () => new GraniteSentencePieceTokenizer(
                path,
                GraniteModelMetadata.MaxTokens,
                _ => throw new System.Security.SecurityException(path)));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.Equal(GraniteAssetKind.Tokenizer, exception.AssetKind);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssetVerification_PermissionDeniedIsWrappedAndPathRedacted()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);
        var directoryPath = Path.Combine(temporary.Path, "unreadable-model.onnx");
        Directory.CreateDirectory(directoryPath);

        var exception = await Assert.ThrowsAsync<GraniteModelAssetException>(
            () => GraniteModelAssetCache.VerifyAssetAsync(
                directoryPath,
                configuration.ModelAsset));

        Assert.Equal(GraniteDiagnosticCode.IncompatibleAsset, exception.DiagnosticCode);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(
            directoryPath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssetCache_ConcurrentInstancesPublishVerifiedAssetsSafely()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(
            temporary.Path,
            allowNetworkDownload: true);
        var modelBytes = new byte[] { 1, 2, 3, 4 };
        var tokenizerBytes = new byte[] { 5, 6, 7, 8 };
        var modelAsset = TestAsset(GraniteAssetKind.Model, "test-model.onnx", modelBytes);
        var tokenizerAsset = TestAsset(
            GraniteAssetKind.Tokenizer,
            "test-tokenizer.model",
            tokenizerBytes);
        using var client = new HttpClient(new ConcurrentAssetHandler(modelBytes, tokenizerBytes));
        var first = new GraniteModelAssetCache(
            configuration,
            client,
            modelAsset,
            tokenizerAsset);
        var second = new GraniteModelAssetCache(
            configuration,
            client,
            modelAsset,
            tokenizerAsset);

        var results = await Task.WhenAll(
            first.EnsureAssetsAsync(),
            second.EnsureAssetsAsync());

        Assert.All(results, result =>
        {
            Assert.Equal(first.ModelPath, result.ModelPath);
            Assert.Equal(first.TokenizerPath, result.TokenizerPath);
        });
        await GraniteModelAssetCache.VerifyAssetAsync(first.ModelPath, modelAsset);
        await GraniteModelAssetCache.VerifyAssetAsync(first.TokenizerPath, tokenizerAsset);
    }

    private static GraniteAssetDescriptor TestAsset(
        GraniteAssetKind kind,
        string fileName,
        byte[] content) =>
        new(
            kind,
            fileName,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            new Uri($"https://example.test/{fileName}"));

    private sealed class ConcurrentAssetHandler : HttpMessageHandler
    {
        private readonly byte[] modelBytes;
        private readonly byte[] tokenizerBytes;

        public ConcurrentAssetHandler(byte[] modelBytes, byte[] tokenizerBytes)
        {
            this.modelBytes = modelBytes;
            this.tokenizerBytes = tokenizerBytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken);
            var content = request.RequestUri!.AbsolutePath.EndsWith(
                    "test-model.onnx",
                    StringComparison.Ordinal)
                ? modelBytes
                : tokenizerBytes;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
        }
    }
}
