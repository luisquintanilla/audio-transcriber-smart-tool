using AudioTranscriber.FoundryLocal;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal.Tests;

public sealed class FoundryLocalOptInTests
{
    [FoundryLocalFact]
    public async Task Real_runtime_readiness_is_opt_in_and_does_not_download_models()
    {
        var modelAlias = Environment.GetEnvironmentVariable(
            "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_MODEL");
        Assert.Equal(FoundryLocalModelContract.RequiredModelAlias, modelAlias);

        var runtime = new FoundryLocalSdkRuntime();
        FoundryLocalRuntimeSession? session = null;
        try
        {
            var options = new FoundryLocalEnrichmentOptions(
                FoundryLocalModelContract.RequiredModelAlias)
            {
                AllowModelDownload = false
            };

            var readiness = await runtime.CheckReadinessAsync(options);
            Assert.True(
                readiness.IsReady,
                $"{readiness.DiagnosticCode}: {readiness.Message}");

            try
            {
                session = await runtime.PrepareAsync(options);
            }
            catch (FoundryLocalProviderException exception)
                when (exception.DiagnosticCode is
                    FoundryLocalDiagnosticCode.RuntimeUnavailable or
                    FoundryLocalDiagnosticCode.ModelUnavailable or
                    FoundryLocalDiagnosticCode.ModelNotReady)
            {
                Assert.Fail(
                    $"Foundry Local opt-in prerequisites are unavailable: {exception.Message}");
            }
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    [FoundryLocalPodcastFact]
    public async Task Real_fourteen_chapter_podcast_artifact_can_be_enriched()
    {
        var inputPath = Environment.GetEnvironmentVariable(
            "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_PODCAST_ARTIFACT")!;
        var artifact = await new TranscriptChapterArtifactReader()
            .ReadFileAsync(inputPath);
        Assert.Equal(14, artifact.Count);

        var options = new FoundryLocalEnrichmentOptions(
            FoundryLocalModelContract.RequiredModelAlias)
        {
            ModelCacheDirectory = Environment.GetEnvironmentVariable(
                "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_CACHE"),
            AllowModelDownload = false
        };
        await using var provider = new FoundryLocalEnrichmentProvider(
            options,
            new FoundryLocalSdkRuntime());

        var result = await new TranscriptChapterEnrichmentOrchestrator(
                provider,
                provider)
            .EnrichAsync(
                artifact,
                provider.CreateProcessingOptions(
                    TranscriptChapterEnrichmentFailurePolicy.FailFast,
                    includeOverallSummary: true));

        Assert.Equal(14, result.Count);
        Assert.All(
            result,
            chapter => Assert.Equal(
                TranscriptChapterEnrichmentStatus.Succeeded,
                chapter.Status));
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class FoundryLocalPodcastFactAttribute : FactAttribute
    {
        public FoundryLocalPodcastFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_PODCAST_ARTIFACT")))
            {
                Skip =
                    "Set AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_PODCAST_ARTIFACT to run the opt-in 14-chapter podcast test.";
            }
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class FoundryLocalFactAttribute : FactAttribute
    {
        public FoundryLocalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(
                        "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_MODEL")))
            {
                Skip =
                    "Set AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_MODEL to run the opt-in Foundry Local test.";
            }
        }
    }
}
