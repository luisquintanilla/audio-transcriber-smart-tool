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

Git installation remains supported independently of any package feed.
`dotnet tool install` does not consume a Git URL directly. Clone the repository,
then restore and launch the PackAsTool project explicitly:

```powershell
git clone https://github.com/luisquintanilla/audio-transcriber-smart-tool.git
Set-Location .\audio-transcriber-smart-tool
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

## GitHub Packages installation

The temporary package distribution target is **GitHub Packages**, not
nuget.org or the shared Smart Tools Catalog:

- Package ID: `audio-transcriber`
- Version: `0.1.0`
- Feed: `https://nuget.pkg.github.com/luisquintanilla/index.json`

GitHub requires authentication even for public NuGet packages. Use a personal
access token (classic) with `read:packages`, supplied securely to the current
process as `GITHUB_PACKAGES_TOKEN`, and your GitHub login as
`GITHUB_PACKAGES_USERNAME`. Do not put tokens in commands, source control, or
chat. See [GitHub's NuGet authentication guidance](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

Use the repository's `NuGet.GitHub.config`, or save the following as
`NuGet.GitHub.config` in any installation directory (no Git checkout required):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/luisquintanilla/index.json" />
  </packageSources>
</configuration>
```

Install into a local tool manifest using only that feed:

```powershell
if (-not $env:GITHUB_PACKAGES_TOKEN -or -not $env:GITHUB_PACKAGES_USERNAME) {
    throw "Set GITHUB_PACKAGES_TOKEN and GITHUB_PACKAGES_USERNAME securely before installing."
}
$env:NuGetPackageSourceCredentials_github = "Username=$env:GITHUB_PACKAGES_USERNAME;Password=$env:GITHUB_PACKAGES_TOKEN;ValidAuthenticationTypes=Basic"
try {
    # Skip this command if the directory already has a tool manifest.
    dotnet new tool-manifest
    dotnet tool install audio-transcriber --version 0.1.0 --configfile .\NuGet.GitHub.config
    dotnet tool run audio-transcriber -- --help
} finally {
    Remove-Item Env:NuGetPackageSourceCredentials_github
}
```

This separate config is for installing the tool package, which bundles its
NuGet dependencies, not restoring the source solution. The ordinary `NuGet.config`
remains unchanged so source builds do not require GitHub credentials.
The tool is framework-dependent and requires .NET 10.

Use the `--` separator when forwarding `--help` to a local tool; otherwise the
.NET 10 SDK prints its own `dotnet tool run` help. The README embedded in the
initial `0.1.0` package omits this separator; use the corrected commands here.

For maintainers, pack the CLI project as shown below and push only the resulting
`audio-transcriber.0.1.0.nupkg` to the feed above using securely configured local
credentials with `write:packages`. A newly published GitHub package defaults to
private; its owner must explicitly make it public in **Package settings** and
verify visibility. Linking the public repository does not make the package public.
Publication is manual; no CI publishing workflow is provided.

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
dotnet tool run audio-transcriber -- --help
dotnet tool run audio-transcriber manifest
dotnet tool run audio-transcriber doctor
dotnet tool run audio-transcriber convert --input .\source.audio --output .\speech.wav
dotnet tool run audio-transcriber transcribe --input $env:TEMP\audio-transcriber-jfk.wav --format json
Pop-Location
```

Source checkout and local package installation remain available without
GitHub Packages authentication. A .NET 10 SDK is
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
