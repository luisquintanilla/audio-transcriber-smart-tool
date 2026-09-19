using System.Buffers;
using System.Text;
using System.Text.Json;

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
        IReadOnlyList<TranscriptChapterSourceMetadata> sourceMetadata)
    {
        Id = id;
        SchemaVersion = TranscriptChapterArtifactSchema.CurrentVersion;
        Title = title;
        Text = window.Text;
        Start = window.Start;
        End = window.End;
        SourceSegmentIds = window.SourceSegmentIds;
        SourceIds = window.SourceIds;
        SourceSegments = window.SourceSegments;
        SourceMetadata = sourceMetadata;
        Score = window.Score ?? 0d;
    }

    public string SchemaVersion { get; }

    public string Id { get; }

    public string Title { get; }

    public string Text { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }

    public IReadOnlyList<string> SourceIds { get; }

    public IReadOnlyList<TranscriptSegment> SourceSegments { get; }

    public IReadOnlyList<TranscriptChapterSourceMetadata> SourceMetadata { get; }

    public double Score { get; }
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
        IEnumerable<TranscriptChapterArtifact> chapters)
    {
        Source = source;
        Provenance = provenance;
        Chapters = Array.AsReadOnly(chapters.ToArray());
    }

    public string SchemaVersion => TranscriptChapterArtifactSchema.CurrentVersion;

    public string Source { get; }

    public TranscriptProvenance Provenance { get; }

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
    public TranscriptChapterArtifactDocument Generate(TranscriptChunkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var chapters = result.Windows
            .Where(window => !window.IsGap)
            .Select(
                (window, index) =>
                    new TranscriptChapterArtifact(
                        CreateId(window, index),
                        $"Chapter {index + 1}",
                        window,
                        MergeMetadata(window.SourceSegments)))
            .ToArray();

        return new TranscriptChapterArtifactDocument(
            result.Source,
            result.Provenance,
            chapters);
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
                writer.WriteNumber("score", chapter.Score);

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
        IReadOnlyList<TranscriptSegment> segments)
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
}
