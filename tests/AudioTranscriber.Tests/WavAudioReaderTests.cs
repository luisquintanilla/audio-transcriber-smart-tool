namespace AudioTranscriber.Tests;

public sealed class WavAudioReaderTests
{
    [Fact]
    public void Load_reads_16khz_mono_pcm16_samples()
    {
        var path = TestAudio.CreateWav(sampleCount: 3);
        try
        {
            var audio = new WavAudioReader().Load(path);

            Assert.Equal(AudioRequirements.RequiredSampleRate, audio.SampleRate);
            Assert.Equal(AudioRequirements.RequiredChannels, audio.Channels);
            Assert.Equal(AudioSampleFormat.Pcm16, audio.Format);
            Assert.Equal(3, audio.Samples.Count);
            Assert.Equal(1000 / 32768f, audio.Samples[1]);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Theory]
    [InlineData(8000, 1, 16, 1)]
    [InlineData(16000, 2, 16, 1)]
    [InlineData(16000, 1, 24, 1)]
    [InlineData(16000, 1, 16, 2)]
    public void Load_rejects_unsupported_wav_requirements(
        int sampleRate,
        short channels,
        short bitsPerSample,
        short audioFormat)
    {
        var path = TestAudio.CreateWav(sampleRate, channels, bitsPerSample, audioFormat);
        try
        {
            Assert.Throws<UnsupportedAudioFormatException>(() => new WavAudioReader().Load(path));
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_missing_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav");

        var exception = Assert.Throws<FileNotFoundException>(() => new WavAudioReader().Load(path));

        Assert.Contains(Path.GetFileName(path), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(path), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\Users\\", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_preserves_pcm16_values_duration_and_normalized_source_path()
    {
        var path = TestAudio.CreateWav(sampleCount: 3);
        try
        {
            var audio = new WavAudioReader().Load(path);

            Assert.Equal(Path.GetFullPath(path), audio.SourcePath);
            Assert.Equal(0f, audio.Samples[0]);
            Assert.Equal(1000 / 32768f, audio.Samples[1]);
            Assert.Equal(2000 / 32768f, audio.Samples[2]);
            Assert.Equal(TimeSpan.FromSeconds(3d / AudioRequirements.RequiredSampleRate), audio.Duration);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_truncated_data_chunk()
    {
        var path = TestAudio.CreateWav(sampleCount: 2);
        try
        {
            var bytes = File.ReadAllBytes(path);
            bytes[40] = 5;
            bytes[41] = 0;
            bytes[42] = 0;
            bytes[43] = 0;
            File.WriteAllBytes(path, bytes);

            var exception = Assert.Throws<InvalidDataException>(() => new WavAudioReader().Load(path));

            Assert.Contains("data", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("extends beyond", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RIFF/WAVE", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_wav_containing_only_unsupported_chunks()
    {
        var path = TestAudio.CreateWavWithOnlyUnknownChunk();
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => new WavAudioReader().Load(path));

            Assert.Contains("supported format and data chunk", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }

    [Fact]
    public void Load_rejects_truncated_unsupported_chunk()
    {
        var path = TestAudio.CreateTruncatedUnknownChunkWav();
        try
        {
            var exception = Assert.Throws<InvalidDataException>(() => new WavAudioReader().Load(path));

            Assert.Contains("JUNK", exception.Message, StringComparison.Ordinal);
            Assert.Contains("extends beyond", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestAudio.Delete(path);
        }
    }
}
