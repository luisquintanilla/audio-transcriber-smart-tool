---
name: audio-transcriber
description: >-
  Prepare local audio for Whisper transcription and run timestamped local speech
  recognition with explicit conversion and diagnostics. Use when a task needs a
  local audio file converted to the transcription contract or transcribed.
license: MIT
metadata:
  author: luisquintanilla
  repository: https://github.com/luisquintanilla/audio-transcriber-smart-tool
---

# Using audio-transcriber

`audio-transcriber` is a .NET PackAsTool Smart Tool. Its library owns the
capabilities and its CLI is a thin adapter.

## Install

For a source checkout:

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- --help
```

For an isolated local installation, create a package and install the `.nupkg`
from a temporary tool manifest:

```powershell
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
dotnet new tool-manifest --output $env:TEMP\audio-transcriber-tool
dotnet tool install audio-transcriber --version 0.1.0 --add-source (Resolve-Path .\artifacts\tool)
```

`dotnet tool install` does not consume a Git URL directly. Use a checkout to
run from source or pack the checkout and install from its local package folder.

## Use it

Run the tool's own help before invoking a capability:

```powershell
dotnet audio-transcriber --help
dotnet audio-transcriber <capability> --help
```

Follow the returned tool and capability skills for the arguments, results, and
failure behavior. Do not duplicate those capability documents here.
