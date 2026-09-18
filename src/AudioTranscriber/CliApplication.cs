using System.Text.Json;
using System.Security;

namespace AudioTranscriber;

public sealed class CliApplication
{
    private readonly ITranscriptionEngine _engine;
    private readonly WavAudioReader _audioReader;
    private readonly IAudioConversionService _conversionService;

    public CliApplication(
        ITranscriptionEngine? engine = null,
        WavAudioReader? audioReader = null,
        IAudioConversionService? conversionService = null)
    {
        _engine = engine ?? new WhisperNetEngine();
        _audioReader = audioReader ?? new WavAudioReader();
        _conversionService = conversionService ?? new AudioConversionService();
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string? repositoryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            await error.WriteLineAsync(SmartToolCapabilityRegistry.RenderUsage()).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (args is ["-h"])
            {
                await output.WriteAsync(SmartToolCapabilityRegistry.RenderShortHelp()).ConfigureAwait(false);
                return 0;
            }

            if (args is ["--help"])
            {
                await output.WriteAsync(SmartToolCapabilityRegistry.RenderToolHelp()).ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 2 &&
                (args[1] is "-h" or "--help") &&
                SmartToolCapabilityRegistry.TryGet(args[0], out _))
            {
                await output.WriteAsync(
                    SmartToolCapabilityRegistry.RenderCapabilityHelp(
                        args[0],
                        terse: args[1] == "-h")).ConfigureAwait(false);
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "manifest" when args.Length == 1 => await WriteManifestAsync(output).ConfigureAwait(false),
                "status" when args.Length == 1 => await WriteStatusAsync(output).ConfigureAwait(false),
                "doctor" when args.Length == 1 => await WriteDoctorAsync(
                    output,
                    repositoryRoot ?? Directory.GetCurrentDirectory()).ConfigureAwait(false),
                "transcribe" => await TranscribeAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false),
                "convert" => await ConvertAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false),
                _ => await WriteUsageErrorAsync(error).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return 4;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            InvalidOperationException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SecurityException)
        {
            await error.WriteLineAsync(
                $"error: {SafePathDisplay.RedactKnownPaths(
                    SafePathDisplay.RedactKnownPaths(
                        exception.Message,
                        SafePathDisplay.RepositoryToken,
                        Directory.GetCurrentDirectory()),
                    SafePathDisplay.ModelCacheToken,
                    SmartToolPaths.DefaultModelCacheDirectory)}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> WriteManifestAsync(TextWriter output)
    {
        await output.WriteAsync(SmartToolManifestService.Markdown()).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteStatusAsync(TextWriter output)
    {
        await output.WriteAsync(SmartToolManifestService.ToJson(SmartToolManifestService.Create())).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteDoctorAsync(TextWriter output, string repositoryRoot)
    {
        var report = new DoctorService().Run(repositoryRoot);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            })).ConfigureAwait(false);
        return report.Healthy ? 0 : 1;
    }

    private async Task<int> TranscribeAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseTranscribeOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"error: {parseError}").ConfigureAwait(false);
            return 2;
        }

        var batch = await new BatchTranscriptionService(_engine, _audioReader)
            .TranscribeAsync(options.Inputs, cancellationToken)
            .ConfigureAwait(false);
        var rendered = TranscriptRenderers.Create(options.Format).Render(batch);
        if (options.OutputPath is null)
        {
            await output.WriteAsync(rendered).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await File.WriteAllTextAsync(options.OutputPath, rendered, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Transcript output could not be written to '{SafePathDisplay.Basename(options.OutputPath)}'.",
                    exception);
            }
        }

        return 0;
    }

    private async Task<int> ConvertAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseConvertOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"error: {parseError}").ConfigureAwait(false);
            return 2;
        }

        var result = await _conversionService
            .ConvertAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Converted '{SafePathDisplay.Basename(result.InputPath)}' to " +
            $"'{SafePathDisplay.Basename(result.OutputPath)}' as " +
            $"{result.SampleRate} Hz mono {result.BitsPerSample}-bit PCM WAV.").ConfigureAwait(false);
        return 0;
    }

    private static bool TryParseTranscribeOptions(
        string[] args,
        out TranscribeOptions options,
        out string error)
    {
        var inputs = new List<string>();
        var format = TranscriptOutputFormat.Text;
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--input requires a WAV path.";
                        return false;
                    }

                    inputs.Add(args[index]);
                    break;
                case "--format":
                    if (++index >= args.Length || !TranscriptRenderers.TryParse(args[index], out format))
                    {
                        options = default!;
                        error = "--format must be text, json, srt, or webvtt.";
                        return false;
                    }

                    break;
                case "--output":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--output requires a file path.";
                        return false;
                    }

                    output = Path.GetFullPath(args[index]);
                    break;
                default:
                    options = default!;
                    error = $"Unknown transcribe option: {args[index]}";
                    return false;
            }
        }

        if (inputs.Count == 0)
        {
            options = default!;
            error = "At least one --input WAV path is required.";
            return false;
        }

        options = new TranscribeOptions(inputs, format, output);
        error = string.Empty;
        return true;
    }

    private static bool TryParseConvertOptions(
        string[] args,
        out AudioConversionOptions options,
        out string error)
    {
        string? input = null;
        string? output = null;
        string? ffmpeg = null;
        var force = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--input requires an audio path.";
                        return false;
                    }

                    input = args[index];
                    break;
                case "--output":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--output requires a WAV path.";
                        return false;
                    }

                    output = args[index];
                    break;
                case "--ffmpeg":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--ffmpeg requires an executable path.";
                        return false;
                    }

                    ffmpeg = args[index];
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    options = default!;
                    error = $"Unknown convert option: {args[index]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            options = default!;
            error = "A --input audio path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            options = default!;
            error = "A --output WAV path is required.";
            return false;
        }

        options = new AudioConversionOptions(input, output)
        {
            FfmpegPath = ffmpeg,
            Force = force
        };
        error = string.Empty;
        return true;
    }

    private static async Task<int> WriteUsageErrorAsync(TextWriter error)
    {
        await error.WriteLineAsync(SmartToolCapabilityRegistry.RenderUsage()).ConfigureAwait(false);
        return 2;
    }

    private sealed record TranscribeOptions(
        IReadOnlyList<string> Inputs,
        TranscriptOutputFormat Format,
        string? OutputPath);
}
