# Audio transcriber Smart Tool

A .NET 10 library-first Smart Tool for local, batch transcription of 16 kHz mono WAV files. The `AudioTranscriber` library owns the domain behavior; `audio-transcriber` is only a non-interactive CLI shell.

See [`src/AudioTranscriber/SMART_TOOL.md`](src/AudioTranscriber/SMART_TOOL.md) for the scope, commands, Whisper.net provenance, cache policy, and the optional Model Garden package status.

## Build and test

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet test .\AudioTranscriber.sln --no-restore
```

The default build is deterministic and does not download model binaries. The
first real `transcribe` invocation downloads and verifies the pinned Whisper
Base model outside the repository. Optional Model Garden package validation is
documented in `SMART_TOOL.md`.

## Clean local-tool installation

The CLI is distributed as a NuGet tool. To install a locally packed artifact
without changing global tool state:

```powershell
$packageDirectory = (Resolve-Path .\artifacts\tool).Path
$installDirectory = Join-Path $env:TEMP ("audio-transcriber-install-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $installDirectory | Out-Null
dotnet new tool-manifest --output $installDirectory
Push-Location $installDirectory
dotnet tool install audio-transcriber --version 0.1.0 --add-source $packageDirectory
dotnet tool run audio-transcriber manifest
dotnet tool run audio-transcriber status
dotnet tool run audio-transcriber doctor
dotnet tool run audio-transcriber transcribe --input $env:TEMP\audio-transcriber-jfk.wav --format json
Pop-Location
```

Create the package first with:

```powershell
New-Item -ItemType Directory -Force .\artifacts\tool | Out-Null
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
```

The installed tool never stores model binaries in the package or repository.
The first transcription requires network access to download the pinned
`sandrohanea/whisper.net` artifact and stores it under the platform's external
user cache (`%LOCALAPPDATA%\AudioTranscriber\model-cache` on Windows). Existing
cache files are hash-verified. The commands above assume a local 16 kHz mono
speech WAV at `$env:TEMP\audio-transcriber-jfk.wav`; FFmpeg conversion is not
part of this tool. Transcript renderers expose only the input basename (for
example, `speech.wav`) rather than the absolute input path. Doctor output and
CLI/model errors use safe tokens such as `<repository>`, `<model-cache>`, and
`<model-file>` instead of local absolute paths.
