using System.Diagnostics;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels.Workspaces;

/// <summary>
/// Keeps durable-store preparation away from the WPF composition shell and, for production,
/// away from the Dispatcher until the first window frame has rendered.
/// </summary>
internal sealed class DataStoreStartupController
{
    private readonly DataStoreStartupService service;
    private readonly bool deferred;
    private readonly DataStoreStartupCallbacks callbacks;
    private readonly object initializationGate = new();
    private Task? initializationTask;

    internal DataStoreStartupController(
        DataStoreStartupService service,
        bool deferred,
        DataStoreStartupCallbacks callbacks)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.deferred = deferred;
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void InitializeSynchronously()
    {
        try
        {
            callbacks.ApplySuccess(service.Prepare());
        }
        catch (Exception exception)
        {
            callbacks.ApplyFailure(exception);
        }
        finally
        {
            callbacks.StartPostInitialization();
        }
    }

    internal Task InitializeAfterFirstRenderAsync()
    {
        if (!deferred)
        {
            return Task.CompletedTask;
        }

        lock (initializationGate)
        {
            return initializationTask ??= InitializeDeferredCoreAsync();
        }
    }

    private async Task InitializeDeferredCoreAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(service.Prepare).ConfigureAwait(true);
            callbacks.ApplySuccess(result);
            callbacks.PublishDiagnostic(
                $"startup data store ready elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        }
        catch (Exception exception)
        {
            callbacks.ApplyFailure(exception);
        }
        finally
        {
            callbacks.StartPostInitialization();
        }
    }
}

internal sealed class DataStoreStartupService
{
    private const int RecentRunQueryLimit = 500;

    private readonly ExperimentCatalog catalog;
    private readonly RealtimeRawPersistenceService rawPersistence;
    private readonly IDataRootStorageService storage;
    private readonly Guid sessionId;
    private readonly DateTimeOffset sessionStartedAt;

    internal DataStoreStartupService(
        ExperimentCatalog catalog,
        RealtimeRawPersistenceService rawPersistence,
        IDataRootStorageService storage,
        Guid sessionId,
        DateTimeOffset sessionStartedAt)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.rawPersistence = rawPersistence ?? throw new ArgumentNullException(nameof(rawPersistence));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.sessionId = sessionId;
        this.sessionStartedAt = sessionStartedAt;
    }

    internal DataStoreStartupResult Prepare()
    {
        catalog.Initialize();
        var rawRecovery = rawPersistence.ReconcileIncompleteCatalogShards();
        var recoveredRunCount = catalog.RecoverInterruptedRuns(DateTimeOffset.UtcNow);
        catalog.UpsertSession(
            sessionId,
            $"EIT {sessionStartedAt:yyyy-MM-dd HH:mm:ss}",
            sessionStartedAt);
        var interruptedRuns = recoveredRunCount == 0
            ? Array.Empty<ExperimentRunRecord>()
            : catalog.ListRuns()
                .Where(run => string.Equals(
                    run.Status,
                    ExperimentCatalog.InterruptedStatus,
                    StringComparison.Ordinal))
                .ToArray();
        return new DataStoreStartupResult(
            rawRecovery,
            recoveredRunCount,
            interruptedRuns,
            catalog.ListRunSummaries(RecentRunQueryLimit),
            storage.Inspect(includeManagedSize: false));
    }
}

internal sealed record DataStoreStartupCallbacks(
    Action<DataStoreStartupResult> ApplySuccess,
    Action<Exception> ApplyFailure,
    Action StartPostInitialization,
    Action<string> PublishDiagnostic);

internal sealed record DataStoreStartupResult(
    RealtimeRawRecoveryResult RawRecovery,
    int RecoveredRunCount,
    IReadOnlyList<ExperimentRunRecord> InterruptedRuns,
    IReadOnlyList<ExperimentRunCatalogSummary> CanonicalRuns,
    DataRootStorageSnapshot Storage);
