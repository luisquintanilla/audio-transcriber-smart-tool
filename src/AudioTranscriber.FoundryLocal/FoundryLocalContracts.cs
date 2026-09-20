using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

public enum FoundryLocalDiagnosticCode
{
    RuntimeUnavailable,
    ModelUnavailable,
    ModelNotReady,
    EndpointUnavailable,
    EndpointNotLocal,
    InvalidConfiguration
}

public static class FoundryLocalModelContract
{
    public const string RequiredModelAlias = "qwen3.5-0.8b-generic-cpu:3";
}

public sealed record FoundryLocalModelAvailability
{
    public FoundryLocalModelAvailability(
        string alias,
        string modelId,
        bool isCached,
        bool isLoaded)
    {
        Alias = RequireText(alias, nameof(alias));
        ModelId = RequireText(modelId, nameof(modelId));
        IsCached = isCached;
        IsLoaded = isLoaded;
    }

    public string Alias { get; }

    public string ModelId { get; }

    public bool IsCached { get; }

    public bool IsLoaded { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
}

public sealed record FoundryLocalReadiness
{
    public FoundryLocalReadiness(
        FoundryLocalDiagnosticCode? diagnosticCode,
        string modelAlias,
        string? modelId,
        IEnumerable<FoundryLocalModelAvailability>? availableModels,
        string message,
        Uri? endpoint = null,
        string? cachePath = null)
    {
        if (string.IsNullOrWhiteSpace(modelAlias))
        {
            throw new ArgumentException("Model alias cannot be empty.", nameof(modelAlias));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Readiness message cannot be empty.", nameof(message));
        }

        ModelAlias = modelAlias.Trim();
        ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        AvailableModels = new ReadOnlyCollection<FoundryLocalModelAvailability>(
            (availableModels ?? [])
                .Select(model => model ?? throw new ArgumentException(
                    "Available models cannot contain null entries.",
                    nameof(availableModels)))
                .ToArray());
        Message = message.Trim();
        Endpoint = endpoint;
        CachePath = string.IsNullOrWhiteSpace(cachePath) ? null : cachePath.Trim();
        DiagnosticCode = diagnosticCode;
    }

    public string ModelAlias { get; }

    public string? ModelId { get; }

    public IReadOnlyList<FoundryLocalModelAvailability> AvailableModels { get; }

    public string Message { get; }

    public Uri? Endpoint { get; }

    public string? CachePath { get; }

    public FoundryLocalDiagnosticCode? DiagnosticCode { get; }

    public bool IsReady =>
        DiagnosticCode is null &&
        ModelId is not null;
}

/// <summary>
/// Explicit configuration for a Foundry Local enrichment provider.
/// </summary>
public sealed record FoundryLocalEnrichmentOptions
{
    public FoundryLocalEnrichmentOptions(string modelAlias)
    {
        ModelAlias = RequireText(modelAlias, nameof(modelAlias));
    }

    public string ModelAlias { get; }

    public string ApplicationName { get; init; } = "audio-transcriber";

    public string? ModelCacheDirectory { get; init; }

    public bool AllowModelDownload { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxOutputTokens { get; init; } = 1200;

    public double Temperature { get; init; }

    public int Seed { get; init; }

    public bool DoSample { get; init; }

    public int MaxResponseAttempts { get; init; } = 2;

    public void Validate()
    {
        if (!string.Equals(
                ModelAlias,
                FoundryLocalModelContract.RequiredModelAlias,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Foundry Local enrichment requires the exact model " +
                $"'{FoundryLocalModelContract.RequiredModelAlias}'; no variant or " +
                "provider fallback is supported.",
                nameof(ModelAlias));
        }

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new ArgumentException(
                "Foundry Local application name cannot be empty.",
                nameof(ApplicationName));
        }

        if (ModelCacheDirectory is not null)
        {
            var cachePath = Path.GetFullPath(ModelCacheDirectory);
            var packagePath = Path.GetFullPath(AppContext.BaseDirectory);
            var normalizedCachePath = NormalizeDirectoryPath(cachePath);
            var normalizedPackagePath = NormalizeDirectoryPath(packagePath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(
                    normalizedCachePath,
                    normalizedPackagePath,
                    comparison) ||
                normalizedCachePath.StartsWith(
                    normalizedPackagePath + Path.DirectorySeparatorChar,
                    comparison) ||
                normalizedCachePath.StartsWith(
                    normalizedPackagePath + Path.AltDirectorySeparatorChar,
                    comparison))
            {
                throw new ArgumentException(
                    "The Foundry Local model cache must be external to the application package.",
                    nameof(ModelCacheDirectory));
            }
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestTimeout),
                "The Foundry Local request timeout must be greater than zero.");
        }

        if (MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }

        if (Seed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Seed),
                "Seed must be zero or greater.");
        }

        if (MaxResponseAttempts is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResponseAttempts),
                "MaxResponseAttempts must be one or two.");
        }

        if (!double.IsFinite(Temperature) || Temperature < 0 || Temperature > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Temperature),
                "Temperature must be finite and between zero and two.");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                path,
                root,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return root;
        }

        return path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();
}

public interface IFoundryLocalRuntime
{
    Task<FoundryLocalRuntimeSession> PrepareAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class FoundryLocalRuntimeSession : IAsyncDisposable
{
    private readonly Func<ValueTask>? disposeAsync;
    private int disposed;

    public FoundryLocalRuntimeSession(
        string modelAlias,
        string modelId,
        Uri? endpoint,
        string? cachePath,
        IChatClient chatClient,
        Func<ValueTask>? disposeAsync = null)
    {
        if (string.IsNullOrWhiteSpace(modelAlias))
        {
            throw new ArgumentException("Model alias cannot be empty.", nameof(modelAlias));
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID cannot be empty.", nameof(modelId));
        }

        ArgumentNullException.ThrowIfNull(chatClient);

        ModelAlias = modelAlias.Trim();
        ModelId = modelId.Trim();
        Endpoint = endpoint;
        CachePath = string.IsNullOrWhiteSpace(cachePath) ? null : cachePath.Trim();
        ChatClient = chatClient;
        this.disposeAsync = disposeAsync;
    }

    public string ModelAlias { get; }

    public string ModelId { get; }

    public Uri? Endpoint { get; }

    public string? CachePath { get; }

    public IChatClient ChatClient { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ChatClient.Dispose();
        }
        finally
        {
            if (disposeAsync is not null)
            {
                await disposeAsync().ConfigureAwait(false);
            }
        }
    }
}

public sealed class FoundryLocalProviderException
    : InvalidOperationException, ITranscriptProviderFailure
{
    public FoundryLocalProviderException(
        FoundryLocalDiagnosticCode code,
        string modelAlias,
        string message,
        FoundryLocalReadiness? readiness = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        DiagnosticCode = code;
        ModelAlias = modelAlias;
        Readiness = readiness;
    }

    public FoundryLocalDiagnosticCode DiagnosticCode { get; }

    public string ModelAlias { get; }

    public FoundryLocalReadiness? Readiness { get; }

    public string Code =>
        $"foundry_{DiagnosticCode.ToString().ToLowerInvariant()}";

    public string SanitizedMessage => Message;
}
