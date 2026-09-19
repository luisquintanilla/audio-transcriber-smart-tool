using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTranscriber.FoundryLocal;

internal interface IFoundryLocalManagerHost
{
    bool IsInitialized { get; }

    Task InitializeAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken);
}

internal sealed class FoundryLocalManagerHost : IFoundryLocalManagerHost
{
    public bool IsInitialized => FoundryLocalManager.IsInitialized;

    public Task InitializeAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken) =>
        FoundryLocalManager.CreateAsync(
            new Configuration
            {
                AppName = options.ApplicationName,
                ModelCacheDir = options.ModelCacheDirectory
            },
            NullLogger.Instance,
            cancellationToken);
}

internal sealed class FoundryLocalManagerConfigurationRegistry
{
    private readonly IFoundryLocalManagerHost managerHost;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FoundryLocalManagerConfiguration? acceptedConfiguration;

    public FoundryLocalManagerConfigurationRegistry(
        IFoundryLocalManagerHost managerHost)
    {
        this.managerHost = managerHost
            ?? throw new ArgumentNullException(nameof(managerHost));
    }

    public async Task EnsureCompatibleAsync(
        FoundryLocalEnrichmentOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var requested = FoundryLocalManagerConfiguration.From(options);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!managerHost.IsInitialized)
            {
                acceptedConfiguration = null;
            }

            if (acceptedConfiguration is not null &&
                !acceptedConfiguration.Equals(requested))
            {
                throw new FoundryLocalProviderException(
                    FoundryLocalDiagnosticCode.InvalidConfiguration,
                    options.ModelAlias,
                    "Foundry Local is already initialized with a different " +
                    "application name or model-cache directory.");
            }

            if (managerHost.IsInitialized)
            {
                if (acceptedConfiguration is null)
                {
                    throw new FoundryLocalProviderException(
                        FoundryLocalDiagnosticCode.InvalidConfiguration,
                        options.ModelAlias,
                        "Foundry Local is already initialized outside this adapter; " +
                        "its application name and model-cache directory cannot be verified.");
                }

                return;
            }

            await managerHost
                .InitializeAsync(options, cancellationToken)
                .ConfigureAwait(false);
            acceptedConfiguration = requested;
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record FoundryLocalManagerConfiguration(
        string ApplicationName,
        string? ModelCacheDirectory)
    {
        public static FoundryLocalManagerConfiguration From(
            FoundryLocalEnrichmentOptions options) =>
            new(
                options.ApplicationName.Trim(),
                NormalizeDirectory(options.ModelCacheDirectory));

        private static string? NormalizeDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root) &&
                string.Equals(fullPath, root, GetComparison()))
            {
                return root;
            }

            return fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static StringComparison GetComparison() =>
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
    }
}

internal interface IFoundryLocalModelLifecycle
{
    string ModelId { get; }

    Task<bool> IsLoadedAsync(CancellationToken cancellationToken);

    Task LoadAsync(CancellationToken cancellationToken);

    Task UnloadAsync(CancellationToken cancellationToken);
}

internal sealed class FoundryLocalSdkModelLifecycle : IFoundryLocalModelLifecycle
{
    private readonly IModel model;

    public FoundryLocalSdkModelLifecycle(IModel model)
    {
        this.model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public string ModelId => model.Id;

    public Task<bool> IsLoadedAsync(CancellationToken cancellationToken) =>
        model.IsLoadedAsync(cancellationToken);

    public Task LoadAsync(CancellationToken cancellationToken) =>
        model.LoadAsync(cancellationToken);

    public Task UnloadAsync(CancellationToken cancellationToken) =>
        model.UnloadAsync(cancellationToken);
}

internal sealed class FoundryLocalModelLeaseRegistry
{
    internal sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int ReferenceCount;
        public bool AdapterOwnsLoad;
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly object entriesGate = new();

    public async Task<FoundryLocalModelLease> AcquireAsync(
        IFoundryLocalModelLifecycle model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        Entry entry;
        lock (entriesGate)
        {
            if (!entries.TryGetValue(model.ModelId, out entry!))
            {
                entry = new Entry();
                entries.Add(model.ModelId, entry);
            }
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.ReferenceCount == 0)
            {
                if (await model.IsLoadedAsync(cancellationToken).ConfigureAwait(false))
                {
                    entry.AdapterOwnsLoad = false;
                }
                else
                {
                    await model.LoadAsync(cancellationToken).ConfigureAwait(false);
                    entry.AdapterOwnsLoad = true;
                }
            }

            entry.ReferenceCount++;
            return new FoundryLocalModelLease(
                this,
                model.ModelId,
                model,
                entry);
        }
        catch
        {
            lock (entriesGate)
            {
                if (entry.ReferenceCount == 0)
                {
                    entries.Remove(model.ModelId);
                }
            }

            throw;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async ValueTask ReleaseAsync(
        string modelId,
        IFoundryLocalModelLifecycle model,
        Entry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        var unload = false;
        try
        {
            if (entry.ReferenceCount <= 0)
            {
                return;
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                unload = entry.AdapterOwnsLoad;
                try
                {
                    if (unload)
                    {
                        await model
                            .UnloadAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    entry.AdapterOwnsLoad = false;
                    lock (entriesGate)
                    {
                        entries.Remove(modelId);
                    }
                }
            }
        }
        finally
        {
            entry.Gate.Release();
        }

    }

    internal sealed class FoundryLocalModelLease : IAsyncDisposable
    {
        private readonly FoundryLocalModelLeaseRegistry registry;
        private readonly string modelId;
        private readonly IFoundryLocalModelLifecycle model;
        private readonly Entry entry;
        private int released;

        internal FoundryLocalModelLease(
            FoundryLocalModelLeaseRegistry registry,
            string modelId,
            IFoundryLocalModelLifecycle model,
            Entry entry)
        {
            this.registry = registry;
            this.modelId = modelId;
            this.model = model;
            this.entry = entry;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            return registry.ReleaseAsync(modelId, model, entry);
        }
    }
}

internal static class FoundryLocalRequestOwnership
{
    public static void TransferToRequest<TItem>(
        TItem item,
        Action<TItem> addToRequest)
        where TItem : IDisposable
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(addToRequest);

        // Foundry Local Request.AddItem takes ownership by default. The request,
        // not this helper, is responsible for disposing the transferred item.
        addToRequest(item);
    }
}
