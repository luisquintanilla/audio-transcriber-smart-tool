using AudioTranscriber;

namespace AudioTranscriber.Cli;

public static class SmartToolHelpRenderer
{
    private static readonly IReadOnlyList<(string Title, string[] Names)> CapabilityGroups =
    [
        ("Introspection and diagnostics", ["manifest", "doctor"]),
        ("Audio processing", ["convert"]),
        ("Speech recognition", ["transcribe"]),
        ("Transcript processing", ["chapters", "enrich"])
    ];

    public static string RenderShortHelp()
    {
        var lines = new List<string>
        {
            $"Usage: {SmartToolPaths.ToolId} <capability> [options]",
            string.Empty,
            "Capabilities:"
        };

        foreach (var group in CapabilityGroups)
        {
            lines.Add(string.Empty);
            lines.Add($"{group.Title}:");
            lines.AddRange(RenderCapabilitySummaries(group.Names));
        }

        lines.Add(string.Empty);
        lines.Add($"Run '{SmartToolPaths.ToolId} --help' for the full tool skill.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string RenderUsage() =>
        $"""
        Usage:
          {SmartToolPaths.ToolId} -h
          {SmartToolPaths.ToolId} --help
          {SmartToolPaths.ToolId} <capability> [options]

        Run '{SmartToolPaths.ToolId} -h' for the capability summary.
        """;

    public static string RenderToolHelp()
    {
        return $"""
            <skill_content name="{SmartToolPaths.ToolId}">
            {SmartToolManifestService.Body().Trim()}

            ## Capabilities

            Each capability is available through the library and the thin CLI adapter.
            Read a capability's own skill before invoking it:

            {string.Join(Environment.NewLine, RenderCapabilityLinks())}

            <skill_resources>
              <file>src/AudioTranscriber/SMART_TOOL.md</file>
            </skill_resources>
            </skill_content>
            """;
    }

    public static string RenderCapabilityHelp(string name, bool terse = false)
    {
        if (!SmartToolCapabilityRegistry.TryGet(name, out var capability))
        {
            throw new ArgumentException($"Unknown capability: {name}.", nameof(name));
        }

        return terse
            ? $"{SmartToolPaths.ToolId} {capability.Name} [{capability.Classification}] - {capability.Summary}{Environment.NewLine}"
            : capability.Skill.TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<string> RenderCapabilitySummaries(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (SmartToolCapabilityRegistry.TryGet(name, out var capability))
            {
                yield return $"  {capability.Name,-10} [{capability.Classification}] - {capability.Summary}";
            }
        }
    }

    private static IEnumerable<string> RenderCapabilityLinks()
    {
        foreach (var group in CapabilityGroups)
        {
            yield return $"### {group.Title}";
            foreach (var name in group.Names)
            {
                if (SmartToolCapabilityRegistry.TryGet(name, out var capability))
                {
                    yield return $"- `{SmartToolPaths.ToolId} {capability.Name} --help` [{capability.Classification}] - {capability.Summary}";
                }
            }
        }
    }
}
