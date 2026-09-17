---
smart_tool_format: 1
name: audio-transcriber
version: 0.1.0
description: >
  Validate local 16 kHz mono WAV recordings and run local Whisper Base
  transcription with deterministic timestamped transcript renderers.
use_cases:
  - Validate local WAV inputs against the supported 16 kHz mono contract
  - Transcribe local speech with real timestamped Whisper Base segments
  - Render typed transcripts as text, JSON, SRT, or WebVTT
  - Inspect deterministic model, cache, and package integration readiness
platforms:
  - windows
---

# Audio transcriber Smart Tool

`audio-transcriber` is a .NET 10, library-first Smart Tool for deterministic
local batch transcription. The CLI is intentionally thin: all input
validation, orchestration, typed transcript contracts, renderers, manifest,
and doctor behavior live in `AudioTranscriber`.

## Scope

- WAV-first local batch input.
- 16 kHz, mono PCM WAV is required.
- Whisper Base is the default model target.
- Real timestamped typed transcript segments.
- Text, JSON, SRT, and WebVTT renderers.
- Deterministic `manifest` and `doctor` commands.
- Explicit non-interactive failures.

The tool deliberately does not implement summaries, diarization, streaming,
remote providers, or FFmpeg conversion. `manifest` emits this canonical
document from the packaged library. The separate `status` command emits
deterministic machine-readable frontmatter status JSON.

## Catalog readiness

**Available.** The default engine uses Whisper.net 1.9.0 with the public
whisper.cpp runtime and returns the underlying model's real timestamped
segments. Model files are downloaded only on first transcription, verified by
SHA-256, and stored outside the repository.

## CLI

```powershell
audio-transcriber manifest
audio-transcriber status
audio-transcriber doctor
audio-transcriber transcribe --input .\speech.wav --format json
```

The CLI is a PackAsTool command and never prompts. Exit code `2` means usage
failure, `1` means an input/integration failure, `4` means cancellation, and
`0` means success. Model download, checksum, native runtime, and input errors
are reported explicitly rather than producing degraded or fabricated output.
Transcript renderers expose only input basenames, not absolute local paths.
Doctor output and CLI/model errors use safe path tokens such as
`<repository>`, `<model-cache>`, and `<model-file>`.

## Distribution smoke

Build and install the packed CLI into a fresh temporary local-tool manifest:

```powershell
New-Item -ItemType Directory -Force .\artifacts\tool | Out-Null
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
$packageDirectory = (Resolve-Path .\artifacts\tool).Path
$installDirectory = Join-Path $env:TEMP ("audio-transcriber-install-" + [guid]::NewGuid().ToString("N"))
dotnet new tool-manifest --output $installDirectory
Push-Location $installDirectory
dotnet tool install audio-transcriber --version 0.1.0 --add-source $packageDirectory
dotnet tool run audio-transcriber manifest
dotnet tool run audio-transcriber status
dotnet tool run audio-transcriber doctor
dotnet tool run audio-transcriber transcribe --input $env:TEMP\audio-transcriber-jfk.wav --format json
Pop-Location
```

For another checkout, replace the absolute `--add-source` path with that
checkout's `artifacts\tool` directory. The `.nupkg` contains the library under
`tools/net10.0/any/AudioTranscriber.dll`; the canonical `SMART_TOOL.md` is an
embedded library resource, and no model binaries are packaged. The installed
tool still downloads the pinned artifact only on first transcription and keeps
it in the external user cache.

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
reports the resolved path, the available Whisper.net backend, and the optional
Model Garden package/API status without attempting downloads.
