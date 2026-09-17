using System.Net;

namespace AudioTranscriber.Tests;

public sealed class WhisperNetIntegrationTests
{
    [Fact]
    public void Provenance_pins_public_package_model_variant_and_checksum()
    {
        var cache = Path.Combine(Path.GetTempPath(), "audio-transcriber-cache");
        var provenance = WhisperNetIntegration.Provenance(cache);

        Assert.Equal("Whisper.net/whisper.cpp", provenance.Provider);
        Assert.Equal("openai/whisper-base", provenance.Model);
        Assert.Equal("Whisper.net;Whisper.net.Runtime", provenance.PackageId);
        Assert.Equal("1.9.0", provenance.PackageVersion);
        Assert.Equal(cache, provenance.CachePath);
        Assert.Contains("upstreamModelFamily=openai/whisper-base", provenance.Source);
        Assert.Contains("artifactRepository=sandrohanea/whisper.net", provenance.Source);
        Assert.Contains("v5/classic", provenance.Source);
        Assert.Contains("artifactFile=ggml-base.bin", provenance.Source);
        Assert.Contains(WhisperNetIntegration.ModelSha256, provenance.Source);
        Assert.EndsWith("/ggml-base.bin", WhisperNetIntegration.ModelUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_cache_rejects_download_with_unexpected_checksum()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"audio-transcriber-cache-{Guid.NewGuid():N}");
        using var httpClient = new HttpClient(new StaticResponseHandler(
            new ByteArrayContent([1, 2, 3])));

        try
        {
            var exception = await Assert.ThrowsAsync<ModelIntegrationUnavailableException>(
                () => new WhisperNetModelCache(cache, httpClient).EnsureModelAsync());

            Assert.Contains("SHA-256 mismatch", exception.Message);
            Assert.Contains("<model-file>", exception.Message);
            Assert.DoesNotContain(cache, exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(cache, WhisperNetIntegration.ModelFileName)));
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Engine_rejects_unsupported_audio_before_model_access()
    {
        var audio = new AudioClip(
            "test.wav",
            sampleRate: 8_000,
            channels: AudioRequirements.RequiredChannels,
            AudioSampleFormat.Pcm16,
            [0.0f]);

        var exception = await Assert.ThrowsAsync<UnsupportedAudioFormatException>(
            () => new WhisperNetEngine().TranscribeAsync(audio).AsTask());

        Assert.Contains("16000 Hz", exception.Message);
    }

    [RealModelFact]
    public async Task Real_model_smoke_transcribes_external_fixture_when_opted_in()
    {
        var fixture = Environment.GetEnvironmentVariable("AUDIO_TRANSCRIBER_REAL_MODEL_FIXTURE");
        Assert.False(string.IsNullOrWhiteSpace(fixture));
        Assert.True(File.Exists(fixture), $"The configured speech fixture does not exist: {fixture}");

        var audio = new WavAudioReader().Load(fixture);
        AudioRequirements.Validate(audio);
        var cacheDirectory = Environment.GetEnvironmentVariable("AUDIO_TRANSCRIBER_REAL_MODEL_CACHE");
        var engine = new WhisperNetEngine(
            new WhisperNetModelCache(string.IsNullOrWhiteSpace(cacheDirectory) ? null : cacheDirectory));
        var segments = await engine.TranscribeAsync(audio);

        Assert.NotEmpty(segments);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            Assert.False(string.IsNullOrWhiteSpace(segment.Text));
            Assert.True(segment.Start >= TimeSpan.Zero);
            Assert.True(segment.End > segment.Start);
            Assert.True(segment.End <= audio.Duration);
            if (index > 0)
            {
                Assert.True(segment.Start >= segments[index - 1].End);
            }
        }
    }

    public sealed class RealModelFactAttribute : FactAttribute
    {
        public RealModelFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIO_TRANSCRIBER_REAL_MODEL_FIXTURE")))
            {
                Skip = "Set AUDIO_TRANSCRIBER_REAL_MODEL_FIXTURE to run the real model smoke.";
            }
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpContent _content;

        public StaticResponseHandler(HttpContent content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = _content
            });
    }
}
