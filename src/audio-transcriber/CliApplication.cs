using System.Security;
using System.Text.Json;
using AudioTranscriber;
using AudioTranscriber.FoundryLocal;
using AudioTranscriber.Granite;
using AudioTranscriber.TranscriptProcessing;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.Cli;

public sealed class CliApplication
{
    private static readonly StringComparison FilePathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly ITranscriptionEngine _engine;
    private readonly WavAudioReader _audioReader;
    private readonly IAudioConversionService _conversionService;
    private readonly Func<FoundryLocalEnrichmentOptions, IFoundryLocalRuntime>
        _foundryRuntimeFactory;
    private readonly Func<
        FoundryLocalEnrichmentOptions,
        IFoundryLocalRuntime,
        FoundryLocalEnrichmentProvider> _foundryProviderFactory;

    public CliApplication(
        ITranscriptionEngine? engine = null,
        WavAudioReader? audioReader = null,
        IAudioConversionService? conversionService = null,
        Func<FoundryLocalEnrichmentOptions, IFoundryLocalRuntime>? foundryRuntimeFactory = null,
        Func<
            FoundryLocalEnrichmentOptions,
            IFoundryLocalRuntime,
            FoundryLocalEnrichmentProvider>? foundryProviderFactory = null)
    {
        _engine = engine ?? new WhisperNetEngine();
        _audioReader = audioReader ?? new WavAudioReader();
        _conversionService = conversionService ?? new AudioConversionService();
        _foundryRuntimeFactory = foundryRuntimeFactory ??
            (_ => new FoundryLocalSdkRuntime());
        _foundryProviderFactory = foundryProviderFactory ??
            ((options, runtime) => new FoundryLocalEnrichmentProvider(options, runtime));
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
                "enrich" => await EnrichAsync(
                    args[1..],
                    output,
                    error,
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

        if (AreSameChapterInputAndOutput(options.InputPath, options.OutputPath))
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
                ? formatException.Code == "unsupported_schema_version"
                    ? $" ({formatException.Code}: " +
                      TranscriptChapterArtifactSchema.RegenerationGuidance + ")"
                    : $" ({formatException.Code} at {formatException.JsonPath})"
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
            try
            {
                await new TranscriptChapterArtifactFileWriter()
                    .WriteAsync(
                        options.OutputPath,
                        artifact,
                        options.Overwrite,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                DirectoryNotFoundException)
            {
                throw new IOException(
                    "Chapter output could not be written to " +
                    $"'{SafePathDisplay.Basename(options.OutputPath)}'.",
                    exception);
            }

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

    private async Task<int> EnrichAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseEnrichOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"error: {parseError}").ConfigureAwait(false);
            return 2;
        }

        if (AreSameChapterInputAndOutput(options.InputPath, options.OutputPath))
        {
            await error.WriteLineAsync(
                "error: enrichment input and output must be different files.")
                .ConfigureAwait(false);
            return 1;
        }

        if (File.Exists(options.OutputPath) && !options.Overwrite)
        {
            await error.WriteLineAsync(
                "error: enrichment output already exists; pass --overwrite to replace it explicitly.")
                .ConfigureAwait(false);
            return 1;
        }

        TranscriptChapterArtifactDocument artifact;
        try
        {
            artifact = await new TranscriptChapterArtifactReader()
                .ReadFileAsync(options.InputPath, cancellationToken)
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
                ? formatException.Code == "unsupported_schema_version"
                    ? $" ({formatException.Code}: " +
                      TranscriptChapterArtifactSchema.RegenerationGuidance + ")"
                    : $" ({formatException.Code} at {formatException.JsonPath})"
                : string.Empty;
            throw new InvalidDataException(
                $"Chapter artifact input could not be read{detail} from " +
                $"'{SafePathDisplay.Basename(options.InputPath)}'.",
                exception);
        }

        var providerOptions = new FoundryLocalEnrichmentOptions(options.Model)
        {
            ModelCacheDirectory = options.CachePath,
            AllowModelDownload = options.AllowModelDownload,
            RequestTimeout = options.RequestTimeout,
            MaxOutputTokens = options.MaxOutputTokens,
            Seed = options.Seed,
            DoSample = options.DoSample,
            Temperature = options.Temperature,
            MaxResponseAttempts = options.MaxResponseAttempts
        };
        providerOptions.Validate();
        var runtime = _foundryRuntimeFactory(providerOptions);
        await using var provider = _foundryProviderFactory(providerOptions, runtime);
        var document = await new TranscriptChapterEnrichmentOrchestrator(
                provider,
                provider)
            .EnrichAsync(
                artifact,
                provider.CreateProcessingOptions(
                    options.FailurePolicy,
                    options.IncludeOverallSummary),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await new TranscriptChapterEnrichmentArtifactFileWriter()
                .WriteAsync(
                    options.OutputPath,
                    document,
                    options.Overwrite,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            throw new IOException(
                "Enrichment output could not be written to " +
                $"'{SafePathDisplay.Basename(options.OutputPath)}'.",
                exception);
        }

        await output.WriteLineAsync(
            $"Wrote enrichment for {document.Count} chapter(s) to " +
            $"'{SafePathDisplay.Basename(options.OutputPath)}' using " +
            $"{FoundryLocalModelContract.RequiredModelAlias}.").ConfigureAwait(false);
        return 0;
    }

    private static bool AreSameChapterInputAndOutput(
        string inputPath,
        string outputPath)
    {
        if (string.Equals(inputPath, outputPath, FilePathComparison))
        {
            return true;
        }

        var input = new FileInfo(inputPath);
        var output = new FileInfo(outputPath);
        if (!input.Exists || !output.Exists)
        {
            return false;
        }

        return string.Equals(
            ResolveLinkTargetPath(input),
            ResolveLinkTargetPath(output),
            FilePathComparison);
    }

    private static string ResolveLinkTargetPath(FileInfo file) =>
        ResolveFinalLinkTargetPath(
            new FileInfo(
                Path.Combine(
                    ResolveDirectoryPath(file.Directory!),
                    file.Name)));

    private static string ResolveFinalLinkTargetPath(FileInfo file)
    {
        var target = file.ResolveLinkTarget(returnFinalTarget: true);
        return target is null
            ? file.FullName
            : ResolveLinkTargetPath(new FileInfo(target.FullName));
    }

    private static string ResolveDirectoryPath(DirectoryInfo directory)
    {
        var components = new Stack<string>();
        for (var current = directory; current.Parent is not null; current = current.Parent)
        {
            components.Push(current.Name);
        }

        var resolvedDirectory = directory.Root;
        while (components.Count > 0)
        {
            var candidate = new DirectoryInfo(
                Path.Combine(resolvedDirectory.FullName, components.Pop()));
            resolvedDirectory = candidate.ResolveLinkTarget(returnFinalTarget: true)
                as DirectoryInfo ?? candidate;
        }

        return resolvedDirectory.FullName;
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

    private static bool TryParseEnrichOptions(
        string[] args,
        out EnrichOptions options,
        out string error)
    {
        string? input = null;
        string? output = null;
        var model = FoundryLocalModelContract.RequiredModelAlias;
        string? cachePath = null;
        var allowModelDownload = false;
        var requestTimeout = TimeSpan.FromMinutes(2);
        var maxOutputTokens = 1200;
        var seed = 0;
        var doSample = false;
        var temperature = 0d;
        var maxResponseAttempts = 2;
        var failurePolicy = TranscriptChapterEnrichmentFailurePolicy.FailFast;
        var includeOverallSummary = false;
        var overwrite = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--input requires a chapter artifact JSON path.";
                        return false;
                    }

                    input = Path.GetFullPath(args[index]);
                    break;
                case "--output":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--output requires an enrichment JSON path.";
                        return false;
                    }

                    output = Path.GetFullPath(args[index]);
                    break;
                case "--model":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = $"--model requires '{FoundryLocalModelContract.RequiredModelAlias}'.";
                        return false;
                    }

