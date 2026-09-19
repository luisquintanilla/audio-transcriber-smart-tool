using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.ChapterEvaluation;

/// <summary>
/// Runs deterministic chapter-quality evaluations over labeled fixtures.
/// </summary>
public sealed class ChapterEvaluationHarness
{
    private readonly IEmbeddingGenerator<TextContent, Embedding<float>> embeddingGenerator;
    private readonly ITranscriptChunkScoringProvider scoringProvider;

    public ChapterEvaluationHarness(
        IEmbeddingGenerator<TextContent, Embedding<float>>? embeddingGenerator = null,
        ITranscriptChunkScoringProvider? scoringProvider = null)
    {
        this.embeddingGenerator = embeddingGenerator
            ?? new DeterministicTranscriptEmbeddingGenerator();
        this.scoringProvider = scoringProvider
            ?? new DeterministicTranscriptChunkScoringProvider();
    }

    public async Task<ChapterEvaluationReport> EvaluateAsync(
        IEnumerable<ChapterEvaluationFixture> fixtures,
        ChapterEvaluationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        options ??= new ChapterEvaluationOptions();
        options.Validate();

        var fixtureValues = fixtures
            .Select(fixture => fixture ?? throw new ArgumentException(
                "Fixtures cannot contain null entries.",
                nameof(fixtures)))
            .OrderBy(fixture => fixture.Id, StringComparer.Ordinal)
            .ToArray();
        if (fixtureValues.Length == 0)
        {
            throw new ArgumentException(
                "At least one fixture is required.",
                nameof(fixtures));
        }

        var results = new List<ChapterEvaluationCaseResult>(fixtureValues.Length);
        var generator = new TranscriptChapterArtifactGenerator();
        var chunkBuilder = new TranscriptChunkBuilder(
            embeddingGenerator,
            scoringProvider);
        foreach (var fixture in fixtureValues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ingestionDocument = TranscriptIngestionAdapter.ToIngestionDocument(
                fixture.Transcript);
            var chunkResult = await chunkBuilder
                .BuildAsync(
                    ingestionDocument,
                    options.ToChunkingOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            var artifactDocument = generator.Generate(chunkResult);
            var predictions = artifactDocument
                .Select(
                    chapter => new ChapterEvaluationPrediction(
                        chapter.Id,
                        chapter.Start,
                        chapter.End,
                        chapter.SourceSegmentIds))
                .ToArray();
            var metrics = ChapterEvaluationMetricCalculator.Evaluate(
                fixture,
                predictions,
                options);
            results.Add(
                new ChapterEvaluationCaseResult(
                    fixture.Id,
                    fixture.SchemaVersion,
                    metrics,
                    CreateFailures(fixture.Id, metrics, options.Thresholds)));
        }

        return new ChapterEvaluationReport(
            ChapterEvaluationReport.CurrentSchemaVersion,
            options,
            results);
    }

    private static IReadOnlyList<string> CreateFailures(
        string fixtureId,
        ChapterEvaluationMetrics metrics,
        ChapterEvaluationThresholds thresholds)
    {
        var failures = new List<string>();
        if (metrics.BoundaryF1 < thresholds.MinimumBoundaryF1)
        {
            failures.Add(
                $"{fixtureId}: boundary F1 {metrics.BoundaryF1:0.000} " +
                $"is below {thresholds.MinimumBoundaryF1:0.000}.");
        }

        if (metrics.CoverageRatio < thresholds.MinimumCoverage)
        {
            failures.Add(
                $"{fixtureId}: coverage {metrics.CoverageRatio:0.000} " +
                $"is below {thresholds.MinimumCoverage:0.000}.");
        }

        if (metrics.WindowDiff > thresholds.MaximumWindowDiff)
        {
            failures.Add(
                $"{fixtureId}: WindowDiff {metrics.WindowDiff:0.000} " +
                $"exceeds {thresholds.MaximumWindowDiff:0.000}.");
        }

        if (metrics.DurationConstraintViolationCount >
            thresholds.MaximumDurationConstraintViolations)
        {
            failures.Add(
                $"{fixtureId}: {metrics.DurationConstraintViolationCount} duration " +
                "constraint violation(s).");
        }

        if (metrics.SourceSegmentIdMismatchCount >
            thresholds.MaximumSourceSegmentIdMismatches)
        {
            failures.Add(
                $"{fixtureId}: {metrics.SourceSegmentIdMismatchCount} source segment " +
                "ID mismatch(es).");
        }

        if (metrics.TimestampSemanticViolationCount >
            thresholds.MaximumTimestampSemanticViolations)
        {
            failures.Add(
                $"{fixtureId}: {metrics.TimestampSemanticViolationCount} timestamp " +
                "semantic violation(s).");
        }

        return failures.AsReadOnly();
    }
}

/// <summary>
/// Per-fixture evaluation output.
/// </summary>
public sealed class ChapterEvaluationCaseResult
{
    internal ChapterEvaluationCaseResult(
        string fixtureId,
        string fixtureSchemaVersion,
        ChapterEvaluationMetrics metrics,
        IReadOnlyList<string> failures)
    {
        FixtureId = fixtureId;
        FixtureSchemaVersion = fixtureSchemaVersion;
        Metrics = metrics;
        Failures = failures;
    }

