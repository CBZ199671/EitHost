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

    internal void PublishProvisionalUnavailableIfCurrent(
        string setLabel,
        RealtimeRunState state,
        int dynamicGeneration,
        int referenceEpoch)
    {
        const string summary = "ROI：快速预览参考仅供观察；定量曲线等待正式参考稳定。";
        var snapshot = CreateUnavailableSnapshot(summary);
        var displayed = callbacks.IsDisplayedSet(setLabel);
        if (!state.TryCommitReconstructionState(
                dynamicGeneration,
                referenceEpoch,
                () =>
                {
                    previewState.PublishRoi(setLabel, displayed, snapshot);
                    callbacks.PublishReadiness(
                        setLabel,
                        $"ROI 就绪：否 · {summary.Replace("ROI：", string.Empty, StringComparison.Ordinal)}");
                }))
        {
            return;
        }

        callbacks.RequestPreviewFlush();
    }

    internal void PublishUnavailable(string setLabel, string summary)
    {
        var snapshot = CreateUnavailableSnapshot(summary);
        previewState.PublishRoi(setLabel, callbacks.IsDisplayedSet(setLabel), snapshot);
        callbacks.PublishReadiness(
            setLabel,
            $"ROI 就绪：否 · {summary.Replace("ROI：", string.Empty, StringComparison.Ordinal)}");
        callbacks.RequestPreviewFlush();
    }

    private static RealtimeRoiPreviewSnapshot CreateUnavailableSnapshot(string summary) =>
        new(
            null,
            null,
            null,
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            summary,
            FixedRoiTemporalVisualSnapshot.Empty);

    internal void PublishMeasurement(
        string setLabel,
        RealtimeReconstructionResult result,
        double qualityWeight,
        RealtimeRunState state,
        int referenceEpoch,
        int dynamicGeneration,
        string referenceLockKind,
        string valueSource = RoiValueSource.InverseReconstruction)
    {
        if (!state.IsReconstructionContextCurrent(dynamicGeneration, referenceEpoch))
        {
            return;
        }

        if (!string.Equals(valueSource, RoiValueSource.TrustedNeutral, StringComparison.Ordinal))
        {
            FlushPendingNeutralMeasurements(setLabel, state);
        }

        PublishMeasurementCore(
            setLabel,
            result,
            qualityWeight,
            state,
            valueSource,
            referenceEpoch > 0 ? referenceEpoch : null,
            referenceLockKind,
            dynamicGeneration);
    }

    private void PublishMeasurementCore(
        string setLabel,
        RealtimeReconstructionResult result,
        double qualityWeight,
        RealtimeRunState state,
        string valueSource,
        int? referenceEpoch,
        string referenceLockKind,
        int? expectedDynamicGeneration = null)
    {
        bool IsCurrentResult() => expectedDynamicGeneration is null ||
            referenceEpoch is { } epoch &&
            state.IsReconstructionContextCurrent(expectedDynamicGeneration.Value, epoch);

        if (!IsCurrentResult())
        {
            return;
        }

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
                referenceEpoch,
                referenceLockKind);
            point = RoiVisualizationEngine.CreateRoiCurvePointFromMeasurement(
                setLabel,
                0,
                result.BlockNumber,
                result.CompletedAt,
                qualityWeight,
                referenceEpoch,
                referenceLockKind,
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
                referenceEpoch,
                referenceLockKind,
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
        if (!IsCurrentResult())
        {
            return;
        }

        if (point is null && fixedSample is null)
        {
            var displayed = callbacks.IsDisplayedSet(setLabel);
            if (!TryCommitResultState(
                    state,
                    expectedDynamicGeneration,
                    referenceEpoch,
                    () =>
                    {
                        previewState.PublishRoi(
                            setLabel,
                            displayed,
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
                        callbacks.PublishReadiness(setLabel, "ROI 就绪：是 · 当前参考 epoch 正常发布");
                    }))
            {
                return;
            }

            callbacks.RequestPreviewFlush();
            return;
        }

        var shouldUpdatePreview = callbacks.ShouldUpdatePreview(state);
        RoiCurvePoint[] seriesSnapshot = [];
        FixedRoiTemporalSample[] fixedSamplesSnapshot = [];
        FixedRoiTemporalVisualSnapshot previousFixedTemporal = FixedRoiTemporalVisualSnapshot.Empty;
        var revisionCurrent = true;
        if (!TryCommitResultState(
                state,
                expectedDynamicGeneration,
                referenceEpoch,
                () =>
                {
                    lock (previewState.Gate)
                    {
                        if (roi.Revision != workspace.RoiDefinitionRevision)
                        {
                            revisionCurrent = false;
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
                }))
        {
            return;
        }
        if (!revisionCurrent)
        {
            return;
        }

        if (!shouldUpdatePreview)
        {
            return;
        }
        if (!IsCurrentResult())
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
            var displayed = callbacks.IsDisplayedSet(setLabel);
            if (!TryCommitResultState(
                    state,
                    expectedDynamicGeneration,
                    referenceEpoch,
                    () =>
                    {
                        previewState.PublishRoi(setLabel, displayed, fixedSnapshot);
                        callbacks.PublishReadiness(setLabel, "ROI 就绪：是 · 当前参考 epoch 正常发布");
                    }))
            {
                return;
            }

            if (callbacks.ShouldUpdateTemporal(state))
            {
                QueueFixedTemporalVisualRebuild(
                    setLabel,
                    fixedSamplesSnapshot,
                    roi,
                    state,
                    expectedDynamicGeneration,
                    referenceEpoch);
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
        if (!IsCurrentResult())
        {
            return;
        }

        var chartDisplayed = callbacks.IsDisplayedSet(setLabel);
        if (!TryCommitResultState(
                state,
                expectedDynamicGeneration,
                referenceEpoch,
                () =>
                {
                    previewState.PublishRoi(setLabel, chartDisplayed, snapshot);
                    callbacks.PublishReadiness(setLabel, "ROI 就绪：是 · 当前参考 epoch 正常发布");
                }))
        {
            return;
        }

        callbacks.RaiseSaveCanExecute();
        callbacks.RequestPreviewFlush();
    }

    internal void PublishNeutral(
        string setLabel,
        RealtimeDemodulatedBlock block,
        RealtimeRunState state)
    {
        callbacks.PersistTrustedNeutralEvidence(block, state);

        var sample = new RealtimeNeutralRoiSample(
            block.BlockNumber,
            block.QualityWeight,
            DateTimeOffset.UtcNow,
            state.ReferenceEpoch > 0 ? state.ReferenceEpoch : null,
            state.ActiveReferenceLockKind);
        var geometry = state.RoiGeometry;
        if (geometry is null || geometry.CellConnectivity.GetLength(0) == 0)
        {
            lock (state.PendingNeutralRoiGate)
            {
                state.PendingNeutralRoiSamples.Add(sample);
                while (state.PendingNeutralRoiSamples.Count > SeriesLimit)
                {
                    state.PendingNeutralRoiSamples.RemoveAt(0);
                }
            }

            return;
        }

        FlushPendingNeutralMeasurements(setLabel, state);
        PublishNeutralMeasurement(setLabel, sample, state, geometry);
    }

    private void FlushPendingNeutralMeasurements(string setLabel, RealtimeRunState state)
    {
        var geometry = state.RoiGeometry;
        if (geometry is null || geometry.CellConnectivity.GetLength(0) == 0)
        {
            return;
        }

        RealtimeNeutralRoiSample[] pending;
        lock (state.PendingNeutralRoiGate)
        {
            if (state.PendingNeutralRoiSamples.Count == 0)
            {
                return;
            }

            pending = [.. state.PendingNeutralRoiSamples];
            state.PendingNeutralRoiSamples.Clear();
        }

        foreach (var sample in pending)
        {
            PublishNeutralMeasurement(setLabel, sample, state, geometry);
        }
    }

    private void PublishNeutralMeasurement(
        string setLabel,
        RealtimeNeutralRoiSample sample,
        RealtimeRunState state,
        RealtimeRoiGeometry geometry)
    {
        var valueCount = string.Equals(
            geometry.MeshIndexMetadata.ParameterEntity,
            ReconstructionParameterEntity.Node,
            StringComparison.Ordinal)
                ? geometry.NodeCoords.GetLength(0)
                : geometry.CellConnectivity.GetLength(0);

        var neutral = new RealtimeReconstructionResult(
            sample.BlockNumber,
            string.Empty,
            Enumerable.Repeat(1.0, valueCount).ToArray(),
            geometry.NodeCoords,
            geometry.CellConnectivity,
            sample.ObservedAt,
            TimeSpan.Zero,
            OutputPersisted: false,
            ReconstructionScaleStatus: ReconstructionScale.ModelRelative,
            ReconstructionScaleProvenance: ReconstructionScale.NormalizedModelProvenance,
            MeshIndexMetadata: geometry.MeshIndexMetadata);
        PublishMeasurementCore(
            setLabel,
            neutral,
            sample.QualityWeight,
            state,
            RoiValueSource.TrustedNeutral,
            sample.ReferenceEpoch,
            sample.ReferenceLockKind);
    }

    private void QueueFixedTemporalVisualRebuild(
        string setLabel,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        RoiSelectionSnapshot roi,
        RealtimeRunState state,
        int? expectedDynamicGeneration,
        int? referenceEpoch)
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

                var published = false;
                if (!TryCommitResultState(
                        state,
                        expectedDynamicGeneration,
                        referenceEpoch,
                        () =>
                        {
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

                                published = true;
                            }
                        }))
                {
                    return;
                }

                if (published)
                {
                    callbacks.RequestPreviewFlush();
                }
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

    private static bool TryCommitResultState(
        RealtimeRunState state,
        int? expectedDynamicGeneration,
        int? referenceEpoch,
        Action commit)
    {
        if (expectedDynamicGeneration is null || referenceEpoch is null)
        {
            commit();
            return true;
        }

        return state.TryCommitReconstructionState(
            expectedDynamicGeneration.Value,
            referenceEpoch.Value,
            commit);
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
