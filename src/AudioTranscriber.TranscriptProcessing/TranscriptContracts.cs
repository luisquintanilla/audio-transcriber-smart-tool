using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AudioTranscriber.TranscriptProcessing;

public static class TranscriptSchema
{
    public const string CurrentVersion = "1.0";
}

public sealed record TranscriptProvenance
{
    public string Provider { get; }
    public string Model { get; }
    public string? PackageId { get; }
    public string? PackageVersion { get; }
    public string? Source { get; }
    public string? CachePath { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public TranscriptProvenance(
        string provider,
        string model,
        string? packageId = null,
        string? packageVersion = null,
        string? source = null,
        string? cachePath = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null)
    {
        Provider = RequireText(provider, nameof(provider));
        Model = RequireText(model, nameof(model));
        PackageId = NormalizeOptional(packageId);
        PackageVersion = NormalizeOptional(packageVersion);
        Source = NormalizeOptional(source);
        CachePath = NormalizeOptional(cachePath);
        Metadata = ReadOnlyMetadata(metadata, nameof(metadata));
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value.Trim();

    internal static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static IReadOnlyDictionary<string, string> ReadOnlyMetadata(
        IEnumerable<KeyValuePair<string, string>>? metadata,
        string parameterName)
    {
        if (metadata is null)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in metadata)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("Metadata keys cannot be empty.", parameterName);
            }

            if (entry.Value is null)
            {
                throw new ArgumentException("Metadata values cannot be null.", parameterName);
            }

            if (!values.TryAdd(entry.Key.Trim(), entry.Value))
            {
                throw new ArgumentException($"Duplicate metadata key '{entry.Key}'.", parameterName);
            }
        }

        return new ReadOnlyDictionary<string, string>(values);
    }
}

public sealed record TranscriptSegment
{
    public string Id { get; }
    public string? SourceId { get; }
    public int OriginalOrdinal { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public string Text { get; }
    public string? Speaker { get; }
    public double? Confidence { get; }
    public IReadOnlyDictionary<string, string> SourceMetadata { get; }

    public TranscriptSegment(
        string text,
        TimeSpan start,
        TimeSpan end,
        int originalOrdinal,
        string? sourceId = null,
        string? id = null,
        string? speaker = null,
        double? confidence = null,
        IEnumerable<KeyValuePair<string, string>>? sourceMetadata = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Transcript segment text cannot be empty or whitespace.",
                nameof(text));
        }

        if (originalOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originalOrdinal),
                "Original ordinal cannot be negative.");
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "Segment start cannot be negative.");
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(end),
                "Segment end must be greater than its start.");
        }

        if (confidence is not null &&
            (!double.IsFinite(confidence.Value) || confidence.Value < 0 || confidence.Value > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                "Confidence must be finite and between zero and one.");
        }

        Text = TranscriptText.NormalizeWhitespace(text);
        Start = start;
        End = end;
        OriginalOrdinal = originalOrdinal;
        SourceId = TranscriptProvenance.NormalizeOptional(sourceId);
        SourceMetadata = TranscriptProvenance.ReadOnlyMetadata(
            sourceMetadata,
            nameof(sourceMetadata));
        Id = TranscriptProvenance.NormalizeOptional(id)
            ?? SourceId
            ?? CreateDeterministicId(
                Text,
                Start,
                End,
                OriginalOrdinal,
                speaker,
                confidence,
                SourceMetadata);
        Speaker = TranscriptProvenance.NormalizeOptional(speaker);
        Confidence = confidence;
    }

    private static string CreateDeterministicId(
        string text,
        TimeSpan start,
        TimeSpan end,
        int originalOrdinal,
        string? speaker,
        double? confidence,
        IEnumerable<KeyValuePair<string, string>>? sourceMetadata)
    {
        var metadata = (sourceMetadata ?? [])
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key.Trim()}={entry.Value}");
        var canonical = string.Join(
            "\u001f",
            originalOrdinal.ToString(CultureInfo.InvariantCulture),
            start.Ticks.ToString(CultureInfo.InvariantCulture),
            end.Ticks.ToString(CultureInfo.InvariantCulture),
            text.Trim(),
            TranscriptProvenance.NormalizeOptional(speaker) ?? string.Empty,
            confidence?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join("\u001e", metadata));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"seg-{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
    }
}

/// <summary>
/// A versioned, model-independent transcript document.
/// </summary>
/// <remarks>
/// Documents are ordered by <see cref="TranscriptSegment.OriginalOrdinal"/>.
/// Segment starts must be monotonic and segments may be adjacent or separated
/// by legitimate gaps. Overlapping segments are rejected. Empty or
/// whitespace-only segment text is rejected; fragments are never merged.
/// </remarks>
public sealed record TranscriptDocument
{
    public string SchemaVersion { get; }
    public string Source { get; }
    public TranscriptProvenance Provenance { get; }
    public IReadOnlyList<TranscriptSegment> Segments { get; }
    public string Text => string.Join(' ', Segments.Select(segment => segment.Text));

    public TranscriptDocument(
        string source,
        TranscriptProvenance provenance,
        IEnumerable<TranscriptSegment> segments,
        string schemaVersion = TranscriptSchema.CurrentVersion)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Document source cannot be empty.", nameof(source));
        }

        if (!string.Equals(schemaVersion, TranscriptSchema.CurrentVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported transcript schema version '{schemaVersion}'.",
                nameof(schemaVersion));
        }

        Source = source.Trim();
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        SchemaVersion = schemaVersion;

        var input = (segments ?? throw new ArgumentNullException(nameof(segments))).ToArray();
        if (input.Any(segment => segment is null))
        {
            throw new ArgumentException("Transcript segments cannot contain null entries.", nameof(segments));
        }

        var ordered = input
            .OrderBy(segment => segment.OriginalOrdinal)
            .ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].OriginalOrdinal == ordered[index - 1].OriginalOrdinal)
            {
                throw new ArgumentException(
                    "Transcript segment original ordinals must be unique.",
                    nameof(segments));
            }

            if (ordered[index].Start < ordered[index - 1].Start)
            {
                throw new ArgumentException(
                    "Transcript segment start times must be monotonic.",
                    nameof(segments));
            }

            if (ordered[index].Start < ordered[index - 1].End)
            {
                throw new ArgumentException(
                    "Transcript segments cannot overlap; gaps are allowed.",
                    nameof(segments));
            }
        }

        Segments = Array.AsReadOnly(ordered);
    }
}
