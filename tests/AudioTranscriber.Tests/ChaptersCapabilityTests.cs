using System.Text.Json;
using AudioTranscriber.Cli;
using Microsoft.Extensions.AI;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class ChaptersCapabilityTests
{
    [Fact]
    public async Task Chapters_cli_reads_existing_transcript_and_writes_versioned_artifact()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.WriteTranscript(
            CreateDocument(
                Segment("first source", 0, 2, 0, "segment-first", "source-first"),
                Segment("second source", 2, 4, 1, "segment-second", "source-second")));
        var outputPath = fixture.Path("chapters.json");
        var stdout = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            [
                "chapters",
                "--input", input,
                "--output", outputPath,
                "--min-duration", "1",
                "--max-duration", "10"
            ],
            stdout,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Wrote 1 chapter(s)", stdout.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("1.0", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("deterministic", json.RootElement.GetProperty("generation").GetProperty("provider").GetString());
        Assert.Equal(
            ["segment-first", "segment-second"],
            json.RootElement.GetProperty("chapters")[0]
                .GetProperty("sourceSegmentIds")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            "first source second source",
            json.RootElement.GetProperty("chapters")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Chapters_cli_rejects_malformed_input_before_creating_output()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.Path("malformed.json");
        var outputPath = fixture.Path("chapters.json");
        File.WriteAllText(input, "{ not valid json");
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", outputPath],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.StartsWith("error:", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("invalid_json", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
        Assert.DoesNotContain(Path.GetFullPath(input), error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chapters_cli_rejects_invalid_provider_as_usage_failure()
    {
        using var fixture = new TemporaryFixture();
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            [
                "chapters",
                "--input", fixture.Path("input.json"),
                "--output", fixture.Path("chapters.json"),
                "--provider", "llm"
            ],
            new StringWriter(),
            error);

        Assert.Equal(2, exitCode);
        Assert.Contains("--provider must be deterministic or granite.", error.ToString());
        Assert.False(File.Exists(fixture.Path("chapters.json")));
    }

    [Fact]
    public async Task Chapters_cli_is_deterministic_and_preserves_source_boundaries()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.WriteTranscript(
            CreateDocument(
                Segment("alpha", 0, 1, 0, "segment-alpha", "source-alpha"),
                Segment("beta", 1, 2, 1, "segment-beta", "source-beta"),
                Segment("gamma", 2, 3, 2, "segment-gamma", "source-gamma")));
        var firstPath = fixture.Path("first.json");
        var secondPath = fixture.Path("second.json");
        var first = await RunChaptersAsync(input, firstPath);
        var second = await RunChaptersAsync(input, secondPath);

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        var chapter = Assert.Single(json.RootElement.GetProperty("chapters").EnumerateArray());
        var boundary = chapter.GetProperty("boundary");
        Assert.Equal("segment-alpha", boundary.GetProperty("startSegmentId").GetString());
        Assert.Equal("segment-gamma", boundary.GetProperty("endSegmentId").GetString());
        Assert.True(boundary.GetProperty("startSnappedToSegmentBoundary").GetBoolean());
        Assert.True(boundary.GetProperty("endSnappedToSegmentBoundary").GetBoolean());
        Assert.Equal(0, boundary.GetProperty("startOriginalOrdinal").GetInt32());
        Assert.Equal(2, boundary.GetProperty("endOriginalOrdinal").GetInt32());
    }

    [Fact]
    public void Chapter_generator_works_without_embedding_and_rejects_invalid_bounds()
    {
        var document = CreateDocument(Segment("offline", 0, 1, 0, "segment-offline"));
        var generator = new Processing.TranscriptChapterGenerator();

        var artifact = generator.Generate(
            document,
            new Processing.TranscriptChapterGenerationOptions
            {
                MinimumDuration = TimeSpan.FromMilliseconds(100),
                MaximumDuration = TimeSpan.FromSeconds(2)
            });

        Assert.Equal("deterministic", artifact.Generation.Provider);
        Assert.Single(artifact);
        Assert.Throws<ArgumentException>(
            () => generator.Generate(
                document,
                new Processing.TranscriptChapterGenerationOptions
                {
                    MinimumDuration = TimeSpan.FromSeconds(3),
                    MaximumDuration = TimeSpan.FromSeconds(2)
                }));
    }

    [Fact]
    public async Task Chapter_generator_uses_explicit_injected_provider_without_implicit_selection()
    {
        var embedding = new RecordingEmbeddingProvider();
        var document = CreateDocument(Segment("provider-backed", 0, 1, 0, "segment-provider"));

        var artifact = await new Processing.TranscriptChapterGenerator(embedding)
            .GenerateAsync(
                document,
                new Processing.TranscriptChapterGenerationOptions
                {
                    Provider = "fake",
                    MinimumDuration = TimeSpan.FromMilliseconds(100),
                    MaximumDuration = TimeSpan.FromSeconds(2)
                });

        Assert.Equal(1, embedding.CallCount);
        Assert.Equal("fake", artifact.Provider);
        Assert.Equal("provider-backed", Assert.Single(artifact).Text);
        Assert.True(Assert.Single(artifact).Score > 0);
    }

    [Fact]
    public void Chapter_generator_preserves_gap_metadata_without_emitting_gap_chapters()
    {
        var document = CreateDocument(
            Segment("before gap", 0, 1, 0, "segment-before"),
            Segment("after gap", 3, 4, 1, "segment-after"));

        var artifact = new Processing.TranscriptChapterGenerator().Generate(
            document,
            new Processing.TranscriptChapterGenerationOptions
            {
                MinimumDuration = TimeSpan.FromMilliseconds(100),
                MaximumDuration = TimeSpan.FromSeconds(10)
            });

        Assert.Equal(2, artifact.Count);
        Assert.Null(artifact[0].Boundary.GapBefore);
        Assert.Equal(TimeSpan.FromSeconds(2), artifact[0].Boundary.GapAfter);
        Assert.Equal(TimeSpan.FromSeconds(2), artifact[1].Boundary.GapBefore);
        Assert.Null(artifact[1].Boundary.GapAfter);
        Assert.Equal(["before gap", "after gap"], artifact.Select(chapter => chapter.Text));
    }

    [Fact]
    public void Chapter_generator_clips_gap_metadata_to_requested_start()
    {
        var document = CreateDocument(
            Segment("before requested range", 0, 1, 0, "segment-before"),
            Segment("outside requested range", 2, 3, 1, "segment-outside"),
            Segment("inside requested range", 11, 12, 2, "segment-inside"));

        var artifact = new Processing.TranscriptChapterGenerator().Generate(
            document,
            new Processing.TranscriptChapterGenerationOptions
            {
                MinimumDuration = TimeSpan.FromMilliseconds(100),
                MaximumDuration = TimeSpan.FromSeconds(10),
                RequestedStart = TimeSpan.FromSeconds(10)
            });

        var chapter = Assert.Single(artifact);
        Assert.Equal("inside requested range", chapter.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), chapter.Boundary.GapBefore);
        Assert.Null(chapter.Boundary.GapAfter);
    }

    [Fact]
    public async Task Chapters_cli_refuses_overwrite_and_supports_explicit_overwrite()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.WriteTranscript(
            CreateDocument(Segment("stable output", 0, 1, 0, "segment-stable")));
        var outputPath = fixture.Path("chapters.json");
        var firstExitCode = await RunChaptersExitCodeAsync(input, outputPath);
        var original = File.ReadAllText(outputPath);

        var refusalError = new StringWriter();
        var refusalExitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", outputPath],
            new StringWriter(),
            refusalError);

        Assert.Equal(0, firstExitCode);
        Assert.Equal(1, refusalExitCode);
        Assert.Contains("--overwrite", refusalError.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(outputPath));

        var overwriteExitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", outputPath, "--overwrite"],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, overwriteExitCode);
        Assert.Equal(original, File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task Chapters_cli_rejects_identical_input_and_output_in_default_and_overwrite_modes()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.WriteTranscript(
            CreateDocument(Segment("source must survive", 0, 1, 0, "segment-source")));
        var original = File.ReadAllText(input);

        var defaultError = new StringWriter();
        var defaultExitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", input],
            new StringWriter(),
            defaultError);

        var overwriteError = new StringWriter();
        var overwriteExitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", input, "--overwrite"],
            new StringWriter(),
            overwriteError);

        Assert.Equal(1, defaultExitCode);
        Assert.Equal(1, overwriteExitCode);
        Assert.Contains("must be different files", defaultError.ToString(), StringComparison.Ordinal);
        Assert.Contains("must be different files", overwriteError.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(input));
    }

    [Fact]
    public async Task Chapters_cli_rejects_input_symlink_to_output()
    {
        using var fixture = new TemporaryFixture();
        var output = fixture.WriteTranscript(
            CreateDocument(Segment("source must survive", 0, 1, 0, "segment-source")));
        var input = fixture.Path("input-alias.json");
        try
        {
            File.CreateSymbolicLink(input, output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var original = File.ReadAllText(output);
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", output, "--overwrite"],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("must be different files", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(output));
    }

    [Fact]
    public async Task Artifact_writer_removes_partial_file_after_destination_failure()
    {
        using var fixture = new TemporaryFixture();
        var destinationDirectory = fixture.Path("destination");
        Directory.CreateDirectory(destinationDirectory);
        var document = CreateDocument(Segment("atomic", 0, 1, 0, "segment-atomic"));
        var writer = new Processing.TranscriptChapterArtifactFileWriter();

        await Assert.ThrowsAsync<IOException>(
            () => writer.WriteAsync(destinationDirectory, CreateArtifact(document)));

        Assert.True(Directory.Exists(destinationDirectory));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.partial"));
    }

    [Fact]
    public async Task Chapters_cli_granite_selection_reports_missing_assets_without_fallback()
    {
        using var fixture = new TemporaryFixture();
        var input = fixture.WriteTranscript(
            CreateDocument(Segment("needs granite", 0, 1, 0, "segment-granite")));
        var outputPath = fixture.Path("chapters.json");
        var cacheRoot = fixture.Path("granite-cache");
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            [
                "chapters",
                "--input", input,
                "--output", outputPath,
                "--provider", "granite",
                "--cache", cacheRoot
            ],
            new StringWriter(),
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("asset is missing", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.DoesNotContain(Path.GetFullPath(cacheRoot), error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunChaptersAsync(string input, string output)
    {
        var exitCode = await RunChaptersExitCodeAsync(input, output);
        Assert.Equal(0, exitCode);
        return File.ReadAllText(output);
    }

    private static async Task<int> RunChaptersExitCodeAsync(string input, string output)
    {
        return await new CliApplication().RunAsync(
            ["chapters", "--input", input, "--output", output],
            new StringWriter(),
            new StringWriter());
    }

    private static Processing.TranscriptChapterArtifactDocument CreateArtifact(
        Processing.TranscriptDocument document)
    {
        return new Processing.TranscriptChapterGenerator().Generate(document);
    }

    private static Processing.TranscriptDocument CreateDocument(
        params Processing.TranscriptSegment[] segments)
    {
        return new Processing.TranscriptDocument(
            "chapters-fixture.wav",
            new Processing.TranscriptProvenance(
                "fixture-provider",
                "fixture-model",
                metadata: new Dictionary<string, string>
                {
                    ["fixture"] = "chapters"
                }),
            segments);
    }

    private static Processing.TranscriptSegment Segment(
        string text,
        double startSeconds,
        double endSeconds,
        int ordinal,
        string? id = null,
        string? sourceId = null)
    {
        return new Processing.TranscriptSegment(
            text,
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(endSeconds),
            ordinal,
            id: id,
            sourceId: sourceId);
    }

    private sealed class TemporaryFixture : IDisposable
    {
        public TemporaryFixture()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"audio-transcriber-chapters-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path(string name) => System.IO.Path.Combine(Root, name);

        public string WriteTranscript(Processing.TranscriptDocument document)
        {
            var path = Path("transcript.json");
            File.WriteAllText(path, new Processing.TranscriptJsonWriter().Write(document));
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class RecordingEmbeddingProvider
        : IEmbeddingGenerator<TextContent, Embedding<float>>
    {
        public int CallCount { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<TextContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            _ = Assert.Single(values);
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(new float[] { 0.5f, 0.5f })
                ]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
