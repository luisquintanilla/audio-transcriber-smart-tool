# Final implementation update

The production contracts described by the original plan are now implemented.
The chunk builder consumes canonical
`Microsoft.Extensions.DataIngestion.IngestionDocument` values with one ordered
section and paragraph per transcript segment. Typed document and segment
metadata use `audioTranscriber.transcript.document` and
`audioTranscriber.transcript.segment`; the mapping preserves timing,
provenance, IDs, speakers, confidence, ordinals, and detached read-only
metadata copies.

The embedding seam is
`IEmbeddingGenerator<TextContent, Embedding<float>>`, and the builder uses
`TensorPrimitives.CosineSimilarity` for standard vector similarity. The
preview2 higher `SemanticSimilarityChunker` was not vendored because its
`IngestionChunk` output cannot preserve the source element identity required
for transcript timing and provenance. No provider, model, CLI, manifest, or
higher DataIngestion pipeline dependency was added. The focused
chunking/artifact/ingestion run passes 55 tests; the Release solution build has
0 warnings and 0 errors. The Release solution test run passed 210 tests with 1
pre-existing opt-in model smoke test skipped and 1 unrelated cross-platform
path-display assertion failing.

The remaining sections below are the original pre-implementation research and
plan record.

# Test Implementation Plan

## Overview

This plan covers the missing transcript chunking foundation in the existing
`tests/AudioTranscriber.Tests` xUnit project targeting `net10.0`. It uses a
broad strategy over the untested chunking surface, while avoiding duplicate
coverage for the already substantial `TranscriptDocument` and
`TranscriptIngestionAdapter` suites.

The implementation order follows the intended dependency graph:

1. Narrow embedding and chunk-scoring seams, using local deterministic fakes.
2. Transcript-aware chunk/window construction over the existing validated
   `TranscriptDocument` contract.
3. Deterministic chapter-artifact construction above the chunk/window result.

All new tests are additive files under `tests/AudioTranscriber.Tests/`; no
new test project, package, model integration, CLI wiring, manifest change, CI
change, or production substitute is planned. The production declarations
needed by the first three phases are currently absent, so the affected tests
have explicit compile blockers rather than test-project stubs.

## Commands

- **Build**: `dotnet build .\tests\AudioTranscriber.Tests\AudioTranscriber.Tests.csproj --no-restore --no-incremental -v:q`
- **Test**: `dotnet test .\tests\AudioTranscriber.Tests\AudioTranscriber.Tests.csproj --no-restore --filter "FullyQualifiedName~TranscriptChunkingTests" -v:q`
- **Discovery check**: `dotnet test .\AudioTranscriber.sln --list-tests --no-build`
- **Lint**: No repository lint command or lint configuration was found.
- **Restore prerequisite, if required**: `dotnet restore .\AudioTranscriber.sln`

The scoped test command becomes useful when the `TranscriptChunkingTests`
class exists. Until the missing production API is added, the new chunking
tests must be expected to fail compilation; those failures must not be hidden
by test-only interfaces or stubs.

## Phase Summary

| Phase | Focus | Files | Est. Tests |
|-------|-------|-------|------------|
| 1 | Dependency seams and format diagnostics | 2 | 8-12 |
| 2 | Transcript-aware chunks/windows and failure behavior | 2 | 18-26 |
| 3 | Deterministic chapter artifacts | 1 | 6-10 |

## Phase 1: Dependency Seams and Format Diagnostics

### Overview

Establish the leaf-level test doubles and verify the remaining directly
observable diagnostic contract before testing the chunker. This phase must
not introduce a real embedding model or a mocking package. Its seam tests
should compile only against the production embedding and scoring abstractions
once those abstractions are declared.

### Files to Test

#### 1. Missing embedding and scoring declarations

- **Source**: No production source path currently exists. The intended
  declarations belong with the transcript-processing/chunking foundation.
- **Test File**: `tests/AudioTranscriber.Tests/TranscriptChunkingDependencyTests.cs`
- **Test Class**: `TranscriptChunkingDependencyTests`

**Methods/scenarios to test**:

1. `EmbeddingFake_ImplementsTheNarrowEmbeddingContract`
   - Use a deterministic local fake that accepts the production embedding
     request and returns a fixed vector.
   - Verify that the fake needs only the narrow embedding member and does not
     require a concrete model, SDK, network, or CLI dependency.
   - Compile blocker: the embedding interface name, request/response types,
     callable member, and cancellation signature are not present.

2. `ScoringFake_ImplementsTheNarrowChunkScoringContract`
   - Use a deterministic local fake that scores supplied candidate
     chunks/windows without model integration.
   - Verify the seam can receive the information required to score a
     transcript-aware candidate and return a stable score.
   - Compile blocker: no chunk-scoring interface or callable member exists.

