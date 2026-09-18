---
smart_tool_format: 1
name: audio-transcriber
version: 0.1.0
description: >
  Prepare local audio for Whisper transcription and run timestamped local
  speech recognition with explicit deterministic conversion and diagnostics.
use_cases:
  - Prepare local audio for speech recognition
  - Check whether a WAV file meets the transcription input contract
  - Produce timestamped transcripts from local speech recordings
  - Render transcripts for people or downstream programs
  - Check local model, cache, FFmpeg, and package readiness
platforms:
  - windows
requires:
  - name: ffmpeg
    purpose: Required only by the deterministic convert capability; other capabilities remain available without it.
    optional: true
    install: https://ffmpeg.org/download.html
  - name: network access
    purpose: Needed by the model-backed transcribe capability when the verified Whisper Base artifact is not already cached.
    optional: true
    install: https://huggingface.co/sandrohanea/whisper.net
---

# audio-transcriber

`audio-transcriber` is a .NET 10 library-first Smart Tool for local audio
preparation and model-backed speech transcription. The `AudioTranscriber`
library owns every capability and its self-description; the CLI only parses
arguments, adapts file paths, renders library-provided help, writes
stdout/stderr, and returns exit codes.

## When to reach for it

Use this tool when the input is local audio and the caller needs either a
validated transcription WAV or a timestamped local Whisper transcript. It is
not a summarizer, diarization service, streaming service, remote provider, or
podcast pipeline.

Conversion is explicit and never happens implicitly inside `transcribe`.
State, model caches, and temporary files stay outside the source or install
tree. Output artifacts are written only where the caller asks for them.

## Use and help

The top-level `-h` is a terse capability summary. The top-level `--help` is
the full tool skill. Read the capability skill before invoking a capability:

```powershell
audio-transcriber --help
audio-transcriber transcribe --help
```

The deterministic capabilities are `manifest`, `doctor`, and `convert`.
`transcribe` is model-backed local inference. The library APIs are
the composable surface for callers that need typed values instead of CLI text.

## Command semantics

- `manifest` prints the canonical `SMART_TOOL.md` document shipped by the
  library.
- `doctor` checks cache placement and local integration readiness without
  downloading a model. It returns exit code `0` when the cache policy is
  healthy; blocked optional integrations are reported in its JSON rather than
  treated as command failures.

## Source checkout and local package install

`dotnet tool install` does not install directly from a Git URL. From a source
checkout, a .NET 10 SDK is required to restore and run the PackAsTool project
explicitly:

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- --help
```

The SDK can be installed from the official .NET download page above. `dotnetup`
is an optional SDK manager, not a launcher dependency; when available, it can
install the required channel with `dotnetup sdk install 10.0`. See its current
guidance at
`https://github.com/dotnet/sdk/blob/release/dnup/documentation/general/dotnetup/usecases/update-installations.md`.

The SDK is needed for a source checkout, restore, test, or pack operation. An
already-packed tool invocation needs the matching .NET 10 runtime, not the SDK.

For an isolated installed tool, pack first and install the local `.nupkg`
through a temporary tool manifest:

```powershell
New-Item -ItemType Directory -Force .\artifacts\tool | Out-Null
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
$packageDirectory = (Resolve-Path .\artifacts\tool).Path
$installDirectory = Join-Path $env:TEMP ("audio-transcriber-install-" + [guid]::NewGuid().ToString("N"))
dotnet new tool-manifest --output $installDirectory
Push-Location $installDirectory
dotnet tool install audio-transcriber --version 0.1.0 --add-source $packageDirectory
dotnet tool run audio-transcriber -- --help
Pop-Location
```

Git source checkout and local package installation remain supported. The
temporary distribution target is the owner's GitHub Packages NuGet feed,
`https://nuget.pkg.github.com/luisquintanilla/index.json`, for package
`audio-transcriber` version `0.1.0`, not nuget.org or the shared catalog.
Even public GitHub NuGet packages require authenticated installation with
`read:packages`. See the repository README's GitHub Packages installation
section for a separate feed config and process-scoped credentials; source
restore does not require GitHub Packages credentials. The `.nupkg`
contains the library, the canonical `SMART_TOOL.md`, and `smart-tool.json`; no
model binaries or FFmpeg binaries are packaged. Replace the local
`--add-source` path with another checkout's package directory when needed.

## Typed library installation

The companion `AudioTranscriber` NuGet package, version `0.1.0`, is the normal
.NET 10 library dependency. Use `PackageReference` to `AudioTranscriber` for
typed APIs; `audio-transcriber` (with a hyphen) is a separate CLI tool package,
not an application dependency. Both target the owner's GitHub Packages feed.
See the README's typed library consumption section and `NuGet.Library.config`
for authenticated restore with package source mapping: the library comes from
GitHub, while its default Whisper dependencies come from nuget.org.

`SmartToolManifestService.Create()` returns a typed manifest and
`SmartToolManifestService.Markdown()` returns this document from an embedded
resource, so introspection does not require source files or model downloads.
Git checkout and direct project-reference consumption remain supported.

## Capability boundaries

