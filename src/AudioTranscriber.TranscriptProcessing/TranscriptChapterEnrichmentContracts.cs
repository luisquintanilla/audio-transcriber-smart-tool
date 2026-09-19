using System.Collections.ObjectModel;

namespace AudioTranscriber.TranscriptProcessing;

public static class TranscriptChapterEnrichmentSchema
{
    public const string CurrentVersion = "1.0";
}

public enum TranscriptChapterEnrichmentFailurePolicy
{
    FailFast,
    PreservePartial
}

public enum TranscriptChapterEnrichmentStatus
{
    Succeeded,
    Missing,
    Failed
}

/// <summary>
/// Configures an explicitly injected chapter enrichment provider.
/// </summary>
public sealed record TranscriptChapterEnrichmentOptions
{
    public string Algorithm { get; init; } = "chapter-enrichment-v1";

    public string Provider { get; init; } = "injected";

    public string? Model { get; init; }

    public IReadOnlyDictionary<string, string>? ProviderConfiguration { get; init; }

    public TranscriptChapterEnrichmentFailurePolicy FailurePolicy { get; init; } =
        TranscriptChapterEnrichmentFailurePolicy.FailFast;

    public bool IncludeOverallSummary { get; init; }

    internal TranscriptChapterEnrichmentGenerationMetadata ToMetadata()
    {
        if (string.IsNullOrWhiteSpace(Algorithm))
        {
            throw new ArgumentException(
                "Enrichment algorithm cannot be empty.",
                nameof(Algorithm));
        }

        if (string.IsNullOrWhiteSpace(Provider))
        {
            throw new ArgumentException(
                "Enrichment provider cannot be empty.",
                nameof(Provider));
        }

        if (!Enum.IsDefined(FailurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(FailurePolicy),
                "The enrichment failure policy is not supported.");
        }

        var configuration = new Dictionary<string, string>(
            StringComparer.Ordinal);
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

                if (!configuration.TryAdd(entry.Key.Trim(), entry.Value))
                {
                    throw new ArgumentException(
                        $"Duplicate provider configuration key '{entry.Key.Trim()}'.",
                        nameof(ProviderConfiguration));
                }
            }
        }

        return new TranscriptChapterEnrichmentGenerationMetadata(
            Algorithm.Trim(),
            Provider.Trim().ToLowerInvariant(),
            TranscriptProvenance.NormalizeOptional(Model),
            configuration,
            FailurePolicy);
    }
}

/// <summary>
/// Identifies the provider and policy used to create an enrichment artifact.
/// </summary>
public sealed record TranscriptChapterEnrichmentGenerationMetadata
{
    public TranscriptChapterEnrichmentGenerationMetadata(
        string algorithm,
        string provider,
        string? model = null,
        IReadOnlyDictionary<string, string>? configuration = null,
        TranscriptChapterEnrichmentFailurePolicy failurePolicy =
            TranscriptChapterEnrichmentFailurePolicy.FailFast)
    {
        if (string.IsNullOrWhiteSpace(algorithm))
        {
            throw new ArgumentException(
                "Enrichment algorithm cannot be empty.",
                nameof(algorithm));
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException(
                "Enrichment provider cannot be empty.",
                nameof(provider));
        }

        if (!Enum.IsDefined(failurePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(failurePolicy));
        }

        Algorithm = algorithm.Trim();
        Provider = provider.Trim().ToLowerInvariant();
        Model = TranscriptProvenance.NormalizeOptional(model);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configuration is not null)
        {
            foreach (var entry in configuration)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    throw new ArgumentException(
                        "Enrichment configuration keys cannot be empty.",
                        nameof(configuration));
                }

                if (entry.Value is null)
                {
                    throw new ArgumentException(
                        "Enrichment configuration values cannot be null.",
                        nameof(configuration));
                }

