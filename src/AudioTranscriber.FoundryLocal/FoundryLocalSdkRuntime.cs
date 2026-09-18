using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTranscriber.FoundryLocal;

/// <summary>
/// Bridges the optional Microsoft Foundry Local SDK to the provider-neutral seam.
/// Model downloads are never performed unless explicitly enabled in the options.
/// </summary>
public sealed class FoundryLocalSdkRuntime : IFoundryLocalRuntime
{
    public async Task<FoundryLocalReadiness> CheckReadinessAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!FoundryLocalManager.IsInitialized)
            {
                await FoundryLocalManager
                    .CreateAsync(
                        new Configuration
                        {
                            AppName = options.ApplicationName,
                            ModelCacheDir = options.ModelCacheDirectory
                        },
                        NullLogger.Instance,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var catalog = await FoundryLocalManager.Instance
                .GetCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            var catalogModels = await catalog
                .ListModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var cachedModels = await catalog
                .GetCachedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var loadedModels = await catalog
                .GetLoadedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var availableModels = CreateAvailability(
                catalogModels,
                cachedModels,
                loadedModels);
            var selected = catalogModels.FirstOrDefault(
                model => string.Equals(
                             model.Alias,
                             options.ModelAlias,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             model.Id,
                             options.ModelAlias,
                             StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return new FoundryLocalReadiness(
                    FoundryLocalDiagnosticCode.ModelUnavailable,
                    options.ModelAlias,
                    null,
                    availableModels,
                    $"Foundry Local model '{options.ModelAlias}' was not found in the local catalog.");
            }

            if (!IsChatModel(selected))
            {
                return new FoundryLocalReadiness(
                    FoundryLocalDiagnosticCode.ModelNotReady,
                    selected.Alias,
                    selected.Id,
                    availableModels,
                    $"Foundry Local model '{selected.Alias}' has unsupported task " +
                    $"'{selected.Info.Task}'.");
            }

            var isCached = IsPresent(
                cachedModels,
                selected.Alias,
                selected.Id);
            var isLoaded = IsPresent(
                loadedModels,
                selected.Alias,
                selected.Id);
            var cachePath = isCached
                ? await selected.GetPathAsync(cancellationToken).ConfigureAwait(false)
                : null;
            if (!isCached || !isLoaded)
            {
                return new FoundryLocalReadiness(
                    FoundryLocalDiagnosticCode.ModelNotReady,
                    selected.Alias,
                    selected.Id,
                    availableModels,
                    $"Foundry Local model '{selected.Alias}' is " +
                    $"{(isCached ? "loaded" : "not cached")} and " +
                    $"{(isLoaded ? "loaded" : "not loaded")}.",
                    cachePath: cachePath);
            }

            return new FoundryLocalReadiness(
                diagnosticCode: null,
                selected.Alias,
                selected.Id,
                availableModels,
                $"Foundry Local model '{selected.Alias}' is cached and loaded.",
                cachePath: cachePath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FoundryLocalException exception)
        {
            return new FoundryLocalReadiness(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                null,
                [],
                "Foundry Local reported that its runtime is unavailable: " +
                exception.Message);
        }
        catch (InvalidOperationException)
        {
            return new FoundryLocalReadiness(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                null,
                [],
                "Foundry Local could not initialize its local runtime.");
        }
        catch (IOException)
        {
            return new FoundryLocalReadiness(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                null,
                [],
                "Foundry Local could not access its external runtime state.");
        }
        catch (DllNotFoundException)
        {
            return new FoundryLocalReadiness(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                null,
                [],
                "The Foundry Local native runtime is unavailable.");
        }
        catch (TypeInitializationException)
        {
            return new FoundryLocalReadiness(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                null,
                [],
                "The Foundry Local native runtime could not be initialized.");
        }
    }

    public async Task<FoundryLocalRuntimeSession> PrepareAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!FoundryLocalManager.IsInitialized)
            {
                await FoundryLocalManager
                    .CreateAsync(
                        new Configuration
                        {
                            AppName = options.ApplicationName,
                            ModelCacheDir = options.ModelCacheDirectory
                        },
                        NullLogger.Instance,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var manager = FoundryLocalManager.Instance;
            var catalog = await manager
                .GetCatalogAsync(cancellationToken)
                .ConfigureAwait(false);
            var catalogModels = await catalog
                .ListModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var cachedModels = await catalog
                .GetCachedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var loadedModels = await catalog
                .GetLoadedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var availableModels = CreateAvailability(
                catalogModels,
                cachedModels,
                loadedModels);
            var selected = catalogModels.FirstOrDefault(
                model => string.Equals(
                             model.Alias,
                             options.ModelAlias,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             model.Id,
                             options.ModelAlias,
                             StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw CreateFailure(
                    FoundryLocalDiagnosticCode.ModelUnavailable,
                    options,
                    $"Foundry Local model '{options.ModelAlias}' was not found in the local catalog.",
                    availableModels);
            }

            if (!IsChatModel(selected))
            {
                throw CreateFailure(
                    FoundryLocalDiagnosticCode.ModelNotReady,
                    options,
                    $"Foundry Local model '{selected.Alias}' has unsupported task " +
                    $"'{selected.Info.Task}'.",
                    availableModels);
            }

            var cached = IsPresent(
                cachedModels,
                selected.Alias,
                selected.Id);
            if (!cached && !options.AllowModelDownload)
            {
                throw CreateFailure(
                    FoundryLocalDiagnosticCode.ModelNotReady,
                    options,
                    $"Foundry Local model '{selected.Alias}' is available but not cached. " +
                    "Set AllowModelDownload=true to opt in to a model download.",
                    availableModels);
            }

            if (!cached)
            {
                await selected
                    .DownloadAsync(null, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!IsPresent(loadedModels, selected.Alias, selected.Id))
            {
                await selected
                    .LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            var refreshedCached = await catalog
                .GetCachedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            var refreshedLoaded = await catalog
                .GetLoadedModelsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsPresent(refreshedCached, selected.Alias, selected.Id) ||
                !IsPresent(refreshedLoaded, selected.Alias, selected.Id))
            {
                throw CreateFailure(
                    FoundryLocalDiagnosticCode.ModelNotReady,
                    options,
                    $"Foundry Local model '{selected.Alias}' did not reach cached and loaded state.",
                    CreateAvailability(
                        catalogModels,
                        refreshedCached,
                        refreshedLoaded));
            }

            var modelPath = await selected
                .GetPathAsync(cancellationToken)
                .ConfigureAwait(false);

            return new FoundryLocalRuntimeSession(
                selected.Alias,
                selected.Id,
                endpoint: null,
                modelPath,
                new FoundryLocalSdkChatClient(selected),
                () => new ValueTask(
                    selected.UnloadAsync(CancellationToken.None)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FoundryLocalProviderException)
        {
            throw;
        }
        catch (FoundryLocalException exception)
        {
            throw new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                "Foundry Local reported that its runtime is unavailable.",
                innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                "Foundry Local could not initialize its local runtime.",
                innerException: exception);
        }
        catch (IOException exception)
        {
            throw new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                "Foundry Local could not access its external runtime state.",
                innerException: exception);
        }
        catch (DllNotFoundException exception)
        {
            throw new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                "The Foundry Local native runtime is unavailable.",
                innerException: exception);
        }
        catch (TypeInitializationException exception)
        {
            throw new FoundryLocalProviderException(
                FoundryLocalDiagnosticCode.RuntimeUnavailable,
                options.ModelAlias,
                "The Foundry Local native runtime could not be initialized.",
                innerException: exception);
        }
    }

    private static IReadOnlyList<FoundryLocalModelAvailability> CreateAvailability(
        IEnumerable<IModel> catalog,
        IEnumerable<IModel> cached,
        IEnumerable<IModel> loaded)
    {
        var cachedIds = cached
            .SelectMany(model => ModelKeys(model))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadedIds = loaded
            .SelectMany(model => ModelKeys(model))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog
            .Select(
                model => new FoundryLocalModelAvailability(
                    model.Alias,
                    model.Id,
                    cachedIds.Contains(model.Id) || cachedIds.Contains(model.Alias),
                    loadedIds.Contains(model.Id) || loadedIds.Contains(model.Alias)))
            .ToArray();
    }

    private static bool IsPresent(
        IEnumerable<IModel> models,
        string alias,
        string id) =>
        models
            .SelectMany(ModelKeys)
            .Any(
                key => string.Equals(key, alias, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(key, id, StringComparison.OrdinalIgnoreCase));

    private static bool IsChatModel(IModel model) =>
        string.Equals(
            model.Info.Task,
            "chat-completion",
            StringComparison.Ordinal) ||
        string.Equals(
            model.Info.Task,
            "vision-language-chat",
            StringComparison.Ordinal);

    private static IEnumerable<string> ModelKeys(IModel model)
    {
        yield return model.Alias;
        yield return model.Id;
        foreach (var variant in model.Variants)
        {
            yield return variant.Alias;
            yield return variant.Id;
        }
    }

    private static FoundryLocalProviderException CreateFailure(
        FoundryLocalDiagnosticCode code,
        FoundryLocalEnrichmentOptions options,
        string message,
        IReadOnlyList<FoundryLocalModelAvailability> availableModels,
        Uri? endpoint = null,
        string? cachePath = null) =>
        new(
            code,
            options.ModelAlias,
            message,
            new FoundryLocalReadiness(
                code,
                options.ModelAlias,
                null,
                availableModels,
                message,
                endpoint,
                cachePath));
}
