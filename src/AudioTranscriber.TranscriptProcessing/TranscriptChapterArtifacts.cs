using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

public static class TranscriptChapterArtifactSchema
{
    public const string CurrentVersion = "1.0";
}

public sealed record TranscriptChapterSourceMetadata(
    string SourceSegmentId,
    string Key,
    string Value);

/// <summary>
/// A stable, versioned chapter artifact derived from a transcript window.
/// </summary>
public sealed class TranscriptChapterArtifact
{
    internal TranscriptChapterArtifact(
        string id,
        string title,
        TranscriptChunkWindow window,
        IReadOnlyList<TranscriptChapterSourceMetadata> sourceMetadata,
        TranscriptChapterBoundaryMetadata boundary)
    {
        Id = id;
        SchemaVersion = TranscriptChapterArtifactSchema.CurrentVersion;
        Title = title;
        Text = window.Text;
        Start = window.Start;
        End = window.End;
        SourceSegmentIds = window.SourceSegmentIds;
        SourceIds = window.SourceIds;
        SourceElements = window.SourceElements;
        SourceSegments = window.SourceSegments;
        SourceMetadata = sourceMetadata;
        Score = window.Score;
        Boundary = boundary;
    }

    public string SchemaVersion { get; }

    public string Id { get; }

    public string Title { get; }

    public string Text { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public IReadOnlyList<IngestionDocumentParagraph> SourceElements { get; }

    public IReadOnlyList<TranscriptSegmentMetadata> SourceSegments { get; }

    public IReadOnlyList<TranscriptChapterSourceMetadata> SourceMetadata { get; }

    /// <summary>
    /// The evaluated model score, or <see langword="null"/> for a structural chapter.
    /// </summary>
    public double? Score { get; }

    public TranscriptChapterBoundaryMetadata Boundary { get; }

    public TranscriptChapterBoundaryMetadata BoundaryMetadata => Boundary;
}

/// <summary>
/// Describes how a chapter boundary maps to source transcript segments.
/// </summary>
public sealed record TranscriptChapterBoundaryMetadata
{
    public TranscriptChapterBoundaryMetadata(
        string startSegmentId,
        string endSegmentId,
        int startOriginalOrdinal,
        int endOriginalOrdinal,
        TimeSpan? gapBefore = null,
        TimeSpan? gapAfter = null)
    {
        if (string.IsNullOrWhiteSpace(startSegmentId))
        {
            throw new ArgumentException(
                "The chapter start segment ID cannot be empty.",
                nameof(startSegmentId));
        }

        if (string.IsNullOrWhiteSpace(endSegmentId))
        {
            throw new ArgumentException(
                "The chapter end segment ID cannot be empty.",
                nameof(endSegmentId));
        }

        if (startOriginalOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOriginalOrdinal));
        }

        if (endOriginalOrdinal < startOriginalOrdinal)
        {
            throw new ArgumentOutOfRangeException(nameof(endOriginalOrdinal));
        }

        if (gapBefore is not null && gapBefore.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gapBefore));
        }

        if (gapAfter is not null && gapAfter.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(gapAfter));
        }

        StartSegmentId = startSegmentId;
        EndSegmentId = endSegmentId;
        StartOriginalOrdinal = startOriginalOrdinal;
        EndOriginalOrdinal = endOriginalOrdinal;
        StartSnappedToSegmentBoundary = true;
        EndSnappedToSegmentBoundary = true;
        GapBefore = gapBefore;
        GapAfter = gapAfter;
    }

    public string StartSegmentId { get; }

    public string EndSegmentId { get; }

    public int StartOriginalOrdinal { get; }

    public int EndOriginalOrdinal { get; }

    public bool StartSnappedToSegmentBoundary { get; }

    public bool EndSnappedToSegmentBoundary { get; }

    public TimeSpan? GapBefore { get; }

    public TimeSpan? GapAfter { get; }
}

