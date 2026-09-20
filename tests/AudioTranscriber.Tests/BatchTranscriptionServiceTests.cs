namespace AudioTranscriber.Tests;

public sealed class BatchTranscriptionServiceTests
{
    [Fact]
    public async Task TranscribeAsync_preserves_input_order_and_provenance()
    {
        var first = TestAudio.CreateWav(sampleCount: 1600);
        var second = TestAudio.CreateWav(sampleCount: 3200);
        try
        {
            var engine = new FakeEngine();
            var result = await new BatchTranscriptionService(engine).TranscribeAsync([second, first]);

            Assert.Equal(2, result.Transcripts.Count);
            Assert.Equal(Path.GetFullPath(second), result.Transcripts[0].SourcePath);
            Assert.Equal(Path.GetFullPath(first), result.Transcripts[1].SourcePath);
            Assert.All(result.Transcripts, transcript => Assert.Equal("fake", transcript.Provenance.Model));
            Assert.Equal(2, engine.Calls);
        }
        finally
        {
            TestAudio.Delete(first);
            TestAudio.Delete(second);
        }
    }

    [Fact]
    public async Task TranscribeAsync_propagates_cancellation_before_loading_next_file()
    {
        var path = TestAudio.CreateWav();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new BatchTranscriptionService(new FakeEngine()).TranscribeAsync([path], cancellation.Token));
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_rejects_engine_segments_beyond_audio_duration()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BatchTranscriptionService(new FakeEngine(endsAfterAudio: true)).TranscribeAsync([path]));
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_rejects_segment_one_tick_beyond_audio_duration()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BatchTranscriptionService(new OneTickBeyondDurationEngine()).TranscribeAsync([path]));

            Assert.Contains("ends after the audio duration", exception.Message);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_accepts_segment_ending_exactly_at_audio_duration()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var result = await new BatchTranscriptionService(new ExactDurationEngine()).TranscribeAsync([path]);

            var segment = Assert.Single(result.Transcripts[0].Segments);
            Assert.Equal(TimeSpan.Zero, segment.Start);
            Assert.Equal(TimeSpan.FromMilliseconds(100), segment.End);
            Assert.Equal("full duration", segment.Text);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_rejects_overlapping_engine_segments()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new BatchTranscriptionService(new OverlappingEngine()).TranscribeAsync([path]));

            Assert.Contains("overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_accepts_adjacent_non_overlapping_segments_without_merging()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var result = await new BatchTranscriptionService(new AdjacentEngine()).TranscribeAsync([path]);

            Assert.Equal(2, result.Transcripts[0].Segments.Count);
            Assert.Equal(["first fragment", "second fragment"], result.Transcripts[0].Segments.Select(segment => segment.Text));
            Assert.Equal("first fragment second fragment", result.Transcripts[0].Text);
            Assert.Equal(TimeSpan.FromMilliseconds(50), result.Transcripts[0].Segments[1].Start);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_accepts_legitimate_gaps_between_non_overlapping_segments()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var result = await new BatchTranscriptionService(new GappedEngine()).TranscribeAsync([path]);
            var segments = result.Transcripts[0].Segments;

            Assert.Equal(2, segments.Count);
            Assert.Equal(TimeSpan.FromMilliseconds(25), segments[0].End);
            Assert.Equal(TimeSpan.FromMilliseconds(50), segments[1].Start);
            Assert.Equal(["before gap", "after gap"], segments.Select(segment => segment.Text));
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_rejects_empty_input_paths()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            new BatchTranscriptionService(new FakeEngine()).TranscribeAsync([]));

        Assert.Equal("inputPaths", exception.ParamName);
        Assert.Contains("At least one input path is required.", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_processes_inputs_sequentially_and_keeps_result_order()
    {
        var first = TestAudio.CreateWav(sampleCount: 1600);
        var second = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var engine = new RecordingEngine(TranscriptModelsTests.TestProvenance());

            var result = await new BatchTranscriptionService(engine).TranscribeAsync([second, first]);

            Assert.Equal(
                new[] { Path.GetFullPath(second), Path.GetFullPath(first) },
                engine.Sources);
            Assert.Equal(["segment-1", "segment-2"], result.Transcripts.Select(transcript => transcript.Text));
            Assert.Equal(
                new[] { Path.GetFullPath(second), Path.GetFullPath(first) },
                result.Transcripts.Select(transcript => transcript.SourcePath));
        }
        finally
        {
            TestAudio.Delete(first);
            TestAudio.Delete(second);
        }
    }

    [Fact]
    public async Task TranscribeAsync_copies_complete_engine_provenance_to_each_transcript()
    {
        var provenance = new ModelProvenance(
            "local-provider",
            "whisper-custom",
            "provider.package",
            "4.5.6",
            "fixture-source",
            Path.Combine(Path.GetTempPath(), "fixture-model-cache"));
        var first = TestAudio.CreateWav(sampleCount: 1600);
        var second = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            var result = await new BatchTranscriptionService(new RecordingEngine(provenance))
                .TranscribeAsync([first, second]);

            Assert.Equal(2, result.Transcripts.Count);
            Assert.All(result.Transcripts, transcript =>
            {
                Assert.Same(provenance, transcript.Provenance);
                Assert.Equal(provenance.Provider, transcript.Provenance.Provider);
                Assert.Equal(provenance.Model, transcript.Provenance.Model);
                Assert.Equal(provenance.PackageId, transcript.Provenance.PackageId);
                Assert.Equal(provenance.PackageVersion, transcript.Provenance.PackageVersion);
                Assert.Equal(provenance.Source, transcript.Provenance.Source);
                Assert.Equal(provenance.CachePath, transcript.Provenance.CachePath);
            });
        }
        finally
        {
            TestAudio.Delete(first);
            TestAudio.Delete(second);
        }
    }

    [Fact]
    public async Task TranscribeAsync_passes_cancellation_to_engine_and_preserves_engine_cancellation()
    {
        var path = TestAudio.CreateWav(sampleCount: 1600);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var engine = new CancellingEngine();

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new BatchTranscriptionService(engine).TranscribeAsync([path], cancellation.Token));

            Assert.Equal(cancellation.Token, engine.ReceivedToken);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public async Task TranscribeAsync_propagates_explicit_engine_failure_without_processing_later_inputs()
    {
        var first = TestAudio.CreateWav(sampleCount: 1600);
        var second = TestAudio.CreateWav(sampleCount: 1600);
        var failure = new InvalidOperationException("engine failed deterministically");
        try
        {
            var engine = new FailingEngine(failure);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new BatchTranscriptionService(engine).TranscribeAsync([first, second]));

            Assert.Same(failure, exception);
            Assert.Equal(1, engine.Calls);
        }
        finally
        {
            TestAudio.Delete(first);
            TestAudio.Delete(second);
        }
    }

    private sealed class FakeEngine : ITranscriptionEngine
    {
        private readonly bool _endsAfterAudio;

        public FakeEngine(bool endsAfterAudio = false)
        {
            _endsAfterAudio = endsAfterAudio;
        }

        public int Calls { get; private set; }
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var end = _endsAfterAudio ? audio.Duration + TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(50);
            return ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
                [new TranscriptSegment("transcribed", TimeSpan.Zero, end)]);
        }
    }

    private sealed class OneTickBeyondDurationEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment(
                    "too long",
                    TimeSpan.Zero,
                    audio.Duration + TimeSpan.FromTicks(1))
            ]);
    }

    private sealed class ExactDurationEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment("full duration", TimeSpan.Zero, audio.Duration)
            ]);
    }

    private sealed class AdjacentEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment(
                    " first fragment ",
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(50)),
                new TranscriptSegment(
                    "second fragment",
                    TimeSpan.FromMilliseconds(50),
                    audio.Duration)
            ]);
    }

    private sealed class GappedEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment(
                    "before gap",
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(25)),
                new TranscriptSegment(
                    "after gap",
                    TimeSpan.FromMilliseconds(50),
                    TimeSpan.FromMilliseconds(75))
            ]);
    }

    private sealed class RecordingEngine : ITranscriptionEngine
    {
        public RecordingEngine(ModelProvenance provenance)
        {
            Provenance = provenance;
        }

        public List<string> Sources { get; } = [];
        public ModelProvenance Provenance { get; }

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default)
        {
            Sources.Add(audio.SourcePath);
            var sequence = Sources.Count;
            return ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
                [new TranscriptSegment($"segment-{sequence}", TimeSpan.Zero, TimeSpan.FromMilliseconds(50))]);
        }
    }

    private sealed class CancellingEngine : ITranscriptionEngine
    {
        public CancellationToken ReceivedToken { get; private set; }
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default)
        {
            ReceivedToken = cancellationToken;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class OverlappingEngine : ITranscriptionEngine
    {
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<TranscriptSegment>>(
            [
                new TranscriptSegment("first", TimeSpan.Zero, TimeSpan.FromMilliseconds(50)),
                new TranscriptSegment("overlap", TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(70))
            ]);
    }

    private sealed class FailingEngine : ITranscriptionEngine
    {
        private readonly Exception _failure;

        public FailingEngine(Exception failure)
        {
            _failure = failure;
        }

        public int Calls { get; private set; }
        public ModelProvenance Provenance => TranscriptModelsTests.TestProvenance();

        public ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            AudioClip audio,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw _failure;
        }
    }
}
