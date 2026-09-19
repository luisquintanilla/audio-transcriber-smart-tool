using Microsoft.AI.Foundry.Local;

namespace AudioTranscriber.FoundryLocal;

/// <summary>
/// Uses Foundry Local's current in-process ChatSession API.
/// </summary>
public sealed class FoundryLocalSdkChatClient : IFoundryLocalChatClient
{
    private readonly IModel model;

    public FoundryLocalSdkChatClient(IModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public async Task<string> CompleteAsync(
        FoundryLocalChatRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);

        using var session = new ChatSession(model);
        session.SetOptions(
            new RequestOptions
            {
                AdditionalOptions = new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["temperature"] = request.Temperature.ToString(
                        "R",
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["max_tokens"] = request.MaxOutputTokens.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                }
            });
        using var nativeRequest = new Request();
        foreach (var message in request.Messages)
        {
            var item = message.Role switch
            {
                "system" => MessageItem.System(message.Content),
                "user" => MessageItem.User(message.Content),
                "assistant" => MessageItem.Assistant(message.Content),
                _ => throw new ArgumentException(
                    $"Unsupported chat role '{message.Role}'.",
                    nameof(request))
            };
            FoundryLocalRequestOwnership.TransferToRequest(
                item,
                itemToAdd => nativeRequest.AddItem(itemToAdd));
        }

        try
        {
            using var response = await session
                .ProcessRequestAsync(nativeRequest, timeoutCancellation.Token)
                .ConfigureAwait(false);
            var text = response
                .OfType<MessageItem>()
                .Select(message => message.IsSimpleText()
                    ? message.GetSimpleText()
                    : string.Concat(
                        message.Parts
                            .OfType<TextItem>()
                            .Select(part => part.Text)))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidDataException(
                    "Foundry Local returned an empty chat completion.");
            }

            return text;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Foundry Local did not respond within {timeout}.");
        }
    }
}
