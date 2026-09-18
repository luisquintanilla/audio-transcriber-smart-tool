using System.Globalization;

namespace AudioTranscriber;

public sealed record AudioConversionOptions(string InputPath, string OutputPath)
{
    public int SampleRate { get; init; } = AudioRequirements.RequiredSampleRate;

    public short Channels { get; init; } = AudioRequirements.RequiredChannels;

    public short BitsPerSample { get; init; } = 16;

    public string? FfmpegPath { get; init; }

    public bool Force { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InputPath))
        {
            throw new ArgumentException("Input path cannot be empty.", nameof(InputPath));
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            throw new ArgumentException("Output path cannot be empty.", nameof(OutputPath));
        }

        if (SampleRate != AudioRequirements.RequiredSampleRate ||
            Channels != AudioRequirements.RequiredChannels ||
            BitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"Only {AudioRequirements.RequiredSampleRate} Hz mono 16-bit PCM WAV conversion is supported.");
        }
    }
}

public sealed record AudioConversionResult(
    string InputPath,
    string OutputPath,
    int SampleRate,
    short Channels,
    short BitsPerSample,
    long OutputBytes,
    TimeSpan Duration);

public interface IAudioConversionService
{
    Task<AudioConversionResult> ConvertAsync(
        AudioConversionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class AudioConversionService : IAudioConversionService
{
    private readonly IFfmpegExecutableResolver _executableResolver;
    private readonly IFfmpegRunner _ffmpegRunner;
    private readonly WavAudioMetadataReader _metadataReader;

    public AudioConversionService(
        IFfmpegRunner? ffmpegRunner = null,
        IFfmpegExecutableResolver? executableResolver = null,
        WavAudioMetadataReader? metadataReader = null)
    {
        _ffmpegRunner = ffmpegRunner ?? new ProcessFfmpegRunner();
        _executableResolver = executableResolver ?? new FfmpegExecutableResolver();
        _metadataReader = metadataReader ?? new WavAudioMetadataReader();
    }

    public async Task<AudioConversionResult> ConvertAsync(
        AudioConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var inputPath = Path.GetFullPath(options.InputPath);
        var outputPath = Path.GetFullPath(options.OutputPath);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException(
                $"Audio input was not found: '{SafePathDisplay.Basename(inputPath)}'.");
        }

        if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Audio conversion input and output must be different files: '{SafePathDisplay.Basename(inputPath)}'.");
        }

        if (File.Exists(outputPath) && !options.Force)
        {
            throw new IOException(
                $"Audio output already exists: '{SafePathDisplay.Basename(outputPath)}'. Use --force to overwrite it.");
        }

        var resolution = _executableResolver.Resolve(options.FfmpegPath);
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.ExecutablePath))
        {
            throw new FfmpegUnavailableException(resolution.Detail);
        }

        string? temporaryOutputPath = CreateTemporaryOutputPath(outputPath);
        var normalizedOptions = options with
        {
            InputPath = inputPath,
            OutputPath = temporaryOutputPath!
        };
        try
        {
            var arguments = FfmpegArguments.Build(normalizedOptions);
            var runResult = await _ffmpegRunner
                .RunAsync(resolution.ExecutablePath, arguments, cancellationToken)
                .ConfigureAwait(false);

            if (!runResult.Succeeded)
            {
                var diagnostics = RedactDiagnostics(
                    runResult.StandardError,
                    inputPath,
                    outputPath,
                    temporaryOutputPath);
                var detail = string.IsNullOrWhiteSpace(diagnostics)
                    ? "FFmpeg did not provide diagnostic output."
                    : $"FFmpeg reported: {diagnostics}";
                throw new AudioConversionException(
                    $"Audio conversion failed for '{SafePathDisplay.Basename(inputPath)}' " +
                    $"to '{SafePathDisplay.Basename(outputPath)}' with exit code {runResult.ExitCode}. {detail}");
            }

            if (!File.Exists(temporaryOutputPath))
            {
                throw new AudioConversionException(
                    $"FFmpeg reported success but did not create '{SafePathDisplay.Basename(outputPath)}'.");
            }

            WavAudioMetadata metadata;
            try
            {
                metadata = _metadataReader.Read(temporaryOutputPath);
            }
            catch (EndOfStreamException exception)
            {
                throw new AudioConversionException(
                    $"FFmpeg produced a truncated WAV file at '{SafePathDisplay.Basename(outputPath)}'.",
                    exception);
            }
            catch (UnsupportedAudioFormatException exception)
            {
                throw new AudioConversionException(
                    $"FFmpeg produced '{SafePathDisplay.Basename(outputPath)}', but it does not meet the " +
                    $"{AudioRequirements.RequiredSampleRate} Hz mono 16-bit PCM WAV contract.",
                    exception);
            }
            catch (InvalidDataException exception)
            {
                throw new AudioConversionException(
                    $"FFmpeg produced an invalid WAV file at '{SafePathDisplay.Basename(outputPath)}'.",
                    exception);
            }

            File.Move(temporaryOutputPath, outputPath, overwrite: options.Force);
            temporaryOutputPath = null;

            return new AudioConversionResult(
                inputPath,
                outputPath,
                metadata.SampleRate,
                metadata.Channels,
                metadata.BitsPerSample,
                new FileInfo(outputPath).Length,
                metadata.Duration);
        }
        finally
        {
            if (temporaryOutputPath is not null && File.Exists(temporaryOutputPath))
            {
                File.Delete(temporaryOutputPath);
            }
        }
    }

    private static string CreateTemporaryOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        var fileName = $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp.wav";
        return Path.Combine(directory, fileName);
    }

    private static string RedactDiagnostics(string diagnostics, params string?[] paths)
    {
        var redacted = diagnostics.Trim();
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                redacted = SafePathDisplay.RedactKnownPaths(
                    redacted,
                    SafePathDisplay.Basename(path),
                    path);
            }
        }

        return redacted;
    }
}

public static class FfmpegArguments
{
    public static IReadOnlyList<string> Build(AudioConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-nostdin",
            options.Force ? "-y" : "-n",
            "-i",
            options.InputPath,
            "-vn",
            "-ac",
            options.Channels.ToString(CultureInfo.InvariantCulture),
            "-ar",
            options.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-c:a",
            "pcm_s16le",
            "-f",
            "wav",
            options.OutputPath
        ];
    }
}

public sealed class AudioConversionException : IOException
{
    public AudioConversionException(string message) : base(message)
    {
    }

    public AudioConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FfmpegUnavailableException : IOException
{
    public FfmpegUnavailableException(string message) : base(message)
    {
    }

    public FfmpegUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
