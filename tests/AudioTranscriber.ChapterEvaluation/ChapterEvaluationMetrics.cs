using System.Collections.ObjectModel;
using System.Globalization;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.ChapterEvaluation;

/// <summary>
/// A generated chapter boundary used by the evaluation metrics.
/// </summary>
public sealed record ChapterEvaluationPrediction
{
    public ChapterEvaluationPrediction(
        string id,
        TimeSpan start,
        TimeSpan end,
        IEnumerable<string> sourceSegmentIds)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Prediction ID cannot be empty.", nameof(id));
        }

        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (end <= start)
        {
            throw new ArgumentException(
                "Prediction end must be greater than its start.",
                nameof(end));
        }

        ArgumentNullException.ThrowIfNull(sourceSegmentIds);
        var ids = sourceSegmentIds
            .Select(value => value?.Trim() ?? string.Empty)
            .ToArray();
        if (ids.Length == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Predictions must reference at least one source segment.",
                nameof(sourceSegmentIds));
        }

        Id = id.Trim();
        Start = start;
        End = end;
        SourceSegmentIds = Array.AsReadOnly(ids);
    }

    public string Id { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public IReadOnlyList<string> SourceSegmentIds { get; }
}

/// <summary>
/// Thresholds used to turn evaluation measurements into a deterministic gate.
/// </summary>
public sealed record ChapterEvaluationThresholds
{
    public double MinimumBoundaryF1 { get; init; } = 0.9d;

    public double MinimumCoverage { get; init; } = 1d;

    public double MaximumWindowDiff { get; init; } = 0d;

    public int MaximumDurationConstraintViolations { get; init; }

    public int MaximumSourceSegmentIdMismatches { get; init; }

    public int MaximumTimestampSemanticViolations { get; init; }

    internal void Validate()
    {
        ValidateRatio(MinimumBoundaryF1, nameof(MinimumBoundaryF1));
        ValidateRatio(MinimumCoverage, nameof(MinimumCoverage));
        ValidateRatio(MaximumWindowDiff, nameof(MaximumWindowDiff));
        ValidateNonNegative(
            MaximumDurationConstraintViolations,
            nameof(MaximumDurationConstraintViolations));
        ValidateNonNegative(
            MaximumSourceSegmentIdMismatches,
            nameof(MaximumSourceSegmentIdMismatches));
        ValidateNonNegative(
            MaximumTimestampSemanticViolations,
            nameof(MaximumTimestampSemanticViolations));
    }

    private static void ValidateRatio(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Evaluation ratios must be finite values between zero and one.");
        }
    }

    private static void ValidateNonNegative(int value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                name,
                "Evaluation thresholds cannot be negative.");
        }
    }
}

/// <summary>
/// Options shared by chunk construction and chapter-quality evaluation.
/// </summary>
public sealed record ChapterEvaluationOptions
{
    public TimeSpan BoundaryTolerance { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MinimumChapterDuration { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumChapterDuration { get; init; } = TimeSpan.FromMinutes(5);

    public int? WindowDiffWindowSize { get; init; }

    public ChapterEvaluationThresholds Thresholds { get; init; } = new();

    public TranscriptChunkingOptions ToChunkingOptions() =>
        new()
        {
            MinimumDuration = MinimumChapterDuration,
            MaximumDuration = MaximumChapterDuration
        };

    internal void Validate()
    {
        if (BoundaryTolerance < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BoundaryTolerance));
        }

        if (MinimumChapterDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumChapterDuration));
        }

        if (MaximumChapterDuration < MinimumChapterDuration)
        {
            throw new ArgumentException(
                "Maximum chapter duration cannot be less than the minimum duration.",
                nameof(MaximumChapterDuration));
        }

        if (WindowDiffWindowSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowDiffWindowSize));
        }

        (Thresholds ?? throw new ArgumentNullException(nameof(Thresholds))).Validate();
    }
}

/// <summary>
/// A duration constraint violation for one generated chapter.
/// </summary>
public sealed record DurationConstraintViolation(
    string PredictionId,
    TimeSpan Duration,
    string Constraint);