3. `ChunkingDependencies_AcceptCancellation`
   - Pass a `CancellationToken` through the production dependency members
     when the contract defines it.
   - Assert that a canceled token is observable by each fake rather than
     being silently dropped.
   - Compile blocker: cancellation behavior and member signatures are
     unspecified.

4. `ChunkingDependencies_DoNotRequireConcreteModelTypes`
   - Keep the test references limited to the production abstractions and
     local fakes.
   - This is a compile/scope gate rather than a real-model integration test.

#### 2. `src/AudioTranscriber.TranscriptProcessing/TranscriptFormatException.cs`

- **Source**: `src/AudioTranscriber.TranscriptProcessing/TranscriptFormatException.cs`
- **Test File**: `tests/AudioTranscriber.Tests/TranscriptFormatExceptionTests.cs`
- **Test Class**: `TranscriptFormatExceptionTests`

**Methods/scenarios to test**:

1. `Constructor_PreservesJsonPath`
   - Construct the exception through the documented public constructor and
     assert the `JsonPath` value, including an absent-path case if supported.

2. `Constructor_PreservesDocumentedDiagnosticDetails`
   - Assert the documented error-code/message and inner-exception properties
     exposed by the production type.
   - Do not invent an overload or property; bind this test to the actual
     declaration.

3. `ReaderFailure_ExposesStableFormatDiagnostics`
   - Use the existing reader fixture style to assert that a malformed input
     produces the documented exception details without asserting incidental
     stack text.

### Success Criteria

- [ ] Local deterministic fakes compile against only the production
      embedding/scoring abstractions.
- [ ] No model SDK, network, CLI, manifest, or mocking dependency is added.
- [ ] Direct `TranscriptFormatException` properties not already covered by
      reader tests are asserted.
- [ ] Missing seam declarations and unknown member signatures are recorded as
      compile blockers rather than replaced in the test project.

## Phase 2: Transcript-Aware Chunk and Window Construction

### Overview

Test the mid-layer builder after the leaf seams exist. Inputs should be
explicit `AudioTranscriber.TranscriptProcessing.TranscriptDocument` values
with stable IDs, ordinals, typed timestamps, and metadata dictionaries, as
used by the existing processing tests. The tests must verify transcript
semantics rather than only vector or score values.

The existing `TranscriptContracts.cs` behavior is a fixture prerequisite, not
a reason to duplicate its substantial validation suite. The existing
`TranscriptIngestionAdapterTests.cs` likewise remains the compatibility
coverage for ingestion projections and adapter cancellation/error behavior.

### Files to Test

#### 1. Missing transcript chunk/window builder and result contracts

- **Source**: No production source path currently exists for the intended
  chunk/window builder, result types, options, or window representation.
- **Test File**: `tests/AudioTranscriber.Tests/TranscriptChunkingTests.cs`
- **Test Class**: `TranscriptChunkingTests`

**Methods/scenarios to test**:

1. `Build_EmptyTranscript_ReturnsEmptyResult`
   - Pass an empty validated transcript.
   - Assert no chunks/windows, no phantom text, and no fabricated source
     segment ID or metadata.

2. `Build_SingleSegment_PreservesSourceIdAndMetadata`
   - Build from one segment with an explicit `Id`, `SourceId`, ordinal,
     speaker, confidence, and source metadata.
   - Assert the result carries the source segment ID and metadata without
     flattening, dropping, or mutating values.

3. `Build_MultipleSegments_PreservesAllSourceIdsAndMetadata`
   - Use multiple ordered segments with distinct metadata.
   - Assert every output chunk/window identifies the contributing source
     segments and retains the associated metadata in source order.

4. `Build_SnapsStartToSourceSegmentBoundary`
   - Request a candidate whose natural start falls inside a segment.
   - Assert the emitted start is snapped to the documented segment
     boundary, not left at an arbitrary embedding/window timestamp.

5. `Build_SnapsEndToSourceSegmentBoundary`
   - Request a candidate whose natural end falls inside a segment.
   - Assert the emitted end is snapped to the documented segment boundary.
   - Assert the result remains ordered and has a valid positive duration.

6. `Build_GapBetweenSegments_EmitsExplicitGap`
   - Use two non-overlapping segments with a deliberate time gap.
   - Assert the gap is represented explicitly, with no synthetic transcript
     text and no incorrect source segment ID attached to the gap.

7. `Build_GapAtChunkBoundary_PreservesGapSemantics`
   - Place a requested boundary in a gap.
   - Assert snapping and gap representation follow the documented policy
     instead of assigning the gap to either neighboring segment.

