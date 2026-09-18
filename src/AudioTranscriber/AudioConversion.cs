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
    private readonly WavAudioReader _wavReader;

    public AudioConversionService(
        IFfmpegRunner? ffmpegRunner = null,
        IFfmpegExecutableResolver? executableResolver = null,
        WavAudioReader? wavReader = null)
    {
        _ffmpegRunner = ffmpegRunner ?? new ProcessFfmpegRunner();
        _executableResolver = executableResolver ?? new FfmpegExecutableResolver();
        _wavReader = wavReader ?? new WavAudioReader();
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

        var normalizedOptions = options with
        {
            InputPath = inputPath,
            OutputPath = outputPath
        };
        var arguments = FfmpegArguments.Build(normalizedOptions);
        var runResult = await _ffmpegRunner
            .RunAsync(resolution.ExecutablePath, arguments, cancellationToken)
            .ConfigureAwait(false);

        if (!runResult.Succeeded)
        {
            var diagnostics = RedactDiagnostics(runResult.StandardError, inputPath, outputPath);
            var detail = string.IsNullOrWhiteSpace(diagnostics)
                ? "FFmpeg did not provide diagnostic output."
                : $"FFmpeg reported: {diagnostics}";
            throw new AudioConversionException(
                $"Audio conversion failed for '{SafePathDisplay.Basename(inputPath)}' " +
                $"to '{SafePathDisplay.Basename(outputPath)}' with exit code {runResult.ExitCode}. {detail}");
        }

        if (!File.Exists(outputPath))
        {
            throw new AudioConversionException(
                $"FFmpeg reported success but did not create '{SafePathDisplay.Basename(outputPath)}'.");
        }

        AudioClip convertedAudio;
        try
        {
            convertedAudio = _wavReader.Load(outputPath);
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

        if (convertedAudio.Format != AudioSampleFormat.Pcm16)
        {
            throw new AudioConversionException(
                $"FFmpeg produced '{SafePathDisplay.Basename(outputPath)}' with sample format " +
                $"{convertedAudio.Format}, not 16-bit PCM.");
        }

        return new AudioConversionResult(
            inputPath,
            outputPath,
            convertedAudio.SampleRate,
            convertedAudio.Channels,
            16,
            new FileInfo(outputPath).Length,
            convertedAudio.Duration);
    }

    private static string RedactDiagnostics(string diagnostics, string inputPath, string outputPath)
    {
        var redacted = diagnostics.Trim();
        redacted = SafePathDisplay.RedactKnownPaths(
            redacted,
            SafePathDisplay.Basename(inputPath),
            inputPath);
        return SafePathDisplay.RedactKnownPaths(
            redacted,
            SafePathDisplay.Basename(outputPath),
            outputPath);
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
