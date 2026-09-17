using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Acquisition;

/// <summary>Owns a finite, ADC-before-DDS slot. Never joins samples across slots.</summary>
public sealed class Pseudo3dTimeDivisionDevice : IPseudo3dTimeDivisionDevice
{
    private readonly IUsb2070NativeApi nativeApi;
    private readonly Usb2070Device device;
    private readonly IDdsSerialTransport transport;
    private readonly DdsProtocolClient dds;
    private readonly TimeProvider timeProvider;
    private Usb2070Session? adc;
    private DdsFirmwareCapabilities? capabilities;
    private bool disposed;
    private bool quiescent;
    private long? outputOffSince;

    public Pseudo3dTimeDivisionDevice(
        IUsb2070NativeApi nativeApi, Usb2070Device device, string setLabel, string ddsPort,
        IDdsSerialTransport? transport = null, TimeProvider? timeProvider = null)
    {
        this.nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        this.device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(ddsPort);
        SetLabel = setLabel;
        DdsIdentity = ddsPort.Trim();
        this.transport = transport ?? new DdsSerialPortTransport(ddsPort);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        dds = new DdsProtocolClient(this.transport);
    }

    public string SetLabel { get; }
    public string UsbIdentity => $"USB2070:{device.DeviceNumber}";
    public string DdsIdentity { get; }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        capabilities = await dds.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (capabilities.FirmwareVersion < new Version(1, 4, 0) || !capabilities.SupportsScanStatus)
            throw new InvalidOperationException($"{SetLabel} 分时采集需要支持有限扫描状态的 DDS 固件 v1.4 或更新版本。");
        await QuiesceAsync(cancellationToken).ConfigureAwait(false);
        await dds.SetPgaAsync(1, cancellationToken).ConfigureAwait(false);
        adc ??= new Usb2070Service(nativeApi).Open(device);
    }

    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quiescent) return; // This owner is the only writer to its serial port.
        // Attempt both commands even if the first ACK fails. Never infer that a
        // lost reply means the other electrode set can safely be enabled.
        var errors = new List<Exception>();
        try
        {
            await dds.StopExcitationAsync(cancellationToken).ConfigureAwait(false);
            outputOffSince ??= Stopwatch.GetTimestamp();
        }
        catch (Exception exception) { errors.Add(exception); }
        try { await dds.StopDacAsync(1, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { errors.Add(exception); }
        try
        {
            var status = await dds.GetScanStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != DdsScanState.Idle || status.Running || status.TargetCycles != 0 || status.CompletedCycles != 0)
                throw new InvalidDataException($"{SetLabel} 未确认空闲，禁止启动下一套。");
        }
        catch (Exception exception) { errors.Add(exception); }
        if (errors.Count > 0) throw new AggregateException($"{SetLabel} 关闭输出确认失败。", errors);
        var remainingGuard = Pseudo3dAcquisitionProfile.HandoffGuard - Stopwatch.GetElapsedTime(outputOffSince!.Value);
        if (remainingGuard > TimeSpan.Zero) await Task.Delay(remainingGuard, cancellationToken).ConfigureAwait(false);
        quiescent = true;
    }

    public async Task<Pseudo3dSlotCapture> CaptureAsync(
        Guid sessionId, long round, int slot, CancellationToken cancellationToken)
    {
        if (adc is null || capabilities is null) throw new InvalidOperationException("Prepare the device before capturing a slot.");
        if (sessionId == Guid.Empty || round < 1 || slot is < 0 or > 1) throw new ArgumentException("Invalid time-division slot identity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Pseudo3dAcquisitionProfile.SlotTimeout);
        var token = timeout.Token;
        await QuiesceAsync(token).ConfigureAwait(false);
        var chunks = new List<ushort[]>();
        var rowsRead = 0;
        const int channels = 16;
        const int readRows = 1024;
        DdsExecutionReceipt execution;
        DdsScanStatus completed;
        Pseudo3dFiniteScanWindow startWindow;
        Task<Pseudo3dFiniteScanResult>? analysisTask = null;
        ushort[,] raw;
        DateTimeOffset captureStart;
        double uncertaintyMs;
        try
        {
            quiescent = false;
            outputOffSince = null;
            var configureDac = dds.SetDacAsync(Pseudo3dAcquisitionProfile.DacSettings, token);
            // ADC initialization can overlap a DAC command while the electrode
            // multiplexer is confirmed off. Measure the ADC call itself: waiting
            // for the DAC ACK afterward must not shift sample zero or inflate its bracket.
            var beforeInit = timeProvider.GetTimestamp();
            var beforeInitUtc = timeProvider.GetUtcNow();
            TimeSpan initElapsed;
            try
            {
                adc.StartAcquisition(Pseudo3dAcquisitionProfile.AcquisitionSettings);
                initElapsed = timeProvider.GetElapsedTime(beforeInit);
                captureStart = beforeInitUtc + initElapsed / 2;
            }
            finally { await configureDac.ConfigureAwait(false); }
            ReadChunk(); // Confirm a functioning ADC before enabling the electrode scan.
            var beforeStart = timeProvider.GetTimestamp();
            var start = await dds.StartExcitationAsync(Pseudo3dAcquisitionProfile.ExcitationSettings, token).ConfigureAwait(false);
            var afterStart = timeProvider.GetTimestamp();
            execution = start.ExecutionReceipt ?? throw new InvalidDataException("分时启动缺少实际驻留时间 ACK。");
            var settings = Pseudo3dAcquisitionProfile.CreateDemodulationSettings(execution);
            var startAcknowledgementMs = timeProvider.GetElapsedTime(beforeStart, afterStart).TotalMilliseconds;
            uncertaintyMs = initElapsed.TotalMilliseconds / 2 + startAcknowledgementMs;
            if (uncertaintyMs > Pseudo3dTimingUncertaintyException.MaximumMilliseconds)
                throw new Pseudo3dTimingUncertaintyException(SetLabel, initElapsed.TotalMilliseconds, startAcknowledgementMs);
            startWindow = new(readRows, checked((int)Math.Ceiling(
                timeProvider.GetElapsedTime(beforeInit, afterStart).TotalSeconds * Pseudo3dAcquisitionProfile.SampleRateHz)));
            int onset;
            do
            {
                ReadChunk();
                onset = Pseudo3dFiniteScanDemodulator.FindStartRow(Materialize(), settings, startWindow);
                if (onset < 0 && rowsRead > startWindow.MaximumStartRow + settings.NominalWindowSamples)
                    throw new InvalidDataException("分时槽未检测到激励启动，已停止整组。");
            } while (onset < 0);
            // Read all three scans plus one dwell of passive tail. Waveform
            // onset removes the USB initialization latency from the row budget.
            var captureRows = checked((int)Math.Ceiling(onset + settings.NominalFrameSamples *
                Pseudo3dAcquisitionProfile.ScanFrames + settings.NominalWindowSamples));
            while (rowsRead < captureRows) ReadChunk();
            raw = Materialize();
            analysisTask = Task.Run(() => Pseudo3dFiniteScanDemodulator.Demodulate(raw, settings, startWindow), CancellationToken.None);
            _ = analysisTask.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            do
            {
                token.ThrowIfCancellationRequested();
                completed = await dds.GetScanStatusAsync(token).ConfigureAwait(false);
                if (completed.State == DdsScanState.Idle)
                    throw new InvalidDataException($"{SetLabel} 有限扫描提前变为空闲，当前轮次作废。");
                if (completed.Running) await Task.Delay(5, token).ConfigureAwait(false);
            } while (completed.Running);
            if (completed.State != DdsScanState.Completed || completed.TargetCycles != Pseudo3dAcquisitionProfile.ScanFrames ||
                completed.CompletedCycles != Pseudo3dAcquisitionProfile.ScanFrames || completed.CurrentStep != 15)
                throw new InvalidDataException($"{SetLabel} 有限扫描完成计数不匹配。");
            outputOffSince = Stopwatch.GetTimestamp();
        }
        finally
        {
            // No parallel native ReadAD/Dispose: the finite firmware scan already
            // limits output if ReadAD itself stalls inside the vendor driver.
            try { adc.StopAcquisition(); }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await QuiesceAsync(cleanup.Token).ConfigureAwait(false);
            }
        }

        var analysis = await analysisTask!.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var block = analysis.Block;
        var midpoint = captureStart + TimeSpan.FromSeconds(
            (block.StartSampleIndex + block.PeakLocations[0] + block.EndSampleIndex) / 2.0 / Pseudo3dAcquisitionProfile.SampleRateHz);
        block = block with
        {
            BlockNumber = checked((int)round),
            TimeDivision = new Pseudo3dAcquisitionStamp(sessionId, round, slot, Pseudo3dAcquisitionProfile.Version,
                Pseudo3dAcquisitionProfile.FrequencyTuningWord, captureStart, midpoint, uncertaintyMs,
                block.IsHighQuality && block.AcceptedFrameCount == Pseudo3dAcquisitionProfile.FramesPerBlock)
        };
        return new Pseudo3dSlotCapture(SetLabel, raw, analysis.AnalysisStartRow, analysis.AnalysisEndRow,
            captureStart, capabilities, execution, completed, block) { FiniteScan = startWindow };

        ushort[,] Materialize()
        {
            var values = new ushort[rowsRead, channels];
            var offsetBytes = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, values, offsetBytes, chunk.Length * sizeof(ushort));
                offsetBytes += chunk.Length * sizeof(ushort);
            }
            return values;
        }

        void ReadChunk()
        {
            token.ThrowIfCancellationRequested();
            var chunk = new ushort[readRows * channels];
            if (adc.Read(chunk, (uint)chunk.Length) != chunk.Length || adc.LastReadBufferOverflow)
                throw new InvalidDataException($"{SetLabel} 分时采集出现短读或 USB 溢出，已停止整组。");
            chunks.Add(chunk);
            rowsRead += readRows;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        disposed = true;
        try { adc?.Dispose(); }
        finally { (transport as IDisposable)?.Dispose(); }
        return ValueTask.CompletedTask;
    }
}
