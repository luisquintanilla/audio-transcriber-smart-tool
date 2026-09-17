namespace AudioTranscriber;

public enum AudioSampleFormat
{
    Pcm16,
    Pcm32Float
}

public sealed record AudioClip
{
    public string SourcePath { get; }
    public int SampleRate { get; }
    public short Channels { get; }
    public AudioSampleFormat Format { get; }
    public IReadOnlyList<float> Samples { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Count / SampleRate / Channels);

    public AudioClip(
        string sourcePath,
        int sampleRate,
        short channels,
        AudioSampleFormat format,
        IEnumerable<float> samples)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Source path cannot be empty.", nameof(sourcePath));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }

        var values = (samples ?? throw new ArgumentNullException(nameof(samples))).ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("Audio must contain at least one sample.", nameof(samples));
        }

        SourcePath = Path.GetFullPath(sourcePath);
        SampleRate = sampleRate;
        Channels = channels;
        Format = format;
        Samples = Array.AsReadOnly(values);
    }
}

public static class AudioRequirements
{
    public const int RequiredSampleRate = 16_000;
    public const short RequiredChannels = 1;

    public static void Validate(AudioClip audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (audio.SampleRate != RequiredSampleRate)
        {
            throw new UnsupportedAudioFormatException(
                $"Audio '{SafePathDisplay.Basename(audio.SourcePath)}' must use {RequiredSampleRate} Hz; found {audio.SampleRate} Hz.");
        }

        if (audio.Channels != RequiredChannels)
        {
            throw new UnsupportedAudioFormatException(
                $"Audio '{SafePathDisplay.Basename(audio.SourcePath)}' must be mono; found {audio.Channels} channels.");
        }
    }
}

public sealed class UnsupportedAudioFormatException : IOException
{
    public UnsupportedAudioFormatException(string message) : base(message)
    {
    }
}
