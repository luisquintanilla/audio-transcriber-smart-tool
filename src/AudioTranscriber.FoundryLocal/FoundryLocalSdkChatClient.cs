using System.Runtime.CompilerServices;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.AI;

namespace AudioTranscriber.FoundryLocal;

/// <summary>
/// Uses Foundry Local's current in-process ChatSession API.
/// </summary>
public sealed class FoundryLocalSdkChatClient : IChatClient
{
    private readonly IModel model;

    public FoundryLocalSdkChatClient(IModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (options?.ModelId is { } requestedModelId &&
            !string.Equals(
                requestedModelId,
                model.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Foundry Local chat client is bound to model '{model.Id}', " +
                $"not '{requestedModelId}'.");
        }

        using var session = new ChatSession(model);
        if (options is not null)
        {
            session.SetOptions(CreateRequestOptions(options));
        }

        using var nativeRequest = new Request();
        using var cancellationRegistration = cancellationToken.Register(
            nativeRequest.Cancel);
        foreach (var message in messages)
        {
            ArgumentNullException.ThrowIfNull(message);
            var item = message.Role == ChatRole.System
                ? MessageItem.System(message.Text)
                : message.Role == ChatRole.User
                    ? MessageItem.User(message.Text)
                    : message.Role == ChatRole.Assistant
                        ? MessageItem.Assistant(message.Text)
                        : string.Equals(
                            message.Role.Value,
                            "developer",
                            StringComparison.OrdinalIgnoreCase)
                            ? MessageItem.Developer(message.Text)
                            : throw new NotSupportedException(
                            $"Foundry Local does not support chat role '{message.Role.Value}'.");
            FoundryLocalRequestOwnership.TransferToRequest(
                item,
                itemToAdd => nativeRequest.AddItem(itemToAdd));
        }

        using var response = await session
            .ProcessRequestAsync(nativeRequest, cancellationToken)
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

        return new ChatResponse(
            new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = model.Id
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(
                messages,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (CanProvideUnkeyedService(typeof(IChatClient), serviceKey) &&
            serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return CanProvideUnkeyedService(typeof(ChatClientMetadata), serviceKey) &&
            serviceType == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(
                "microsoft.ai.foundry.local",
                defaultModelId: model.Id)
            : null;
    }

    internal static bool CanProvideUnkeyedService(
        Type serviceType,
        object? serviceKey)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null &&
            (serviceType == typeof(IChatClient) ||
             serviceType == typeof(ChatClientMetadata));
    }

    internal static RequestOptions CreateRequestOptions(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RequestOptions
        {
            Search = new SearchOptions
            {
                Temperature = options.Temperature ?? 0,
                MaxOutputTokens = options.MaxOutputTokens ?? 1200
            }
        };
    }

    public void Dispose()
    {
        // Model/session ownership belongs to FoundryLocalRuntimeSession.
    }
}
