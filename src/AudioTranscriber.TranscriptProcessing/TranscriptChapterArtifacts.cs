using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

public static class TranscriptChapterArtifactSchema
{
    public const string CurrentVersion = "1.1";

    public const string PreviousVersion = "1.0";

    public const string RegenerationGuidance =
        "Regenerate the artifact with 'audio-transcriber chapters' using the " +
        "source transcript; schema 1.0 cannot be enriched safely because it " +
        "does not contain source-segment snapshots.";
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

    internal TranscriptChapterArtifact(
        string id,
        string title,
        string text,
        TimeSpan start,
        TimeSpan end,
        double? score,
        TranscriptChapterBoundaryMetadata boundary,
        IEnumerable<string> sourceSegmentIds,
        IEnumerable<string> sourceIds,
        IEnumerable<IngestionDocumentParagraph> sourceElements,
        IEnumerable<TranscriptSegmentMetadata> sourceSegments,
        IEnumerable<TranscriptChapterSourceMetadata> sourceMetadata)
    {
        Id = RequireText(id, nameof(id));
        Title = RequireText(title, nameof(title));
        Text = RequireText(text, nameof(text));
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        if (score is not null &&
            (!double.IsFinite(score.Value) || score.Value < 0 || score.Value > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        Boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        SourceSegmentIds = ReadOnlyStrings(sourceSegmentIds, nameof(sourceSegmentIds));
        SourceIds = ReadOnlyStrings(sourceIds, nameof(sourceIds));
        SourceElements = ReadOnlyCollection(sourceElements, nameof(sourceElements));
        SourceSegments = ReadOnlyCollection(sourceSegments, nameof(sourceSegments));
        SourceMetadata = ReadOnlyCollection(sourceMetadata, nameof(sourceMetadata));
        if (SourceSegmentIds.Count == 0 ||
            SourceSegmentIds.Count != SourceSegments.Count ||
            SourceSegmentIds.Count != SourceElements.Count)
        {
            throw new ArgumentException(
                "Chapter source segment IDs, snapshots, and elements must have equal non-zero counts.");
        }

        SchemaVersion = TranscriptChapterArtifactSchema.CurrentVersion;
        Text = TranscriptText.NormalizeWhitespace(text);
        Start = start;
        End = end;
        Score = score;
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

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();

    private static IReadOnlyList<string> ReadOnlyStrings(
        IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        if (result.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Values cannot contain empty entries.",
                parameterName);
        }

        return Array.AsReadOnly(result);
    }

    private static IReadOnlyList<T> ReadOnlyCollection<T>(
        IEnumerable<T> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = values.ToArray();
        if (result.Any(value => value is null))
        {
            throw new ArgumentException(
                "Values cannot contain null entries.",
                parameterName);
        }

        return Array.AsReadOnly(result);
    }
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
                writer.WritePropertyName("sourceSegments");
                writer.WriteStartArray();
                foreach (var segment in chapter.SourceSegments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", segment.Id);
                    WriteOptionalString(writer, "sourceId", segment.SourceId);
                    writer.WriteNumber("originalOrdinal", segment.OriginalOrdinal);
                    writer.WriteString(
                        "start",
                        TranscriptJsonWriter.FormatTimestamp(segment.Start));
                    writer.WriteString(
                        "end",
                        TranscriptJsonWriter.FormatTimestamp(segment.End));
                    writer.WriteString("text", segment.Text);
                    WriteOptionalString(writer, "speaker", segment.Speaker);
                    if (segment.Confidence is { } confidence)
                    {
                        writer.WriteNumber("confidence", confidence);
                    }
                    else
                    {
                        writer.WriteNull("confidence");
                    }

                    WriteMetadata(writer, "sourceMetadata", segment.SourceMetadata);
                    writer.WriteEndObject();
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

    /// <summary>
    /// Strictly reads standalone Audio Transcriber chapter artifacts.
    /// </summary>
    public sealed class TranscriptChapterArtifactReader
    {
            public async Task<TranscriptChapterArtifactDocument> ReadDocumentAsync(
                Stream source,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (!source.CanRead)
                {
                    throw new ArgumentException(
                        "Chapter artifact input stream must be readable.",
                        nameof(source));
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var document = await JsonDocument
                        .ParseAsync(source, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Parse(document.RootElement);
                }
                catch (TranscriptFormatException)
                {
                    throw;
                }
                catch (JsonException exception)
                {
                    throw Failure(
                        "invalid_json",
                        "$",
                        "Chapter artifact input is not valid JSON.",
                        exception);
                }
            }

            public async Task<TranscriptChapterArtifactDocument> ReadFileAsync(
                string path,
                CancellationToken cancellationToken = default)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("Chapter artifact path cannot be empty.", nameof(path));
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            private static TranscriptChapterArtifactDocument Parse(JsonElement root)
            {
                var rootObject = RequireObject(root, "$");
                RequireKnownProperties(
                    rootObject,
                    "$",
                    "schemaVersion",
                    "source",
                    "provenance",
                    "generation",
                    "chapters");
        RequireRequiredProperties(
                    rootObject,
                    "$",
                    "schemaVersion",
                    "source",
                    "provenance",
                    "generation",
                    "chapters");
                var schemaVersion = RequiredString(rootObject, "schemaVersion", "$");
                if (schemaVersion == TranscriptChapterArtifactSchema.PreviousVersion)
                {
                    throw Failure(
                        "unsupported_schema_version",
                        "$.schemaVersion",
                        $"Chapter artifact schema 1.0 is not accepted. " +
                        TranscriptChapterArtifactSchema.RegenerationGuidance);
                }

                if (schemaVersion != TranscriptChapterArtifactSchema.CurrentVersion)
                {
                    throw Failure(
                        "unsupported_schema_version",
                        "$.schemaVersion",
                        $"Chapter artifact schema '{schemaVersion}' is not supported. " +
                        $"Expected '{TranscriptChapterArtifactSchema.CurrentVersion}'.");
                }

                var source = RequiredString(rootObject, "source", "$");
                var provenance = ParseProvenance(
                    RequireObject(rootObject.GetProperty("provenance"), "$.provenance"));
                var generation = ParseGeneration(
                    RequireObject(rootObject.GetProperty("generation"), "$.generation"));
                var chaptersElement = RequiredArray(rootObject, "chapters", "$");
                var chapters = chaptersElement
                    .EnumerateArray()
                    .Select((chapter, index) => ParseChapter(chapter, index))
                    .ToArray();
                var duplicate = chapters
                    .GroupBy(chapter => chapter.Id, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicate is not null)
                {
                    throw Failure(
                        "duplicate_chapter_id",
                        "$.chapters",
                        $"Chapter ID '{duplicate.Key}' is repeated.");
                }

                try
                {
                    return new TranscriptChapterArtifactDocument(
                        source,
                        provenance,
                        chapters,
                        generation);
                }
                catch (ArgumentException exception)
                {
                    throw Failure(
                        "invalid_artifact",
                        "$",
                        "Chapter artifact metadata is invalid.",
                        exception);
                }
            }

            private static TranscriptChapterArtifact ParseChapter(
                JsonElement value,
                int index)
            {
                var path = $"$.chapters[{index}]";
                var root = RequireObject(value, path);
                RequireKnownProperties(
                    root,
                    path,
                    "schemaVersion",
                    "id",
                    "title",
                    "start",
                    "end",
                    "text",
                    "score",
                    "boundary",
                    "sourceSegmentIds",
                    "sourceIds",
                    "sourceSegments",
                    "sourceMetadata");
        RequireRequiredProperties(
                    root,
                    path,
                    "schemaVersion",
                    "id",
                    "title",
                    "start",
                    "end",
                    "text",
                    "score",
                    "boundary",
                    "sourceSegmentIds",
                    "sourceIds",
                    "sourceSegments",
                    "sourceMetadata");
                var schemaVersion = RequiredString(root, "schemaVersion", path);
                if (schemaVersion != TranscriptChapterArtifactSchema.CurrentVersion)
                {
                    throw Failure(
                        "chapter_schema_mismatch",
                        $"{path}.schemaVersion",
                        $"Chapter schema '{schemaVersion}' is not supported. " +
                        $"Expected '{TranscriptChapterArtifactSchema.CurrentVersion}'.");
                }

                var id = RequiredString(root, "id", path);
                var title = RequiredString(root, "title", path);
                var start = RequiredTimestamp(root, "start", path);
                var end = RequiredTimestamp(root, "end", path);
                if (end <= start)
                {
                    throw Failure(
                        "invalid_source_segment_interval",
                        path,
                        "Source segment end must be after its start.");
                }

                var text = RequiredString(root, "text", path);
                var score = OptionalNumber(root, "score", path);
                var boundary = ParseBoundary(
                    RequireObject(root.GetProperty("boundary"), $"{path}.boundary"),
                    $"{path}.boundary");
                var sourceSegmentIds = RequiredStringArray(
                    root,
                    "sourceSegmentIds",
                    path);
                var sourceIds = RequiredStringArray(root, "sourceIds", path);
                var sourceSegmentsElement = RequiredArray(root, "sourceSegments", path);
                var sourceSegments = sourceSegmentsElement
                    .EnumerateArray()
                    .Select(
                        (segment, segmentIndex) =>
                            ParseSegment(
                                segment,
                                $"{path}.sourceSegments[{segmentIndex}]"))
                    .ToArray();
                var sourceMetadata = ParseSourceMetadata(
                    RequiredArray(root, "sourceMetadata", path),
                    $"{path}.sourceMetadata");

                ValidateChapterInvariants(
                    path,
                    id,
                    text,
                    start,
                    end,
                    boundary,
                    sourceSegmentIds,
                    sourceIds,
                    sourceSegments,
                    sourceMetadata);

                var document = new IngestionDocument($"chapter-artifact:{id}");
                var sourceElements = new List<IngestionDocumentParagraph>();
                foreach (var segment in sourceSegments)
                {
                    var paragraph = new IngestionDocumentParagraph(segment.Text!)
                    {
                        Text = segment.Text
                    };
                    paragraph.Metadata[TranscriptIngestionAdapter.SegmentMetadataKey] = segment;
                    sourceElements.Add(paragraph);
                }

                return new TranscriptChapterArtifact(
                    id,
                    title,
                    text,
                    start,
                    end,
                    score,
                    boundary,
                    sourceSegmentIds,
                    sourceIds,
                    sourceElements,
                    sourceSegments,
                    sourceMetadata);
            }

            private static TranscriptSegmentMetadata ParseSegment(
                JsonElement value,
                string path)
            {
                var root = RequireObject(value, path);
                RequireKnownProperties(
                    root,
                    path,
                    "id",
                    "sourceId",
                    "originalOrdinal",
                    "start",
                    "end",
                    "text",
                    "speaker",
                    "confidence",
                    "sourceMetadata");
        RequireRequiredProperties(
                    root,
                    path,
                    "id",
                    "originalOrdinal",
                    "start",
                    "end",
                    "text",
                    "confidence",
                    "sourceMetadata");
                var id = RequiredString(root, "id", path);
                var sourceId = OptionalString(root, "sourceId", path);
                var ordinal = RequiredInt32(root, "originalOrdinal", path);
                if (ordinal < 0)
                {
                    throw Failure(
                        "invalid_original_ordinal",
                        $"{path}.originalOrdinal",
                        "Original ordinal cannot be negative.");
                }

                var start = RequiredTimestamp(root, "start", path);
                var end = RequiredTimestamp(root, "end", path);
                var text = RequiredString(root, "text", path);
                var speaker = OptionalString(root, "speaker", path);
                var confidence = OptionalNumber(root, "confidence", path);
                var sourceMetadata = ParseMetadataObject(
                    RequireObject(
                        root.GetProperty("sourceMetadata"),
                        $"{path}.sourceMetadata"),
                    $"{path}.sourceMetadata");
                try
                {
                    return new TranscriptSegmentMetadata(
                        id,
                        sourceId,
                        ordinal,
                        start,
                        end,
                        speaker,
                        confidence,
                        sourceMetadata)
                    {
                        Text = text
                    };
                }
                catch (ArgumentException exception)
                {
                    throw Failure(
                        "invalid_source_segment",
                        path,
                        "Source segment snapshot is invalid.",
                        exception);
                }
            }

            private static void ValidateChapterInvariants(
                string path,
                string id,
                string text,
                TimeSpan start,
                TimeSpan end,
                TranscriptChapterBoundaryMetadata boundary,
                IReadOnlyList<string> sourceSegmentIds,
                IReadOnlyList<string> sourceIds,
                IReadOnlyList<TranscriptSegmentMetadata> sourceSegments,
                IReadOnlyList<TranscriptChapterSourceMetadata> sourceMetadata)
            {
                if (sourceSegments.Count == 0)
                {
                    throw Failure(
                        "missing_source_segments",
                        $"{path}.sourceSegments",
                        "Each chapter must contain at least one source segment snapshot.");
                }

                var segmentIds = sourceSegments.Select(segment => segment.Id).ToArray();
                if (!sourceSegmentIds.SequenceEqual(segmentIds, StringComparer.Ordinal))
                {
                    throw Failure(
                        "source_segment_ids_mismatch",
                        $"{path}.sourceSegmentIds",
                        "sourceSegmentIds must exactly match sourceSegments[].id in order.");
                }

                var expectedSourceIds = sourceSegments
                    .Where(segment => segment.SourceId is not null)
                    .Select(segment => segment.SourceId!)
                    .ToArray();
                if (!sourceIds.SequenceEqual(expectedSourceIds, StringComparer.Ordinal))
                {
                    throw Failure(
                        "source_ids_mismatch",
                        $"{path}.sourceIds",
                        "sourceIds must contain each non-null sourceSegments[].sourceId in order.");
                }

                var first = sourceSegments[0];
                var last = sourceSegments[^1];
                if (start != first.Start || end != last.End)
                {
                    throw Failure(
                        "chapter_timing_mismatch",
                        path,
                        "Chapter start/end must match the first/last source segment.");
                }

                if (boundary.StartSegmentId != first.Id ||
                    boundary.EndSegmentId != last.Id ||
                    boundary.StartOriginalOrdinal != first.OriginalOrdinal ||
                    boundary.EndOriginalOrdinal != last.OriginalOrdinal)
                {
                    throw Failure(
                        "boundary_mismatch",
                        $"{path}.boundary",
                        "Boundary metadata must match the first and last source segments.");
                }

                var expectedText = TranscriptText.NormalizeWhitespace(
                    string.Join(' ', sourceSegments.Select(segment => segment.Text)));
                if (!string.Equals(text, expectedText, StringComparison.Ordinal))
                {
                    throw Failure(
                        "chapter_text_mismatch",
                        $"{path}.text",
                        "Chapter text must be the normalized ordered source-segment text.");
                }

                var expectedMetadata = sourceSegments
                    .SelectMany(
                        segment => segment.SourceMetadata.Select(
                            pair => new TranscriptChapterSourceMetadata(
                                segment.Id,
                                pair.Key,
                                pair.Value)))
                    .ToArray();
                var actualMetadata = sourceMetadata
                    .OrderBy(entry => entry.SourceSegmentId, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                    .ToArray();
                var orderedExpectedMetadata = expectedMetadata
                    .OrderBy(entry => entry.SourceSegmentId, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Value, StringComparer.Ordinal)
                    .ToArray();
                if (!actualMetadata.SequenceEqual(orderedExpectedMetadata))
                {
                    throw Failure(
                        "source_metadata_mismatch",
                        $"{path}.sourceMetadata",
                        "sourceMetadata must reflect the source segment snapshots.");
                }

                var duplicateSegmentId = segmentIds
                    .GroupBy(value => value, StringComparer.Ordinal)
                    .FirstOrDefault(group => group.Count() > 1);
                if (duplicateSegmentId is not null)
                {
                    throw Failure(
                        "duplicate_source_segment_id",
                        $"{path}.sourceSegments",
                        $"Source segment ID '{duplicateSegmentId.Key}' is repeated.");
                }
            }

            private static TranscriptProvenance ParseProvenance(JsonElement root)
            {
                RequireKnownProperties(
                    root,
                    "$.provenance",
                    "provider",
                    "model",
                    "packageId",
                    "packageVersion",
                    "source",
                    "cachePath",
                    "metadata");
        RequireRequiredProperties(root, "$.provenance", "provider", "model", "metadata");
                return new TranscriptProvenance(
                    RequiredString(root, "provider", "$.provenance"),
                    RequiredString(root, "model", "$.provenance"),
                    OptionalString(root, "packageId", "$.provenance"),
                    OptionalString(root, "packageVersion", "$.provenance"),
                    OptionalString(root, "source", "$.provenance"),
                    OptionalString(root, "cachePath", "$.provenance"),
                    ParseMetadataObject(
                        RequireObject(
                            root.GetProperty("metadata"),
                            "$.provenance.metadata"),
                        "$.provenance.metadata"));
            }

            private static TranscriptChapterGenerationMetadata ParseGeneration(
                JsonElement root)
            {
                RequireKnownProperties(
                    root,
                    "$.generation",
                    "algorithm",
                    "provider",
                    "configuration");
        RequireRequiredProperties(
                    root,
                    "$.generation",
                    "algorithm",
                    "provider",
                    "configuration");
                return new TranscriptChapterGenerationMetadata(
                    RequiredString(root, "algorithm", "$.generation"),
                    RequiredString(root, "provider", "$.generation"),
                    ParseMetadataObject(
                        RequireObject(
                            root.GetProperty("configuration"),
                            "$.generation.configuration"),
                        "$.generation.configuration"));
            }

            private static TranscriptChapterBoundaryMetadata ParseBoundary(
                JsonElement root,
                string path)
            {
                RequireKnownProperties(
                    root,
                    path,
                    "startSegmentId",
                    "endSegmentId",
                    "startOriginalOrdinal",
                    "endOriginalOrdinal",
                    "startSnappedToSegmentBoundary",
                    "endSnappedToSegmentBoundary",
                    "gapBefore",
                    "gapAfter");
        RequireRequiredProperties(
                    root,
                    path,
                    "startSegmentId",
                    "endSegmentId",
                    "startOriginalOrdinal",
                    "endOriginalOrdinal",
                    "startSnappedToSegmentBoundary",
                    "endSnappedToSegmentBoundary");
                var startSnapped = RequiredBoolean(root, "startSnappedToSegmentBoundary", path);
                var endSnapped = RequiredBoolean(root, "endSnappedToSegmentBoundary", path);
                if (!startSnapped || !endSnapped)
                {
                    throw Failure(
                        "unsupported_boundary",
                        path,
                        "Chapter artifacts require boundaries snapped to complete source segments.");
                }

                try
                {
                    return new TranscriptChapterBoundaryMetadata(
                        RequiredString(root, "startSegmentId", path),
                        RequiredString(root, "endSegmentId", path),
                        RequiredInt32(root, "startOriginalOrdinal", path),
                        RequiredInt32(root, "endOriginalOrdinal", path),
                        OptionalTimestamp(root, "gapBefore", path),
                        OptionalTimestamp(root, "gapAfter", path));
                }
                catch (ArgumentException exception)
                {
                    throw Failure(
                        "invalid_boundary",
                        path,
                        "Boundary metadata is invalid.",
                        exception);
                }
            }

            private static IReadOnlyList<TranscriptChapterSourceMetadata> ParseSourceMetadata(
                JsonElement value,
                string path)
            {
                var entries = new List<TranscriptChapterSourceMetadata>();
                foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
                {
                    var itemPath = $"{path}[{index}]";
                    var root = RequireObject(item, itemPath);
                    RequireKnownProperties(root, itemPath, "sourceSegmentId", "key", "value");
                    entries.Add(
                        new TranscriptChapterSourceMetadata(
                            RequiredString(root, "sourceSegmentId", itemPath),
                            RequiredString(root, "key", itemPath),
                            RequiredMetadataString(root, "value", itemPath)));
                }

                return entries;
            }

            private static IReadOnlyDictionary<string, string> ParseMetadataObject(
                JsonElement root,
                string path)
            {
                var entries = new List<KeyValuePair<string, string>>();
                foreach (var property in root.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name) ||
                        property.Value.ValueKind != JsonValueKind.String ||
                        property.Value.GetString() is null)
                    {
                        throw Failure(
                            "invalid_metadata",
                            path,
                            "Metadata must contain non-empty string values.");
                    }

                    entries.Add(new(property.Name, property.Value.GetString()!));
                }

                try
                {
                    return TranscriptProvenance.ReadOnlyMetadata(entries, path);
                }
                catch (ArgumentException exception)
                {
                    throw Failure("invalid_metadata", path, "Metadata is invalid.", exception);
                }
            }

            private static JsonElement RequireObject(JsonElement value, string path)
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    throw Failure("invalid_object", path, "Expected a JSON object.");
                }

                return value;
            }

            private static JsonElement RequiredArray(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    value.ValueKind != JsonValueKind.Array)
                {
                    throw Failure(
                        $"missing_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be an array.");
                }

                return value;
            }

            private static string RequiredString(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    value.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(value.GetString()))
                {
                    throw Failure(
                        $"missing_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a non-empty string.");
                }

                return value.GetString()!.Trim();
            }

