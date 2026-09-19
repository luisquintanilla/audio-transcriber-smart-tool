using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// Options for deterministic or explicitly injected transcript chapter generation.
/// </summary>
public sealed class TranscriptChapterGenerationOptions
{
    public string Algorithm { get; init; } = "segment-boundary-v1";

    public string Provider { get; init; } = "deterministic";

    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan? RequestedStart { get; init; }

    public TimeSpan? RequestedEnd { get; init; }

    public IReadOnlyDictionary<string, string>? ProviderConfiguration { get; init; }

    internal TranscriptChunkingOptions ToChunkingOptions()
    {
        Validate();
        return new TranscriptChunkingOptions
        {
            MinimumDuration = MinimumDuration,
            MaximumDuration = MaximumDuration,
            RequestedStart = RequestedStart,
            RequestedEnd = RequestedEnd
        };
    }

    internal TranscriptChapterGenerationMetadata ToMetadata()
    {
        Validate();

        var configuration = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["minimumDuration"] = FormatDuration(MinimumDuration),
            ["maximumDuration"] = FormatDuration(MaximumDuration)
        };

        if (RequestedStart is not null)
        {
            configuration["requestedStart"] =
                TranscriptJsonWriter.FormatTimestamp(RequestedStart.Value);
        }

        if (RequestedEnd is not null)
        {
            configuration["requestedEnd"] =
                TranscriptJsonWriter.FormatTimestamp(RequestedEnd.Value);
        }

        if (ProviderConfiguration is not null)
        {
            foreach (var entry in ProviderConfiguration)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    throw new ArgumentException(
                        "Provider configuration keys cannot be empty.",
                        nameof(ProviderConfiguration));
                }

                if (entry.Value is null)
                {
                    throw new ArgumentException(
                        "Provider configuration values cannot be null.",
                        nameof(ProviderConfiguration));
                }

                configuration[entry.Key.Trim()] = entry.Value;
            }
        }

        return new TranscriptChapterGenerationMetadata(
            Algorithm.Trim(),
            Provider.Trim().ToLowerInvariant(),
            configuration);
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Algorithm))
        {
            throw new ArgumentException(
                "Chapter algorithm cannot be empty.",
                nameof(Algorithm));
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ArgumentException(
                "Chapter provider cannot be empty.",
                nameof(Provider));
        }

        if (MinimumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Minimum chapter duration must be greater than zero.",
                nameof(MinimumDuration));
        }

        if (MaximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Maximum chapter duration must be greater than zero.",
                nameof(MaximumDuration));
        }

        if (MinimumDuration > MaximumDuration)
        {
            throw new ArgumentException(
                "Minimum chapter duration cannot exceed maximum chapter duration.",
                nameof(MinimumDuration));
        }

        if (RequestedStart is not null && RequestedStart.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestedStart),
                "Requested chapter start cannot be negative.");
        }

        if (RequestedEnd is not null && RequestedEnd.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestedEnd),
                "Requested chapter end cannot be negative.");
        }

        if (RequestedStart is not null &&
            RequestedEnd is not null &&
            RequestedEnd <= RequestedStart)
        {
            throw new ArgumentException(
                "Requested chapter end must be greater than requested start.",
                nameof(RequestedEnd));
        }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);
}

/// <summary>
/// Identifies how a chapter artifact was produced.
/// </summary>
public sealed record TranscriptChapterGenerationMetadata
{
    public TranscriptChapterGenerationMetadata(
        string algorithm,
        string provider,
        IReadOnlyDictionary<string, string>? configuration = null)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            throw new ArgumentException(
                "Chapter algorithm cannot be empty.",
                nameof(algorithm));
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException(
                "Chapter provider cannot be empty.",
                nameof(provider));
        }

        Algorithm = algorithm.Trim();
        Provider = provider.Trim().ToLowerInvariant();
        Configuration = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                configuration ?? new Dictionary<string, string>(),
                StringComparer.Ordinal));
    }

    public string Algorithm { get; }

    public string Provider { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }
}

/// <summary>
/// Generates chapter artifacts from one validated transcript document.
/// </summary>
/// <remarks>
/// The default provider is structural and deterministic. A non-deterministic
/// provider name is only accepted when embedding and scoring seams are injected;
/// this keeps model selection explicit and prevents an implicit model download.
/// </remarks>
public sealed class TranscriptChapterGenerator
{
    private readonly IEmbeddingGenerator<TextContent, Embedding<float>>? embeddingGenerator;
    private readonly ITranscriptChunkScoringProvider? scoringProvider;
    private readonly TranscriptJsonReader reader;
    private readonly TranscriptChapterArtifactGenerator artifactGenerator;