8. `Build_RespectsMinimumDuration`
   - Configure a minimum duration smaller than the available neighboring
     transcript material and assert the result expands using legal snapped
     boundaries.
   - Include a case where the minimum cannot be met without crossing a gap;
     assert the documented gap/expansion behavior.

9. `Build_RespectsMaximumDuration`
   - Configure a maximum duration and use several segments.
   - Assert no emitted chunk/window exceeds the maximum except for a
     documented single-segment impossibility case.
   - Assert splits occur only at legal snapped segment boundaries.

10. `Build_InvalidDurationOptions_ThrowsDocumentedArgumentException`
    - Cover negative/zero minimum or maximum values and minimum greater than
      maximum, using only the production-documented invalid combinations.
    - Assert the documented exception type and parameter/details.

11. `Build_MalformedTiming_IsRejectedWithDocumentedValidationDetails`
    - Exercise the production boundary intended to validate malformed timing:
      reversed start/end, negative timestamps, non-finite values if accepted
      at that boundary, overlap, or non-monotonic ordering.
    - If the builder accepts only `TranscriptDocument`, assert rejection at
      the `TranscriptDocument` construction boundary and document that the
      chunker receives only validated input.
    - Assert the documented exception type, parameter, and stable diagnostic
      details. Do not bypass the existing contract with test-only objects.

#### 2. Missing asynchronous/error-aware chunking entry point

- **Source**: Same absent chunk/window production surface; the intended
  asynchronous entry point and dependency call sequence are not declared.
- **Test File**: `tests/AudioTranscriber.Tests/TranscriptChunkingBehaviorTests.cs`
- **Test Class**: `TranscriptChunkingBehaviorTests`

**Methods/scenarios to test**:

1. `BuildAsync_CancellationBeforeWork_ThrowsOperationCanceledException`
   - Supply an already-canceled token and assert prompt cancellation without
     invoking the embedding or scoring fake.

2. `BuildAsync_CancellationDuringEmbedding_StopsAndThrowsOperationCanceledException`
   - Have the embedding fake observe cancellation while processing.
   - Assert the cancellation is not converted into an empty or partial success.

3. `BuildAsync_CancellationDuringScoring_StopsAndThrowsOperationCanceledException`
   - Repeat the cancellation check at the scoring seam.

4. `BuildAsync_PropagatesEmbeddingProviderError`
   - Make the local embedding fake throw a sentinel exception.
   - Assert the same failure type/details propagate and are not replaced by a
     fabricated chunking result.

5. `BuildAsync_PropagatesChunkScoringError`
   - Make the local scorer throw a sentinel exception.
   - Assert the failure propagates with its diagnostic context intact.

6. `BuildAsync_PreservesDeterministicOutputForDeterministicDependencies`
   - Run the builder twice with identical transcript data and deterministic
     fakes.
   - Assert equivalent ordering, IDs, timestamps, text, scores, and metadata.

If the production design exposes a synchronous entry point with a
`CancellationToken` rather than `BuildAsync`, use the equivalent exact
production member names; do not create an async adapter in the test project.

### Success Criteria

- [ ] Empty input, explicit gaps, source IDs, metadata, snapped boundaries,
      and duration constraints are asserted at the result-contract level.
- [ ] Malformed timing is tested at the actual documented validation boundary.
- [ ] Cancellation is tested before work and during each injectable dependency
      call.
- [ ] Embedding and scoring failures propagate without being swallowed or
      converted into partial success.
- [ ] Tests use `TranscriptDocument` fixtures and local deterministic fakes,
      not the legacy `AudioTranscriber.Transcript` model or real models.
- [ ] All unknown builder/result/options/member names remain explicit compile
      blockers until production declares them.

## Phase 3: Deterministic Chapter Artifacts

### Overview

Test the top layer only after chunk/window output is stable. Chapter tests
should consume the production chunk/window result rather than reconstructing
chunks or reaching into private implementation details. Determinism includes
stable ordering and the documented serialization or artifact representation.

### Files to Test

#### 1. Missing deterministic chapter-artifact builder/generator

- **Source**: No production source path currently exists for chapter-artifact
  types, generation, or serialization.
- **Test File**: `tests/AudioTranscriber.Tests/ChapterArtifactTests.cs`
- **Test Class**: `ChapterArtifactTests`

**Methods/scenarios to test**:

1. `Generate_SameChunkResult_ProducesEquivalentArtifacts`
   - Generate artifacts twice from identical deterministic chunk/window input.
   - Assert exact equality of chapter order, IDs, labels/titles, timestamps,
     source segment references, metadata, scores, and all other documented
     fields.