/// <summary>
/// Measurements for one labeled transcript fixture.
/// </summary>
public sealed class ChapterEvaluationMetrics
{
    internal ChapterEvaluationMetrics(
        int expectedChapterCount,
        int predictedChapterCount,
        int expectedBoundaryCount,
        int predictedBoundaryCount,
        int matchedBoundaryCount,
        TimeSpan boundaryTolerance,
        TimeSpan expectedContentDuration,
        TimeSpan coveredContentDuration,
        IReadOnlyList<DurationConstraintViolation> durationConstraintViolations,
        int windowDiffWindowSize,
        int windowDiffWindowCount,
        int windowDiffMismatches,
        int sourceSegmentIdMismatchCount,
        int timestampSemanticViolationCount)
    {
        ExpectedChapterCount = expectedChapterCount;
        PredictedChapterCount = predictedChapterCount;
        ExpectedBoundaryCount = expectedBoundaryCount;
        PredictedBoundaryCount = predictedBoundaryCount;
        MatchedBoundaryCount = matchedBoundaryCount;
        BoundaryTolerance = boundaryTolerance;
        ExpectedContentDuration = expectedContentDuration;
        CoveredContentDuration = coveredContentDuration;
        DurationConstraintViolations = durationConstraintViolations;
        WindowDiffWindowSize = windowDiffWindowSize;
        WindowDiffWindowCount = windowDiffWindowCount;
        WindowDiffMismatches = windowDiffMismatches;
        SourceSegmentIdMismatchCount = sourceSegmentIdMismatchCount;
        TimestampSemanticViolationCount = timestampSemanticViolationCount;
    }

    public int ExpectedChapterCount { get; }

    public int PredictedChapterCount { get; }

    public int ExpectedBoundaryCount { get; }

    public int PredictedBoundaryCount { get; }

    public int MatchedBoundaryCount { get; }

    public TimeSpan BoundaryTolerance { get; }

    public double BoundaryPrecision =>
        PredictedBoundaryCount == 0
            ? ExpectedBoundaryCount == 0 ? 1d : 0d
            : MatchedBoundaryCount / (double)PredictedBoundaryCount;

    public double BoundaryRecall =>
        ExpectedBoundaryCount == 0
            ? PredictedBoundaryCount == 0 ? 1d : 0d
            : MatchedBoundaryCount / (double)ExpectedBoundaryCount;

    public double BoundaryF1
    {
        get
        {
            var denominator = BoundaryPrecision + BoundaryRecall;
            return denominator <= double.Epsilon
                ? 0d
                : 2d * BoundaryPrecision * BoundaryRecall / denominator;
        }
    }

    public TimeSpan ExpectedContentDuration { get; }

    public TimeSpan CoveredContentDuration { get; }

    /// <summary>
    /// Fraction of source-segment duration represented by generated chapters.
    /// Timing gaps are not content and therefore do not reduce this ratio.
    /// </summary>
    public double CoverageRatio =>
        ExpectedContentDuration <= TimeSpan.Zero
            ? 1d
            : Math.Clamp(
                CoveredContentDuration.TotalSeconds /
                ExpectedContentDuration.TotalSeconds,
                0d,
                1d);

    public IReadOnlyList<DurationConstraintViolation> DurationConstraintViolations { get; }

    public int DurationConstraintViolationCount =>
        DurationConstraintViolations.Count;

    public int WindowDiffWindowSize { get; }

    public int WindowDiffWindowCount { get; }

    public int WindowDiffMismatches { get; }

    /// <summary>
    /// WindowDiff error rate over source-segment windows.
    /// </summary>
    public double WindowDiff =>
        WindowDiffWindowCount == 0
            ? 0d
            : WindowDiffMismatches / (double)WindowDiffWindowCount;

    public int SourceSegmentIdMismatchCount { get; }

    public int TimestampSemanticViolationCount { get; }
}

