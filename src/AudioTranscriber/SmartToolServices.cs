namespace AudioTranscriber;

public static class SmartToolPaths
{
    public const string ToolId = "audio-transcriber";
    public const string ToolVersion = "0.1.0";

    public static string DefaultModelCacheDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioTranscriber",
            "model-cache");

    public static bool IsOutsideRepository(string repositoryRoot, string candidatePath)
    {
        var repository = EnsureTrailingSeparator(Path.GetFullPath(repositoryRoot));
        var candidate = Path.GetFullPath(candidatePath);
        return !candidate.StartsWith(repository, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

public sealed record SmartToolManifest
{
    public required int SmartToolFormat { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> UseCases { get; init; }
    public required IReadOnlyList<string> Platforms { get; init; }
    public required IReadOnlyList<SmartToolRequirement> Requires { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Markdown { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Body { get; init; } = string.Empty;
}

public sealed record SmartToolRequirement(
    string Name,
    string Purpose,
    string Install,
    bool Optional = false);

public static class SmartToolManifestService
{
    private static readonly Lazy<SmartToolManifest> Manifest = new(Parse);

    public static SmartToolManifest Create() => Manifest.Value;

    public static string Markdown() => Manifest.Value.Markdown;

    public static string Body() => Manifest.Value.Body;

    private static SmartToolManifest Parse()
    {
        var markdown = ReadManifestResource();
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException("SMART_TOOL.md must contain YAML frontmatter delimited by ---.");
        }

        var closingMarker = normalized.IndexOf("\n---\n", "---\n".Length, StringComparison.Ordinal);
        if (closingMarker < 0)
        {
            throw new InvalidDataException("SMART_TOOL.md must contain YAML frontmatter delimited by ---.");
        }

        var frontmatter = normalized["---\n".Length..closingMarker].Split('\n');
        var smartToolFormat = 0;
        string? name = null;
        string? version = null;
        string? description = null;
        var useCases = new List<string>();
        var platforms = new List<string>();
        var requirements = new List<SmartToolRequirement>();

        for (var index = 0; index < frontmatter.Length; index++)
        {
            var line = frontmatter[index];
            if (line.StartsWith("smart_tool_format:", StringComparison.Ordinal))
            {
                smartToolFormat = ParseInt(line, "smart_tool_format");
            }
            else if (line.StartsWith("name:", StringComparison.Ordinal))
            {
                name = ParseScalar(line, "name");
            }
            else if (line.StartsWith("version:", StringComparison.Ordinal))
            {
                version = ParseScalar(line, "version");
            }
            else if (line.StartsWith("description:", StringComparison.Ordinal))
            {
                var value = line["description:".Length..].Trim();
                if (value == ">")
                {
                    var lines = new List<string>();
                    while (index + 1 < frontmatter.Length &&
                           frontmatter[index + 1].StartsWith("  ", StringComparison.Ordinal))
                    {
                        lines.Add(frontmatter[++index].Trim());
                    }

                    description = string.Join(' ', lines);
                }
                else
                {
                    description = value;
                }
            }
            else if (line == "use_cases:")
            {
                ReadList(frontmatter, ref index, useCases);
            }
            else if (line == "platforms:")
            {
                ReadList(frontmatter, ref index, platforms);
            }
            else if (line == "requires:")
            {
                ReadRequirements(frontmatter, ref index, requirements);
            }
        }

        if (smartToolFormat == 0 ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(description) ||
            useCases.Count == 0 ||
            platforms.Count == 0)
        {
            throw new InvalidDataException("SMART_TOOL.md is missing required frontmatter fields.");
        }

        var body = normalized[(closingMarker + "\n---\n".Length)..].TrimStart('\n');

        return new SmartToolManifest
        {
            SmartToolFormat = smartToolFormat,
            Name = name,
            Version = version,
            Description = description,
            UseCases = useCases,
            Platforms = platforms,
            Requires = requirements,
            Markdown = markdown,
            Body = body
        };
    }

    private static string ReadManifestResource()
    {
        var assembly = typeof(SmartToolManifestService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(".SMART_TOOL.md", StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidDataException("The canonical SMART_TOOL.md resource is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException("The canonical SMART_TOOL.md resource could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void ReadList(string[] lines, ref int index, ICollection<string> values)
    {
        while (index + 1 < lines.Length &&
               lines[index + 1].StartsWith("  - ", StringComparison.Ordinal))
        {
            values.Add(lines[++index]["  - ".Length..].Trim());
        }
    }

    private static void ReadRequirements(
        string[] lines,
        ref int index,
        ICollection<SmartToolRequirement> values)
    {
        while (index + 1 < lines.Length &&
               lines[index + 1].StartsWith("  - name:", StringComparison.Ordinal))
        {
            var name = ParseValue(lines[++index], "  - name");
            string? purpose = null;
            string? install = null;
            var optional = false;

            while (index + 1 < lines.Length &&
                   lines[index + 1].StartsWith("    ", StringComparison.Ordinal))
            {
                var line = lines[++index].TrimStart();
                if (line.StartsWith("purpose:", StringComparison.Ordinal))
                {
                    purpose = ParseValue(line, "purpose");
                }
                else if (line.StartsWith("install:", StringComparison.Ordinal))
                {
                    install = ParseValue(line, "install");
                }
                else if (line.StartsWith("optional:", StringComparison.Ordinal))
                {
                    var value = ParseValue(line, "optional");
                    if (!bool.TryParse(value, out optional))
                    {
                        throw new InvalidDataException(
                            "SMART_TOOL.md requirement 'optional' fields must be boolean.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(purpose) ||
                string.IsNullOrWhiteSpace(install))
            {
                throw new InvalidDataException(
                    "SMART_TOOL.md requirement entries must include name, purpose, and install.");
            }

            values.Add(new SmartToolRequirement(name, purpose, install, optional));
        }
    }

    private static int ParseInt(string line, string key) =>
        int.TryParse(ParseScalar(line, key), out var value)
            ? value
            : throw new InvalidDataException($"SMART_TOOL.md field '{key}' must be an integer.");

    private static string ParseScalar(string line, string key)
    {
        return ParseValue(line, key);
    }

    private static string ParseValue(string line, string key)
    {
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SMART_TOOL.md field '{key}' is malformed.");
        }

        return line[prefix.Length..].Trim().Trim('"', '\'');
    }
}

public sealed class DoctorService
{
    private readonly IFfmpegExecutableResolver _ffmpegResolver;

    public DoctorService(IFfmpegExecutableResolver? ffmpegResolver = null)
    {
        _ffmpegResolver = ffmpegResolver ?? new FfmpegExecutableResolver();
    }

    public DoctorReport Run(string repositoryRoot, string? modelCacheDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            throw new ArgumentException("Repository root cannot be empty.", nameof(repositoryRoot));
        }

        var fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        var cacheDirectory = Path.GetFullPath(modelCacheDirectory ?? SmartToolPaths.DefaultModelCacheDirectory);
        var cacheOutsideRepository = SmartToolPaths.IsOutsideRepository(fullRepositoryRoot, cacheDirectory);
        var ffmpeg = _ffmpegResolver.Resolve(null);

        var checks = new[]
        {
            new IntegrationCheck(
                "model-cache",
                cacheOutsideRepository ? "pass" : "fail",
                cacheOutsideRepository
                    ? $"Model cache is outside the repository: {SafePathDisplay.ModelCacheToken}"
                    : $"Model cache must be outside the repository: {SafePathDisplay.ModelCacheToken}"),
            new IntegrationCheck(
                "whisper-net",
                "pass",
                $"Whisper.net {WhisperNetIntegration.PackageVersion} is configured for {WhisperNetIntegration.ModelId} ({WhisperNetIntegration.ModelVersion}); model downloads are verified outside the repository."),
            new IntegrationCheck(
                "ffmpeg",
                ffmpeg.IsAvailable ? "pass" : "blocked",
                ffmpeg.IsAvailable
                    ? $"{ffmpeg.Detail} The convert command can prepare audio for transcription."
                    : $"{ffmpeg.Detail} The convert command requires this external prerequisite; no binary is downloaded."),
            new IntegrationCheck(
                "whisper-model-garden",
                "blocked",
                $"{ModelGardenIntegration.PackageRestoreBlocker} Package restore is opt-in and not attempted by doctor.")
        };

        return new DoctorReport(
            Healthy: cacheOutsideRepository,
            RepositoryRoot: SafePathDisplay.RepositoryToken,
            ModelCacheDirectory: SafePathDisplay.ModelCacheToken,
            Checks: checks);
    }
}
