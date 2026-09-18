using System.Text;

namespace AudioTranscriber;

public sealed record WavAudioMetadata(
    string SourcePath,
    int SampleRate,
    short Channels,
    short BitsPerSample,
    long DataBytes,
    TimeSpan Duration);

public sealed class WavAudioMetadataReader
{
    public WavAudioMetadata Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Input path cannot be empty.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Audio file was not found: '{SafePathDisplay.Basename(fullPath)}'.");
        }

        using var stream = File.OpenRead(fullPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        RequireFourCc(reader, "RIFF", fullPath);
        var riffSize = reader.ReadUInt32();
        if (riffSize < 4)
        {
            throw InvalidWav(fullPath, "RIFF chunk is too small.");
        }

        var riffEnd = 8L + riffSize;
        if (riffEnd > stream.Length)
        {
            throw InvalidWav(fullPath, "RIFF chunk extends beyond the file.");
        }

        RequireFourCc(reader, "WAVE", fullPath);

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        short audioFormat = 0;
        short blockAlign = 0;
        int byteRate = 0;
        long dataBytes = 0;
        var hasFormat = false;
        var hasData = false;

        while (stream.Position < riffEnd)
        {
            if (riffEnd - stream.Position < 8)
            {
                throw InvalidWav(fullPath, "WAV chunk header is incomplete.");
            }

            var chunkId = ReadFourCc(reader, fullPath);
            var chunkSize = reader.ReadUInt32();
            var chunkEnd = stream.Position + chunkSize;
            if (chunkEnd > riffEnd)
            {
                throw InvalidWav(fullPath, $"WAV chunk '{chunkId}' extends beyond the RIFF file.");
            }

            switch (chunkId)
            {
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw InvalidWav(fullPath, "WAV format chunk is too small.");
                    }

                    audioFormat = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    byteRate = reader.ReadInt32();
                    blockAlign = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();
                    hasFormat = true;
                    stream.Position = chunkEnd;
                    break;
                case "data":
                    if (hasData)
                    {
                        throw InvalidWav(fullPath, "WAV contains multiple data chunks.");
                    }

                    dataBytes = chunkSize;
                    hasData = true;
                    stream.Position = chunkEnd;
                    break;
                default:
                    stream.Position = chunkEnd;
                    break;
            }

            if ((chunkSize & 1) == 1)
            {
                if (stream.Position >= riffEnd)
                {
                    throw InvalidWav(fullPath, $"WAV chunk '{chunkId}' has an incomplete padding byte.");
                }

                stream.Position++;
            }
        }

        if (!hasFormat || !hasData)
        {
            throw InvalidWav(fullPath, "WAV file is missing a format or data chunk.");
        }

        if (audioFormat != 1)
        {
            throw new UnsupportedAudioFormatException(
                $"WAV '{SafePathDisplay.Basename(fullPath)}' uses unsupported audio format code {audioFormat}.");
        }

        if (channels != AudioRequirements.RequiredChannels ||
            sampleRate != AudioRequirements.RequiredSampleRate ||
            bitsPerSample != 16 ||
            blockAlign != 2 ||
            byteRate != AudioRequirements.RequiredSampleRate * 2)
        {
            throw new UnsupportedAudioFormatException(
                $"WAV '{SafePathDisplay.Basename(fullPath)}' must be " +
                $"{AudioRequirements.RequiredSampleRate} Hz mono 16-bit PCM.");
        }

        if (dataBytes == 0 || dataBytes % blockAlign != 0)
        {
            throw InvalidWav(fullPath, "WAV data chunk has an incomplete sample.");
        }

        var duration = TimeSpan.FromSeconds(
            (double)dataBytes / blockAlign / AudioRequirements.RequiredSampleRate);
        return new WavAudioMetadata(
            fullPath,
            sampleRate,
            channels,
            bitsPerSample,
            dataBytes,
            duration);
    }

    private static string ReadFourCc(BinaryReader reader, string path)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException(
                $"WAV header ended unexpectedly: '{SafePathDisplay.Basename(path)}'.");
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static void RequireFourCc(BinaryReader reader, string expected, string path)
    {
        if (!string.Equals(ReadFourCc(reader, path), expected, StringComparison.Ordinal))
        {
            throw InvalidWav(path, $"Audio file is not a RIFF/WAVE file; expected {expected}.");
        }
    }

    private static InvalidDataException InvalidWav(string path, string detail) =>
        new($"Invalid WAV '{SafePathDisplay.Basename(path)}': {detail}");
}
