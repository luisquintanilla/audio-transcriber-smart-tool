using AudioTranscriber.ChapterEvaluation;

namespace AudioTranscriber.ChapterEvaluation.Tests;

public sealed class ChapterEvaluationTests
{
    [Fact]
    public void LabeledFixtures_PreserveTimestampSemanticsAndSourceIds()
    {
        var fixture = LoadFixture("product-launch.json");

        Assert.Equal("1.0", fixture.SchemaVersion);
        Assert.Equal(
            new[] { "seg-001", "seg-002", "seg-003", "seg-004", "seg-005", "seg-006" },
            fixture.Transcript.Segments.Select(segment => segment.Id));
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            fixture.ExpectedChapters[1].Start);
        Assert.Equal(
            new[] { "seg-003", "seg-004" },
            fixture.ExpectedChapters[1].SourceSegmentIds);
    }

    [Fact]
    public async Task Harness_DefaultsToDeterministicProvidersAndMatchesFixtures()
    {
        var fixtures = LoadFixtures();
        var options = new ChapterEvaluationOptions
        {
            MinimumChapterDuration = TimeSpan.FromSeconds(1),
            MaximumChapterDuration = TimeSpan.FromSeconds(10),
            BoundaryTolerance = TimeSpan.FromSeconds(2),
            Thresholds = new ChapterEvaluationThresholds
            {
                MinimumBoundaryF1 = 1d,
                MinimumCoverage = 1d,
                MaximumWindowDiff = 0d
            }
        };

        var report = await new ChapterEvaluationHarness()
            .EvaluateAsync(fixtures, options);

        Assert.True(report.Passed, report.ToText());
        Assert.Equal(new[] { "product-launch", "support-session" }, report.Cases.Select(result => result.FixtureId));
        Assert.All(
            report.Cases,
            result =>
            {
                Assert.Equal(1d, result.Metrics.BoundaryF1);
                Assert.Equal(1d, result.Metrics.CoverageRatio);
                Assert.Equal(0d, result.Metrics.WindowDiff);
                Assert.Equal(0, result.Metrics.SourceSegmentIdMismatchCount);
                Assert.Equal(0, result.Metrics.TimestampSemanticViolationCount);
            });
    }

    [Fact]
    public async Task Harness_ReportIsStableAcrossRepeatedRuns()
    {
        var fixtures = LoadFixtures();
        var options = new ChapterEvaluationOptions
        {
            MaximumChapterDuration = TimeSpan.FromSeconds(10)
        };
        var harness = new ChapterEvaluationHarness();

        var first = await harness.EvaluateAsync(fixtures, options);
        var second = await harness.EvaluateAsync(fixtures, options);

        Assert.Equal(first.ToJson(), second.ToJson());
        Assert.Equal(first.ToText(), second.ToText());
    }

    [Fact]
    public void BoundaryMetrics_UseDocumentedTwoSecondTolerance()
    {
        var fixture = LoadFixture("product-launch.json");
        var predictions = fixture.ExpectedChapters
            .Select(
                (chapter, index) =>
                    new ChapterEvaluationPrediction(
                        $"prediction-{index}",
                        chapter.Start + (index == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(1.5)),
                        chapter.End + (index == 2 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero),
                        chapter.SourceSegmentIds))
            .ToArray();

        var metrics = ChapterEvaluationMetricCalculator.Evaluate(
            fixture,
            predictions,
            new ChapterEvaluationOptions
            {
                BoundaryTolerance = TimeSpan.FromSeconds(2)
            });

        Assert.Equal(2, metrics.ExpectedBoundaryCount);
        Assert.Equal(2, metrics.PredictedBoundaryCount);
        Assert.Equal(2, metrics.MatchedBoundaryCount);
        Assert.Equal(1d, metrics.BoundaryPrecision);
        Assert.Equal(1d, metrics.BoundaryRecall);
        Assert.Equal(1d, metrics.BoundaryF1);
    }

    [Fact]
    public void BoundaryMetrics_RejectBoundaryOutsideTolerance()
    {
        var fixture = LoadFixture("product-launch.json");
        var predictions = fixture.ExpectedChapters
            .Select(
                (chapter, index) =>
                    new ChapterEvaluationPrediction(
                        $"prediction-{index}",
                        chapter.Start + (index == 1 ? TimeSpan.FromSeconds(3) : TimeSpan.Zero),
                        chapter.End,
                        chapter.SourceSegmentIds))
            .ToArray();

        var metrics = ChapterEvaluationMetricCalculator.Evaluate(
            fixture,
            predictions,
            new ChapterEvaluationOptions
            {
                BoundaryTolerance = TimeSpan.FromSeconds(2)
            });

        Assert.Equal(1, metrics.MatchedBoundaryCount);
        Assert.Equal(0.5d, metrics.BoundaryPrecision);
        Assert.Equal(0.5d, metrics.BoundaryRecall);
        Assert.Equal(0.5d, metrics.BoundaryF1);
    }

    [Fact]
    public void CoverageAndConstraints_ReportMissingSegmentsAndInvalidDurations()
    {
        var fixture = LoadFixture("product-launch.json");
        var predictions = new[]
        {
            new ChapterEvaluationPrediction(
                "short",
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(500),
                new[] { "seg-001" }),
            new ChapterEvaluationPrediction(
                "long",
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(40),
                new[] { "seg-002" })
        };

        var metrics = ChapterEvaluationMetricCalculator.Evaluate(
            fixture,
            predictions,
            new ChapterEvaluationOptions
            {
                MinimumChapterDuration = TimeSpan.FromSeconds(1),
                MaximumChapterDuration = TimeSpan.FromSeconds(10)
            });

        Assert.Equal(10d, metrics.CoveredContentDuration.TotalSeconds);
        Assert.Equal(30d, metrics.ExpectedContentDuration.TotalSeconds);
        Assert.Equal(1d / 3d, metrics.CoverageRatio, 6);
        Assert.Equal(2, metrics.DurationConstraintViolationCount);
        Assert.Equal(
            new[] { "below-minimum", "above-maximum" },
            metrics.DurationConstraintViolations.Select(violation => violation.Constraint));
    }

    [Fact]
    public void WindowDiff_ReportsSegmentationErrorOverSourceSegmentWindows()
    {
        var fixture = LoadFixture("product-launch.json");
        var predictions = new[]
        {
            new ChapterEvaluationPrediction(
                "all-segments",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(30),
                fixture.Transcript.Segments.Select(segment => segment.Id))
        };

        var metrics = ChapterEvaluationMetricCalculator.Evaluate(
            fixture,
            predictions,
            new ChapterEvaluationOptions
            {
                MaximumChapterDuration = TimeSpan.FromSeconds(60)
            });

        Assert.Equal(2, metrics.WindowDiffWindowSize);
        Assert.Equal(5, metrics.WindowDiffWindowCount);
        Assert.Equal(2, metrics.WindowDiffMismatches);
        Assert.Equal(0.4d, metrics.WindowDiff);
    }

    [Fact]
    public void Metrics_ReportSourceIdAndTimestampViolations()
    {
        var fixture = LoadFixture("product-launch.json");
        var predictions = new[]
        {
            new ChapterEvaluationPrediction(
                "wrong-source",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(9),
                new[] { "seg-002", "seg-001" }),
            new ChapterEvaluationPrediction(
                "wrong-source-2",
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(20),
                new[] { "seg-003", "seg-004" }),
            new ChapterEvaluationPrediction(
                "wrong-source-3",
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30),
                new[] { "seg-005", "seg-006" })
        };

        var metrics = ChapterEvaluationMetricCalculator.Evaluate(fixture, predictions);

        Assert.Equal(1, metrics.SourceSegmentIdMismatchCount);
        Assert.Equal(1, metrics.TimestampSemanticViolationCount);
    }

    [Fact]
    public async Task Report_FormatsGateFailuresClearly()
    {
        var fixture = LoadFixture("product-launch.json");
        var report = await new ChapterEvaluationHarness()
            .EvaluateAsync(
                new[] { fixture },
                new ChapterEvaluationOptions
                {
                    MaximumChapterDuration = TimeSpan.FromSeconds(60),
                    Thresholds = new ChapterEvaluationThresholds
                    {
                        MinimumBoundaryF1 = 1d,
                        MaximumWindowDiff = 0d
                    }
                });

        Assert.False(report.Passed);
        Assert.Contains("Status: FAIL", report.ToText(), StringComparison.Ordinal);
        Assert.Contains(
            "boundary F1",
            string.Join(Environment.NewLine, report.Failures),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"schemaVersion\": \"1.0\"",
            report.ToJson(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FixtureLoader_RejectsUnsupportedSchemaVersion()
    {
        using var stream = new MemoryStream(
            """
            {
              "schemaVersion": "2.0",
              "id": "invalid",
              "source": "invalid.wav",
              "provenance": { "provider": "test", "model": "test" },
              "segments": [],
              "expectedChapters": []
            }
            """u8.ToArray());

        var exception = Assert.Throws<ArgumentException>(
            () => ChapterEvaluationFixtureLoader.Load(stream));

        Assert.Contains("Unsupported chapter evaluation schema version", exception.Message, StringComparison.Ordinal);
    }

    private static ChapterEvaluationFixture[] LoadFixtures()
    {
        return new[]
        {
            LoadFixture("product-launch.json"),
            LoadFixture("support-session.json")
        };
    }

    private static ChapterEvaluationFixture LoadFixture(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            name);
        return ChapterEvaluationFixtureLoader.Load(path);
    }
}