                var normalizedKey = entry.Key.Trim();
                if (!values.TryAdd(normalizedKey, entry.Value))
                {
                    throw new ArgumentException(
                        $"Duplicate enrichment configuration key '{normalizedKey}'.",
                        nameof(configuration));
                }
            }
        }

        Configuration = new ReadOnlyDictionary<string, string>(values);
        FailurePolicy = failurePolicy;
    }

    public string Algorithm { get; }

    public string Provider { get; }

    public string? Model { get; }

    public IReadOnlyDictionary<string, string> Configuration { get; }

    public TranscriptChapterEnrichmentFailurePolicy FailurePolicy { get; }
}

/// <summary>
/// A source-linked reference returned by a chapter enrichment provider.
/// </summary>
public sealed record TranscriptChapterEvidenceReference
{
    public TranscriptChapterEvidenceReference(
        string sourceSegmentId,
        TimeSpan start,
        TimeSpan end,
        string? sourceId = null)
    {
        if (string.IsNullOrWhiteSpace(sourceSegmentId))
        {
            throw new ArgumentException(
                "Evidence source segment ID cannot be empty.",
                nameof(sourceSegmentId));
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "Evidence end must be greater than its start.");
        }

        SourceSegmentId = sourceSegmentId.Trim();
        SourceId = TranscriptProvenance.NormalizeOptional(sourceId);
        Start = start;
        End = end;
    }

    public string SourceSegmentId { get; }

    public string? SourceId { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }
}

/// <summary>
/// The provider-neutral enrichment for one chapter.
/// </summary>
public sealed record TranscriptChapterSummary
{
    public TranscriptChapterSummary(
        string summary,
        IEnumerable<string>? keywords = null,
        string? title = null,
        IEnumerable<TranscriptChapterEvidenceReference>? evidence = null)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException(
                "Chapter summaries cannot be empty.",
                nameof(summary));
        }

        Summary = TranscriptText.NormalizeWhitespace(summary);
        Title = TranscriptProvenance.NormalizeOptional(title);
        Keywords = NormalizeKeywords(keywords);
        Evidence = NormalizeEvidence(evidence);
    }

    public string Summary { get; }

    public IReadOnlyList<string> Keywords { get; }

    public string? Title { get; }

    public IReadOnlyList<TranscriptChapterEvidenceReference> Evidence { get; }

    private static IReadOnlyList<string> NormalizeKeywords(
        IEnumerable<string>? keywords)
    {
        if (keywords is null)
        {
            return Array.Empty<string>();
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                throw new ArgumentException(
                    "Chapter keywords cannot be empty.",
                    nameof(keywords));
            }

            values.Add(TranscriptText.NormalizeWhitespace(keyword));
        }

        return values
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<TranscriptChapterEvidenceReference> NormalizeEvidence(
        IEnumerable<TranscriptChapterEvidenceReference>? evidence)
    {
        if (evidence is null)
        {
            return Array.Empty<TranscriptChapterEvidenceReference>();
        }

        var values = evidence.ToArray();
        if (values.Any(value => value is null))
        {
            throw new ArgumentException(
                "Chapter evidence cannot contain null entries.",
                nameof(evidence));
        }

        var duplicate = values
            .GroupBy(value => value.SourceSegmentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Chapter evidence source segment '{duplicate.Key}' is repeated.",
                nameof(evidence));
        }

        return Array.AsReadOnly(values);
    }
}

