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
    public void Transcript_rejects_non_monotonic_start_times_by_one_tick()
    {
        var segments = new[]
        {
            new TranscriptSegment("first", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new TranscriptSegment(
                "second",
                TimeSpan.FromSeconds(1) - TimeSpan.FromTicks(1),
                TimeSpan.FromSeconds(3))
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            new Transcript("audio.wav", TestProvenance(), segments));

        Assert.Equal("segments", exception.ParamName);
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

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n ")]
    public void Segment_rejects_empty_or_whitespace_text(string text)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new TranscriptSegment(text, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

        Assert.Equal("text", exception.ParamName);
    }

    [Fact]
    public void Segment_preserves_sub_millisecond_timestamp_values()
    {
        var start = TimeSpan.FromTicks(1);
        var end = TimeSpan.FromMilliseconds(1) - TimeSpan.FromTicks(1);

        var segment = new TranscriptSegment("precise", start, end);

        Assert.Equal(start, segment.Start);
        Assert.Equal(end, segment.End);
        Assert.NotEqual(TimeSpan.Zero, segment.Start);
        Assert.NotEqual(TimeSpan.FromMilliseconds(1), segment.End);
    }

    [Fact]
    public void Segment_rejects_end_before_start_timestamp()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TranscriptSegment("text", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));

        Assert.Equal("end", exception.ParamName);
    }

    [Fact]
    public void ModelProvenance_preserves_all_required_metadata()
    {
        var provenance = new ModelProvenance(
            "provider/name",
            "model.variant",
            "package.id",
            "1.2.3-preview.4",
            "local-fixture",
            "cache/path");

        Assert.Equal("provider/name", provenance.Provider);
        Assert.Equal("model.variant", provenance.Model);
        Assert.Equal("package.id", provenance.PackageId);
        Assert.Equal("1.2.3-preview.4", provenance.PackageVersion);
        Assert.Equal("local-fixture", provenance.Source);
        Assert.Equal("cache/path", provenance.CachePath);
    }

    [Fact]
    public void ModelProvenance_rejects_null_empty_or_whitespace_required_fields()
    {
        var invalidValues = new[] { null, string.Empty, " ", "\t\r\n" };

        foreach (var invalid in invalidValues)
        {
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                invalid!,
                "model",
                "package",
                "1.0.0",
                "source",
                "cache"));
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                "provider",
                invalid!,
                "package",
                "1.0.0",
                "source",
                "cache"));
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                "provider",
                "model",
                invalid!,
                "1.0.0",
                "source",
                "cache"));
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                "provider",
                "model",
                "package",
                invalid!,
                "source",
                "cache"));
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                "provider",
                "model",
                "package",
                "1.0.0",
                invalid!,
                "cache"));
            Assert.Throws<ArgumentException>(() => new ModelProvenance(
                "provider",
                "model",
                "package",
                "1.0.0",
                "source",
                invalid!));
        }
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

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Transcript_rejects_empty_or_whitespace_source_path(string sourcePath)
    {
        Assert.Throws<ArgumentException>(() =>
            new Transcript(sourcePath, TestProvenance(), []));
    }

    [Fact]
    public void Transcript_rejects_null_provenance()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Transcript("audio.wav", null!, []));
    }

    [Fact]
    public void Transcript_rejects_null_segments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Transcript("audio.wav", TestProvenance(), null!));
    }

    [Fact]
    public void Transcript_snapshots_input_and_preserves_unmerged_fragment_order()
    {
        var segments = new List<TranscriptSegment>
        {
            new(" first fragment ", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new("second fragment", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
        };

        var transcript = new Transcript("audio.wav", TestProvenance(), segments);
        segments.Add(new TranscriptSegment("late mutation", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)));

        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal(["first fragment", "second fragment"], transcript.Segments.Select(segment => segment.Text));
        Assert.Equal("first fragment second fragment", transcript.Text);
    }

    [Fact]
    public void Transcript_exposes_read_only_segment_collection()
    {
        var segment = new TranscriptSegment("text", TimeSpan.Zero, TimeSpan.FromSeconds(1));
        var transcript = new Transcript("audio.wav", TestProvenance(), [segment]);
        var list = Assert.IsAssignableFrom<IList<TranscriptSegment>>(transcript.Segments);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(segment));
        Assert.Same(segment, transcript.Segments[0]);
    }

    [Fact]
    public void Transcript_accepts_empty_segment_collection_with_empty_derived_text()
    {
        var transcript = new Transcript("audio.wav", TestProvenance(), []);

        Assert.Empty(transcript.Segments);
        Assert.Equal(string.Empty, transcript.Text);
    }

    [Fact]
    public void BatchTranscript_rejects_null_transcript_enumerable()
    {
        Assert.Throws<ArgumentNullException>(() => new BatchTranscript(null!));
    }

    [Fact]
    public void BatchTranscript_snapshots_enumerable_input()
    {
        var transcript = new Transcript(
            "audio.wav",
            TestProvenance(),
            [new TranscriptSegment("text", TimeSpan.Zero, TimeSpan.FromSeconds(1))]);
        var source = new List<Transcript> { transcript };

        var batch = new BatchTranscript(source);
        source.Clear();

        Assert.Single(batch.Transcripts);
        Assert.Same(transcript, batch.Transcripts[0]);
    }

    [Fact]
    public void BatchTranscript_exposes_read_only_collection()
    {
        var transcript = new Transcript(
            "audio.wav",
            TestProvenance(),
            [new TranscriptSegment("text", TimeSpan.Zero, TimeSpan.FromSeconds(1))]);
        var batch = new BatchTranscript([transcript]);
        var list = Assert.IsAssignableFrom<IList<Transcript>>(batch.Transcripts);

        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(transcript));
        Assert.Single(batch.Transcripts);
    }

    [Fact]
    public void BatchTranscript_preserves_empty_enumerable_as_empty_collection()
    {
        var batch = new BatchTranscript([]);

        Assert.Empty(batch.Transcripts);
    }
}
