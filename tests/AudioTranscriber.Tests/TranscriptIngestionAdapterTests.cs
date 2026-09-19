using DataIngestion = Microsoft.Extensions.DataIngestion;
using Ingestion = AudioTranscriber.TranscriptIngestion;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class TranscriptIngestionAdapterTests
{
    [Fact]
    public void Map_uses_standard_document_elements_and_preserves_metadata()
    {
        var document = CreateDocument(
            "meeting.wav",
            [
                new Processing.TranscriptSegment(
                    "second",
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
                    "first",
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

        var mapped = new Ingestion.TranscriptIngestionAdapter().Map(
            document,
            "stable-document-id");

        Assert.IsType<DataIngestion.IngestionDocument>(mapped);
        Assert.Equal("stable-document-id", mapped.Identifier);
        var section = Assert.Single(mapped.Sections);
        var paragraphs = section.Elements
            .Cast<DataIngestion.IngestionDocumentParagraph>()
            .ToArray();
        Assert.Equal(["first", "second"], paragraphs.Select(paragraph => paragraph.Text));

        var documentMetadata = Assert.IsType<Processing.TranscriptDocumentMetadata>(
            section.Metadata[Processing.TranscriptIngestionAdapter.DocumentMetadataKey]);
        Assert.Equal(document.Source, documentMetadata.Source);
        Assert.Equal(document.Provenance.Metadata, documentMetadata.Provenance.Metadata);
        Assert.NotSame(document.Provenance, documentMetadata.Provenance);
        Assert.NotSame(document.Provenance.Metadata, documentMetadata.Provenance.Metadata);

        var firstMetadata = Assert.IsType<Processing.TranscriptSegmentMetadata>(
            paragraphs[0].Metadata[Processing.TranscriptIngestionAdapter.SegmentMetadataKey]);
        Assert.Equal("segment-1", firstMetadata.Id);
        Assert.Equal("source-1", firstMetadata.SourceId);
        Assert.Equal(1, firstMetadata.OriginalOrdinal);
        Assert.Equal(TimeSpan.Zero, firstMetadata.Start);
        Assert.Equal(TimeSpan.FromSeconds(1), firstMetadata.End);
        Assert.Equal("speaker-a", firstMetadata.Speaker);
        Assert.Equal(0.95, firstMetadata.Confidence);
        Assert.Equal("left", firstMetadata.SourceMetadata["channel"]);
        Assert.NotSame(document.Segments[0].SourceMetadata, firstMetadata.SourceMetadata);
    }

    [Fact]
    public void Map_round_trips_through_the_standard_document_boundary()
    {
        var document = CreateDocument(
            "round-trip.wav",
            [
                new Processing.TranscriptSegment(
                    "text",
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2),
                    4,
                    sourceId: "source-4",
                    id: "segment-4",
                    speaker: "speaker",
                    confidence: 0.4,
                    sourceMetadata: new Dictionary<string, string>
                    {
                        ["channel"] = "center"
                    })
            ]);

        var mapped = new Ingestion.TranscriptIngestionAdapter().Map(document);
        var roundTrip = Processing.TranscriptIngestionAdapter.ToTranscriptDocument(mapped);

        Assert.Equal(document.Source, roundTrip.Source);
        Assert.Equal(document.Provenance.Provider, roundTrip.Provenance.Provider);
        Assert.Equal(document.Provenance.Model, roundTrip.Provenance.Model);
        Assert.Equal(document.Provenance.Metadata, roundTrip.Provenance.Metadata);
        var originalSegment = Assert.Single(document.Segments);
        var roundTripSegment = Assert.Single(roundTrip.Segments);
        Assert.Equal(originalSegment.Id, roundTripSegment.Id);
        Assert.Equal(originalSegment.SourceId, roundTripSegment.SourceId);
        Assert.Equal(originalSegment.OriginalOrdinal, roundTripSegment.OriginalOrdinal);
        Assert.Equal(originalSegment.Start, roundTripSegment.Start);
        Assert.Equal(originalSegment.End, roundTripSegment.End);
        Assert.Equal(originalSegment.Text, roundTripSegment.Text);
        Assert.Equal(originalSegment.Speaker, roundTripSegment.Speaker);
        Assert.Equal(originalSegment.Confidence, roundTripSegment.Confidence);
        Assert.Equal(originalSegment.SourceMetadata, roundTripSegment.SourceMetadata);
    }

    [Fact]
    public void Map_preserves_input_document_order()
    {
        var first = CreateDocument("first.wav");
        var second = CreateDocument("second.wav");

        var mapped = new Ingestion.TranscriptIngestionAdapter().Map([second, first]);

        Assert.Equal(["second.wav", "first.wav"], mapped.Select(document => document.Identifier));
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
        Assert.Throws<ArgumentNullException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(
                (Processing.TranscriptDocument)null!));
    }

    [Fact]
    public void Map_rejects_null_document_entry()
    {
        var documents = new[] { (Processing.TranscriptDocument)null! };

        Assert.Throws<ArgumentException>(
            () => new Ingestion.TranscriptIngestionAdapter().Map(documents));
    }

    [Fact]
    public void Map_honors_cancellation_between_documents()
    {
        var first = CreateDocument("first.wav");
        var second = CreateDocument("second.wav");
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
            yield return CreateDocument("document.wav");
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

    private static Processing.TranscriptDocument CreateDocument(
        string source,
        IReadOnlyList<Processing.TranscriptSegment>? segments = null)
    {
        return new Processing.TranscriptDocument(
            source,
            new Processing.TranscriptProvenance(
                "provider",
                "model",
                metadata: new Dictionary<string, string>
                {
                    ["run"] = "42"
                }),
            segments ??
            [
                new Processing.TranscriptSegment(
                    "text",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1),
                    0)
            ]);
    }
}
