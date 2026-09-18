using System.ComponentModel;
using System.Diagnostics;

namespace AudioTranscriber;

public sealed record FfmpegExecutableResolution(
    bool IsAvailable,
    string? ExecutablePath,
    string Detail);

public interface IFfmpegExecutableResolver
{
    FfmpegExecutableResolution Resolve(string? configuredPath);
}

public sealed class FfmpegExecutableResolver : IFfmpegExecutableResolver
{
    public FfmpegExecutableResolution Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var candidate = configuredPath.Trim();
            var executablePath = ResolveCandidate(candidate);
            return executablePath is null
                ? new FfmpegExecutableResolution(
                    false,
                    null,
                    $"FFmpeg executable '{SafePathDisplay.Basename(candidate)}' was not found or is not executable. " +
                    "Provide a valid executable --ffmpeg path.")
                : new FfmpegExecutableResolution(
                    true,
                    executablePath,
                    $"FFmpeg executable is available: '{SafePathDisplay.Basename(executablePath)}'.");
        }

        var pathExecutable = FindOnPath();
        return pathExecutable is null
            ? new FfmpegExecutableResolution(
                false,
                null,
                "An executable FFmpeg was not found on PATH. Install ffmpeg and add it to PATH, or pass --ffmpeg <path>.")
            : new FfmpegExecutableResolution(
                true,
                pathExecutable,
                $"FFmpeg was found on PATH: '{SafePathDisplay.Basename(pathExecutable)}'.");
    }

    private static string? ResolveCandidate(string candidate)
    {
        if (!Path.IsPathRooted(candidate) &&
            !candidate.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !candidate.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return FindOnPath(candidate);
        }

        return File.Exists(candidate) && IsExecutable(candidate)
            ? Path.GetFullPath(candidate)
            : null;
    }

    private static string? FindOnPath(string executableName = "ffmpeg")
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedDirectory = directory.Trim('"');
            var candidates = OperatingSystem.IsWindows()
                ? new[]
                {
                    Path.Combine(normalizedDirectory, executableName),
                    Path.Combine(normalizedDirectory, executableName + ".exe")
                }
                : [Path.Combine(normalizedDirectory, executableName)];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && IsExecutable(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        return mode.HasFlag(UnixFileMode.UserExecute) ||
               mode.HasFlag(UnixFileMode.GroupExecute) ||
               mode.HasFlag(UnixFileMode.OtherExecute);
    }
}

public sealed record FfmpegRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IFfmpegRunner
{
    Task<FfmpegRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessFfmpegRunner : IFfmpegRunner
{
    public async Task<FfmpegRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new FfmpegUnavailableException(
                    $"FFmpeg executable '{SafePathDisplay.Basename(executablePath)}' could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new FfmpegUnavailableException(
                $"FFmpeg executable '{SafePathDisplay.Basename(executablePath)}' could not be started. " +
                "Install ffmpeg, add it to PATH, or pass --ffmpeg <path>.",
                exception);
        }

        try
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new FfmpegRunResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            StopProcess(process);
            throw;
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