/// <summary>
/// The versioned, ordered chapter-artifact document.
/// </summary>
public sealed class TranscriptChapterArtifactDocument
    : IReadOnlyList<TranscriptChapterArtifact>
{
    internal TranscriptChapterArtifactDocument(
        string source,
        TranscriptProvenance provenance,
        IEnumerable<TranscriptChapterArtifact> chapters,
        TranscriptChapterGenerationMetadata generation)
    {
        Source = source;
        Provenance = provenance;
        Chapters = Array.AsReadOnly(chapters.ToArray());
        Generation = generation;
    }

    public string SchemaVersion => TranscriptChapterArtifactSchema.CurrentVersion;

    public string Source { get; }

    public TranscriptProvenance Provenance { get; }

    public TranscriptChapterGenerationMetadata Generation { get; }

    public string Algorithm => Generation.Algorithm;

    public string Provider => Generation.Provider;

    public IReadOnlyDictionary<string, string> ProviderConfiguration =>
        Generation.Configuration;

    public IReadOnlyList<TranscriptChapterArtifact> Chapters { get; }

    public int Count => Chapters.Count;

    public TranscriptChapterArtifact this[int index] => Chapters[index];

    public IEnumerator<TranscriptChapterArtifact> GetEnumerator() =>
        Chapters.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

/// <summary>
/// Creates deterministic chapter artifacts from chunk windows.
/// </summary>
public sealed class TranscriptChapterArtifactGenerator
{
    public TranscriptChapterArtifactDocument Generate(
        TranscriptChunkResult result,
        TranscriptChapterGenerationMetadata? generation = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        generation ??= new TranscriptChapterGenerationMetadata(
            "segment-boundary-v1",
            "deterministic");

        var chapters = result.Windows
            .Where(window => !window.IsGap)
            .Select(
                (window, index) =>
                    new TranscriptChapterArtifact(
                        CreateId(window, index),
                        $"Chapter {index + 1}",
                        window,
                        MergeMetadata(window.SourceSegments),
                        CreateBoundary(result.Windows, window)))
            .ToArray();

        return new TranscriptChapterArtifactDocument(
            result.Source,
            result.Provenance,
            chapters,
            generation);
    }

    public Task<TranscriptChapterArtifactDocument> GenerateAsync(
        TranscriptChunkResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Generate(result));
    }

    public string Serialize(TranscriptChapterArtifactDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", document.SchemaVersion);
            writer.WriteString("source", document.Source);
            writer.WritePropertyName("provenance");
            writer.WriteStartObject();
            writer.WriteString("provider", document.Provenance.Provider);
            writer.WriteString("model", document.Provenance.Model);
            WriteOptionalString(writer, "packageId", document.Provenance.PackageId);
            WriteOptionalString(
                writer,
                "packageVersion",
                document.Provenance.PackageVersion);
            WriteOptionalString(writer, "source", document.Provenance.Source);
            WriteOptionalString(writer, "cachePath", document.Provenance.CachePath);
            WriteMetadata(writer, "metadata", document.Provenance.Metadata);
            writer.WriteEndObject();
            writer.WritePropertyName("generation");
            writer.WriteStartObject();
            writer.WriteString("algorithm", document.Generation.Algorithm);
            writer.WriteString("provider", document.Generation.Provider);
            WriteMetadata(writer, "configuration", document.Generation.Configuration);
            writer.WriteEndObject();
            writer.WritePropertyName("chapters");
            writer.WriteStartArray();

            foreach (var chapter in document)
            {
                writer.WriteStartObject();
                writer.WriteString("schemaVersion", chapter.SchemaVersion);
                writer.WriteString("id", chapter.Id);
                writer.WriteString("title", chapter.Title);
                writer.WriteString("start", TranscriptJsonWriter.FormatTimestamp(chapter.Start));
                writer.WriteString("end", TranscriptJsonWriter.FormatTimestamp(chapter.End));
                writer.WriteString("text", chapter.Text);
                if (chapter.Score is { } score)
                {
                    writer.WriteNumber("score", score);
                }
                else
                {
                    writer.WriteNull("score");
                }
                writer.WritePropertyName("boundary");
                writer.WriteStartObject();
                writer.WriteString(
                    "startSegmentId",
                    chapter.Boundary.StartSegmentId);
                writer.WriteString(
                    "endSegmentId",
                    chapter.Boundary.EndSegmentId);
                writer.WriteNumber(
                    "startOriginalOrdinal",
                    chapter.Boundary.StartOriginalOrdinal);
                writer.WriteNumber(
                    "endOriginalOrdinal",
                    chapter.Boundary.EndOriginalOrdinal);
                writer.WriteBoolean(
                    "startSnappedToSegmentBoundary",
                    chapter.Boundary.StartSnappedToSegmentBoundary);
                writer.WriteBoolean(
                    "endSnappedToSegmentBoundary",
                    chapter.Boundary.EndSnappedToSegmentBoundary);
                WriteOptionalTimestamp(
                    writer,
                    "gapBefore",
                    chapter.Boundary.GapBefore);
                WriteOptionalTimestamp(
                    writer,
                    "gapAfter",
                    chapter.Boundary.GapAfter);
                writer.WriteEndObject();

                writer.WritePropertyName("sourceSegmentIds");
                writer.WriteStartArray();
                foreach (var sourceSegmentId in chapter.SourceSegmentIds)
                {
                    writer.WriteStringValue(sourceSegmentId);
                }

                writer.WriteEndArray();
                writer.WritePropertyName("sourceIds");
                writer.WriteStartArray();
                foreach (var sourceId in chapter.SourceIds)
                {
                    writer.WriteStringValue(sourceId);
                }

                writer.WriteEndArray();
                WriteMetadata(writer, "sourceMetadata", chapter.SourceMetadata);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static string CreateId(TranscriptChunkWindow window, int index) =>
        $"chapter-{index + 1:D4}-{window.Id}";

    private static IReadOnlyList<TranscriptChapterSourceMetadata> MergeMetadata(
        IReadOnlyList<TranscriptSegmentMetadata> segments)
    {
        var values = new List<TranscriptChapterSourceMetadata>();
        foreach (var segment in segments)
        {
            foreach (var entry in segment.SourceMetadata.OrderBy(
                         entry => entry.Key,
                         StringComparer.Ordinal))
            {
                values.Add(new TranscriptChapterSourceMetadata(
                    segment.Id,
                    entry.Key,
                    entry.Value));
            }
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static TranscriptChapterBoundaryMetadata CreateBoundary(
        IReadOnlyList<TranscriptChunkWindow> windows,
        TranscriptChunkWindow window)
    {
        var firstSegment = window.SourceSegments[0];
        var lastSegment = window.SourceSegments[^1];
        var index = 0;
        while (index < windows.Count && !ReferenceEquals(windows[index], window))
        {
            index++;
        }

        if (index == windows.Count)
        {
            throw new InvalidOperationException(
                "The chapter window was not found in its chunk result.");
        }

        var gapBefore = GetAdjacentGapDuration(windows, index - 1, -1);
        var gapAfter = GetAdjacentGapDuration(windows, index + 1, 1);

        return new TranscriptChapterBoundaryMetadata(
            firstSegment.Id,
            lastSegment.Id,
            firstSegment.OriginalOrdinal,
            lastSegment.OriginalOrdinal,
            gapBefore,
            gapAfter);
    }

    private static TimeSpan? GetAdjacentGapDuration(
        IReadOnlyList<TranscriptChunkWindow> windows,
        int index,
        int step)
    {
        if (index < 0 || index >= windows.Count || !windows[index].IsGap)
        {
            return null;
        }

        var duration = TimeSpan.Zero;
        while (index >= 0 && index < windows.Count && windows[index].IsGap)
        {
            duration += windows[index].End - windows[index].Start;
            index += step;
        }

        return duration;
    }

    private static void WriteMetadata(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyDictionary<string, string> metadata)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var entry in metadata.OrderBy(
                     entry => entry.Key,
                     StringComparer.Ordinal))
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteMetadata(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyList<TranscriptChapterSourceMetadata> metadata)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var entry in metadata)
        {
            writer.WriteStartObject();
            writer.WriteString("sourceSegmentId", entry.SourceSegmentId);
            writer.WriteString("key", entry.Key);
            writer.WriteString("value", entry.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalTimestamp(
        Utf8JsonWriter writer,
        string name,
        TimeSpan? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, TranscriptJsonWriter.FormatTimestamp(value.Value));
        }
    }
}
