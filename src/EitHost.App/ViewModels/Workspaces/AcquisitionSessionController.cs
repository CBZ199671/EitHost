using System.Collections.Concurrent;
using EitHost.Core.Acquisition;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class AcquisitionSessionController : IDisposable
{
    private readonly IUsb2070NativeApi nativeApi;
    private readonly Func<bool> memoryPressureProbe;
    private readonly Func<ActiveBufferedAcquisitionSession<PairingSummaryItem>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush;
    private readonly Action<ActiveBufferedAcquisitionSession<PairingSummaryItem>, long, long>? valuesDropped;
    private readonly Action<string> log;
    private readonly ConcurrentDictionary<string, ActiveBufferedAcquisitionSession<PairingSummaryItem>> sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ActiveBufferedAcquisitionSession<PairingSummaryItem>> stoppingSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CapturedRawBlock> captures =
        new(StringComparer.OrdinalIgnoreCase);
    private int disposeStarted;

    internal AcquisitionSessionController(
        IUsb2070NativeApi nativeApi,
        Func<bool> memoryPressureProbe,
        Func<ActiveBufferedAcquisitionSession<PairingSummaryItem>, ushort[], DateTimeOffset, string, BufferedAcquisitionAutoFlushResult> autoFlush,
        Action<ActiveBufferedAcquisitionSession<PairingSummaryItem>, long, long>? valuesDropped,
        Action<string> log)
    {
        this.nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        this.memoryPressureProbe = memoryPressureProbe ?? throw new ArgumentNullException(nameof(memoryPressureProbe));
        this.autoFlush = autoFlush ?? throw new ArgumentNullException(nameof(autoFlush));
        this.valuesDropped = valuesDropped;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    internal int ActiveCount => sessions.Count + stoppingSessions.Count;

    internal int CaptureCount => captures.Count;

    internal IReadOnlyCollection<string> ActiveSetLabels => sessions.Keys
        .Concat(stoppingSessions.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal bool IsActive(string setLabel) => sessions.ContainsKey(setLabel) || stoppingSessions.ContainsKey(setLabel);

    internal bool CanStop(string setLabel) =>
        sessions.ContainsKey(setLabel) && !stoppingSessions.ContainsKey(setLabel);

    internal bool HasCapture(string setLabel) => captures.ContainsKey(setLabel);

    internal bool TryGetCapture(string setLabel, out CapturedRawBlock capture) =>
        captures.TryGetValue(setLabel, out capture!);

    internal IReadOnlyList<CapturedRawBlock> GetCaptures() =>
        captures.Values
            .OrderBy(capture => capture.Pairing.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal HardwareSyncController CreateSyncController(
        PairingSummaryItem pairing,
        Usb2070Device usbDevice,
        string ddsPortName,
        DdsExcitationSettings excitationSettings,
        Usb2070AcquisitionSettings acquisitionSettings,
        Usb2070AcquisitionMetadata fallbackAcquisitionMetadata,
        Hdf5ExcitationMetadata excitationMetadata,
        AcquisitionBufferPolicy bufferPolicy)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(usbDevice);
        ArgumentException.ThrowIfNullOrWhiteSpace(ddsPortName);
        ArgumentNullException.ThrowIfNull(excitationSettings);
        ArgumentNullException.ThrowIfNull(acquisitionSettings);
        ArgumentNullException.ThrowIfNull(fallbackAcquisitionMetadata);
        ArgumentNullException.ThrowIfNull(excitationMetadata);
        ArgumentNullException.ThrowIfNull(bufferPolicy);
        return new HardwareSyncController(
            pairing,
            nativeApi,
            usbDevice,
            ddsPortName,
            excitationSettings,
            acquisitionSettings,
            fallbackAcquisitionMetadata,
            excitationMetadata,
            bufferPolicy.ReadValueCount,
            bufferPolicy.AutoFlushByteThreshold,
            bufferPolicy.MaxBufferedByteCount,
            bufferPolicy.ReadLoopIdleDelay,
            bufferPolicy.CompressionStartByteThreshold,
            bufferPolicy.CompressionYieldDelay,
            memoryPressureProbe,
            autoFlush,
            valuesDropped);
    }

    internal void RecordCapture(CapturedRawBlock capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        captures[capture.Pairing.Title] = capture;
    }

    internal void Start(
        PairingSummaryItem pairing,
        Usb2070Device device,
        Usb2070AcquisitionSettings settings,
        Usb2070AcquisitionMetadata fallbackMetadata,
        Hdf5ExcitationMetadata excitation,
        AcquisitionBufferPolicy bufferPolicy)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsActive(pairing.Title))
        {
            throw new InvalidOperationException($"{pairing.Title} 已经在采集中。");
        }

        Usb2070Session? session = null;
        try
        {
            session = new Usb2070Service(nativeApi).Open(device);
            session.StartAcquisition(settings);
            var metadata = session.LastAcquisitionMetadata ?? fallbackMetadata;
            var activeSession = new ActiveBufferedAcquisitionSession<PairingSummaryItem>(
                pairing,
                session,
                metadata,
                excitation,
                bufferPolicy.ReadValueCount,
                bufferPolicy.AutoFlushByteThreshold,
                bufferPolicy.MaxBufferedByteCount,
                bufferPolicy.ReadLoopIdleDelay,
                bufferPolicy.CompressionStartByteThreshold,
                bufferPolicy.CompressionYieldDelay,
                memoryPressureProbe,
                autoFlush,
                valuesDropped);
            session = null;
            if (!sessions.TryAdd(pairing.Title, activeSession))
            {
                activeSession.Dispose();
                throw new InvalidOperationException($"{pairing.Title} 已经在采集中。");
            }

            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD buffer start {settings.SampleRateHz}Hz {settings.Range}");
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    internal void AdoptStartedSession(
        string setLabel,
        ActiveBufferedAcquisitionSession<PairingSummaryItem> session)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentNullException.ThrowIfNull(session);
        if (IsActive(setLabel))
        {
            throw new InvalidOperationException($"{setLabel} 已经在采集中。");
        }

        if (!sessions.TryAdd(setLabel, session))
        {
            throw new InvalidOperationException($"{setLabel} 已经在采集中。");
        }
    }

    internal async Task<CapturedRawBlock> CaptureAsync(
        PairingSummaryItem pairing,
        Usb2070Device device,
        Usb2070AcquisitionSettings settings,
        Usb2070AcquisitionMetadata fallbackMetadata,
        Hdf5ExcitationMetadata excitation,
        int readSampleRows)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (sessions.TryGetValue(pairing.Title, out var activeSession))
        {
            await activeSession.WaitForBufferedDataAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
            if (activeSession.BufferedValueCount <= 0)
            {
                throw new InvalidOperationException($"{pairing.Title} 当前内存缓存为空，数据可能已自动保存为 HDF5。");
            }

            return CaptureBuffered(activeSession, "AD memory snapshot");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readSampleRows);
        Usb2070Session? session = null;
        try
        {
            session = new Usb2070Service(nativeApi).Open(device);
            session.StartAcquisition(settings);
            var metadata = session.LastAcquisitionMetadata ?? fallbackMetadata;
            var rawValueCount = checked((uint)(readSampleRows * Usb2070Constants.RequiredMeasurementChannelCount));
            var buffer = new ushort[rawValueCount];
            var readCount = session.Read(buffer, rawValueCount);
            var capture = CreateCapturedRawBlock(
                pairing,
                DateTimeOffset.Now,
                buffer.Take((int)readCount).ToArray(),
                excitation,
                metadata);
            captures[pairing.Title] = capture;
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD one-shot read {readCount} values / {capture.AdcCounts.GetLength(0)} rows");
            session.StopAcquisition();
            return capture;
        }
        finally
        {
            session?.Dispose();
        }
    }

    internal async Task<AcquisitionStopOutcome> StopAsync(PairingSummaryItem pairing, string? logMessage)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!sessions.TryGetValue(pairing.Title, out var activeSession)
            || !stoppingSessions.TryAdd(pairing.Title, activeSession))
        {
            return new AcquisitionStopOutcome(false, null);
        }

        if (!sessions.TryRemove(pairing.Title, out var removedSession))
        {
            stoppingSessions.TryRemove(pairing.Title, out _);
            return new AcquisitionStopOutcome(false, null);
        }

        activeSession = removedSession;

        try
        {
            await activeSession.StopAsync().ConfigureAwait(true);
            string summary;
            if (activeSession.BufferedValueCount > 0)
            {
                var capture = CaptureBuffered(activeSession, logMessage ?? "AD buffer stop");
                summary = $"{pairing.Title} 内存采集 {capture.AdcCounts.GetLength(0)} 行 x {capture.AdcCounts.GetLength(1)} 通道";
            }
            else if (activeSession.AutoFlushResults.Count > 0)
            {
                var savedRows = activeSession.AutoFlushResults.Sum(result => result.RowCount);
                summary = $"{pairing.Title} 已自动保存 {savedRows} 行 x {Usb2070Constants.RequiredMeasurementChannelCount} 通道，内存缓存已清空。";
                log($"{DateTime.Now:HH:mm:ss} {logMessage ?? $"{pairing.Title} AD buffer stop"} auto-saved {activeSession.AutoFlushResults.Count} files / {savedRows} rows");
            }
            else
            {
                summary = $"{pairing.Title} 未采集到有效数据。";
                log($"{DateTime.Now:HH:mm:ss} {logMessage ?? $"{pairing.Title} AD buffer stop"} no buffered data");
            }

            LogStopWarnings(pairing, activeSession);
            return new AcquisitionStopOutcome(true, summary);
        }
        finally
        {
            activeSession.Dispose();
            stoppingSessions.TryRemove(pairing.Title, out _);
        }
    }

    internal Task<BufferedAcquisitionAutoFlushResult> WaitForFirstAutoFlushAsync(
        string setLabel,
        TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        if (!sessions.TryGetValue(setLabel, out var activeSession)
            && !stoppingSessions.TryGetValue(setLabel, out activeSession))
        {
            throw new InvalidOperationException($"{setLabel} 当前没有正在采集或停止中的会话。");
        }

        return activeSession.WaitForFirstAutoFlushAsync(timeout);
    }

    internal bool TryGetRecentPreview(
        string? preferredSetLabel,
        int maxValueCount,
        out BufferedAcquisitionPreviewData preview)
    {
        preview = default!;
        KeyValuePair<string, ActiveBufferedAcquisitionSession<PairingSummaryItem>> selected;
        if (!string.IsNullOrWhiteSpace(preferredSetLabel)
            && sessions.TryGetValue(preferredSetLabel, out var preferred))
        {
            selected = new KeyValuePair<string, ActiveBufferedAcquisitionSession<PairingSummaryItem>>(
                preferredSetLabel,
                preferred);
        }
        else
        {
            selected = sessions.FirstOrDefault();
            if (selected.Value is null)
            {
                return false;
            }
        }

        preview = new BufferedAcquisitionPreviewData(
            selected.Key,
            selected.Value.SnapshotRecentValues(maxValueCount),
            selected.Value.Excitation,
            selected.Value.Acquisition);
        return true;
    }

    internal void UpdateExcitationMetadata(
        string setLabel,
        DdsExecutionReceipt execution,
        DdsScanStatus? status)
    {
        if (!sessions.TryGetValue(setLabel, out var activeSession))
        {
            return;
        }

        activeSession.UpdateExcitationMetadata(
            activeSession.Excitation with
            {
                Execution = execution,
                ScanStatus = status
            });
    }

    internal long GetBufferedValueCount(string setLabel)
    {
        if (sessions.TryGetValue(setLabel, out var session)
            || stoppingSessions.TryGetValue(setLabel, out session))
        {
            return session.BufferedValueCount;
        }

        return 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        var sessionsToDispose = sessions.Values
            .Concat(stoppingSessions.Values)
            .DistinctBy(session => session, ReferenceEqualityComparer.Instance)
            .ToArray();
        sessions.Clear();
        stoppingSessions.Clear();
        foreach (var session in sessionsToDispose)
        {
            session.Dispose();
        }
    }

    private bool IsDisposed => Volatile.Read(ref disposeStarted) != 0;

    private CapturedRawBlock CaptureBuffered(
        ActiveBufferedAcquisitionSession<PairingSummaryItem> activeSession,
        string actionName)
    {
        var values = activeSession.SnapshotValues();
        var capture = CreateCapturedRawBlock(
            activeSession.Pairing,
            activeSession.StartedAt,
            values,
            activeSession.Excitation,
            activeSession.Acquisition);
        captures[activeSession.Pairing.Title] = capture;
        log($"{DateTime.Now:HH:mm:ss} {activeSession.Pairing.Title} {actionName} {values.Length} values / {capture.AdcCounts.GetLength(0)} rows");
        return capture;
    }

    private void LogStopWarnings(
        PairingSummaryItem pairing,
        ActiveBufferedAcquisitionSession<PairingSummaryItem> activeSession)
    {
        if (activeSession.ReaderFailure is not null)
        {
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD reader warning {activeSession.ReaderFailure.Message}");
        }

        if (activeSession.StopFailure is not null)
        {
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} AD stop warning {activeSession.StopFailure.Message}");
        }

        if (activeSession.DroppedValueCount > 0)
        {
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} memory ring dropped {activeSession.DroppedValueCount} oldest values");
        }

        foreach (var failure in activeSession.AutoFlushFailures)
        {
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} auto-save warning {failure.Message}");
        }

        foreach (var failure in activeSession.CompressionFailures)
        {
            log($"{DateTime.Now:HH:mm:ss} {pairing.Title} memory compression warning {failure.Message}");
        }
    }

    internal static CapturedRawBlock CreateCapturedRawBlock(
        PairingSummaryItem pairing,
        DateTimeOffset capturedAt,
        IReadOnlyList<ushort> values,
        Hdf5ExcitationMetadata excitation,
        Usb2070AcquisitionMetadata acquisition)
    {
        ArgumentNullException.ThrowIfNull(values);
        var usableValueCount = values.Count - (values.Count % Usb2070Constants.RequiredMeasurementChannelCount);
        if (usableValueCount <= 0)
        {
            throw new InvalidOperationException($"{pairing.Title} 尚未采集到完整的 16 通道数据行。");
        }

        var matrix = RawAdcMatrix.FromInterleaved(values, usableValueCount);
        return new CapturedRawBlock(pairing, capturedAt, matrix, excitation, acquisition);
    }
}

internal sealed record AcquisitionBufferPolicy(
    int ReadValueCount,
    long AutoFlushByteThreshold,
    long MaxBufferedByteCount,
    TimeSpan ReadLoopIdleDelay,
    long CompressionStartByteThreshold,
    TimeSpan CompressionYieldDelay);

internal sealed record AcquisitionStopOutcome(bool WasActive, string? CaptureSummary);

internal sealed record BufferedAcquisitionPreviewData(
    string SetLabel,
    ushort[] Values,
    Hdf5ExcitationMetadata Excitation,
    Usb2070AcquisitionMetadata Acquisition);

internal sealed record CapturedRawBlock(
    PairingSummaryItem Pairing,
    DateTimeOffset CapturedAt,
    ushort[,] AdcCounts,
    Hdf5ExcitationMetadata Excitation,
    Usb2070AcquisitionMetadata Acquisition);
