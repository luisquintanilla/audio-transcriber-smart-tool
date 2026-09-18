using System.Text.Json;
using AudioTranscriber.Cli;

namespace AudioTranscriber.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task Empty_arguments_return_usage_failure_without_prompting()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync([], output, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage:", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Manifest_command_is_available_without_model_integration()
    {
        var output = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(["manifest"], output, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("smart_tool_format: 1", output.ToString());
        Assert.Contains("name: audio-transcriber", output.ToString());
        Assert.Contains("## Model integration status", output.ToString());
    }

    [Fact]
    public async Task Status_command_exposes_deterministic_frontmatter_json_separately()
    {
        var output = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(["status"], output, new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("\"smartToolFormat\": 1", output.ToString());
        Assert.Contains("\"name\": \"audio-transcriber\"", output.ToString());
    }

    [Fact]
    public async Task Top_level_short_help_is_terse_and_full_help_is_the_tool_skill()
    {
        var shortOutput = new StringWriter();
        var fullOutput = new StringWriter();

        var shortExitCode = await new CliApplication().RunAsync(["-h"], shortOutput, new StringWriter());
        var fullExitCode = await new CliApplication().RunAsync(["--help"], fullOutput, new StringWriter());

        Assert.Equal(0, shortExitCode);
        Assert.Equal(0, fullExitCode);
        Assert.Contains("Capabilities:", shortOutput.ToString());
        Assert.DoesNotContain("## Arguments", shortOutput.ToString());
        Assert.Contains("<skill_content name=\"audio-transcriber\">", fullOutput.ToString());
        Assert.Contains("## Capabilities", fullOutput.ToString());
        Assert.Contains("audio-transcriber transcribe --help", fullOutput.ToString());
        Assert.DoesNotContain("smart_tool_format:", fullOutput.ToString());
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("status")]
    [InlineData("doctor")]
    [InlineData("convert")]
    [InlineData("transcribe")]
    public async Task Every_capability_supports_short_and_full_help(string capabilityName)
    {
        var shortOutput = new StringWriter();
        var fullOutput = new StringWriter();

        var shortExitCode = await new CliApplication().RunAsync(
            [capabilityName, "-h"],
            shortOutput,
            new StringWriter());
        var fullExitCode = await new CliApplication().RunAsync(
            [capabilityName, "--help"],
            fullOutput,
            new StringWriter());

        Assert.Equal(0, shortExitCode);
        Assert.Equal(0, fullExitCode);
        Assert.Contains(capabilityName, shortOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("## Arguments", shortOutput.ToString());
        Assert.Contains("## Arguments", fullOutput.ToString());
        Assert.Contains("## Failures", fullOutput.ToString());
    }

    [Fact]
    public async Task Explicit_model_garden_engine_reports_restore_blocker()
    {
        var path = TestAudio.CreateWav();
        try
        {
            var error = new StringWriter();
            var exitCode = await new CliApplication(new ModelGardenWhisperEngine()).RunAsync(
                ["transcribe", "--input", path],
                new StringWriter(),
                error);

            Assert.Equal(1, exitCode);
            Assert.Contains("requires authentication", error.ToString());
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task Transcribe_command_renders_using_injected_library_engine()
    {
        var path = TestAudio.CreateWav();
        try
        {
            var output = new StringWriter();
            var exitCode = await new CliApplication(new FakeEngine()).RunAsync(
                ["transcribe", "--input", path, "--format", "webvtt"],
                output,
                new StringWriter());

            Assert.Equal(0, exitCode);
            Assert.StartsWith("WEBVTT", output.ToString());
            Assert.Contains("hello", output.ToString());
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task Unknown_command_returns_usage_failure_without_writing_success_output()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(["not-a-command"], output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString());
    }

    [Fact]
    public async Task Missing_transcribe_input_returns_usage_failure_without_prompting()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            ["transcribe", "--format", "text"],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("At least one --input WAV path is required.", error.ToString());
    }

    [Fact]
    public async Task Invalid_transcribe_format_returns_usage_failure_before_reading_input()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication().RunAsync(
            ["transcribe", "--input", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav"), "--format", "xml"],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("--format must be text, json, srt, or webvtt.", error.ToString());
    }

    [Fact]
    public async Task Missing_transcribe_file_returns_explicit_input_failure_without_prompting()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav");

        var exitCode = await new CliApplication().RunAsync(
            ["transcribe", "--input", path],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.StartsWith("error:", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(path), error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Doctor_command_redacts_repository_and_cache_paths()
    {
        var output = new StringWriter();
        var repository = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "private-repository");

        var exitCode = await new CliApplication().RunAsync(
            ["doctor"],
            output,
            new StringWriter(),
            repositoryRoot: repository);

        Assert.Equal(0, exitCode);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal("<repository>", report.RootElement.GetProperty("repositoryRoot").GetString());
        Assert.Equal("<model-cache>", report.RootElement.GetProperty("modelCacheDirectory").GetString());
        Assert.DoesNotContain(Path.GetFullPath(repository), output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_returns_non_interactive_cancellation_exit_code()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication(new FakeEngine()).RunAsync(
            ["transcribe", "--input", "never-read.wav"],
            output,
            error,
            cancellationToken: cancellation.Token);

        Assert.Equal(4, exitCode);
        Assert.Empty(output.ToString());
        Assert.Equal("Operation cancelled." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public async Task Injected_engine_is_called_and_success_output_is_returned_without_model_integration()
    {
        var path = TestAudio.CreateWav();
        try
        {
            var engine = new RecordingEngine();
            var output = new StringWriter();

            var exitCode = await new CliApplication(engine).RunAsync(
                ["transcribe", "--input", path, "--format", "text"],
                output,
                new StringWriter());

            Assert.Equal(0, exitCode);
            Assert.Equal(1, engine.CallCount);
            Assert.Equal(Path.GetFullPath(path), engine.LastSourcePath);
            Assert.Contains("[00:00:00.000 - 00:00:00.050] injected", output.ToString());
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
                [new TranscriptSegment("hello", TimeSpan.Zero, TimeSpan.FromMilliseconds(50))]);
    }

    private sealed class RecordingEngine : ITranscriptionEngine
    {
        public int CallCount { get; private set; }

        public string? LastSourcePath { get; private set; }

        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSourcePath = audio.SourcePath;
            return ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
                [new TranscriptSegment("injected", TimeSpan.Zero, TimeSpan.FromMilliseconds(50))]);
        }
    }
}
