using EitHost.Core.Acquisition;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeAcquisitionLoopCallbacks(
    Action<string> Diagnostic,
    Action<string, string> PublishSummary,
    Action<string> PublishStatus,
    Action<string> PanelLog,
    Action<RealtimeImagingRunConfig, RealtimeRunState> BeginExperimentRun,
    Func<RealtimeImagingRunConfig, CancellationToken, Task<RealtimeDdsStartupResult>> ConfigureDds,
    Action<RealtimeImagingRunConfig, RealtimeRunState, DdsExecutionReceipt> InitializeAdaptiveContact,
    Action<RealtimeImagingRunConfig, RealtimeRunState> RegisterExperimentConfig,
    Func<RealtimeImagingRunConfig, RealtimeRunState, RealtimeDemodulationPipeline, CancellationToken, Task> ConsumeBlocks,
    Func<int, int, long> GetRawFlushByteThreshold,
    Func<RealtimeRawBatch<RealtimeRawPersistenceContext>, RealtimeImagingRunConfig, RealtimeRunState, Task> PersistRawBatch,
    Func<RealtimeImagingRunConfig, RealtimeRunState, bool, Task> CompleteRawPersistence,
    Func<Task> DrainDerivedPersistence,
    Func<string, string, Func<Task<DdsCommandResult>>, Task<DdsCommandResult>> SendDdsCommand,
    Action<RealtimeImagingRunConfig, RealtimeRunState, Exception?> CompleteExperimentRun,
    Action<RealtimeImagingRunConfig, RealtimeRunState> CompleteUi);

internal sealed class RealtimeAcquisitionLoopController
{
    private const int SampleQueueCapacity = 256;
    private const int SampleQueueRecoveryLowWaterMark = 128;
    private const int DiagnosticBlockQueueCapacity = 2;
    private const int RawPersistenceQueueCapacity = 6;
    private const long BytesPerAdcValue = sizeof(ushort);
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly RealtimeAcquisitionLoopCallbacks callbacks;

