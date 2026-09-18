namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// A narrow embedding request that does not depend on a model SDK.
/// </summary>
public sealed record TranscriptEmbeddingRequest
{
    public TranscriptEmbeddingRequest(string text)
    {
        Text = RequireText(text, nameof(text));
    }

    public string Text { get; }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Embedding text cannot be empty.", parameterName)
            : value.Trim();
}

/// <summary>
/// A model-independent embedding response.
/// </summary>
public sealed record TranscriptEmbeddingResponse
{
    public TranscriptEmbeddingResponse(IEnumerable<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);

        var values = vector.ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("Embedding vectors cannot be empty.", nameof(vector));
        }

        if (values.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException(
                "Embedding vectors must contain only finite values.",
                nameof(vector));
        }

        Vector = Array.AsReadOnly(values);
    }

    public IReadOnlyList<float> Vector { get; }
}

/// <summary>
/// Supplies embeddings without coupling transcript processing to a model runtime.
/// </summary>
public interface ITranscriptEmbeddingProvider
{
    ValueTask<TranscriptEmbeddingResponse> EmbedAsync(
        TranscriptEmbeddingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A candidate window supplied to a model-independent chunk scorer.
/// </summary>
public sealed record TranscriptChunkScoringRequest
{
    public TranscriptChunkScoringRequest(
        string text,
        IEnumerable<TranscriptSegment> segments,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<float>? embedding = null)
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
        Embedding = embedding is null
            ? null
            : Array.AsReadOnly(embedding.ToArray());
    }

    public string Text { get; }

    public IReadOnlyList<TranscriptSegment> Segments { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<float>? Embedding { get; }
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
        TimeSpan start,
        TimeSpan end,
        IEnumerable<TranscriptSegment> sourceSegments,
        double? score = null)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        ArgumentNullException.ThrowIfNull(sourceSegments);
        var segments = sourceSegments.ToArray();
        if (segments.Any(segment => segment is null))
        {
            throw new ArgumentException(
                "Window source segments cannot contain null entries.",
                nameof(sourceSegments));
        }

        Start = start;
        End = end;
        SourceSegments = Array.AsReadOnly(segments);
        SourceSegmentIds = Array.AsReadOnly(
            segments.Select(segment => segment.Id).ToArray());
        SourceIds = Array.AsReadOnly(
            segments
                .Select(segment => segment.SourceId)
                .Where(sourceId => sourceId is not null)
                .Cast<string>()
                .ToArray());
        Text = string.Join(' ', segments.Select(segment => segment.Text));
        Score = score;
        IsGap = segments.Length == 0;
        Id = CreateId(start, end, segments);
    }

    public string Id { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public string Text { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public IReadOnlyList<TranscriptSegment> SourceSegments { get; }

    public bool IsGap { get; }

    public double? Score { get; }

    internal TranscriptChunkWindow WithScore(double score) =>
        new(Start, End, SourceSegments, score);

    private static string CreateId(
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var sourceIds = string.Join(
            "\u001f",
            segments.Select(segment => segment.Id));
        return $"window-{start.Ticks:x}-{end.Ticks:x}-{sourceIds}";
    }
}

/// <summary>
/// The ordered output of transcript chunk construction.
/// </summary>
public sealed class TranscriptChunkResult
{
    internal TranscriptChunkResult(
        string source,
        TranscriptProvenance provenance,
        IEnumerable<TranscriptChunkWindow> windows)
    {
        Source = source;
        Provenance = provenance;
        Windows = Array.AsReadOnly(windows.ToArray());
    }

    public string SchemaVersion => TranscriptChunkSchema.CurrentVersion;

    public string Source { get; }

    public TranscriptProvenance Provenance { get; }

    public IReadOnlyList<TranscriptChunkWindow> Windows { get; }
}

public static class TranscriptChunkSchema
{
    public const string CurrentVersion = "1.0";
}