/// <summary>
/// Evaluates chapter predictions against timestamped labeled fixtures.
/// </summary>
public static class ChapterEvaluationMetricCalculator
{
    public static ChapterEvaluationMetrics Evaluate(
        ChapterEvaluationFixture fixture,
        IEnumerable<ChapterEvaluationPrediction> predictions,
        ChapterEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(predictions);
        options ??= new ChapterEvaluationOptions();
        options.Validate();

        var predictionValues = predictions.ToArray();
        var expectedBoundaries = fixture.ExpectedChapters
            .Skip(1)
            .Select(chapter => chapter.Start)
            .ToArray();
        var predictedBoundaries = predictionValues
            .Skip(1)
            .Select(prediction => prediction.Start)
            .ToArray();
        var matchedBoundaryCount = MatchBoundaries(
            expectedBoundaries,
            predictedBoundaries,
            options.BoundaryTolerance);

        var segmentById = fixture.Transcript.Segments.ToDictionary(
            segment => segment.Id,
            StringComparer.Ordinal);
        var coveredSegmentIds = new HashSet<string>(StringComparer.Ordinal);
        var timestampSemanticViolationCount = 0;
        foreach (var prediction in predictionValues)
        {
            if (!TryGetSourceSegments(
                    prediction,
                    segmentById,
                    out var sourceSegments))
            {
                timestampSemanticViolationCount++;
                continue;
            }

            if (!sourceSegments.SequenceEqual(
                    sourceSegments.OrderBy(segment => segment.OriginalOrdinal)) ||
                prediction.Start != sourceSegments[0].Start ||
                prediction.End != sourceSegments[^1].End)
            {
                timestampSemanticViolationCount++;
            }

            foreach (var segment in sourceSegments)
            {
                coveredSegmentIds.Add(segment.Id);
            }
        }

        var expectedContentDuration = fixture.Transcript.Segments
            .Aggregate(TimeSpan.Zero, (total, segment) => total + segment.End - segment.Start);
        var coveredContentDuration = fixture.Transcript.Segments
            .Where(segment => coveredSegmentIds.Contains(segment.Id))
            .Aggregate(TimeSpan.Zero, (total, segment) => total + segment.End - segment.Start);
        var durationViolations = predictionValues
            .Select(
                prediction =>
                {
                    var duration = prediction.End - prediction.Start;
                    if (duration < options.MinimumChapterDuration)
                    {
                        return new DurationConstraintViolation(
                            prediction.Id,
                            duration,
                            "below-minimum");
                    }

                    if (duration > options.MaximumChapterDuration)
                    {
                        return new DurationConstraintViolation(
                            prediction.Id,
                            duration,
                            "above-maximum");
                    }

                    return null;
                })
            .Where(violation => violation is not null)
            .Cast<DurationConstraintViolation>()
            .ToArray();

        var windowDiff = CalculateWindowDiff(
            fixture,
            predictionValues,
            options.WindowDiffWindowSize);
        var sourceSegmentIdMismatchCount = CountSourceSegmentIdMismatches(
            fixture.ExpectedChapters,
            predictionValues);

        return new ChapterEvaluationMetrics(
            fixture.ExpectedChapters.Count,
            predictionValues.Length,
            expectedBoundaries.Length,
            predictedBoundaries.Length,
            matchedBoundaryCount,
            options.BoundaryTolerance,
            expectedContentDuration,
            coveredContentDuration,
            new ReadOnlyCollection<DurationConstraintViolation>(durationViolations),
            windowDiff.WindowSize,
            windowDiff.WindowCount,
            windowDiff.MismatchCount,
            sourceSegmentIdMismatchCount,
            timestampSemanticViolationCount);
    }

    private static bool TryGetSourceSegments(
        ChapterEvaluationPrediction prediction,
        IReadOnlyDictionary<string, TranscriptSegment> segmentById,
        out TranscriptSegment[] sourceSegments)
    {
        var values = new List<TranscriptSegment>(prediction.SourceSegmentIds.Count);
        foreach (var segmentId in prediction.SourceSegmentIds)
        {
            if (!segmentById.TryGetValue(segmentId, out var segment) ||
                values.Any(existing => existing.Id == segment.Id))
            {
                sourceSegments = [];
                return false;
            }

            values.Add(segment);
        }

        sourceSegments = values.ToArray();
        return true;
    }

    /// <summary>
    /// Matches sorted boundary sequences in order using the earliest feasible
    /// pair. For one-dimensional ordered boundaries with a symmetric tolerance,
    /// consuming the earliest feasible pair is maximum-cardinality: skipping it
    /// cannot make a later expected or predicted boundary more matchable.
    /// </summary>
    private static int MatchBoundaries(
        IReadOnlyList<TimeSpan> expected,
        IReadOnlyList<TimeSpan> predicted,
        TimeSpan tolerance)
    {
        var orderedExpected = expected
            .OrderBy(value => value)
            .ToArray();
        var orderedPredicted = predicted
            .OrderBy(value => value)
            .ToArray();
        var expectedIndex = 0;
        var predictedIndex = 0;
        var count = 0;
        while (expectedIndex < orderedExpected.Length &&
               predictedIndex < orderedPredicted.Length)
        {
            var difference =
                orderedPredicted[predictedIndex] - orderedExpected[expectedIndex];
            if (difference < -tolerance)
            {
                predictedIndex++;
                continue;
            }

            if (difference > tolerance)
            {
                expectedIndex++;
                continue;
            }

            count++;
            expectedIndex++;
            predictedIndex++;
        }

        return count;
    }

