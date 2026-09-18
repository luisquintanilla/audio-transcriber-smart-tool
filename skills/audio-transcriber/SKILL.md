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
capabilities and its CLI is a thin adapter. The `chapters` capability consumes
an existing timestamped transcript JSON document; it does not run Whisper, call
an LLM, or generate summaries.

## Install

The source checkout and pack flow require the .NET 10 SDK:

```powershell
dotnet restore .\AudioTranscriber.sln
dotnet run --project .\src\audio-transcriber\audio-transcriber.csproj --no-restore -- --help
```

Install the SDK from https://dotnet.microsoft.com/download/dotnet/10.0. An
optional `dotnetup` SDK manager can install the required channel with
`dotnetup sdk install 10.0`; `dotnetup` is not required by the launcher.

For an isolated local installation, create a package and install the `.nupkg`
from a temporary tool manifest:

```powershell
dotnet pack .\src\audio-transcriber\audio-transcriber.csproj --configuration Release --output .\artifacts\tool
$packageDirectory = (Resolve-Path .\artifacts\tool).Path
$installDirectory = Join-Path $env:TEMP ("audio-transcriber-tool-" + [guid]::NewGuid().ToString("N"))
dotnet new tool-manifest --output $installDirectory
Push-Location $installDirectory
dotnet tool install audio-transcriber --version 0.1.1 --add-source $packageDirectory
dotnet tool run audio-transcriber -- --help
Pop-Location
```

`dotnet tool install` does not consume a Git URL directly. Use a checkout to
run from source or pack the checkout and install from its local package folder.
The GitHub Packages feed also offers the `audio-transcriber` CLI tool and
the separate `AudioTranscriber` library package, both version `0.1.1`.
Library callers use `PackageReference` to `AudioTranscriber`, not the tool
package. See the repository README for authenticated installation and restore;
even public GitHub NuGet packages require authentication, and each package's
visibility is managed separately. Source checkout and packing require the .NET 10 SDK;
running an already-packed tool requires the matching .NET 10 runtime, not the
SDK.

## Use it

Run the tool's own help before invoking a capability:

```powershell
audio-transcriber --help
audio-transcriber <capability> --help
```

Follow the returned tool and capability skills for the arguments, results, and
failure behavior. Do not duplicate those capability documents here.
