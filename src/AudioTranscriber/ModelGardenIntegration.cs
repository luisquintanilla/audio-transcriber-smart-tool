namespace AudioTranscriber;

public static class ModelGardenIntegration
{
    public const string PackageId = "DotnetAILab.ModelGarden.ASR.WhisperBase";
    public const string PackageVersion = "0.1.0";
    public const string ModelPackagesVersion = "0.1.0-preview.15";
    public const string AudioInferenceVersion = "0.1.0-preview.2";
    public const string PackageFeed = "https://nuget.pkg.github.com/luisquintanilla/index.json";
    public const string ModelId = "openai/whisper-base";
    public const string SourceCommit = "c8f5f3a2127124463a7fcdb7acfcc787f7fe5824";

    public const string TimestampApiStatus =
        "The pinned MLNet.AudioInference.Onnx source exposes OnnxWhisperTransformer.TranscribeWithTimestamps(IReadOnlyList<AudioData>) with model-derived segment timestamps.";

    public const string PackageRestoreBlocker =
        "The optional Model Garden packages cannot be cleanly restored from the configured GitHub Packages feed because it requires authentication (HTTP 401).";

    public static ModelProvenance Provenance(string cachePath) =>
        new(
            provider: "DotnetAILab.ModelGarden",
            model: ModelId,
            packageId: PackageId,
            packageVersion: PackageVersion,
            source: $"GitHub source commit {SourceCommit}; feed {PackageFeed}",
            cachePath: cachePath);
}

public sealed class ModelGardenWhisperEngine : ITranscriptionEngine
{
    public ModelProvenance Provenance { get; } = ModelGardenIntegration.Provenance(
        SmartToolPaths.DefaultModelCacheDirectory);

    public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioClip audio,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        cancellationToken.ThrowIfCancellationRequested();
        throw new ModelIntegrationUnavailableException(ModelGardenIntegration.PackageRestoreBlocker);
    }
}

public sealed record IntegrationCheck(
    string Name,
    string Status,
    string Detail);

public sealed record DoctorReport(
    bool Healthy,
    string RepositoryRoot,
    string ModelCacheDirectory,
    IReadOnlyList<IntegrationCheck> Checks);
