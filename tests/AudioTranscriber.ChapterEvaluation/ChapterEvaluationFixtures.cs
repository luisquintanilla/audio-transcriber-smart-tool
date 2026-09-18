using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.ChapterEvaluation;

/// <summary>
/// A labeled transcript fixture with expected chapter boundaries.
/// </summary>
public sealed class ChapterEvaluationFixture
{
    public const string CurrentSchemaVersion = "1.0";

    public ChapterEvaluationFixture(
        string id,
        string source,
        TranscriptProvenance provenance,
        IEnumerable<TranscriptSegment> segments,
        IEnumerable<ExpectedChapterBoundary> expectedChapters,
        string schemaVersion = CurrentSchemaVersion)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Fixture ID cannot be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Fixture source cannot be empty.", nameof(source));
        }

        if (!string.Equals(
                schemaVersion,
                CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported chapter evaluation schema version '{schemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(expectedChapters);

        var segmentValues = segments.ToArray();
        var transcript = new TranscriptDocument(source, provenance, segmentValues);
        var expectedValues = expectedChapters.ToArray();
        if (expectedValues.Length == 0)
        {
            throw new ArgumentException(
                "Fixtures must define at least one expected chapter.",
                nameof(expectedChapters));
        }

        var knownSegmentIds = transcript.Segments
            .Select(segment => segment.Id)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < expectedValues.Length; index++)
        {
            var chapter = expectedValues[index]
                ?? throw new ArgumentException(
                    "Expected chapter boundaries cannot contain null entries.",
                    nameof(expectedChapters));

            if (chapter.SourceSegmentIds.Any(
                    segmentId => !knownSegmentIds.Contains(segmentId)))
            {
                throw new ArgumentException(
                    $"Expected chapter '{chapter.Id}' references an unknown source segment.",
                    nameof(expectedChapters));
            }

            if (index > 0 && chapter.Start < expectedValues[index - 1].End)
            {
                throw new ArgumentException(
                    "Expected chapter boundaries must be ordered and non-overlapping.",
                    nameof(expectedChapters));
            }
        }

        Id = id.Trim();
        SchemaVersion = schemaVersion;
        Transcript = transcript;
        ExpectedChapters = Array.AsReadOnly(expectedValues);
    }

    public string SchemaVersion { get; }

    public string Id { get; }

    public TranscriptDocument Transcript { get; }

    public IReadOnlyList<ExpectedChapterBoundary> ExpectedChapters { get; }
}

/// <summary>
/// The labeled timing and source-segment assignment for one expected chapter.
/// </summary>
public sealed record ExpectedChapterBoundary
{
    public ExpectedChapterBoundary(
        string id,
        string label,
        TimeSpan start,
        TimeSpan end,
        IEnumerable<string> sourceSegmentIds)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Chapter ID cannot be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Chapter label cannot be empty.", nameof(label));
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentException(
                "Chapter end must be greater than its start.",
                nameof(end));
        }

        ArgumentNullException.ThrowIfNull(sourceSegmentIds);
        var ids = sourceSegmentIds
            .Select(idValue => idValue?.Trim() ?? string.Empty)
            .ToArray();
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Expected chapters must reference at least one source segment.",
                nameof(sourceSegmentIds));
        }

        Id = id.Trim();
        Label = label.Trim();
        Start = start;
        End = end;
        SourceSegmentIds = Array.AsReadOnly(ids);
    }

    public string Id { get; }

    public string Label { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }
}