/// <summary>
/// Supplies provider-neutral enrichment for one timestamped chapter.
/// Returning null explicitly records missing enrichment under the partial policy.
/// </summary>
public interface ITranscriptChapterEnricher
{
    ValueTask<TranscriptChapterSummary?> EnrichAsync(
        TranscriptChapterEnrichmentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TranscriptChapterEnrichmentRequest
{
    public TranscriptChapterEnrichmentRequest(TranscriptChapterArtifact chapter)
    {
        Chapter = chapter ?? throw new ArgumentNullException(nameof(chapter));
    }

    public TranscriptChapterArtifact Chapter { get; }
}

/// <summary>
/// A chapter summary supplied to an overall-summary assembler.
/// </summary>
public sealed record TranscriptChapterSummaryReference
{
    public TranscriptChapterSummaryReference(
        string chapterId,
        TranscriptChapterSummary summary)
    {
        if (string.IsNullOrWhiteSpace(chapterId))
        {
            throw new ArgumentException(
                "Summary chapter IDs cannot be empty.",
                nameof(chapterId));
        }

        ChapterId = chapterId.Trim();
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public string ChapterId { get; }

    public TranscriptChapterSummary Summary { get; }
}

/// <summary>
/// Contains only chapter summaries; no full transcript text is exposed.
/// </summary>
public sealed class TranscriptOverallSummaryRequest
{
    public TranscriptOverallSummaryRequest(
        IEnumerable<TranscriptChapterSummaryReference> chapterSummaries,
        bool isPartial)
    {
        ArgumentNullException.ThrowIfNull(chapterSummaries);
        var values = chapterSummaries.ToArray();
        if (values.Any(value => value is null))
        {
            throw new ArgumentException(
                "Overall summary inputs cannot contain null entries.",
                nameof(chapterSummaries));
        }

        if (values.Length == 0)
        {
            throw new ArgumentException(
                "Overall summary inputs must contain at least one chapter.",
                nameof(chapterSummaries));
        }

        var duplicate = values
            .GroupBy(value => value.ChapterId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Overall summary chapter '{duplicate.Key}' is repeated.",
                nameof(chapterSummaries));
        }

        ChapterSummaries = Array.AsReadOnly(values);
        IsPartial = isPartial;
    }

    public IReadOnlyList<TranscriptChapterSummaryReference> ChapterSummaries { get; }

    public bool IsPartial { get; }
}

/// <summary>
/// Produces an overall summary from chapter summaries only.
/// </summary>
public interface ITranscriptOverallSummaryAssembler
{
    ValueTask<TranscriptOverallSummary?> AssembleAsync(
        TranscriptOverallSummaryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TranscriptOverallSummary
{
    public TranscriptOverallSummary(
        string summary,
        IEnumerable<string> chapterIds,
        bool isPartial)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException(
                "Overall summaries cannot be empty.",
                nameof(summary));
        }

        ArgumentNullException.ThrowIfNull(chapterIds);
        var values = chapterIds
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException(
                "Overall summaries must cite at least one chapter.",
                nameof(chapterIds));
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Overall summary chapter IDs cannot be empty.",
                nameof(chapterIds));
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException(
                "Overall summary chapter IDs must be unique.",
                nameof(chapterIds));
        }

        Summary = TranscriptText.NormalizeWhitespace(summary);
        ChapterIds = Array.AsReadOnly(values.Select(value => value.Trim()).ToArray());
        IsPartial = isPartial;
    }

    public string Summary { get; }

    public IReadOnlyList<string> ChapterIds { get; }

    public bool IsPartial { get; }
}

public sealed record TranscriptChapterEnrichmentFailure
{
    public TranscriptChapterEnrichmentFailure(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException(
                "Enrichment failure codes cannot be empty.",
                nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Enrichment failure messages cannot be empty.",
                nameof(message));
        }

        Code = code.Trim();
        Message = message.Trim();
    }

    public string Code { get; }

    public string Message { get; }
}

/// <summary>
/// Enrichment for one chapter, including its structural source snapshot.
/// </summary>
public sealed class TranscriptChapterEnrichment
{
    internal TranscriptChapterEnrichment(
        TranscriptChapterArtifact chapter,
        TranscriptChapterEnrichmentStatus status,
        TranscriptChapterSummary? summary,
        TranscriptChapterEnrichmentFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == TranscriptChapterEnrichmentStatus.Succeeded)
        {
            if (summary is null || failure is not null)
            {
                throw new ArgumentException(
                    "Succeeded enrichment must contain a summary and no failure.");
            }
        }
        else if (status == TranscriptChapterEnrichmentStatus.Missing)
        {
            if (summary is not null || failure is not null)
            {
                throw new ArgumentException(
                    "Missing enrichment cannot contain a summary or failure.");
            }
        }
        else if (summary is not null || failure is null)
        {
            throw new ArgumentException(
                "Failed enrichment must contain a failure and no summary.");
        }

