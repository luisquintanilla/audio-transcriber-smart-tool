using System.Collections.ObjectModel;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.TranscriptIngestion;

/// <summary>
/// A stable, runtime-independent ingestion representation of a transcript document.
/// </summary>
/// <remarks>
/// This contract intentionally does not reference the preview
/// <c>Microsoft.Extensions.DataIngestion</c> packages. Consumers can map it to their
/// selected ingestion runtime without changing the core transcript contracts.
/// </remarks>
public sealed class TranscriptIngestionDocument
{
    internal TranscriptIngestionDocument(
        string source,
        string schemaVersion,
        TranscriptIngestionProvenance provenance,
        IReadOnlyList<TranscriptIngestionSegment> segments)
    {
        Identifier = source;
        Source = source;
        SchemaVersion = schemaVersion;
        Provenance = provenance;
        Segments = segments;
    }

    /// <summary>
    /// Gets the stable source identifier for the document.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Gets the source reported by the transcript producer.
    /// </summary>
    public string Source { get; }

    /// <summary>
    /// Gets the validated transcript schema version.
    /// </summary>
    public string SchemaVersion { get; }

    /// <summary>
    /// Gets the producer and source metadata for the document.
    /// </summary>
    public TranscriptIngestionProvenance Provenance { get; }

    /// <summary>
    /// Gets the transcript segments in original-ordinal order.
    /// </summary>
    public IReadOnlyList<TranscriptIngestionSegment> Segments { get; }

    /// <summary>
    /// Gets the normalized document text in segment order.
    /// </summary>
    public string Text => string.Join(' ', Segments.Select(segment => segment.Text));
}

/// <summary>
/// Producer and source metadata carried with an ingested transcript.
/// </summary>
public sealed class TranscriptIngestionProvenance
{
    internal TranscriptIngestionProvenance(Processing.TranscriptProvenance provenance)
    {
        Provider = provenance.Provider;
        Model = provenance.Model;
        PackageId = provenance.PackageId;
        PackageVersion = provenance.PackageVersion;
        Source = provenance.Source;
        CachePath = provenance.CachePath;
        Metadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(provenance.Metadata, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the transcription provider.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// Gets the transcription model.
    /// </summary>
    public string Model { get; }

    /// <summary>
    /// Gets the optional package identifier used by the provider.
    /// </summary>
    public string? PackageId { get; }

    /// <summary>
    /// Gets the optional package version used by the provider.
    /// </summary>
    public string? PackageVersion { get; }

    /// <summary>
    /// Gets the optional provider source.
    /// </summary>
    public string? Source { get; }

    /// <summary>
    /// Gets the optional local cache path used by the provider.
    /// </summary>
    public string? CachePath { get; }

    /// <summary>
    /// Gets provider metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }
}

/// <summary>
/// A transcript segment represented for ingestion.
/// </summary>
public sealed class TranscriptIngestionSegment
{
    internal TranscriptIngestionSegment(Processing.TranscriptSegment segment)
    {
        Id = segment.Id;
        SourceId = segment.SourceId;
        OriginalOrdinal = segment.OriginalOrdinal;
        Start = segment.Start;
        End = segment.End;
        Text = segment.Text;
        Speaker = segment.Speaker;
        Confidence = segment.Confidence;
        SourceMetadata = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(segment.SourceMetadata, StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets the stable segment identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the optional identifier assigned by the source recognizer.
    /// </summary>
    public string? SourceId { get; }

    /// <summary>
    /// Gets the original ordinal assigned by the source recognizer.
    /// </summary>
    public int OriginalOrdinal { get; }

    /// <summary>
    /// Gets the segment start offset.
    /// </summary>
    public TimeSpan Start { get; }

    /// <summary>
    /// Gets the segment end offset.
    /// </summary>
    public TimeSpan End { get; }

    /// <summary>
    /// Gets the normalized segment text.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the optional speaker identifier.
    /// </summary>
    public string? Speaker { get; }

    /// <summary>
    /// Gets the optional recognition confidence.
    /// </summary>
    public double? Confidence { get; }

    /// <summary>
    /// Gets metadata supplied by the transcript source.
    /// </summary>
    public IReadOnlyDictionary<string, string> SourceMetadata { get; }
}