    private static int CountSourceSegmentIdMismatches(
        IReadOnlyList<ExpectedChapterBoundary> expected,
        IReadOnlyList<ChapterEvaluationPrediction> predicted)
    {
        var count = 0;
        var max = Math.Max(expected.Count, predicted.Count);
        for (var index = 0; index < max; index++)
        {
            if (index >= expected.Count ||
                index >= predicted.Count ||
                !expected[index].SourceSegmentIds.SequenceEqual(
                    predicted[index].SourceSegmentIds,
                    StringComparer.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static WindowDiffResult CalculateWindowDiff(
        ChapterEvaluationFixture fixture,
        IReadOnlyList<ChapterEvaluationPrediction> predictions,
        int? requestedWindowSize)
    {
        var segmentCount = fixture.Transcript.Segments.Count;
        if (segmentCount <= 1)
        {
            return new WindowDiffResult(1, 0, 0);
        }

        var windowSize = requestedWindowSize
            ?? Math.Max(
                2,
                (int)Math.Round(
                    segmentCount /
                    (2d * Math.Max(1, fixture.ExpectedChapters.Count)),
                    MidpointRounding.AwayFromZero));
        windowSize = Math.Clamp(windowSize, 2, segmentCount);

        var expectedLabels = ChapterLabels(
            fixture.Transcript.Segments,
            fixture.ExpectedChapters.Select(
                chapter => chapter.SourceSegmentIds));
        var predictedLabels = ChapterLabels(
            fixture.Transcript.Segments,
            predictions.Select(prediction => prediction.SourceSegmentIds));
        var expectedBoundaries = BoundaryFlags(expectedLabels);
        var predictedBoundaries = BoundaryFlags(predictedLabels);
        var windowCount = segmentCount - windowSize + 1;
        var mismatches = 0;

        for (var start = 0; start < windowCount; start++)
        {
            var expectedBoundaryCount = CountBoundaries(
                expectedBoundaries,
                start,
                windowSize - 1);
            var predictedBoundaryCount = CountBoundaries(
                predictedBoundaries,
                start,
                windowSize - 1);
            if (expectedBoundaryCount != predictedBoundaryCount)
            {
                mismatches++;
            }
        }

        return new WindowDiffResult(windowSize, windowCount, mismatches);
    }

    private static int[] ChapterLabels(
        IReadOnlyList<TranscriptSegment> segments,
        IEnumerable<IReadOnlyList<string>> chapterIds)
    {
        var labels = Enumerable.Repeat(-1, segments.Count).ToArray();
        var segmentIndexes = segments
            .Select((segment, index) => (segment.Id, index))
            .ToDictionary(value => value.Id, value => value.index, StringComparer.Ordinal);
        var chapterIndex = 0;
        foreach (var ids in chapterIds)
        {
            foreach (var id in ids)
            {
                if (segmentIndexes.TryGetValue(id, out var segmentIndex))
                {
                    labels[segmentIndex] = chapterIndex;
                }
            }

            chapterIndex++;
        }

        return labels;
    }

    private static bool[] BoundaryFlags(IReadOnlyList<int> labels)
    {
        var boundaries = new bool[Math.Max(0, labels.Count - 1)];
        for (var index = 0; index < boundaries.Length; index++)
        {
            boundaries[index] = labels[index] != labels[index + 1];
        }

        return boundaries;
    }

    private static int CountBoundaries(
        IReadOnlyList<bool> boundaries,
        int start,
        int length)
    {
        var count = 0;
        for (var index = start; index < start + length; index++)
        {
            if (boundaries[index])
            {
                count++;
            }
        }

        return count;
    }

    private readonly record struct WindowDiffResult(
        int WindowSize,
        int WindowCount,
        int MismatchCount);
}
