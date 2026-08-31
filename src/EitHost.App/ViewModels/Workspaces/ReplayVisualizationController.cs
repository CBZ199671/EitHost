using System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;
using EitHost.App.ViewModels;
using EitHost.Core.Analysis;
using EitHost.Core.Concurrency;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class ReplayVisualizationController : IDisposable
{
    private const int RealtimeImageElectrodeCount = 16;
    private static readonly TimeSpan ReplayPlaybackInterval = TimeSpan.FromMilliseconds(150);
    private readonly VisualizationWorkspaceViewModel workspace;
    private readonly DataRootLayout dataLayout;
    private readonly Dictionary<Guid, string> imagingRunStorePaths = [];
    private readonly Func<string> imagePolarity;
    private readonly Func<double> imageGain;
    private readonly Action<Action> postToUi;
    private readonly LatestOnlyAsyncWorker<ReplayRoiRebuildRequest> roiRebuildWorker;
    private readonly object replayFrameRequestGate = new();
    private readonly object replayRoiCalculationGate = new();
    private readonly VisualizationRenderer.RealtimeImageRasterCache replayImageRasterCache = new();
    private bool frameStoreReady;
    private ImagingRunDetail? replayRunDetail;
    private IImagingReplaySource? replaySource;
    private IReadOnlyList<ImagingFrameIndexEntry> replayFrames = [];
    private IReadOnlyDictionary<int, string> replayReferenceLockKinds = new Dictionary<int, string>();
    private IReadOnlyDictionary<int, ImagingReferenceEpochRecord> replayReferenceEpochs = new Dictionary<int, ImagingReferenceEpochRecord>();
    private int replayLoadVersion;
    private int replayFrameVersion;
    private ReplayFrameRequest? pendingReplayFrame;
    private TaskCompletionSource<object?>? replayFrameDrain;
    private CancellationTokenSource? replayRoiCalculationCancellation;
    private Task<ReplayRoiCalculationResult>? replayRoiCalculationTask;
    private int replayFrameWorkerActive;
    private int displayedReplayFrameIndex = -1;
    private DispatcherTimer? replayTimer;
    private List<RoiCurvePoint> replayRoiSeries = [];
    private IReadOnlyList<FixedRoiTemporalSample> replayFixedRoiSamples = [];
    private FixedRoiTemporalAnalysis? replayFixedRoiAnalysis;
    private IReadOnlyList<FixedRoiTemporalAnalysis> replayFixedRoiAnalyses = [];
    private long replayCurveRebuildVersion;
    private long replayTemporalRebuildVersion;
    private long replayRoiCalculationVersion;
    private ExperimentRunListItem? selectedCanonicalExperiment;

    internal ReplayVisualizationController(
        VisualizationWorkspaceViewModel workspace,
        DataRootLayout dataLayout,
        CanonicalExperimentReplaySource canonicalSource,
        Func<string> imagePolarity,
        Func<double> imageGain,
        Action<Action> postToUi)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        CanonicalSource = canonicalSource ?? throw new ArgumentNullException(nameof(canonicalSource));
        this.imagePolarity = imagePolarity ?? throw new ArgumentNullException(nameof(imagePolarity));
        this.imageGain = imageGain ?? throw new ArgumentNullException(nameof(imageGain));
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        roiRebuildWorker = new LatestOnlyAsyncWorker<ReplayRoiRebuildRequest>(
            ProcessRoiRebuildAsync,
            ex => PublishDiagnostic($"Replay ROI rebuild failed: {ex}"),
            isNonReplaceable: static request => request.IncludeCurve);
        RefreshCommand = new AsyncRelayCommand(RefreshImagingRunsAsync, () => frameStoreReady);
        TogglePlaybackCommand = new RelayCommand(ToggleReplayPlayback, () => replayFrames.Count > 0);
        ToggleLiveReplayCommand = new RelayCommand(
            () => _ = ToggleCanonicalLanePlaybackAsync(ReconstructionLane.Live),
            () => HasPublishedLane(ReconstructionLane.Live));
        ToggleOfflineReplayCommand = new RelayCommand(
            () => _ = ToggleCanonicalLanePlaybackAsync(ReconstructionLane.OfflineComplete),
            () => HasPublishedLane(ReconstructionLane.OfflineComplete));
        CalculateRoiCommand = new AsyncRelayCommand(CalculateReplayRoiAsync, CanCalculateReplayRoi);
    }

    internal event Action<string>? StatusChanged;
    internal event Action<string>? DiagnosticMessage;
    internal event Action? LegacyRunsChanged;
    internal event Action? ReplayDataChanged;

    internal CanonicalExperimentReplaySource CanonicalSource { get; }
    internal IReadOnlyDictionary<Guid, string> LegacyStorePaths => imagingRunStorePaths;
    internal IReadOnlyList<RoiCurvePoint> RoiSeries => replayRoiSeries;
    internal IReadOnlyList<FixedRoiTemporalSample> FixedRoiSamples => replayFixedRoiSamples;
    internal IReadOnlyList<FixedRoiTemporalAnalysis> FixedRoiAnalyses => replayFixedRoiAnalyses;
    internal string ReplaySetLabel => replayRunDetail?.SetLabel ?? "replay";
    internal string ReplayExportSource => replaySource is ReconstructionLaneReplaySource laneSource
        ? $"{laneSource.Lane}_{laneSource.RevisionId}"
        : "replay";
    internal string? ReplayLane => (replaySource as ReconstructionLaneReplaySource)?.Lane;
    internal string? ReplayRevisionId => (replaySource as ReconstructionLaneReplaySource)?.RevisionId;
    internal AsyncRelayCommand RefreshCommand { get; }
    internal RelayCommand TogglePlaybackCommand { get; }
    internal RelayCommand ToggleLiveReplayCommand { get; }
    internal RelayCommand ToggleOfflineReplayCommand { get; }
    internal AsyncRelayCommand CalculateRoiCommand { get; }
    private string ImagePolarity => imagePolarity();
    private double ImageGain => imageGain();
    private string StatusMessage { set => StatusChanged?.Invoke(value); }

    internal void SetReady(bool ready)
    {
        frameStoreReady = ready;
        RefreshCommand.RaiseCanExecuteChanged();
    }

    internal void ClearExperimentSelection()
    {
        selectedCanonicalExperiment = null;
        workspace.SetReplayLaneAvailability(false, false);
        workspace.SetActiveReplayLane(null, null);
        RaiseLaneCommandAvailability();
        StopPlayback();
        CancelReplayRoiCalculation();
        Clear();
    }

    internal async Task ReleaseExperimentAsync(Guid experimentRunId)
    {
        if (selectedCanonicalExperiment?.ExperimentRunId != experimentRunId &&
            replayRunDetail?.ImagingRunId != experimentRunId)
        {
            return;
        }

        selectedCanonicalExperiment = null;
        workspace.SetReplayLaneAvailability(false, false);
        RaiseLaneCommandAvailability();
        StopPlayback();
        Interlocked.Increment(ref replayLoadVersion);
        Interlocked.Increment(ref replayFrameVersion);
        var drain = CancelAndCaptureReplayWork();
        Clear();
        await DrainReplayWorkAsync(drain).ConfigureAwait(true);
    }

    internal void InvalidateRoi()
    {
        CancelReplayRoiCalculation();
        replayRoiSeries = [];
        replayFixedRoiSamples = [];
        replayFixedRoiAnalysis = null;
        replayFixedRoiAnalyses = [];
        workspace.ReplayFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
        workspace.ReplayRoiCurveGeometry = null;
        workspace.ReplayRoiMarkers = [];
        workspace.ReplayRoiAxisStart = string.Empty;
        workspace.ReplayRoiAxisMiddle = string.Empty;
        workspace.ReplayRoiAxisEnd = string.Empty;
        workspace.ReplayRoiSummary = "ROI：选区已更新，请重新离线计算。";
        ReplayDataChanged?.Invoke();
    }

    internal void QueueFixedSelectionRebuild() => QueueRoiRebuild(includeCurve: true);

    internal void QueueTemporalViewRebuild() => QueueRoiRebuild(includeCurve: false);

    private void QueueRoiRebuild(bool includeCurve)
    {
        var requestedFrameNumber = workspace.ReplayFrameIndex + 1;
        var analysis = replayFixedRoiAnalyses.FirstOrDefault(candidate =>
                candidate.Frames.Any(frame => frame.FrameIndex == requestedFrameNumber))
            ?? replayFixedRoiAnalysis;
        var curveVersion = includeCurve
            ? Interlocked.Increment(ref replayCurveRebuildVersion)
            : Volatile.Read(ref replayCurveRebuildVersion);
        var request = new ReplayRoiRebuildRequest(
            curveVersion,
            Interlocked.Increment(ref replayTemporalRebuildVersion),
            includeCurve,
            RoiVisualizationEngine.CaptureSelection(workspace) with { CanvasSize = workspace.RoiImageCanvasSize },
            workspace.FixedRoiGrid,
            workspace.SelectedFixedRoiCell,
            workspace.FixedRoiAngularRingNumber,
            workspace.FixedRoiTemporalMapMode,
            requestedFrameNumber,
            replayRunDetail?.SetLabel ?? "replay",
            replayFixedRoiSamples.ToArray(),
            analysis);
        roiRebuildWorker.TryPost(request);
    }

    private ValueTask ProcessRoiRebuildAsync(ReplayRoiRebuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<RoiCurvePoint>? series = null;
        RoiCurveChart? chart = null;
        if (request.IncludeCurve && request.Samples.Count > 0)
        {
            series = RoiVisualizationEngine.CreateFixedRoiCurveSeries(
                request.Grid,
                request.SetLabel,
                request.Samples,
                request.Roi);
            chart = RoiVisualizationEngine.BuildRoiCurveChart(series);
        }

        var visual = FixedRoiTemporalVisualSnapshot.Empty;
        if (request.Analysis is { Frames.Count: > 0 } analysis)
        {
            var analysisFrameIndex = Enumerable.Range(0, analysis.Frames.Count)
                .MinBy(index => Math.Abs(analysis.Frames[index].FrameIndex - request.RequestedFrameNumber));
            visual = FixedRoiTemporalVisualization.Build(
                request.Grid,
                analysis,
                request.SelectedCell,
                analysisFrameIndex,
                request.RingNumber,
                request.MapMode,
                request.Roi.CanvasSize,
                VisualizationGeometry.PaddingFor(request.Roi.CanvasSize));
        }

        postToUi(() => ApplyQueuedRoiRebuild(request, series, chart, visual));
        return ValueTask.CompletedTask;
    }

    private void ApplyQueuedRoiRebuild(
        ReplayRoiRebuildRequest request,
        List<RoiCurvePoint>? series,
        RoiCurveChart? chart,
        FixedRoiTemporalVisualSnapshot visual)
    {
        var applyCurve = request.IncludeCurve
            && request.CurveVersion == Volatile.Read(ref replayCurveRebuildVersion);
        var applyTemporal = request.TemporalVersion == Volatile.Read(ref replayTemporalRebuildVersion);
        if (!applyCurve && !applyTemporal)
        {
            return;
        }

        if (applyCurve && series is not null && chart is not null)
        {
            replayRoiSeries = series;
            workspace.ReplayRoiCurveGeometry = chart.Geometry;
            workspace.ReplayRoiMarkers = chart.Markers;
            workspace.ReplayRoiAxisStart = chart.AxisStart;
            workspace.ReplayRoiAxisMiddle = chart.AxisMiddle;
            workspace.ReplayRoiAxisEnd = chart.AxisEnd;
            workspace.ReplayRoiSummary = RoiVisualizationEngine.FormatRoiSeriesSummary("离线 ROI", series);
        }

        if (applyTemporal)
        {
            workspace.ReplayFixedRoiTemporal = visual;
        }

        ReplayDataChanged?.Invoke();
    }

    private void PublishDiagnostic(string message) => DiagnosticMessage?.Invoke(message);

    internal async Task RefreshImagingRunsAsync()
    {
        if (!frameStoreReady)
        {
            StatusMessage = "成像帧库未准备好。";
            return;
        }

        try
        {
            // V414: only discover existing legacy stores; never create or migrate them.
            var runs = await Task.Run(ListSegmentedImagingRuns).ConfigureAwait(true);
            var selectedRunId = workspace.SelectedImagingRun?.Summary.ImagingRunId;
            workspace.ImagingRuns.Clear();
            imagingRunStorePaths.Clear();
            foreach (var run in runs)
            {
                imagingRunStorePaths[run.Summary.ImagingRunId] = run.StorePath;
                workspace.ImagingRuns.Add(new ImagingRunListItem(run.Summary));
            }

            LegacyRunsChanged?.Invoke();

            if (selectedRunId is { } runId)
            {
                var match = workspace.ImagingRuns.FirstOrDefault(item => item.Summary.ImagingRunId == runId);
                if (match is not null)
                {
                    workspace.SetSelectedImagingRun(match, notifySelection: false);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新成像记录失败：{ex.Message}";
        }
    }

    private IReadOnlyList<SegmentedImagingRun> ListSegmentedImagingRuns()
    {
        // V414: current DataRoot plus legacy AppData frame stores are read-only discoverable.
        var paths = dataLayout.EnumerateFrameStorePaths();
        var runs = new List<SegmentedImagingRun>();
        foreach (var path in paths)
        {
            try
            {
                var store = new EitFrameStore(path);
                runs.AddRange(store.ListImagingRuns().Select(summary => new SegmentedImagingRun(path, summary)));
            }
            catch (Exception ex)
            {
                PublishDiagnostic($"frame store segment skipped path={path}: {ex.Message}");
            }
        }

        return runs
            .OrderByDescending(run => run.Summary.StartedAt)
            .ThenByDescending(run => run.Summary.ImagingRunId)
            .Take(100)
            .ToArray();
    }

    internal async Task LoadLegacyRunAsync(ImagingRunListItem? item)
    {
        selectedCanonicalExperiment = null;
        workspace.SetReplayLaneAvailability(false, false);
        workspace.SetActiveReplayLane(null, null);
        RaiseLaneCommandAvailability();
        if (item is null || !frameStoreReady)
        {
            StopPlayback();
            Interlocked.Increment(ref replayLoadVersion);
            Clear();
            return;
        }

        var runId = item.Summary.ImagingRunId;
        if (!imagingRunStorePaths.TryGetValue(runId, out var storePath))
        {
            StopPlayback();
            Interlocked.Increment(ref replayLoadVersion);
            Clear();
            workspace.ReplayRunSummary = $"{item.Title} · 加载失败";
            workspace.ReplayFrameSummary = "旧版回放数据库位置不可用。";
            workspace.ReplayLoadStatus = "回放状态：加载失败 · 旧版数据库位置不可用";
            return;
        }

        await LoadReplaySourceAsync(
            item.Title,
            runId,
            new LegacyEitFrameReplaySource(new EitFrameStore(storePath))).ConfigureAwait(true);
    }

    internal async Task LoadCanonicalExperimentAsync(ExperimentRunListItem item)
    {
        selectedCanonicalExperiment = item;
        if (!item.IsCanonicalTerminal)
        {
            workspace.SetReplayLaneAvailability(false, false);
            RaiseLaneCommandAvailability();
            StopPlayback();
            Interlocked.Increment(ref replayLoadVersion);
            await DrainReplayWorkAsync(CancelAndCaptureReplayWork()).ConfigureAwait(true);
            Clear();
            StatusMessage = string.Equals(
                item.Run?.Status,
                ExperimentCatalog.RecordingStatus,
                StringComparison.Ordinal)
                ? "所选实验仍在记录中，已阻止回放读取；请先停止采集，待实验进入终态后再回放。"
                : $"所选实验状态“{item.Run?.Status ?? "未知"}”不是可回放终态，已阻止回放读取。";
            return;
        }

        var live = CanonicalSource.GetPublishedReconstructionRevision(
            item.ExperimentRunId,
            ReconstructionLane.Live);
        var offline = CanonicalSource.GetPublishedReconstructionRevision(
            item.ExperimentRunId,
            ReconstructionLane.OfflineComplete);
        workspace.SetReplayLaneAvailability(live?.IsComplete == true, offline?.IsComplete == true);
        RaiseLaneCommandAvailability();
        var selected = live?.IsComplete == true ? live : offline?.IsComplete == true ? offline : null;
        if (selected is null)
        {
            StopPlayback();
            Interlocked.Increment(ref replayLoadVersion);
            await DrainReplayWorkAsync(CancelAndCaptureReplayWork()).ConfigureAwait(true);
            Clear();
            workspace.SetActiveReplayLane(null, null);
            workspace.ReplayRunSummary = $"{item.Title} · 尚无已发布回放线路";
            workspace.ReplayFrameSummary = "实时线路只包含采集时已显示并提交的帧；离线线路需手动完整重算后发布。";
            workspace.ReplayLoadStatus = "回放状态：无已发布 live/offline-complete revision";
            return;
        }

        await LoadPublishedLaneAsync(item, selected).ConfigureAwait(true);
    }

    private Task LoadPublishedLaneAsync(
        ExperimentRunListItem item,
        ReconstructionRevisionCatalogRecord revision) =>
        LoadReplaySourceAsync(
            item.Title,
            item.ExperimentRunId,
            CanonicalSource.OpenPublishedReconstructionLane(
                item.ExperimentRunId,
                revision.Lane,
                revision.RevisionId));

    private async Task ToggleCanonicalLanePlaybackAsync(string lane)
    {
        var item = selectedCanonicalExperiment;
        if (item is null)
        {
            return;
        }

        var revision = CanonicalSource.GetPublishedReconstructionRevision(item.ExperimentRunId, lane);
        if (revision?.IsComplete != true)
        {
            StatusMessage = lane == ReconstructionLane.Live
                ? "该实验没有可用的实时回放 revision。"
                : "该实验尚未手动生成并发布离线完整回放。";
            RaiseLaneCommandAvailability();
            return;
        }

        if (replaySource is ReconstructionLaneReplaySource active &&
            string.Equals(active.Lane, lane, StringComparison.Ordinal) &&
            string.Equals(active.RevisionId, revision.RevisionId, StringComparison.Ordinal))
        {
            ToggleReplayPlayback();
            return;
        }

        await LoadPublishedLaneAsync(item, revision).ConfigureAwait(true);
        if (replayFrames.Count > 0)
        {
            ToggleReplayPlayback();
        }
    }

    private bool HasPublishedLane(string lane) =>
        selectedCanonicalExperiment is { } item &&
        CanonicalSource.GetPublishedReconstructionRevision(item.ExperimentRunId, lane)?.IsComplete == true;

    private void RaiseLaneCommandAvailability()
    {
        ToggleLiveReplayCommand.RaiseCanExecuteChanged();
        ToggleOfflineReplayCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadReplaySourceAsync(
        string title,
        Guid runId,
        IImagingReplaySource runSource)
    {
        StopPlayback();
        var version = Interlocked.Increment(ref replayLoadVersion);
        await DrainReplayWorkAsync(CancelAndCaptureReplayWork()).ConfigureAwait(true);
        Clear();
        if (runSource is ReconstructionLaneReplaySource laneSource)
        {
            workspace.SetActiveReplayLane(laneSource.Lane, laneSource.RevisionId);
        }
        else
        {
            workspace.SetActiveReplayLane(null, null);
        }
        workspace.ReplayRunSummary = $"{title} · 正在加载…";
        workspace.ReplayFrameSummary = "正在加载实验记录。";
        workspace.ReplayContactSummary = "接触诊断：等待当前记录帧加载。";
        workspace.ReplayLoadStatus = "回放状态：正在加载实验记录…";
        try
        {
            IReadOnlyList<ImagingReferenceEpochRecord> referenceEpochs = [];
            var (detail, frames) = await Task.Run(() =>
            {
                referenceEpochs = runSource.ListReferenceEpochs(runId);
                return (runSource.GetImagingRunDetail(runId), runSource.ListFrameIndex(runId));
            }).ConfigureAwait(true);
            if (version != Volatile.Read(ref replayLoadVersion))
            {
                return;
            }

            if (detail is null)
            {
                Clear();
                workspace.ReplayRunSummary = $"{title} · 加载失败";
                workspace.ReplayFrameSummary = "该实验记录不存在，未显示重构图像。";
                workspace.ReplayContactSummary = "接触诊断：实验记录不存在。";
                workspace.ReplayLoadStatus = "回放状态：加载失败 · 实验记录不存在";
                return;
            }

            replaySource = runSource;
            replayRunDetail = detail;
            replayFrames = frames;
            workspace.SetReplayFrameCount(frames.Count);
            replayReferenceLockKinds = referenceEpochs.ToDictionary(
                epoch => epoch.ReferenceEpoch,
                epoch => epoch.LockKind);
            replayReferenceEpochs = referenceEpochs.ToDictionary(epoch => epoch.ReferenceEpoch);
            replayRoiSeries = [];
            replayFixedRoiSamples = [];
            replayFixedRoiAnalysis = null;
            replayFixedRoiAnalyses = [];
            workspace.ReplayFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
            workspace.ReplayRoiCurveGeometry = null;
            workspace.ReplayRoiMarkers = [];
            workspace.ReplayRoiAxisStart = string.Empty;
            workspace.ReplayRoiAxisMiddle = string.Empty;
            workspace.ReplayRoiAxisEnd = string.Empty;
            workspace.ReplayRoiSummary = "ROI：已加载记录，点击“计算 ROI”生成曲线。";
            ReplayDataChanged?.Invoke();
            TogglePlaybackCommand.RaiseCanExecuteChanged();
            CalculateRoiCommand.RaiseCanExecuteChanged();
            var ended = detail.EndedAt is { } endedAt ? endedAt.ToLocalTime().ToString("HH:mm:ss") : "进行中";
            var laneSummary = runSource is ReconstructionLaneReplaySource loadedLane
                ? $" · {DescribeLane(loadedLane.Lane)} · revision {loadedLane.RevisionId}"
                : string.Empty;
            workspace.ReplayRunSummary =
                $"{detail.SetLabel} · {detail.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ~ {ended} · {detail.ReconstructionRoute}{laneSummary} · 帧 {frames.Count}";
            if (frames.Count == 0)
            {
                workspace.ReplayFrameSummary = "该记录没有已保存解调块。";
                workspace.ReplayContactSummary = "接触诊断：该记录没有已保存解调块。";
                workspace.ReplayLoadStatus = "回放状态：记录中没有可回放帧";
                return;
            }

            workspace.ResetReplayFrameIndex();
            await ShowReplayFrameAsync(0).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (version != Volatile.Read(ref replayLoadVersion))
            {
                return;
            }

            Clear();
            workspace.ReplayRunSummary = $"{title} · 加载失败";
            workspace.ReplayFrameSummary = $"加载实验记录失败：{ex.Message}";
            workspace.ReplayContactSummary = "接触诊断：实验记录加载失败。";
            workspace.ReplayLoadStatus = $"回放状态：加载实验失败 · {ex.Message}";
            StatusMessage = $"加载实验记录失败：{ex.Message}";
        }
    }

    internal async Task ShowReplayFrameAsync(int index)
    {
        await QueueReplayFrameAsync(index).ConfigureAwait(true);
    }

    private Task QueueReplayFrameAsync(int index)
    {
        TaskCompletionSource<object?> drain;
        lock (replayFrameRequestGate)
        {
            pendingReplayFrame = new ReplayFrameRequest(
                index,
                Interlocked.Increment(ref replayFrameVersion));
            if (replayFrameWorkerActive != 0)
            {
                return replayFrameDrain!.Task;
            }

            replayFrameWorkerActive = 1;
            drain = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            replayFrameDrain = drain;
        }

        _ = ProcessReplayFrameRequestsAsync(drain);
        return drain.Task;
    }

    private async Task ProcessReplayFrameRequestsAsync(TaskCompletionSource<object?> drain)
    {
        try
        {
            while (true)
            {
                ReplayFrameRequest request;
                lock (replayFrameRequestGate)
                {
                    if (pendingReplayFrame is null)
                    {
                        replayFrameWorkerActive = 0;
                        replayFrameDrain = null;
                        break;
                    }

                    request = pendingReplayFrame;
                    pendingReplayFrame = null;
                }

                await LoadReplayFrameAsync(request).ConfigureAwait(true);
            }

            drain.TrySetResult(null);
        }
        catch (Exception ex)
        {
            lock (replayFrameRequestGate)
            {
                pendingReplayFrame = null;
                replayFrameWorkerActive = 0;
                replayFrameDrain = null;
            }

            PublishDiagnostic($"Replay frame worker failed: {ex.Message}");
            drain.TrySetException(ex);
        }
    }

    private async Task LoadReplayFrameAsync(ReplayFrameRequest request)
    {
        var detail = replayRunDetail;
        var frames = replayFrames;
        if (detail is null || frames.Count == 0)
        {
            return;
        }

        var index = request.Index;
        var entry = frames[Math.Clamp(index, 0, frames.Count - 1)];
        var version = request.Version;
        var displayIndex = Math.Clamp(index, 0, frames.Count - 1) + 1;
        var committed = false;
        // V292: keep the last coherent frame visible while the requested frame is
        // read. Requested identity lives in this separate status slot until every
        // visual and summary for the new frame can be committed in one UI turn.
        workspace.ReplayLoadStatus =
            $"回放状态：正在读取 · 帧 {displayIndex}/{frames.Count} · block {entry.BlockNumber}";
        try
        {
            var runSource = replaySource ?? throw new InvalidOperationException("回放数据源未加载。");
            var frame = await Task.Run(() => runSource.GetFrame(detail.ImagingRunId, entry.BlockNumber)).ConfigureAwait(true);
            if (version != Volatile.Read(ref replayFrameVersion))
            {
                return;
            }

            if (frame is null)
            {
                RestoreDisplayedReplayFrameIndex();
                workspace.ReplayLoadStatus =
                    $"回放状态：帧 {displayIndex}/{frames.Count} · block {entry.BlockNumber} · 该帧记录不存在，保留上一完整帧";
                return;
            }

            var nextCurveGeometry = CreateSeriesGeometry(frame.MeanAmplitude208);
            var replayContactStates = ParseReplayElectrodeStates(frame.ElectrodeStates);
            var presentation = ReadPresentation(frame.ReconstructionPresentationJson);
            var neutral = frame.ReconstructionFrameOutcome is not null &&
                frame.ReconstructionFrameOutcome != ReconstructionFrameOutcome.Reconstructed ||
                string.Equals(presentation?.OverlayDisposition, "neutral", StringComparison.Ordinal);
            ImageSource? nextImageSource;
            string reconText;
            string roiText;
            if (neutral)
            {
                var imagePixelSize = VisualizationGeometry.ClampImagePixelSize(workspace.RoiImageCanvasSize);
                nextImageSource = await Task.Run(() =>
                    replayImageRasterCache.RenderNeutral(replayContactStates, imagePixelSize)).ConfigureAwait(true);
                reconText = frame.ReconstructionFrameOutcome == ReconstructionFrameOutcome.Neutral
                    ? "中性帧（未执行逆问题）"
                    : $"已排除：{frame.ReconstructionExclusionReason ?? frame.ReconstructionFrameOutcome}";
                roiText = "ROI 无重构结果";
            }
            else if (frame.Conductivity is { Length: > 0 } conductivity
                && detail.NodeCoords is { } nodes
                && detail.CellConnectivity is { } cells)
            {
                var result = new RealtimeReconstructionResult(
                    frame.BlockNumber,
                    string.Empty,
                    conductivity,
                    nodes,
                    cells,
                    frame.CapturedAt,
                    TimeSpan.Zero,
                    OutputPersisted: false,
                    ReconstructionScaleStatus: detail.ReconstructionScaleStatus,
                    ReconstructionScaleProvenance: detail.ReconstructionScaleProvenance,
                    MeshIndexMetadata: ReconstructionMeshIndexMetadata.FromPersisted(
                        detail.MeshIndexSchema,
                        detail.ReconstructionParameterEntity,
                        detail.LogicalMeshFingerprint,
                        detail.OrderedIndexFingerprint,
                        detail.MeshCoordinateDecimals,
                        detail.MeshCoordinateQuantizationStep));
                var polarity = presentation?.Polarity ?? ImagePolarity;
                var gain = presentation?.Gain ?? ImageGain;
                var imagePixelSize = VisualizationGeometry.ClampImagePixelSize(workspace.RoiImageCanvasSize);
                nextImageSource = await Task.Run(() =>
                    presentation is { ScaleCenter: { } center, ScaleRange: { } range } && range > 0.0
                        ? replayImageRasterCache.RenderWithPersistedPresentation(
                            result,
                            polarity,
                            gain,
                            center,
                            range,
                            replayContactStates,
                            imagePixelSize)
                        : VisualizationRenderer.RenderReconstructionImageCached(
                            result,
                            polarity,
                            gain,
                            replayContactStates,
                            replayImageRasterCache,
                            imagePixelSize)).ConfigureAwait(true);
                if (version != Volatile.Read(ref replayFrameVersion))
                {
                    return;
                }
                reconText =
                    $"{ReconstructionScale.ToDisplayLabel(detail.ReconstructionScaleStatus)} " +
                    $"{conductivity.Min():F4} ~ {conductivity.Max():F4}";
                var roiPoint = RoiVisualizationEngine.CreateRoiCurvePoint(
                    detail.SetLabel,
                    Math.Clamp(index, 0, frames.Count - 1) + 1,
                    frame.BlockNumber,
                    frame.CapturedAt,
                    frame.QualityWeight,
                    frame.ReferenceEpoch,
                    RoiVisualizationEngine.ResolveReferenceLockKind(frame.ReferenceEpoch, replayReferenceLockKinds),
                    conductivity,
                    nodes,
                    cells,
                    RoiVisualizationEngine.CaptureSelection(workspace),
                    detail.ReconstructionParameterEntity);
                roiText = roiPoint is null
                    ? "ROI 无单元"
                    : $"ROI 重构值 {roiPoint.MeanConductivity:F4} ({roiPoint.SelectedCellCount} 单元)";
            }
            else
            {
                // B177/V292: a successfully read frame without conductivity commits
                // an empty image, but the old image stays visible until this point.
                nextImageSource = null;
                reconText = "该帧无重构结果";
                roiText = "ROI 无重构结果";
            }

            var totalFrames = frame.AcceptedFrames + frame.RejectedFrames;
            var replayReference = frame.ReferenceEpoch is { } epoch
                ? $"e{epoch}/{RoiVisualizationEngine.ResolveReferenceLockKind(frame.ReferenceEpoch, replayReferenceLockKinds)}"
                : "未记录/legacy_unknown";
            var replayActionAudit = frame.ReferenceEpoch is { } auditEpoch
                && replayReferenceEpochs.TryGetValue(auditEpoch, out var epochRecord)
                && epochRecord.ActionGroupId is { Length: > 0 } actionGroupId
                    ? $" · action {actionGroupId[..Math.Min(8, actionGroupId.Length)]} · " +
                      $"窗口 skew {epochRecord.WindowSkewMilliseconds.GetValueOrDefault() / 1000.0:+0.000;-0.000;0.000}s · " +
                      $"switch skew {epochRecord.SwitchSkewMilliseconds.GetValueOrDefault() / 1000.0:+0.000;-0.000;0.000}s"
                    : string.Empty;
            var nextFrameSummary =
                $"帧 {displayIndex}/{frames.Count} · block {frame.BlockNumber} · {frame.CapturedAt.ToLocalTime():HH:mm:ss.fff} · " +
                $"线路 {DescribeLane(frame.ReconstructionLane)} · outcome {frame.ReconstructionFrameOutcome ?? "legacy"} · " +
                $"参考 {replayReference}{replayActionAudit} · 质量 {frame.QualityWeight:F2} ({frame.AcceptedFrames}/{totalFrames}) · {reconText} · {roiText}";
            var nextContactSummary = FormatReplayContactSummary(frame, replayContactStates);

            displayedReplayFrameIndex = Math.Clamp(index, 0, frames.Count - 1);
            workspace.CommitReplayFramePresentation(
                displayedReplayFrameIndex,
                nextImageSource,
                nextCurveGeometry,
                nextFrameSummary,
                nextContactSummary,
                $"回放状态：已就绪 · 帧 {displayIndex}/{frames.Count} · block {frame.BlockNumber}");
            committed = true;
            QueueTemporalViewRebuild();
        }
        catch (Exception ex)
        {
            if (version != Volatile.Read(ref replayFrameVersion))
            {
                return;
            }

            if (!committed)
            {
                RestoreDisplayedReplayFrameIndex();
            }

            workspace.ReplayLoadStatus =
                $"回放状态：读取帧失败 · {ex.Message} · {(committed ? "当前帧已提交" : "保留上一完整帧")}";
        }
    }

    private static ElectrodeContactState[]? ParseReplayElectrodeStates(string[]? states)
    {
        if (states is not { Length: RealtimeImageElectrodeCount })
        {
            return null;
        }

        var parsed = new ElectrodeContactState[RealtimeImageElectrodeCount];
        for (var index = 0; index < states.Length; index++)
        {
            if (!Enum.TryParse(states[index], ignoreCase: true, out ElectrodeContactState state))
            {
                return null;
            }

            parsed[index] = state;
        }

        return parsed;
    }

    private static ReconstructionFramePresentation? ReadPresentation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReconstructionFramePresentation>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeLane(string? lane) => lane switch
    {
        ReconstructionLane.Live => "实时",
        ReconstructionLane.OfflineComplete => "离线完整",
        _ => "旧版"
    };

    private static string FormatReplayContactSummary(
        ImagingFrameDetail frame,
        IReadOnlyList<ElectrodeContactState>? states)
    {
        var imageQuality = frame.ImageQualityScore is { } quality
            ? $"Q={quality:F2}"
            : "Q=未记录";
        var conditionText = frame.ReconstructionConditionNumber is { } conditionNumber
            ? $"κ={conditionNumber:G3}"
            : "κ=未记录";
        var activeWeights = frame.MeasurementWeight208?.Count(weight => weight > 0.0);
        var weightText = activeWeights is { } count
            ? $"权重 {count}/{EitFrameStore.BoundaryVectorLength}"
            : "权重未记录";
        var stateText = states is null
            ? "状态未记录"
            : $"绿{states.Count(state => state == ElectrodeContactState.Green)} 黄{states.Count(state => state == ElectrodeContactState.Yellow)} 红{states.Count(state => state == ElectrodeContactState.Red)} 深红{states.Count(state => state == ElectrodeContactState.DarkRed)} 系统{states.Count(state => state == ElectrodeContactState.SystemLevel)}";
        var referenceText = frame.ReferenceInvalidated
            ? "参考帧失效"
            : string.IsNullOrWhiteSpace(frame.ReferenceStatus)
                ? "参考状态未记录"
                : $"参考={frame.ReferenceStatus}";
        var contactSummary = string.IsNullOrWhiteSpace(frame.ContactSummary)
            ? "无诊断摘要"
            : frame.ContactSummary.Trim();
        var displayCompensation = string.IsNullOrWhiteSpace(frame.DisplayCompensationPolicy)
            ? "模板显示=未记录"
            : frame.DisplayCompensationOnly
                ? $"模板显示={frame.DisplayCompensationPolicy} display-only"
                : $"模板显示={frame.DisplayCompensationPolicy}";
        var dynamicKalman = string.IsNullOrWhiteSpace(frame.DynamicKalmanAction)
            ? "Kalman=未记录"
            : $"Kalman={frame.DynamicKalmanMode ?? "fast_image"}/{frame.DynamicKalmanAction} NIS={frame.DynamicKalmanNisPerDof.GetValueOrDefault():F2} K={frame.DynamicKalmanGainMean.GetValueOrDefault():F2} solve={frame.DynamicKalmanSolveMilliseconds.GetValueOrDefault():F0}ms L={frame.DynamicKalmanTotalLatencyFrames}{(frame.DynamicKalmanFallback == true ? " fallback" : string.Empty)} · backend={frame.ReconstructionBackendElapsedMilliseconds.GetValueOrDefault():F0}ms";

        return $"接触诊断：{imageQuality} · {conditionText} · {weightText} · {stateText} · {referenceText} · {displayCompensation} · {dynamicKalman} · {contactSummary}";
    }

    public void SetRoiCenterFromImagePoint(double x, double y, double width, double height)
    {
        workspace.SetRoiCenterFromImagePoint(x, y, width, height);
    }

    private async Task CalculateReplayRoiAsync()
    {
        var detail = replayRunDetail;
        var frames = replayFrames.ToArray();
        if (detail?.NodeCoords is null || detail.CellConnectivity is null || frames.Length == 0)
        {
            workspace.ReplayRoiSummary = "ROI：当前成像记录缺少网格或帧。";
            return;
        }

        var roi = RoiVisualizationEngine.CaptureSelection(workspace);
        var runSource = replaySource;
        if (runSource is null)
        {
            workspace.ReplayRoiSummary = "ROI：当前回放数据源未加载。";
            return;
        }

        var calculationVersion = Interlocked.Increment(ref replayRoiCalculationVersion);
        using var cancellation = new CancellationTokenSource();
        var progress = new Progress<ReplayRoiProgress>(update =>
        {
            if (calculationVersion != Volatile.Read(ref replayRoiCalculationVersion) ||
                roi.Revision != workspace.RoiDefinitionRevision)
            {
                return;
            }

            workspace.ReplayRoiSummary =
                $"ROI：正在{update.Phase} {update.CompletedFrameCount}/{update.TotalFrameCount} 帧…";
            StatusMessage = workspace.ReplayRoiSummary;
            ReplayDataChanged?.Invoke();
        });
        workspace.ReplayRoiSummary = $"ROI：正在读取 0/{frames.Length} 帧…";
        StatusMessage = workspace.ReplayRoiSummary;
        ReplayDataChanged?.Invoke();
        try
        {
            var calculationTask = Task.Run(
                () => CalculateReplayRoiSeries(
                    runSource,
                    detail,
                    frames,
                    roi,
                    replayReferenceLockKinds,
                    progress,
                    cancellation.Token),
                cancellation.Token);
            lock (replayRoiCalculationGate)
            {
                replayRoiCalculationCancellation = cancellation;
                replayRoiCalculationTask = calculationTask;
            }

            var calculation = await calculationTask.ConfigureAwait(true);
            if (calculationVersion != Volatile.Read(ref replayRoiCalculationVersion) ||
                roi.Revision != workspace.RoiDefinitionRevision)
            {
                return;
            }

            replayRoiSeries = calculation.Series;
            replayFixedRoiSamples = calculation.FixedSamples;
            replayFixedRoiAnalyses = RoiVisualizationEngine.AnalyzeFixedRoiEpochSegments(workspace.FixedRoiGrid, calculation.FixedSamples);
            replayFixedRoiAnalysis = replayFixedRoiAnalyses.LastOrDefault();
            ApplyReplayRoiChart(calculation.Series);
            RebuildTemporalView();
            workspace.ReplayRoiSummary = RoiVisualizationEngine.FormatRoiSeriesSummary("离线 ROI", calculation.Series);
            ReplayDataChanged?.Invoke();
            StatusMessage = $"ROI 离线计算完成：{calculation.Series.Count} 帧。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (calculationVersion == Volatile.Read(ref replayRoiCalculationVersion))
            {
                workspace.ReplayRoiSummary = "ROI：计算已取消。";
                StatusMessage = workspace.ReplayRoiSummary;
                ReplayDataChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            workspace.ReplayRoiSummary = $"ROI 离线计算失败：{ex.Message}";
            StatusMessage = workspace.ReplayRoiSummary;
        }
        finally
        {
            lock (replayRoiCalculationGate)
            {
                if (ReferenceEquals(replayRoiCalculationCancellation, cancellation))
                {
                    replayRoiCalculationCancellation = null;
                    replayRoiCalculationTask = null;
                }
            }
        }
    }

    private bool CanCalculateReplayRoi()
    {
        return replayRunDetail?.NodeCoords is not null
            && replayRunDetail.CellConnectivity is not null
            && replayFrames.Count > 0;
    }

    private ReplayRoiCalculationResult CalculateReplayRoiSeries(
        IImagingReplaySource runSource,
        ImagingRunDetail detail,
        IReadOnlyList<ImagingFrameIndexEntry> frames,
        RoiSelectionSnapshot roi,
        IReadOnlyDictionary<int, string> referenceLockKinds,
        IProgress<ReplayRoiProgress>? progress,
        CancellationToken cancellationToken)
    {
        var points = new List<RoiCurvePoint>(frames.Count);
        var fixedSamples = new List<FixedRoiTemporalSample>(frames.Count);
        var paddingFraction = VisualizationGeometry.ImagePaddingFraction;
        var fixedCellIndex = roi.FixedCell is null
            ? -1
            : workspace.FixedRoiGrid.Cells
                .Select((cell, index) => (cell, index))
                .First(item => string.Equals(item.cell.Id, roi.FixedCell.Id, StringComparison.Ordinal))
                .index;
        IReadOnlyDictionary<int, ReconstructionLaneRoiFrame>? laneFrames = null;
        if (runSource is ReconstructionLaneReplaySource laneSource)
        {
            var laneProgress = new CallbackProgress<ReconstructionLaneRoiReadProgress>(update =>
                progress?.Report(new ReplayRoiProgress(
                    "读取",
                    update.CompletedFrameCount,
                    update.TotalFrameCount)));
            laneFrames = laneSource.ReadRoiFrames(
                detail.ImagingRunId,
                detail,
                frames.Select(frame => frame.BlockNumber).ToArray(),
                laneProgress,
                cancellationToken).FramesByBlock;
        }

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = frames[index];
            double[]? conductivity;
            int? referenceEpoch;
            DateTimeOffset capturedAt;
            double qualityWeight;
            if (laneFrames is not null)
            {
                if (!laneFrames.TryGetValue(entry.BlockNumber, out var laneFrame))
                {
                    ReportReplayRoiProgress(progress, "计算", index + 1, frames.Count);
                    continue;
                }

                conductivity = laneFrame.Conductivity;
                referenceEpoch = laneFrame.ReferenceEpoch;
                capturedAt = entry.CapturedAt;
                qualityWeight = entry.QualityWeight;
            }
            else
            {
                var frame = runSource.GetFrame(detail.ImagingRunId, entry.BlockNumber);
                if (frame?.Conductivity is not { Length: > 0 } frameConductivity)
                {
                    ReportReplayRoiProgress(progress, "计算", index + 1, frames.Count);
                    continue;
                }

                conductivity = frameConductivity;
                referenceEpoch = frame.ReferenceEpoch;
                capturedAt = frame.CapturedAt;
                qualityWeight = frame.QualityWeight;
            }

            if (conductivity is not { Length: > 0 })
            {
                continue;
            }

            RoiCurvePoint? point;
            if (fixedCellIndex >= 0)
            {
                var measurements = RoiConductivityAnalyzer.MeasureAll(
                    workspace.FixedRoiGrid,
                    detail.NodeCoords!,
                    detail.CellConnectivity!,
                    conductivity,
                    paddingFraction,
                    detail.ReconstructionParameterEntity);
                fixedSamples.Add(FixedRoiTemporalSample.FromMeasurements(
                    index + 1,
                    entry.BlockNumber,
                    capturedAt,
                    qualityWeight,
                    measurements,
                    referenceEpoch,
                    RoiVisualizationEngine.ResolveReferenceLockKind(referenceEpoch, referenceLockKinds)));
                point = RoiVisualizationEngine.CreateRoiCurvePointFromMeasurement(
                    detail.SetLabel,
                    index + 1,
                    entry.BlockNumber,
                    capturedAt,
                    qualityWeight,
                    referenceEpoch,
                    RoiVisualizationEngine.ResolveReferenceLockKind(referenceEpoch, referenceLockKinds),
                    measurements[fixedCellIndex],
                    roi);
            }
            else
            {
                point = RoiVisualizationEngine.CreateRoiCurvePoint(
                    detail.SetLabel,
                    index + 1,
                    entry.BlockNumber,
                    capturedAt,
                    qualityWeight,
                    referenceEpoch,
                    RoiVisualizationEngine.ResolveReferenceLockKind(referenceEpoch, referenceLockKinds),
                    conductivity,
                    detail.NodeCoords!,
                    detail.CellConnectivity!,
                    roi,
                    detail.ReconstructionParameterEntity);
            }
            if (point is not null)
            {
                points.Add(point);
            }

            ReportReplayRoiProgress(progress, "计算", index + 1, frames.Count);
        }

        return new ReplayRoiCalculationResult(points, fixedSamples);
    }

    internal void RebuildTemporalView()
    {
        var requestedFrameNumber = workspace.ReplayFrameIndex + 1;
        var analysis = replayFixedRoiAnalyses.FirstOrDefault(candidate =>
                candidate.Frames.Any(frame => frame.FrameIndex == requestedFrameNumber))
            ?? replayFixedRoiAnalysis;
        if (analysis is null || analysis.Frames.Count == 0)
        {
            workspace.ReplayFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
            return;
        }

        var analysisFrameIndex = Enumerable.Range(0, analysis.Frames.Count)
            .MinBy(index => Math.Abs(analysis.Frames[index].FrameIndex - requestedFrameNumber));
        workspace.ReplayFixedRoiTemporal = FixedRoiTemporalVisualization.Build(
            workspace.FixedRoiGrid,
            analysis,
            workspace.SelectedFixedRoiCell,
            analysisFrameIndex,
            workspace.FixedRoiAngularRingNumber,
            workspace.FixedRoiTemporalMapMode,
            workspace.RoiImageCanvasSize,
            VisualizationGeometry.PaddingFor(workspace.RoiImageCanvasSize));
    }

    private void ApplyReplayRoiChart(IReadOnlyList<RoiCurvePoint> series)
    {
        var chart = RoiVisualizationEngine.BuildRoiCurveChart(series);
        workspace.ReplayRoiCurveGeometry = chart.Geometry;
        workspace.ReplayRoiMarkers = chart.Markers;
        workspace.ReplayRoiAxisStart = chart.AxisStart;
        workspace.ReplayRoiAxisMiddle = chart.AxisMiddle;
        workspace.ReplayRoiAxisEnd = chart.AxisEnd;
    }

    private static void ReportReplayRoiProgress(
        IProgress<ReplayRoiProgress>? progress,
        string phase,
        int completed,
        int total)
    {
        if (progress is not null && (completed == total || completed % 16 == 0))
        {
            progress.Report(new ReplayRoiProgress(phase, completed, total));
        }
    }

    private void ToggleReplayPlayback()
    {
        if (workspace.IsReplayPlaying)
        {
            StopPlayback();
            return;
        }

        if (replayFrames.Count == 0)
        {
            return;
        }

        replayTimer ??= CreateReplayTimer();
        replayTimer.Start();
        workspace.IsReplayPlaying = true;
    }

    private DispatcherTimer CreateReplayTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ReplayPlaybackInterval
        };
        timer.Tick += (_, _) =>
        {
            if (replayFrames.Count == 0)
            {
                StopPlayback();
                return;
            }

            if (Volatile.Read(ref replayFrameWorkerActive) != 0)
            {
                return;
            }

            workspace.ReplayFrameIndex = workspace.ReplayFrameIndex >= workspace.ReplayMaxFrameIndex ? 0 : workspace.ReplayFrameIndex + 1;
        };
        return timer;
    }

    internal void StopPlayback()
    {
        replayTimer?.Stop();
        workspace.IsReplayPlaying = false;
    }

    internal void Clear()
    {
        Interlocked.Increment(ref replayFrameVersion);
        lock (replayFrameRequestGate)
        {
            pendingReplayFrame = null;
        }

        replayImageRasterCache.ResetColorScale();
        replaySource = null;
        workspace.SetActiveReplayLane(null, null);
        replayRunDetail = null;
        replayFrames = [];
        workspace.SetReplayFrameCount(0);
        replayReferenceLockKinds = new Dictionary<int, string>();
        replayReferenceEpochs = new Dictionary<int, ImagingReferenceEpochRecord>();
        workspace.ResetReplayFrameIndex();
        TogglePlaybackCommand.RaiseCanExecuteChanged();
        CalculateRoiCommand.RaiseCanExecuteChanged();
        workspace.ReplayImageSource = null;
        workspace.ReplayCurveGeometry = null;
        workspace.ReplayRoiCurveGeometry = null;
        workspace.ReplayRoiMarkers = [];
        workspace.ReplayRoiAxisStart = string.Empty;
        workspace.ReplayRoiAxisMiddle = string.Empty;
        workspace.ReplayRoiAxisEnd = string.Empty;
        workspace.ReplayRoiSummary = "ROI：选择成像记录后可离线计算。";
        replayRoiSeries = [];
        replayFixedRoiSamples = [];
        replayFixedRoiAnalysis = null;
        replayFixedRoiAnalyses = [];
        workspace.ReplayFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
        ReplayDataChanged?.Invoke();
        workspace.ReplayRunSummary = string.Empty;
        workspace.ReplayFrameSummary = "选择成像记录后可逐帧回放。";
        workspace.ReplayContactSummary = "接触诊断：选择成像帧后显示。";
        workspace.ReplayLoadStatus = "回放状态：等待选择实验";
        displayedReplayFrameIndex = -1;
    }

    private (Task FrameDrain, Task RoiDrain) CancelAndCaptureReplayWork()
    {
        Interlocked.Increment(ref replayRoiCalculationVersion);
        Task frameDrain;
        lock (replayFrameRequestGate)
        {
            pendingReplayFrame = null;
            frameDrain = replayFrameDrain?.Task ?? Task.CompletedTask;
        }

        Task roiDrain;
        lock (replayRoiCalculationGate)
        {
            replayRoiCalculationCancellation?.Cancel();
            roiDrain = replayRoiCalculationTask ?? Task.CompletedTask;
        }

        return (frameDrain, roiDrain);
    }

    private void CancelReplayRoiCalculation()
    {
        Interlocked.Increment(ref replayRoiCalculationVersion);
        lock (replayRoiCalculationGate)
        {
            replayRoiCalculationCancellation?.Cancel();
        }
    }

    private async Task DrainReplayWorkAsync((Task FrameDrain, Task RoiDrain) drain)
    {
        try
        {
            await Task.WhenAll(drain.FrameDrain, drain.RoiDrain).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected lane/run/delete hand-off path.
        }
        catch (Exception ex)
        {
            PublishDiagnostic($"Replay reader drain completed with an error: {ex.Message}");
        }
    }

    private void RestoreDisplayedReplayFrameIndex()
    {
        if (displayedReplayFrameIndex >= 0)
        {
            workspace.RestoreReplayFrameIndex(displayedReplayFrameIndex);
        }
    }

    private static Geometry? CreateSeriesGeometry(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
        {
            return null;
        }

        var min = finite.Min();
        var max = finite.Max();
        if (Math.Abs(max - min) < 1.0e-12)
        {
            min -= 1.0;
            max += 1.0;
        }

        const double width = VisualizationGeometry.DefaultPlotCanvasWidth;
        const double height = 220.0;
        const double padding = 14.0;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var started = false;
            for (var index = 0; index < values.Count; index++)
            {
                if (!double.IsFinite(values[index]))
                {
                    started = false;
                    continue;
                }

                var x = padding + (values.Count == 1 ? 0.0 : index * (width - (2 * padding)) / (values.Count - 1));
                var y = height - padding - ((values[index] - min) * (height - (2 * padding)) / (max - min));
                var point = new System.Windows.Point(x, y);
                if (!started)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    started = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public void Dispose()
    {
        replayTimer?.Stop();
        CancelReplayRoiCalculation();
        roiRebuildWorker.Cancel();
        roiRebuildWorker.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed record ReplayFrameRequest(int Index, int Version);

    private sealed record ReplayRoiProgress(
        string Phase,
        int CompletedFrameCount,
        int TotalFrameCount);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed record ReplayRoiRebuildRequest(
        long CurveVersion,
        long TemporalVersion,
        bool IncludeCurve,
        RoiSelectionSnapshot Roi,
        FixedRoiGrid Grid,
        FixedRoiCell SelectedCell,
        int RingNumber,
        string MapMode,
        int RequestedFrameNumber,
        string SetLabel,
        IReadOnlyList<FixedRoiTemporalSample> Samples,
        FixedRoiTemporalAnalysis? Analysis);

    private sealed record SegmentedImagingRun(string StorePath, ImagingRunSummary Summary);
}
