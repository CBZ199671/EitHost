using System.Diagnostics;

namespace EitHost.Core.Sync;

public sealed class SyncStartCoordinator
{
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly Action<string> cleanupFailureReporter;

    public SyncStartCoordinator(
        Func<DateTimeOffset>? getUtcNow = null,
        Action<string>? reportCleanupFailure = null)
    {
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        cleanupFailureReporter = reportCleanupFailure ?? (message => Trace.TraceWarning(message));
    }

    public async Task<SyncStartResult> StartAsync(
        IEnumerable<IEitSetSyncController> controllers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controllers);

        var controllerList = controllers.ToArray();
        if (controllerList.Length < 2)
        {
            throw new ArgumentException("At least two EIT sets are required for synchronized start.", nameof(controllers));
        }

        var records = controllerList.ToDictionary(
            controller => controller.Label,
            controller => new MutableRecord(controller.Label),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var controller in controllerList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records[controller.Label].AcquisitionStartRequestedAt = getUtcNow();
                await controller.StartAcquisitionAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var controller in controllerList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records[controller.Label].ExcitationStartRequestedAt = getUtcNow();
                await controller.StartExcitationAsync(cancellationToken).ConfigureAwait(false);
            }

            return new SyncStartResult(records.Values.Select(record => record.ToRecord()).ToArray());
        }
        catch (Exception ex)
        {
            await StopAllBestEffortAsync(controllerList).ConfigureAwait(false);
            throw new SyncStartException(
                "Synchronized start failed. Stop was attempted for all selected EIT sets.",
                new SyncStartResult(records.Values.Select(record => record.ToRecord()).ToArray()),
                ex);
        }
    }

    private async Task StopAllBestEffortAsync(IEnumerable<IEitSetSyncController> controllers)
    {
        foreach (var controller in controllers)
        {
            try
            {
                await controller.StopExcitationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"{controller.Label} synchronized cleanup StopExcitation failed: {ex}");
            }

            try
            {
                await controller.StopAcquisitionAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportCleanupFailure($"{controller.Label} synchronized cleanup StopAcquisition failed: {ex}");
            }
        }
    }

    private void ReportCleanupFailure(string message)
    {
        try
        {
            cleanupFailureReporter(message);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Synchronized cleanup diagnostic reporter failed: {ex}; original={message}");
        }
    }

    private sealed class MutableRecord
    {
        public MutableRecord(string label)
        {
            Label = label;
        }

        public string Label { get; }

        public DateTimeOffset? AcquisitionStartRequestedAt { get; set; }

        public DateTimeOffset? ExcitationStartRequestedAt { get; set; }

        public SyncSetStartRecord ToRecord()
        {
            return new SyncSetStartRecord(Label, AcquisitionStartRequestedAt, ExcitationStartRequestedAt);
        }
    }
}
