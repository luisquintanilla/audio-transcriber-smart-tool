namespace AudioTranscriber.Tests;

public sealed class TranscriptModelsTests
{
    [Fact]
    public void Transcript_rejects_out_of_order_segments()
    {
        var provenance = TestProvenance();
        var segments = new[]
        {
            new TranscriptSegment("second", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new TranscriptSegment("first", TimeSpan.Zero, TimeSpan.FromSeconds(1))
        };

        Assert.Throws<ArgumentException>(() => new Transcript("audio.wav", provenance, segments));
    }

    [Fact]
    public void Transcript_exposes_joined_text_and_normalized_source_path()
    {
        var transcript = new Transcript(
            "audio.wav",
            TestProvenance(),
            [
                new TranscriptSegment(" Hello ", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
                new TranscriptSegment("world", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
            ]);

        Assert.Equal("Hello world", transcript.Text);
        Assert.Equal(Path.GetFullPath("audio.wav"), transcript.SourcePath);
    }

    [Fact]
    public void Segment_rejects_non_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TranscriptSegment("text", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    internal static ModelProvenance TestProvenance() =>
        new(
            "test",
            "fake",
            "test.package",
            "1.0.0",
            "deterministic-test",
            Path.Combine(Path.GetTempPath(), "audio-transcriber-models"));

    [Fact]
    public void Segment_preserves_typed_timestamps_and_trims_text()
    {
        var start = TimeSpan.FromMilliseconds(125);
        var end = TimeSpan.FromMilliseconds(875);

        var segment = new TranscriptSegment("  typed segment  ", start, end);

        Assert.Equal("typed segment", segment.Text);
        Assert.Equal(start, segment.Start);
        Assert.Equal(end, segment.End);
        Assert.True(segment.End > segment.Start);
    }

    [Fact]
    public void Segment_rejects_negative_start_timestamp()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TranscriptSegment("text", TimeSpan.FromMilliseconds(-1), TimeSpan.Zero));

        Assert.Equal("start", exception.ParamName);
    }

    [Fact]
    public void Segment_rejects_end_before_start_timestamp()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TranscriptSegment("text", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));

        Assert.Equal("end", exception.ParamName);
    }

    [Fact]
    public void Transcript_accepts_equal_start_times_without_reordering_typed_segments()
    {
        var first = new TranscriptSegment("first", TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var second = new TranscriptSegment("second", TimeSpan.Zero, TimeSpan.FromSeconds(2));

        var transcript = new Transcript("audio.wav", TestProvenance(), [first, second]);

        Assert.Equal(2, transcript.Segments.Count);
        Assert.Same(first, transcript.Segments[0]);
        Assert.Same(second, transcript.Segments[1]);
        Assert.Equal("first second", transcript.Text);
    }
}
