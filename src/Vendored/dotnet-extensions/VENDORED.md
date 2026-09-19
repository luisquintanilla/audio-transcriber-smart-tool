# Vendored DataIngestion source

This directory contains the minimum source snapshot needed by the optional
transcript-processing boundary from the
[`data-ingestion-preview2`](https://github.com/dotnet/extensions/tree/data-ingestion-preview2)
branch of [`dotnet/extensions`](https://github.com/dotnet/extensions).

- Upstream commit: `e124c123afeeda2f271f3b99a70eb3cfe187a471`
- License: MIT, as included in [`LICENSE`](./LICENSE)
- Included upstream surface: `Microsoft.Extensions.AI.Abstractions`, shared
  diagnostics helpers, and `Microsoft.Extensions.DataIngestion.Abstractions`
- Intentionally excluded: the higher `Microsoft.Extensions.DataIngestion`
  pipeline, readers, chunkers, processors, model integrations, and tests

The project files are constrained to `net10.0` so this source snapshot builds
without the upstream repository's Arcade build environment. The source APIs
are otherwise unchanged. The transcript adapter composes the sealed
`IngestionDocument` with standard section/paragraph elements and stores typed
transcript timing, speaker, confidence, source IDs, ordinals, and provenance
in element/document metadata. The existing transcript JSON contract remains
the format-specific boundary; no DataIngestion package or model/runtime
dependency is added to the core `AudioTranscriber` project.
