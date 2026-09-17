namespace AudioTranscriber;

public sealed record ModelProvenance
{
    public string Provider { get; init; }
    public string Model { get; init; }
    public string PackageId { get; init; }
    public string PackageVersion { get; init; }
    public string Source { get; init; }
    public string CachePath { get; init; }

    public ModelProvenance(
        string provider,
        string model,
        string packageId,
        string packageVersion,
        string source,
        string cachePath)
    {
        Provider = RequireText(provider, nameof(provider));
        Model = RequireText(model, nameof(model));
        PackageId = RequireText(packageId, nameof(packageId));
        PackageVersion = RequireText(packageVersion, nameof(packageVersion));
        Source = RequireText(source, nameof(source));
        CachePath = RequireText(cachePath, nameof(cachePath));
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be empty.", parameterName)
            : value;
}

public sealed record TranscriptSegment
{
    public string Text { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    public TranscriptSegment(string text, TimeSpan start, TimeSpan end)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Transcript text cannot be empty.", nameof(text));
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Segment start cannot be negative.");
        }

        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "Segment end must be greater than its start.");
        }

        Text = text.Trim();
        Start = start;
        End = end;
    }
}

public sealed record Transcript
{
    public string SourcePath { get; }
    public ModelProvenance Provenance { get; }
    public IReadOnlyList<TranscriptSegment> Segments { get; }
    public string Text => string.Join(' ', Segments.Select(segment => segment.Text));

    public Transcript(
        string sourcePath,
        ModelProvenance provenance,
        IEnumerable<TranscriptSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
        }

        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        var orderedSegments = (segments ?? throw new ArgumentNullException(nameof(segments))).ToArray();

        for (var index = 1; index < orderedSegments.Length; index++)
        {
            if (orderedSegments[index].Start < orderedSegments[index - 1].Start)
            {
                throw new ArgumentException("Transcript segments must be ordered by start time.", nameof(segments));
            }
        }

        SourcePath = Path.GetFullPath(sourcePath);
        Segments = Array.AsReadOnly(orderedSegments);
    }
}

public sealed record BatchTranscript
{
    public IReadOnlyList<Transcript> Transcripts { get; }

    public BatchTranscript(IEnumerable<Transcript> transcripts)
    {
        var items = (transcripts ?? throw new ArgumentNullException(nameof(transcripts))).ToArray();
        Transcripts = Array.AsReadOnly(items);
    }
}
