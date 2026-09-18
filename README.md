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

| Package ID | Version | Consumption |
| --- | --- | --- |
| `audio-transcriber` | `0.1.1` | CLI: `dotnet tool install` |
| `AudioTranscriber` | `0.1.1` | Library: ordinary `PackageReference` |

Both use `https://nuget.pkg.github.com/luisquintanilla/index.json`.
The hyphen distinguishes these IDs; NuGet package IDs are case-insensitive.
The tool bundles the library for execution but is not a library dependency.

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
    dotnet tool install audio-transcriber --version 0.1.1 --configfile .\NuGet.GitHub.config
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
initial `audio-transcriber` `0.1.0` package omits this separator; `0.1.1`
ships the corrected commands shown here.

For maintainers, pack the appropriate project and publish only its new artifact
to the feed above using securely configured local credentials with
`write:packages`. Both existing 0.1.0 package releases are immutable:
packing them for validation is not permission to republish them. A newly published
GitHub package defaults to private; its owner must explicitly make each package
public in **Package settings** and verify visibility. Linking the public
repository does not make either package public. Publication is manual; no CI
publishing workflow is provided.

## Typed library consumption

In a .NET 10 application, reference the **library**, not the tool:

```xml
<ItemGroup>
  <PackageReference Include="AudioTranscriber" Version="0.1.1" />
</ItemGroup>
```

Use `NuGet.Library.config` from this repository, or save the following in the
application directory. It routes `AudioTranscriber` to GitHub and its normal
Whisper dependencies to nuget.org; neither of this repository's packages is
published to nuget.org.

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/luisquintanilla/index.json" />
    <add key="nuget.org" value="https://www.nuget.org/api/v2" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="github">
      <package pattern="AudioTranscriber" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

With the same securely supplied credentials described above:

```powershell
if (-not $env:GITHUB_PACKAGES_TOKEN -or -not $env:GITHUB_PACKAGES_USERNAME) {
    throw "Set GITHUB_PACKAGES_TOKEN and GITHUB_PACKAGES_USERNAME securely before restoring."
}
$env:NuGetPackageSourceCredentials_github = "Username=$env:GITHUB_PACKAGES_USERNAME;Password=$env:GITHUB_PACKAGES_TOKEN;ValidAuthenticationTypes=Basic"
try {
    dotnet restore .\YourApp.csproj --configfile .\NuGet.Library.config
} finally {
    Remove-Item Env:NuGetPackageSourceCredentials_github
}
```

Call the library directly without spawning the CLI. For example, this
deterministic introspection code needs neither model downloads nor provider
credentials at runtime:

```csharp
using AudioTranscriber;

SmartToolManifest manifest = SmartToolManifestService.Create();
IReadOnlyList<SmartToolRequirement> prerequisites = manifest.Requires;
Console.WriteLine($"{manifest.Name} {manifest.Version}");
Console.WriteLine(SmartToolManifestService.Markdown());
```

The canonical manifest is embedded in the library and available through
`SmartToolManifestService`; callers do not need a repository checkout or a loose
manifest file. `AudioConversionService`, `BatchTranscriptionService`, and
`ITranscriptionEngine` expose typed processing APIs. Conversion still requires
external FFmpeg, and real transcription still uses the verified external model
cache. The default library package depends on `Whisper.net` and
`Whisper.net.Runtime` 1.9.0; optional Model Garden integration remains opt-in.
The runtime dependency's build assets flow to the consuming application so its
architecture-specific native libraries are copied correctly. The library
package does not repack them as flattened content files.

To create the library package from Git:

```powershell
dotnet pack .\src\AudioTranscriber\AudioTranscriber.csproj --configuration Release --output .\artifacts\library
```

The library package has `lib/net10.0` assets and no CLI entry point. The CLI
project keeps its source `ProjectReference` to the library; it does not fetch
the published library to build. Future coordinated releases should update both
package versions and the canonical manifest together.

## Optional transcript processing boundary

`src/AudioTranscriber.TranscriptProcessing` is an optional, model-independent
boundary for consuming transcript JSON. It has no project or package dependency
on `AudioTranscriber`, Whisper, Granite, Foundry Local, preview DataIngestion,
or any model runtime, so applications can reference it without changing the
core transcription dependency graph.

The boundary exposes the versioned `TranscriptDocument` and
`TranscriptSegment` contracts (`schemaVersion: "1.0"`), plus typed
`TranscriptJsonReader`, `TranscriptNormalizer`, and `TranscriptJsonWriter`
APIs. It also provides `TranscriptIngestionAdapter` and
`TranscriptIngestionDocumentReader`, which compose the standard
`Microsoft.Extensions.DataIngestion.Abstractions` `IngestionDocument`,
section, and paragraph types without replacing the transcript-specific JSON
contract. Typed document and segment metadata preserve timing, speaker,
confidence, source IDs, original ordinals, source metadata, and provenance
across that boundary. The reader applies the same deterministic normalization
as the public normalizer and accepts a
versioned document object and the existing renderer's legacy document array.
It reads from a `Stream` or file, validates required and unknown properties,
normalizes text and timestamps, and fails atomically with
`TranscriptFormatException` containing a stable error code and JSON path.
Speaker, confidence, source IDs, source metadata, provenance metadata, and
original ordinals are retained. When an input omits source IDs, segment IDs
are deterministic hashes of the normalized segment values and original
ordinal; input order is retained when ordinals are absent and explicit
ordinals determine canonical order when present.

Segment text must contain non-whitespace content. Normalization trims and
collapses Unicode whitespace within each fragment without merging fragments.
Timestamps are non-negative
`HH:MM:SS[.fffffff]` values with monotonic starts and `end > start`.
Adjacent segments and legitimate gaps are accepted; overlaps are rejected.
Fragments are normalized but never merged. The current `AudioTranscriber`
transcript models and JSON renderer remain unchanged for backward
compatibility. The DataIngestion abstraction source is vendored under
`src/Vendored/dotnet-extensions` from the `data-ingestion-preview2` branch at
commit `e124c123afeeda2f271f3b99a70eb3cfe187a471`; see its `VENDORED.md` for
the MIT attribution and the intentionally deferred higher pipeline boundary.

## Optional transcript ingestion boundary

`src/AudioTranscriber.TranscriptIngestion` maps validated transcript documents
to the stable, runtime-independent `TranscriptIngestionDocument` contract. It
preserves source and provenance metadata, segment IDs, source IDs, timestamps,
speaker, confidence, source metadata, and canonical ordering. The project is
non-packable and references only `AudioTranscriber.TranscriptProcessing`; it
intentionally does not reference the preview
`Microsoft.Extensions.DataIngestion` packages. Applications can bridge this
contract to the ingestion runtime they select without adding preview
dependencies to the core `AudioTranscriber` package.

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
dotnet tool install audio-transcriber --version 0.1.1 --add-source $packageDirectory
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
