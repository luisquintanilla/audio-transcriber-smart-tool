using System.Globalization;
using System.Text.Json;

namespace AudioTranscriber.TranscriptProcessing;

public sealed class TranscriptJsonReader
{
    private static readonly TranscriptNormalizer Normalizer = new();

    private static readonly HashSet<string> DocumentProperties =
        new(StringComparer.Ordinal)
        {
            "schemaVersion",
            "source",
            "provenance",
            "segments"
        };

    private static readonly HashSet<string> LegacyDocumentProperties =
        new(StringComparer.Ordinal)
        {
            "source",
            "model",
            "provider",
            "segments"
        };

    private static readonly HashSet<string> ProvenanceProperties =
        new(StringComparer.Ordinal)
        {
            "provider",
            "model",
            "packageId",
            "packageVersion",
            "source",
            "cachePath",
            "metadata"
        };

    private static readonly HashSet<string> SegmentProperties =
        new(StringComparer.Ordinal)
        {
            "id",
            "sourceId",
            "originalOrdinal",
            "start",
            "end",
            "text",
            "speaker",
            "confidence",
            "sourceMetadata"
        };

    public async ValueTask<IReadOnlyList<TranscriptDocument>> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Transcript stream must be readable.", nameof(stream));
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new TranscriptFormatException(
                "invalid_json",
                "$",
                exception.Message,
                exception);
        }

        using (document)
        {
            return ParseRoot(document.RootElement);
        }
    }

    public async ValueTask<IReadOnlyList<TranscriptDocument>> ReadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Transcript file path cannot be empty.", nameof(path));
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TranscriptDocument> ReadDocumentAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var documents = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (documents.Count != 1)
        {
            throw new TranscriptFormatException(
                "single_document_required",
                "$",
                $"Expected one transcript document but found {documents.Count}.");
        }

        return documents[0];
    }

    private static IReadOnlyList<TranscriptDocument> ParseRoot(JsonElement root)
    {
        return root.ValueKind switch
        {
            JsonValueKind.Object => [ReadDocument(root, "$")],
            JsonValueKind.Array => root.EnumerateArray()
                .Select(
                    (item, index) => item.ValueKind == JsonValueKind.Object &&
                                     item.TryGetProperty("schemaVersion", out _)
                        ? ReadDocument(item, $"$[{index}]")
                        : ReadLegacyDocument(item, $"$[{index}]"))
                .ToArray(),
            _ => throw Failure(
                "root_type",
                "$",
                "Transcript JSON must contain a document object or legacy document array.")
        };
    }

    private static TranscriptDocument ReadDocument(JsonElement element, string path)
    {
        EnsureObject(element, path);
        EnsureKnownProperties(element, DocumentProperties, path);

        var version = RequiredString(element, "schemaVersion", path);
        if (!string.Equals(version, TranscriptSchema.CurrentVersion, StringComparison.Ordinal))
        {
            throw Failure(
                "unsupported_schema_version",
                $"{path}.schemaVersion",
                $"Expected '{TranscriptSchema.CurrentVersion}'.");
        }

        var source = RequiredString(element, "source", path);
        var provenance = ReadProvenance(RequiredProperty(element, "provenance", path), $"{path}.provenance");
        var segments = ReadSegments(RequiredProperty(element, "segments", path), $"{path}.segments");
        return Normalizer.Normalize(CreateDocument(source, provenance, segments, path));
    }

    private static TranscriptDocument ReadLegacyDocument(JsonElement element, string path)
    {
        EnsureObject(element, path);
        EnsureKnownProperties(element, LegacyDocumentProperties, path);

        var source = RequiredString(element, "source", path);
        var provider = RequiredString(element, "provider", path);
        var model = RequiredString(element, "model", path);
        var segments = ReadSegments(
            RequiredProperty(element, "segments", path),
            $"{path}.segments",
            legacy: true);
        var provenance = new TranscriptProvenance(
            provider,
            model,
            source: "legacy-json");
        return Normalizer.Normalize(CreateDocument(source, provenance, segments, path));
    }

    private static TranscriptProvenance ReadProvenance(JsonElement element, string path)
    {
        EnsureObject(element, path);
        EnsureKnownProperties(element, ProvenanceProperties, path);
        var provider = RequiredString(element, "provider", path);
        var model = RequiredString(element, "model", path);
        var metadata = OptionalMetadata(element, "metadata", path);

        try
        {
            return new TranscriptProvenance(
                provider,
                model,
                OptionalString(element, "packageId", path),
                OptionalString(element, "packageVersion", path),
                OptionalString(element, "source", path),
                OptionalString(element, "cachePath", path),
                metadata);
        }
        catch (ArgumentException exception)
        {
            throw Failure("invalid_provenance", path, exception.Message, exception);
        }
    }

    private static IReadOnlyList<TranscriptSegment> ReadSegments(
        JsonElement element,
        string path,
        bool legacy = false)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Failure("segments_type", path, "Segments must be a JSON array.");
        }

        var segments = new List<TranscriptSegment>();
        var ordinals = new HashSet<int>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            EnsureObject(item, itemPath);
            EnsureKnownProperties(
                item,
                legacy
                    ? new HashSet<string>(new[] { "start", "end", "text" }, StringComparer.Ordinal)
                    : SegmentProperties,
                itemPath);

            var start = ReadTimestamp(RequiredProperty(item, "start", itemPath), $"{itemPath}.start");
            var end = ReadTimestamp(RequiredProperty(item, "end", itemPath), $"{itemPath}.end");
            var text = RequiredString(item, "text", itemPath);
            var ordinal = OptionalOrdinal(item, itemPath) ?? index;
            if (!ordinals.Add(ordinal))
            {
                throw Failure(
                    "duplicate_original_ordinal",
                    $"{itemPath}.originalOrdinal",
                    $"Original ordinal {ordinal} is used more than once.");
            }

            try
            {
                segments.Add(
                    new TranscriptSegment(
                        text,
                        start,
                        end,
                        ordinal,
                        legacy ? null : OptionalString(item, "sourceId", itemPath),
                        legacy ? null : OptionalString(item, "id", itemPath),
                        legacy ? null : OptionalString(item, "speaker", itemPath),
                        legacy ? null : OptionalConfidence(item, itemPath),
                        legacy ? null : OptionalMetadata(item, "sourceMetadata", itemPath)));
            }
            catch (ArgumentException exception)
            {
                throw Failure("invalid_segment", itemPath, exception.Message, exception);
            }

            index++;
        }

        return segments;
    }

    private static TranscriptDocument CreateDocument(
        string source,
        TranscriptProvenance provenance,
        IReadOnlyList<TranscriptSegment> segments,
        string path)
    {
        try
        {
            return new TranscriptDocument(source, provenance, segments);
        }
        catch (ArgumentException exception)
        {
            var code = exception.Message.Contains("monotonic", StringComparison.OrdinalIgnoreCase)
                ? "non_monotonic_timing"
                : exception.Message.Contains("overlap", StringComparison.OrdinalIgnoreCase)
                    ? "overlapping_timing"
                    : exception.Message.Contains("ordinal", StringComparison.OrdinalIgnoreCase)
                        ? "invalid_original_ordinal"
                        : "invalid_document";
            throw Failure(code, path, exception.Message, exception);
        }
    }

    private static TimeSpan ReadTimestamp(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            if (value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number) &&
                !double.IsFinite(number))
            {
                throw Failure("non_finite_timestamp", path, "Timestamp must be finite.");
            }

            throw Failure("timestamp_type", path, "Timestamp must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure("invalid_timestamp", path, "Timestamp cannot be empty.");
        }

        text = text.Trim();
        if (text.Equals("NaN", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("+Infinity", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure("non_finite_timestamp", path, "Timestamp must be finite.");
        }

        var parts = text.Split(':');
        if (parts.Length != 3 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            throw Failure("invalid_timestamp", path, "Expected HH:MM:SS[.fffffff].");
        }

        var secondsParts = parts[2].Split('.', 2);
        if (!int.TryParse(
                secondsParts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seconds) ||
            minutes is < 0 or >= 60 ||
            seconds is < 0 or >= 60 ||
            hours < 0)
        {
            throw Failure("invalid_timestamp", path, "Timestamp contains an invalid time component.");
        }

        var fraction = secondsParts.Length == 2 ? secondsParts[1] : string.Empty;
        if ((secondsParts.Length == 2 && fraction.Length == 0) ||
            fraction.Length > 7 ||
            (fraction.Length > 0 &&
             !fraction.All(character => character is >= '0' and <= '9')))
        {
            throw Failure("invalid_timestamp", path, "Timestamp fraction must contain one to seven digits.");
        }

        try
        {
            var ticks = checked(
                ((hours * TimeSpan.TicksPerHour) +
                 (minutes * TimeSpan.TicksPerMinute) +
                 (seconds * TimeSpan.TicksPerSecond)) +
                FractionTicks(fraction));
            return new TimeSpan(ticks);
        }
        catch (OverflowException exception)
        {
            throw Failure("invalid_timestamp", path, "Timestamp is outside the supported range.", exception);
        }
    }

    private static long FractionTicks(string fraction)
    {
        if (fraction.Length == 0)
        {
            return 0;
        }

        var padded = fraction.PadRight(7, '0');
        return long.Parse(padded, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static int? OptionalOrdinal(JsonElement element, string path)
    {
        if (!element.TryGetProperty("originalOrdinal", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var ordinal) ||
            ordinal < 0)
        {
            throw Failure(
                "invalid_original_ordinal",
                $"{path}.originalOrdinal",
                "Original ordinal must be a non-negative integer.");
        }

        return ordinal;
    }

    private static double? OptionalConfidence(JsonElement element, string path)
    {
        if (!element.TryGetProperty("confidence", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var confidence))
        {
            throw Failure(
                "invalid_confidence",
                $"{path}.confidence",
                "Confidence must be a JSON number or null.");
        }

        if (!double.IsFinite(confidence))
        {
            throw Failure(
                "non_finite_confidence",
                $"{path}.confidence",
                "Confidence must be finite.");
        }

        return confidence;
    }

    private static IEnumerable<KeyValuePair<string, string>>? OptionalMetadata(
        JsonElement element,
        string propertyName,
        string path)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Failure(
                "metadata_type",
                $"{path}.{propertyName}",
                "Metadata must be a JSON object.");
        }

        var metadata = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Failure(
                    "duplicate_metadata",
                    $"{path}.{propertyName}",
                    $"Metadata key '{property.Name}' is repeated.");
            }

            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                throw Failure(
                    "metadata_value",
                    $"{path}.{propertyName}.{property.Name}",
                    "Metadata keys must be non-empty and values must be strings.");
            }

            metadata.Add(new(property.Name, property.Value.GetString()!));
        }

        return metadata;
    }

    private static string RequiredString(JsonElement element, string propertyName, string path)
    {
        var value = RequiredProperty(element, propertyName, path);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw Failure(
                "property_type",
                $"{path}.{propertyName}",
                "Value must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure(
                "empty_property",
                $"{path}.{propertyName}",
                "Value cannot be empty or whitespace.");
        }

        return text!;
    }

    private static string? OptionalString(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Failure(
                "property_type",
                $"{path}.{propertyName}",
                "Value must be a string or null.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Failure(
                "empty_property",
                $"{path}.{propertyName}",
                "Value cannot be whitespace when supplied.");
        }

        return text;
    }

    private static JsonElement RequiredProperty(JsonElement element, string propertyName, string path)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw Failure(
                "missing_property",
                path,
                $"Required property '{propertyName}' is missing.");
        }

        return value;
    }

    private static void EnsureObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Failure("object_required", path, "Value must be a JSON object.");
        }
    }

    private static void EnsureKnownProperties(
        JsonElement element,
        IReadOnlySet<string> allowed,
        string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Failure(
                    "duplicate_property",
                    path,
                    $"Property '{property.Name}' is repeated.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw Failure(
                    "unknown_property",
                    $"{path}.{property.Name}",
                    $"Property '{property.Name}' is not part of the transcript schema.");
            }
        }
    }

    private static TranscriptFormatException Failure(
        string code,
        string path,
        string message,
        Exception? innerException = null) =>
        new(code, path, message, innerException);
}