    public TranscriptChapterGenerator(
        IEmbeddingGenerator<TextContent, Embedding<float>>? embeddingGenerator = null,
        ITranscriptChunkScoringProvider? scoringProvider = null,
        TranscriptJsonReader? reader = null,
        TranscriptChapterArtifactGenerator? artifactGenerator = null)
    {
        this.embeddingGenerator = embeddingGenerator;
        this.scoringProvider = scoringProvider;
        this.reader = reader ?? new TranscriptJsonReader();
        this.artifactGenerator = artifactGenerator ?? new TranscriptChapterArtifactGenerator();
    }

    public TranscriptChapterArtifactDocument Generate(
        TranscriptDocument document,
        TranscriptChapterGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new TranscriptChapterGenerationOptions();
        var metadata = options.ToMetadata();
        EnsureProviderCanRunSynchronously(metadata.Provider);

        var result = new TranscriptChunkBuilder().Build(
            TranscriptIngestionAdapter.ToIngestionDocument(document),
            options.ToChunkingOptions());
        return artifactGenerator.Generate(result, metadata);
    }

    public async Task<TranscriptChapterArtifactDocument> GenerateAsync(
        TranscriptDocument document,
        TranscriptChapterGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new TranscriptChapterGenerationOptions();
        cancellationToken.ThrowIfCancellationRequested();

        var ingestionDocument = TranscriptIngestionAdapter.ToIngestionDocument(document);
        return await GenerateAsync(
                ingestionDocument,
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TranscriptChapterArtifactDocument> GenerateAsync(
        IngestionDocument document,
        TranscriptChapterGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new TranscriptChapterGenerationOptions();
        var metadata = options.ToMetadata();
        cancellationToken.ThrowIfCancellationRequested();

        TranscriptChunkResult result;
        if (string.Equals(
                metadata.Provider,
                "deterministic",
                StringComparison.OrdinalIgnoreCase))
        {
            result = new TranscriptChunkBuilder().Build(
                document,
                options.ToChunkingOptions());
        }
        else
        {
            var embedding = embeddingGenerator
                ?? throw new InvalidOperationException(
                    $"Chapter provider '{metadata.Provider}' was selected, " +
                    "but no embedding generator was supplied.");
            var scoring = scoringProvider ?? new EmbeddedChunkScoringProvider();
            result = await new TranscriptChunkBuilder(embedding, scoring)
                .BuildAsync(
                    document,
                    options.ToChunkingOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return artifactGenerator.Generate(result, metadata);
    }

    public async Task<TranscriptChapterArtifactDocument> GenerateFromFileAsync(
        string inputPath,
        TranscriptChapterGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException(
                "Transcript input path cannot be empty.",
                nameof(inputPath));
        }

        await using var stream = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await reader
            .ReadDocumentAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return await GenerateAsync(document, options, cancellationToken)
            .ConfigureAwait(false);
    }

    private void EnsureProviderCanRunSynchronously(string provider)
    {
        if (!string.Equals(provider, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Chapter provider '{provider}' requires GenerateAsync so its " +
                "external provider can be awaited.");
        }
    }

    private sealed class EmbeddedChunkScoringProvider
        : ITranscriptChunkScoringProvider
    {
        public ValueTask<double> ScoreAsync(
            TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Embedding is null || request.Embedding.Vector.Length == 0)
            {
                throw new InvalidOperationException(
                    "The selected chapter provider did not return an embedding.");
            }

            var magnitude = Math.Sqrt(
                request.Embedding.Vector.ToArray()
                    .Sum(value => value * (double)value));
            return ValueTask.FromResult(double.IsFinite(magnitude) ? magnitude : 0d);
        }
    }
}

/// <summary>
/// Writes chapter artifacts through a temporary file and an atomic destination move.
/// </summary>
public sealed class TranscriptChapterArtifactFileWriter
{
    private readonly TranscriptChapterArtifactGenerator artifactGenerator;

    public TranscriptChapterArtifactFileWriter(
        TranscriptChapterArtifactGenerator? artifactGenerator = null)
    {
        this.artifactGenerator = artifactGenerator ?? new TranscriptChapterArtifactGenerator();
    }

    public async Task WriteAsync(
        string outputPath,
        TranscriptChapterArtifactDocument document,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(
                "Chapter output path cannot be empty.",
                nameof(outputPath));
        }

        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("Chapter output directory could not be resolved.");
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException(
                $"Chapter output already exists at '{Path.GetFileName(destination)}'. " +
                "Pass overwrite=true to replace it explicitly.");
        }

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Environment.ProcessId}." +
            $"{Guid.NewGuid():N}.partial");
        var content = Encoding.UTF8.GetBytes(artifactGenerator.Serialize(document));

        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(destination))
            {
                File.Replace(temporary, destination, null);
            }
            else
            {
                File.Move(temporary, destination);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
