using AudioTranscriber.Cli;

namespace AudioTranscriber.Tests;

public sealed class AudioConversionTests
{
    [Fact]
    public void Options_default_to_the_transcription_contract()
    {
        var options = new AudioConversionOptions("input.audio", "output.wav");

        Assert.Equal(AudioRequirements.RequiredSampleRate, options.SampleRate);
        Assert.Equal(AudioRequirements.RequiredChannels, options.Channels);
        Assert.Equal(16, options.BitsPerSample);
        Assert.False(options.Force);
        Assert.Null(options.FfmpegPath);
    }

    [Fact]
    public void Arguments_use_safe_non_shell_ffmpeg_conversion_arguments()
    {
        var arguments = FfmpegArguments.Build(
            new AudioConversionOptions("input.audio", "output.wav"));

        Assert.Equal(
            [
                "-hide_banner",
                "-loglevel",
                "error",
                "-nostdin",
                "-n",
                "-i",
                "input.audio",
                "-vn",
                "-ac",
                "1",
                "-ar",
                "16000",
                "-c:a",
                "pcm_s16le",
                "-f",
                "wav",
                "output.wav"
            ],
            arguments);
    }

    [Fact]
    public void Arguments_use_force_overwrite_only_when_requested()
    {
        var arguments = FfmpegArguments.Build(
            new AudioConversionOptions("input.audio", "output.wav")
            {
                Force = true
            });

        Assert.Contains("-y", arguments);
        Assert.DoesNotContain("-n", arguments);
    }

