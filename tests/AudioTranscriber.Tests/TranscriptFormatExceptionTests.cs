using System.Text;
using Processing = AudioTranscriber.TranscriptProcessing;

namespace AudioTranscriber.Tests;

public sealed class TranscriptFormatExceptionTests
{
    [Fact]
    public void Constructor_PreservesJsonPath()
    {
        var exception = new Processing.TranscriptFormatException(
            "invalid_timestamp",
            "$.segments[0].start",
            "Timestamp must be finite.");

        Assert.Equal("$.segments[0].start", exception.JsonPath);
    }

    [Fact]
    public void Constructor_PreservesDocumentedDiagnosticDetails()
    {
        var innerException = new InvalidOperationException("sentinel");
        var exception = new Processing.TranscriptFormatException(
            "invalid_segment",
            "$.segments[0]",
            "Segment timing is invalid.",
            innerException);

        Assert.Equal("invalid_segment", exception.Code);
        Assert.Equal("$.segments[0]", exception.JsonPath);
        Assert.Equal(
            "invalid_segment at $.segments[0]: Segment timing is invalid.",
            exception.Message);
        Assert.Same(innerException, exception.InnerException);
    }

    [Fact]
    public async Task ReaderFailure_ExposesStableFormatDiagnostics()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{"));
        var exception = await Assert.ThrowsAsync<Processing.TranscriptFormatException>(
            async () => await new Processing.TranscriptJsonReader().ReadDocumentAsync(stream));

        Assert.Equal("invalid_json", exception.Code);
        Assert.Equal("$", exception.JsonPath);
        Assert.StartsWith("invalid_json at $: ", exception.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }
}