                    model = args[index].Trim();
                    break;
                case "--cache":
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    {
                        options = default!;
                        error = "--cache requires an external model cache path.";
                        return false;
                    }

                    cachePath = Path.GetFullPath(args[index]);
                    break;
                case "--allow-model-download":
                case "--allow-download":
                    allowModelDownload = true;
                    break;
                case "--timeout":
                    if (++index >= args.Length ||
                        !TryParseDuration(args[index], out requestTimeout))
                    {
                        options = default!;
                        error = "--timeout must be a positive number of seconds.";
                        return false;
                    }

                    break;
                case "--tokens":
                case "--max-output-tokens":
                    if (++index >= args.Length ||
                        !int.TryParse(
                            args[index],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out maxOutputTokens) ||
                        maxOutputTokens <= 0)
                    {
                        options = default!;
                        error = "--tokens must be a positive integer.";
                        return false;
                    }

                    break;
                case "--seed":
                    if (++index >= args.Length ||
                        !int.TryParse(
                            args[index],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out seed) ||
                        seed < 0)
                    {
                        options = default!;
                        error = "--seed must be a non-negative integer.";
                        return false;
                    }

                    break;
                case "--sampling":
                case "--do-sample":
                    if (++index >= args.Length ||
                        !bool.TryParse(args[index], out doSample))
                    {
                        options = default!;
                        error = "--sampling must be true or false.";
                        return false;
                    }

