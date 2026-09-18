using AudioTranscriber;

namespace AudioTranscriber.Cli;

public static class SmartToolHelpRenderer
{
    public static string RenderShortHelp()
    {
        var lines = new List<string>
        {
            $"Usage: {SmartToolPaths.ToolId} <capability> [options]",
            string.Empty,
            "Capabilities:"
        };

        lines.AddRange(
            SmartToolCapabilityRegistry.All.Select(
                capability =>
                    $"  {capability.Name,-10} [{capability.Classification}] - {capability.Summary}"));
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
        var capabilityLines = SmartToolCapabilityRegistry.All.Select(
            capability =>
                $"- `{SmartToolPaths.ToolId} {capability.Name} --help` [{capability.Classification}] - {capability.Summary}");

        return $"""
            <skill_content name="{SmartToolPaths.ToolId}">
            {SmartToolManifestService.Body().Trim()}

            ## Capabilities

            Each capability is available through the library and the thin CLI adapter.
            Read a capability's own skill before invoking it:

            {string.Join(Environment.NewLine, capabilityLines)}

            <skill_resources>
              <file>SMART_TOOL.md</file>
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
}
