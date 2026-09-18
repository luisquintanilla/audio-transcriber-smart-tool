using System.Buffers;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber.TranscriptProcessing;

public sealed class TranscriptChapterEnrichmentException : InvalidOperationException
{
    public TranscriptChapterEnrichmentException(
        string chapterId,
        string code,
        Exception? innerException = null)
        : base(
            $"Chapter enrichment failed for '{chapterId}' ({code}).",
            innerException)
    {
        ChapterId = chapterId;
        Code = code;
    }

    public string ChapterId { get; }

    public string Code { get; }
}

/// <summary>
/// Orchestrates injected chapter enrichers and an optional summary assembler.
/// </summary>
public sealed class TranscriptChapterEnrichmentOrchestrator
{
    private readonly ITranscriptChapterEnricher chapterEnricher;
    private readonly ITranscriptOverallSummaryAssembler? overallSummaryAssembler;

    public TranscriptChapterEnrichmentOrchestrator(
        ITranscriptChapterEnricher chapterEnricher,
        ITranscriptOverallSummaryAssembler? overallSummaryAssembler = null)
    {
        this.chapterEnricher = chapterEnricher
            ?? throw new ArgumentNullException(nameof(chapterEnricher));
        this.overallSummaryAssembler = overallSummaryAssembler;
    }

