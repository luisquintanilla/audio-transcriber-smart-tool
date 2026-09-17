using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTranscriber;

public enum TranscriptOutputFormat
{
    Text,
    Json,
    Srt,
    WebVtt
}

public interface ITranscriptRenderer
{
    string Render(BatchTranscript batch);
}

public static class TranscriptRenderers
{
    public static ITranscriptRenderer Create(TranscriptOutputFormat format) =>
        format switch
        {
            TranscriptOutputFormat.Text => new TextTranscriptRenderer(),
            TranscriptOutputFormat.Json => new JsonTranscriptRenderer(),
            TranscriptOutputFormat.Srt => new SrtTranscriptRenderer(),
            TranscriptOutputFormat.WebVtt => new WebVttTranscriptRenderer(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    public static bool TryParse(string value, out TranscriptOutputFormat format)
    {
        if (Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format))
        {
            return true;
        }

        format = default;
        return false;
    }
}

public sealed class TextTranscriptRenderer : ITranscriptRenderer
{
    public string Render(BatchTranscript batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var output = new StringBuilder();
        foreach (var transcript in batch.Transcripts)
        {
            if (output.Length > 0)
            {
                output.Append('\n');
                output.Append('\n');
            }

            output.Append($"# {SafePathDisplay.Basename(transcript.SourcePath)}\n");
            foreach (var segment in transcript.Segments)
            {
                output.Append('[')
                    .Append(TimestampFormatting.Format(segment.Start, '.'))
                    .Append(" - ")
                    .Append(TimestampFormatting.Format(segment.End, '.'))
                    .Append("] ")
                    .Append(segment.Text)
                    .Append('\n');
            }
        }

        return output.ToString();
    }
}

public sealed class JsonTranscriptRenderer : ITranscriptRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    public string Render(BatchTranscript batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var model = batch.Transcripts.Select(transcript => new
        {
            source = SafePathDisplay.Basename(transcript.SourcePath),
            model = transcript.Provenance.Model,
            provider = transcript.Provenance.Provider,
            segments = transcript.Segments.Select(segment => new
            {
                start = TimestampFormatting.Format(segment.Start, '.'),
                end = TimestampFormatting.Format(segment.End, '.'),
                text = segment.Text
            })
        });

        return JsonSerializer.Serialize(model, Options) + Environment.NewLine;
    }
}

public sealed class SrtTranscriptRenderer : ITranscriptRenderer
{
    public string Render(BatchTranscript batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var output = new StringBuilder();
        var cueNumber = 1;
        foreach (var transcript in batch.Transcripts)
        {
            foreach (var segment in transcript.Segments)
            {
                output.Append(cueNumber++)
                    .Append('\n')
                    .Append(TimestampFormatting.Format(segment.Start, ','))
                    .Append(" --> ")
                    .Append(TimestampFormatting.Format(segment.End, ','))
                    .Append('\n')
                    .Append(segment.Text)
                    .Append("\n\n");
            }
        }

        return output.ToString();
    }
}

public sealed class WebVttTranscriptRenderer : ITranscriptRenderer
{
    public string Render(BatchTranscript batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var transcript in batch.Transcripts)
        {
            foreach (var segment in transcript.Segments)
            {
                output.Append(TimestampFormatting.Format(segment.Start, '.'))
                    .Append(" --> ")
                    .Append(TimestampFormatting.Format(segment.End, '.'))
                    .Append('\n')
                    .Append(segment.Text)
                    .Append("\n\n");
            }
        }

        return output.ToString();
    }
}

internal static class TimestampFormatting
{
    public static string Format(TimeSpan value, char millisecondsSeparator)
    {
        var totalHours = (long)value.TotalHours;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}{millisecondsSeparator}{value.Milliseconds:000}");
    }
}