    internal RealtimeAcquisitionLoopController(
        IUsb2070NativeApi usb2070NativeApi,
        RealtimeAcquisitionLoopCallbacks callbacks)
    {
        this.usb2070NativeApi = usb2070NativeApi ?? throw new ArgumentNullException(nameof(usb2070NativeApi));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal async Task RunAsync(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        CancellationToken cancellationToken)
    {
        Usb2070Session? session = null;
        DdsSerialPortTransport? ddsTransport = null;
        RealtimeDemodulationPipeline? pipeline = null;
        RealtimeRawBatchCollector<RealtimeRawPersistenceContext>? rawBatchCollector = null;
        RealtimePersistenceQueue<RealtimeRawBatch<RealtimeRawPersistenceContext>>? rawPersistenceQueue = null;
        Exception? realtimeLoopFailure = null;
        Exception? rawEnqueueFailure = null;
        try
        {
            callbacks.Diagnostic($"{config.SetLabel} loop enter");
            callbacks.BeginExperimentRun(config, state);
            callbacks.PublishSummary(config.SetLabel, $"{config.SetLabel} 启动中：正在验证 DDS firmware v2 ACK。");
            callbacks.PublishStatus($"{config.SetLabel} 实时成像启动中：验证 DDS 能力并获取实际驻留时间。");
            callbacks.PublishSummary(config.SetLabel, $"{config.SetLabel} 启动中：正在打开 DDS 串口 {config.DdsPortName}。");
            callbacks.PublishStatus($"{config.SetLabel} 实时成像启动中：正在配置 DDS。");
            var ddsStartup = await callbacks.ConfigureDds(config, cancellationToken).ConfigureAwait(false);
            ddsTransport = ddsStartup.Transport;
            state.ExecutionReceipt = ddsStartup.Execution;
            callbacks.InitializeAdaptiveContact(config, state, ddsStartup.Execution);
            callbacks.RegisterExperimentConfig(config, state);
            var actualFrequencyHz = config.DacSettings.ActualFrequencyHz;
            var effectiveChannelCycles = ddsStartup.Execution.CalculateEffectiveChannelCycles(actualFrequencyHz);
            var realtimeSettings = new RealtimeDemodulationSettings(
                config.AcquisitionSettings.SampleRateHz,
                actualFrequencyHz,
                effectiveChannelCycles,
                framesPerBlock: config.FramesPerBlock,
                minimumAcceptedFrames: config.MinimumAcceptedFrames,
                discardLeadingCycles: config.DemodDiscardLeadingCycles,
                discardTrailingCycles: config.DemodDiscardTrailingCycles,
                interferenceFrequencyHz: config.InterferenceFrequencyHz,
                adRange: config.AcquisitionSettings.Range);
            pipeline = new RealtimeDemodulationPipeline(
                realtimeSettings,
                new RealtimeDemodulationPipelineOptions(
                    SampleQueueCapacity: SampleQueueCapacity,
                    BlockQueueCapacity: DiagnosticBlockQueueCapacity,
                    DropOldestBlocksWhenFull: true,
                    RetainProcessedBlocks: false,
                    DropOldestSamplesWhenFull: true,
                    SampleQueueRecoveryLowWaterMark: SampleQueueRecoveryLowWaterMark,
                    DiscontinuityObserver: state.SampleContinuity.Report));
            state.RawPreviewBuffer = new RealtimeRawChannelBuffer(
                CalculateRawPreviewBufferCapacity(realtimeSettings, config.ReadRows));
            state.RunCoordinator.AttachConsumer(
                callbacks.ConsumeBlocks(config, state, pipeline, cancellationToken));
            callbacks.Diagnostic(
                $"{config.SetLabel} demod pipeline ready firmware={ddsStartup.Capabilities.FirmwareVersion} " +
                $"requestedFrequency={config.DacSettings.FrequencyHz}Hz ftw={config.DacSettings.FrequencyTuningWord} " +
                $"actualFrequency={actualFrequencyHz:0.########}Hz requested={ddsStartup.Execution.RequestedTimeUs}us " +
                $"effective={ddsStartup.Execution.EffectiveTimeUs:0.###}us " +
                $"cycles={effectiveChannelCycles:0.######} ticks={ddsStartup.Execution.TimerTicks}");

            callbacks.Diagnostic($"{config.SetLabel} DDS startup status={ddsStartup.Status}; opening USB2070 #{config.UsbDevice.DeviceNumber}");
            callbacks.PublishSummary(
                config.SetLabel,
                $"{config.SetLabel} DDS v{ddsStartup.Capabilities.FirmwareVersion} 已确认：" +
                $"请求 {ddsStartup.Execution.RequestedTimeUs} us，实际 {ddsStartup.Execution.EffectiveTimeUs:0.###} us，" +
                $"tick={ddsStartup.Execution.TimerTicks}；正在打开 USB2070 #{config.UsbDevice.DeviceNumber}。");
            callbacks.PublishStatus($"{config.SetLabel} 实时成像启动中：正在启动采集卡。");
            var service = new Usb2070Service(usb2070NativeApi);
            session = service.Open(config.UsbDevice);
            session.StartAcquisition(config.AcquisitionSettings);
            var acquisitionMetadata = session.LastAcquisitionMetadata ?? CreateAcquisitionMetadata(config.AcquisitionSettings);
            state.RawRingAcquisitionMetadata = acquisitionMetadata;
            if (config.PersistRawAcquisitionHdf5)
            {
                rawBatchCollector = new RealtimeRawBatchCollector<RealtimeRawPersistenceContext>(
                    new RealtimeRawPersistenceContext(
                        config.Pairing,
                        config.ExcitationMetadata with { Execution = ddsStartup.Execution },
                        acquisitionMetadata),
                    Usb2070Constants.RequiredMeasurementChannelCount,
                    BytesPerAdcValue,
                    callbacks.GetRawFlushByteThreshold(
                        config.AcquisitionSettings.SampleRateHz,
                        config.ReadRows));
                rawPersistenceQueue = new RealtimePersistenceQueue<RealtimeRawBatch<RealtimeRawPersistenceContext>>(
                    RawPersistenceQueueCapacity,
                    batch => callbacks.PersistRawBatch(batch, config, state),
                    batch => batch.Dispose(),
                    state.RunCoordinator.RecordRawPersistenceQueueDepth);
            }
            else if (config.StoragePolicy.KeepRawRingBuffer)
            {
                state.RawRingBuffer = new RealtimeRawRingBuffer();
            }

            callbacks.Diagnostic($"{config.SetLabel} USB2070 acquisition started");
            callbacks.PublishSummary(config.SetLabel, $"{config.SetLabel} 采集中：等待高质量解调 block。");
            if (config.PersistRawAcquisitionHdf5)
            {
                callbacks.PanelLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime raw HDF5 batching enabled");
            }

            callbacks.PanelLog(
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime AD start {config.AcquisitionSettings.SampleRateHz}Hz read={config.ReadRows} rows/{config.ReadRows * Usb2070Constants.RequiredMeasurementChannelCount} values");

            var valuesPerRead = checked(config.ReadRows * Usb2070Constants.RequiredMeasurementChannelCount);
            var buffer = new ushort[valuesPerRead];
            var loggedFirstRead = false;
            long nextSampleIndex = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                state.RunCoordinator.EnsureConsumerRunning();
                rawPersistenceQueue?.ThrowIfFaulted();
                var readCount = session.Read(buffer, checked((uint)buffer.Length));
                var readCompletedAt = DateTimeOffset.UtcNow;
                if (!loggedFirstRead)
                {
                    loggedFirstRead = true;
                    callbacks.Diagnostic($"{config.SetLabel} first USB2070 read values={readCount}");
                    callbacks.PublishSummary(
                        config.SetLabel,
                        $"{config.SetLabel} 采集中：已读取首块 {readCount} values，等待解调 block。");
                    callbacks.PublishStatus($"{config.SetLabel} 已开始采集，正在实时解调。");
                }

                var matrix = RawAdcMatrix.FromInterleaved(buffer, readCount);
                var readEndSampleIndex = checked(nextSampleIndex + matrix.GetLength(0));
                var bufferOverflow = session.LastReadBufferOverflow;
                RawAcquisitionDiscontinuityEvent? discontinuity = null;
                if (bufferOverflow)
                {
                    discontinuity = new RawAcquisitionDiscontinuityEvent(
                        nextSampleIndex,
                        readEndSampleIndex,
                        readCompletedAt);
                    state.RecordAcquisitionDiscontinuity(discontinuity);
                }

                state.RawPreviewBuffer.Append(matrix, nextSampleIndex, channelIndex: 0);
                state.RawRingBuffer?.Append(buffer, readCount, DateTimeOffset.Now);
                var rawBatch = rawBatchCollector?.Append(
                        buffer,
                        readCount,
                        nextSampleIndex,
                        discontinuity);

                _ = pipeline.TryEnqueue(matrix, nextSampleIndex, bufferOverflow);
                nextSampleIndex = readEndSampleIndex;
                state.TotalRawSamples = nextSampleIndex;
                if (rawBatch is not null && rawPersistenceQueue is not null)
                {
                    try
                    {
                        if (!rawPersistenceQueue.TryEnqueue(rawBatch))
                        {
                            await rawPersistenceQueue.EnqueueAsync(rawBatch).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        rawBatch.Dispose();
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            callbacks.Diagnostic($"{config.SetLabel} loop canceled by stop request");
        }
        catch (Exception ex)
        {
            realtimeLoopFailure = ex;
            callbacks.Diagnostic($"{config.SetLabel} loop failed: {ex}");
            callbacks.PublishSummary(config.SetLabel, $"{config.SetLabel} 实时成像异常：{ex.Message}");
            callbacks.PublishStatus($"{config.SetLabel} 实时成像异常：{ex.Message}");
            callbacks.PanelLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime loop failed {ex.Message}");
            throw;
        }
        finally
        {
            callbacks.Diagnostic($"{config.SetLabel} loop cleanup begin");
            if (rawBatchCollector?.Detach("stop") is { } finalRawBatch)
            {
                try
                {
                    if (rawPersistenceQueue is null)
                    {
                        await callbacks.PersistRawBatch(finalRawBatch, config, state).ConfigureAwait(false);
                    }
                    else if (!rawPersistenceQueue.TryEnqueue(finalRawBatch))
                    {
                        await rawPersistenceQueue.EnqueueAsync(finalRawBatch).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    rawEnqueueFailure = ex;
                    finalRawBatch.Dispose();
                    callbacks.Diagnostic($"{config.SetLabel} final raw enqueue failed: {ex}");
                }
            }

            rawBatchCollector?.Dispose();

            try
            {
                session?.StopAcquisition();
            }
            catch (Exception ex)
            {
                callbacks.PanelLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime AD stop warning {ex.Message}");
            }
            finally
            {
                session?.Dispose();
            }

            if (ddsTransport is not null)
            {
                try
                {
                    var ddsClient = new DdsProtocolClient(ddsTransport);
                    await callbacks.SendDdsCommand(
                        config.SetLabel,
                        "停止激励",
                        () => ddsClient.StopExcitationAsync()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    callbacks.PanelLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime DDS stop warning {ex.Message}");
                }
                finally
                {
                    ddsTransport.Dispose();
                }
            }

            if (pipeline is not null)
            {
                try
                {
                    await pipeline.AbortAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    callbacks.PanelLog($"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime demod abort warning {ex.Message}");
                }
            }

            Exception? consumerFailure = null;
            try
            {
                await state.RunCoordinator.WaitForConsumerAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal stop
            }
            catch (Exception ex)
            {
                consumerFailure = ex;
                callbacks.Diagnostic($"{config.SetLabel} realtime consumer cleanup observed failure: {ex}");
            }

            if (state.ReconstructionTask is { IsCompleted: false } reconstructionTask)
            {
                try
                {
                    await reconstructionTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch
                {
                    // do not block hardware shutdown on a slow backend request
                }
            }

            if (state.VisualizationWorker is { } visualizationWorker)
            {
                try
                {
                    await visualizationWorker.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await visualizationWorker.DisposeAsync().ConfigureAwait(false);
                    state.VisualizationWorker = null;
                }
                catch (TimeoutException)
                {
                    callbacks.Diagnostic($"{config.SetLabel} visualization worker stop timeout");
                }
            }

            Exception? rawPersistenceFailure = rawEnqueueFailure;
            if (rawPersistenceQueue is not null)
            {
                try
                {
                    await rawPersistenceQueue.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    rawPersistenceFailure = ex;
                    callbacks.Diagnostic($"{config.SetLabel} raw persistence queue failed: {ex}");
                }

                try
                {
                    await callbacks.CompleteRawPersistence(
                            config,
                            state,
                            rawPersistenceFailure is null)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    rawPersistenceFailure ??= ex;
                    callbacks.Diagnostic($"{config.SetLabel} raw persistence final checkpoint failed: {ex}");
                }
            }

            try
            {
                await state.RunCoordinator.DrainRawPersistenceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                rawPersistenceFailure ??= ex;
                callbacks.Diagnostic($"{config.SetLabel} raw ring persistence failed: {ex}");
            }

            Exception? derivedPersistenceFailure = null;
            try
            {
                await callbacks.DrainDerivedPersistence().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                derivedPersistenceFailure = ex;
                callbacks.Diagnostic($"{config.SetLabel} derived persistence queue failed: {ex}");
            }

            state.RawRingBuffer = null;
            callbacks.CompleteExperimentRun(
                config,
                state,
                realtimeLoopFailure ?? consumerFailure ?? rawPersistenceFailure ?? derivedPersistenceFailure);

            if (pipeline is not null)
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
            }

            callbacks.PublishSummary(
                config.SetLabel,
                $"{config.SetLabel} 已停止：blocks={state.BlocksProcessed}, high={state.HighQualityBlocks}, recon={state.ReconstructionFrames}, skip={state.SkippedReconstructionBlocks}。");
            callbacks.CompleteUi(config, state);
            callbacks.Diagnostic($"{config.SetLabel} loop cleanup complete");
            if (realtimeLoopFailure is null && consumerFailure is not null)
            {
                throw new InvalidOperationException(
                    $"{config.SetLabel} realtime demodulation consumer failed.",
                    consumerFailure);
            }

            if (realtimeLoopFailure is null && consumerFailure is null && rawPersistenceFailure is not null)
            {
                throw new InvalidOperationException(
                    $"{config.SetLabel} realtime raw persistence failed.",
                    rawPersistenceFailure);
            }

            if (realtimeLoopFailure is null &&
                consumerFailure is null &&
                rawPersistenceFailure is null &&
                derivedPersistenceFailure is not null)
            {
                throw new InvalidOperationException(
                    $"{config.SetLabel} realtime derived persistence failed.",
                    derivedPersistenceFailure);
            }
        }
    }

    private static int CalculateRawPreviewBufferCapacity(
        RealtimeDemodulationSettings settings,
        int readRows)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readRows);
        return checked(Math.Max(settings.RequiredBufferedSamples * 2, readRows * 8));
    }

    private static Usb2070AcquisitionMetadata CreateAcquisitionMetadata(Usb2070AcquisitionSettings settings) =>
        new(
            settings.SampleRateHz,
            settings.Range,
            16,
            settings.EnabledOneBasedChannels,
            settings.TriggerMode,
            settings.TriggerSource);
}
