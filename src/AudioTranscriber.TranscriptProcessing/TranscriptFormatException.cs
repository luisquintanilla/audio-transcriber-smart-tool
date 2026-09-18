namespace AudioTranscriber.TranscriptProcessing;

public sealed class TranscriptFormatException : FormatException
{
    public string Code { get; }
    public string JsonPath { get; }

    public TranscriptFormatException(
        string code,
        string jsonPath,
        string message,
        Exception? innerException = null)
        : base($"{code} at {jsonPath}: {message}", innerException)
    {
        Code = code;
        JsonPath = jsonPath;
    }
}
