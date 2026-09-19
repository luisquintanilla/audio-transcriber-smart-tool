using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.TranscriptIngestion;

/// <summary>
/// Maps validated transcript documents into the stable ingestion boundary.
/// </summary>
public sealed class TranscriptIngestionAdapter
{
    /// <summary>
    /// Maps one validated transcript document.
    /// </summary>
    /// <param name="document">The validated transcript document.</param>
    /// <returns>The mapped ingestion document.</returns>
    public TranscriptIngestionDocument Map(Processing.TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var segments = document.Segments
            .Select(segment =>
            {
                ArgumentNullException.ThrowIfNull(segment);
                return new TranscriptIngestionSegment(segment);
            })
            .ToArray();

        return new TranscriptIngestionDocument(
            document.Source,
            document.SchemaVersion,
            new TranscriptIngestionProvenance(document.Provenance),
            Array.AsReadOnly(segments));
    }

    /// <summary>
    /// Maps transcript documents in enumeration order.
    /// </summary>
    /// <param name="documents">The validated transcript documents.</param>
    /// <param name="cancellationToken">A token used to stop mapping between documents.</param>
    /// <returns>The mapped documents in input order.</returns>
    public IReadOnlyList<TranscriptIngestionDocument> Map(
        IEnumerable<Processing.TranscriptDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        var mapped = new List<TranscriptIngestionDocument>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document is null)
            {
                throw new ArgumentException(
                    "Transcript documents cannot contain null entries.",
                    nameof(documents));
            }

            mapped.Add(Map(document));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return mapped.AsReadOnly();
    }
}
