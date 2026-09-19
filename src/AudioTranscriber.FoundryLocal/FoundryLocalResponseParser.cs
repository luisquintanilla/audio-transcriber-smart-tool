using System.Globalization;
using System.Text.Json;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

public sealed class FoundryLocalResponseException : FormatException
{
    public FoundryLocalResponseException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
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
        var root = RequireObject(document.RootElement);
        RequireSchema(root, "chapter");

        var chapterId = RequiredString(root, "chapterId");
        if (!string.Equals(chapterId, chapter.Id, StringComparison.Ordinal))
        {
            throw Invalid(
                "chapter_id_mismatch",
                $"The response chapter ID '{chapterId}' does not match '{chapter.Id}'.");
        }

        var summary = RequiredString(root, "summary");
        var title = OptionalString(root, "title");
        var keywords = RequiredStringArray(root, "keywords");
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
        var root = RequireObject(document.RootElement);
        RequireSchema(root, "overall");

        var summary = RequiredString(root, "summary");
        var chapterIds = RequiredStringArray(root, "chapterIds");
        var expected = request.ChapterSummaries
            .Select(item => item.ChapterId)
            .ToArray();
        if (!chapterIds.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw Invalid(
                "overall_chapter_ids_mismatch",
                "The overall response must cite the input chapter IDs in order.");
        }

        try
        {
            return new TranscriptOverallSummary(summary, chapterIds, request.IsPartial);
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

        var sourceSegments = chapter.SourceSegments.ToDictionary(
            segment => segment.Id,
            StringComparer.Ordinal);
        var values = new List<TranscriptChapterEvidenceReference>();
        foreach (var item in evidenceElement.EnumerateArray())
        {
            var evidence = RequireObject(item);
            var sourceSegmentId = RequiredString(evidence, "sourceSegmentId");
            if (!sourceSegments.TryGetValue(sourceSegmentId, out var segment))
            {
                throw Invalid(
                    "unknown_evidence_segment",
                    $"Evidence segment '{sourceSegmentId}' is not part of chapter '{chapter.Id}'.");
            }

            var sourceId = OptionalString(evidence, "sourceId");
            if (!string.Equals(sourceId, segment.SourceId, StringComparison.Ordinal))
            {
                throw Invalid(
                    "evidence_source_mismatch",
                    $"Evidence source ID for '{sourceSegmentId}' does not match the transcript.");
            }

            var start = RequiredTimestamp(evidence, "start");
            var end = RequiredTimestamp(evidence, "end");
            if (start != segment.Start || end != segment.End)
            {
                throw Invalid(
                    "evidence_timing_mismatch",
                    $"Evidence timing for '{sourceSegmentId}' does not match the transcript.");
            }

            values.Add(
                new TranscriptChapterEvidenceReference(
                    sourceSegmentId,
                    start,
                    end,
                    sourceId));
        }

        return values;
    }

    private static JsonDocument ParseDocument(string response)
    {
        try
        {
            return JsonDocument.Parse(response);
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
        var schemaVersion = RequiredString(root, "schemaVersion");
        if (!string.Equals(
                schemaVersion,
                FoundryLocalPromptBuilder.ResponseSchemaVersion,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "unsupported_schema",
                $"Response schema '{schemaVersion}' is not supported.");
        }

        var responseKind = RequiredString(root, "kind");
        if (!string.Equals(responseKind, kind, StringComparison.Ordinal))
        {
            throw Invalid(
                "response_kind_mismatch",
                $"Expected response kind '{kind}', but received '{responseKind}'.");
        }
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("invalid_root", "The response must contain a JSON object.");
        }

        return value;
    }

    private static string RequiredString(JsonElement objectElement, string propertyName)
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

    private static string? OptionalString(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
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
        string propertyName)
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

    private static TimeSpan RequiredTimestamp(
        JsonElement objectElement,
        string propertyName)
    {
        var text = RequiredString(objectElement, propertyName);
        if (!TimeSpan.TryParseExact(
                text,
                "c",
                CultureInfo.InvariantCulture,
                out var timestamp) ||
            timestamp < TimeSpan.Zero)
        {
            throw Invalid(
                $"invalid_{propertyName}",
                $"The response property '{propertyName}' must use the invariant duration format.");
        }

        return timestamp;
    }

    private static FoundryLocalResponseException Invalid(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
