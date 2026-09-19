using System.Collections.ObjectModel;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// Converts the stable transcript contract to and from the standard DataIngestion
/// document exchange model.
/// </summary>
public static class TranscriptIngestionAdapter
{
    public const string DocumentMetadataKey = "audioTranscriber.transcript.document";
    public const string SegmentMetadataKey = "audioTranscriber.transcript.segment";

    /// <summary>
    /// Creates a DataIngestion document whose elements retain the typed transcript
    /// metadata needed to reconstruct the source document.
    /// </summary>
    public static IngestionDocument ToIngestionDocument(
        TranscriptDocument document,
        string? identifier = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var ingestionDocument = new IngestionDocument(
            string.IsNullOrWhiteSpace(identifier) ? document.Source : identifier.Trim());
        var section = new IngestionDocumentSection();
        section.Metadata[DocumentMetadataKey] = TranscriptDocumentMetadata.From(document);

        foreach (var segment in document.Segments)
        {
            var paragraph = new IngestionDocumentParagraph(segment.Text)
            {
                Text = segment.Text
            };
            paragraph.Metadata[SegmentMetadataKey] = TranscriptSegmentMetadata.From(segment);
            section.Elements.Add(paragraph);
        }

        ingestionDocument.Sections.Add(section);
        return ingestionDocument;
    }

    /// <summary>
    /// Reconstructs a transcript from a DataIngestion document produced by this adapter.
    /// </summary>
    /// <exception cref="TranscriptFormatException">
    /// The document does not contain the adapter metadata or has invalid transcript values.
    /// </exception>
    public static TranscriptDocument ToTranscriptDocument(IngestionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var metadata = RequireDocumentMetadata(document);
        var segments = new List<TranscriptSegment>();
        foreach (var element in document.EnumerateContent())
        {
            if (element is not IngestionDocumentParagraph paragraph)
            {
                throw Failure(
                    "Only paragraph elements produced by the transcript adapter can be converted.");
            }

            var segmentMetadata = RequireSegmentMetadata(paragraph);

            try
            {
                segments.Add(segmentMetadata.CreateSegment(paragraph.Text));
            }
            catch (ArgumentException exception)
            {
                throw Failure(
                    $"Transcript segment metadata is invalid: {exception.Message}",
                    exception);
            }
        }

        try
        {
            return new TranscriptDocument(
                metadata.Source,
                metadata.Provenance,
                segments,
                metadata.SchemaVersion);
        }
        catch (ArgumentException exception)
        {
            throw Failure(
                $"Transcript document metadata is invalid: {exception.Message}",
                exception);
        }
    }

    internal static TranscriptDocumentMetadata RequireDocumentMetadata(
        IngestionDocument document)
    {
        TranscriptDocumentMetadata? result = null;
        foreach (var section in document.Sections)
        {
            if (!section.Metadata.TryGetValue(DocumentMetadataKey, out var value))
            {
                continue;
            }

            if (value is not TranscriptDocumentMetadata metadata)
            {
                throw Failure("Document metadata has an unexpected type.");
            }

            if (result is not null)
            {
                throw Failure("A document cannot contain duplicate transcript metadata.");
            }

            result = metadata;
        }

        return result ?? throw Failure("Document is missing typed transcript metadata.");
    }

    internal static TranscriptSegmentMetadata RequireSegmentMetadata(
        IngestionDocumentElement element)
    {
        if (!element.Metadata.TryGetValue(SegmentMetadataKey, out var value) ||
            value is not TranscriptSegmentMetadata metadata)
        {
            throw Failure(
                "A transcript paragraph is missing typed segment metadata.");
        }

        return metadata;
    }

    private static TranscriptFormatException Failure(
        string message,
        Exception? innerException = null) =>
        new("invalid_ingestion_document", "$", message, innerException);
}

/// <summary>
/// Typed document metadata stored on the standard DataIngestion section.
/// </summary>
public sealed record TranscriptDocumentMetadata(
    string SchemaVersion,
    string Source,
    TranscriptProvenance Provenance)
{
    public static TranscriptDocumentMetadata From(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var provenance = document.Provenance;
        return new(
            document.SchemaVersion,
            document.Source,
            new TranscriptProvenance(
                provenance.Provider,
                provenance.Model,
                provenance.PackageId,
                provenance.PackageVersion,
                provenance.Source,
                provenance.CachePath,
                provenance.Metadata));
    }
}

/// <summary>
/// Typed segment metadata stored on the standard DataIngestion paragraph.
/// </summary>
public sealed record TranscriptSegmentMetadata(
    string Id,
    string? SourceId,
    int OriginalOrdinal,
    TimeSpan Start,
    TimeSpan End,
    string? Speaker,
    double? Confidence,
    IReadOnlyDictionary<string, string> SourceMetadata,
    string Text)
{
    public static TranscriptSegmentMetadata From(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new(
            segment.Id,
            segment.SourceId,
            segment.OriginalOrdinal,
            segment.Start,
            segment.End,
            segment.Speaker,
            segment.Confidence,
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(
                    segment.SourceMetadata,
                    StringComparer.Ordinal)),
            segment.Text);
    }

    internal TranscriptSegment CreateSegment(string? text) =>
        new(
            text ?? Text,
            Start,
            End,
            OriginalOrdinal,
            SourceId,
            Id,
            Speaker,
            Confidence,
            SourceMetadata);
}

/// <summary>
/// Reads transcript JSON through the standard DataIngestion reader boundary.
/// </summary>
public sealed class TranscriptIngestionDocumentReader : IngestionDocumentReader
{
    private readonly TranscriptJsonReader _reader = new();

    public override async Task<IngestionDocument> ReadAsync(
        Stream source,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException(
                "Transcript ingestion identifier cannot be empty.",
                nameof(identifier));
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw new ArgumentException(
                "Transcript ingestion media type cannot be empty.",
                nameof(mediaType));
        }

        var transcript = await _reader.ReadDocumentAsync(
            source,
            cancellationToken).ConfigureAwait(false);
        return TranscriptIngestionAdapter.ToIngestionDocument(
            transcript,
            identifier);
    }
}