/// <summary>
/// Loads versioned JSON fixtures without network or model-runtime dependencies.
/// </summary>
public static class ChapterEvaluationFixtureLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static ChapterEvaluationFixture Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Fixture path cannot be empty.", nameof(path));
        }

        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static ChapterEvaluationFixture Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var dto = JsonSerializer.Deserialize<FixtureDto>(stream, SerializerOptions)
            ?? throw new InvalidDataException("Fixture JSON cannot be empty.");
        return ToFixture(dto);
    }

    private static ChapterEvaluationFixture ToFixture(FixtureDto dto)
    {
        var schemaVersion = Require(dto.SchemaVersion, "schemaVersion");
        var id = Require(dto.Id, "id");
        var source = Require(dto.Source, "source");
        if (dto.Provenance is null)
        {
            throw new InvalidDataException("Fixture provenance is required.");
        }

        var segments = (dto.Segments ?? throw new InvalidDataException(
                "Fixture segments are required."))
            .Select(
                segment => new TranscriptSegment(
                    Require(segment.Text, "segments[].text"),
                    ParseTimestamp(segment.Start, "segments[].start"),
                    ParseTimestamp(segment.End, "segments[].end"),
                    segment.Ordinal,
                    NormalizeOptional(segment.SourceId),
                    NormalizeOptional(segment.Id),
                    NormalizeOptional(segment.Speaker),
                    segment.Confidence,
                    segment.SourceMetadata))
            .ToArray();

        var expectedChapters = (dto.ExpectedChapters ?? throw new InvalidDataException(
                "Fixture expectedChapters are required."))
            .Select(
                chapter => new ExpectedChapterBoundary(
                    Require(chapter.Id, "expectedChapters[].id"),
                    Require(chapter.Label, "expectedChapters[].label"),
                    ParseTimestamp(chapter.Start, "expectedChapters[].start"),
                    ParseTimestamp(chapter.End, "expectedChapters[].end"),
                    chapter.SourceSegmentIds ?? throw new InvalidDataException(
                        "Expected chapter sourceSegmentIds are required.")))
            .ToArray();

        var provenance = new TranscriptProvenance(
            Require(dto.Provenance.Provider, "provenance.provider"),
            Require(dto.Provenance.Model, "provenance.model"),
            NormalizeOptional(dto.Provenance.PackageId),
            NormalizeOptional(dto.Provenance.PackageVersion),
            NormalizeOptional(dto.Provenance.Source),
            NormalizeOptional(dto.Provenance.CachePath),
            dto.Provenance.Metadata);

        return new ChapterEvaluationFixture(
            id,
            source,
            provenance,
            segments,
            expectedChapters,
            schemaVersion);
    }

    private static TimeSpan ParseTimestamp(string? value, string fieldName)
    {
        var text = Require(value, fieldName);
        if (!TimeSpan.TryParse(
                text,
                CultureInfo.InvariantCulture,
                out var timestamp) ||
            timestamp < TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Fixture field '{fieldName}' must be a non-negative duration.");
        }

        return timestamp;
    }

    private static string Require(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Fixture field '{fieldName}' is required.")
            : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class FixtureDto
    {
        public string? SchemaVersion { get; init; }

        public string? Id { get; init; }

        public string? Source { get; init; }

        public ProvenanceDto? Provenance { get; init; }

        public SegmentDto[]? Segments { get; init; }

        public ExpectedChapterDto[]? ExpectedChapters { get; init; }
    }

    private sealed class ProvenanceDto
    {
        public string? Provider { get; init; }

        public string? Model { get; init; }

        public string? PackageId { get; init; }

        public string? PackageVersion { get; init; }

        public string? Source { get; init; }

        public string? CachePath { get; init; }

        public Dictionary<string, string>? Metadata { get; init; }
    }

    private sealed class SegmentDto
    {
        public string? Id { get; init; }

        public string? SourceId { get; init; }

        public int Ordinal { get; init; }

        public string? Start { get; init; }

        public string? End { get; init; }

        public string? Text { get; init; }

        public string? Speaker { get; init; }

        public double? Confidence { get; init; }

        public Dictionary<string, string>? SourceMetadata { get; init; }
    }

    private sealed class ExpectedChapterDto
    {
        public string? Id { get; init; }

        public string? Label { get; init; }

        public string? Start { get; init; }

        public string? End { get; init; }

        public string[]? SourceSegmentIds { get; init; }
    }
}
