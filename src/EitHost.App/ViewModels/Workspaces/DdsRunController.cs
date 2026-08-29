using System.Diagnostics;
using EitHost.Core.Hardware.Dds;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class DdsRunController : IDisposable
{
    private readonly Action<Action> postToUi;
    private readonly Action<PairingSummaryItem, DdsScanStatus> applyStatus;
    private readonly Func<PairingSummaryItem, DdsScanStatus, Task> completeScan;
    private readonly Action<PairingSummaryItem, Exception> reportMonitorFailure;
    private readonly HashSet<string> activeSetLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DdsExecutionReceipt> executionReceipts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object monitorGate = new();
    private readonly Dictionary<string, DdsMonitorCancellation> monitorCancellations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> monitorTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    internal DdsRunController(
        Action<Action> postToUi,
        Action<PairingSummaryItem, DdsScanStatus> applyStatus,
        Func<PairingSummaryItem, DdsScanStatus, Task> completeScan,
        Action<PairingSummaryItem, Exception> reportMonitorFailure)
    {
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        this.applyStatus = applyStatus ?? throw new ArgumentNullException(nameof(applyStatus));
        this.completeScan = completeScan ?? throw new ArgumentNullException(nameof(completeScan));
        this.reportMonitorFailure = reportMonitorFailure ?? throw new ArgumentNullException(nameof(reportMonitorFailure));
    }

    internal IReadOnlyCollection<string> ActiveSetLabels => activeSetLabels.ToArray();

    internal bool IsActive(string setLabel) => activeSetLabels.Contains(setLabel);

    internal void MarkActive(string setLabel)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        activeSetLabels.Add(setLabel);
    }

    internal void MarkStarted(string setLabel, DdsExecutionReceipt execution)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        activeSetLabels.Add(setLabel);
        executionReceipts[setLabel] = execution;
    }

    internal void MarkStopped(string setLabel)
    {
        activeSetLabels.Remove(setLabel);
        executionReceipts.Remove(setLabel);
    }

    internal bool TryGetExecution(string setLabel, out DdsExecutionReceipt execution) =>
        executionReceipts.TryGetValue(setLabel, out execution!);

    internal void StartFiniteScanMonitor(PairingSummaryItem pairing, DdsExecutionReceipt execution)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancelMonitor(pairing.Title);
        var cancellation = new DdsMonitorCancellation();
        lock (monitorGate)
        {
            monitorCancellations[pairing.Title] = cancellation;
            try
            {
                monitorTasks[pairing.Title] = Task.Run(
                    () => MonitorFiniteScanAsync(pairing, execution, cancellation));
            }
            catch
            {
                monitorCancellations.Remove(pairing.Title);
                cancellation.Dispose();
                throw;
            }
        }
    }

    internal async Task CancelMonitorAsync(string setLabel)
    {
        DdsMonitorCancellation? cancellation;
        Task? monitorTask;
        lock (monitorGate)
        {
            monitorCancellations.Remove(setLabel, out cancellation);
            monitorTasks.Remove(setLabel, out monitorTask);
        }

        cancellation?.TryCancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // An explicit stop owns the serial port after monitor cancellation.
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DdsMonitorCancellation[] cancellations;
        lock (monitorGate)
        {
            cancellations = monitorCancellations.Values.ToArray();
            monitorCancellations.Clear();
            monitorTasks.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.TryCancel();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"DDS monitor cancellation failed during controller disposal: {ex}");
            }
        }

        activeSetLabels.Clear();
        executionReceipts.Clear();
    }

    private async Task MonitorFiniteScanAsync(
        PairingSummaryItem pairing,
        DdsExecutionReceipt execution,
        DdsMonitorCancellation cancellation)
    {
        try
        {
            var initialDelayMs = Math.Clamp(
                execution.EffectiveTimeUs * 16.0 * execution.ScanTimes * 0.8 / 1000.0,
                20.0,
                1000.0);
            await Task.Delay(TimeSpan.FromMilliseconds(initialDelayMs), cancellation.Token).ConfigureAwait(false);
            var portName = pairing.Pairing.DdsSerialCandidate.PortName ?? throw new InvalidOperationException(
                $"{pairing.Title} 没有可用 DDS 串口。");
            using var transport = new DdsSerialPortTransport(portName);
            var client = new DdsProtocolClient(transport);
            while (!cancellation.IsCancellationRequested)
            {
                var status = await client.GetScanStatusAsync(cancellation.Token).ConfigureAwait(false);
                if (status.TargetCycles != execution.ScanTimes)
                {
                    throw new DdsProtocolException(
                        $"{pairing.Title} 扫描状态目标圈数 {status.TargetCycles} 与启动 ACK {execution.ScanTimes} 不一致。");
                }

                if (status.State == DdsScanState.Completed)
                {
                    postToUi(() => _ = completeScan(pairing, status));
                    return;
                }

                if (status.State == DdsScanState.Idle)
                {
                    throw new DdsProtocolException($"{pairing.Title} 有限扫描在完成前意外进入空闲状态。");
                }

                postToUi(() => applyStatus(pairing, status));
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Explicit stop or application shutdown owns final state cleanup.
        }
        catch (Exception ex)
        {
            postToUi(() => reportMonitorFailure(pairing, ex));
        }
        finally
        {
            lock (monitorGate)
            {
                if (monitorCancellations.TryGetValue(pairing.Title, out var current)
                    && ReferenceEquals(current, cancellation))
                {
                    monitorCancellations.Remove(pairing.Title);
                    monitorTasks.Remove(pairing.Title);
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelMonitor(string setLabel)
    {
        DdsMonitorCancellation? cancellation = null;
        lock (monitorGate)
        {
            if (monitorCancellations.Remove(setLabel, out var current))
            {
                cancellation = current;
            }
        }

        cancellation?.TryCancel();
    }
}

internal sealed class DdsMonitorCancellation : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource source = new();
    private bool disposed;

    internal CancellationToken Token { get; }

    internal bool IsCancellationRequested => Token.IsCancellationRequested;

    internal DdsMonitorCancellation()
    {
        Token = source.Token;
    }

    internal bool TryCancel()
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            source.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            source.Dispose();
        }
    }
}
