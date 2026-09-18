using AudioTranscriber.FoundryLocal;

namespace AudioTranscriber.FoundryLocal.Tests;

public sealed class FoundryLocalOptInTests
{
    [FoundryLocalFact]
    public async Task Real_runtime_readiness_is_opt_in_and_does_not_download_models()
    {
        var modelAlias = Environment.GetEnvironmentVariable(
            "AUDIO_TRANSCRIBER_FOUNDRY_LOCAL_MODEL");

        var runtime = new FoundryLocalSdkRuntime();
        FoundryLocalRuntimeSession? session = null;
        try
        {
            var options = new FoundryLocalEnrichmentOptions(modelAlias!)
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
