using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

public static class FoundryLocalPromptBuilder
{
    public const string ResponseSchemaVersion = "2.0";

    private const string SystemPrompt =
        "You enrich transcript chapters. Return only one complete JSON object. " +
        "Do not return markdown, prose, extra JSON, or code fences. Use only the " +
        "short request references provided. Never invent references.";

    public static string ChapterReference(int index) => $"c{index + 1}";

    public static string SegmentReference(int index) => $"s{index + 1}";

    public static IReadOnlyList<ChatMessage> BuildChapterPrompt(
        TranscriptChapterArtifact chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var prompt = new StringBuilder();
        prompt.AppendLine("Create a concise chapter enrichment.");
        prompt.AppendLine("Output schema:");
        prompt.AppendLine(
            """{"schemaVersion":"2.0","kind":"chapter","chapterRef":"c1","summary":"...","title":"...","keywords":["..."],"evidence":[{"segmentRef":"s1"}]}""");
        prompt.AppendLine("The title may be a string or null.");
        prompt.AppendLine("The evidence array may contain only segment references below.");
        prompt.AppendLine($"chapterRef: {ChapterReference(0)}");
        prompt.AppendLine($"artifactTitle: {chapter.Title}");
        prompt.AppendLine("sourceSegments:");

        foreach (var (segment, index) in chapter.SourceSegments.Select((segment, index) => (segment, index)))
        {
            prompt.AppendLine(
                $"- segmentRef={SegmentReference(index)}; " +
                $"start={FormatTimestamp(segment.Start)}; " +
                $"end={FormatTimestamp(segment.End)}; " +
                $"text={segment.Text}");
        }

        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, prompt.ToString().TrimEnd())
        ];
    }

    public static IReadOnlyList<ChatMessage> BuildOverallPrompt(
        TranscriptOverallSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = new StringBuilder();
        prompt.AppendLine("Create one overall summary from the chapter summaries below.");
        prompt.AppendLine("Do not infer or request the full transcript.");
        prompt.AppendLine(
            """Output schema: {"schemaVersion":"2.0","kind":"overall","summary":"...","chapterRefs":["c1"]}""");
        prompt.AppendLine($"isPartial: {request.IsPartial.ToString().ToLowerInvariant()}");
        prompt.AppendLine("chapterSummaries:");

        foreach (var (chapter, index) in request.ChapterSummaries.Select((chapter, index) => (chapter, index)))
        {
            prompt.AppendLine(
                $"- chapterRef={ChapterReference(index)}; " +
                $"title={chapter.Summary.Title ?? "<null>"}; " +
                $"keywords={string.Join(", ", chapter.Summary.Keywords)}; " +
                $"summary={chapter.Summary.Summary}");
        }

        prompt.AppendLine(
            "chapterRefs must contain each input chapterRef exactly once and in input order.");

        return
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, prompt.ToString().TrimEnd())
        ];
    }

    public static IReadOnlyList<ChatMessage> AddCorrection(
        IReadOnlyList<ChatMessage> messages,
        string kind)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Response kind cannot be empty.", nameof(kind));
        }

        return messages
            .Append(
                new ChatMessage(
                    ChatRole.User,
                    $"Correct the previous response. Return exactly one valid schema " +
                    $"2.0 {kind} JSON object, with no prose, markdown, extra properties, " +
                    "or fabricated references."))
            .ToArray();
    }

    private static string FormatTimestamp(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);
}
