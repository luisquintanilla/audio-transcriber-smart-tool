using System.Buffers.Binary;

namespace AudioTranscriber;

public sealed class WavAudioReader
{
    public AudioClip Load(string path)
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

        FileStream stream;
        try
        {
            stream = File.OpenRead(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Audio file could not be opened: '{SafePathDisplay.Basename(fullPath)}'.",
                exception);
        }

        using (stream)
        {
        using var reader = new BinaryReader(stream);

        if (!ReadFourCc(reader, "RIFF"))
        {
            throw new InvalidDataException(
                $"Audio file is not a RIFF/WAVE file: '{SafePathDisplay.Basename(fullPath)}'.");
        }

        var riffSize = reader.ReadUInt32();
        if (riffSize < 4 || !ReadFourCc(reader, "WAVE"))
        {
            throw new InvalidDataException(
                $"Audio file is not a RIFF/WAVE file: '{SafePathDisplay.Basename(fullPath)}'.");
        }

        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        short audioFormat = 0;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();

            if (chunkSize > stream.Length - stream.Position)
            {
                throw new InvalidDataException(
                    $"WAV chunk '{chunkId}' extends beyond the file: '{SafePathDisplay.Basename(fullPath)}'.");
            }

            var chunkStart = stream.Position;
            switch (chunkId)
            {
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw new InvalidDataException(
                            $"WAV format chunk is too small: '{SafePathDisplay.Basename(fullPath)}'.");
                    }

                    audioFormat = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();
                    stream.Position = chunkStart + chunkSize;
                    break;
                case "data":
                    data = reader.ReadBytes(checked((int)chunkSize));
                    break;
                default:
                    stream.Position = chunkStart + chunkSize;
                    break;
            }

            if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
            {
                stream.Position++;
            }
        }

        if (channels <= 0 || sampleRate <= 0 || bitsPerSample <= 0 || data is null)
        {
            throw new InvalidDataException(
                $"WAV file is missing a supported format and data chunk: '{SafePathDisplay.Basename(fullPath)}'.");
        }

        if (audioFormat is not (1 or 3))
        {
            throw new UnsupportedAudioFormatException(
                $"WAV '{SafePathDisplay.Basename(fullPath)}' uses unsupported audio format code {audioFormat}.");
        }

        if (channels != AudioRequirements.RequiredChannels || sampleRate != AudioRequirements.RequiredSampleRate)
        {
            throw new UnsupportedAudioFormatException(
                $"WAV '{SafePathDisplay.Basename(fullPath)}' must be {AudioRequirements.RequiredSampleRate} Hz mono; found {sampleRate} Hz/{channels} channels.");
        }

        var format = audioFormat == 1 && bitsPerSample == 16
            ? AudioSampleFormat.Pcm16
            : audioFormat == 3 && bitsPerSample == 32
                ? AudioSampleFormat.Pcm32Float
                : throw new UnsupportedAudioFormatException(
                    $"WAV '{SafePathDisplay.Basename(fullPath)}' must be PCM 16-bit or IEEE float 32-bit; found format {audioFormat}, {bitsPerSample} bits.");

        var bytesPerSample = bitsPerSample / 8;
        if (data.Length == 0 || data.Length % bytesPerSample != 0)
        {
            throw new InvalidDataException(
                $"WAV data chunk has an incomplete sample: '{SafePathDisplay.Basename(fullPath)}'.");
        }

        var samples = new float[data.Length / bytesPerSample];
        for (var index = 0; index < samples.Length; index++)
        {
            var offset = index * bytesPerSample;
            samples[index] = format switch
            {
                AudioSampleFormat.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768f,
                AudioSampleFormat.Pcm32Float => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))),
                _ => throw new InvalidDataException("Unsupported WAV sample format.")
            };
        }

        return new AudioClip(fullPath, sampleRate, channels, format, samples);
        }
    }

    private static bool ReadFourCc(BinaryReader reader, string expected)
    {
        var actual = new string(reader.ReadChars(4));
        return actual == expected;
    }
}
