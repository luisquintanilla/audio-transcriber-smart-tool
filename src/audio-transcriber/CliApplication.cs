using System.Security;
using System.Text.Json;
using AudioTranscriber;
using AudioTranscriber.Granite;
using AudioTranscriber.TranscriptProcessing;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.Cli;

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
            await error.WriteLineAsync(SmartToolHelpRenderer.RenderUsage()).ConfigureAwait(false);
            return 2;
        }

        try
        {
            if (args is ["-h"])
            {
                await output.WriteAsync(SmartToolHelpRenderer.RenderShortHelp()).ConfigureAwait(false);
                return 0;
            }

            if (args is ["--help"])
            {
                await output.WriteAsync(SmartToolHelpRenderer.RenderToolHelp()).ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 2 &&
                (args[1] is "-h" or "--help") &&
                SmartToolCapabilityRegistry.TryGet(args[0], out _))
            {
                await output.WriteAsync(
                    SmartToolHelpRenderer.RenderCapabilityHelp(
                        args[0],
                        terse: args[1] == "-h")).ConfigureAwait(false);
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "manifest" when args.Length == 1 => await WriteManifestAsync(output).ConfigureAwait(false),
                "doctor" when args.Length == 1 => await WriteDoctorAsync(
                    output,
                    repositoryRoot ?? Directory.GetCurrentDirectory()).ConfigureAwait(false),
                "transcribe" => await TranscribeAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false),
                "convert" => await ConvertAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false),
                "chapters" => await ChaptersAsync(
                    args[1..],
                    output,
                    error,
                    repositoryRoot ?? Directory.GetCurrentDirectory(),
                    cancellationToken).ConfigureAwait(false),
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

    private static async Task<int> ChaptersAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (!TryParseChaptersOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"error: {parseError}").ConfigureAwait(false);
            return 2;
        }

        if (string.Equals(
                options.InputPath,
                options.OutputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            await error.WriteLineAsync(
                "error: chapter input and output must be different files: " +
                $"'{SafePathDisplay.Basename(options.InputPath)}'.")
                .ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(options.OutputPath) && !options.Overwrite)
        {
            await error.WriteLineAsync(
                "error: chapter output already exists; pass --overwrite to replace it explicitly.")
                .ConfigureAwait(false);
            return 1;
        }

        TranscriptDocument document;
        try
        {
            await using var input = new FileStream(
                options.InputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await new TranscriptJsonReader()
                .ReadDocumentAsync(input, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
            DirectoryNotFoundException or
            UnauthorizedAccessException or
            IOException or
            TranscriptFormatException)
        {
            var detail = exception is TranscriptFormatException formatException
                ? $" ({formatException.Code} at {formatException.JsonPath})"
                : string.Empty;
            throw new InvalidDataException(
                $"Transcript input could not be read{detail} from " +
                $"'{SafePathDisplay.Basename(options.InputPath)}'.",
                exception);
        }

        GraniteEmbeddingProvider? graniteProvider = null;
        try
        {
            IEmbeddingGenerator<TextContent, Embedding<float>>? embeddingGenerator = null;
            if (string.Equals(options.Provider, "granite", StringComparison.Ordinal))
            {
                var configuration = new GraniteModelConfiguration(
                    options.CachePath,
                    options.AllowNetworkDownload,
                    options.RequireAvx2,
                    repositoryRoot);
                graniteProvider = await GraniteEmbeddingProvider
                    .CreateAsync(
                        configuration,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                embeddingGenerator = graniteProvider;
            }

            var generationOptions = new TranscriptChapterGenerationOptions
            {
                Provider = options.Provider,
                MinimumDuration = options.MinimumDuration,
                MaximumDuration = options.MaximumDuration,
                ProviderConfiguration = CreateProviderConfiguration(options)
            };
            var documentGenerator = new TranscriptChapterGenerator(embeddingGenerator);
            var artifact = await documentGenerator
                .GenerateAsync(document, generationOptions, cancellationToken)
                .ConfigureAwait(false);
            await new TranscriptChapterArtifactFileWriter()
                .WriteAsync(
                    options.OutputPath,
                    artifact,
                    options.Overwrite,
                    cancellationToken)
                .ConfigureAwait(false);

            await output.WriteLineAsync(
                $"Wrote {artifact.Count} chapter(s) to " +
                $"'{SafePathDisplay.Basename(options.OutputPath)}' " +
                $"using {artifact.Generation.Provider}.").ConfigureAwait(false);
            return 0;
        }
        finally
        {
            graniteProvider?.Dispose();
        }
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

    private static bool TryParseChaptersOptions(
        string[] args,
        out ChaptersOptions options,
        out string error)
    {
        string? input = null;
        string? output = null;
        var provider = "deterministic";
        var minimumDuration = TimeSpan.FromSeconds(30);
        var maximumDuration = TimeSpan.FromMinutes(5);
        string? cachePath = null;
        var allowNetworkDownload = false;
        var requireAvx2 = false;
        var overwrite = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--input requires a transcript JSON path.";
                        return false;
                    }

                    input = Path.GetFullPath(args[index]);
                    break;
                case "--output":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--output requires a chapter JSON path.";
                        return false;
                    }

                    output = Path.GetFullPath(args[index]);
                    break;
                case "--provider":
                    if (++index >= args.Length ||
                        !TryParseChapterProvider(args[index], out provider))
                    {
                        options = default!;
                        error = "--provider must be deterministic or granite.";
                        return false;
                    }

                    break;
                case "--min-duration":
                case "--minimum-duration":
                    if (++index >= args.Length ||
                        !TryParseDuration(args[index], out minimumDuration))
                    {
                        options = default!;
                        error = "--min-duration must be a positive number of seconds.";
                        return false;
                    }

                    break;
                case "--max-duration":
                case "--maximum-duration":
                    if (++index >= args.Length ||
                        !TryParseDuration(args[index], out maximumDuration))
                    {
                        options = default!;
                        error = "--max-duration must be a positive number of seconds.";
                        return false;
                    }

                    break;
                case "--cache":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--cache requires an external Granite cache path.";
                        return false;
                    }

                    cachePath = Path.GetFullPath(args[index]);
                    break;
                case "--allow-network-download":
                    allowNetworkDownload = true;
                    break;
                case "--require-avx2":
                    requireAvx2 = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                default:
                    options = default!;
                    error = $"Unknown chapters option: {args[index]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            options = default!;
            error = "A --input transcript JSON path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            options = default!;
            error = "A --output chapter JSON path is required.";
            return false;
        }

        if (minimumDuration > maximumDuration)
        {
            options = default!;
            error = "--min-duration cannot exceed --max-duration.";
            return false;
        }

        if (!string.Equals(provider, "granite", StringComparison.Ordinal) &&
            (cachePath is not null || allowNetworkDownload || requireAvx2))
        {
            options = default!;
            error = "--cache, --allow-network-download, and --require-avx2 require --provider granite.";
            return false;
        }

        options = new ChaptersOptions(
            input,
            output,
            provider,
            minimumDuration,
            maximumDuration,
            cachePath,
            allowNetworkDownload,
            requireAvx2,
            overwrite);
        error = string.Empty;
        return true;
    }

    private static bool TryParseChapterProvider(string value, out string provider)
    {
        provider = value.Trim().ToLowerInvariant();
        return provider is "deterministic" or "granite";
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (!double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds <= 0)
        {
            duration = default;
            return false;
        }

        try
        {
            duration = TimeSpan.FromSeconds(seconds);
            return duration > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> CreateProviderConfiguration(
        ChaptersOptions options)
    {
        var configuration = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options.CachePath is not null)
        {
            configuration["cache"] = SafePathDisplay.Basename(options.CachePath);
        }

        if (options.AllowNetworkDownload)
        {
            configuration["allowNetworkDownload"] = "true";
        }

        if (options.RequireAvx2)
        {
            configuration["requireAvx2"] = "true";
        }

        return configuration;
    }

    private static async Task<int> WriteUsageErrorAsync(TextWriter error)
    {
        await error.WriteLineAsync(SmartToolHelpRenderer.RenderUsage()).ConfigureAwait(false);
        return 2;
    }

    private sealed record TranscribeOptions(
        IReadOnlyList<string> Inputs,
        TranscriptOutputFormat Format,
        string? OutputPath);

    private sealed record ChaptersOptions(
        string InputPath,
        string OutputPath,
        string Provider,
        TimeSpan MinimumDuration,
        TimeSpan MaximumDuration,
        string? CachePath,
        bool AllowNetworkDownload,
        bool RequireAvx2,
        bool Overwrite);
}
