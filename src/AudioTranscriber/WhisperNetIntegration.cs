using System.Net.Http;
using System.Security.Cryptography;
using System.Security;
using System.Runtime.InteropServices;
using Whisper.net;

namespace AudioTranscriber;

public static class WhisperNetIntegration
{
    public const string PackageId = "Whisper.net";
    public const string RuntimePackageId = "Whisper.net.Runtime";
    public const string PackageVersion = "1.9.0";
    public const string ModelId = "openai/whisper-base";
    public const string ArtifactRepository = "sandrohanea/whisper.net";
    public const string ModelVersion = "v5/classic";
    public const string ModelFileName = "ggml-base.bin";
    public const string ModelUrl =
        "https://huggingface.co/sandrohanea/whisper.net/resolve/v5/classic/ggml-base.bin";
    public const string ModelSha256 =
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe";

    public static ModelProvenance Provenance(string cachePath) =>
        new(
            provider: "Whisper.net/whisper.cpp",
            model: ModelId,
            packageId: $"{PackageId};{RuntimePackageId}",
            packageVersion: PackageVersion,
            source:
                $"upstreamModelFamily={ModelId}; artifactRepository={ArtifactRepository}; " +
                $"artifactRevision={ModelVersion}; artifactFile={ModelFileName}; " +
                $"artifactUrl={ModelUrl}; sha256={ModelSha256}",
            cachePath: cachePath);
}

public sealed class WhisperNetModelCache
{
    private static readonly HttpClient DefaultHttpClient = new();
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private readonly HttpClient _httpClient;

    public WhisperNetModelCache(
        string? cacheDirectory = null,
        HttpClient? httpClient = null)
    {
        CacheDirectory = Path.GetFullPath(cacheDirectory ?? SmartToolPaths.DefaultModelCacheDirectory);
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public string CacheDirectory { get; }

    public string ModelPath => Path.Combine(CacheDirectory, WhisperNetIntegration.ModelFileName);

    public async Task<string> EnsureModelAsync(CancellationToken cancellationToken = default)
    {
        await DownloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            if (File.Exists(ModelPath))
            {
                await VerifyModelAsync(ModelPath, cancellationToken).ConfigureAwait(false);
                return ModelPath;
            }

            var temporaryPath = ModelPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.partial";
            try
            {
                using var response = await _httpClient.GetAsync(
                    WhisperNetIntegration.ModelUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var destination = File.Create(temporaryPath))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                await VerifyModelAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, ModelPath);
                return ModelPath;
            }
            catch (ModelIntegrationUnavailableException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                IOException or
                UnauthorizedAccessException or
                SecurityException)
            {
                throw new ModelIntegrationUnavailableException(
                    $"Whisper.net model download failed from {WhisperNetIntegration.ModelUrl}: " +
                    SafePathDisplay.RedactKnownPaths(
                        exception.Message,
                        SafePathDisplay.ModelFileToken,
                        temporaryPath,
                        ModelPath));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (ModelIntegrationUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            SecurityException)
        {
            throw new ModelIntegrationUnavailableException(
                $"Whisper.net model cache operation failed for '{SafePathDisplay.ModelCacheToken}': " +
                SafePathDisplay.RedactKnownPaths(
                    exception.Message,
                    SafePathDisplay.ModelCacheToken,
                    CacheDirectory,
                    ModelPath));
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task VerifyModelAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, WhisperNetIntegration.ModelSha256, StringComparison.Ordinal))
        {
            throw new ModelIntegrationUnavailableException(
                $"Whisper.net model SHA-256 mismatch for '{SafePathDisplay.ModelFileToken}'. " +
                $"Expected {WhisperNetIntegration.ModelSha256}, found {actual}.");
        }
    }
}

public sealed class WhisperNetEngine : ITranscriptionEngine
{
    private readonly WhisperNetModelCache _modelCache;

    public WhisperNetEngine(WhisperNetModelCache? modelCache = null)
    {
        _modelCache = modelCache ?? new WhisperNetModelCache();
        Provenance = WhisperNetIntegration.Provenance(_modelCache.CacheDirectory);
    }

    public ModelProvenance Provenance { get; }

    public async ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioClip audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        AudioRequirements.Validate(audio);
        cancellationToken.ThrowIfCancellationRequested();

        var modelPath = await _modelCache.EnsureModelAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            using var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .Build();

            var segments = new List<TranscriptSegment>();
            await foreach (var result in processor.ProcessAsync(
                               audio.Samples.ToArray(),
                               cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    segments.Add(new TranscriptSegment(result.Text, result.Start, result.End));
                }
            }

            return segments;
        }
        catch (ModelIntegrationUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is WhisperModelLoadException or
            WhisperProcessingException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException or
            UnauthorizedAccessException or
            SecurityException or
            IOException or
            InvalidDataException or
            TypeInitializationException or
            InvalidOperationException or
            ArgumentException)
        {
            throw new ModelIntegrationUnavailableException(
                $"Whisper.net transcription failed using model '{SafePathDisplay.ModelFileToken}': " +
                SafePathDisplay.RedactKnownPaths(
                    exception.Message,
                    SafePathDisplay.ModelFileToken,
                    modelPath,
                    _modelCache.CacheDirectory));
        }
    }
}
