using System.IO;
using System.Text;
using EitHost.Core.Analysis;
using EitHost.Core.Concurrency;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RoiInteractionCallbacks(
    Func<string?> SelectedSetLabel,
    Func<string, bool> IsDisplayedSet,
    Func<string, string, string, string?> PromptSaveFile,
    Action<string> PublishStatus,
    Action<string> LogExport,
    Action<Action> PostToUi);

internal sealed class RoiInteractionController : IDisposable
{
    private const string FixedNominalMode = "fixed_nominal";
    private readonly VisualizationWorkspaceViewModel workspace;
    private readonly RealtimePreviewStateStore previewState;
    private readonly RealtimePreviewController preview;
    private readonly ReplayVisualizationController replay;
    private readonly string dataRootPath;
    private readonly string sessionDirectory;
    private readonly RoiInteractionCallbacks callbacks;
    private readonly LatestOnlyAsyncWorker<RoiInteractionRebuildRequest> rebuildWorker;
    private long rebuildVersion;

    internal RoiInteractionController(
        VisualizationWorkspaceViewModel workspace,
        RealtimePreviewStateStore previewState,
        RealtimePreviewController preview,
        ReplayVisualizationController replay,
        string dataRootPath,
        string sessionDirectory,
        RoiInteractionCallbacks callbacks)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.previewState = previewState ?? throw new ArgumentNullException(nameof(previewState));
        this.preview = preview ?? throw new ArgumentNullException(nameof(preview));
        this.replay = replay ?? throw new ArgumentNullException(nameof(replay));
        this.dataRootPath = Path.GetFullPath(dataRootPath);
        this.sessionDirectory = Path.GetFullPath(sessionDirectory);
        this.callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
        rebuildWorker = new LatestOnlyAsyncWorker<RoiInteractionRebuildRequest>(
            ProcessRebuildAsync,
            ex => callbacks.PublishStatus($"ROI 交互重建失败：{ex.Message}"));
    }

    internal void HandleDefinitionChanged(bool fixedCellChanged)
    {
        if (fixedCellChanged)
        {
            RebuildFixedSelection();
            return;
        }

        Clear(resetSummary: false);
        replay.InvalidateRoi();
    }

    internal void RebuildFixedSelection()
    {
        QueueRebuild(includeCurve: true);
    }

    internal void RebuildTemporalViews()
    {
        QueueRebuild(includeCurve: false);
    }

    private void QueueRebuild(bool includeCurve)
    {
        var roi = RoiVisualizationEngine.CaptureSelection(workspace) with
        {
            CanvasSize = workspace.RoiImageCanvasSize
        };
        var version = Interlocked.Increment(ref rebuildVersion);
        rebuildWorker.TryPost(new RoiInteractionRebuildRequest(
            version,
            includeCurve,
            roi,
            workspace.FixedRoiGrid,
            workspace.SelectedFixedRoiCell,
            workspace.FixedRoiAngularRingNumber,
            workspace.FixedRoiTemporalMapMode));
    }

    private ValueTask ProcessRebuildAsync(RoiInteractionRebuildRequest request, CancellationToken cancellationToken)
    {
        KeyValuePair<string, FixedRoiTemporalSample[]>[] realtimeSamples;
        lock (previewState.Gate)
        {
            realtimeSamples = previewState.FixedRoiSamplesBySet
                .Where(item => item.Value.Count > 0)
                .Select(item => new KeyValuePair<string, FixedRoiTemporalSample[]>(item.Key, [.. item.Value]))
                .ToArray();
        }

        var rebuilt = new List<KeyValuePair<string, (List<RoiCurvePoint> Series, RealtimeRoiPreviewSnapshot Snapshot)>>();
        foreach (var item in realtimeSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = RoiVisualizationEngine.AnalyzeLatestFixedRoiEpoch(request.Grid, item.Value);
            var series = request.IncludeCurve
                ? RoiVisualizationEngine.CreateFixedRoiCurveSeries(
                request.Grid,
                item.Key,
                item.Value,
                request.Roi)
                : [];
            RoiCurveChart? chart = request.IncludeCurve
                ? RoiVisualizationEngine.BuildRoiCurveChart(series)
                : null;
            var visual = FixedRoiTemporalVisualization.Build(
                request.Grid,
                analysis,
                request.SelectedCell,
                analysis.Frames.Count - 1,
                request.RingNumber,
                request.MapMode,
                request.Roi.CanvasSize,
                VisualizationGeometry.PaddingFor(request.Roi.CanvasSize));
            rebuilt.Add(new KeyValuePair<string, (List<RoiCurvePoint>, RealtimeRoiPreviewSnapshot)>(
                item.Key,
                (series, new RealtimeRoiPreviewSnapshot(
                    chart?.Geometry,
                    chart?.RawGeometry,
                    chart?.NoiseBandGeometry,
                    chart?.Markers ?? [],
                    chart?.AxisStart ?? string.Empty,
                    chart?.AxisMiddle ?? string.Empty,
                    chart?.AxisEnd ?? string.Empty,
                    request.IncludeCurve ? RoiVisualizationEngine.FormatRoiSeriesSummary("ROI 实时", series) : string.Empty,
                    visual))));
        }

        callbacks.PostToUi(() => ApplyRebuild(request, rebuilt));
        return ValueTask.CompletedTask;
    }

    private void ApplyRebuild(
        RoiInteractionRebuildRequest request,
        IReadOnlyList<KeyValuePair<string, (List<RoiCurvePoint> Series, RealtimeRoiPreviewSnapshot Snapshot)>> rebuilt)
    {
        if (request.Version != Volatile.Read(ref rebuildVersion))
        {
            return;
        }

        lock (previewState.Gate)
        {
            foreach (var item in rebuilt)
            {
                var cache = previewState.GetOrCreateUnsafe(item.Key);
                if (request.IncludeCurve)
                {
                    previewState.RoiSeriesBySet[item.Key] = item.Value.Series;
                    cache.Roi = item.Value.Snapshot;
                }
                else if (cache.Roi is { } existing)
                {
                    cache.Roi = existing with { FixedTemporal = item.Value.Snapshot.FixedTemporal };
                }

                if (callbacks.IsDisplayedSet(item.Key))
                {
                    previewState.PendingRoiUnsafe = cache.Roi;
                }
            }
        }

        if (request.IncludeCurve)
        {
            replay.QueueFixedSelectionRebuild();
        }
        else
        {
            replay.QueueTemporalViewRebuild();
        }

        workspace.SaveRoiCurveCommand.RaiseCanExecuteChanged();
        preview.RequestFlush();
    }

    public void Dispose()
    {
        rebuildWorker.Cancel();
        rebuildWorker.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record RoiInteractionRebuildRequest(
        long Version,
        bool IncludeCurve,
        RoiSelectionSnapshot Roi,
        FixedRoiGrid Grid,
        FixedRoiCell SelectedCell,
        int RingNumber,
        string MapMode);

    internal void Save()
    {
        if (workspace.RoiMode == FixedNominalMode && GetCurrentFixedExportSource() is { } fixedSource)
        {
            var isReplayExport = string.Equals(
                fixedSource.Source,
                replay.ReplayExportSource,
                StringComparison.Ordinal);
            var defaultPath = CreateDefaultPath(fixedSource.Source);
            if (callbacks.PromptSaveFile(defaultPath, "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*", ".csv") is not { } path)
            {
                return;
            }

            File.WriteAllText(
                path,
                RoiCsvExporter.BuildFixedTemporal(
                    workspace.FixedRoiGrid,
                    fixedSource.SetLabel,
                    fixedSource.Analyses,
                    isReplayExport ? replay.ReplayLane : null,
                    isReplayExport ? replay.ReplayRevisionId : null),
                Encoding.UTF8);
            var rows = fixedSource.Analyses.Sum(item => item.Frames.Count) * workspace.FixedRoiGrid.Cells.Count;
            callbacks.PublishStatus($"固定 ROI 时空长表已保存：{path}");
            callbacks.LogExport($"{DateTime.Now:HH:mm:ss} fixed ROI temporal CSV saved {rows} rows {path}");
            return;
        }

        var (series, source) = GetCurrentSeriesForSave();
        if (series.Count == 0)
        {
            callbacks.PublishStatus("当前没有可保存的 ROI 曲线。");
            return;
        }

        var curvePath = CreateDefaultPath(source);
        if (callbacks.PromptSaveFile(curvePath, "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*", ".csv") is not { } csvPath)
        {
            return;
        }

        var isReplayCurve = replay.RoiSeries.Count > 0;
        File.WriteAllText(
            csvPath,
            RoiCsvExporter.BuildCurve(
                series,
                isReplayCurve ? replay.ReplayLane : null,
                isReplayCurve ? replay.ReplayRevisionId : null),
            Encoding.UTF8);
        callbacks.PublishStatus($"ROI 曲线已保存：{csvPath}");
        callbacks.LogExport($"{DateTime.Now:HH:mm:ss} ROI curve saved {series.Count} rows {csvPath}");
    }

    internal bool CanSave()
    {
        return replay.RoiSeries.Count > 0 ||
            replay.FixedRoiSamples.Count > 0 ||
            GetSelectedRealtimeSeries().Count > 0 ||
            GetSelectedRealtimeFixedSamples().Count > 0;
    }

    internal void Clear()
    {
        workspace.InvalidateRoiRevision();
        Clear(resetSummary: true);
    }

    private void Clear(bool resetSummary)
    {
        previewState.ClearAllRoi();
        workspace.RealtimeRoiCurveGeometry = null;
        workspace.RealtimeRoiRawCurveGeometry = null;
        workspace.RealtimeRoiNoiseBandGeometry = null;
        workspace.RealtimeRoiMarkers = [];
        workspace.RealtimeRoiAxisStart = string.Empty;
        workspace.RealtimeRoiAxisMiddle = string.Empty;
        workspace.RealtimeRoiAxisEnd = string.Empty;
        workspace.RealtimeFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
        workspace.RealtimeRoiSummary = resetSummary
            ? "ROI：实时曲线已清空，等待下一帧。"
            : "ROI：选区已更新，等待下一帧。";
        workspace.SaveRoiCurveCommand.RaiseCanExecuteChanged();
    }

    private FixedRoiTemporalExportSource? GetCurrentFixedExportSource()
    {
        if (replay.FixedRoiAnalyses.Count > 0)
        {
            return new FixedRoiTemporalExportSource(
                replay.ReplaySetLabel,
                replay.ReplayExportSource,
                replay.FixedRoiAnalyses);
        }

        var samples = GetSelectedRealtimeFixedSamples();
        if (samples.Count == 0)
        {
            return null;
        }

        var setLabel = callbacks.SelectedSetLabel() ?? "realtime";
        return new FixedRoiTemporalExportSource(
            setLabel,
            setLabel,
            RoiVisualizationEngine.AnalyzeFixedRoiEpochSegments(workspace.FixedRoiGrid, samples));
    }

    private (IReadOnlyList<RoiCurvePoint> Series, string Source) GetCurrentSeriesForSave()
    {
        return replay.RoiSeries.Count > 0
            ? (replay.RoiSeries, replay.ReplayExportSource)
            : (GetSelectedRealtimeSeries(), callbacks.SelectedSetLabel() ?? "realtime");
    }

    private IReadOnlyList<RoiCurvePoint> GetSelectedRealtimeSeries()
    {
        var setLabel = callbacks.SelectedSetLabel();
        if (string.IsNullOrWhiteSpace(setLabel))
        {
            return [];
        }

        lock (previewState.Gate)
        {
            return previewState.RoiSeriesBySet.TryGetValue(setLabel, out var series) ? series.ToArray() : [];
        }
    }

    private IReadOnlyList<FixedRoiTemporalSample> GetSelectedRealtimeFixedSamples()
    {
        var setLabel = callbacks.SelectedSetLabel();
        if (string.IsNullOrWhiteSpace(setLabel))
        {
            return [];
        }

        lock (previewState.Gate)
        {
            return previewState.FixedRoiSamplesBySet.TryGetValue(setLabel, out var samples) ? samples.ToArray() : [];
        }
    }

    private string CreateDefaultPath(string source)
    {
        var directory = Directory.Exists(sessionDirectory) ? sessionDirectory : dataRootPath;
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(source.Select(character => invalid.Contains(character) ? '_' : character));
        return Path.Combine(
            directory,
            $"roi_curve_{(string.IsNullOrWhiteSpace(safe) ? "unnamed" : safe)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }
}
