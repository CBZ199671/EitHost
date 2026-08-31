using EitHost.Core.Analysis;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Demodulation;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeRoiCallbacks(
    Func<string, bool> IsDisplayedSet,
    Func<RealtimeRunState, bool> ShouldUpdatePreview,
    Func<RealtimeRunState, bool> ShouldUpdateTemporal,
    Action<string, string> PublishReadiness,
    Action RequestPreviewFlush,
    Action RaiseSaveCanExecute,
    Action<RealtimeDemodulatedBlock, RealtimeRunState> PersistTrustedNeutralEvidence,
    Action<string> Diagnostic);

internal sealed class RealtimeRoiController
{
    private const int SeriesLimit = 2000;
    private readonly VisualizationWorkspaceViewModel workspace;
    private readonly RealtimePreviewStateStore previewState;
    private readonly RealtimeRoiCallbacks callbacks;

    internal RealtimeRoiController(
        VisualizationWorkspaceViewModel workspace,
        RealtimePreviewStateStore previewState,
        RealtimeRoiCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.previewState = previewState ?? throw new ArgumentNullException(nameof(previewState));
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    internal void PublishProvisionalUnavailable(string setLabel)
    {
        PublishUnavailable(setLabel, "ROI：快速预览参考仅供观察；定量曲线等待正式参考稳定。");
    }

    internal void PublishUnavailable(string setLabel, string summary)
    {
        var snapshot = new RealtimeRoiPreviewSnapshot(
            null,
            null,
            null,
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            summary,
            FixedRoiTemporalVisualSnapshot.Empty);
        previewState.PublishRoi(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot);
        callbacks.PublishReadiness(
            setLabel,
            $"ROI 就绪：否 · {summary.Replace("ROI：", string.Empty, StringComparison.Ordinal)}");
        callbacks.RequestPreviewFlush();
    }

    internal void PublishMeasurement(
        string setLabel,
        RealtimeReconstructionResult result,
        double qualityWeight,
        RealtimeRunState state,
        string valueSource = RoiValueSource.InverseReconstruction)
    {
        callbacks.PublishReadiness(setLabel, "ROI 就绪：是 · 当前参考 epoch 正常发布");
        var roi = RoiVisualizationEngine.CaptureSelection(workspace);
        FixedRoiTemporalSample? fixedSample = null;
        RoiCurvePoint? point;
        if (roi.FixedCell is { } fixedCell)
        {
            var measurements = RoiConductivityAnalyzer.MeasureAll(
                workspace.FixedRoiGrid,
                result.NodeCoords,
                result.CellConnectivity,
                result.Conductivity,
                VisualizationGeometry.ImagePaddingFraction,
                result.ParameterEntity);
            var fixedCellIndex = RoiVisualizationEngine.GetFixedRoiCellIndex(workspace.FixedRoiGrid, fixedCell.Id);
            fixedSample = FixedRoiTemporalSample.FromMeasurements(
                0,
                result.BlockNumber,
                result.CompletedAt,
                qualityWeight,
                measurements,
                state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
                state.ActiveReferenceLockKind);
            point = RoiVisualizationEngine.CreateRoiCurvePointFromMeasurement(
                setLabel,
                0,
                result.BlockNumber,
                result.CompletedAt,
                qualityWeight,
                state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
                state.ActiveReferenceLockKind,
                measurements[fixedCellIndex],
                roi,
                valueSource);
        }
        else
        {
            point = RoiVisualizationEngine.CreateRoiCurvePoint(
                setLabel,
                0,
                result.BlockNumber,
                result.CompletedAt,
                qualityWeight,
                state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
                state.ActiveReferenceLockKind,
                result.Conductivity,
                result.NodeCoords,
                result.CellConnectivity,
                roi,
                result.ParameterEntity);
            if (point is not null)
            {
                point = point with { ValueSource = valueSource };
            }
        }

        if (roi.Revision != workspace.RoiDefinitionRevision)
        {
            return;
        }

        if (point is null && fixedSample is null)
        {
            previewState.PublishRoi(
                setLabel,
                callbacks.IsDisplayedSet(setLabel),
                new RealtimeRoiPreviewSnapshot(
                    null,
                    null,
                    null,
                    [],
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    "ROI：当前选区没有命中重构单元。",
                    FixedRoiTemporalVisualSnapshot.Empty));
            callbacks.RequestPreviewFlush();
            return;
        }

        var shouldUpdatePreview = callbacks.ShouldUpdatePreview(state);
        RoiCurvePoint[] seriesSnapshot;
        FixedRoiTemporalSample[] fixedSamplesSnapshot;
        FixedRoiTemporalVisualSnapshot previousFixedTemporal;
        lock (previewState.Gate)
        {
            if (roi.Revision != workspace.RoiDefinitionRevision)
            {
                return;
            }

            var series = previewState.RoiSeriesBySet.TryGetValue(setLabel, out var existing)
                ? existing
                : previewState.RoiSeriesBySet[setLabel] = [];
            var lastSeriesFrameIndex = series.Count == 0 ? 0 : series[^1].FrameIndex;
            var lastFixedFrameIndex = previewState.FixedRoiSamplesBySet.TryGetValue(setLabel, out var priorSamples) &&
                priorSamples.Count > 0
                    ? priorSamples[^1].FrameIndex
                    : 0;
            var nextFrameIndex = Math.Max(lastSeriesFrameIndex, lastFixedFrameIndex) + 1;
            if (point is not null)
            {
                point = point with { FrameIndex = nextFrameIndex };
                series.Add(point);
                RoiVisualizationEngine.ApplyRealtimeRoiFilteringUnsafe(series);
                point = series[^1];
            }

            while (series.Count > SeriesLimit)
            {
                series.RemoveAt(0);
            }

            if (fixedSample is not null)
            {
                var samples = previewState.FixedRoiSamplesBySet.TryGetValue(setLabel, out var existingSamples)
                    ? existingSamples
                    : previewState.FixedRoiSamplesBySet[setLabel] = [];
                fixedSample = fixedSample with { FrameIndex = nextFrameIndex };
                samples.Add(fixedSample);
                TrimFixedSamplesUnsafe(setLabel, samples);
            }

            seriesSnapshot = shouldUpdatePreview ? [.. series] : [];
            fixedSamplesSnapshot = fixedSample is null || !shouldUpdatePreview
                ? []
                : [.. previewState.FixedRoiSamplesBySet[setLabel]];
            previousFixedTemporal = previewState.GetOrCreateUnsafe(setLabel).Roi?.FixedTemporal
                ?? FixedRoiTemporalVisualSnapshot.Empty;
        }

        if (!shouldUpdatePreview)
        {
            return;
        }

        if (fixedSamplesSnapshot.Length > 0)
        {
            var selectedText = point is null
                ? "选中区无重构单元"
                : $"{RoiVisualizationEngine.FormatRoiValueSourceLabel(point.ValueSource)} {point.MeanConductivity:F4} · {point.SelectedCellCount} 单元";
            var fixedSnapshot = new RealtimeRoiPreviewSnapshot(
                null,
                null,
                null,
                [],
                string.Empty,
                string.Empty,
                string.Empty,
                $"ROI 实时：{fixedSamplesSnapshot.Length} 帧 · block {result.BlockNumber} · {selectedText}",
                previousFixedTemporal);
            previewState.PublishRoi(setLabel, callbacks.IsDisplayedSet(setLabel), fixedSnapshot);

            if (callbacks.ShouldUpdateTemporal(state))
            {
                QueueFixedTemporalVisualRebuild(setLabel, fixedSamplesSnapshot, roi, state);
            }

            callbacks.RaiseSaveCanExecute();
            callbacks.RequestPreviewFlush();
            return;
        }

        var chart = RoiVisualizationEngine.BuildRoiCurveChart(seriesSnapshot);
        var snapshot = new RealtimeRoiPreviewSnapshot(
            chart.Geometry,
            chart.RawGeometry,
            chart.NoiseBandGeometry,
            chart.Markers,
            chart.AxisStart,
            chart.AxisMiddle,
            chart.AxisEnd,
            $"ROI 实时：{seriesSnapshot.Length} 帧 · block {result.BlockNumber} · {RoiVisualizationEngine.FormatRoiValueSourceLabel(point!.ValueSource)} {point.MeanConductivity:F4} · {point.SelectedCellCount} 单元{RoiVisualizationEngine.FormatRoiFilterCountSummary(seriesSnapshot)}{RoiVisualizationEngine.FormatRoiNoiseSummary(seriesSnapshot)}",
            FixedRoiTemporalVisualSnapshot.Empty);
        if (roi.Revision != workspace.RoiDefinitionRevision)
        {
            return;
        }

        previewState.PublishRoi(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot);
        callbacks.RaiseSaveCanExecute();
        callbacks.RequestPreviewFlush();
    }

    internal void PublishNeutral(
        string setLabel,
        RealtimeDemodulatedBlock block,
        RealtimeRunState state)
    {
        var geometry = state.RoiGeometry;
        if (geometry is null || geometry.CellConnectivity.GetLength(0) == 0)
        {
            return;
        }

        callbacks.PersistTrustedNeutralEvidence(block, state);

        var neutral = new RealtimeReconstructionResult(
            block.BlockNumber,
            string.Empty,
            Enumerable.Repeat(1.0, geometry.CellConnectivity.GetLength(0)).ToArray(),
            geometry.NodeCoords,
            geometry.CellConnectivity,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            OutputPersisted: false,
            ReconstructionScaleStatus: ReconstructionScale.ModelRelative,
            ReconstructionScaleProvenance: ReconstructionScale.NormalizedModelProvenance);
        PublishMeasurement(setLabel, neutral, block.QualityWeight, state, RoiValueSource.TrustedNeutral);
    }

    private void QueueFixedTemporalVisualRebuild(
        string setLabel,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        RoiSelectionSnapshot roi,
        RealtimeRunState state)
    {
        if (Interlocked.CompareExchange(ref state.FixedRoiTemporalRebuildPending, 1, 0) != 0)
        {
            return;
        }

        var mapMode = workspace.FixedRoiTemporalMapMode;
        var ringNumber = workspace.FixedRoiAngularRingNumber;
        _ = Task.Run(() =>
        {
            try
            {
                var analysis = RoiVisualizationEngine.AnalyzeLatestFixedRoiEpoch(workspace.FixedRoiGrid, samples);
                var visual = FixedRoiTemporalVisualization.Build(
                    workspace.FixedRoiGrid,
                    analysis,
                    roi.FixedCell!,
                    analysis.Frames.Count - 1,
                    ringNumber,
                    mapMode,
                    workspace.RoiImageCanvasSize,
                    VisualizationGeometry.PaddingFor(workspace.RoiImageCanvasSize));
                if (roi.Revision != workspace.RoiDefinitionRevision ||
                    ringNumber != workspace.FixedRoiAngularRingNumber ||
                    !string.Equals(mapMode, workspace.FixedRoiTemporalMapMode, StringComparison.Ordinal))
                {
                    return;
                }

                lock (previewState.Gate)
                {
                    UpdatePinnedFramesUnsafe(setLabel, samples, analysis);
                    var cache = previewState.GetOrCreateUnsafe(setLabel);
                    if (cache.Roi is not { } existing)
                    {
                        return;
                    }

                    cache.Roi = existing with { FixedTemporal = visual };
                    if (callbacks.IsDisplayedSet(setLabel))
                    {
                        previewState.PendingRoiUnsafe = cache.Roi;
                    }
                }

                callbacks.RequestPreviewFlush();
            }
            catch (Exception ex)
            {
                callbacks.Diagnostic($"{setLabel} fixed ROI temporal visualization failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref state.FixedRoiTemporalRebuildPending, 0);
            }
        });
    }

    private void TrimFixedSamplesUnsafe(string setLabel, List<FixedRoiTemporalSample> samples)
    {
        var pinned = previewState.FixedRoiPinnedFramesBySet.TryGetValue(setLabel, out var existing)
            ? existing
            : previewState.FixedRoiPinnedFramesBySet[setLabel] = [];
        foreach (var baseline in samples
                     .Where(sample => sample.MeanConductivity.Any(double.IsFinite))
                     .Take(new FixedRoiTemporalOptions().BaselineFrameCount))
        {
            pinned.Add(baseline.FrameIndex);
        }

        while (samples.Count > SeriesLimit)
        {
            var removeIndex = samples.FindIndex(sample => !pinned.Contains(sample.FrameIndex));
            if (removeIndex < 0)
            {
                removeIndex = Math.Min(new FixedRoiTemporalOptions().BaselineFrameCount, samples.Count - 1);
            }

            samples.RemoveAt(removeIndex);
        }
    }

    private void UpdatePinnedFramesUnsafe(
        string setLabel,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        FixedRoiTemporalAnalysis analysis)
    {
        var pinned = previewState.FixedRoiPinnedFramesBySet.TryGetValue(setLabel, out var existing)
            ? existing
            : previewState.FixedRoiPinnedFramesBySet[setLabel] = [];
        foreach (var baseline in samples
                     .Where(sample => sample.MeanConductivity.Any(double.IsFinite))
                     .Take(analysis.Options.BaselineFrameCount))
        {
            pinned.Add(baseline.FrameIndex);
        }

        foreach (var cell in analysis.Cells)
        {
            if (cell.ArrivalSeriesIndex is not { } arrivalIndex)
            {
                continue;
            }

            for (var offset = 0; offset < analysis.Options.ArrivalConsecutiveFrames; offset++)
            {
                var seriesIndex = arrivalIndex + offset;
                if (seriesIndex < samples.Count)
                {
                    pinned.Add(samples[seriesIndex].FrameIndex);
                }
            }
        }
    }
}
