using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Security.Cryptography;

namespace AudioTranscriber.Granite;

public sealed record GraniteModelAssets(
    string ModelPath,
    string TokenizerPath);

public sealed class GraniteModelAssetCache
{
    private static readonly HttpClient DefaultHttpClient = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly GraniteModelConfiguration configuration;
    private readonly HttpClient httpClient;
    private readonly GraniteAssetDescriptor modelAsset;
    private readonly GraniteAssetDescriptor tokenizerAsset;
    private readonly SemaphoreSlim gate;

    public GraniteModelAssetCache(
        GraniteModelConfiguration configuration,
        HttpClient? httpClient = null)
        : this(configuration, httpClient, null, null)
    {
    }

    internal GraniteModelAssetCache(
        GraniteModelConfiguration configuration,
        HttpClient? httpClient,
        GraniteAssetDescriptor? modelAsset,
        GraniteAssetDescriptor? tokenizerAsset)
    {
        this.configuration = configuration
            ?? throw new ArgumentNullException(nameof(configuration));
        this.httpClient = httpClient ?? DefaultHttpClient;
        this.modelAsset = modelAsset ?? configuration.ModelAsset;
        this.tokenizerAsset = tokenizerAsset ?? configuration.TokenizerAsset;
        gate = CacheGates.GetOrAdd(
            configuration.CacheDirectory,
            static _ => new SemaphoreSlim(1, 1));
    }

    public string ModelPath =>
        Path.Combine(configuration.CacheDirectory, modelAsset.FileName);

    public string TokenizerPath =>
        Path.Combine(configuration.CacheDirectory, tokenizerAsset.FileName);

    public async Task<GraniteModelAssets> EnsureAssetsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                Directory.CreateDirectory(configuration.CacheDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                SecurityException)
            {
                throw new GraniteModelAssetException(
                    GraniteDiagnosticCode.DownloadFailed,
                    GraniteAssetKind.Model,
                    "The Granite model cache could not be created.");
            }

            await EnsureAssetAsync(
                    modelAsset,
                    ModelPath,
                    cancellationToken)
                .ConfigureAwait(false);
            await EnsureAssetAsync(
                    tokenizerAsset,
                    TokenizerPath,
                    cancellationToken)
                .ConfigureAwait(false);

            return new GraniteModelAssets(ModelPath, TokenizerPath);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task VerifyAssetAsync(
        string path,
        GraniteAssetDescriptor asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(asset);

        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            if (!string.Equals(actual, asset.Sha256, StringComparison.Ordinal))
            {
                throw new GraniteModelAssetException(
                    GraniteDiagnosticCode.HashMismatch,
                    asset.Kind,
                    $"The Granite {AssetLabel(asset.Kind)} SHA-256 does not match the pinned revision.",
                    asset.Sha256,
                    actual);
            }
        }
        catch (GraniteModelAssetException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.MissingAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset is missing from the external cache.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.MissingAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset is missing from the external cache.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset could not be read.");
        }
        catch (SecurityException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset could not be read.");
        }
        catch (IOException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.IncompatibleAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset could not be read.");
        }
    }

    private async Task EnsureAssetAsync(
        GraniteAssetDescriptor asset,
        string path,
        CancellationToken cancellationToken)
    {
        if (await TryVerifyExistingAssetAsync(asset, path, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        if (!configuration.AllowNetworkDownload)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.MissingAsset,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset is missing from the external cache. " +
                "Provision the exact pinned asset or explicitly enable network downloads.");
        }

        var temporaryPath =
            $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.partial";
        try
        {
            using var response = await httpClient.GetAsync(
                    asset.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                throw new GraniteModelAssetException(
                    GraniteDiagnosticCode.DownloadFailed,
                    asset.Kind,
                    $"The pinned Granite {AssetLabel(asset.Kind)} asset download returned " +
                    $"HTTP {(int)response.StatusCode}.");
            }

            await using (var source = await response.Content
                               .ReadAsStreamAsync(cancellationToken)
                               .ConfigureAwait(false))
            await using (var destination = File.Create(temporaryPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            await VerifyAssetAsync(temporaryPath, asset, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (GraniteModelAssetException)
        {
            throw;
        }
        catch (IOException)
        {
            if (await TryVerifyExistingAssetAsync(asset, path, cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.DownloadFailed,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset could not be downloaded.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.DownloadFailed,
                asset.Kind,
                $"The pinned Granite {AssetLabel(asset.Kind)} asset could not be downloaded.");
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<bool> TryVerifyExistingAssetAsync(
        GraniteAssetDescriptor asset,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyAssetAsync(path, asset, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GraniteModelAssetException exception)
            when (exception.DiagnosticCode == GraniteDiagnosticCode.MissingAsset)
        {
            return false;
        }
    }

    private static string AssetLabel(GraniteAssetKind kind) =>
        kind == GraniteAssetKind.Model ? "model" : "tokenizer";
}