    public string FixtureId { get; }

    public string FixtureSchemaVersion { get; }

    public ChapterEvaluationMetrics Metrics { get; }

    public IReadOnlyList<string> Failures { get; }

    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Deterministic report suitable for local checks and future release gates.
/// </summary>
public sealed class ChapterEvaluationReport
{
    public const string CurrentSchemaVersion = "1.0";

    internal ChapterEvaluationReport(
        string schemaVersion,
        ChapterEvaluationOptions options,
        IEnumerable<ChapterEvaluationCaseResult> cases)
    {
        SchemaVersion = schemaVersion;
        BoundaryTolerance = options.BoundaryTolerance;
        Thresholds = options.Thresholds;
        Cases = Array.AsReadOnly(cases.ToArray());
    }

    public string SchemaVersion { get; }

    public TimeSpan BoundaryTolerance { get; }

    public ChapterEvaluationThresholds Thresholds { get; }

    public IReadOnlyList<ChapterEvaluationCaseResult> Cases { get; }

    public IReadOnlyList<string> Failures =>
        Cases.SelectMany(result => result.Failures).ToArray();

    public bool Passed => Cases.All(result => result.Passed);

    public string ToJson()
    {
        return JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            }) + Environment.NewLine;
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Chapter evaluation report v{SchemaVersion}");
        builder.AppendLine($"Status: {(Passed ? "PASS" : "FAIL")}");
        builder.AppendLine(
            $"Boundary tolerance: {BoundaryTolerance.ToString("c", null)}");
        builder.AppendLine(
            "Fixture | Boundary P/R/F1 | Coverage | WindowDiff | " +
            "Duration violations | Source ID mismatches | Timestamp violations");
        foreach (var result in Cases)
        {
            var metrics = result.Metrics;
            builder.AppendLine(
                string.Join(
                    " | ",
                    result.FixtureId,
                    $"{metrics.BoundaryPrecision:0.000}/" +
                    $"{metrics.BoundaryRecall:0.000}/" +
                    $"{metrics.BoundaryF1:0.000}",
                    $"{metrics.CoverageRatio:0.000}",
                    $"{metrics.WindowDiff:0.000}",
                    metrics.DurationConstraintViolationCount,
                    metrics.SourceSegmentIdMismatchCount,
                    metrics.TimestampSemanticViolationCount));
        }

        if (!Passed)
        {
            builder.AppendLine("Failures:");
            foreach (var failure in Failures)
            {
                builder.AppendLine($"- {failure}");
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// A local deterministic embedding provider for evaluation runs.
/// </summary>
public sealed class DeterministicTranscriptEmbeddingGenerator
    : IEmbeddingGenerator<TextContent, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<TextContent> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values.ToArray();
        var embeddings = new GeneratedEmbeddings<Embedding<float>>(inputs.Length);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            cancellationToken.ThrowIfCancellationRequested();
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.Text));
            var vector = Enumerable.Range(0, 8)
                .Select(index => ((hash[index] / 255f) * 2f) - 1f)
                .ToArray();
            embeddings.Add(new Embedding<float>(vector));
        }

        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>
/// A local deterministic score provider for evaluation runs.
/// </summary>
public sealed class DeterministicTranscriptChunkScoringProvider
    : ITranscriptChunkScoringProvider
{
    public ValueTask<double> ScoreAsync(
        TranscriptChunkScoringRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text));
        var score = hash[0] / 255d;
        return ValueTask.FromResult(score);
    }
}
