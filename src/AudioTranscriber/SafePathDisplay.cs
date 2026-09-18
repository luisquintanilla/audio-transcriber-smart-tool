namespace AudioTranscriber;

public static class SafePathDisplay
{
    public const string RepositoryToken = "<repository>";
    public const string ModelCacheToken = "<model-cache>";
    public const string ModelFileToken = "<model-file>";

    public static string Basename(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? "<input>" : name;
    }

    public static string RedactKnownPaths(string message, string replacement, params string?[] paths)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);

        var redacted = message;
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                redacted = redacted.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
            }
        }

        return redacted;
    }
}
