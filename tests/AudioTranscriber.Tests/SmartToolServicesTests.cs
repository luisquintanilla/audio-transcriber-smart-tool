using System.Text.Json;

namespace AudioTranscriber.Tests;

public sealed class SmartToolServicesTests
{
    [Fact]
    public void Manifest_reads_canonical_frontmatter_in_stable_json()
    {
        var first = SmartToolManifestService.ToJson(SmartToolManifestService.Create());
        var second = SmartToolManifestService.ToJson(SmartToolManifestService.Create());

        Assert.Equal(first, second);
        Assert.Contains("\"smartToolFormat\": 1", first);
        Assert.Contains("\"name\": \"audio-transcriber\"", first);
        Assert.Contains("\"version\": \"0.1.0\"", first);
        Assert.Contains("\"useCases\"", first);
        Assert.Contains("\"platforms\"", first);
        Assert.DoesNotContain("catalogReadiness", first, StringComparison.OrdinalIgnoreCase);
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
            ["audio-transcriber"],
            root.RootElement.GetProperty("cli_argv").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["manifest"],
            root.RootElement.GetProperty("deterministic_smoke").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public void Canonical_manifest_reports_whisper_net_catalog_readiness()
    {
        var manifest = SmartToolManifestService.Markdown();

        Assert.Contains("smart_tool_format: 1", manifest);
        Assert.Contains("name: audio-transcriber", manifest);
        Assert.Contains("**Available.**", manifest);
        Assert.Contains("Whisper.net", manifest);
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
                    "  Validate local 16 kHz mono WAV recordings and run local Whisper Base",
                    "  transcription with deterministic timestamped transcript renderers.",
                    "use_cases:",
                    "  - Validate local WAV inputs against the supported 16 kHz mono contract",
                    "  - Transcribe local speech with real timestamped Whisper Base segments",
                    "  - Render typed transcripts as text, JSON, SRT, or WebVTT",
                    "  - Inspect deterministic model, cache, and package integration readiness",
                    "platforms:",
                    "  - windows"
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
            "Validate local 16 kHz mono WAV recordings and run local Whisper Base transcription with deterministic timestamped transcript renderers.",
            manifest.Description);
        Assert.Equal(
            [
                "Validate local WAV inputs against the supported 16 kHz mono contract",
                "Transcribe local speech with real timestamped Whisper Base segments",
                "Render typed transcripts as text, JSON, SRT, or WebVTT",
                "Inspect deterministic model, cache, and package integration readiness"
            ],
            manifest.UseCases);
        Assert.Equal(["windows"], manifest.Platforms);
    }

    [Fact]
    public void Manifest_json_has_canonical_property_order_and_payload()
    {
        var expected = string.Join(
                Environment.NewLine,
                [
                    "{",
                    "  \"smartToolFormat\": 1,",
                    "  \"name\": \"audio-transcriber\",",
                    "  \"version\": \"0.1.0\",",
                    "  \"description\": \"Validate local 16 kHz mono WAV recordings and run local Whisper Base transcription with deterministic timestamped transcript renderers.\",",
                    "  \"useCases\": [",
                    "    \"Validate local WAV inputs against the supported 16 kHz mono contract\",",
                    "    \"Transcribe local speech with real timestamped Whisper Base segments\",",
                    "    \"Render typed transcripts as text, JSON, SRT, or WebVTT\",",
                    "    \"Inspect deterministic model, cache, and package integration readiness\"",
                    "  ],",
                    "  \"platforms\": [",
                    "    \"windows\"",
                    "  ]",
                    "}"
                ]) +
            Environment.NewLine;

        var first = SmartToolManifestService.ToJson(SmartToolManifestService.Create());
        var second = SmartToolManifestService.ToJson(SmartToolManifestService.Create());

        Assert.Equal(expected, first);
        Assert.Equal(first, second);
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
