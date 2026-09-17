namespace AudioTranscriber.Tests;

public sealed class ModelGardenIntegrationTests
{
    [Fact]
    public void Integration_metadata_pins_dependencies_and_records_timestamp_capability()
    {
        Assert.Equal("0.1.0", ModelGardenIntegration.PackageVersion);
        Assert.Equal("0.1.0-preview.15", ModelGardenIntegration.ModelPackagesVersion);
        Assert.Equal("0.1.0-preview.2", ModelGardenIntegration.AudioInferenceVersion);
        Assert.Contains("TranscribeWithTimestamps", ModelGardenIntegration.TimestampApiStatus);
    }

    [Fact]
    public async Task Engine_fails_explicitly_when_optional_package_restore_is_unavailable()
    {
        var audio = new AudioClip(
            "test.wav",
            AudioRequirements.RequiredSampleRate,
            AudioRequirements.RequiredChannels,
            AudioSampleFormat.Pcm16,
            [0.0f]);

        var exception = await Assert.ThrowsAsync<ModelIntegrationUnavailableException>(async () =>
            await new ModelGardenWhisperEngine().TranscribeAsync(audio));

        Assert.Contains("requires authentication", exception.Message);
    }

    [Fact]
    public void Integration_metadata_is_opt_in_version_pinned_and_provenance_is_deterministic()
    {
        Assert.Equal("DotnetAILab.ModelGarden.ASR.WhisperBase", ModelGardenIntegration.PackageId);
        Assert.Equal("https://nuget.pkg.github.com/luisquintanilla/index.json", ModelGardenIntegration.PackageFeed);
        Assert.Equal("openai/whisper-base", ModelGardenIntegration.ModelId);
        Assert.Equal("c8f5f3a2127124463a7fcdb7acfcc787f7fe5824", ModelGardenIntegration.SourceCommit);

        var sourceProject = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "AudioTranscriber", "AudioTranscriber.csproj"));

        Assert.Contains(
            "<EnableWhisperModelGarden Condition=\"'$(EnableWhisperModelGarden)' == ''\">false</EnableWhisperModelGarden>",
            sourceProject);
        Assert.Contains(
            "<PackageReference Include=\"DotnetAILab.ModelGarden.ASR.WhisperBase\" Version=\"$(WhisperBasePackageVersion)\" />",
            sourceProject);
        Assert.Contains(
            "<PackageReference Include=\"ModelPackages\" Version=\"$(ModelPackagesVersion)\" />",
            sourceProject);
        Assert.Contains(
            "<PackageReference Include=\"MLNet.AudioInference.Onnx\" Version=\"$(MLNetAudioInferenceVersion)\" />",
            sourceProject);

        var cachePath = Path.Combine(Path.GetTempPath(), "audio-transcriber-test-cache");
        var provenance = ModelGardenIntegration.Provenance(cachePath);

        Assert.Equal("DotnetAILab.ModelGarden", provenance.Provider);
        Assert.Equal(ModelGardenIntegration.ModelId, provenance.Model);
        Assert.Equal(ModelGardenIntegration.PackageId, provenance.PackageId);
        Assert.Equal(ModelGardenIntegration.PackageVersion, provenance.PackageVersion);
        Assert.Equal(cachePath, provenance.CachePath);
        Assert.Contains(ModelGardenIntegration.SourceCommit, provenance.Source);
        Assert.Contains(ModelGardenIntegration.PackageFeed, provenance.Source);
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
