using System.Buffers.Binary;

namespace AudioTranscriber.Tests;

internal static class TestAudio
{
    public static string CreateWav(
        int sampleRate = AudioRequirements.RequiredSampleRate,
        short channels = AudioRequirements.RequiredChannels,
        short bitsPerSample = 16,
        short audioFormat = 1,
        int sampleCount = 1600)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audio-transcriber-{Guid.NewGuid():N}.wav");
        var bytesPerSample = bitsPerSample / 8;
        var data = new byte[sampleCount * channels * bytesPerSample];

        if (audioFormat == 1 && bitsPerSample == 16)
        {
            for (var index = 0; index < sampleCount * channels; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(index * 2, 2), (short)(index % 5 * 1000));
            }
        }

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + data.Length);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write(audioFormat);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bytesPerSample);
            writer.Write((short)(channels * bytesPerSample));
            writer.Write(bitsPerSample);
            writer.Write("data"u8.ToArray());
            writer.Write(data.Length);
            writer.Write(data);
        }

        return path;
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static string CreateWavWithOnlyUnknownChunk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audio-transcriber-{Guid.NewGuid():N}.wav");

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(16);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("JUNK"u8.ToArray());
            writer.Write(4);
            writer.Write(new byte[] { 1, 2, 3, 4 });
        }

        return path;
    }

    public static string CreateTruncatedUnknownChunkWav()
    {
        var path = Path.Combine(Path.GetTempPath(), $"audio-transcriber-{Guid.NewGuid():N}.wav");

        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RIFF"u8.ToArray());
            writer.Write(37);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(AudioRequirements.RequiredSampleRate);
            writer.Write(AudioRequirements.RequiredSampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("JUNK"u8.ToArray());
            writer.Write(4);
            writer.Write((byte)1);
        }

        return path;
    }
}
