using System.Numerics.Tensors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.TranscriptProcessing;

/// <summary>
/// Builds deterministic transcript windows over canonical DataIngestion elements.
/// </summary>
/// <remarks>
/// The higher DataIngestion semantic chunker cannot be reused here because its
/// <see cref="IngestionChunk"/> output does not retain the source elements that
/// carry transcript timing and provenance metadata. This builder keeps that
/// mapping explicit and uses the standard TextContent embedding and cosine
/// similarity primitives around the transcript-specific boundary rules.
/// </remarks>
public sealed class TranscriptChunkBuilder
{
    private readonly IEmbeddingGenerator<TextContent, Embedding<float>>? embeddingGenerator;
    private readonly ITranscriptChunkScoringProvider? scoringProvider;

    public TranscriptChunkBuilder()
    {
    }

    public TranscriptChunkBuilder(
        IEmbeddingGenerator<TextContent, Embedding<float>> embeddingGenerator,
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
        IngestionDocument document,
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
        IngestionDocument document,
        TranscriptChunkingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var structural = BuildStructural(document, options);
        var evaluated = new List<TranscriptChunkWindow>(structural.Windows.Count);
        Embedding<float>? previousEmbedding = null;

        foreach (var window in structural.Windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window.IsGap)
            {
                previousEmbedding = null;
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
                    [new TextContent(window.Text)],
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

            var semanticSimilarity = previousEmbedding is null
                ? (double?)null
                : (double)TensorPrimitives.CosineSimilarity(
                    previousEmbedding.Vector.Span,
                    embedding.Vector.Span);
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

            previousEmbedding = embedding;
            evaluated.Add(window.WithScore(score, semanticSimilarity));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new TranscriptChunkResult(
            structural.Document,
            structural.Metadata,
            evaluated);
    }

    private static TranscriptChunkResult BuildStructural(
        IngestionDocument document,
        TranscriptChunkingOptions options)
    {
        ValidateOptions(options);
        var input = MapDocument(document);

        if (input.Elements.Count == 0)
        {
            return new TranscriptChunkResult(
                input.Document,
                input.Metadata,
                Array.Empty<TranscriptChunkWindow>());
        }

        var requestedStart = options.RequestedStart ?? input.Elements[0].Metadata.Start;
        var requestedEnd = options.RequestedEnd ??
            input.Elements[^1].Metadata.End;
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
        var run = new List<TranscriptChunkSourceElement>();
        TranscriptChunkSourceElement? previousElement = null;
        var firstElement = input.Elements[0];
        var lastElement = input.Elements[^1];

        if (requestedStart < firstElement.Metadata.Start &&
            requestedEnd > firstElement.Metadata.Start)
        {
            AddGapWindows(
                result,
                input.Document,
                requestedStart,
                firstElement.Metadata.Start,
                options.MaximumDuration);
        }

        foreach (var element in input.Elements)
        {
            if (previousElement is not null &&
                element.Metadata.Start > previousElement.Metadata.End &&
                element.Metadata.Start > requestedStart &&
                previousElement.Metadata.End < requestedEnd)
            {
                AddRunWindows(result, input, run, options);
                run.Clear();
                AddGapWindows(
                    result,
                    input.Document,
                    previousElement.Metadata.End,
                    element.Metadata.Start,
                    options.MaximumDuration);
            }

            if (element.Metadata.End <= requestedStart ||
                element.Metadata.Start >= requestedEnd)
            {
                previousElement = element;
                continue;
            }

            run.Add(element);
            previousElement = element;
        }

        AddRunWindows(result, input, run, options);

        if (requestedEnd > lastElement.Metadata.End &&
            requestedStart < lastElement.Metadata.End)
        {
            AddGapWindows(
                result,
                input.Document,
                lastElement.Metadata.End,
                requestedEnd,
                options.MaximumDuration);
        }

        return new TranscriptChunkResult(
            input.Document,
            input.Metadata,
            result);
    }

    private static void AddGapWindows(
        ICollection<TranscriptChunkWindow> output,
        IngestionDocument document,
        TimeSpan start,
        TimeSpan end,
        TimeSpan maximumDuration)
    {
        var duration = end - start;
        var windowCount = duration.Ticks / maximumDuration.Ticks +
            (duration.Ticks % maximumDuration.Ticks == 0 ? 0 : 1);
        var ticksPerWindow = duration.Ticks / windowCount;
        var remainder = duration.Ticks % windowCount;
        var windowStart = start;

        for (long index = 0; index < windowCount; index++)
        {
            var windowTicks = ticksPerWindow + (index < remainder ? 1 : 0);
            var windowEnd = windowStart + TimeSpan.FromTicks(windowTicks);
            output.Add(new TranscriptChunkWindow(document, windowStart, windowEnd, []));
            windowStart = windowEnd;
        }
    }

    private static void AddRunWindows(
        ICollection<TranscriptChunkWindow> output,
        TranscriptChunkInput input,
        IReadOnlyList<TranscriptChunkSourceElement> run,
        TranscriptChunkingOptions options)
    {
        if (run.Count == 0)
        {
            return;
        }

        var best = new Partition?[run.Count + 1];
        best[0] = new Partition(0, 0, -1, 0);

        for (var start = 0; start < run.Count; start++)
        {
            if (best[start] is not { } prefix)
            {
                continue;
            }

            for (var end = start; end < run.Count; end++)
            {
                var candidateDuration =
                    run[end].Metadata.End - run[start].Metadata.Start;
                if (end > start && candidateDuration > options.MaximumDuration)
                {
                    break;
                }

                var candidate = new Partition(
                    prefix.ShortWindowCount +
                        (candidateDuration < options.MinimumDuration ? 1 : 0),
                    prefix.WindowCount + 1,
                    start,
                    end + 1);
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
        var ends = new List<int>();
        for (var state = final; state.EndIndex > 0; state = best[state.PreviousIndex]!)
        {
            ends.Add(state.EndIndex);
        }

        ends.Reverse();
        var startIndex = 0;
        foreach (var endIndex in ends)
        {
            output.Add(
                new TranscriptChunkWindow(
                    input.Document,
                    run[startIndex].Metadata.Start,
                    run[endIndex - 1].Metadata.End,
                    run.Skip(startIndex).Take(endIndex - startIndex).ToArray()));
            startIndex = endIndex;
        }
    }

    private static TranscriptChunkInput MapDocument(IngestionDocument document)
    {
        var metadata = TranscriptIngestionAdapter.RequireDocumentMetadata(document);
        if (!string.Equals(
                metadata.SchemaVersion,
                TranscriptSchema.CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new TranscriptFormatException(
                "unsupported_schema_version",
                "$.schemaVersion",
                $"Expected '{TranscriptSchema.CurrentVersion}'.");
        }

        var elements = new List<TranscriptChunkSourceElement>();
        foreach (var element in document.EnumerateContent())
        {
            if (element is not IngestionDocumentParagraph paragraph)
            {
                throw new TranscriptFormatException(
                    "invalid_ingestion_document",
                    "$",
                    "Transcript documents can contain only paragraph elements.");
            }

            var segmentMetadata =
                TranscriptIngestionAdapter.RequireSegmentMetadata(paragraph);
            ValidateSegmentMetadata(segmentMetadata);
            elements.Add(new TranscriptChunkSourceElement(paragraph, segmentMetadata));
        }

        for (var index = 1; index < elements.Count; index++)
        {
            var previous = elements[index - 1].Metadata;
            var current = elements[index].Metadata;
            if (current.OriginalOrdinal <= previous.OriginalOrdinal)
            {
                throw new TranscriptFormatException(
                    "invalid_ingestion_document",
                    "$",
                    "Transcript segment ordinals must be strictly increasing.");
            }

            if (current.Start < previous.Start ||
                current.Start < previous.End)
            {
                throw new TranscriptFormatException(
                    "invalid_ingestion_document",
                    "$",
                    "Transcript segment timings must be monotonic and non-overlapping.");
            }
        }

        return new TranscriptChunkInput(document, metadata, elements);
    }

    private static void ValidateSegmentMetadata(TranscriptSegmentMetadata metadata)
    {
        if (metadata.OriginalOrdinal < 0 ||
            metadata.Start < TimeSpan.Zero ||
            metadata.End <= metadata.Start ||
            metadata.SourceMetadata is null ||
            (metadata.Confidence is not null &&
             (!double.IsFinite(metadata.Confidence.Value) ||
              metadata.Confidence.Value < 0 ||
              metadata.Confidence.Value > 1)))
        {
            throw new TranscriptFormatException(
                "invalid_ingestion_document",
                "$",
                "Transcript segment metadata contains invalid timing or confidence.");
        }
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

    private sealed record TranscriptChunkInput(
        IngestionDocument Document,
        TranscriptDocumentMetadata Metadata,
        IReadOnlyList<TranscriptChunkSourceElement> Elements);

    private sealed record Partition(
        int ShortWindowCount,
        int WindowCount,
        int PreviousIndex,
        int EndIndex)
    {
        public bool IsBetterThan(Partition other) =>
            ShortWindowCount < other.ShortWindowCount ||
            (ShortWindowCount == other.ShortWindowCount &&
             WindowCount < other.WindowCount);
    }
}
