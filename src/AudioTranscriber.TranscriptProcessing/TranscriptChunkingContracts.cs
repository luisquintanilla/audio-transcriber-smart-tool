using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// A candidate window supplied to a model-independent chunk scorer.
/// </summary>
public sealed record TranscriptChunkScoringRequest
{
    public TranscriptChunkScoringRequest(
        string text,
        IEnumerable<TranscriptSegmentMetadata> segments,
        TimeSpan start,
        TimeSpan end,
        Embedding<float>? embedding = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Chunk scoring text cannot be empty.",
                nameof(text));
        }

        ArgumentNullException.ThrowIfNull(segments);
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "Chunk start cannot be negative.");
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "Chunk end must be greater than its start.");
        }

        var sourceSegments = segments.ToArray();
        if (sourceSegments.Length == 0)
        {
            throw new ArgumentException(
                "Chunk scoring requests must contain at least one source segment.",
                nameof(segments));
        }

        if (sourceSegments.Any(segment => segment is null))
        {
            throw new ArgumentException(
                "Chunk scoring segments cannot contain null entries.",
                nameof(segments));
        }

        Text = text.Trim();
        Segments = Array.AsReadOnly(sourceSegments);
        Start = start;
        End = end;
        Embedding = embedding;
    }

    public string Text { get; }

    public IReadOnlyList<TranscriptSegmentMetadata> Segments { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public Embedding<float>? Embedding { get; }
}

/// <summary>
/// Scores transcript-aware candidate windows for later semantic boundary decisions.
/// </summary>
public interface ITranscriptChunkScoringProvider
{
    ValueTask<double> ScoreAsync(
        TranscriptChunkScoringRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deterministic bounds for transcript chunk construction.
/// </summary>
public sealed record TranscriptChunkingOptions
{
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan? RequestedStart { get; init; }

    public TimeSpan? RequestedEnd { get; init; }
}

/// <summary>
/// A transcript window aligned to complete source segments or an explicit timing gap.
/// </summary>
public sealed class TranscriptChunkWindow
{
    internal TranscriptChunkWindow(
        IngestionDocument document,
        TimeSpan start,
        TimeSpan end,
        IEnumerable<TranscriptChunkSourceElement> sourceElements,
        double? score = null,
        double? semanticSimilarity = null)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourceElements);
        var elements = sourceElements.ToArray();
        if (elements.Any(element => element is null))
        {
            throw new ArgumentException(
                "Window source elements cannot contain null entries.",
                nameof(sourceElements));
        }

        Document = document;
        Start = start;
        End = end;
        SourceElements = Array.AsReadOnly(
            elements.Select(element => element.Element).ToArray());
        SourceSegments = Array.AsReadOnly(
            elements.Select(element => element.Metadata).ToArray());
        SourceSegmentIds = Array.AsReadOnly(
            SourceSegments.Select(segment => segment.Id).ToArray());
        SourceIds = Array.AsReadOnly(
            SourceSegments
                .Select(segment => segment.SourceId)
                .Where(sourceId => sourceId is not null)
                .Cast<string>()
                .ToArray());
        Text = string.Join(
            ' ',
            SourceElements.Select(element => element.Text ?? element.GetMarkdown()));
        Score = score;
        SemanticSimilarity = semanticSimilarity;
        IsGap = elements.Length == 0;
        Id = CreateId(start, end, SourceSegments);
    }

    public IngestionDocument Document { get; }

    public string Id { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public string Text { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public IReadOnlyList<IngestionDocumentParagraph> SourceElements { get; }

    public IReadOnlyList<TranscriptSegmentMetadata> SourceSegments { get; }

    public bool IsGap { get; }

    public double? Score { get; }

    public double? SemanticSimilarity { get; }

    internal TranscriptChunkWindow WithScore(
        double score,
        double? semanticSimilarity) =>
        new(
            Document,
            Start,
            End,
            SourceSegments
                .Select(
                    (metadata, index) =>
                        new TranscriptChunkSourceElement(
                            SourceElements[index],
                            metadata)),
            score,
            semanticSimilarity);

    private static string CreateId(
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<TranscriptSegmentMetadata> segments)
    {
        var sourceIds = string.Join(
            "\u001f",
            segments.Select(segment => segment.Id));
        return $"window-{start.Ticks:x}-{end.Ticks:x}-{sourceIds}";
    }

}

internal sealed record TranscriptChunkSourceElement(
    IngestionDocumentParagraph Element,
    TranscriptSegmentMetadata Metadata);

/// <summary>
/// The ordered output of transcript chunk construction.
/// </summary>
public sealed class TranscriptChunkResult
{
    internal TranscriptChunkResult(
        IngestionDocument document,
        TranscriptDocumentMetadata metadata,
        IEnumerable<TranscriptChunkWindow> windows)
    {
        Document = document;
        Source = metadata.Source;
        Provenance = metadata.Provenance;
        Metadata = metadata;
        Windows = Array.AsReadOnly(windows.ToArray());
    }

    public string SchemaVersion => TranscriptChunkSchema.CurrentVersion;

    public string Source { get; }

    public TranscriptProvenance Provenance { get; }

    internal TranscriptDocumentMetadata Metadata { get; }

    public IngestionDocument Document { get; }

    public IReadOnlyList<TranscriptChunkWindow> Windows { get; }
}

public static class TranscriptChunkSchema
{
    public const string CurrentVersion = "1.0";
}