    [Fact]
    public async Task Existing_output_is_rejected_before_running_ffmpeg()
    {
        var input = TestAudio.CreateWav();
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        File.WriteAllText(output, "existing");
        var runner = new RecordingRunner();

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                new AudioConversionService(runner, new FakeResolver())
                    .ConvertAsync(new AudioConversionOptions(input, output)));

            Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, runner.CallCount);
            Assert.Equal("existing", File.ReadAllText(output));
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public async Task Force_overwrites_existing_output_and_returns_validated_result()
    {
        var input = TestAudio.CreateWav(sampleCount: 320);
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        File.WriteAllText(output, "existing");
        var runner = new RecordingRunner
        {
            OnRun = (_, arguments) =>
            {
                File.Copy(arguments[Array.IndexOf(arguments.ToArray(), "-i") + 1], arguments[^1], overwrite: true);
                return new FfmpegRunResult(0, string.Empty, string.Empty);
            }
        };

        try
        {
            var result = await new AudioConversionService(runner, new FakeResolver())
                .ConvertAsync(
                    new AudioConversionOptions(input, output)
                    {
                        Force = true
                    });

            Assert.Equal(Path.GetFullPath(input), result.InputPath);
            Assert.Equal(Path.GetFullPath(output), result.OutputPath);
            Assert.Equal(AudioRequirements.RequiredSampleRate, result.SampleRate);
            Assert.Equal(AudioRequirements.RequiredChannels, result.Channels);
            Assert.Equal(16, result.BitsPerSample);
            Assert.True(result.OutputBytes > 0);
            Assert.Equal(TimeSpan.FromSeconds(320d / AudioRequirements.RequiredSampleRate), result.Duration);
            Assert.Contains("-y", runner.Arguments!);
            Assert.DoesNotContain("-n", runner.Arguments!);
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public async Task Force_conversion_replaces_output_symlink_without_modifying_source()
    {
        var input = TestAudio.CreateWav(sampleCount: 320);
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-alias-{Guid.NewGuid():N}.wav");
        try
        {
            try
            {
                File.CreateSymbolicLink(output, input);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var sourceBefore = File.ReadAllBytes(input);
            var runner = new RecordingRunner
            {
                OnRun = (_, arguments) =>
                {
                    File.Copy(arguments[Array.IndexOf(arguments.ToArray(), "-i") + 1], arguments[^1]);
                    return new FfmpegRunResult(0, string.Empty, string.Empty);
                }
            };

            await new AudioConversionService(runner, new FakeResolver())
                .ConvertAsync(
                    new AudioConversionOptions(input, output)
                    {
                        Force = true
                    });

            Assert.Equal(sourceBefore, File.ReadAllBytes(input));
            Assert.Equal(sourceBefore, File.ReadAllBytes(output));
            Assert.False(File.GetAttributes(output).HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public async Task Nonzero_ffmpeg_exit_is_reported_with_actionable_diagnostics()
    {
        var input = TestAudio.CreateWav();
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        var runner = new RecordingRunner
        {
            Result = new FfmpegRunResult(17, string.Empty, $"failed to decode '{input}' into '{output}'")
        };

        try
        {
            var exception = await Assert.ThrowsAsync<AudioConversionException>(() =>
                new AudioConversionService(runner, new FakeResolver())
                    .ConvertAsync(new AudioConversionOptions(input, output)));

            Assert.Contains("exit code 17", exception.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(input), exception.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(output), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFullPath(input), exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(output), exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public async Task Missing_ffmpeg_is_reported_without_running_a_process()
    {
        var input = TestAudio.CreateWav();
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        var runner = new RecordingRunner();

        try
        {
            var exception = await Assert.ThrowsAsync<FfmpegUnavailableException>(() =>
                new AudioConversionService(
                        runner,
                        new FakeResolver
                        {
                            Resolution = new FfmpegExecutableResolution(
                                false,
                                null,
                                "FFmpeg was not found on PATH. Install ffmpeg or pass --ffmpeg <path>.")
                    })
                    .ConvertAsync(new AudioConversionOptions(input, output)));

            Assert.Contains("not found on PATH", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, runner.CallCount);
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public async Task Invalid_ffmpeg_output_is_rejected_by_wav_validation()
    {
        var input = TestAudio.CreateWav();
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        var invalidOutputSource = TestAudio.CreateWav(8000, sampleCount: 10);
        var runner = new RecordingRunner
        {
            OnRun = (_, arguments) =>
            {
                File.Copy(invalidOutputSource, arguments[arguments.Count - 1], overwrite: true);
                return new FfmpegRunResult(0, string.Empty, string.Empty);
            }
        };

        try
        {
            var exception = await Assert.ThrowsAsync<AudioConversionException>(() =>
                new AudioConversionService(runner, new FakeResolver())
                    .ConvertAsync(new AudioConversionOptions(input, output)));

            Assert.Contains("does not meet", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(output)!,
                $".{Path.GetFileName(output)}.*.tmp.wav"));
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
            TestAudio.Delete(invalidOutputSource);
        }
    }

    [Fact]
    public async Task Truncated_ffmpeg_output_is_normalized_and_cleaned_up()
    {
        var input = TestAudio.CreateWav();
        var output = Path.Combine(Path.GetTempPath(), $"audio-transcriber-output-{Guid.NewGuid():N}.wav");
        var runner = new RecordingRunner
        {
            OnRun = (_, arguments) =>
            {
                File.WriteAllBytes(arguments[^1], "RIFF"u8.ToArray());
                return new FfmpegRunResult(0, string.Empty, string.Empty);
            }
        };

        try
        {
            var exception = await Assert.ThrowsAsync<AudioConversionException>(() =>
                new AudioConversionService(runner, new FakeResolver())
                    .ConvertAsync(new AudioConversionOptions(input, output)));

            Assert.Contains("truncated WAV", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFileName(output), exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(output)!,
                $".{Path.GetFileName(output)}.*.tmp.wav"));
        }
        finally
        {
            TestAudio.Delete(input);
            TestAudio.Delete(output);
        }
    }

    [Fact]
    public void Metadata_reader_validates_contract_without_materializing_samples()
    {
        var path = TestAudio.CreateWav(sampleCount: 320);
        try
        {
            var metadata = new WavAudioMetadataReader().Read(path);

            Assert.Equal(Path.GetFullPath(path), metadata.SourcePath);
            Assert.Equal(AudioRequirements.RequiredSampleRate, metadata.SampleRate);
            Assert.Equal(AudioRequirements.RequiredChannels, metadata.Channels);
            Assert.Equal(16, metadata.BitsPerSample);
            Assert.Equal(320L * 2, metadata.DataBytes);
            Assert.Equal(TimeSpan.FromSeconds(320d / AudioRequirements.RequiredSampleRate), metadata.Duration);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task Cli_convert_requires_explicit_input_and_output()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await new CliApplication(conversionService: new FakeConversionService()).RunAsync(
            ["convert", "--input", "input.audio"],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("A --output WAV path is required.", error.ToString());
    }

    [Fact]
    public async Task Cli_convert_reports_a_safe_success_result()
    {
        var output = new StringWriter();
        var service = new FakeConversionService();

        var exitCode = await new CliApplication(conversionService: service).RunAsync(
            ["convert", "--input", @"C:\private\input.audio", "--output", @"C:\private\speech.wav"],
            output,
            new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.Contains("Converted 'input.audio' to 'speech.wav'", output.ToString());
        Assert.Equal(Path.GetFullPath(@"C:\private\input.audio"), service.Options!.InputPath);
        Assert.Equal(Path.GetFullPath(@"C:\private\speech.wav"), service.Options.OutputPath);
    }

    [Fact]
    public void Resolver_rejects_non_executable_unix_candidate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"audio-transcriber-ffmpeg-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "not executable");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);

            var resolution = new FfmpegExecutableResolver().Resolve(path);

            Assert.False(resolution.IsAvailable);
            Assert.Contains("not found or is not executable", resolution.Detail, StringComparison.Ordinal);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    private sealed class FakeResolver : IFfmpegExecutableResolver
    {
        public FfmpegExecutableResolution Resolution { get; init; } =
            new(true, "fake-ffmpeg", "FFmpeg is available.");

        public FfmpegExecutableResolution Resolve(string? configuredPath) => Resolution;
    }

    private sealed class RecordingRunner : IFfmpegRunner
    {
        public FfmpegRunResult Result { get; init; } = new(1, string.Empty, "conversion failed");

        public Func<string, IReadOnlyList<string>, FfmpegRunResult>? OnRun { get; init; }

        public int CallCount { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<FfmpegRunResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Arguments = arguments;
            return Task.FromResult(OnRun?.Invoke(executablePath, arguments) ?? Result);
        }
    }

    private sealed class FakeConversionService : IAudioConversionService
    {
        public AudioConversionOptions? Options { get; private set; }

        public Task<AudioConversionResult> ConvertAsync(
            AudioConversionOptions options,
            CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(
                new AudioConversionResult(
                    Path.GetFullPath(options.InputPath),
                    Path.GetFullPath(options.OutputPath),
                    options.SampleRate,
                    options.Channels,
                    options.BitsPerSample,
                    10,
                    TimeSpan.FromSeconds(1)));
        }
    }
}
