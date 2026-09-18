using System.Text.Json;
using AudioTranscriber.Cli;

namespace AudioTranscriber.Tests;

public sealed class SmartToolServicesTests
{
    [Fact]
    public void Capability_registry_exposes_all_current_commands_with_classification()
    {
        Assert.Equal(
            ["manifest", "doctor", "convert", "transcribe"],
            SmartToolCapabilityRegistry.All.Select(capability => capability.Name));
        Assert.Equal(
            [
                SmartToolCapabilityKind.Deterministic,
                SmartToolCapabilityKind.Deterministic,
                SmartToolCapabilityKind.Deterministic,
                SmartToolCapabilityKind.ModelBacked
            ],
            SmartToolCapabilityRegistry.All.Select(capability => capability.Kind));
    }

    [Fact]
    public void Capability_registry_lookup_is_nullable_safe_for_missing_capabilities()
    {
        Assert.False(SmartToolCapabilityRegistry.TryGet("missing", out SmartToolCapability? missing));
        Assert.Null(missing);
        Assert.True(SmartToolCapabilityRegistry.TryGet("manifest", out var manifest));
        Assert.Equal("manifest", manifest.Name);
    }

    [Fact]
    public void Capability_registry_renders_tool_skill_from_manifest_body()
    {
        var help = SmartToolHelpRenderer.RenderToolHelp();

        Assert.Contains("<skill_content name=\"audio-transcriber\">", help);
        Assert.Contains("# audio-transcriber", help);
        Assert.Contains("audio-transcriber transcribe --help", help);
        Assert.Contains("<file>src/AudioTranscriber/SMART_TOOL.md</file>", help);
        Assert.Contains("[model-backed]", help);
        Assert.DoesNotContain("smart_tool_format:", help);
    }

    [Fact]
    public void Capability_registry_renders_short_help_without_capability_skill_details()
    {
        var help = SmartToolHelpRenderer.RenderShortHelp();

        Assert.Contains("Capabilities:", help);
        Assert.Contains("manifest   [deterministic]", help);
        Assert.Contains("transcribe [model-backed]", help);
        Assert.DoesNotContain("## Arguments", help);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("doctor")]
    [InlineData("convert")]
    [InlineData("transcribe")]
    public void Capability_registry_skills_document_invocation_contract(string capabilityName)
    {
        var skill = SmartToolHelpRenderer.RenderCapabilityHelp(capabilityName);

        Assert.Contains("## When to use", skill);
        Assert.Contains("## Determinism", skill);
        Assert.Contains("## Arguments", skill);
        Assert.Contains("## Worked invocation", skill);
        Assert.Contains("## Result", skill);
        Assert.Contains("## Failures", skill);
        Assert.Contains(capabilityName, skill, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_reports_cache_inside_repository_as_unhealthy()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        var cache = Path.Combine(repository, "cache");

        var report = new DoctorService().Run(repository, cache);

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks, check => check.Name == "model-cache" && check.Status == "fail");
    }

    [Fact]
    public void Doctor_keeps_default_cache_outside_repository_and_reports_backend_status()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");

        var report = new DoctorService().Run(repository);

