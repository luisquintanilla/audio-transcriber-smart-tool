using Processing = AudioTranscriber.TranscriptProcessing;
using Ingestion = AudioTranscriber.TranscriptIngestion;

namespace AudioTranscriber.Tests;

public sealed class TranscriptIngestionAdapterTests
{
    [Fact]
    public void Map_preserves_provenance_segment_metadata_and_canonical_order()
    {
        var document = new Processing.TranscriptDocument(
            "meeting.wav",
            new Processing.TranscriptProvenance(
                "provider",
                "model",
                packageId: "package",
                packageVersion: "1.2.3",
                source: "offline",
                cachePath: "cache",
                metadata: new Dictionary<string, string>
                {
                    ["tenant"] = "test"
                }),
            [
                new Processing.TranscriptSegment(
                    " second ",
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(3),
                    originalOrdinal: 2,
                    sourceId: "source-2",
                    id: "segment-2",
                    speaker: "speaker-b",
                    confidence: 0.75,
                    sourceMetadata: new Dictionary<string, string>
                    {
                        ["channel"] = "right"
                    }),
                new Processing.TranscriptSegment(
                    " first ",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    originalOrdinal: 1,
                    sourceId: "source-1",
                    id: "segment-1",
                    speaker: "speaker-a",
                    confidence: 0.95,
                    sourceMetadata: new Dictionary<string, string>
                    {
                        ["channel"] = "left"
                    })
            ]);

        var mapped = new Ingestion.TranscriptIngestionAdapter().Map(document);

        Assert.Equal("meeting.wav", mapped.Identifier);
        Assert.Equal(document.Source, mapped.Source);
        Assert.Equal(document.SchemaVersion, mapped.SchemaVersion);
        Assert.Equal("provider", mapped.Provenance.Provider);
        Assert.Equal("model", mapped.Provenance.Model);
        Assert.Equal("package", mapped.Provenance.PackageId);
        Assert.Equal("1.2.3", mapped.Provenance.PackageVersion);
        Assert.Equal("offline", mapped.Provenance.Source);
        Assert.Equal("cache", mapped.Provenance.CachePath);
        Assert.Equal("test", mapped.Provenance.Metadata["tenant"]);
        Assert.NotSame(document.Provenance.Metadata, mapped.Provenance.Metadata);
        Assert.Equal(document.Provenance.Metadata, mapped.Provenance.Metadata);
        Assert.Equal(["first", "second"], mapped.Segments.Select(segment => segment.Text));
        Assert.Equal("first second", mapped.Text);

        var first = mapped.Segments[0];
        Assert.Equal("segment-1", first.Id);
        Assert.Equal("source-1", first.SourceId);
        Assert.Equal(1, first.OriginalOrdinal);
        Assert.Equal(TimeSpan.Zero, first.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), first.End);
        Assert.Equal("speaker-a", first.Speaker);
        Assert.Equal(0.95, first.Confidence);
        Assert.Equal("left", first.SourceMetadata["channel"]);

        var second = mapped.Segments[1];
        Assert.Equal("segment-2", second.Id);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Start);
        Assert.Equal(2, second.OriginalOrdinal);
    }

    [Fact]
    public void Map_preserves_input_document_order()
    {
        var first = CreateDocument("first.wav", 0);
        var second = CreateDocument("second.wav", 1);

        var mapped = new Ingestion.TranscriptIngestionAdapter().Map([second, first]);

        Assert.Equal(["second.wav", "first.wav"], mapped.Select(document => document.Source));
    }

    [Fact]
    public void Map_empty_input_returns_empty_result()
    {
        var mapped = new Ingestion.TranscriptIngestionAdapter().Map(
            Array.Empty<Processing.TranscriptDocument>());

        Assert.Empty(mapped);
    }

    [Fact]
    public void Map_rejects_null_document_collection()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(
                (IEnumerable<Processing.TranscriptDocument>)null!));
    }

    [Fact]
    public void Map_rejects_null_document()
    {
        var adapter = new Ingestion.TranscriptIngestionAdapter();

        Assert.Throws<ArgumentNullException>(
            () => adapter.Map((Processing.TranscriptDocument)null!));
    }

    [Fact]
    public void Map_rejects_null_document_entry()
    {
        var adapter = new Ingestion.TranscriptIngestionAdapter();
        var documents = new[] { (Processing.TranscriptDocument)null! };

        Assert.Throws<ArgumentException>(() => adapter.Map(documents));
    }

    [Fact]
    public void Map_honors_cancellation_between_documents()
    {
        var first = CreateDocument("first.wav", 0);
        var second = CreateDocument("second.wav", 1);
        using var cancellation = new CancellationTokenSource();

        IEnumerable<Processing.TranscriptDocument> Documents()
        {
            yield return first;
            cancellation.Cancel();
            yield return second;
        }

        Assert.Throws<OperationCanceledException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(
                Documents(),
                cancellation.Token));
    }

    [Fact]
    public void Map_checks_cancellation_before_enumerating_documents()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var enumerated = false;

        IEnumerable<Processing.TranscriptDocument> Documents()
        {
            enumerated = true;
            yield return CreateDocument("document.wav", 0);
        }

        Assert.Throws<OperationCanceledException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(
                Documents(),
                cancellation.Token));
        Assert.False(enumerated);
    }

    [Fact]
    public void Map_propagates_source_enumeration_errors()
    {
        static IEnumerable<Processing.TranscriptDocument> FailingDocuments()
        {
            throw new InvalidOperationException("source failed");
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(FailingDocuments()));

        Assert.Equal("source failed", exception.Message);
    }

    private static Processing.TranscriptDocument CreateDocument(string source, int ordinal)
    {
        return new Processing.TranscriptDocument(
            source,
            new Processing.TranscriptProvenance("provider", "model"),
            [
                new Processing.TranscriptSegment(
                    "text",
                    TimeSpan.FromSeconds(ordinal),
                    TimeSpan.FromSeconds(ordinal + 1),
                    ordinal)
            ]);
    }
}
