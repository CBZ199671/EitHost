using System.Diagnostics;
using EitHost.Core.Acquisition;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using static EitHost.App.ViewModels.RealtimeVisualizationProjection;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimePreviewCallbacks(
    Func<string, bool> IsDisplayedSet,
    Func<string> SignalViewMode,
    Func<string> DemodDisplayMode,
    Func<string> ImagePolarity,
    Func<double> ImageGain,
    Action ReferencePresentationChanged,
    Action<string> AddDiagnostic);

internal sealed class RealtimePreviewController
{
    private const int CoalescedLogLimit = 32;
    private const double LowImageQualityThreshold = 0.65;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DemodPreviewInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan BoundaryFitPreviewInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan ImagePreviewInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RoiPreviewInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FixedRoiTemporalInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(250);

    private readonly VisualizationWorkspaceViewModel workspace;

    /// <summary>Rasterise at the surface the operator is actually looking at.</summary>
    private int ImageRenderPixelSize =>
        VisualizationGeometry.ClampImagePixelSize(workspace.RoiImageCanvasSize);
    private readonly RealtimePreviewStateStore stateStore;
    private readonly RealtimePreviewCallbacks callbacks;

    internal RealtimePreviewController(
        VisualizationWorkspaceViewModel workspace,
        RealtimePreviewStateStore stateStore,
        RealtimePreviewCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void StartPump() => workspace.RealtimePreviewPump.Start();

    internal void StopPump() => workspace.RealtimePreviewPump.Stop();

    internal void RequestFlush() => workspace.RealtimePreviewPump.RequestFlush();

    internal void Clear(string? setLabel = null)
    {
        stateStore.Clear(
            setLabel,
            string.IsNullOrWhiteSpace(setLabel) || callbacks.IsDisplayedSet(setLabel));
    }

    internal void ResetPresentation()
    {
        workspace.RealtimeRawWaveStats = "等待采集数据";
        workspace.RealtimeDemodStats = "等待解调数据";
        workspace.RealtimePreviewPresenter.ClearDemod();
        workspace.RealtimeBoundaryStats = "等待边界电压";
        workspace.RealtimePreviewPresenter.ClearBoundaryAxis();
        workspace.RealtimeRawChannel1Geometry = null;
        workspace.RealtimeRawChannel2Geometry = null;
        workspace.RealtimeDemodPrimaryGeometry = null;
        workspace.RealtimeDemodSecondaryGeometry = null;
        workspace.RealtimeBoundaryTargetGeometry = null;
        workspace.RealtimeBoundaryReferenceGeometry = null;
        workspace.RealtimeBoundaryTemplateGeometry = null;
    }

    internal static void ResetTimers(RealtimeRunState state)
    {
        Interlocked.Exchange(ref state.LastPreviewTicks, 0);
        Interlocked.Exchange(ref state.LastDemodPreviewTicks, 0);
        Interlocked.Exchange(ref state.LastBoundaryFitPreviewTicks, 0);
        Interlocked.Exchange(ref state.LastImagePreviewTicks, 0);
        Interlocked.Exchange(ref state.LastRoiPreviewTicks, 0);
        Interlocked.Exchange(ref state.LastFixedRoiTemporalTicks, 0);
        Interlocked.Exchange(ref state.LastStatusTicks, 0);
        Interlocked.Exchange(ref state.LastContactAnalysisTicks, 0);
        Interlocked.Exchange(ref state.LastReconstructionScheduleTicks, 0);
        state.ReconstructionCadence.Reset();
        state.ContactDiagnosticsRunCount = 0;
        state.ContactDiagnosticsSkippedCount = 0;
        state.LastContactDiagnosticElapsedMs = 0.0;
        state.MaxContactDiagnosticElapsedMs = 0.0;
    }

    internal static bool ShouldUpdatePreview(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastPreviewTicks, PreviewInterval);

    internal static bool ShouldUpdateDemodPreview(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastDemodPreviewTicks, DemodPreviewInterval);

    internal static bool ShouldUpdateImagePreview(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastImagePreviewTicks, ImagePreviewInterval);

    internal static bool ShouldUpdateRoiPreview(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastRoiPreviewTicks, RoiPreviewInterval);

    internal static bool ShouldUpdateFixedRoiTemporal(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastFixedRoiTemporalTicks, FixedRoiTemporalInterval);

    internal static bool ShouldUpdateBoundaryFitPreview(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastBoundaryFitPreviewTicks, BoundaryFitPreviewInterval);

    internal static bool ShouldUpdateStatus(RealtimeRunState state) =>
        ShouldUpdate(ref state.LastStatusTicks, StatusInterval);

    internal void PublishImageStats(string setLabel, string stats)
    {
        stateStore.PublishImageStats(setLabel, callbacks.IsDisplayedSet(setLabel), stats);
        RequestFlush();
    }

    internal void PublishReconstructionActivity(string setLabel, string activity)
    {
        stateStore.PublishReconstructionActivity(setLabel, callbacks.IsDisplayedSet(setLabel), activity);
        RequestFlush();
    }

    internal void PublishReferenceInvalidated(string setLabel, bool invalidated)
    {
        stateStore.PublishReferenceInvalidated(setLabel, callbacks.IsDisplayedSet(setLabel), invalidated);
        RequestFlush();
    }

    internal void PublishSummary(string setLabel, string summary)
    {
        stateStore.PublishSummary(setLabel, callbacks.IsDisplayedSet(setLabel), summary);
        RequestFlush();
    }

    internal void ClearLowConfidenceImage(string setLabel)
    {
        if (stateStore.ClearLowConfidenceImage(setLabel, callbacks.IsDisplayedSet(setLabel)))
        {
            RequestFlush();
        }
    }

    internal void PublishReferenceNeutralImage(string setLabel, RealtimeRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var referenceKind = state.ReferenceIsProvisional
            ? "快速预览参考"
            : state.StartupDegradedReference is not null
                ? "降级参考"
                : "正式参考";
        var stats =
            $"{referenceKind} e{state.ReferenceEpoch} 已启用 · 中性基线 ΔV=0 · 等待新参考后的首个目标块";
        QueueNeutralImage(
            setLabel,
            state,
            state.LatestContactResult,
            stats,
            $"重构状态：等待参考 e{state.ReferenceEpoch} 后的首个目标");
    }

    internal void QueueNeutralImage(
        string setLabel,
        RealtimeRunState state,
        ElectrodeContactDiagnosticResult? contactResult,
        string stats,
        string activity)
    {
        state.ImageRasterCache.ResetColorScale();
        if (state.VisualizationWorker?.TryPost(new RealtimeVisualizationWorkItem(
                null,
                [],
                [],
                contactResult,
                null,
                null,
                0,
                RenderBoundaryFit: false,
                RenderImage: true,
                state.ReferenceEpoch,
                NeutralPresentation: new RealtimeNeutralImagePresentation(stats, activity),
                NonReplaceable: true)) != true)
        {
            callbacks.AddDiagnostic($"{setLabel} neutral visualization request rejected");
        }
    }

    internal void PublishReferenceSummary(string setLabel, string summary)
    {
        stateStore.PublishReferenceSummary(setLabel, callbacks.IsDisplayedSet(setLabel), summary);
        callbacks.ReferencePresentationChanged();
        RequestFlush();
    }

    internal void PublishBaselineIntegritySummary(string setLabel, string summary)
    {
        stateStore.PublishBaselineIntegritySummary(setLabel, callbacks.IsDisplayedSet(setLabel), summary);
        RequestFlush();
    }

    internal void PublishContactSummary(string setLabel, string summary)
    {
        stateStore.PublishContactSummary(setLabel, callbacks.IsDisplayedSet(setLabel), summary);
        RequestFlush();
    }

    internal void PublishMultiFrequencySummary(string setLabel, string summary)
    {
        stateStore.PublishMultiFrequencySummary(setLabel, callbacks.IsDisplayedSet(setLabel), summary);
        RequestFlush();
    }

    internal void PublishQualityAxes(
        string setLabel,
        string? dataQuality = null,
        string? referenceMode = null,
        string? reconstructionQuality = null,
        string? roiReadiness = null)
    {
        stateStore.PublishQualityAxes(
            setLabel,
            callbacks.IsDisplayedSet(setLabel),
            dataQuality,
            referenceMode,
            reconstructionQuality,
            roiReadiness);
        RequestFlush();
    }

    internal void QueueLog(string line)
    {
        stateStore.QueueLog(line, CoalescedLogLimit);
        RequestFlush();
    }

    internal void PublishRaw(string setLabel, RealtimeRawPreviewSnapshot snapshot)
    {
        stateStore.PublishRaw(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot);
        RequestFlush();
    }

    internal void TryPublishDemodAlignedRaw(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeDemodulatedBlock block)
    {
        if (!ShouldUpdatePreview(state))
        {
            return;
        }

        var selection = RealtimeRawPreviewSelector.Select(
            block,
            config.AcquisitionSettings.SampleRateHz,
            config.DacSettings.ActualFrequencyHz,
            state.ExecutionReceipt?.CalculateEffectiveChannelCycles(config.DacSettings.ActualFrequencyHz)
                ?? config.ExcitationSettings.ChannelCycles,
            config.DemodDiscardLeadingCycles,
            config.DemodDiscardTrailingCycles);
        if (selection is null ||
            state.RawPreviewBuffer is null ||
            !state.RawPreviewBuffer.TryRead(selection.StartSampleIndex, selection.SampleCount, out var rawCounts))
        {
            state.RawPreviewSliceMisses++;
            if (state.RawPreviewSliceMisses == 1 || state.RawPreviewSliceMisses % 100 == 0)
            {
                callbacks.AddDiagnostic(
                    $"{config.SetLabel} aligned raw preview unavailable block={block.BlockNumber} misses={state.RawPreviewSliceMisses}");
            }

            return;
        }

        PublishRaw(config.SetLabel, CreateRealtimeRawSnapshot(rawCounts, config, selection));
    }

    internal RealtimeRawPreviewSnapshot CreateBufferedRawSnapshot(BufferedAcquisitionPreviewData preview)
    {
        var usableValueCount = preview.Values.Length
            - (preview.Values.Length % Usb2070Constants.RequiredMeasurementChannelCount);
        if (usableValueCount < Usb2070Constants.RequiredMeasurementChannelCount)
        {
            return new RealtimeRawPreviewSnapshot(
                null,
                null,
                $"{preview.SetLabel} 同步采集已启动，等待缓冲数据。");
        }

        var matrix = RawAdcMatrix.FromInterleaved(preview.Values, usableValueCount);
        var excitation = preview.Excitation.Excitation;
        var acquisition = preview.Acquisition;
        var windowSamples = CalculateStimulusWindowSamples(
            acquisition.SampleRateHz,
            excitation.FrequencyHz,
            excitation.ChannelCycles);
        var channel1 = ExtractChannelVoltageWindow(matrix, 0, windowSamples, acquisition.Range);
        var range = FindFiniteRange(channel1);
        return new RealtimeRawPreviewSnapshot(
            CreateSeriesGeometry(channel1, range.Min, range.Max),
            null,
            $"{preview.SetLabel} buffer · AD {FormatAdRangeLabel(acquisition.Range)} · CH1 V1-V2 · {channel1.Length}/{windowSamples} pts · 实测 {range.Min:F4}~{range.Max:F4} V");
    }

    internal void RefreshSignalFromCache(string? setLabel)
    {
        var source = stateStore.GetSignalSource(setLabel);
        if (source is null)
        {
            workspace.RealtimePreviewPresenter.ClearDemod();
            workspace.RealtimeDemodStats = callbacks.SignalViewMode() switch
            {
                "reference" => "参考帧 · 未锁定",
                "target" => "目标帧 · 等待采集",
                _ => "等待解调数据"
            };
            return;
        }

        workspace.RealtimePreviewPresenter.ApplyDemod(CreateRealtimeSignalPreviewSnapshot(
            source,
            callbacks.SignalViewMode(),
            callbacks.DemodDisplayMode()));
    }

    internal void PublishSignal(string setLabel, RealtimeSignalPreviewSource source, string summary)
    {
        stateStore.PublishSignal(
            setLabel,
            callbacks.IsDisplayedSet(setLabel),
            source,
            summary,
            callbacks.SignalViewMode(),
            callbacks.DemodDisplayMode());
        RequestFlush();
    }

    internal void PublishBoundary(string setLabel, RealtimeBoundaryFitPreviewSnapshot snapshot)
    {
        stateStore.PublishBoundary(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot);
        RequestFlush();
    }

    internal void PublishBoundaryUnavailable(string setLabel, string summary)
    {
        PublishBoundary(
            setLabel,
            new RealtimeBoundaryFitPreviewSnapshot(
                null,
                null,
                null,
                summary,
                string.Empty,
                string.Empty,
                string.Empty));
    }

    internal void PublishImage(string setLabel, RealtimeImagePreviewSnapshot snapshot, string? summary)
    {
        stateStore.PublishImage(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot, summary);
        RequestFlush();
    }

    internal void PublishPreReferenceContactDiagnostic(
        string setLabel,
        int blockNumber,
        ElectrodeContactDiagnosticResult result)
    {
        var image = VisualizationRenderer.RenderPreReferenceContactDiagnosticImage(
            result.States,
            ImageRenderPixelSize);
        var yellow = result.States.Count(item => item == ElectrodeContactState.Yellow);
        var red = result.States.Count(item => item == ElectrodeContactState.Red);
        var darkRed = result.States.Count(item => item == ElectrodeContactState.DarkRed);
        var stats = $"启动电极诊断 · block {blockNumber} · 无 qc_ref/v_ref · 仅诊断、未重构 · 红 {red} · 黄 {yellow} · 深红 {darkRed}";
        PublishImage(setLabel, new RealtimeImagePreviewSnapshot(image, stats, LowConfidence: false), null);
    }

    internal RealtimePreviewUiUpdate CreateDisplayUpdate(string? setLabel)
    {
        var cache = stateStore.SelectDisplay(setLabel);
        return workspace.RealtimePreviewPresenter.ApplyDisplay(
            cache,
            setLabel,
            callbacks.SignalViewMode(),
            callbacks.DemodDisplayMode());
    }

    internal RealtimePreviewUiUpdate CreatePendingUpdate()
    {
        return workspace.RealtimePreviewPresenter.ApplyPending(stateStore.TakePending());
    }

    internal void ProcessVisualization(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        RealtimeVisualizationWorkItem item)
    {
        if (item.NeutralPresentation is { } neutral)
        {
            ProcessNeutralVisualization(config.SetLabel, state, item, neutral);
            return;
        }

        var result = item.Result ?? throw new InvalidOperationException("Reconstruction visualization requires a result.");
        if (item.ReferenceEpoch != state.ReferenceEpoch)
        {
            callbacks.AddDiagnostic(
                $"{config.SetLabel} discard stale visualization block={result.BlockNumber} " +
                $"reference-epoch={item.ReferenceEpoch}->{state.ReferenceEpoch}");
            return;
        }

        if (item.RenderBoundaryFit)
        {
            PublishBoundary(
                config.SetLabel,
                RealtimeVisualizationProjection.CreateRealtimeBoundaryFitPreviewSnapshot(
                    result,
                    item.Reference,
                    item.Target,
                    config.DifferenceOrientation,
                    item.TemplateDisplayPackage));
        }

        if (!item.RenderImage)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var renderNeutral = item.BoundaryChangeDecision is { Action: not EcdCwrBoundaryChangeAction.Change };
        var lowConfidence = !renderNeutral &&
            item.ImageQualityScore is { } quality &&
            quality < LowImageQualityThreshold;
        var image = renderNeutral
            ? state.ImageRasterCache.RenderNeutral(item.ContactResult?.States, ImageRenderPixelSize)
            : VisualizationRenderer.RenderReconstructionImageCached(
                result,
                callbacks.ImagePolarity(),
                callbacks.ImageGain(),
                item.ContactResult?.States,
                state.ImageRasterCache,
                ImageRenderPixelSize);
        var renderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        state.RenderEwmaMilliseconds = state.RenderEwmaMilliseconds <= 0.0
            ? renderMilliseconds
            : state.RenderEwmaMilliseconds + (0.25 * (renderMilliseconds - state.RenderEwmaMilliseconds));
        var now = Stopwatch.GetTimestamp();
        var previousDisplay = Interlocked.Exchange(ref state.LastDisplayCompletedTicks, now);
        var displayFps = previousDisplay == 0 || now <= previousDisplay
            ? 0.0
            : Stopwatch.Frequency / (double)(now - previousDisplay);
        Interlocked.Increment(ref state.DisplayFrameCount);

        var contactSuffix = item.ImageQualityScore is null
            ? string.Empty
            : lowConfidence
                ? $" · Q={item.ImageQualityScore:F2} 低置信度"
                : $" · Q={item.ImageQualityScore:F2}";
        if (result.WeightedSystemConditionNumber is { } conditionNumber)
        {
            contactSuffix += $" · κ={conditionNumber:G3}";
        }

        if (!string.IsNullOrWhiteSpace(item.DegradedStatus))
        {
            contactSuffix += $" · {item.DegradedStatus}";
        }

        string stats;
        if (renderNeutral)
        {
            var decision = item.BoundaryChangeDecision!;
            var stateText = decision.Action == EcdCwrBoundaryChangeAction.PendingChange
                ? $"候选变化 {decision.ConsecutiveChangeCount}/3"
                : "无变化";
            stats =
                $"block {result.BlockNumber} · 逆求解已完成 · ΔV {stateText} · " +
                $"score {decision.GlobalScore:F2}/{decision.Threshold:F2} · 3σ通道 {decision.ExcursionCount}/208 · " +
                $"可信显示中性 · backend {result.BackendElapsed.TotalMilliseconds:F0} ms";
        }
        else
        {
            var scaleLabel = ReconstructionScale.ToDisplayLabel(result.ReconstructionScaleStatus);
            stats = $"block {result.BlockNumber} · {config.ReconstructionRoute} · {scaleLabel} {result.MinConductivity:F4} ~ {result.MaxConductivity:F4} · backend {result.BackendElapsed.TotalMilliseconds:F0} ms · render {renderMilliseconds:F0} ms{contactSuffix}";
        }

        if (config.EnableDynamicKalman && !renderNeutral)
        {
            stats += result.DynamicKalmanApplied
                ? $" · Kalman {result.DynamicKalmanMode ?? "fast_image"}/{result.DynamicKalmanAction ?? "update"} NIS={result.DynamicKalmanNisPerDof.GetValueOrDefault():F2} K={result.DynamicKalmanGainMean.GetValueOrDefault():F2} solve={result.DynamicKalmanSolveMilliseconds.GetValueOrDefault():F0}ms L={result.DynamicKalmanTotalLatencyFrames}{(result.DynamicKalmanFallback == true ? " fallback" : string.Empty)}"
                : " · Kalman未应用（静态回退）";
        }

        stats += $" · {state.ReconstructionCadence.TargetFramesPerSecond:F1}/{(displayFps > 0.0 ? displayFps : 0.0):F1}fps";
        Volatile.Write(ref state.LastReconImageStatsTicks, Environment.TickCount64);
        PublishImage(
            config.SetLabel,
            new RealtimeImagePreviewSnapshot(image, stats, lowConfidence),
            ComposeImagingSummary(config.SetLabel, state));
    }

    internal void ProcessNeutralVisualization(
        string setLabel,
        RealtimeRunState state,
        RealtimeVisualizationWorkItem item,
        RealtimeNeutralImagePresentation neutral)
    {
        if (item.ReferenceEpoch != state.ReferenceEpoch)
        {
            return;
        }

        var neutralImage = state.ImageRasterCache.RenderNeutral(item.ContactResult?.States, ImageRenderPixelSize);
        PublishImage(
            setLabel,
            new RealtimeImagePreviewSnapshot(neutralImage, neutral.Stats, LowConfidence: false),
            null);
        PublishReconstructionActivity(setLabel, neutral.Activity);
    }

    internal static string ComposeImagingSummary(string setLabel, RealtimeRunState state)
    {
        var recon = Volatile.Read(ref state.ReconstructionFrames);
        var degraded = Volatile.Read(ref state.DegradedReconstructionFrames);
        return $"{setLabel} blocks={state.BlocksProcessed}, high={state.HighQualityBlocks}, low={state.LowQualityBlocks}, " +
            $"recon={recon}, degraded={degraded}, skip={state.SkippedReconstructionBlocks}, " +
            $"block-drop={state.PipelineDroppedBlocks}, sample-drop={state.PipelineDroppedSampleRows}, " +
            $"gap={state.PipelineSampleGaps}, usb-over={state.PipelineUsbOverflows}, " +
            $"q={state.PipelineQueuedSamples}/{state.PipelineQueueHighWater}, " +
            $"cad-reject={state.PipelineCadenceRefreshRejected}, " +
            $"diag={state.LastContactDiagnosticElapsedMs:F0}ms/{state.ContactDiagnosticsRunCount}, diag-skip={state.ContactDiagnosticsSkippedCount}";
    }

    internal static int CalculateStimulusWindowSamples(
        int sampleRateHz,
        int excitationFrequencyHz,
        double channelCycles)
    {
        if (sampleRateHz <= 0 || excitationFrequencyHz <= 0 || !double.IsFinite(channelCycles) || channelCycles <= 0)
        {
            return 1;
        }

        return Math.Max(1, (int)Math.Round(sampleRateHz / (double)excitationFrequencyHz * channelCycles));
    }

    private static RealtimeRawPreviewSnapshot CreateRealtimeRawSnapshot(
        IReadOnlyList<ushort> rawCounts,
        RealtimeImagingRunConfig config,
        RealtimeRawPreviewWindow selection)
    {
        var channel1 = rawCounts
            .Select(count => Usb2070VoltageScale.ConvertCountToVoltage(count, config.AcquisitionSettings.Range))
            .ToArray();
        var range = FindFiniteRange(channel1);
        return new RealtimeRawPreviewSnapshot(
            CreateSeriesGeometry(channel1, range.Min, range.Max),
            null,
            $"AD {FormatAdRangeLabel(config.AcquisitionSettings.Range)} · CH1 V1-V2 · " +
            $"激励 E{selection.StimulationChannelOneBased} {(selection.IsDiagnosticOnly ? "诊断" : "稳定")} " +
            $"{channel1.Length}/{selection.NominalSampleCount} pts · " +
            $"裁剪 {selection.LeadingDiscardSamples}/{selection.TrailingDiscardSamples} · " +
            $"实测 {range.Min:F4}~{range.Max:F4} V");
    }

    private static double[] ExtractChannelVoltageWindow(
        ushort[,] matrix,
        int channel,
        int requestedWindowSamples,
        Usb2070AdRange range)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var rows = matrix.GetLength(0);
        var channels = matrix.GetLength(1);
        if (rows <= 0 || channel < 0 || channel >= channels)
        {
            return [];
        }

        var values = new double[rows];
        for (var row = 0; row < rows; row++)
        {
            values[row] = Usb2070VoltageScale.ConvertCountToVoltage(matrix[row, channel], range);
        }

        var windowSamples = Math.Clamp(requestedWindowSamples, 1, rows);
        if (values.Length <= windowSamples)
        {
            return values;
        }

        var start = values.Length - windowSamples;
        var window = new double[windowSamples];
        Array.Copy(values, start, window, 0, windowSamples);
        return window;
    }

    internal static string FormatAdRangeLabel(Usb2070AdRange range) =>
        range switch
        {
            Usb2070AdRange.Bipolar5V => "±5V(10V)",
            Usb2070AdRange.Bipolar10V => "±10V(20V)",
            Usb2070AdRange.Bipolar2_5V => "±2.5V(5V)",
            Usb2070AdRange.Bipolar6_25V => "±6.25V(12.5V)",
            Usb2070AdRange.Bipolar12_5V => "±12.5V(25V)",
            Usb2070AdRange.Unipolar5V => "0~5V(5V)",
            Usb2070AdRange.Unipolar10V => "0~10V(10V)",
            Usb2070AdRange.Unipolar12_5V => "0~12.5V(12.5V)",
            _ => range.ToString()
        };

    private static bool ShouldUpdate(ref long lastTicks, TimeSpan interval)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastTicks);
        var intervalTicks = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        if (previous != 0 && now - previous < intervalTicks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref lastTicks, now, previous) == previous;
    }
}
