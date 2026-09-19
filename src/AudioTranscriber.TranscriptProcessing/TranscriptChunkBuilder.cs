using Microsoft.Extensions.AI;

namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// Builds deterministic transcript windows without requiring an embedding model.
/// </summary>
public sealed class TranscriptChunkBuilder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator;
    private readonly ITranscriptChunkScoringProvider? scoringProvider;

    public TranscriptChunkBuilder()
    {
    }

    public TranscriptChunkBuilder(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ITranscriptChunkScoringProvider scoringProvider)
    {
        this.embeddingGenerator = embeddingGenerator
            ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this.scoringProvider = scoringProvider
            ?? throw new ArgumentNullException(nameof(scoringProvider));
    }

    /// <summary>
    /// Builds structural windows without invoking asynchronous model services.
    /// </summary>
    public TranscriptChunkResult Build(
        TranscriptDocument document,
        TranscriptChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        return BuildStructural(document, options);
    }

    /// <summary>
    /// Builds windows and evaluates each content window through the injected seams.
    /// </summary>
    public async Task<TranscriptChunkResult> BuildAsync(
        TranscriptDocument document,
        TranscriptChunkingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var structural = BuildStructural(document, options);
        var evaluated = new List<TranscriptChunkWindow>(structural.Windows.Count);

        foreach (var window in structural.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window.IsGap)
            {
                evaluated.Add(window);
                continue;
            }

            var embeddingGenerator = this.embeddingGenerator
                ?? throw new InvalidOperationException(
                    "An embedding generator is required for asynchronous chunk evaluation.");
            var scoringProvider = this.scoringProvider
                ?? throw new InvalidOperationException(
                    "A chunk scoring provider is required for asynchronous chunk evaluation.");

            var embeddings = await embeddingGenerator
                .GenerateAsync(
                    [window.Text],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (embeddings.Count != 1)
            {
                throw new InvalidOperationException(
                    "Embedding generators must return one embedding per input.");
            }

            var embedding = embeddings[0]
                ?? throw new InvalidOperationException(
                    "Embedding generators must not return null embeddings.");
            cancellationToken.ThrowIfCancellationRequested();

            var score = await scoringProvider
                .ScoreAsync(
                    new TranscriptChunkScoringRequest(
                        window.Text,
                        window.SourceSegments,
                        window.Start,
                        window.End,
                        embedding),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!double.IsFinite(score))
            {
                throw new InvalidOperationException(
                    "Transcript chunk scoring providers must return finite scores.");
            }

            evaluated.Add(window.WithScore(score));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new TranscriptChunkResult(
            structural.Source,
            structural.Provenance,
            evaluated);
    }

    private static TranscriptChunkResult BuildStructural(
        TranscriptDocument document,
        TranscriptChunkingOptions options)
    {
        ValidateOptions(options);
        ValidateSegments(document.Segments);

        if (document.Segments.Count == 0)
        {
            return new TranscriptChunkResult(
                document.Source,
                document.Provenance,
                Array.Empty<TranscriptChunkWindow>());
        }

        var requestedStart = options.RequestedStart ?? document.Segments[0].Start;
        var requestedEnd = options.RequestedEnd ?? document.Segments[^1].End;
        if (requestedStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RequestedStart),
                "Requested start cannot be negative.");
        }

        if (requestedEnd <= requestedStart)
        {
            throw new ArgumentException(
                "Requested end must be greater than requested start.",
                nameof(options));
        }

        var result = new List<TranscriptChunkWindow>();
        var run = new List<TranscriptSegment>();
        var previousSegment = (TranscriptSegment?)null;

        foreach (var segment in document.Segments)
        {
            if (previousSegment is not null &&
                segment.Start > previousSegment.End &&
                segment.Start > requestedStart &&
                previousSegment.End < requestedEnd)
            {
                AddRunWindows(result, run, options);
                run.Clear();
                result.Add(new TranscriptChunkWindow(
                    previousSegment.End,
                    segment.Start,
                    []));
            }

            if (segment.End <= requestedStart || segment.Start >= requestedEnd)
            {
                previousSegment = segment;
                continue;
            }

            if (run.Count > 0)
            {
                var previous = run[^1];
                if (segment.Start > previous.End)
                {
                    AddRunWindows(result, run, options);
                    result.Add(new TranscriptChunkWindow(previous.End, segment.Start, []));
                    run.Clear();
                }
            }

            run.Add(segment);
            previousSegment = segment;
        }

        AddRunWindows(result, run, options);

        return new TranscriptChunkResult(
            document.Source,
            document.Provenance,
            result);
    }

    private static void AddRunWindows(
        ICollection<TranscriptChunkWindow> output,
        IReadOnlyList<TranscriptSegment> run,
        TranscriptChunkingOptions options)
    {
        if (run.Count == 0)
        {
            return;
        }

        var best = new Partition?[run.Count + 1];
        best[0] = new Partition(0, 0, []);

        for (var start = 0; start < run.Count; start++)
        {
            if (best[start] is not { } prefix)
            {
                continue;
            }

            for (var end = start; end < run.Count; end++)
            {
                var candidateDuration = run[end].End - run[start].Start;
                if (end > start && candidateDuration > options.MaximumDuration)
                {
                    break;
                }

                var candidate = new Partition(
                    prefix.ShortWindowCount +
                        (candidateDuration < options.MinimumDuration ? 1 : 0),
                    prefix.WindowCount + 1,
                    [.. prefix.Ends, end + 1]);
                if (best[end + 1] is not { } existing ||
                    candidate.IsBetterThan(existing))
                {
                    best[end + 1] = candidate;
                }
            }
        }

        var final = best[^1]
            ?? throw new InvalidOperationException(
                "Transcript segments could not be partitioned into windows.");
        var startIndex = 0;
        foreach (var endIndex in final.Ends)
        {
            output.Add(
                CreateContentWindow(
                    run.Skip(startIndex).Take(endIndex - startIndex).ToArray()));
            startIndex = endIndex;
        }
    }

    private sealed record Partition(
        int ShortWindowCount,
        int WindowCount,
        IReadOnlyList<int> Ends)
    {
        public bool IsBetterThan(Partition other) =>
            ShortWindowCount < other.ShortWindowCount ||
            (ShortWindowCount == other.ShortWindowCount &&
             WindowCount < other.WindowCount);
    }

    private static TranscriptChunkWindow CreateContentWindow(
        IReadOnlyList<TranscriptSegment> segments)
    {
        return new TranscriptChunkWindow(
            segments[0].Start,
            segments[^1].End,
            segments);
    }

    private static void ValidateOptions(TranscriptChunkingOptions options)
    {
        if (options.MinimumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Minimum duration must be greater than zero.",
                nameof(options));
        }

        if (options.MaximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Maximum duration must be greater than zero.",
                nameof(options));
        }

        if (options.MinimumDuration > options.MaximumDuration)
        {
            throw new ArgumentException(
                "Minimum duration cannot exceed maximum duration.",
                nameof(options));
        }
    }

    private static void ValidateSegments(IReadOnlyList<TranscriptSegment> segments)
    {
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index]
                ?? throw new ArgumentException(
                    "Transcript segments cannot contain null entries.",
                    nameof(segments));

            if (segment.Start < TimeSpan.Zero || segment.End <= segment.Start)
            {
                throw new ArgumentException(
                    "Transcript segment timing is malformed.",
                    nameof(segments));
            }

            if (index == 0)
            {
                continue;
            }

            var previous = segments[index - 1];
            if (segment.Start < previous.Start || segment.Start < previous.End)
            {
                throw new ArgumentException(
                    "Transcript segment timings must be monotonic and non-overlapping.",
                    nameof(segments));
            }
        }
    }
}
