using System.Text;
using System.Text.Json;
using Processing = AudioTranscriber.TranscriptProcessing;
using DataIngestion = Microsoft.Extensions.DataIngestion;

namespace AudioTranscriber.Tests;

public sealed class TranscriptProcessingTests
{
    [Fact]
    public async Task Json_reader_parses_and_normalizes_versioned_fixture()
    {
        var reader = new Processing.TranscriptJsonReader();

        await using var stream = File.OpenRead(Fixture("transcript-v1.json"));
        var documents = await reader.ReadAsync(stream);

        var document = Assert.Single(documents);
        Assert.Equal(Processing.TranscriptSchema.CurrentVersion, document.SchemaVersion);
        Assert.Equal("meeting.wav", document.Source);
        Assert.Equal("fixture-provider", document.Provenance.Provider);
        Assert.Equal("fixture-model", document.Provenance.Model);
        Assert.Equal("fixture.package", document.Provenance.PackageId);
        Assert.Equal("2.4.1", document.Provenance.PackageVersion);
        Assert.Equal("offline-fixture", document.Provenance.Source);
        Assert.Equal("fixture-cache", document.Provenance.CachePath);
        Assert.Equal("fixture-adapter", document.Provenance.Metadata["adapter"]);

        Assert.Equal(2, document.Segments.Count);
        var first = document.Segments[0];
        var second = document.Segments[1];
        Assert.Equal(0, first.OriginalOrdinal);
        Assert.Equal(4, second.OriginalOrdinal);
        Assert.Equal("Hello there", first.Text);
        Assert.Equal("speaker-a", first.Speaker);
        Assert.Equal(0.875, first.Confidence);
        Assert.Equal("left", first.SourceMetadata["channel"]);
        Assert.Equal("fragment-a", first.SourceMetadata["rawId"]);
        Assert.Equal(TimeSpan.FromTicks(1_250_000), first.Start);
        Assert.Equal(TimeSpan.FromTicks(15_000_000), first.End);
        Assert.Equal(TimeSpan.FromTicks(30_000_001), second.End);
        Assert.Equal("Hello there second fragment", document.Text);
        Assert.True(first.End < second.Start);
    }

