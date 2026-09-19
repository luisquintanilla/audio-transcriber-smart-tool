namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// Applies deterministic, non-destructive normalization to transcript documents.
/// </summary>
public sealed class TranscriptNormalizer
{
    public TranscriptDocument Normalize(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var segments = document.Segments
            .Select(
                segment => new TranscriptSegment(
                    segment.Text,
                    segment.Start,
                    segment.End,
                    segment.OriginalOrdinal,
                    segment.SourceId,
                    segment.Id,
                    segment.Speaker,
                    segment.Confidence,
                    segment.SourceMetadata))
            .ToArray();

        return new TranscriptDocument(
            document.Source,
            document.Provenance,
            segments,
            document.SchemaVersion);
    }
}

internal static class TranscriptText
{
    public static string NormalizeWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
