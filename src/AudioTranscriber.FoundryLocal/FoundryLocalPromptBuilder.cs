using System.Globalization;
using System.Text;
using AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.FoundryLocal;

public static class FoundryLocalPromptBuilder
{
    public const string ResponseSchemaVersion = "1.0";

    private const string SystemPrompt =
        "You enrich transcript chapters. Return only one JSON object, with no " +
        "markdown fences or commentary. Copy evidence IDs, source IDs, and " +
        "timestamps exactly from the input. Never invent evidence.";

    public static IReadOnlyList<FoundryLocalChatMessage> BuildChapterPrompt(
        TranscriptChapterArtifact chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);

        var prompt = new StringBuilder();
        prompt.AppendLine("Create a concise chapter enrichment.");
        prompt.AppendLine("Output schema:");
        prompt.AppendLine(
            """{"schemaVersion":"1.0","kind":"chapter","chapterId":"...","summary":"...","title":"...","keywords":["..."],"evidence":[{"sourceSegmentId":"...","sourceId":"...","start":"c","end":"c"}]}""");
        prompt.AppendLine("The evidence array may contain only source segments below.");
        prompt.AppendLine($"chapterId: {chapter.Id}");
        prompt.AppendLine($"artifactTitle: {chapter.Title}");
        prompt.AppendLine($"start: {FormatTimestamp(chapter.Start)}");
        prompt.AppendLine($"end: {FormatTimestamp(chapter.End)}");
        prompt.AppendLine("sourceSegments:");

        foreach (var segment in chapter.SourceSegments)
        {
            prompt.AppendLine(
                $"- id={segment.Id}; " +
                $"sourceId={segment.SourceId ?? "<null>"}; " +
                $"start={FormatTimestamp(segment.Start)}; " +
                $"end={FormatTimestamp(segment.End)}; " +
                $"text={segment.Text}");
        }

        return
        [
            new FoundryLocalChatMessage("system", SystemPrompt),
            new FoundryLocalChatMessage("user", prompt.ToString().TrimEnd())
        ];
    }

    public static IReadOnlyList<FoundryLocalChatMessage> BuildOverallPrompt(
        TranscriptOverallSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = new StringBuilder();
        prompt.AppendLine("Create one overall summary from the chapter summaries below.");
        prompt.AppendLine("Do not infer or request the full transcript.");
        prompt.AppendLine(
            """Output schema: {"schemaVersion":"1.0","kind":"overall","summary":"...","chapterIds":["..."]}""");
        prompt.AppendLine($"isPartial: {request.IsPartial.ToString().ToLowerInvariant()}");
        prompt.AppendLine("chapterSummaries:");

        foreach (var chapter in request.ChapterSummaries)
        {
            prompt.AppendLine(
                $"- chapterId={chapter.ChapterId}; " +
                $"title={chapter.Summary.Title ?? "<null>"}; " +
                $"keywords={string.Join(", ", chapter.Summary.Keywords)}; " +
                $"summary={chapter.Summary.Summary}");
        }

        prompt.AppendLine(
            "chapterIds must contain each input chapterId exactly once and in input order.");

        return
        [
            new FoundryLocalChatMessage("system", SystemPrompt),
            new FoundryLocalChatMessage("user", prompt.ToString().TrimEnd())
        ];
    }

    private static string FormatTimestamp(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);
}
