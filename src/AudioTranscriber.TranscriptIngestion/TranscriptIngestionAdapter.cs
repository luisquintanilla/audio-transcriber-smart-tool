using DataIngestion = Microsoft.Extensions.DataIngestion;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.TranscriptIngestion;

/// <summary>
/// Maps validated transcript documents to the standard DataIngestion boundary.
/// </summary>
public sealed class TranscriptIngestionAdapter
{
    /// <summary>
    /// Maps one validated transcript document to a standard
    /// <see cref="DataIngestion.IngestionDocument"/>.
    /// </summary>
    /// <param name="document">The validated transcript document.</param>
    /// <param name="identifier">
    /// The optional ingestion identifier; the transcript source is used by default.
    /// </param>
    /// <returns>The mapped ingestion document.</returns>
    public DataIngestion.IngestionDocument Map(
        Processing.TranscriptDocument document,
        string? identifier = null)
    {
        return Processing.TranscriptIngestionAdapter.ToIngestionDocument(
            document,
            identifier);
    }

    /// <summary>
    /// Maps transcript documents in input order, stopping before enumeration
    /// when the token is already canceled.
    /// </summary>
    /// <param name="documents">The validated transcript documents.</param>
    /// <param name="cancellationToken">A token used to stop mapping between documents.</param>
    /// <returns>The mapped documents in input order.</returns>
    public IReadOnlyList<DataIngestion.IngestionDocument> Map(
        IEnumerable<Processing.TranscriptDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        var mapped = new List<DataIngestion.IngestionDocument>();
        using var enumerator = documents.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var document = enumerator.Current;
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
