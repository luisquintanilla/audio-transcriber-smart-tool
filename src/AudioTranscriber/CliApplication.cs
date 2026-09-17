using System.Text.Json;
using System.Security;

namespace AudioTranscriber;

public sealed class CliApplication
{
    private readonly ITranscriptionEngine _engine;
    private readonly WavAudioReader _audioReader;

    public CliApplication(ITranscriptionEngine? engine = null, WavAudioReader? audioReader = null)
    {
        _engine = engine ?? new WhisperNetEngine();
        _audioReader = audioReader ?? new WavAudioReader();
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
            await error.WriteLineAsync(Usage()).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (args is ["-h"] or ["--help"])
            {
                await output.WriteAsync(SmartToolManifestService.Markdown()).ConfigureAwait(false);
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "manifest" when args.Length == 1 => await WriteManifestAsync(output).ConfigureAwait(false),
                "status" when args.Length == 1 => await WriteStatusAsync(output).ConfigureAwait(false),
                "doctor" when args.Length == 1 => await WriteDoctorAsync(
                    output,
                    repositoryRoot ?? Directory.GetCurrentDirectory()).ConfigureAwait(false),
                "transcribe" when args.Length == 2 && (args[1] is "-h" or "--help") =>
                    await WriteTranscribeHelpAsync(output).ConfigureAwait(false),
                "transcribe" => await TranscribeAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false),
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

    private static async Task<int> WriteTranscribeHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync(
            """
            Transcribe local 16 kHz mono WAV files with the configured local Whisper model.

            Usage:
              audio-transcriber transcribe --input <file.wav> [--input <file.wav>] [--format text|json|srt|webvtt] [--output <path>]

            The capability is model-backed and fails explicitly when the model integration
            is unavailable. It never prompts and never silently returns a degraded result.
            """).ConfigureAwait(false);
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

    private static async Task<int> WriteUsageErrorAsync(TextWriter error)
    {
        await error.WriteLineAsync(Usage()).ConfigureAwait(false);
        return 2;
    }

    private static string Usage() =>
        """
        Usage:
          audio-transcriber -h|--help
          audio-transcriber manifest
          audio-transcriber status
          audio-transcriber doctor
          audio-transcriber transcribe --input <file.wav> [--input <file.wav>] [--format text|json|srt|webvtt] [--output <path>]
        """;

    private sealed record TranscribeOptions(
        IReadOnlyList<string> Inputs,
        TranscriptOutputFormat Format,
        string? OutputPath);
}
