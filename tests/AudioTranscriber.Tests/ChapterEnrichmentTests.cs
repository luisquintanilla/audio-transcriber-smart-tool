using System.Text.Json;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class ChapterEnrichmentTests
{
    [Fact]
    public async Task Enrichment_serialization_preserves_source_evidence_and_provider_metadata()
    {
        var artifact = CreateArtifact(
            Segment(
                "opening source",
                0,
                1,
                0,
                "segment-opening",
                "source-opening",
                new Dictionary<string, string> { ["speaker"] = "host" }),
            Segment("closing source", 1, 2, 1, "segment-closing", "source-closing"));
        var enricher = new ScriptedEnricher(
            (chapter, _) => new Processing.TranscriptChapterSummary(
                $"Summary for {chapter.Title}.",
                ["zeta", "alpha"],
                "Enriched title",
                chapter.SourceSegments.Select(
                    segment => new Processing.TranscriptChapterEvidenceReference(
                        segment.Id,
                        segment.Start,
                        segment.End,
                        segment.SourceId))));
        var assembler = new RecordingAssembler(
            request => new Processing.TranscriptOverallSummary(
                string.Join(
                    " | ",
                    request.ChapterSummaries.Select(item => item.Summary.Summary)),
                request.ChapterSummaries.Select(item => item.ChapterId),
                request.IsPartial));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                enricher,
                assembler)
            .EnrichAsync(
                artifact,
                new Processing.TranscriptChapterEnrichmentOptions
                {
                    Provider = "fake-provider",
                    Model = "fake-model",
                    ProviderConfiguration = new Dictionary<string, string>
                    {
                        ["temperature"] = "0"
                    },
                    IncludeOverallSummary = true
                });

        using var json = JsonDocument.Parse(
            new Processing.TranscriptChapterEnrichmentArtifactSerializer()
                .Serialize(document));
        var root = json.RootElement;
        Assert.Equal("1.0", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("1.0", root.GetProperty("chapterArtifactSchemaVersion").GetString());
        Assert.Equal("fixture.wav", root.GetProperty("source").GetString());
        Assert.Equal(
            "fixture-provider",
            root.GetProperty("provenance").GetProperty("provider").GetString());
        Assert.Equal(
            "fixture-model",
            root.GetProperty("provenance").GetProperty("model").GetString());
        Assert.Equal(
            "fake-provider",
            root.GetProperty("generation").GetProperty("provider").GetString());
        Assert.Equal(
            "fake-model",
            root.GetProperty("generation").GetProperty("model").GetString());
        Assert.Equal(
            "0",
            root.GetProperty("generation")
                .GetProperty("configuration")
                .GetProperty("temperature")
                .GetString());

        var chapters = root.GetProperty("chapters").EnumerateArray().ToArray();
        Assert.Equal(
            artifact.Select(chapter => chapter.Id),
            chapters.Select(chapter => chapter.GetProperty("chapterId").GetString()!));
        Assert.Equal(
            ["segment-opening", "segment-closing"],
            chapters.SelectMany(
                    chapter => chapter.GetProperty("sourceSegmentIds")
                        .EnumerateArray()
                        .Select(value => value.GetString()!))
                .ToArray());
        Assert.Equal("00:00:00.0000000", chapters[0].GetProperty("start").GetString());
        Assert.Equal("00:00:01.0000000", chapters[0].GetProperty("end").GetString());
        Assert.Equal(
            ["alpha", "zeta"],
            chapters[0].GetProperty("keywords")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            "segment-opening",
            chapters[0].GetProperty("evidence")[0]
                .GetProperty("sourceSegmentId")
                .GetString());
        Assert.Equal(
            "source-opening",
            chapters[0].GetProperty("evidence")[0]
                .GetProperty("sourceId")
                .GetString());
        Assert.Equal(
            "00:00:00.0000000",
            chapters[0].GetProperty("evidence")[0].GetProperty("start").GetString());
        Assert.Equal(
            "opening source",
            artifact[0].SourceSegments[0].Text);
        Assert.Equal(
            "Summary for Chapter 1.",
            root.GetProperty("overallSummary").GetProperty("summary").GetString()!
                .Split(" | ", StringSplitOptions.None)[0]);
        Assert.False(root.GetProperty("partial").GetBoolean());
        Assert.DoesNotContain(
            "\"summary\"",
            new Processing.TranscriptChapterArtifactGenerator().Serialize(
                artifact),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enrichment_is_stable_and_overall_summary_receives_chapter_summaries_in_order()
    {
        var artifact = CreateArtifact(
            Segment("first", 0, 1, 0, "segment-first"),
            Segment("second", 1, 2, 1, "segment-second"));
        var assembler = new RecordingAssembler(
            request => new Processing.TranscriptOverallSummary(
                string.Join(
                    " + ",
                    request.ChapterSummaries.Select(item => item.Summary.Summary)),
                request.ChapterSummaries.Select(item => item.ChapterId),
                request.IsPartial));
        var options = new Processing.TranscriptChapterEnrichmentOptions
        {
            Provider = "deterministic-fake",
            ProviderConfiguration = new Dictionary<string, string>
            {
                ["b"] = "2",
                ["a"] = "1"
            },
            IncludeOverallSummary = true
        };

        var first = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (chapter, _) => new Processing.TranscriptChapterSummary(
                        $"summary-{chapter.ChapterNumber()}",
                        ["topic"])),
                assembler)
            .EnrichAsync(artifact, options);
        var second = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (chapter, _) => new Processing.TranscriptChapterSummary(
                        $"summary-{chapter.ChapterNumber()}",
                        ["topic"])),
                new RecordingAssembler(
                    request => new Processing.TranscriptOverallSummary(
                        string.Join(
                            " + ",
                            request.ChapterSummaries.Select(item => item.Summary.Summary)),
                        request.ChapterSummaries.Select(item => item.ChapterId),
                        request.IsPartial)))
            .EnrichAsync(artifact, options);

        var serializer = new Processing.TranscriptChapterEnrichmentArtifactSerializer();
        Assert.Equal(serializer.Serialize(first), serializer.Serialize(second));
        Assert.Equal(
            ["summary-1", "summary-2"],
            assembler.LastRequest!.ChapterSummaries
                .Select(item => item.Summary.Summary)
                .ToArray());
        Assert.Equal(
            first.Chapters.Select(chapter => chapter.ChapterId),
            assembler.LastRequest.ChapterSummaries.Select(item => item.ChapterId));
        Assert.Equal(
            "summary-1 + summary-2",
            first.OverallSummary!.Summary);
    }

    [Fact]
    public async Task Options_reject_provider_configuration_keys_that_collide_after_trimming()
    {
        var artifact = CreateArtifact(
            Segment("configuration", 0, 1, 0, "segment-configuration"));
        var enricher = new ScriptedEnricher(
            (_, _) => new Processing.TranscriptChapterSummary("unreachable"));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => new Processing.TranscriptChapterEnrichmentOrchestrator(enricher)
                .EnrichAsync(
                    artifact,
                    new Processing.TranscriptChapterEnrichmentOptions
                    {
                        ProviderConfiguration = new Dictionary<string, string>
                        {
                            ["duplicate"] = "first",
                            [" duplicate "] = "second"
                        }
                    }));

        Assert.Equal("ProviderConfiguration", exception.ParamName);
        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "' duplicate '",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, enricher.CallCount);

        var metadataException = Assert.Throws<ArgumentException>(
            () => new Processing.TranscriptChapterEnrichmentGenerationMetadata(
                "algorithm",
                "provider",
                configuration: new Dictionary<string, string>
                {
                    ["duplicate"] = "first",
                    [" duplicate "] = "second"
                }));
        Assert.Contains(
            "Duplicate enrichment configuration key 'duplicate'.",
            metadataException.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "' duplicate '",
            metadataException.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservePartial_records_missing_and_provider_failures_without_fallback()
    {
        var artifact = CreateArtifact(
            Segment("missing", 0, 1, 0, "segment-missing"),
            Segment("failed", 1, 2, 1, "segment-failed"),
            Segment("successful", 2, 3, 2, "segment-successful"));
        var assembler = new RecordingAssembler(
            request => new Processing.TranscriptOverallSummary(
                "partial overall",
                request.ChapterSummaries.Select(item => item.ChapterId),
                request.IsPartial));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (chapter, call) => call switch
                    {
                        1 => null,
                        2 => throw new InvalidOperationException("provider detail"),
                        _ => new Processing.TranscriptChapterSummary("successful summary")
                    }),
                assembler)
            .EnrichAsync(
                artifact,
                new Processing.TranscriptChapterEnrichmentOptions
                {
                    FailurePolicy =
                        Processing.TranscriptChapterEnrichmentFailurePolicy.PreservePartial,
                    IncludeOverallSummary = true
                });

        Assert.Equal(
            [
                Processing.TranscriptChapterEnrichmentStatus.Missing,
                Processing.TranscriptChapterEnrichmentStatus.Failed,
                Processing.TranscriptChapterEnrichmentStatus.Succeeded
            ],
            document.Select(chapter => chapter.Status));
        Assert.Equal(
            Processing.TranscriptChapterEnrichmentStatus.Missing,
            document[0].Status);
        Assert.Null(document[0].Failure);
        Assert.Equal("provider_failure", document[1].Failure!.Code);
        Assert.Null(document[0].Summary);
        Assert.Null(document[1].Summary);
        Assert.Equal("successful summary", document[2].Summary);
        Assert.True(document.IsPartial);
        Assert.True(document.OverallSummary!.IsPartial);
        Assert.Equal([document[2].ChapterId], document.OverallSummary.ChapterIds);
        Assert.Equal(
            [document[2].ChapterId],
            assembler.LastRequest!.ChapterSummaries.Select(item => item.ChapterId));

        using var json = JsonDocument.Parse(
            new Processing.TranscriptChapterEnrichmentArtifactSerializer()
                .Serialize(document));
        Assert.True(json.RootElement.GetProperty("partial").GetBoolean());
        Assert.Equal(
            ["missing", "failed", "succeeded"],
            json.RootElement.GetProperty("chapters")
                .EnumerateArray()
                .Select(chapter => chapter.GetProperty("status").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task PreservePartial_records_missing_overall_summary_when_all_chapters_are_missing_or_failed()
    {
        var artifact = CreateArtifact(
                Segment("missing", 0, 1, 0, "segment-missing"),
                Segment("failed", 1, 2, 1, "segment-failed"));
        var assembler = new RecordingAssembler(
                _ => throw new InvalidOperationException("assembler should not run"));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                    new ScriptedEnricher(
                        (_, call) => call == 1
                            ? null
                            : throw new InvalidOperationException("provider detail")),
                    assembler)
                .EnrichAsync(
                    artifact,
                    new Processing.TranscriptChapterEnrichmentOptions
                    {
                        IncludeOverallSummary = true,
                        FailurePolicy =
                            Processing.TranscriptChapterEnrichmentFailurePolicy.PreservePartial
                    });

        Assert.Equal(
                [
                    Processing.TranscriptChapterEnrichmentStatus.Missing,
                    Processing.TranscriptChapterEnrichmentStatus.Failed
                ],
                document.Select(chapter => chapter.Status));
        Assert.Null(document.OverallSummary);
        Assert.Equal(
                "missing_overall_summary",
                document.OverallSummaryFailure!.Code);
        Assert.Null(assembler.LastRequest);
    }

    [Fact]
    public async Task FailFast_stops_on_provider_failure_with_stable_exception_metadata()
    {
        var artifact = CreateArtifact(
            Segment("first", 0, 1, 0, "segment-first"),
            Segment("second", 1, 2, 1, "segment-second"));
        var enricher = new ScriptedEnricher(
            (_, call) => call == 2
                ? throw new InvalidOperationException("provider detail")
                : new Processing.TranscriptChapterSummary("first summary"));

        var exception = await Assert.ThrowsAsync<Processing.TranscriptChapterEnrichmentException>(
            () => new Processing.TranscriptChapterEnrichmentOrchestrator(enricher)
                .EnrichAsync(artifact));

        Assert.Equal("chapter-0002-window-989680-1312d00-segment-second", exception.ChapterId);
        Assert.Equal("provider_failure", exception.Code);
        Assert.Equal(2, enricher.CallCount);
        Assert.DoesNotContain("provider detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_during_enrichment_is_propagated_without_fallback()
    {
        var artifact = CreateArtifact(Segment("blocking", 0, 1, 0, "segment-blocking"));
        using var cancellation = new CancellationTokenSource();
        var enricher = new BlockingEnricher();
        var task = new Processing.TranscriptChapterEnrichmentOrchestrator(enricher)
            .EnrichAsync(artifact, cancellationToken: cancellation.Token);

        await enricher.Started.Task;
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Enrichment_writer_refuses_overwrite_and_cleans_partial_files()
    {
        using var fixture = new TemporaryFixture();
        var artifact = CreateArtifact(Segment("atomic", 0, 1, 0, "segment-atomic"));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (_, _) => new Processing.TranscriptChapterSummary("atomic summary")))
            .EnrichAsync(artifact);
        var writer = new Processing.TranscriptChapterEnrichmentArtifactFileWriter();
        var output = fixture.Path("enrichment.json");

        await writer.WriteAsync(output, document);
        var original = File.ReadAllText(output);
        await Assert.ThrowsAsync<IOException>(
            () => writer.WriteAsync(output, document));
        Assert.Equal(original, File.ReadAllText(output));
        await writer.WriteAsync(output, document, overwrite: true);
        Assert.Equal(original, File.ReadAllText(output));

        var destinationDirectory = fixture.Path("destination");
        Directory.CreateDirectory(destinationDirectory);
        await Assert.ThrowsAsync<IOException>(
            () => writer.WriteAsync(destinationDirectory, document));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.partial"));
    }

    [Fact]
    public async Task Enrichment_writer_serializes_valid_utf8_without_full_byte_array_duplication()
    {
        using var fixture = new TemporaryFixture();
        var artifact = CreateArtifact(
            Segment("utf8 output", 0, 1, 0, "segment-utf8"));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (_, _) => new Processing.TranscriptChapterSummary("summary \u00e9")))
            .EnrichAsync(artifact);
        var output = fixture.Path("utf8-enrichment.json");

        await new Processing.TranscriptChapterEnrichmentArtifactFileWriter()
            .WriteAsync(output, document);

        var bytes = File.ReadAllBytes(output);
        Assert.NotEmpty(bytes);
        Assert.NotEqual(0xEF, bytes[0]);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            "summary \u00e9",
            json.RootElement.GetProperty("chapters")[0]
                .GetProperty("summary")
                .GetString());
    }

    [Fact]
    public async Task Invalid_evidence_is_preserved_as_a_provider_failure_under_partial_policy()
    {
        var artifact = CreateArtifact(
            Segment("source", 0, 1, 0, "segment-source"));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (_, _) => new Processing.TranscriptChapterSummary(
                        "bad evidence",
                        evidence:
                        [
                            new Processing.TranscriptChapterEvidenceReference(
                                "not-a-source-segment",
                                TimeSpan.Zero,
                                TimeSpan.FromSeconds(1))
                        ])))
            .EnrichAsync(
                artifact,
                new Processing.TranscriptChapterEnrichmentOptions
                {
                    FailurePolicy =
                        Processing.TranscriptChapterEnrichmentFailurePolicy.PreservePartial
                });

        Assert.Equal(
            Processing.TranscriptChapterEnrichmentStatus.Failed,
            Assert.Single(document).Status);
        Assert.Equal(
            "invalid_enrichment_result",
            Assert.Single(document).Failure!.Code);
        Assert.Null(Assert.Single(document).Summary);
    }

    [Fact]
    public async Task PreservePartial_records_overall_provider_failure_without_fabricating_a_summary()
    {
        var artifact = CreateArtifact(Segment("source", 0, 1, 0, "segment-source"));
        var document = await new Processing.TranscriptChapterEnrichmentOrchestrator(
                new ScriptedEnricher(
                    (_, _) => new Processing.TranscriptChapterSummary("chapter summary")),
                new RecordingAssembler(
                    _ => throw new InvalidOperationException("assembler detail")))
            .EnrichAsync(
                artifact,
                new Processing.TranscriptChapterEnrichmentOptions
                {
                    IncludeOverallSummary = true,
                    FailurePolicy =
                        Processing.TranscriptChapterEnrichmentFailurePolicy.PreservePartial
                });

        Assert.Null(document.OverallSummary);
        Assert.Equal(
            "overall_summary_provider_failure",
            document.OverallSummaryFailure!.Code);
        Assert.True(document.IsPartial);
    }

    private static Processing.TranscriptChapterArtifactDocument CreateArtifact(
        params Processing.TranscriptSegment[] segments)
    {
        var document = new Processing.TranscriptDocument(
            "fixture.wav",
            new Processing.TranscriptProvenance(
                "fixture-provider",
                "fixture-model",
                metadata: new Dictionary<string, string>
                {
                    ["fixture"] = "enrichment"
                }),
            segments);
        return new Processing.TranscriptChapterGenerator().Generate(
            document,
            new Processing.TranscriptChapterGenerationOptions
            {
                MinimumDuration = TimeSpan.FromMilliseconds(100),
                MaximumDuration = TimeSpan.FromSeconds(1)
            });
    }

    private static Processing.TranscriptSegment Segment(
        string text,
        double startSeconds,
        double endSeconds,
        int ordinal,
        string id,
        string? sourceId = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            text,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            ordinal,
            id: id,
            sourceId: sourceId,
            sourceMetadata: metadata);

    private sealed class ScriptedEnricher : Processing.ITranscriptChapterEnricher
    {
        private readonly Func<
            Processing.TranscriptChapterArtifact,
            int,
            Processing.TranscriptChapterSummary?> script;

        public ScriptedEnricher(
            Func<
                Processing.TranscriptChapterArtifact,
                int,
                Processing.TranscriptChapterSummary?> script)
        {
            this.script = script;
        }

        public int CallCount { get; private set; }

        public ValueTask<Processing.TranscriptChapterSummary?> EnrichAsync(
            Processing.TranscriptChapterEnrichmentRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(script(request.Chapter, CallCount));
        }
    }

    private sealed class RecordingAssembler : Processing.ITranscriptOverallSummaryAssembler
    {
        private readonly Func<
            Processing.TranscriptOverallSummaryRequest,
            Processing.TranscriptOverallSummary?> script;

        public RecordingAssembler(
            Func<
                Processing.TranscriptOverallSummaryRequest,
                Processing.TranscriptOverallSummary?> script)
        {
            this.script = script;
        }

        public Processing.TranscriptOverallSummaryRequest? LastRequest { get; private set; }

        public ValueTask<Processing.TranscriptOverallSummary?> AssembleAsync(
            Processing.TranscriptOverallSummaryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return ValueTask.FromResult(script(request));
        }
    }

    private sealed class BlockingEnricher : Processing.ITranscriptChapterEnricher
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Processing.TranscriptChapterSummary?> EnrichAsync(
            Processing.TranscriptChapterEnrichmentRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new Processing.TranscriptChapterSummary("unreachable");
        }
    }

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"audio-transcriber-enrichment-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}

internal static class TranscriptChapterArtifactTestExtensions
{
    public static int ChapterNumber(
        this Processing.TranscriptChapterArtifact chapter) =>
        int.Parse(chapter.Title["Chapter ".Length..]);
}