2. `Generate_PreservesChunkOrder`
   - Provide chunks/windows with stable but distinct order and assert chapter
     output follows that order rather than dictionary, task-completion, or
     hash order.

3. `Serialize_SameArtifacts_ProducesIdenticalOutput`
   - If serialization is part of the production contract, serialize the same
     artifacts twice and assert byte/string equality.
   - Assert stable property ordering and formatting only where documented.

4. `Generate_EmptyChunkResult_ProducesDeterministicEmptyArtifact`
   - Assert an empty input/result has a stable empty representation and does
     not invent a chapter.

5. `Generate_PreservesSourceIdsAndMetadata`
   - Assert chapter artifacts retain the source references and metadata
     established by the chunk/window result.

6. `Generate_PropagatesDependencyFailure`
   - If chapter generation invokes an injectable chunking/scoring dependency,
     assert the sentinel error is propagated with its documented type/details.

7. `GenerateAsync_CancellationIsPropagated`
   - If chapter generation is asynchronous, assert cancellation before work and
     during generation are surfaced as cancellation rather than partial
     artifacts.

### Success Criteria

- [ ] Chapter artifacts are deterministic across repeated runs with identical
      input.
- [ ] Stable order, identifiers, source references, metadata, and documented
      serialization are asserted.
- [ ] Empty input, propagated errors, and cancellation are covered when those
      responsibilities belong to the chapter entry point.
- [ ] Tests bind to the production artifact contract and do not create
      test-only chapter DTOs.

## Acceptance-Item Traceability

| Acceptance item | Concrete planned coverage | Current blocker/status |
|-----------------|---------------------------|------------------------|
| 1. Narrow embedding and chunk-scoring interfaces | Phase 1 fake-compatibility and cancellation tests | Both interfaces, members, and signatures are absent. |
| 2. Transcript-aware chunks/windows preserving source IDs and metadata | Phase 2 `Build_SingleSegment_PreservesSourceIdAndMetadata` and `Build_MultipleSegments_PreservesAllSourceIdsAndMetadata` | Builder/result types and output fields are absent. |
| 3. Snapped segment boundaries | Phase 2 start/end snapping tests | Boundary policy and result members are absent. |
| 4. Explicit gaps and empty input | Phase 2 empty and gap tests | Gap/result representation is absent. |
| 5. Minimum/maximum duration behavior | Phase 2 duration and invalid-option tests | Options and duration semantics are absent. |
| 6. Malformed timing validation | Phase 2 malformed-timing test plus the existing validated transcript fixture contract | Chunking validation boundary and documented exception/details are unspecified; `TranscriptDocument` already validates its own contract. |
| 7. Deterministic chapter artifacts | Phase 3 repeatability, ordering, empty, and serialization tests | Artifact types and generation/serialization entry point are absent. |
| 8. Cancellation | Phase 1 dependency-token test; Phase 2 builder cancellation tests; Phase 3 chapter cancellation where applicable | Cancellation-capable production members are absent. |
| 9. Propagated errors | Phase 2 embedding/scoring error tests; Phase 3 generator error test where applicable | Entry points and documented propagation behavior are absent. |
| 10. No real model integrations/CLI/manifest/packages/CI/unrelated changes | Local fakes, existing test project, and phase scope gates | No blocker; this is a plan constraint. |
| 11. Additive tests and reported compile blockers | All phase files under `tests/AudioTranscriber.Tests`; blocker list below | No substitute production types may be added to make tests compile. |

## Compile Blockers and Explicit Non-Scope

The following are expected compile blockers until production work supplies the
actual declarations. The implementation must report the resulting
`CS0246`, `CS0234`, `CS1061`, `CS7036`, or equivalent diagnostics instead of
masking them:

- No embedding abstraction, embedding request/response contract, or
  cancellation-aware member exists.
- No chunk-scoring abstraction or cancellation-aware member exists.
- No transcript-aware chunk/window result type, gap representation, options
  type, or builder/chunker entry point exists.
- No documented output members exist for source segment IDs, metadata,
  snapped timestamps, or explicit gaps.
- No documented minimum/maximum duration options or impossible-constraint
  behavior exists.
- No chunking-level malformed-timing validation boundary or documented
  exception/details exists.
- No chapter-artifact type, generator, stable serializer, or async contract
  exists.
- Exact production names are not discoverable; test names above describe the
  required behaviors and must be bound to the actual API once declared.

The following remain outside this additive suite: the legacy transcript model,
real embedding/model integrations, CLI behavior, package and manifest
authoring, CI configuration, unrelated source trees, and duplicate tests for
the substantially covered `TranscriptContracts.cs` and
`TranscriptIngestionAdapter.cs` behavior.
