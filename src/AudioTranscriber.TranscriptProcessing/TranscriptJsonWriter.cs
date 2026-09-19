using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace AudioTranscriber.TranscriptProcessing;

public sealed class TranscriptJsonWriter
{
    public string Write(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return WriteMany([document]);
    }

    public string WriteMany(IEnumerable<TranscriptDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var items = documents.ToArray();
        if (items.Any(document => document is null))
        {
            throw new ArgumentException("Documents cannot contain null entries.", nameof(documents));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            if (items.Length == 1)
            {
                WriteDocument(writer, items[0]);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var document in items)
                {
                    WriteDocument(writer, document);
                }

                writer.WriteEndArray();
            }

            writer.Flush();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static void WriteDocument(Utf8JsonWriter writer, TranscriptDocument document)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", document.SchemaVersion);
        writer.WriteString("source", document.Source);

        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("provider", document.Provenance.Provider);
        writer.WriteString("model", document.Provenance.Model);
        WriteOptionalString(writer, "packageId", document.Provenance.PackageId);
        WriteOptionalString(writer, "packageVersion", document.Provenance.PackageVersion);
        WriteOptionalString(writer, "source", document.Provenance.Source);
        WriteOptionalString(writer, "cachePath", document.Provenance.CachePath);
        WriteMetadata(writer, "metadata", document.Provenance.Metadata);
        writer.WriteEndObject();

        writer.WritePropertyName("segments");
        writer.WriteStartArray();
        foreach (var segment in document.Segments)
        {
            writer.WriteStartObject();
            writer.WriteString("id", segment.Id);
            WriteOptionalString(writer, "sourceId", segment.SourceId);
            writer.WriteNumber("originalOrdinal", segment.OriginalOrdinal);
            writer.WriteString("start", FormatTimestamp(segment.Start));
            writer.WriteString("end", FormatTimestamp(segment.End));
            writer.WriteString("text", segment.Text);
            WriteOptionalString(writer, "speaker", segment.Speaker);
            if (segment.Confidence is null)
            {
                writer.WriteNull("confidence");
            }
            else
            {
                writer.WriteNumber("confidence", segment.Confidence.Value);
            }

            WriteMetadata(writer, "sourceMetadata", segment.SourceMetadata);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteMetadata(
        Utf8JsonWriter writer,
        string name,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count == 0)
        {
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var entry in metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }

    internal static string FormatTimestamp(TimeSpan value)
    {
        var totalHours = value.Ticks / TimeSpan.TicksPerHour;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Ticks % TimeSpan.TicksPerSecond:0000000}");
    }
}
