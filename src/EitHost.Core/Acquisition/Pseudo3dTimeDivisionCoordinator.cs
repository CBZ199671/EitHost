namespace EitHost.Core.Acquisition;

public interface IPseudo3dTimeDivisionDevice : IAsyncDisposable
{
    string SetLabel { get; }
    string UsbIdentity { get; }
    string DdsIdentity { get; }
    Task PrepareAsync(CancellationToken cancellationToken);
    // Completes only after StopDAC/Idle confirmation and the output-off guard.
    Task QuiesceAsync(CancellationToken cancellationToken);
    Task<Pseudo3dSlotCapture> CaptureAsync(Guid sessionId, long round, int slot, CancellationToken cancellationToken);
}

public sealed class Pseudo3dTimeDivisionCoordinator
{
    private const int MaximumConsecutiveTimingRetries = 2;
    private readonly IPseudo3dTimeDivisionDevice[] devices;
    private int started;

    public Pseudo3dTimeDivisionCoordinator(Guid sessionId, IReadOnlyList<IPseudo3dTimeDivisionDevice> devices)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A time-division session needs an identity.", nameof(sessionId));
        ArgumentNullException.ThrowIfNull(devices);
        if (devices.Count != 2 || devices.Any(device => device is null) ||
            devices.Select(device => device.SetLabel).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            devices.Select(device => device.UsbIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2 ||
            devices.Select(device => device.DdsIdentity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
            throw new ArgumentException("伪三维需要两套不同的设备，USB2070 与 DDS 串口均不能重复。", nameof(devices));
        SessionId = sessionId;
        this.devices = devices.ToArray();
    }

    public Guid SessionId { get; }

    public async Task RunAsync(
        Func<Pseudo3dSlotCapture, Pseudo3dSlotCapture, CancellationToken, Task> publishRound,
        CancellationToken cancellationToken,
        int? maximumRounds = null,
        Action<string>? reportTimingRetry = null)
    {
        ArgumentNullException.ThrowIfNull(publishRound);
        if (maximumRounds is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRounds));
        if (Interlocked.Exchange(ref started, 1) != 0) throw new InvalidOperationException("A time-division session cannot be restarted.");
        Exception? failure = null;
        try
        {
            // Prepare never enables an electrode path. Both devices must be known
            // quiescent before either is allowed to issue a finite start command.
            foreach (var device in devices) await device.PrepareAsync(cancellationToken).ConfigureAwait(false);
            foreach (var device in devices) await device.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            var consecutiveTimingRetries = 0;
            for (long round = 1; !maximumRounds.HasValue || round <= maximumRounds.Value; round++)
            {
                var roundStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                cancellationToken.ThrowIfCancellationRequested();
                var captures = new Pseudo3dSlotCapture[2];
                try
                {
                    for (var slot = 0; slot < 2; slot++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await devices[1 - slot].QuiesceAsync(cancellationToken).ConfigureAwait(false);
                        captures[slot] = await devices[slot].CaptureAsync(SessionId, round, slot, cancellationToken).ConfigureAwait(false);
                        await devices[slot].QuiesceAsync(cancellationToken).ConfigureAwait(false);
                        var completed = captures[slot].CompletedStatus;
                        var stamp = captures[slot].Block.TimeDivision;
                        if (captures[slot].SetLabel != devices[slot].SetLabel || stamp is null ||
                            stamp.SessionId != SessionId || stamp.Round != round || stamp.Slot != slot ||
                            stamp.Profile != Pseudo3dAcquisitionProfile.Version || stamp.FrequencyTuningWord != Pseudo3dAcquisitionProfile.FrequencyTuningWord)
                            throw new InvalidDataException("分时采集标记不匹配，禁止发布或继续调度。");
                        if (completed.State != Hardware.Dds.DdsScanState.Completed || completed.Running ||
                            completed.CurrentStep != 15 || completed.TargetCycles != Pseudo3dAcquisitionProfile.ScanFrames ||
                            completed.CompletedCycles != completed.TargetCycles)
                            throw new InvalidDataException("分时扫描没有确认精确完成，禁止切换至另一套设备。");
                    }
                }
                catch (Pseudo3dTimingUncertaintyException exception) when (
                    !cancellationToken.IsCancellationRequested && consecutiveTimingRetries < MaximumConsecutiveTimingRetries &&
                    (!maximumRounds.HasValue || round < maximumRounds.Value))
                {
                    // Never reuse the other layer from a rejected round. Cleanup
                    // errors and all non-timing failures still terminate the group.
                    foreach (var device in devices) await device.QuiesceAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    consecutiveTimingRetries++;
                    reportTimingRetry?.Invoke($"分时第 {round} 轮作废，两套输出已关闭；" +
                        $"自动重试 {consecutiveTimingRetries}/{MaximumConsecutiveTimingRetries}。{exception.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                // Potentially slow storage and inverse processing occur after both
                // slots are off, so they cannot lengthen the inter-layer skew.
                await publishRound(captures[0], captures[1], cancellationToken).ConfigureAwait(false);
                consecutiveTimingRetries = 0;
                var remaining = TimeSpan.FromSeconds(1 / Pseudo3dAcquisitionProfile.TargetVolumesPerSecond) -
                    System.Diagnostics.Stopwatch.GetElapsedTime(roundStarted);
                if (remaining > TimeSpan.Zero && (!maximumRounds.HasValue || round < maximumRounds.Value))
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            foreach (var device in devices)
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await device.QuiesceAsync(cleanupTimeout.Token).ConfigureAwait(false); }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            if (cleanupFailures.Count > 0)
            {
                if (failure is not null) cleanupFailures.Insert(0, failure);
                failure = new AggregateException("分时组已停止调度，部分停止确认失败。", cleanupFailures);
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