        Assert.True(SmartToolPaths.IsOutsideRepository(repository, SmartToolPaths.DefaultModelCacheDirectory));
        Assert.Equal("<repository>", report.RepositoryRoot);
        Assert.Equal("<model-cache>", report.ModelCacheDirectory);
        Assert.Contains(report.Checks, check => check.Name == "whisper-net" && check.Status == "pass");
        Assert.Contains(
            report.Checks,
            check => check.Name == "ffmpeg" && (check.Status == "pass" || check.Status == "blocked"));
        Assert.Contains(report.Checks, check => check.Name == "whisper-model-garden" && check.Status == "blocked");
    }

    [Fact]
    public void Root_descriptor_points_to_canonical_manifest_and_smoke_capability()
    {
        using var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "smart-tool.json")));

        Assert.Equal(
            ["manifest", "cli_argv", "deterministic_smoke"],
            root.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("src/AudioTranscriber/SMART_TOOL.md", root.RootElement.GetProperty("manifest").GetString());
        Assert.Equal(
            ["dotnet", "run", "--project", "src/audio-transcriber/audio-transcriber.csproj", "--no-restore", "--"],
            root.RootElement.GetProperty("cli_argv").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["manifest"],
            root.RootElement.GetProperty("deterministic_smoke").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public void Descriptor_launch_recipe_targets_the_pack_as_tool_project()
    {
        using var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "smart-tool.json")));
        var cliArguments = root.RootElement.GetProperty("cli_argv")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

        Assert.Equal("dotnet", cliArguments[0]);
        Assert.Contains("--project", cliArguments);
        Assert.Contains("src/audio-transcriber/audio-transcriber.csproj", cliArguments);
        Assert.Contains("--no-restore", cliArguments);
        Assert.Equal("--", cliArguments[^1]);
    }

    [Fact]
    public void Pack_as_tool_project_carries_the_canonical_manifest_and_descriptor()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(
            Path.Combine(root, "src", "audio-transcriber", "audio-transcriber.csproj"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("<PackAsTool>true</PackAsTool>", project);
        Assert.Contains("<Version>0.1.0</Version>", project);
        Assert.Contains("PackagePath=\"src\\AudioTranscriber\\SMART_TOOL.md\"", project);
        Assert.Contains("PackagePath=\"smart-tool.json\"", project);
        Assert.Contains("does not consume a Git URL directly", readme);
        Assert.Contains("<RepositoryUrl>https://github.com/luisquintanilla/audio-transcriber-smart-tool</RepositoryUrl>", project);
        Assert.Contains("Git installation remains supported", readme);
        Assert.Contains("https://nuget.pkg.github.com/luisquintanilla/index.json", readme);
        Assert.Contains("GitHub requires authentication even for public NuGet packages", readme);
    }

    [Fact]
    public void Cli_adapter_lives_in_the_pack_as_tool_project_not_the_reusable_library()
    {
        var root = FindRepositoryRoot();
        var libraryProject = File.ReadAllText(
            Path.Combine(root, "src", "AudioTranscriber", "AudioTranscriber.csproj"));
        var cliProject = File.ReadAllText(
            Path.Combine(root, "src", "audio-transcriber", "audio-transcriber.csproj"));

        Assert.False(File.Exists(Path.Combine(root, "src", "AudioTranscriber", "CliApplication.cs")));
        Assert.True(File.Exists(Path.Combine(root, "src", "audio-transcriber", "CliApplication.cs")));
        Assert.True(File.Exists(Path.Combine(root, "src", "audio-transcriber", "SmartToolHelpRenderer.cs")));
        Assert.DoesNotContain("CliApplication", libraryProject, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=\"..\\AudioTranscriber\\AudioTranscriber.csproj\" />", cliProject);
    }

    [Fact]
    public void Optional_agent_skill_defers_capability_details_to_tool_help()
    {
        var skill = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "skills", "audio-transcriber", "SKILL.md"));

        Assert.Contains("name: audio-transcriber", skill);
        Assert.Contains("audio-transcriber --help", skill);
        Assert.Contains("does not consume a Git URL directly", skill);
        Assert.DoesNotContain("## Arguments", skill);
        Assert.DoesNotContain("## Failures", skill);
    }

    [Fact]
    public void Canonical_manifest_reports_whisper_net_integration()
    {
        var manifest = SmartToolManifestService.Markdown();

        Assert.Contains("smart_tool_format: 1", manifest);
        Assert.Contains("name: audio-transcriber", manifest);
        Assert.Contains("## Model integration status", manifest);
        Assert.Contains("Whisper.net", manifest);
    }

    [Fact]
    public void Canonical_manifest_defines_manifest_and_doctor_semantics()
    {
        var manifest = SmartToolManifestService.Markdown();

        Assert.Contains("`manifest` prints the canonical `SMART_TOOL.md` document", manifest);
        Assert.Contains("`doctor` checks cache placement and local integration readiness", manifest);
    }

    [Fact]
    public void Canonical_manifest_documents_sdk_outside_environment_requires()
    {
        var manifest = SmartToolManifestService.Create();

        Assert.DoesNotContain(manifest.Requires, requirement => requirement.Name == ".NET 10 SDK");
        Assert.Contains(".NET 10 SDK is required", manifest.Markdown);
        Assert.Contains("dotnetup sdk install 10.0", manifest.Markdown);
    }

    [Fact]
    public void Canonical_manifest_uses_exact_yaml_frontmatter()
    {
        var normalized = SmartToolManifestService.Markdown().Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var closingMarker = Array.IndexOf(lines, "---", 1);

        Assert.Equal("---", lines[0]);
        Assert.True(closingMarker > 1);
        Assert.Equal(
            string.Join(
                '\n',
                [
                    "---",
                    "smart_tool_format: 1",
                    "name: audio-transcriber",
                    "version: 0.1.0",
                    "description: >",
                    "  Prepare local audio for Whisper transcription and run timestamped local",
                    "  speech recognition with explicit deterministic conversion and diagnostics.",
                    "use_cases:",
                    "  - Prepare local audio for speech recognition",
                    "  - Check whether a WAV file meets the transcription input contract",
                    "  - Produce timestamped transcripts from local speech recordings",
                    "  - Render transcripts for people or downstream programs",
                    "  - Check local model, cache, FFmpeg, and package readiness",
                    "platforms:",
                    "  - windows",
                    "requires:",
                    "  - name: ffmpeg",
                    "    purpose: Required only by the deterministic convert capability; other capabilities remain available without it.",
                    "    optional: true",
                    "    install: https://ffmpeg.org/download.html",
                    "  - name: network access",
                    "    purpose: Needed by the model-backed transcribe capability when the verified Whisper Base artifact is not already cached.",
                    "    optional: true",
                    "    install: https://huggingface.co/sandrohanea/whisper.net"
                ]),
            string.Join('\n', lines[..closingMarker]));
    }

    [Fact]
    public void Manifest_exposes_exact_stable_fields()
    {
        var manifest = SmartToolManifestService.Create();

        Assert.Equal(1, manifest.SmartToolFormat);
        Assert.Equal("audio-transcriber", manifest.Name);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(
            "Prepare local audio for Whisper transcription and run timestamped local speech recognition with explicit deterministic conversion and diagnostics.",
            manifest.Description);
        Assert.Equal(
            [
                "Prepare local audio for speech recognition",
                "Check whether a WAV file meets the transcription input contract",
                "Produce timestamped transcripts from local speech recordings",
                "Render transcripts for people or downstream programs",
                "Check local model, cache, FFmpeg, and package readiness"
            ],
            manifest.UseCases);
        Assert.Equal(["windows"], manifest.Platforms);
        Assert.Equal(
            [
                new SmartToolRequirement(
                    "ffmpeg",
                    "Required only by the deterministic convert capability; other capabilities remain available without it.",
                    "https://ffmpeg.org/download.html",
                    true),
                new SmartToolRequirement(
                    "network access",
                    "Needed by the model-backed transcribe capability when the verified Whisper Base artifact is not already cached.",
                    "https://huggingface.co/sandrohanea/whisper.net",
                    true)
            ],
            manifest.Requires);
    }

    [Fact]
    public void Doctor_reports_external_cache_as_healthy_without_creating_or_repairing_paths()
    {
        var repository = Path.Combine(Path.GetTempPath(), $"repo-{Guid.NewGuid():N}");
        var cache = Path.Combine(Path.GetTempPath(), $"cache-{Guid.NewGuid():N}");

        var report = new DoctorService().Run(repository, cache);

        Assert.True(report.Healthy);
        Assert.Equal("<repository>", report.RepositoryRoot);
        Assert.Equal("<model-cache>", report.ModelCacheDirectory);

        var cacheCheck = Assert.Single(report.Checks, check => check.Name == "model-cache");
        Assert.Equal("pass", cacheCheck.Status);
        Assert.Contains("outside the repository", cacheCheck.Detail);
        Assert.DoesNotContain(Path.GetFullPath(repository), cacheCheck.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(cache), cacheCheck.Detail, StringComparison.OrdinalIgnoreCase);

        var integrationCheck = Assert.Single(report.Checks, check => check.Name == "whisper-model-garden");
        Assert.Equal("blocked", integrationCheck.Status);
        Assert.Contains(ModelGardenIntegration.PackageRestoreBlocker, integrationCheck.Detail);
        Assert.Contains("Package restore is opt-in and not attempted by doctor.", integrationCheck.Detail);
        Assert.False(Directory.Exists(repository));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public void Doctor_rejects_an_empty_repository_root_before_running_checks()
    {
        var exception = Assert.Throws<ArgumentException>(() => new DoctorService().Run(" "));

        Assert.Equal("repositoryRoot", exception.ParamName);
        Assert.Contains("Repository root cannot be empty.", exception.Message);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "smart-tool.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