`convert` requires an external FFmpeg executable from `PATH` or `--ffmpeg` and
never downloads one. `transcribe` requires a valid 16 kHz mono PCM WAV and
downloads the verified Whisper Base artifact only when it is absent from the
external user cache. Missing prerequisites fail explicitly; the CLI never
prompts or returns fabricated/degraded text. Exit code `2` means usage failure,
`1` means input or integration failure, `4` means cancellation, and `0` means
success.

## Model integration status

The default backend is:

| Component | Version / value |
| --- | --- |
| `Whisper.net` | `1.9.0` |
| `Whisper.net.Runtime` | `1.9.0` |
| Upstream model family | `openai/whisper-base` |
| Artifact repository | `sandrohanea/whisper.net` |
| Artifact revision | `v5/classic` |
| Artifact file | `ggml-base.bin` |
| Model URL | `https://huggingface.co/sandrohanea/whisper.net/resolve/v5/classic/ggml-base.bin` |
| SHA-256 | `60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe` |

`openai/whisper-base` is the semantic upstream model family. The downloaded
artifact is the pinned `ggml-base.bin` file from the
`sandrohanea/whisper.net` Hugging Face repository; provenance does not imply
that the artifact was downloaded from an `openai` repository.

The first transcription downloads the pinned model to
`%LOCALAPPDATA%\AudioTranscriber\model-cache` on Windows (or the platform
equivalent). Existing files are hash-verified, downloads use a temporary file,
and the verified file is moved into place atomically. Model binaries are never
stored in the repository.

The real model path was smoke-tested with the public
`whisper.cpp` `samples/jfk.wav` speech fixture (downloaded to the system
temporary directory, not committed):

```powershell
$fixture = Join-Path $env:TEMP 'audio-transcriber-jfk.wav'
curl.exe -L --fail --output $fixture 'https://raw.githubusercontent.com/ggerganov/whisper.cpp/master/samples/jfk.wav'
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore --configuration Release -- transcribe --input $fixture --format json
```

The verified output contained one non-empty bounded segment:

```json
{
  "start": "00:00:00.000",
  "end": "00:00:11.000",
  "text": "And so my fellow Americans, ask not what your country can do for you, ask what you can do for your country."
}
```

The same path can be exercised as an opt-in test. It downloads and verifies
the model through the external cache when the cache is absent:

```powershell
$env:AUDIO_TRANSCRIBER_REAL_MODEL_FIXTURE = $fixture
$env:AUDIO_TRANSCRIBER_REAL_MODEL_CACHE = Join-Path $env:TEMP ("audio-transcriber-real-model-" + [guid]::NewGuid().ToString("N"))
dotnet test .\AudioTranscriber.sln --no-restore --configuration Release --filter "FullyQualifiedName~Real_model_smoke"
```

When `AUDIO_TRANSCRIBER_REAL_MODEL_CACHE` is set to a new external directory,
this test downloads and verifies `ggml-base.bin` before inference. The test is
skipped by default so ordinary deterministic test runs never download model
binaries or require network access.

The repository also retains an explicit, opt-in `ModelGardenWhisperEngine`
seam for the requested Model Garden/ML.NET integration:

| Package | Version |
| --- | --- |
| `DotnetAILab.ModelGarden.ASR.WhisperBase` | `0.1.0` source `VersionPrefix` |
| `ModelPackages` | `0.1.0-preview.15` |
| `MLNet.AudioInference.Onnx` | `0.1.0-preview.2` |

The WhisperBase source facade at commit
`c8f5f3a2127124463a7fcdb7acfcc787f7fe5824` returns the concrete
`OnnxWhisperTransformer`. Its pinned `MLNet.AudioInference.Onnx`
`0.1.0-preview.2` source exposes
`TranscribeWithTimestamps(IReadOnlyList<AudioData>)` with model-derived
segments, even though the facade README documents only the plain-text
`Transcribe` method. The optional engine remains unavailable here because the
package is not listed on public NuGet.org and its source workflow publishes to
the `luisquintanilla` GitHub Packages feed, which requires authentication.
No model downloader or credential is committed here.

To attempt the opt-in dependency check in an environment with authorized feed
access:

```powershell
dotnet restore .\AudioTranscriber.sln -p:EnableWhisperModelGarden=true --source https://nuget.pkg.github.com/luisquintanilla/index.json
```

This is expected to fail without package-feed access. The default build and
tests do not restore the optional Model Garden packages. The default Whisper.net
backend is publicly restorable from NuGet.org.

An opt-in clean-checkout probe remains externally blocked with `NU1301`/`NU1101`
because the GitHub Packages endpoint returns `401 Unauthorized` and no
credentials are available in this repository. The timestamp API is
source-verified, but package restore and runtime execution through that path
remain unverified. This does not block the default Whisper.net transcription
path.

## Cache policy

Model caches resolve under `%LOCALAPPDATA%\AudioTranscriber\model-cache` on
Windows (or the platform equivalent), never in the repository. `doctor`
reports the resolved path, the available Whisper.net backend, the FFmpeg
prerequisite, and the optional Model Garden package/API status without
attempting downloads.
