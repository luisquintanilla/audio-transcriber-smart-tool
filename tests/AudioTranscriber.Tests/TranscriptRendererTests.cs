using System.Text.Json;

namespace AudioTranscriber.Tests;

public sealed class TranscriptRendererTests
{
    private static BatchTranscript Batch() =>
        new(
        [
            new Transcript(
                "first.wav",
                TranscriptModelsTests.TestProvenance(),
                [
                    new TranscriptSegment("Hello", TimeSpan.Zero, TimeSpan.FromMilliseconds(1250)),
                    new TranscriptSegment("world", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3))
                ])
        ]);

    [Fact]
    public void Text_renderer_is_timestamped_and_deterministic()
    {
        var rendered = new TextTranscriptRenderer().Render(Batch());

        Assert.Equal(
            "# first.wav\n[00:00:00.000 - 00:00:01.250] Hello\n[00:00:02.000 - 00:00:03.000] world\n",
            rendered);
    }

    [Fact]
    public void Json_renderer_has_typed_timestamp_strings()
    {
        using var document = JsonDocument.Parse(new JsonTranscriptRenderer().Render(Batch()));
        var segment = document.RootElement[0].GetProperty("segments")[0];

        Assert.Equal("first.wav", document.RootElement[0].GetProperty("source").GetString());
        Assert.Equal("00:00:00.000", segment.GetProperty("start").GetString());
        Assert.Equal("00:00:01.250", segment.GetProperty("end").GetString());
        Assert.Equal("Hello", segment.GetProperty("text").GetString());
    }

    [Fact]
    public void Json_renderer_preserves_fragment_boundaries_and_backward_compatible_shape()
    {
        var batch = new BatchTranscript(
        [
            new Transcript(
                Path.Combine("recordings", "speech.wav"),
                new ModelProvenance(
                    "provider",
                    "model",
                    "package",
                    "1.0.0",
                    "fixture",
                    "cache"),
                [
                    new TranscriptSegment(" first fragment ", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                    new TranscriptSegment("second fragment", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
                ])
        ]);

        using var document = JsonDocument.Parse(new JsonTranscriptRenderer().Render(batch));
        var transcript = Assert.Single(document.RootElement.EnumerateArray());
        var segments = transcript.GetProperty("segments");

        Assert.Equal("speech.wav", transcript.GetProperty("source").GetString());
        Assert.Equal("provider", transcript.GetProperty("provider").GetString());
        Assert.Equal("model", transcript.GetProperty("model").GetString());
        Assert.Equal(2, segments.GetArrayLength());
        Assert.Equal("first fragment", segments[0].GetProperty("text").GetString());
        Assert.Equal("second fragment", segments[1].GetProperty("text").GetString());
        Assert.Equal("00:00:01.000", segments[1].GetProperty("start").GetString());
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine("recordings", "speech.wav")), document.RootElement.ToString());
    }

    [Fact]
    public void Srt_renderer_uses_comma_milliseconds()
    {
        var rendered = new SrtTranscriptRenderer().Render(Batch());

        Assert.Contains("1\n00:00:00,000 --> 00:00:01,250\nHello\n", rendered);
        Assert.Contains("2\n00:00:02,000 --> 00:00:03,000\nworld\n", rendered);
    }

    [Fact]
    public void WebVtt_renderer_has_header_and_dot_milliseconds()
    {
        var rendered = new WebVttTranscriptRenderer().Render(Batch());

        Assert.StartsWith("WEBVTT\n\n00:00:00.000 --> 00:00:01.250\nHello\n", rendered);
    }

    [Fact]
    public void Text_renderer_preserves_batch_order_and_formats_hour_boundaries()
    {
        var batch = new BatchTranscript(
        [
            new Transcript(
                "first.wav",
                TranscriptModelsTests.TestProvenance(),
                [
                    new TranscriptSegment(
                        "first",
                        TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromMilliseconds(3004),
                        TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3) + TimeSpan.FromMilliseconds(4005))
                ]),
            new Transcript(
                "second.wav",
                TranscriptModelsTests.TestProvenance(),
                [
                    new TranscriptSegment(
                        "second",
                        TimeSpan.FromMilliseconds(999),
                        TimeSpan.FromMilliseconds(1001))
                ])
        ]);

        var rendered = new TextTranscriptRenderer().Render(batch);

        Assert.Equal(
            "# first.wav\n" +
            "[01:02:03.004 - 02:03:04.005] first\n" +
            "\n" +
            "\n" +
            "# second.wav\n" +
            "[00:00:00.999 - 00:00:01.001] second\n",
            rendered);
    }

    [Fact]
    public void Json_renderer_is_deterministic_and_escapes_special_text()
    {
        const string text = "say \"hello\"\\path\n<tag>&";
        var batch = new BatchTranscript(
        [
            new Transcript(
                "json.wav",
                new ModelProvenance(
                    "provider",
                    "model",
                    "package",
                    "1.2.3",
                    "source",
                    "cache"),
                [
                    new TranscriptSegment(
                        text,
                        TimeSpan.FromHours(12) + TimeSpan.FromMinutes(34) + TimeSpan.FromMilliseconds(56007),
                        TimeSpan.FromHours(13) + TimeSpan.FromMinutes(35) + TimeSpan.FromMilliseconds(57008))
                ])
        ]);
        var renderer = new JsonTranscriptRenderer();

        var rendered = renderer.Render(batch);

        Assert.Equal(rendered, renderer.Render(batch));
        Assert.DoesNotContain("\"hello\"", rendered);
        Assert.DoesNotContain("<tag>", rendered);
        Assert.Contains("\\", rendered);
        Assert.Contains("\\n", rendered);

        using var document = JsonDocument.Parse(rendered);
        var transcript = document.RootElement[0];
        var segment = transcript.GetProperty("segments")[0];
        Assert.Equal("json.wav", transcript.GetProperty("source").GetString());
        Assert.DoesNotContain(Path.GetFullPath("json.wav"), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("model", transcript.GetProperty("model").GetString());
        Assert.Equal("provider", transcript.GetProperty("provider").GetString());
        Assert.Equal("12:34:56.007", segment.GetProperty("start").GetString());
        Assert.Equal("13:35:57.008", segment.GetProperty("end").GetString());
        Assert.Equal(text, segment.GetProperty("text").GetString());
    }

    [Fact]
    public void All_renderers_avoid_absolute_source_path_leakage()
    {
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "private-recordings",
            "speech.wav");
        var batch = new BatchTranscript(
        [
            new Transcript(
                source,
                TranscriptModelsTests.TestProvenance(),
                [new TranscriptSegment("safe", TimeSpan.Zero, TimeSpan.FromSeconds(1))])
        ]);

        foreach (var format in Enum.GetValues<TranscriptOutputFormat>())
        {
            var rendered = TranscriptRenderers.Create(format).Render(batch);

            Assert.DoesNotContain(Path.GetFullPath(source), rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\", rendered, StringComparison.OrdinalIgnoreCase);
        }

        using var json = JsonDocument.Parse(
            TranscriptRenderers.Create(TranscriptOutputFormat.Json).Render(batch));
        Assert.Equal("speech.wav", json.RootElement[0].GetProperty("source").GetString());
    }

    [Fact]
    public void Srt_renderer_is_deterministic_with_comma_timestamps_and_trailing_cues()
    {
        var rendered = new SrtTranscriptRenderer().Render(Batch());

        Assert.Equal(
            "1\n00:00:00,000 --> 00:00:01,250\nHello\n\n" +
            "2\n00:00:02,000 --> 00:00:03,000\nworld\n\n",
            rendered);
    }

    [Fact]
    public void WebVtt_renderer_is_deterministic_with_header_and_dot_timestamps()
    {
        var rendered = new WebVttTranscriptRenderer().Render(Batch());

        Assert.Equal(
            "WEBVTT\n\n" +
            "00:00:00.000 --> 00:00:01.250\nHello\n\n" +
            "00:00:02.000 --> 00:00:03.000\nworld\n\n",
            rendered);
    }
}
