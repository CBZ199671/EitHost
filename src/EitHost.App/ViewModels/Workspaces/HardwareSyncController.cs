using EitHost.Core.Acquisition;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Sync;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class HardwareSyncController : IEitSetSyncController
{
    private static readonly TimeSpan DefaultOperatorStopWaitTimeout = TimeSpan.FromMilliseconds(2800);

    private readonly object lifecycleGate = new();
    private readonly PairingSummaryItem pairing;
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly Usb2070Device usbDevice;
    private readonly string ddsPortName;
    private readonly DdsExcitationSettings excitationSettings;
    private readonly Usb2070AcquisitionSettings acquisitionSettings;
    private readonly Usb2070AcquisitionMetadata fallbackAcquisitionMetadata;
    private Hdf5ExcitationMetadata excitationMetadata;
    private readonly int readValueCount;
    private readonly long autoFlushByteThreshold;
    private readonly long maxBufferedByteCount;
    private readonly TimeSpan readLoopIdleDelay;
    private readonly long compressionStartByteThreshold;
    private readonly TimeSpan compressionYieldDelay;
    private readonly Func<bool> isMemoryPressureHigh;
    private readonly Func<ActiveBufferedAcquisitionSession<PairingSummaryItem>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush;
    private readonly Action<ActiveBufferedAcquisitionSession<PairingSummaryItem>, long, long>? valuesDropped;
    private readonly TimeSpan operatorStopWaitTimeout;
    private readonly TimeSpan? readerStopWaitTimeout;
    private ActiveBufferedAcquisitionSession<PairingSummaryItem>? startedSession;
    private Task? stoppingSessionTask;

    internal HardwareSyncController(
        PairingSummaryItem pairing,
        IUsb2070NativeApi usb2070NativeApi,
        Usb2070Device usbDevice,
        string ddsPortName,
        DdsExcitationSettings excitationSettings,
        Usb2070AcquisitionSettings acquisitionSettings,
        Usb2070AcquisitionMetadata fallbackAcquisitionMetadata,
        Hdf5ExcitationMetadata excitationMetadata,
        int readValueCount,
        long autoFlushByteThreshold,
        long maxBufferedByteCount,
        TimeSpan readLoopIdleDelay,
        long compressionStartByteThreshold,
        TimeSpan compressionYieldDelay,
        Func<bool> isMemoryPressureHigh,
        Func<ActiveBufferedAcquisitionSession<PairingSummaryItem>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush,
        Action<ActiveBufferedAcquisitionSession<PairingSummaryItem>, long, long>? valuesDropped,
        TimeSpan? operatorStopWaitTimeout = null,
        TimeSpan? readerStopWaitTimeout = null)
    {
        this.pairing = pairing;
        this.usb2070NativeApi = usb2070NativeApi;
        this.usbDevice = usbDevice;
        this.ddsPortName = ddsPortName;
        this.excitationSettings = excitationSettings;
        this.acquisitionSettings = acquisitionSettings;
        this.fallbackAcquisitionMetadata = fallbackAcquisitionMetadata;
        this.excitationMetadata = excitationMetadata;
        this.readValueCount = readValueCount;
        this.autoFlushByteThreshold = autoFlushByteThreshold;
        this.maxBufferedByteCount = maxBufferedByteCount;
        this.readLoopIdleDelay = readLoopIdleDelay;
        this.compressionStartByteThreshold = compressionStartByteThreshold;
        this.compressionYieldDelay = compressionYieldDelay;
        this.isMemoryPressureHigh = isMemoryPressureHigh;
        this.autoFlush = autoFlush;
        this.valuesDropped = valuesDropped;
        if (operatorStopWaitTimeout is { } configuredOperatorStopWaitTimeout &&
            configuredOperatorStopWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operatorStopWaitTimeout));
        }

        if (readerStopWaitTimeout is { } configuredReaderStopWaitTimeout &&
            configuredReaderStopWaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readerStopWaitTimeout));
        }

        this.operatorStopWaitTimeout = operatorStopWaitTimeout ?? DefaultOperatorStopWaitTimeout;
        this.readerStopWaitTimeout = readerStopWaitTimeout;
    }

    public string Label => pairing.Title;

    public DdsCommandResult? StartExcitationResult { get; private set; }

    public Task StartAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
            if (startedSession is not null)
            {
                throw new InvalidOperationException($"{Label} 已经在采集中。");
            }

            stoppingSessionTask = null;
            Usb2070Session? session = null;
            try
            {
                var service = new Usb2070Service(usb2070NativeApi);
                session = service.Open(usbDevice);
                session.StartAcquisition(acquisitionSettings);
                var metadata = session.LastAcquisitionMetadata ?? fallbackAcquisitionMetadata;
                startedSession = new ActiveBufferedAcquisitionSession<PairingSummaryItem>(
                    pairing,
                    session,
                    metadata,
                    excitationMetadata,
                    readValueCount,
                    autoFlushByteThreshold,
                    maxBufferedByteCount,
                    readLoopIdleDelay,
                    compressionStartByteThreshold,
                    compressionYieldDelay,
                    isMemoryPressureHigh,
                    autoFlush,
                    valuesDropped,
                    readerStopWaitTimeout);
                session = null;
                return Task.CompletedTask;
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    public async Task StartExcitationAsync(CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(ddsPortName);
        var client = new DdsProtocolClient(transport);
        StartExcitationResult = await client.StartExcitationAsync(excitationSettings, cancellationToken).ConfigureAwait(false);
        excitationMetadata = excitationMetadata with
        {
            Execution = StartExcitationResult.ExecutionReceipt ?? throw new DdsProtocolException(
                $"{Label} DDS firmware v2 ACK did not include an execution receipt.")
        };
    }

    public async Task StopAcquisitionAsync(CancellationToken cancellationToken = default)
    {
        Task stopTask;
        lock (lifecycleGate)
        {
            if (startedSession is null)
            {
                return;
            }

            if (stoppingSessionTask is null)
            {
                stoppingSessionTask = StopAndReleaseSessionAsync(startedSession);
                _ = ObserveStopCompletionAsync(stoppingSessionTask);
            }

            stopTask = stoppingSessionTask;
        }

        try
        {
            await stopTask.WaitAsync(operatorStopWaitTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (!stopTask.IsCompleted)
        {
            throw new TimeoutException(
                $"{Label} 采集停止清理超时；读取或自动落盘资源仍在后台安全清理。",
                ex);
        }
    }

    private static async Task ObserveStopCompletionAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller receives an immediate failure when possible; a later
            // terminal fault is observed here after ownership-safe background drain.
        }
    }

    private async Task StopAndReleaseSessionAsync(
        ActiveBufferedAcquisitionSession<PairingSummaryItem> session)
    {
        try
        {
            await session.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            lock (lifecycleGate)
            {
                if (ReferenceEquals(startedSession, session))
                {
                    startedSession = null;
                }
            }
        }
    }

    public async Task StopExcitationAsync(CancellationToken cancellationToken = default)
    {
        using var transport = new DdsSerialPortTransport(ddsPortName);
        var client = new DdsProtocolClient(transport);
        await client.StopExcitationAsync(cancellationToken).ConfigureAwait(false);
    }

    internal ActiveBufferedAcquisitionSession<PairingSummaryItem> TakeStartedSession()
    {
        lock (lifecycleGate)
        {
            if (startedSession is null || stoppingSessionTask is not null)
            {
                throw new InvalidOperationException($"{Label} 同步采集会话未启动。");
            }

            var session = startedSession;
            startedSession = null;
            return session;
        }
    }
}