    public async Task<TranscriptChapterEnrichmentDocument> EnrichAsync(
        TranscriptChapterArtifactDocument chapterArtifact,
        TranscriptChapterEnrichmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapterArtifact);
        options ??= new TranscriptChapterEnrichmentOptions();
        var generation = options.ToMetadata();
        if (options.IncludeOverallSummary && overallSummaryAssembler is null)
        {
            throw new InvalidOperationException(
                "An overall summary assembler is required when overall summary output is enabled.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var output = new List<TranscriptChapterEnrichment>(chapterArtifact.Count);
        foreach (var chapter in chapterArtifact)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TranscriptChapterSummary? summary;
            try
            {
                summary = await chapterEnricher
                    .EnrichAsync(
                        new TranscriptChapterEnrichmentRequest(chapter),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (generation.FailurePolicy ==
                    TranscriptChapterEnrichmentFailurePolicy.FailFast)
                {
                    throw new TranscriptChapterEnrichmentException(
                        chapter.Id,
                        "provider_failure",
                        exception);
                }

                output.Add(
                    CreateFailure(
                        chapter,
                        "provider_failure",
                        "The chapter enrichment provider failed."));
                continue;
            }

            if (summary is null)
            {
                if (generation.FailurePolicy ==
                    TranscriptChapterEnrichmentFailurePolicy.FailFast)
                {
                    throw new TranscriptChapterEnrichmentException(
                        chapter.Id,
                        "missing_enrichment");
                }

                output.Add(
                    new TranscriptChapterEnrichment(
                        chapter,
                        TranscriptChapterEnrichmentStatus.Missing,
                        summary: null,
                        failure: null));
                continue;
            }

            try
            {
                output.Add(CreateSuccess(chapter, summary));
            }
            catch (ArgumentException exception)
            {
                if (generation.FailurePolicy ==
                    TranscriptChapterEnrichmentFailurePolicy.FailFast)
                {
                    throw new TranscriptChapterEnrichmentException(
                        chapter.Id,
                        "invalid_enrichment_result",
                        exception);
                }

                output.Add(
                    CreateFailure(
                        chapter,
                        "invalid_enrichment_result",
                        "The chapter enrichment provider returned invalid evidence."));
            }
        }

        TranscriptOverallSummary? overallSummary = null;
        TranscriptChapterEnrichmentFailure? overallSummaryFailure = null;
        if (options.IncludeOverallSummary &&
            output.Any(item =>
                item.Status == TranscriptChapterEnrichmentStatus.Succeeded))
        {
            var successful = output
                .Where(item => item.Status == TranscriptChapterEnrichmentStatus.Succeeded)
                .Select(
                    item => new TranscriptChapterSummaryReference(
                        item.ChapterId,
                        new TranscriptChapterSummary(
                            item.Summary!,
                            item.Keywords,
                            item.Title,
                            item.Evidence)))
                .ToArray();
            var isPartial = output.Any(
                item => item.Status != TranscriptChapterEnrichmentStatus.Succeeded);

            try
            {
                var candidate = await overallSummaryAssembler!
                    .AssembleAsync(
                        new TranscriptOverallSummaryRequest(successful, isPartial),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (candidate is not null)
                {
                    ValidateOverallSummary(candidate, successful, isPartial);
                    overallSummary = candidate;
                }
                else if (generation.FailurePolicy ==
                         TranscriptChapterEnrichmentFailurePolicy.FailFast)
                {
                    throw new TranscriptChapterEnrichmentException(
                        "overall",
                        "missing_overall_summary");
                }
                else
                {
                    overallSummaryFailure = new TranscriptChapterEnrichmentFailure(
                        "missing_overall_summary",
                        "The overall summary assembler returned no summary.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TranscriptChapterEnrichmentException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (generation.FailurePolicy ==
                    TranscriptChapterEnrichmentFailurePolicy.FailFast)
                {
                    throw new TranscriptChapterEnrichmentException(
                        "overall",
                        "overall_summary_provider_failure",
                        exception);
                }

                overallSummaryFailure = new TranscriptChapterEnrichmentFailure(
                    "overall_summary_provider_failure",
                    "The overall summary assembler failed.");
            }
        }

        return new TranscriptChapterEnrichmentDocument(
            chapterArtifact,
            output,
            generation,
            overallSummary,
            overallSummaryFailure);
    }

    private static TranscriptChapterEnrichment CreateSuccess(
        TranscriptChapterArtifact chapter,
        TranscriptChapterSummary summary)
    {
        var sourceSegments = chapter.SourceSegments.ToDictionary(
            segment => segment.Id,
            StringComparer.Ordinal);
        var sourceOrder = chapter.SourceSegmentIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);

        foreach (var evidence in summary.Evidence)
        {
            if (!sourceSegments.TryGetValue(evidence.SourceSegmentId, out var segment))
            {
                throw new ArgumentException(
                    $"Evidence source segment '{evidence.SourceSegmentId}' is not part of the chapter.",
                    nameof(summary));
            }

            if (evidence.SourceId is not null &&
                !string.Equals(
                    evidence.SourceId,
                    segment.SourceId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Evidence source ID for '{evidence.SourceSegmentId}' does not match the chapter.",
                    nameof(summary));
            }

            if (evidence.Start != segment.Start || evidence.End != segment.End)
            {
                throw new ArgumentException(
                    $"Evidence timing for '{evidence.SourceSegmentId}' does not match the chapter.",
                    nameof(summary));
            }
        }

        var canonicalSummary = summary.Evidence.Count == 0
            ? summary
            : new TranscriptChapterSummary(
                summary.Summary,
                summary.Keywords,
                summary.Title,
                summary.Evidence.OrderBy(
                    evidence => sourceOrder[evidence.SourceSegmentId]));

        return new TranscriptChapterEnrichment(
            chapter,
            TranscriptChapterEnrichmentStatus.Succeeded,
            canonicalSummary,
            failure: null);
    }

    private static TranscriptChapterEnrichment CreateFailure(
        TranscriptChapterArtifact chapter,
        string code,
        string message) =>
        new(
            chapter,
            TranscriptChapterEnrichmentStatus.Failed,
            summary: null,
            failure: new TranscriptChapterEnrichmentFailure(code, message));

    private static void ValidateOverallSummary(
        TranscriptOverallSummary summary,
        IReadOnlyList<TranscriptChapterSummaryReference> successful,
        bool isPartial)
    {
        if (summary.IsPartial != isPartial)
        {
            throw new ArgumentException(
                "Overall summary partial state does not match chapter enrichment output.",
                nameof(summary));
        }

        var expected = successful.Select(item => item.ChapterId).ToArray();
        if (!summary.ChapterIds.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Overall summary chapter IDs must match successful chapter order.",
                nameof(summary));
        }
    }
}

/// <summary>
/// Serializes enrichment artifacts without modifying the source chapter artifact.
/// </summary>
public sealed class TranscriptChapterEnrichmentArtifactSerializer
{
    public string Serialize(TranscriptChapterEnrichmentDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", document.SchemaVersion);
            writer.WriteString("chapterArtifactSchemaVersion", document.ChapterArtifactSchemaVersion);
            writer.WriteString("source", document.Source);
            WriteProvenance(writer, document.Provenance);
            WriteChapterGeneration(writer, document.ChapterGeneration);
            writer.WritePropertyName("generation");
            writer.WriteStartObject();
            writer.WriteString("algorithm", document.Generation.Algorithm);
            writer.WriteString("provider", document.Generation.Provider);
            WriteOptionalString(writer, "model", document.Generation.Model);
            writer.WriteString(
                "failurePolicy",
                FormatFailurePolicy(document.Generation.FailurePolicy));
            WriteMetadata(writer, "configuration", document.Generation.Configuration);
            writer.WriteEndObject();
            writer.WriteBoolean("partial", document.IsPartial);
            WriteOverallSummary(writer, document.OverallSummary);
            WriteFailure(writer, "overallSummaryFailure", document.OverallSummaryFailure);
            writer.WritePropertyName("chapters");
            writer.WriteStartArray();

            foreach (var chapter in document)
            {
                writer.WriteStartObject();
                writer.WriteString("schemaVersion", chapter.SchemaVersion);
                writer.WriteString("chapterId", chapter.ChapterId);
                writer.WriteString("artifactTitle", chapter.ArtifactTitle);
                writer.WriteString("start", TranscriptJsonWriter.FormatTimestamp(chapter.Start));
                writer.WriteString("end", TranscriptJsonWriter.FormatTimestamp(chapter.End));
                writer.WriteString("status", FormatStatus(chapter.Status));
                writer.WritePropertyName("sourceSegmentIds");
                WriteStringArray(writer, chapter.SourceSegmentIds);
                writer.WritePropertyName("sourceIds");
                WriteStringArray(writer, chapter.SourceIds);
                WriteMetadata(writer, "sourceMetadata", chapter.SourceMetadata);
                WriteBoundary(writer, chapter.Boundary);
                WriteOptionalString(writer, "summary", chapter.Summary);
                WriteOptionalString(writer, "title", chapter.Title);
                writer.WritePropertyName("keywords");
                WriteStringArray(writer, chapter.Keywords);
                writer.WritePropertyName("evidence");
                writer.WriteStartArray();
                foreach (var evidence in chapter.Evidence)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sourceSegmentId", evidence.SourceSegmentId);
                    WriteOptionalString(writer, "sourceId", evidence.SourceId);
                    writer.WriteString(
                        "start",
                        TranscriptJsonWriter.FormatTimestamp(evidence.Start));
                    writer.WriteString(
                        "end",
                        TranscriptJsonWriter.FormatTimestamp(evidence.End));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                WriteFailure(writer, "failure", chapter.Failure);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteProvenance(
        Utf8JsonWriter writer,
        TranscriptProvenance provenance)
    {
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("provider", provenance.Provider);
        writer.WriteString("model", provenance.Model);
        WriteOptionalString(writer, "packageId", provenance.PackageId);
        WriteOptionalString(writer, "packageVersion", provenance.PackageVersion);
        WriteOptionalString(writer, "source", provenance.Source);
        WriteOptionalString(writer, "cachePath", provenance.CachePath);
        WriteMetadata(writer, "metadata", provenance.Metadata);
        writer.WriteEndObject();
    }

    private static void WriteChapterGeneration(
        Utf8JsonWriter writer,
        TranscriptChapterGenerationMetadata generation)
    {
        writer.WritePropertyName("chapterGeneration");
        writer.WriteStartObject();
        writer.WriteString("algorithm", generation.Algorithm);
        writer.WriteString("provider", generation.Provider);
        WriteMetadata(writer, "configuration", generation.Configuration);
        writer.WriteEndObject();
    }

    private static void WriteOverallSummary(
        Utf8JsonWriter writer,
        TranscriptOverallSummary? summary)
    {
        writer.WritePropertyName("overallSummary");
        if (summary is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("summary", summary.Summary);
        writer.WriteBoolean("partial", summary.IsPartial);
        writer.WritePropertyName("chapterIds");
        WriteStringArray(writer, summary.ChapterIds);
        writer.WriteEndObject();
    }

    private static void WriteBoundary(
        Utf8JsonWriter writer,
        TranscriptChapterBoundaryMetadata boundary)
    {
        writer.WritePropertyName("boundary");
        writer.WriteStartObject();
        writer.WriteString("startSegmentId", boundary.StartSegmentId);
        writer.WriteString("endSegmentId", boundary.EndSegmentId);
        writer.WriteNumber("startOriginalOrdinal", boundary.StartOriginalOrdinal);
        writer.WriteNumber("endOriginalOrdinal", boundary.EndOriginalOrdinal);
        writer.WriteBoolean(
            "startSnappedToSegmentBoundary",
            boundary.StartSnappedToSegmentBoundary);
        writer.WriteBoolean(
            "endSnappedToSegmentBoundary",
            boundary.EndSnappedToSegmentBoundary);
        WriteOptionalTimestamp(writer, "gapBefore", boundary.GapBefore);
        WriteOptionalTimestamp(writer, "gapAfter", boundary.GapAfter);
        writer.WriteEndObject();
    }

    private static void WriteFailure(
        Utf8JsonWriter writer,
        string name,
        TranscriptChapterEnrichmentFailure? failure)
    {
        writer.WritePropertyName(name);
        if (failure is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("code", failure.Code);
        writer.WriteString("message", failure.Message);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(
        Utf8JsonWriter writer,
        IEnumerable<string> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
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

    private static string FormatFailurePolicy(
        TranscriptChapterEnrichmentFailurePolicy policy) =>
        policy switch
        {
            TranscriptChapterEnrichmentFailurePolicy.FailFast => "failFast",
            TranscriptChapterEnrichmentFailurePolicy.PreservePartial =>
                "preservePartial",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };

    private static string FormatStatus(TranscriptChapterEnrichmentStatus status) =>
        status switch
        {
            TranscriptChapterEnrichmentStatus.Succeeded => "succeeded",
            TranscriptChapterEnrichmentStatus.Missing => "missing",
            TranscriptChapterEnrichmentStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}

/// <summary>
/// Writes enrichment artifacts through a temporary file and atomic move.
/// </summary>
public sealed class TranscriptChapterEnrichmentArtifactFileWriter
{
    private readonly TranscriptChapterEnrichmentArtifactSerializer serializer;

    public TranscriptChapterEnrichmentArtifactFileWriter(
        TranscriptChapterEnrichmentArtifactSerializer? serializer = null)
    {
        this.serializer = serializer ?? new TranscriptChapterEnrichmentArtifactSerializer();
    }

    public async Task WriteAsync(
        string outputPath,
        TranscriptChapterEnrichmentDocument document,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(
                "Enrichment output path cannot be empty.",
                nameof(outputPath));
        }

        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("Enrichment output directory could not be resolved.");
        }

        if (File.Exists(destination) && !overwrite)
        {
            throw new IOException(
                $"Enrichment output already exists at '{Path.GetFileName(destination)}'. " +
                "Pass overwrite=true to replace it explicitly.");
        }

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Environment.ProcessId}." +
            $"{Guid.NewGuid():N}.partial");
        var content = Encoding.UTF8.GetBytes(serializer.Serialize(document));

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