    [Fact]
    public async Task Json_reader_reads_from_file_and_preserves_typed_contract()
    {
        var reader = new Processing.TranscriptJsonReader();

        var documents = await reader.ReadFileAsync(Fixture("transcript-v1.json"));

        var document = Assert.Single(documents);
        Assert.IsAssignableFrom<IReadOnlyList<Processing.TranscriptSegment>>(document.Segments);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            document.Segments[0].SourceMetadata);
    }

    [Fact]
    public async Task Json_reader_preserves_legacy_renderer_array_shape()
    {
        var reader = new Processing.TranscriptJsonReader();

        await using var stream = File.OpenRead(Fixture("transcript-legacy.json"));
        var documents = await reader.ReadAsync(stream);

        var document = Assert.Single(documents);
        Assert.Equal("legacy.wav", document.Source);
        Assert.Equal("legacy-provider", document.Provenance.Provider);
        Assert.Equal("legacy-model", document.Provenance.Model);
        Assert.Null(document.Provenance.Source);
        Assert.Null(document.Provenance.PackageId);
        Assert.Equal("legacy fragment", document.Segments[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), document.Segments[0].End);
    }

    [Fact]
    public async Task Json_reader_assigns_stable_ids_and_order_when_source_ids_are_absent()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "stable.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:01.000", "end": "00:00:02.000", "text": "second", "originalOrdinal": 20 },
                { "start": "00:00:00.000", "end": "00:00:00.500", "text": "first", "originalOrdinal": 10 }
              ]
            }
            """;
        var reader = new Processing.TranscriptJsonReader();

        var firstRun = await ReadDocumentAsync(reader, json);
        var secondRun = await ReadDocumentAsync(reader, json);

        Assert.Equal(["first", "second"], firstRun.Segments.Select(segment => segment.Text));
        Assert.Equal(
            firstRun.Segments.Select(segment => segment.Id),
            secondRun.Segments.Select(segment => segment.Id));
        Assert.All(firstRun.Segments, segment => Assert.StartsWith("seg-", segment.Id));
        Assert.NotEqual(firstRun.Segments[0].Id, firstRun.Segments[1].Id);
        Assert.Equal([10, 20], firstRun.Segments.Select(segment => segment.OriginalOrdinal));
    }

    [Fact]
    public async Task Json_reader_preserves_source_ids_and_original_ordinals()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "source-ids.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                {
                  "id": "stable-id",
                  "sourceId": "asr-42",
                  "originalOrdinal": 7,
                  "start": "00:00:00.000",
                  "end": "00:00:01.000",
                  "text": "fragment"
                }
              ]
            }
            """;

        var document = await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json);
        var segment = Assert.Single(document.Segments);

        Assert.Equal("stable-id", segment.Id);
        Assert.Equal("asr-42", segment.SourceId);
        Assert.Equal(7, segment.OriginalOrdinal);
    }

    [Fact]
    public async Task Json_reader_does_not_merge_trimmed_asr_fragments()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "fragments.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:00.000", "end": "00:00:01.000", "text": " first " },
                { "start": "00:00:01.000", "end": "00:00:02.000", "text": " second " }
              ]
            }
            """;

        var document = await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json);

        Assert.Equal(2, document.Segments.Count);
        Assert.Equal(["first", "second"], document.Segments.Select(segment => segment.Text));
        Assert.Equal("first second", document.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), document.Segments[1].Start);
    }

    [Fact]
    public void Transcript_normalizer_collapses_whitespace_without_merging_fragments()
    {
        var document = new Processing.TranscriptDocument(
            "whitespace.wav",
            new Processing.TranscriptProvenance("provider", "model"),
            [
                new Processing.TranscriptSegment(
                    " first \r\n fragment ",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    0),
                new Processing.TranscriptSegment(
                    " second\tfragment ",
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    1)
            ]);

        var normalizer = new Processing.TranscriptNormalizer();
        var normalized = normalizer.Normalize(document);
        var repeated = normalizer.Normalize(normalized);

        Assert.Equal(2, normalized.Segments.Count);
        Assert.Equal(["first fragment", "second fragment"], normalized.Segments.Select(segment => segment.Text));
        Assert.Equal(
            normalized.Segments.Select(segment => segment.Id),
            repeated.Segments.Select(segment => segment.Id));
        Assert.Equal(
            new Processing.TranscriptJsonWriter().Write(normalized),
            new Processing.TranscriptJsonWriter().Write(repeated));
        Assert.Same(document.Provenance, normalized.Provenance);
        Assert.Equal(
            document.Segments.Select(segment => segment.OriginalOrdinal),
            normalized.Segments.Select(segment => segment.OriginalOrdinal));
    }

    [Fact]
    public async Task Json_reader_normalizes_timestamp_whitespace_and_precision()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "normalized-time.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": " 00:00:00.1 ", "end": "00:00:01.2500", "text": "text" }
              ]
            }
            """;

        var document = await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json);
        var written = new Processing.TranscriptJsonWriter().Write(document);

        Assert.Equal(TimeSpan.FromMilliseconds(100), document.Segments[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), document.Segments[0].End);
        Assert.Contains("\"start\": \"00:00:00.1000000\"", written);
        Assert.Contains("\"end\": \"00:00:01.2500000\"", written);
    }

    [Fact]
    public async Task Json_writer_round_trips_large_timestamp_near_hour_boundary()
    {
        const long hourCount = 100_000_000;
        var hour = TimeSpan.FromTicks(hourCount * TimeSpan.TicksPerHour);
        var start = hour - TimeSpan.FromTicks(1);
        var document = new Processing.TranscriptDocument(
            "large-time.wav",
            new Processing.TranscriptProvenance("provider", "model"),
            [
                new Processing.TranscriptSegment(
                    "large timestamp",
                    start,
                    hour,
                    0)
            ]);
        var writer = new Processing.TranscriptJsonWriter();

        var json = writer.Write(document);
        var roundTrip = await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json);

        Assert.Contains("\"start\": \"99999999:59:59.9999999\"", json);
        Assert.Equal(start, roundTrip.Segments[0].Start);
        Assert.Equal(hour, roundTrip.Segments[0].End);
    }

    [Fact]
    public async Task Json_writer_is_deterministic_and_round_trips_all_contract_fields()
    {
        var document = new Processing.TranscriptDocument(
            "round-trip.wav",
            new Processing.TranscriptProvenance(
                "provider",
                "model",
                "package",
                "1.0.0",
                "adapter",
                "cache",
                new Dictionary<string, string>
                {
                    ["z"] = "last",
                    ["a"] = "first"
                }),
            [
                new Processing.TranscriptSegment(
                    " text ",
                    TimeSpan.FromTicks(1),
                    TimeSpan.FromTicks(10_000_001),
                    3,
                    sourceId: "source-1",
                    speaker: " speaker ",
                    confidence: 0.25,
                    sourceMetadata: new Dictionary<string, string>
                    {
                        ["z"] = "last",
                        ["a"] = "first"
                    })
            ]);
        var writer = new Processing.TranscriptJsonWriter();

        var first = writer.Write(document);
        var second = writer.Write(document);
        var roundTrip = await ReadDocumentAsync(new Processing.TranscriptJsonReader(), first);

        Assert.Equal(first, second);
        Assert.Contains("\"schemaVersion\": \"1.0\"", first);
        Assert.Equal(document.Source, roundTrip.Source);
        Assert.Equal(document.Provenance.Metadata, roundTrip.Provenance.Metadata);
        Assert.Equal(document.Segments[0].Id, roundTrip.Segments[0].Id);
        Assert.Equal(document.Segments[0].Start, roundTrip.Segments[0].Start);
        Assert.Equal(document.Segments[0].End, roundTrip.Segments[0].End);
        Assert.Equal(document.Segments[0].Speaker, roundTrip.Segments[0].Speaker);
        Assert.Equal(document.Segments[0].SourceMetadata, roundTrip.Segments[0].SourceMetadata);
    }

    [Fact]
    public async Task Json_writer_round_trips_multiple_documents_in_stable_order()
    {
        var first = new Processing.TranscriptDocument(
            "first.wav",
            new Processing.TranscriptProvenance("p", "m"),
            [
                new Processing.TranscriptSegment(
                    "first",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    0)
            ]);
        var second = new Processing.TranscriptDocument(
            "second.wav",
            new Processing.TranscriptProvenance("p", "m"),
            [
                new Processing.TranscriptSegment(
                    "second",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    0)
            ]);
        var writer = new Processing.TranscriptJsonWriter();

        var json = writer.WriteMany([first, second]);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var documents = await new Processing.TranscriptJsonReader().ReadAsync(stream);

        Assert.Equal(["first.wav", "second.wav"], documents.Select(document => document.Source));
        Assert.Equal(json, writer.WriteMany(documents));
    }

    [Fact]
    public async Task Json_reader_accepts_legitimate_gaps_and_rejects_overlap()
    {
        const string gapJson = """
            {
              "schemaVersion": "1.0",
              "source": "gaps.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:00.000", "end": "00:00:01.000", "text": "one" },
                { "start": "00:00:03.000", "end": "00:00:04.000", "text": "two" }
              ]
            }
            """;
        const string overlapJson = """
            {
              "schemaVersion": "1.0",
              "source": "overlap.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:00.000", "end": "00:00:02.000", "text": "one" },
                { "start": "00:00:01.000", "end": "00:00:03.000", "text": "two" }
              ]
            }
            """;

        var reader = new Processing.TranscriptJsonReader();
        var document = await ReadDocumentAsync(reader, gapJson);
        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(reader, overlapJson));

        Assert.Equal(2, document.Segments.Count);
        Assert.Equal("overlapping_timing", exception.Code);
        Assert.Contains("gaps are allowed", exception.Message);
    }

    [Fact]
    public async Task Json_reader_rejects_non_monotonic_timing()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "order.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:02.000", "end": "00:00:03.000", "text": "late" },
                { "start": "00:00:01.000", "end": "00:00:01.500", "text": "early" }
              ]
            }
            """;

        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json));

        Assert.Equal("non_monotonic_timing", exception.Code);
        Assert.Contains("monotonic", exception.Message);
    }

    [Theory]
    [InlineData("-00:00:01.000")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("00:60:00.000")]
    [InlineData("00:00:00.12345678")]
    [InlineData("00:00:00.")]
    [InlineData("999999999999999999999999:00:00.000")]
    public async Task Json_reader_rejects_negative_non_finite_and_invalid_timestamps(string start)
    {
        var json = $$"""
            {
              "schemaVersion": "1.0",
              "source": "invalid-time.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "{{start}}", "end": "00:00:02.000", "text": "invalid" }
              ]
            }
            """;

        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json));

        Assert.Contains("timestamp", exception.Code);
    }

    [Fact]
    public async Task Json_reader_rejects_reversed_timing_with_clear_failure()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "reversed.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:02.000", "end": "00:00:01.000", "text": "reversed" }
              ]
            }
            """;

        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json));

        Assert.Equal("invalid_segment", exception.Code);
        Assert.Contains("end", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Json_reader_rejects_empty_text(string text)
    {
        var json = $$"""
            {
              "schemaVersion": "1.0",
              "source": "empty-text.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                { "start": "00:00:00.000", "end": "00:00:01.000", "text": {{JsonSerializer.Serialize(text)}} }
              ]
            }
            """;

        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json));

        Assert.Equal("empty_property", exception.Code);
        Assert.Contains("text", exception.Message);
    }

    [Fact]
    public async Task Json_reader_rejects_non_finite_confidence()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "confidence.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                {
                  "start": "00:00:00.000",
                  "end": "00:00:01.000",
                  "text": "text",
                  "confidence": 1e999
                }
              ]
            }
            """;

        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(new Processing.TranscriptJsonReader(), json));

        Assert.Contains("confidence", exception.Code);
    }

    [Fact]
    public async Task Json_reader_rejects_missing_unknown_and_duplicate_schema_properties()
    {
        const string missing = """
            {
              "schemaVersion": "1.0",
              "source": "missing.wav",
              "provenance": { "provider": "p", "model": "m" }
            }
            """;
        const string unknown = """
            {
              "schemaVersion": "1.0",
              "source": "unknown.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [],
              "unexpected": true
            }
            """;
        const string duplicate = """
            {
              "schemaVersion": "1.0",
              "source": "duplicate.wav",
              "source": "duplicate-again.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": []
            }
            """;
        var reader = new Processing.TranscriptJsonReader();

        var missingException = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(reader, missing));
        var unknownException = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(reader, unknown));
        var duplicateException = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(reader, duplicate));

        Assert.Equal("missing_property", missingException.Code);
        Assert.Equal("unknown_property", unknownException.Code);
        Assert.Equal("duplicate_property", duplicateException.Code);
    }

    [Fact]
    public async Task Read_document_requires_one_document_and_reports_invalid_json()
    {
        const string invalidJson = "{";
        const string multipleDocuments = """
            [
              {
                "source": "one.wav",
                "model": "m",
                "provider": "p",
                "segments": []
              },
              {
                "source": "two.wav",
                "model": "m",
                "provider": "p",
                "segments": []
              }
            ]
            """;
        var reader = new Processing.TranscriptJsonReader();

        var invalidException = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await ReadDocumentAsync(reader, invalidJson));
        await using var multipleStream = new MemoryStream(Encoding.UTF8.GetBytes(multipleDocuments));
        var multipleException = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await reader.ReadDocumentAsync(multipleStream));

        Assert.Equal("invalid_json", invalidException.Code);
        Assert.Equal("single_document_required", multipleException.Code);
    }

    [Fact]
    public void Typed_contracts_snapshot_collections_and_keep_provenance_extensible()
    {
        var sourceMetadata = new Dictionary<string, string> { ["raw"] = "value" };
        var segments = new List<Processing.TranscriptSegment>
        {
            new(
                "text",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0,
                sourceMetadata: sourceMetadata)
        };
        var provenanceMetadata = new Dictionary<string, string> { ["adapter"] = "future" };
        var provenance = new Processing.TranscriptProvenance(
            "provider",
            "model",
            metadata: provenanceMetadata);
        var document = new Processing.TranscriptDocument("source.wav", provenance, segments);
        sourceMetadata["raw"] = "mutated";
        provenanceMetadata["adapter"] = "mutated";
        segments.Clear();

        Assert.Equal("value", document.Segments[0].SourceMetadata["raw"]);
        Assert.Equal("future", document.Provenance.Metadata["adapter"]);
        Assert.Single(document.Segments);
        Assert.Throws<NotSupportedException>(
            () => ((IList<Processing.TranscriptSegment>)document.Segments).Add(document.Segments[0]));
    }

    [Fact]
    public void Typed_contract_rejects_invalid_confidence_and_original_ordinal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Processing.TranscriptSegment(
                "text",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Processing.TranscriptSegment(
                "text",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0,
                confidence: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Processing.TranscriptSegment(
                "text",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0,
                confidence: 1.1));
    }

    [Fact]
    public void Ingestion_adapter_uses_standard_elements_and_round_trips_transcript_metadata()
    {
        var document = new Processing.TranscriptDocument(
            "adapter.wav",
            new Processing.TranscriptProvenance(
                "provider",
                "model",
                source: "source-adapter",
                metadata: new Dictionary<string, string> { ["run"] = "42" }),
            [
                new Processing.TranscriptSegment(
                    "second",
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    8,
                    sourceId: "source-8",
                    id: "segment-8",
                    speaker: "speaker-b",
                    confidence: 0.8,
                    sourceMetadata: new Dictionary<string, string> { ["channel"] = "right" }),
                new Processing.TranscriptSegment(
                    "first",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    3,
                    sourceId: "source-3",
                    id: "segment-3",
                    speaker: "speaker-a",
                    confidence: 0.3,
                    sourceMetadata: new Dictionary<string, string> { ["channel"] = "left" })
            ]);

        var ingestion = Processing.TranscriptIngestionAdapter.ToIngestionDocument(
            document,
            "adapter-identifier");
        var section = Assert.Single(ingestion.Sections);
        var paragraphs = section.Elements.Cast<DataIngestion.IngestionDocumentParagraph>().ToArray();
        var documentMetadata = Assert.IsType<Processing.TranscriptDocumentMetadata>(
            section.Metadata[Processing.TranscriptIngestionAdapter.DocumentMetadataKey]);

        Assert.IsType<DataIngestion.IngestionDocument>(ingestion);
        Assert.Equal("adapter-identifier", ingestion.Identifier);
        Assert.Equal(["first", "second"], paragraphs.Select(paragraph => paragraph.Text));
        Assert.Equal(document.Source, documentMetadata.Source);
        Assert.Equal(document.Provenance.Metadata, documentMetadata.Provenance.Metadata);

        var firstMetadata = Assert.IsType<Processing.TranscriptSegmentMetadata>(
            paragraphs[0].Metadata[Processing.TranscriptIngestionAdapter.SegmentMetadataKey]);
        Assert.Equal(document.Segments[0].Start, firstMetadata.Start);
        Assert.Equal(document.Segments[0].End, firstMetadata.End);
        Assert.Equal(document.Segments[0].SourceId, firstMetadata.SourceId);
        Assert.Equal(document.Segments[0].Speaker, firstMetadata.Speaker);
        Assert.Equal(document.Segments[0].Confidence, firstMetadata.Confidence);
        Assert.Equal(document.Segments[0].SourceMetadata, firstMetadata.SourceMetadata);

        var roundTrip = Processing.TranscriptIngestionAdapter.ToTranscriptDocument(ingestion);
        Assert.Equal(document.Source, roundTrip.Source);
        Assert.Equal(document.Provenance, roundTrip.Provenance);
        Assert.Equal(
            document.Segments.Select(segment => segment.Id),
            roundTrip.Segments.Select(segment => segment.Id));
        Assert.Equal(
            document.Segments.Select(segment => (segment.Start, segment.End, segment.Speaker, segment.Confidence)),
            roundTrip.Segments.Select(segment => (segment.Start, segment.End, segment.Speaker, segment.Confidence)));
        Assert.Equal(
            document.Segments.Select(segment => segment.SourceMetadata),
            roundTrip.Segments.Select(segment => segment.SourceMetadata));
    }

    [Fact]
    public async Task Ingestion_reader_preserves_source_and_uses_requested_identifier()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "source": "original-source.wav",
              "provenance": { "provider": "p", "model": "m" },
              "segments": [
                {
                  "sourceId": "asr-1",
                  "start": "00:00:00.000",
                  "end": "00:00:01.000",
                  "text": "text"
                }
              ]
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var ingestion = await new Processing.TranscriptIngestionDocumentReader().ReadAsync(
            stream,
            "stable-document-id",
            "application/json");
        var roundTrip = Processing.TranscriptIngestionAdapter.ToTranscriptDocument(ingestion);

        Assert.Equal("stable-document-id", ingestion.Identifier);
        Assert.Equal("original-source.wav", roundTrip.Source);
        Assert.Equal("asr-1", roundTrip.Segments[0].SourceId);
    }

    [Fact]
    public async Task Ingestion_reader_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new Processing.TranscriptIngestionDocumentReader().ReadAsync(
                stream,
                "cancelled",
                "application/json",
                cancellation.Token));
    }

    [Fact]
    public void Ingestion_adapter_rejects_documents_without_typed_metadata()
    {
        var ingestion = new DataIngestion.IngestionDocument("missing-metadata");
        ingestion.Sections.Add(
            new DataIngestion.IngestionDocumentSection
            {
                Elements = { new DataIngestion.IngestionDocumentParagraph("text") }
            });

        var exception = Assert.Throws<Processing.TranscriptFormatException>(
            () => Processing.TranscriptIngestionAdapter.ToTranscriptDocument(ingestion));

        Assert.Equal("invalid_ingestion_document", exception.Code);
        Assert.Contains("missing typed transcript metadata", exception.Message);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static async Task<Processing.TranscriptDocument> ReadDocumentAsync(
        Processing.TranscriptJsonReader reader,
        string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await reader.ReadDocumentAsync(stream);
    }
}
