using System.Diagnostics.CodeAnalysis;

namespace AudioTranscriber;

public enum SmartToolCapabilityKind
{
    Deterministic,
    ModelBacked
}

public sealed record SmartToolCapability(
    string Name,
    SmartToolCapabilityKind Kind,
    string Summary,
    string Invocation,
    string Skill)
{
    public string Classification =>
        Kind == SmartToolCapabilityKind.ModelBacked
            ? "model-backed"
            : "deterministic";
}

public static class SmartToolCapabilityRegistry
{
    private static readonly IReadOnlyList<SmartToolCapability> RegisteredCapabilities =
        CreateCapabilities();

    public static IReadOnlyList<SmartToolCapability> All => RegisteredCapabilities;

    public static bool TryGet(
        string name,
        [NotNullWhen(true)] out SmartToolCapability? capability)
    {
        capability = RegisteredCapabilities.FirstOrDefault(
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))!;
        return capability is not null;
    }

    private static IReadOnlyList<SmartToolCapability> CreateCapabilities() =>
    [
        new(
            "manifest",
            SmartToolCapabilityKind.Deterministic,
            "Print the canonical SMART_TOOL.md manifest.",
            $"{SmartToolPaths.ToolId} manifest",
            """
            # audio-transcriber manifest

            ## When to use

            Use this capability when a caller needs the canonical manifest document
            shipped with the tool, including its frontmatter and agent-facing body.

            ## Determinism

            This capability is deterministic. It reads the library-embedded manifest
            and does not use a model, network, credentials, or local files.

            ## Arguments

            No arguments are accepted.

            ## Worked invocation

            ```text
            audio-transcriber manifest
            ```

            ## Result

            Stdout contains the canonical `SMART_TOOL.md` document exactly as shipped.
            Stderr is empty on success. The exit code is `0`.

            ## Failures

            Extra arguments are a usage failure and return exit code `2`. A missing or
            invalid embedded manifest returns a non-zero integration failure with an
            actionable error.
            """
        ),
        new(
            "doctor",
            SmartToolCapabilityKind.Deterministic,
            "Inspect model-cache, Whisper.net, FFmpeg, and package readiness.",
            $"{SmartToolPaths.ToolId} doctor",
            """
            # audio-transcriber doctor

            ## When to use

            Use this capability to inspect local readiness before conversion or
            transcription. It is safe to run as a preflight check.

            ## Determinism

            This capability is deterministic. It checks paths and installed
            integrations without downloading models, prompting, or invoking a model.

            ## Arguments

            No arguments are accepted. The current working directory is used only as
            the repository-root reference for the cache containment check.

            ## Worked invocation

            ```text
            audio-transcriber doctor
            ```

            ## Result

            Stdout contains indented JSON with `healthy`, redacted path tokens, and
            named integration checks. The exit code is `0` when the cache policy is
            healthy and `1` when it is not. A blocked optional integration is reported
            in the JSON without preventing deterministic diagnostics.

            ## Failures

            Extra arguments are a usage failure and return exit code `2`. Invalid
            repository-path input or an integration inspection error returns a
            non-zero failure with a remediation message.
            """
        ),
        new(
            "convert",
            SmartToolCapabilityKind.Deterministic,
            "Convert audio to 16 kHz mono 16-bit PCM WAV through FFmpeg.",
            $"{SmartToolPaths.ToolId} convert --input <path> --output <file.wav> [--ffmpeg <path>] [--force]",
            """
            # audio-transcriber convert

            ## When to use

            Use this capability when a source audio file is not already a valid
            16 kHz, mono, 16-bit PCM WAV for `transcribe`.

            ## Determinism

            This capability is deterministic. It uses the requested external FFmpeg
            executable and performs no model inference. The same input, FFmpeg
            version, and options produce the same conversion result.

            ## Arguments

            - `--input <path>` is the source audio path and is required.
            - `--output <file.wav>` is the destination path and is required.
            - `--ffmpeg <path>` selects an executable; otherwise FFmpeg is resolved
              from `PATH`.
            - `--force` permits replacing an existing destination.

            ## Worked invocation

            ```text
            audio-transcriber convert --input .\source.audio --output .\speech.wav
            ```

            ## Result

            The source is preserved. The destination is validated as 16,000 Hz,
            mono, 16-bit PCM WAV and a one-line result is written to stdout. The
            exit code is `0`.

            ## Failures

            Missing FFmpeg, missing input, an existing destination without `--force`,
            unsupported source data, or a failed output validation returns a non-zero
            error. The command never downloads or bundles FFmpeg and never overwrites
            an existing destination implicitly.
            """
        ),
        new(
            "transcribe",
            SmartToolCapabilityKind.ModelBacked,
            "Run local Whisper Base inference and render timestamped transcripts.",
            $"{SmartToolPaths.ToolId} transcribe --input <file.wav> [--input <file.wav>] [--format text|json|srt|webvtt] [--output <path>]",
            """
            # audio-transcriber transcribe

            ## When to use

            Use this capability to transcribe one or more local speech WAV files
            after they meet the 16 kHz, mono, PCM input contract. Use `convert`
            explicitly for other source formats.

            ## Determinism

            This capability is model-backed local inference. It uses Whisper.net with
            the pinned Whisper Base artifact and may produce model-dependent output.
            It does not require a remote provider credential, but the first run needs
            network access to download the verified model unless it is already in the
            external user cache.

            ## Arguments

            - `--input <file.wav>` is required and may be repeated for batch input.
            - `--format text|json|srt|webvtt` selects the renderer and defaults to
              `text`.
            - `--output <path>` writes the rendered result to the caller-selected
              path instead of stdout.

            ## Worked invocation

            ```text
            audio-transcriber transcribe --input .\speech.wav --format json
            ```

            ## Result

            Stdout contains the selected transcript representation unless
            `--output` is supplied. Results contain timestamped segments, input
            basenames, and model provenance without absolute local paths. The exit
            code is `0` on complete success.

            ## Failures

            Invalid arguments return exit code `2`. Invalid or missing WAV input,
            model download or checksum failures, native runtime failures, and
            inference failures return a non-zero error. The command never falls
            back to fabricated or degraded deterministic text and never prompts.
            """
        )
    ];
}
