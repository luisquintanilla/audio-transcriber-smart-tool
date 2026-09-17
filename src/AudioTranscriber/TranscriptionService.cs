namespace AudioTranscriber;

public interface ITranscriptionEngine
{
    ModelProvenance Provenance { get; }

    ValueTask<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        AudioClip audio,
        CancellationToken cancellationToken = default);
}

public sealed class BatchTranscriptionService
{
    private readonly WavAudioReader _audioReader;
    private readonly ITranscriptionEngine _engine;

    public BatchTranscriptionService(ITranscriptionEngine engine, WavAudioReader? audioReader = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _audioReader = audioReader ?? new WavAudioReader();
    }

    public async Task<BatchTranscript> TranscribeAsync(
        IEnumerable<string> inputPaths,
        CancellationToken cancellationToken = default)
    {
        var paths = (inputPaths ?? throw new ArgumentNullException(nameof(inputPaths))).ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one input path is required.", nameof(inputPaths));
        }

        var transcripts = new List<Transcript>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var audio = _audioReader.Load(path);
            AudioRequirements.Validate(audio);
            var segments = await _engine.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
            ValidateSegments(audio, segments);
            transcripts.Add(new Transcript(audio.SourcePath, _engine.Provenance, segments));
        }

        return new BatchTranscript(transcripts);
    }

    private static void ValidateSegments(AudioClip audio, IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.End > audio.Duration)
            {
                throw new InvalidDataException(
                    $"Transcription segment {index + 1} for '{SafePathDisplay.Basename(audio.SourcePath)}' ends after the audio duration.");
            }

            if (index > 0 && segment.Start < segments[index - 1].End)
            {
                throw new InvalidDataException(
                    $"Transcription segments for '{SafePathDisplay.Basename(audio.SourcePath)}' overlap.");
            }
        }
    }
}

public sealed class ModelIntegrationUnavailableException : InvalidOperationException
{
    public ModelIntegrationUnavailableException(string message) : base(message)
    {
    }
}
