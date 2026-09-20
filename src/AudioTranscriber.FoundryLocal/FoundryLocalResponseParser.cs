using System.Text.Json;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

public sealed class FoundryLocalResponseException
    : FormatException, ITranscriptProviderFailure
{
    public FoundryLocalResponseException(
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    public string SanitizedMessage => Message;
}

public static class FoundryLocalResponseParser
{
    public static TranscriptChapterSummary ParseChapterSummary(
        string response,
        TranscriptChapterArtifact chapter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentNullException.ThrowIfNull(chapter);

        using var document = ParseDocument(response);
        var root = RequireObject(document.RootElement, "$");
        RequireKnownProperties(
            root,
            "$",
            "schemaVersion",
            "kind",
            "chapterRef",
            "summary",
            "title",
            "keywords",
            "evidence");
        RequireSchema(root, "chapter");

        var chapterRef = RequiredString(root, "chapterRef", "$");
        if (!string.Equals(
                chapterRef,
                FoundryLocalPromptBuilder.ChapterReference(0),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "chapter_ref_mismatch",
                "The response chapter reference does not match the request.");
        }

        var summary = RequiredString(root, "summary", "$");
        var title = RequiredNullableString(root, "title", "$");
        var keywords = RequiredStringArray(root, "keywords", "$");
        var evidence = ParseEvidence(root, chapter);

        try
        {
            return new TranscriptChapterSummary(summary, keywords, title, evidence);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "invalid_chapter_summary",
                "The response did not satisfy the chapter summary contract.",
                exception);
        }
    }

    public static TranscriptOverallSummary ParseOverallSummary(
        string response,
        TranscriptOverallSummaryRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentNullException.ThrowIfNull(request);

        using var document = ParseDocument(response);
        var root = RequireObject(document.RootElement, "$");
        RequireKnownProperties(
            root,
            "$",
            "schemaVersion",
            "kind",
            "summary",
            "chapterRefs");
        RequireSchema(root, "overall");

        var summary = RequiredString(root, "summary", "$");
        var chapterRefs = RequiredStringArray(root, "chapterRefs", "$");
        var expectedRefs = request.ChapterSummaries
            .Select((_, index) => FoundryLocalPromptBuilder.ChapterReference(index))
            .ToArray();
        if (!chapterRefs.SequenceEqual(expectedRefs, StringComparer.Ordinal))
        {
            throw Invalid(
                "overall_chapter_refs_mismatch",
                "The overall response must cite each input chapter reference in order.");
        }

        var chapterIds = request.ChapterSummaries
            .Select(item => item.ChapterId)
            .ToArray();
        try
        {
            return new TranscriptOverallSummary(
                summary,
                chapterIds,
                request.IsPartial);
        }
        catch (ArgumentException exception)
        {
            throw Invalid(
                "invalid_overall_summary",
                "The response did not satisfy the overall summary contract.",
                exception);
        }
    }

    private static IReadOnlyList<TranscriptChapterEvidenceReference> ParseEvidence(
        JsonElement root,
        TranscriptChapterArtifact chapter)
    {
        if (!root.TryGetProperty("evidence", out var evidenceElement) ||
            evidenceElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(
                "missing_evidence",
                "The chapter response must contain an evidence array.");
        }

        var sourceSegments = chapter.SourceSegments
            .Select(
                (segment, index) =>
                    (Reference: FoundryLocalPromptBuilder.SegmentReference(index), Segment: segment))
            .ToDictionary(item => item.Reference, item => item.Segment, StringComparer.Ordinal);
        var values = new List<TranscriptChapterEvidenceReference>();
        foreach (var (item, index) in evidenceElement.EnumerateArray().Select((item, index) => (item, index)))
        {
            var path = $"$.evidence[{index}]";
            var evidence = RequireObject(item, path);
            RequireKnownProperties(evidence, path, "segmentRef");
            var segmentRef = RequiredString(evidence, "segmentRef", path);
            if (!sourceSegments.TryGetValue(segmentRef, out var segment))
            {
                throw Invalid(
                    "unknown_evidence_segment_ref",
                    $"Evidence reference '{segmentRef}' is not part of the chapter request.");
            }

            values.Add(
                new TranscriptChapterEvidenceReference(
                    segment.Id,
                    segment.Start,
                    segment.End,
                    segment.SourceId));
        }

        return values;
    }

    public static string ExtractJsonEnvelope(string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            const string prefix = "```json";
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal) ||
                !trimmed.EndsWith("```", StringComparison.Ordinal) ||
                trimmed.Length <= prefix.Length + 3)
            {
                throw Invalid(
                    "invalid_response_envelope",
                    "The response must be raw JSON or exactly one complete json code fence.");
            }

            var body = trimmed[prefix.Length..^3];
            if (body.Length > 0 && (body[0] == '\r' || body[0] == '\n'))
            {
                body = body.TrimStart('\r', '\n');
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                throw Invalid(
                    "invalid_response_envelope",
                    "The json code fence must contain one complete JSON document.");
            }

            return body.Trim();
        }

        if (trimmed.Contains("```", StringComparison.Ordinal))
        {
            throw Invalid(
                "invalid_response_envelope",
                "The response must be raw JSON or exactly one complete json code fence.");
        }

        return trimmed;
    }

    private static JsonDocument ParseDocument(string response)
    {
        var json = ExtractJsonEnvelope(response);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw Invalid(
                "invalid_json",
                "The Foundry Local response was not valid JSON.",
                exception);
        }
    }

    private static void RequireSchema(JsonElement root, string kind)
    {
        var schemaVersion = RequiredString(root, "schemaVersion", "$");
        if (!string.Equals(
                schemaVersion,
                FoundryLocalPromptBuilder.ResponseSchemaVersion,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "unsupported_schema",
                $"Response schema '{schemaVersion}' is not supported.");
        }

        var responseKind = RequiredString(root, "kind", "$");
        if (!string.Equals(responseKind, kind, StringComparison.Ordinal))
        {
            throw Invalid(
                "response_kind_mismatch",
                $"Expected response kind '{kind}', but received '{responseKind}'.");
        }
    }

    private static JsonElement RequireObject(JsonElement value, string path)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("invalid_root", $"'{path}' must be a JSON object.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement objectElement,
        string propertyName,
        string path)
    {
        if (!objectElement.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid(
                $"missing_{propertyName}",
                $"The response must contain a non-empty '{propertyName}' string.");
        }

        return value.GetString()!.Trim();
    }

    private static string? RequiredNullableString(
        JsonElement objectElement,
        string propertyName,
        string path)
    {
        if (!objectElement.TryGetProperty(propertyName, out var value))
        {
            throw Invalid(
                $"missing_{propertyName}",
                $"The response must contain '{propertyName}' as a string or null.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid(
                $"invalid_{propertyName}",
                $"The response property '{propertyName}' must be a string or null.");
        }

        return string.IsNullOrWhiteSpace(value.GetString())
            ? null
            : value.GetString()!.Trim();
    }

    private static IReadOnlyList<string> RequiredStringArray(
        JsonElement objectElement,
        string propertyName,
        string path)
    {
        if (!objectElement.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(
                $"missing_{propertyName}",
                $"The response must contain a '{propertyName}' array.");
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw Invalid(
                    $"invalid_{propertyName}",
                    $"The response '{propertyName}' array must contain non-empty strings.");
            }

            values.Add(item.GetString()!.Trim());
        }

        return values;
    }

    private static void RequireKnownProperties(
        JsonElement root,
        string path,
        params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw Invalid("duplicate_property", $"Response property '{property.Name}' is repeated.");
            }

            if (!allowed.Contains(property.Name))
            {
                throw Invalid(
                    "unknown_property",
                    $"Response property '{property.Name}' is not part of schema 2.0.");
            }
        }
    }

    private static FoundryLocalResponseException Invalid(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
