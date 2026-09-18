# Audio transcriber Smart Tool

A .NET 10 library-first Smart Tool for local audio preparation and
model-backed speech transcription. The `AudioTranscriber` library owns every
capability; `audio-transcriber` is only a non-interactive CLI adapter.

See [`src/AudioTranscriber/SMART_TOOL.md`](src/AudioTranscriber/SMART_TOOL.md)
for the manifest, source/package launch paths, capability boundaries,
Whisper.net provenance, cache policy, and optional Model Garden package details.

## Build and test

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet test .\AudioTranscriber.sln --no-restore
```

The default build and deterministic capabilities do not download model
binaries. The first real `transcribe` invocation downloads and verifies the
pinned Whisper Base model outside the repository. Optional Model Garden package
validation is documented in `SMART_TOOL.md`.

## Source checkout

`dotnet tool install` does not consume a Git URL directly. From a source
checkout, restore and launch the PackAsTool project explicitly:

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- --help
```

The top-level `-h` is a terse capability summary and `--help` is the full
agent-facing tool skill. Each capability has the same split, for example:

```powershell
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- -h
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- transcribe --help
```

The introspection and diagnostics commands have distinct roles:

- `manifest` prints the canonical manifest document.
- `doctor` reports cache and integration readiness without downloading a model.

## Clean local-tool installation

Create a package and install it into a temporary tool manifest without changing
global tool state:

```powershell
New-Item -ItemType Directory -Force .\artifacts\tool | Out-Null
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
$packageDirectory = (Resolve-Path .\artifacts\tool).Path
$installDirectory = Join-Path $env:TEMP ("audio-transcriber-install-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $installDirectory | Out-Null
dotnet new tool-manifest --output $installDirectory
Push-Location $installDirectory
dotnet tool install audio-transcriber --version 0.1.0 --add-source $packageDirectory
dotnet tool run audio-transcriber --help
dotnet tool run audio-transcriber manifest
dotnet tool run audio-transcriber doctor
dotnet tool run audio-transcriber convert --input .\source.audio --output .\speech.wav
dotnet tool run audio-transcriber transcribe --input $env:TEMP\audio-transcriber-jfk.wav --format json
Pop-Location
```

The repository documents source checkout and local package installation; it
does not assume or claim publication to a public NuGet feed. A .NET 10 SDK is
required for restore, build, test, and packing. `dotnetup` is an optional SDK
manager, not a launcher dependency; when available, use
`dotnetup sdk install 10.0`.
An already-packed tool invocation needs the matching .NET 10 runtime, not the
SDK.

The installed tool never stores model binaries in the package or repository.
The first transcription requires network access to download the pinned
`sandrohanea/whisper.net` artifact and stores it under the platform's external
user cache (`%LOCALAPPDATA%\AudioTranscriber\model-cache` on Windows). Existing
cache files are hash-verified. The commands above assume a local 16 kHz mono
speech WAV at `$env:TEMP\audio-transcriber-jfk.wav`. The explicit `convert`
command uses an external `ffmpeg` executable resolved from `PATH`, or from
`--ffmpeg <path>`, and writes 16 kHz mono 16-bit PCM WAV without modifying its
source. It never downloads or bundles FFmpeg and refuses to overwrite an
existing output unless `--force` is supplied. Source formats are limited to
what the installed FFmpeg executable can decode; no particular container or
codec is promised. Transcript renderers expose only the input basename (for
example, `speech.wav`) rather than the absolute input path. Doctor output and
CLI/model errors use safe tokens such as `<repository>`, `<model-cache>`, and
`<model-file>` instead of local absolute paths.
