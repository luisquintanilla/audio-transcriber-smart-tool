using Microsoft.Extensions.AI;
using DataIngestion = Microsoft.Extensions.DataIngestion;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class TranscriptChunkingTests
{
    [Fact]
    public void Build_EmptyTranscript_ReturnsEmptyResult()
    {
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(),
            CreateOptions());

        Assert.Empty(result.Windows);
    }

    [Fact]
    public void Build_ConsumesCanonicalDataIngestionElementsAndTypedMetadata()
    {
        var segment = Segment(
            "canonical transcript element",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            ordinal: 7,
            id: "segment-canonical",
            sourceId: "asr-canonical",
            speaker: "speaker-canonical",
            confidence: 0.87,
            metadata: new Dictionary<string, string>
            {
                ["channel"] = "left"
            });
        var document = CreateDocument(segment);
        var section = Assert.Single(document.Sections);
        var paragraph = Assert.IsType<DataIngestion.IngestionDocumentParagraph>(
            Assert.Single(section.Elements));

        Assert.Contains(
            Processing.TranscriptIngestionAdapter.DocumentMetadataKey,
            section.Metadata.Keys);
        Assert.Contains(
            Processing.TranscriptIngestionAdapter.SegmentMetadataKey,
            paragraph.Metadata.Keys);

        var result = CreateBuilder().Build(document, CreateOptions());
        var window = Assert.Single(result.Windows);

        Assert.Same(document, result.Document);
        Assert.Same(paragraph, Assert.Single(window.SourceElements));
        var metadata = Assert.Single(window.SourceSegments);
        Assert.Equal("segment-canonical", metadata.Id);
        Assert.Equal("asr-canonical", metadata.SourceId);
        Assert.Equal(7, metadata.OriginalOrdinal);
        Assert.Equal(TimeSpan.FromSeconds(2), metadata.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), metadata.End);
        Assert.Equal("speaker-canonical", metadata.Speaker);
        Assert.Equal(0.87, metadata.Confidence);
        Assert.Equal("canonical transcript element", metadata.Text);
        Assert.Equal("left", metadata.SourceMetadata["channel"]);
    }

    [Fact]
    public void Build_RequestedRangeBeyondTranscript_EmitsSnappedLeadingAndTrailingGaps()
    {
        var segment = Segment(
            "middle transcript",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            ordinal: 0,
            id: "segment-middle",
            sourceId: "source-middle",
            metadata: new Dictionary<string, string>
            {
                ["channel"] = "center"
            });

        var result = CreateBuilder().Build(
            CreateDocument(segment),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(5),
                requestedStart: TimeSpan.Zero,
                requestedEnd: TimeSpan.FromSeconds(4)));

        Assert.Collection(
            result.Windows,
            leadingGap =>
            {
                Assert.True(leadingGap.IsGap);
                Assert.Equal(TimeSpan.Zero, leadingGap.Start);
                Assert.Equal(TimeSpan.FromSeconds(2), leadingGap.End);
                Assert.Empty(leadingGap.SourceSegmentIds);
                Assert.Empty(leadingGap.SourceSegments);
            },
            content =>
            {
                Assert.False(content.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(2), content.Start);
                Assert.Equal(TimeSpan.FromSeconds(3), content.End);
                Assert.Equal(["segment-middle"], content.SourceSegmentIds);
                Assert.Equal("source-middle", Assert.Single(content.SourceIds));
                Assert.Equal(
                    "center",
                    Assert.Single(content.SourceSegments)
                        .SourceMetadata["channel"]);
            },
            trailingGap =>
            {
                Assert.True(trailingGap.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(3), trailingGap.Start);
                Assert.Equal(TimeSpan.FromSeconds(4), trailingGap.End);
                Assert.Empty(trailingGap.SourceSegmentIds);
                Assert.Empty(trailingGap.SourceSegments);
            });
    }

    [Fact]
    public void Build_SingleSegment_PreservesSourceIdAndMetadata()
    {
        var segment = Segment(
            "opening statement",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2.5),
            ordinal: 4,
            id: "segment-opening",
            sourceId: "asr-42",
            speaker: "speaker-a",
            confidence: 0.93,
            metadata: new Dictionary<string, string>
            {
                ["channel"] = "left",
                ["rawId"] = "fragment-42"
            });
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(segment),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(4)));

        var window = Assert.Single(result.Windows);
        Assert.Equal("opening statement", window.Text);
        Assert.Equal(TimeSpan.Zero, window.Start);
        Assert.Equal(TimeSpan.FromSeconds(2.5), window.End);
        Assert.Equal(["segment-opening"], window.SourceSegmentIds);

        var source = Assert.Single(window.SourceSegments);
        Assert.Equal("segment-opening", source.Id);
        Assert.Equal("asr-42", source.SourceId);
        Assert.Equal(4, source.OriginalOrdinal);
        Assert.Equal("speaker-a", source.Speaker);
        Assert.Equal(0.93, source.Confidence);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["channel"] = "left",
                ["rawId"] = "fragment-42"
            },
            source.SourceMetadata);
    }

    [Fact]
    public void Build_MultipleSegments_PreservesAllSourceIdsAndMetadata()
    {
        var first = Segment(
            "first fragment",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            ordinal: 0,
            id: "segment-first",
            sourceId: "asr-first",
            metadata: new Dictionary<string, string> { ["channel"] = "left" });
        var second = Segment(
            "second fragment",
            TimeSpan.FromSeconds(2.25),
            TimeSpan.FromSeconds(4),
            ordinal: 1,
            id: "segment-second",
            sourceId: "asr-second",
            metadata: new Dictionary<string, string> { ["channel"] = "right" });
        var third = Segment(
            "third fragment",
            TimeSpan.FromSeconds(4.25),
            TimeSpan.FromSeconds(6),
            ordinal: 2,
            id: "segment-third",
            sourceId: "asr-third",
            metadata: new Dictionary<string, string> { ["channel"] = "center" });
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second, third),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(10)));

        var sources = result.Windows
            .SelectMany(window => window.SourceSegments)
            .ToArray();

        Assert.Equal(
            ["segment-first", "segment-second", "segment-third"],
            sources.Select(source => source.Id));
        Assert.Equal(
            ["asr-first", "asr-second", "asr-third"],
            sources.Select(source => source.SourceId));
        Assert.Equal(
            ["left", "right", "center"],
            sources.Select(source => source.SourceMetadata["channel"]));
        Assert.Equal(
            ["first fragment", "second fragment", "third fragment"],
            result.Windows
                .SelectMany(window => window.SourceElements)
                .Select(element => element.Text));
        Assert.Equal(
            ["segment-first", "segment-second", "segment-third"],
            result.Windows.SelectMany(window => window.SourceSegmentIds));
    }

    [Fact]
    public void Build_SnapsStartToSourceSegmentBoundary()
    {
        var segment = Segment(
            "boundary spanning text",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            ordinal: 0,
            id: "segment-boundary");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(segment),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(5),
                requestedStart: TimeSpan.FromSeconds(1.25),
                requestedEnd: TimeSpan.FromSeconds(2.75)));

        var window = Assert.Single(result.Windows);
        Assert.Equal(TimeSpan.Zero, window.Start);
        Assert.NotEqual(TimeSpan.FromSeconds(1.25), window.Start);
        Assert.Equal("segment-boundary", Assert.Single(window.SourceSegmentIds));
        Assert.True(window.End > window.Start);
    }

    [Fact]
    public void Build_SnapsInternalStartToSourceSegmentBoundary()
    {
        var first = Segment(
            "first internal fragment",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            ordinal: 0,
            id: "segment-first-internal");
        var middle = Segment(
            "middle internal fragment",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            ordinal: 1,
            id: "segment-middle-internal");
        var last = Segment(
            "last internal fragment",
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(6),
            ordinal: 2,
            id: "segment-last-internal");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, middle, last),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(10),
                requestedStart: TimeSpan.FromSeconds(2.25),
                requestedEnd: TimeSpan.FromSeconds(5.5)));

        var window = Assert.Single(result.Windows);
        Assert.False(window.IsGap);
        Assert.Equal(TimeSpan.FromSeconds(2), window.Start);
        Assert.Equal(TimeSpan.FromSeconds(6), window.End);
        Assert.Equal(
            ["segment-middle-internal", "segment-last-internal"],
            window.SourceSegmentIds);
        Assert.DoesNotContain("segment-first-internal", window.SourceSegmentIds);
    }

    [Fact]
    public void Build_SnapsEndToSourceSegmentBoundary()
    {
        var segment = Segment(
            "boundary ending text",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(4),
            ordinal: 0,
            id: "segment-boundary");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(segment),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(5),
                requestedStart: TimeSpan.FromSeconds(0.75),
                requestedEnd: TimeSpan.FromSeconds(3.25)));

        var window = Assert.Single(result.Windows);
        Assert.Equal(TimeSpan.FromSeconds(4), window.End);
        Assert.NotEqual(TimeSpan.FromSeconds(3.25), window.End);
        Assert.Equal(TimeSpan.Zero, window.Start);
        Assert.True(window.End > window.Start);
    }

    [Fact]
    public void Build_SnapsInternalEndToSourceSegmentBoundary()
    {
        var first = Segment(
            "first internal fragment",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            ordinal: 0,
            id: "segment-first-internal");
        var middle = Segment(
            "middle internal fragment",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            ordinal: 1,
            id: "segment-middle-internal");
        var last = Segment(
            "last internal fragment",
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(6),
            ordinal: 2,
            id: "segment-last-internal");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, middle, last),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(10),
                requestedStart: TimeSpan.FromSeconds(0.5),
                requestedEnd: TimeSpan.FromSeconds(3.5)));

        var window = Assert.Single(result.Windows);
        Assert.False(window.IsGap);
        Assert.Equal(TimeSpan.Zero, window.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), window.End);
        Assert.Equal(
            ["segment-first-internal", "segment-middle-internal"],
            window.SourceSegmentIds);
        Assert.DoesNotContain("segment-last-internal", window.SourceSegmentIds);
    }

    [Fact]
    public void Build_GapBetweenSegments_EmitsExplicitGap()
    {
        var first = Segment(
            "before the pause",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0,
            id: "segment-before-gap");
        var second = Segment(
            "after the pause",
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(4),
            ordinal: 1,
            id: "segment-after-gap");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(5),
                requestedStart: TimeSpan.Zero,
                requestedEnd: TimeSpan.FromSeconds(4)));

        var gap = Assert.Single(result.Windows, window => window.IsGap);
        Assert.Equal(TimeSpan.FromSeconds(1), gap.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), gap.End);
        Assert.Equal(string.Empty, gap.Text);
        Assert.Empty(gap.SourceSegmentIds);
        Assert.Empty(gap.SourceSegments);
        Assert.Contains(
            result.Windows,
            window => window.SourceSegmentIds.SequenceEqual(["segment-before-gap"]));
        Assert.Contains(
            result.Windows,
            window => window.SourceSegmentIds.SequenceEqual(["segment-after-gap"]));
        Assert.Equal(3, result.Windows.Count());
        Assert.Collection(
            result.Windows,
            before =>
            {
                Assert.False(before.IsGap);
                Assert.Equal(TimeSpan.Zero, before.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), before.End);
                Assert.Equal("before the pause", before.Text);
                Assert.Equal(["segment-before-gap"], before.SourceSegmentIds);
            },
            gapWindow =>
            {
                Assert.True(gapWindow.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(1), gapWindow.Start);
                Assert.Equal(TimeSpan.FromSeconds(3), gapWindow.End);
                Assert.Equal(string.Empty, gapWindow.Text);
                Assert.Empty(gapWindow.SourceSegmentIds);
                Assert.Empty(gapWindow.SourceSegments);
            },
            after =>
            {
                Assert.False(after.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(3), after.Start);
                Assert.Equal(TimeSpan.FromSeconds(4), after.End);
                Assert.Equal("after the pause", after.Text);
                Assert.Equal(["segment-after-gap"], after.SourceSegmentIds);
            });
    }

    [Fact]
    public void Build_LongGap_PartitionsWithinMaximumDuration()
    {
        var first = Segment(
            "before the pause",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0);
        var second = Segment(
            "after the pause",
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(13),
            ordinal: 1);

        var result = CreateBuilder().Build(
            CreateDocument(first, second),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(4)));

        var gaps = result.Windows.Where(window => window.IsGap).ToArray();
        Assert.Equal(3, gaps.Length);
        Assert.Equal(TimeSpan.FromSeconds(1), gaps[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(12), gaps[^1].End);
        Assert.All(
            gaps,
            gap => Assert.True(
                gap.End - gap.Start <= TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void Build_RejectsGapThatWouldCreateTooManyWindows()
    {
        var first = Segment(
            "before the pause",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0);
        var second = Segment(
            "after the pause",
            TimeSpan.FromTicks(TimeSpan.FromSeconds(1).Ticks + 10_001),
            TimeSpan.FromTicks(TimeSpan.FromSeconds(1).Ticks + 10_002),
            ordinal: 1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateBuilder().Build(
                CreateDocument(first, second),
                CreateOptions(
                    minimumDuration: TimeSpan.FromTicks(1),
                    maximumDuration: TimeSpan.FromTicks(1))));

        Assert.Contains("maximum number of chunk windows", exception.Message);
    }

    [Fact]
    public void Build_GapAtChunkBoundary_ClipsGapToRequestedRange()
    {
        var first = Segment(
            "first side",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0,
            id: "segment-left");
        var second = Segment(
            "second side",
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            ordinal: 1,
            id: "segment-right");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1),
                maximumDuration: TimeSpan.FromSeconds(5),
                requestedStart: TimeSpan.FromSeconds(1.5),
                requestedEnd: TimeSpan.FromSeconds(3.5)));

        var gap = Assert.Single(result.Windows, window => window.IsGap);
        Assert.Equal(TimeSpan.FromSeconds(1.5), gap.Start);
        Assert.Equal(TimeSpan.FromSeconds(3.5), gap.End);
        Assert.Equal(TimeSpan.FromSeconds(2), gap.End - gap.Start);
        var onlyWindow = Assert.Single(result.Windows);
        Assert.True(onlyWindow.IsGap);
        Assert.Equal(string.Empty, onlyWindow.Text);
        Assert.Empty(gap.SourceSegmentIds);
        Assert.Empty(gap.SourceSegments);
    }

    [Fact]
    public void Build_RespectsMinimumDuration()
    {
        var segment = Segment(
            "expand to legal boundary",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            ordinal: 0,
            id: "segment-expand");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(segment),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(2),
                maximumDuration: TimeSpan.FromSeconds(4),
                requestedStart: TimeSpan.FromSeconds(0.75),
                requestedEnd: TimeSpan.FromSeconds(1.25)));

        var window = Assert.Single(result.Windows);
        Assert.False(window.IsGap);
        Assert.Equal(TimeSpan.FromSeconds(3), window.End - window.Start);
        Assert.Equal(TimeSpan.Zero, window.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), window.End);
        Assert.Equal("expand to legal boundary", window.Text);
        Assert.Equal("segment-expand", Assert.Single(window.SourceSegmentIds));
        Assert.Empty(window.SourceSegments.Single().SourceMetadata);
    }

    [Fact]
    public void Build_MinimumDuration_MergesContiguousSegmentsWithinMaximum()
    {
        var first = Segment(
            "first short fragment",
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(500),
            ordinal: 0,
            id: "segment-minimum-first");
        var second = Segment(
            "second short fragment",
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            ordinal: 1,
            id: "segment-minimum-second");
        var third = Segment(
            "third short fragment",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1.5),
            ordinal: 2,
            id: "segment-minimum-third");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second, third),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(1.25),
                maximumDuration: TimeSpan.FromSeconds(2)));

        var window = Assert.Single(result.Windows);
        Assert.False(window.IsGap);
        Assert.Equal(TimeSpan.FromSeconds(1.5), window.End - window.Start);
        Assert.Equal(
            ["segment-minimum-first", "segment-minimum-second", "segment-minimum-third"],
            window.SourceSegmentIds);
    }

    [Fact]
    public void Build_MinimumDuration_RebalancesBoundaryToAvoidShortTail()
    {
        var first = Segment(
            "long first fragment",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(0.8),
            ordinal: 0,
            id: "segment-rebalance-first");
        var second = Segment(
            "short middle fragment",
            TimeSpan.FromSeconds(0.8),
            TimeSpan.FromSeconds(1),
            ordinal: 1,
            id: "segment-rebalance-middle");
        var third = Segment(
            "short final fragment",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1.3),
            ordinal: 2,
            id: "segment-rebalance-final");

        var result = CreateBuilder().Build(
            CreateDocument(first, second, third),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(0.4),
                maximumDuration: TimeSpan.FromSeconds(1)));

        Assert.Collection(
            result.Windows,
            firstWindow =>
            {
                Assert.Equal(TimeSpan.Zero, firstWindow.Start);
                Assert.Equal(TimeSpan.FromSeconds(0.8), firstWindow.End);
                Assert.Equal(
                    ["segment-rebalance-first"],
                    firstWindow.SourceSegmentIds);
            },
            secondWindow =>
            {
                Assert.Equal(TimeSpan.FromSeconds(0.8), secondWindow.Start);
                Assert.Equal(TimeSpan.FromSeconds(1.3), secondWindow.End);
                Assert.Equal(
                    ["segment-rebalance-middle", "segment-rebalance-final"],
                    secondWindow.SourceSegmentIds);
                Assert.True(
                    secondWindow.End - secondWindow.Start >=
                    TimeSpan.FromSeconds(0.4));
            });
    }

    [Fact]
    public void Build_MinimumDuration_WhenGapPreventsExpansion_UsesExplicitGap()
    {
        var first = Segment(
            "short first fragment",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0,
            id: "segment-short-first");
        var second = Segment(
            "short second fragment",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(6),
            ordinal: 1,
            id: "segment-short-second");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second),
            CreateOptions(
                minimumDuration: TimeSpan.FromSeconds(3),
                maximumDuration: TimeSpan.FromSeconds(8),
                requestedStart: TimeSpan.Zero,
                requestedEnd: TimeSpan.FromSeconds(6)));

        Assert.Equal(3, result.Windows.Count());
        Assert.Collection(
            result.Windows,
            firstWindow =>
            {
                Assert.False(firstWindow.IsGap);
                Assert.Equal(TimeSpan.Zero, firstWindow.Start);
                Assert.Equal(TimeSpan.FromSeconds(1), firstWindow.End);
                Assert.Equal("short first fragment", firstWindow.Text);
                Assert.Equal(["segment-short-first"], firstWindow.SourceSegmentIds);
            },
            gap =>
            {
                Assert.True(gap.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(1), gap.Start);
                Assert.Equal(TimeSpan.FromSeconds(5), gap.End);
                Assert.Equal(TimeSpan.FromSeconds(4), gap.End - gap.Start);
                Assert.Equal(string.Empty, gap.Text);
                Assert.Empty(gap.SourceSegmentIds);
                Assert.Empty(gap.SourceSegments);
            },
            secondWindow =>
            {
                Assert.False(secondWindow.IsGap);
                Assert.Equal(TimeSpan.FromSeconds(5), secondWindow.Start);
                Assert.Equal(TimeSpan.FromSeconds(6), secondWindow.End);
                Assert.Equal("short second fragment", secondWindow.Text);
                Assert.Equal(["segment-short-second"], secondWindow.SourceSegmentIds);
            });
    }

    [Fact]
    public void Build_RespectsMaximumDuration()
    {
        var first = Segment(
            "one",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            ordinal: 0,
            id: "segment-one");
        var second = Segment(
            "two",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            ordinal: 1,
            id: "segment-two");
        var third = Segment(
            "three",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            ordinal: 2,
            id: "segment-three");
        var builder = CreateBuilder();

        var result = builder.Build(
            CreateDocument(first, second, third),
            CreateOptions(
                minimumDuration: TimeSpan.FromMilliseconds(500),
                maximumDuration: TimeSpan.FromSeconds(1.5)));

        var legalBoundaries = new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3)
        };
        var contentWindows = result.Windows.Where(window => !window.IsGap).ToArray();

        Assert.NotEmpty(contentWindows);
        Assert.All(
            contentWindows,
            window =>
            {
                Assert.True(window.End - window.Start <= TimeSpan.FromSeconds(1.5));
                Assert.Contains(window.Start, legalBoundaries);
                Assert.Contains(window.End, legalBoundaries);
            });
        Assert.Equal(
            ["segment-one", "segment-two", "segment-three"],
            contentWindows.SelectMany(window => window.SourceSegmentIds));
    }

    [Fact]
    public void Build_InvalidDurationOptions_ThrowsDocumentedArgumentException()
    {
        var builder = CreateBuilder();
        var document = CreateDocument(
            Segment("duration validation", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));

        var negativeMinimum = Assert.Throws<ArgumentException>(
            () => builder.Build(
                document,
                CreateOptions(
                    minimumDuration: TimeSpan.FromSeconds(-1),
                    maximumDuration: TimeSpan.FromSeconds(2))));
        var zeroMaximum = Assert.Throws<ArgumentException>(
            () => builder.Build(
                document,
                CreateOptions(
                    minimumDuration: TimeSpan.FromSeconds(1),
                    maximumDuration: TimeSpan.Zero)));
        var zeroMinimum = Assert.Throws<ArgumentException>(
            () => builder.Build(
                document,
                CreateOptions(
                    minimumDuration: TimeSpan.Zero,
                    maximumDuration: TimeSpan.FromSeconds(2))));
        var negativeMaximum = Assert.Throws<ArgumentException>(
            () => builder.Build(
                document,
                CreateOptions(
                    minimumDuration: TimeSpan.FromSeconds(1),
                    maximumDuration: TimeSpan.FromSeconds(-1))));
        var invertedRange = Assert.Throws<ArgumentException>(
            () => builder.Build(
                document,
                CreateOptions(
                    minimumDuration: TimeSpan.FromSeconds(3),
                    maximumDuration: TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public void Build_UnsupportedTranscriptSchema_ThrowsFormatException()
    {
        var document = CreateDocument(
            Segment("unsupported schema", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));
        document.Sections[0].Metadata[Processing.TranscriptIngestionAdapter.DocumentMetadataKey] =
            new Processing.TranscriptDocumentMetadata(
                "2.0",
                "chunking-fixture.wav",
                new Processing.TranscriptProvenance(
                    "fixture-provider",
                    "fixture-model"));

        var exception = Assert.Throws<Processing.TranscriptFormatException>(
            () => CreateBuilder().Build(document, CreateOptions()));

        Assert.Equal("unsupported_schema_version", exception.Code);
        Assert.Equal("$.schemaVersion", exception.JsonPath);
        Assert.Contains("'2.0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AdditionalSection_ThrowsFormatException()
    {
        var document = CreateDocument(
            Segment("canonical transcript", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));
        document.Sections.Add(new DataIngestion.IngestionDocumentSection());

        var exception = Assert.Throws<Processing.TranscriptFormatException>(
            () => CreateBuilder().Build(document, CreateOptions()));

        Assert.Equal("invalid_ingestion_document", exception.Code);
        Assert.Contains("exactly one section", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MalformedTiming_IsRejectedWithDocumentedValidationDetails()
    {
        var reversed = Assert.Throws<ArgumentOutOfRangeException>(
            () => Segment(
                "reversed",
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1),
                ordinal: 0));
        var negative = Assert.Throws<ArgumentOutOfRangeException>(
            () => Segment(
                "negative",
                TimeSpan.FromSeconds(-1),
                TimeSpan.Zero,
                ordinal: 0));

        var overlap = Assert.Throws<ArgumentException>(
            () => CreateTranscriptDocument(
                Segment("first", TimeSpan.Zero, TimeSpan.FromSeconds(2), 0),
                Segment("overlap", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), 1)));
        var nonMonotonic = Assert.Throws<ArgumentException>(
            () => CreateTranscriptDocument(
                Segment("late", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), 0),
                Segment("early", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), 1)));

        Assert.Equal("end", reversed.ParamName);
        Assert.Equal("start", negative.ParamName);
        Assert.Contains("overlap", overlap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("monotonic", nonMonotonic.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Processing.TranscriptChunkBuilder CreateBuilder()
    {
        return new Processing.TranscriptChunkBuilder(
            new DeterministicEmbeddingProvider(),
            new DeterministicChunkScoringProvider());
    }

    private static DataIngestion.IngestionDocument CreateDocument(
        params Processing.TranscriptSegment[] segments)
    {
        return Processing.TranscriptIngestionAdapter.ToIngestionDocument(
            CreateTranscriptDocument(segments));
    }

    private static Processing.TranscriptDocument CreateTranscriptDocument(
        params Processing.TranscriptSegment[] segments)
    {
        return new Processing.TranscriptDocument(
            "chunking-fixture.wav",
            new Processing.TranscriptProvenance(
                "fixture-provider",
                "fixture-model",
                metadata: new Dictionary<string, string>
                {
                    ["fixture"] = "phase-2"
                }),
            segments);
    }

    private static Processing.TranscriptChunkingOptions CreateOptions(
        TimeSpan? minimumDuration = null,
        TimeSpan? maximumDuration = null,
        TimeSpan? requestedStart = null,
        TimeSpan? requestedEnd = null)
    {
        return new Processing.TranscriptChunkingOptions
        {
            MinimumDuration = minimumDuration ?? TimeSpan.FromSeconds(1),
            MaximumDuration = maximumDuration ?? TimeSpan.FromSeconds(10),
            RequestedStart = requestedStart,
            RequestedEnd = requestedEnd
        };
    }

    private static Processing.TranscriptSegment Segment(
        string text,
        TimeSpan start,
        TimeSpan end,
        int ordinal,
        string? id = null,
        string? sourceId = null,
        string? speaker = null,
        double? confidence = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        return new Processing.TranscriptSegment(
            text,
            start,
            end,
            ordinal,
            sourceId: sourceId,
            id: id,
            speaker: speaker,
            confidence: confidence,
            sourceMetadata: metadata);
    }

    private sealed class DeterministicEmbeddingProvider
        : IEmbeddingGenerator<TextContent, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<TextContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new GeneratedEmbeddings<Embedding<float>>(
                [
                    new Embedding<float>(new float[] { 0.25f, -0.5f, 0.75f })
                ]));
        }

        public object? GetService(Type serviceType, object? serviceKey) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class DeterministicChunkScoringProvider
        : Processing.ITranscriptChunkScoringProvider
    {
        public ValueTask<double> ScoreAsync(
            Processing.TranscriptChunkScoringRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(0.75d);
        }
    }
}