                    break;
                case "--temperature":
                    if (++index >= args.Length ||
                        !double.TryParse(
                            args[index],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out temperature) ||
                        !double.IsFinite(temperature) ||
                        temperature < 0 ||
                        temperature > 2)
                    {
                        options = default!;
                        error = "--temperature must be between zero and two.";
                        return false;
                    }

                    break;
                case "--max-response-attempts":
                    if (++index >= args.Length ||
                        !int.TryParse(
                            args[index],
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out maxResponseAttempts) ||
                        maxResponseAttempts is < 1 or > 2)
                    {
                        options = default!;
                        error = "--max-response-attempts must be one or two.";
                        return false;
                    }

                    break;
                case "--failure-policy":
                    if (++index >= args.Length ||
                        !TryParseFailurePolicy(args[index], out failurePolicy))
                    {
                        options = default!;
                        error = "--failure-policy must be fail-fast or preserve-partial.";
                        return false;
                    }

                    break;
                case "--overall-summary":
                    includeOverallSummary = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                default:
                    options = default!;
                    error = $"Unknown enrich option: {args[index]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            options = default!;
            error = "A --input chapter artifact JSON path is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            options = default!;
            error = "A --output enrichment JSON path is required.";
            return false;
        }

        options = new EnrichOptions(
            input,
            output,
            model,
            cachePath,
            allowModelDownload,
            requestTimeout,
            maxOutputTokens,
            seed,
            doSample,
            temperature,
            maxResponseAttempts,
            failurePolicy,
            includeOverallSummary,
            overwrite);
        error = string.Empty;
        return true;
    }

    private static bool TryParseFailurePolicy(
        string value,
        out TranscriptChapterEnrichmentFailurePolicy failurePolicy)
    {
        if (value.Equals("fail-fast", StringComparison.OrdinalIgnoreCase))
        {
            failurePolicy = TranscriptChapterEnrichmentFailurePolicy.FailFast;
            return true;
        }

        if (value.Equals("preserve-partial", StringComparison.OrdinalIgnoreCase))
        {
            failurePolicy = TranscriptChapterEnrichmentFailurePolicy.PreservePartial;
            return true;
        }

        return Enum.TryParse(
                value,
                ignoreCase: true,
                out failurePolicy) &&
            Enum.IsDefined(failurePolicy);
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

    private sealed record EnrichOptions(
        string InputPath,
        string OutputPath,
        string Model,
        string? CachePath,
        bool AllowModelDownload,
        TimeSpan RequestTimeout,
        int MaxOutputTokens,
        int Seed,
        bool DoSample,
        double Temperature,
        int MaxResponseAttempts,
        TranscriptChapterEnrichmentFailurePolicy FailurePolicy,
        bool IncludeOverallSummary,
        bool Overwrite);
}