            private static string? OptionalString(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value))
                {
                    return null;
                }

                if (value.ValueKind != JsonValueKind.String)
                {
                    throw Failure(
                        $"invalid_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a string or null.");
                }

                return string.IsNullOrWhiteSpace(value.GetString())
                    ? null
                    : value.GetString()!.Trim();
            }

            private static string RequiredMetadataString(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    value.ValueKind != JsonValueKind.String ||
                    value.GetString() is null)
                {
                    throw Failure(
                        $"invalid_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a string.");
                }

                return value.GetString()!;
            }

            private static IReadOnlyList<string> RequiredStringArray(
                JsonElement root,
                string propertyName,
                string path)
            {
                var value = RequiredArray(root, propertyName, path);
                var result = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        throw Failure(
                            $"invalid_{propertyName}",
                            $"{path}.{propertyName}",
                            $"Expected '{propertyName}' to contain non-empty strings.");
                    }

                    result.Add(item.GetString()!.Trim());
                }

                return result;
            }

            private static int RequiredInt32(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    !value.TryGetInt32(out var result))
                {
                    throw Failure(
                        $"invalid_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a 32-bit integer.");
                }

                return result;
            }

            private static bool RequiredBoolean(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    (value.ValueKind != JsonValueKind.True &&
                     value.ValueKind != JsonValueKind.False))
                {
                    throw Failure(
                        $"invalid_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a boolean.");
                }

                return value.GetBoolean();
            }

            private static double? OptionalNumber(
                JsonElement root,
                string propertyName,
                string path)
            {
                if (!root.TryGetProperty(propertyName, out var value) ||
                    value.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                if (!value.TryGetDouble(out var result) || !double.IsFinite(result))
                {
                    throw Failure(
                        $"invalid_{propertyName}",
                        $"{path}.{propertyName}",
                        $"Expected '{propertyName}' to be a finite number.");
                }

                return result;
            }

            private static TimeSpan RequiredTimestamp(
                JsonElement root,
                string propertyName,
                string path) =>
                ParseTimestamp(
                    RequiredString(root, propertyName, path),
                    $"{path}.{propertyName}");

            private static TimeSpan? OptionalTimestamp(
                JsonElement root,
                string propertyName,
                string path)
            {
                var value = OptionalString(root, propertyName, path);
                return value is null ? null : ParseTimestamp(value, $"{path}.{propertyName}");
            }

            private static TimeSpan ParseTimestamp(string value, string path)
            {
                var parts = value.Split(':');
                if (parts.Length != 3 ||
                    !long.TryParse(
                        parts[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var hours) ||
                    !int.TryParse(
                        parts[1],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var minutes))
                {
                    throw Failure(
                        "invalid_timestamp",
                        path,
                        "Timestamps must use invariant duration format 'c' and be non-negative.");
                }

                var secondsParts = parts[2].Split('.', 2);
                if (!int.TryParse(
                        secondsParts[0],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var seconds) ||
                    hours < 0 ||
                    minutes is < 0 or >= 60 ||
                    seconds is < 0 or >= 60)
                {
                    throw Failure(
                        "invalid_timestamp",
                        path,
                        "Timestamps must use invariant duration format 'c' and be non-negative.");
                }

                var fraction = secondsParts.Length == 2
                    ? secondsParts[1]
                    : string.Empty;
                if ((secondsParts.Length == 2 && fraction.Length == 0) ||
                    fraction.Length > 7 ||
                    (fraction.Length > 0 &&
                     !fraction.All(character => character is >= '0' and <= '9')))
                {
                    throw Failure(
                        "invalid_timestamp",
                        path,
                        "Timestamps must use invariant duration format 'c' and be non-negative.");
                }

                try
                {
                    var ticks = checked(
                        (hours * TimeSpan.TicksPerHour) +
                        (minutes * TimeSpan.TicksPerMinute) +
                        (seconds * TimeSpan.TicksPerSecond) +
                        FractionTicks(fraction));
                    return new TimeSpan(ticks);
                }
                catch (OverflowException exception)
                {
                    throw Failure(
                        "invalid_timestamp",
                        path,
                        "Timestamp is outside the supported range.",
                        exception);
                }
            }

            private static long FractionTicks(string fraction) =>
                fraction.Length == 0
                    ? 0
                    : long.Parse(
                        fraction.PadRight(7, '0'),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);

            private static void RequireKnownProperties(
                JsonElement root,
                string path,
                params string[] names)
            {
                var allowed = names.ToHashSet(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        throw Failure(
                            "duplicate_property",
                            path,
                            $"Property '{property.Name}' is repeated.");
                    }

                    if (!allowed.Contains(property.Name))
                    {
                        throw Failure(
                            "unknown_property",
                            $"{path}.{property.Name}",
                            $"Property '{property.Name}' is not part of the chapter artifact contract.");
                    }
                }
            }

            private static void RequireRequiredProperties(
                JsonElement root,
                string path,
                params string[] names)
            {
                foreach (var name in names)
                {
                    if (!root.TryGetProperty(name, out _))
                    {
                        throw Failure(
                            $"missing_{name}",
                            $"{path}.{name}",
                            $"Required property '{name}' is missing.");
                    }
                }
            }

            private static TranscriptFormatException Failure(
                string code,
                string path,
                string message,
                Exception? innerException = null) =>
                new(code, path, message, innerException);
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

/// <summary>
/// Strictly reads standalone Audio Transcriber chapter artifacts.
/// </summary>
public sealed class TranscriptChapterArtifactReader
{
    private readonly TranscriptChapterArtifactGenerator.TranscriptChapterArtifactReader reader = new();

    public Task<TranscriptChapterArtifactDocument> ReadDocumentAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        reader.ReadDocumentAsync(source, cancellationToken);

    public Task<TranscriptChapterArtifactDocument> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        reader.ReadFileAsync(path, cancellationToken);
}
