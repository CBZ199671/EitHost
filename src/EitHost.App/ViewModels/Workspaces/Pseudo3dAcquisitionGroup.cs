using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using EitHost.Core.Acquisition;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record Pseudo3dRunSelection(bool Enabled, PairingSummaryItem? Lower, PairingSummaryItem? Upper);

internal sealed class Pseudo3dAcquisitionGroup
{
    private readonly object gate = new();
    private readonly string[] labels;
    private readonly Func<RealtimeImagingRunConfig, IUsb2070NativeApi, IPseudo3dTimeDivisionDevice> createDevice;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Dictionary<string, Member> members = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? run;
    private Exception? consumerFailure;
    private int exitedMembers;

    internal Pseudo3dAcquisitionGroup(string lower, string upper,
        Func<RealtimeImagingRunConfig, IUsb2070NativeApi, IPseudo3dTimeDivisionDevice>? createDevice = null)
    {
        if (string.IsNullOrWhiteSpace(lower) || string.IsNullOrWhiteSpace(upper) ||
            string.Equals(lower, upper, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("伪三维需要不同的上下层设备。");
        labels = [lower, upper];
        this.createDevice = createDevice ?? ((config, native) =>
            new Pseudo3dTimeDivisionDevice(native, config.UsbDevice, config.SetLabel, config.DdsPortName));
    }

    internal Guid SessionId { get; } = Guid.NewGuid();
    internal int GetSlot(string label) => Array.FindIndex(labels, item => string.Equals(item, label, StringComparison.OrdinalIgnoreCase)) is var slot && slot >= 0
        ? slot : throw new ArgumentException("设备不是当前伪三维组成员。", nameof(label));
    internal bool Contains(string label) => labels.Contains(label, StringComparer.OrdinalIgnoreCase);
    internal void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal static DeviceRunParameterProfile ApplyProfile(DeviceRunParameterProfile parameters) => parameters with
    {
        DdsDacChannel = 1, DdsFrequencyHz = Pseudo3dAcquisitionProfile.FrequencyHz,
        DdsGain = Pseudo3dAcquisitionProfile.CurrentUa / 100, DdsPhaseDegrees = 0, DdsPgaGain = 1,
        ExcitationMode = DdsExcitationMode.Adjacent, ExcitationChannelCycles = Pseudo3dAcquisitionProfile.ChannelCycles,
        ExcitationScanTimes = Pseudo3dAcquisitionProfile.ScanFrames, ExcitationOverheadUs = 0,
        DemodDiscardLeadingCycles = Pseudo3dAcquisitionProfile.DiscardLeadingCycles,
        DemodDiscardTrailingCycles = Pseudo3dAcquisitionProfile.DiscardTrailingCycles,
        AcquisitionSampleRateHz = Pseudo3dAcquisitionProfile.SampleRateHz, AcquisitionRange = Usb2070AdRange.Bipolar5V,
        AcquisitionTriggerMode = Usb2070TriggerMode.Continue, AcquisitionTriggerSource = Usb2070TriggerSource.Software,
        AcquisitionTriggerDelay = 0, AcquisitionTriggerLength = 1024, AcquisitionTriggerLevel = 2048,
        AcquisitionReadSampleRows = 2048, RealtimeFramesPerBlock = 3, RealtimeMinimumAcceptedFrames = 3,
        RealtimeUseFrequencyDivisionLockIn = false
    };

    internal async Task RunMemberAsync(RealtimeImagingRunConfig config, RealtimeRunState state,
        IUsb2070NativeApi nativeApi, RealtimeAcquisitionLoopCallbacks callbacks, CancellationToken memberToken)
    {
        using var registration = memberToken.Register(Cancel);
        Exception? failure = null;
        try
        {
            callbacks.BeginExperimentRun(config, state);
            callbacks.PublishSummary(config.SetLabel, $"{config.SetLabel} 同频分时启动中：等待两套就绪。");
            lock (gate)
            {
                if (!Contains(config.SetLabel) || !members.TryAdd(config.SetLabel, new Member(config, state)))
                    throw new InvalidOperationException("分时组成员重复或不匹配。");
                if (members.Count == 2) ready.TrySetResult();
            }
            await ready.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            Task shared;
            lock (gate) shared = run ??= Task.Run(() => RunCoreAsync(nativeApi, callbacks));
            await shared.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failure = exception;
            Cancel();
            callbacks.PublishStatus($"双设备分时组已停止：{exception.Message}");
            callbacks.Diagnostic($"{config.SetLabel} time-division group failure: {exception}");
        }
        finally
        {
            state.RequestStop();
            try { await state.RunCoordinator.WaitForConsumerAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { failure ??= exception; }
            if (state.ReconstructionTask is { IsCompleted: false } reconstructionTask)
            {
                try { await reconstructionTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (TimeoutException) { callbacks.Diagnostic($"{config.SetLabel} backend still stopping after hardware shutdown"); }
                catch (Exception exception) { failure ??= exception; }
            }
            try
            {
                if (state.VisualizationWorker is { } worker)
                {
                    await worker.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    await worker.DisposeAsync().ConfigureAwait(false);
                    state.VisualizationWorker = null;
                }
                if (config.PersistRawAcquisitionHdf5)
                    await callbacks.CompleteRawPersistence(config, state, failure is null).ConfigureAwait(false);
                await callbacks.DrainDerivedPersistence().ConfigureAwait(false);
            }
            catch (Exception exception) { failure ??= exception; }
            callbacks.CompleteExperimentRun(config, state, failure);
            callbacks.PublishSummary(config.SetLabel,
                $"{config.SetLabel} 分时组已停止：blocks={state.BlocksProcessed}, high={state.HighQualityBlocks}, recon={state.ReconstructionFrames}。");
            callbacks.CompleteUi(config, state);
            if (Interlocked.Increment(ref exitedMembers) == 2) cancellation.Dispose();
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task RunCoreAsync(IUsb2070NativeApi nativeApi, RealtimeAcquisitionLoopCallbacks callbacks)
    {
        var consume = callbacks.ConsumeBlockStream ?? throw new InvalidOperationException("分时解调消费入口未配置。");
        var pair = labels.Select(label => members[label]).ToArray();
        var devices = pair.Select(member => createDevice(member.Config, nativeApi)).ToArray();
        var token = cancellation.Token;
        try
        {
            var coordinator = new Pseudo3dTimeDivisionCoordinator(SessionId, devices);
            await coordinator.RunAsync(async (lower, upper, ct) =>
            {
                var captures = new[] { lower, upper };
                for (var slot = 0; slot < 2; slot++)
                {
                    var member = pair[slot];
                    var capture = captures[slot];
                    var config = member.Config;
                    var state = member.State;
                    if (state.ExecutionReceipt is null)
                    {
                        state.ExecutionReceipt = capture.Execution;
                        callbacks.InitializeAdaptiveContact(config, state, capture.Execution);
                        callbacks.RegisterExperimentConfig(config, state);
                        state.RawRingAcquisitionMetadata = new Usb2070AcquisitionMetadata(
                            config.AcquisitionSettings.SampleRateHz, config.AcquisitionSettings.Range,
                            config.UsbDevice.AdBit, config.AcquisitionSettings.EnabledOneBasedChannels,
                            config.AcquisitionSettings.TriggerMode, config.AcquisitionSettings.EffectiveTriggerSource);
                        state.RawPreviewBuffer = new RealtimeRawChannelBuffer(Pseudo3dAcquisitionProfile.SampleRateHz * 2);
                        var consumption = consume(config, state, member.Blocks.Reader.ReadAllAsync(ct), ct);
                        state.RunCoordinator.AttachConsumer(consumption);
                        _ = consumption.ContinueWith(completed =>
                        {
                            if (ct.IsCancellationRequested) return;
                            var error = completed.Exception?.GetBaseException() ??
                                new InvalidOperationException("分时解调消费者提前退出，整组已停止。");
                            Interlocked.CompareExchange(ref consumerFailure, error, null);
                            Cancel();
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    state.RunCoordinator.EnsureConsumerRunning();
                    var start = member.NextSample;
                    var rows = capture.Raw.GetLength(0);
                    var block = capture.Block with
                    {
                        StartSampleIndex = start + capture.Block.StartSampleIndex,
                        EndSampleIndex = start + capture.Block.EndSampleIndex
                    };
                    state.RawPreviewBuffer!.Append(capture.Raw, start, channelIndex: 0);
                    if (config.PersistRawAcquisitionHdf5)
                    {
                        var values = new ushort[capture.Raw.Length];
                        Buffer.BlockCopy(capture.Raw, 0, values, 0, values.Length * sizeof(ushort));
                        // Explicit excluded ranges reset offline demodulation at
                        // each slot. The complete slot provenance is durable even
                        // when a reconstruction has not yet acquired a reference.
                        var provenance = JsonSerializer.Serialize(new
                        {
                            kind = "pseudo3d-time-division-excluded", stamp = block.TimeDivision,
                            rawStart = start, rawEnd = start + rows,
                            analysisStart = start + capture.AnalysisStartRow,
                            analysisEnd = start + capture.AnalysisEndRow,
                            finiteScan = capture.FiniteScan
                        });
                        var exclusions = new[]
                        {
                            new RawAcquisitionDiscontinuityEvent(start, start + capture.AnalysisStartRow,
                                capture.CaptureStartedAt + TimeSpan.FromSeconds((double)capture.AnalysisStartRow / Pseudo3dAcquisitionProfile.SampleRateHz), provenance),
                            new RawAcquisitionDiscontinuityEvent(start + capture.AnalysisEndRow, start + rows,
                                capture.CaptureStartedAt + TimeSpan.FromSeconds((double)rows / Pseudo3dAcquisitionProfile.SampleRateHz), provenance)
                        }.Where(exclusion => exclusion.EndSampleIndex > exclusion.StartSampleIndex).ToArray();
                        if (capture.FiniteScan is { } finiteWindow)
                        {
                            var finite = new Pseudo3dFiniteScanProvenance(block.TimeDivision!, start, start + rows,
                                start + capture.AnalysisStartRow, start + capture.AnalysisEndRow, finiteWindow);
                            // A typed whole-burst boundary prevents continuous replay
                            // from joining slots. Finite replay reads the full raw burst.
                            exclusions = [new(start, start + rows,
                                capture.CaptureStartedAt + TimeSpan.FromSeconds((double)rows / Pseudo3dAcquisitionProfile.SampleRateHz),
                                JsonSerializer.Serialize(finite))];
                        }
                        using var batch = new RealtimeRawBatch<RealtimeRawPersistenceContext>(
                            new(config.Pairing, config.ExcitationMetadata with { Execution = capture.Execution, ScanStatus = capture.CompletedStatus },
                                state.RawRingAcquisitionMetadata!), [values], values.Length, checked((int)block.TimeDivision!.Round - 1),
                            start, start + rows, capture.CaptureStartedAt, "pseudo3d-time-division", exclusions);
                        await callbacks.PersistRawBatch(batch, config, state).ConfigureAwait(false);
                    }
                    member.NextSample += rows;
                    state.TotalRawSamples = member.NextSample;
                    await member.Blocks.Writer.WriteAsync(block, ct).ConfigureAwait(false);
                    callbacks.Diagnostic($"{config.SetLabel} tdm round={block.TimeDivision!.Round} slot={slot} high={block.IsHighQuality} " +
                        $"midpoint={block.TimeDivision.SampleMidpoint:O} uncertainty_ms={block.TimeDivision.TimingUncertaintyMilliseconds:0.000} " +
                        $"completed={capture.CompletedStatus.CompletedCycles}");
                }
            }, token, reportTimingRetry: message =>
            {
                callbacks.Diagnostic(message);
                callbacks.PanelLog(message);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref consumerFailure) is not null)
        {
            throw new InvalidOperationException("分时解调处理失败，整组已停止。", consumerFailure);
        }
        finally
        {
            foreach (var member in pair) member.Blocks.Writer.TryComplete();
            Cancel();
            foreach (var device in devices) await device.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Member(RealtimeImagingRunConfig config, RealtimeRunState state)
    {
        internal RealtimeImagingRunConfig Config { get; } = config;
        internal RealtimeRunState State { get; } = state;
        internal long NextSample;
        internal Channel<RealtimeDemodulatedBlock> Blocks { get; } = Channel.CreateBounded<RealtimeDemodulatedBlock>(
            new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    }
}