        ChapterId = chapter.Id;
        ArtifactTitle = chapter.Title;
        Start = chapter.Start;
        End = chapter.End;
        SourceSegmentIds = chapter.SourceSegmentIds;
        SourceIds = chapter.SourceIds;
        SourceMetadata = chapter.SourceMetadata;
        Boundary = chapter.Boundary;
        Status = status;
        Summary = summary?.Summary;
        Keywords = summary?.Keywords ?? Array.Empty<string>();
        Title = summary?.Title;
        Evidence = summary?.Evidence ?? Array.Empty<TranscriptChapterEvidenceReference>();
        Failure = failure;
    }

    public string SchemaVersion => TranscriptChapterEnrichmentSchema.CurrentVersion;

    public string ChapterId { get; }

    public string ArtifactTitle { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public IReadOnlyList<TranscriptChapterSourceMetadata> SourceMetadata { get; }

    public TranscriptChapterBoundaryMetadata Boundary { get; }

    public TranscriptChapterEnrichmentStatus Status { get; }

    public string? Summary { get; }

    public IReadOnlyList<string> Keywords { get; }

    public string? Title { get; }

    public IReadOnlyList<TranscriptChapterEvidenceReference> Evidence { get; }

    public TranscriptChapterEnrichmentFailure? Failure { get; }
}

/// <summary>
/// A distinct, versioned enrichment artifact layered over a chapter artifact.
/// </summary>
public sealed class TranscriptChapterEnrichmentDocument
    : IReadOnlyList<TranscriptChapterEnrichment>
{
    internal TranscriptChapterEnrichmentDocument(
        TranscriptChapterArtifactDocument chapterArtifact,
        IEnumerable<TranscriptChapterEnrichment> chapters,
        TranscriptChapterEnrichmentGenerationMetadata generation,
        TranscriptOverallSummary? overallSummary,
        TranscriptChapterEnrichmentFailure? overallSummaryFailure)
    {
        ArgumentNullException.ThrowIfNull(chapterArtifact);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(generation);

        var values = chapters.ToArray();
        if (values.Length != chapterArtifact.Count)
        {
            throw new ArgumentException(
                "Enrichment output must contain one entry for every chapter.",
                nameof(chapters));
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is null)
            {
                throw new ArgumentException(
                    "Enrichment output cannot contain null entries.",
                    nameof(chapters));
            }

            if (!string.Equals(
                    values[index].ChapterId,
                    chapterArtifact[index].Id,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Enrichment output must preserve chapter order and IDs.",
                    nameof(chapters));
            }
        }

        if (overallSummary is not null && overallSummaryFailure is not null)
        {
            throw new ArgumentException(
                "An overall summary cannot be present together with an overall failure.");
        }

        Source = chapterArtifact.Source;
        Provenance = chapterArtifact.Provenance;
        ChapterGeneration = chapterArtifact.Generation;
        ChapterArtifactSchemaVersion = chapterArtifact.SchemaVersion;
        Generation = generation;
        Chapters = Array.AsReadOnly(values);
        OverallSummary = overallSummary;
        OverallSummaryFailure = overallSummaryFailure;
    }

    public string SchemaVersion => TranscriptChapterEnrichmentSchema.CurrentVersion;

    public string ChapterArtifactSchemaVersion { get; }

    public string Source { get; }

    public TranscriptProvenance Provenance { get; }

    public TranscriptChapterGenerationMetadata ChapterGeneration { get; }

    public TranscriptChapterEnrichmentGenerationMetadata Generation { get; }

    public IReadOnlyList<TranscriptChapterEnrichment> Chapters { get; }

    public TranscriptOverallSummary? OverallSummary { get; }

    public TranscriptChapterEnrichmentFailure? OverallSummaryFailure { get; }

    public bool IsPartial =>
        OverallSummaryFailure is not null ||
        Chapters.Any(chapter =>
            chapter.Status != TranscriptChapterEnrichmentStatus.Succeeded);

    public int Count => Chapters.Count;

    public TranscriptChapterEnrichment this[int index] => Chapters[index];

    public IEnumerator<TranscriptChapterEnrichment> GetEnumerator() =>
        Chapters.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}
