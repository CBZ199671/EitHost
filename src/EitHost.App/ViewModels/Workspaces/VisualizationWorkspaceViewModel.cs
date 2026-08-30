using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using EitHost.Core.Analysis;
using EitHost.Core.Application.Visualization;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels.Workspaces;

public sealed class VisualizationWorkspaceViewModel
    : WorkspaceViewModelBase, IVisualizationWorkspaceViewModel
{
    // ROI overlay bounds are proportions of the image square, not absolute pixels, so a larger
    // surface offers a proportionally larger ROI rather than the same small one.
    private const double RoiMinSizeFraction = 24.0 / VisualizationGeometry.DefaultImagePixelSize;
    private const double RoiMaxSizeFraction = 260.0 / VisualizationGeometry.DefaultImagePixelSize;
    private const double DefaultRoiSizeFraction = 96.0 / VisualizationGeometry.DefaultImagePixelSize;
    private const string RoiModeCustom = "custom";
    private const string RoiModeFixedNominal = "fixed_nominal";
    private const string RoiShapeSquare = "square";
    private const string RoiShapeCircle = "circle";
    private const string RoiPresetSmall = "small";
    private const string RoiPresetMedium = "medium";
    private const string RoiPresetLarge = "large";
    private const string RoiPresetCustom = "custom";

    private readonly FixedRoiGrid fixedRoiGrid = new();
    private Geometry fixedRoiGridGeometry;
    private ImagingRunListItem? selectedImagingRun;
    private int replayFrameIndex;
    private int replayFrameCount;
    private bool isReplayPlaying;
    private string? activeReplayLane;
    private string? activeReplayRevisionId;
    private bool hasLiveReplay;
    private bool hasOfflineCompleteReplay;
    private ImageSource? replayImageSource;
    private Geometry? replayCurveGeometry;
    private FixedRoiTemporalVisualSnapshot replayFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
    private Geometry? replayRoiCurveGeometry;
    private IReadOnlyList<RoiCurveMarker> replayRoiMarkers = [];
    private string replayRoiAxisStart = string.Empty;
    private string replayRoiAxisMiddle = string.Empty;
    private string replayRoiAxisEnd = string.Empty;
    private string replayFrameSummary = "选择成像记录后可逐帧回放。";
    private string replayContactSummary = "接触诊断：选择成像帧后显示。";
    private string replayLoadStatus = "回放状态：等待选择实验";
    private string replayRunSummary = string.Empty;
    private string replayRoiSummary = "ROI：选择成像记录后可离线计算。";
    private string roiMode = RoiModeCustom;
    private FixedRoiCell selectedFixedRoiCell;
    private int roiDefinitionRevision;
    private string roiShape = RoiShapeSquare;
    private string roiSizePreset = RoiPresetMedium;
    private double roiCenterX = 0.5;
    private double roiCenterY = 0.5;
    private double roiSizePixels = 96.0;
    private double roiImageCanvasSize = VisualizationGeometry.DefaultImagePixelSize;
    private string fixedRoiTemporalMapMode = FixedRoiTemporalVisualization.ActivityMapMode;
    private int fixedRoiAngularRingNumber = 5;
    private FixedRoiTemporalVisualSnapshot realtimeFixedRoiTemporal = FixedRoiTemporalVisualSnapshot.Empty;
    private RealtimeRoiController? realtimeRoiController;
    private RealtimePreviewController? realtimePreviewController;
    private RoiInteractionController? roiInteractionController;
    private string realtimeRoiSummary = "ROI：等待重构图像。";
    private Geometry? realtimeRoiCurveGeometry;
    private Geometry? realtimeRoiRawCurveGeometry;
    private Geometry? realtimeRoiNoiseBandGeometry;
    private IReadOnlyList<RoiCurveMarker> realtimeRoiMarkers = [];
    private string realtimeRoiAxisStart = string.Empty;
    private string realtimeRoiAxisMiddle = string.Empty;
    private string realtimeRoiAxisEnd = string.Empty;
    private string realtimeImageStats = "重构图像：无。";
    private string realtimeReconstructionActivity = "重构状态：等待开始";
    private string realtimeRawWaveStats = "等待采集数据";
    private string realtimeDemodStats = "等待解调数据";
    private IReadOnlyList<RealtimeDemodulationAxisTick> realtimeDemodYAxisTicks = [];
    private Geometry? realtimeDemodGridGeometry;
    private Geometry? realtimeDemodZeroLineGeometry;
    private string realtimeBoundaryStats = "等待边界电压";
    private string realtimeBoundaryYAxisTop = string.Empty;
    private string realtimeBoundaryYAxisMiddle = string.Empty;
    private string realtimeBoundaryYAxisBottom = string.Empty;
    private Geometry? realtimeRawChannel1Geometry;
    private Geometry? realtimeRawChannel2Geometry;
    private Geometry? realtimeDemodPrimaryGeometry;
    private Geometry? realtimeDemodSecondaryGeometry;
    private Geometry? realtimeBoundaryTargetGeometry;
    private Geometry? realtimeBoundaryReferenceGeometry;
    private Geometry? realtimeBoundaryTemplateGeometry;
    private ImageSource? realtimeImageSource;
    private VisualizationWorkspaceSnapshot stateSnapshot = VisualizationWorkspaceSnapshot.Empty;

    public VisualizationWorkspaceViewModel()
        : base("visualization")
    {
        selectedFixedRoiCell = fixedRoiGrid.CenterCell;
        fixedRoiGridGeometry = CreateFixedRoiGridGeometry(fixedRoiGrid);
        RealtimePreviewPresenter = new RealtimePreviewPresenter(this);
    }

    internal RealtimePreviewStateStore RealtimePreviewState { get; } = new();

    internal RealtimePreviewPresenter RealtimePreviewPresenter { get; }

    internal RealtimeRoiController RealtimeRoiController =>
        realtimeRoiController ?? throw new InvalidOperationException("Realtime ROI controller has not been attached.");

    internal RealtimePreviewController RealtimePreviewController =>
        realtimePreviewController ?? throw new InvalidOperationException("Realtime preview controller has not been attached.");

    internal RoiInteractionController RoiInteractionController =>
        roiInteractionController ?? throw new InvalidOperationException("ROI interaction controller has not been attached.");

    internal RealtimePreviewPump RealtimePreviewPump { get; private set; } = null!;

    internal BufferedAcquisitionPreviewPump BufferedAcquisitionPreviewPump { get; private set; } = null!;

    internal void AttachRealtimePreviewPump(RealtimePreviewPump pump)
    {
        RealtimePreviewPump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    internal void AttachBufferedAcquisitionPreviewPump(BufferedAcquisitionPreviewPump pump)
    {
        BufferedAcquisitionPreviewPump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    internal void AttachRealtimeRoiController(RealtimeRoiController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (realtimeRoiController is not null)
        {
            throw new InvalidOperationException("Realtime ROI controller is already attached.");
        }

        realtimeRoiController = controller;
    }

    internal void AttachRealtimePreviewController(RealtimePreviewController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (realtimePreviewController is not null)
        {
            throw new InvalidOperationException("Realtime preview controller is already attached.");
        }

        realtimePreviewController = controller;
    }

    internal void AttachRoiInteractionController(RoiInteractionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (roiInteractionController is not null)
        {
            throw new InvalidOperationException("ROI interaction controller is already attached.");
        }

        roiInteractionController = controller;
    }

    public event Action<ImagingRunListItem?>? SelectedImagingRunChanged;

    public event Action<int>? ReplayFrameIndexChanged;

    public event Action<bool>? RoiDefinitionChanged;

    public event Action? TemporalViewOptionsChanged;

    public event Action<VisualizationWorkspaceSnapshot>? StateChanged;

    public VisualizationWorkspaceSnapshot StateSnapshot
    {
        get => stateSnapshot;
        private set => SetProperty(ref stateSnapshot, value);
    }

    public ObservableCollection<ImagingRunListItem> ImagingRuns { get; } = [];

    public ImagingRunListItem? SelectedImagingRun
    {
        get => selectedImagingRun;
        set => SetSelectedImagingRun(value, notifySelection: true);
    }

    public int ReplayFrameIndex
    {
        get => replayFrameIndex;
        set => SetReplayFrameIndex(value, notifySelection: true);
    }

    public int ReplayMaxFrameIndex => Math.Max(0, replayFrameCount - 1);

    public bool HasReplayFrames => replayFrameCount > 0;

    public ImageSource? ReplayImageSource
    {
        get => replayImageSource;
        internal set
        {
            if (SetProperty(ref replayImageSource, value))
            {
                PublishStateSnapshot();
            }
        }
    }

    public Geometry? ReplayCurveGeometry
    {
        get => replayCurveGeometry;
        internal set => SetProperty(ref replayCurveGeometry, value);
    }

    public FixedRoiTemporalVisualSnapshot ReplayFixedRoiTemporal
    {
        get => replayFixedRoiTemporal;
        internal set
        {
            if (SetProperty(ref replayFixedRoiTemporal, value))
            {
                SynchronizeFixedRoiMapCells(ReplayFixedRoiMapCells, value.MapCells);
            }
        }
    }

    public ObservableCollection<FixedRoiMapCellVisual> ReplayFixedRoiMapCells { get; } = [];

    public Geometry? ReplayRoiCurveGeometry
    {
        get => replayRoiCurveGeometry;
        internal set => SetProperty(ref replayRoiCurveGeometry, value);
    }

    public IReadOnlyList<RoiCurveMarker> ReplayRoiMarkers
    {
        get => replayRoiMarkers;
        internal set => SetProperty(ref replayRoiMarkers, value);
    }

    public string ReplayRoiAxisStart
    {
        get => replayRoiAxisStart;
        internal set => SetProperty(ref replayRoiAxisStart, value);
    }

    public string ReplayRoiAxisMiddle
    {
        get => replayRoiAxisMiddle;
        internal set => SetProperty(ref replayRoiAxisMiddle, value);
    }

    public string ReplayRoiAxisEnd
    {
        get => replayRoiAxisEnd;
        internal set => SetProperty(ref replayRoiAxisEnd, value);
    }

    public string ReplayFrameSummary
    {
        get => replayFrameSummary;
        internal set
        {
            if (SetProperty(ref replayFrameSummary, value))
            {
                PublishStateSnapshot();
            }
        }
    }

    public string ReplayContactSummary
    {
        get => replayContactSummary;
        internal set => SetProperty(ref replayContactSummary, value);
    }

    public string ReplayLoadStatus
    {
        get => replayLoadStatus;
        internal set => SetProperty(ref replayLoadStatus, value);
    }

    public string ReplayRunSummary
    {
        get => replayRunSummary;
        internal set => SetProperty(ref replayRunSummary, value);
    }

    public string ReplayRoiSummary
    {
        get => replayRoiSummary;
        internal set
        {
            if (SetProperty(ref replayRoiSummary, value))
            {
                PublishStateSnapshot();
            }
        }
    }

    public bool IsReplayPlaying
    {
        get => isReplayPlaying;
        internal set
        {
            if (SetProperty(ref isReplayPlaying, value))
            {
                OnPropertyChanged(nameof(ReplayPlayButtonText));
                OnPropertyChanged(nameof(LiveReplayButtonText));
                OnPropertyChanged(nameof(OfflineReplayButtonText));
                PublishStateSnapshot();
            }
        }
    }

    public string ReplayPlayButtonText => IsReplayPlaying ? "暂停" : "播放";

    public string LiveReplayButtonText =>
        IsReplayPlaying && ActiveReplayLane == ReconstructionLane.Live ? "暂停实时回放" : "实时回放";

    public string OfflineReplayButtonText =>
        IsReplayPlaying && ActiveReplayLane == ReconstructionLane.OfflineComplete ? "暂停离线回放" : "离线回放";

    public string? ActiveReplayLane
    {
        get => activeReplayLane;
        private set
        {
            if (SetProperty(ref activeReplayLane, value))
            {
                OnPropertyChanged(nameof(LiveReplayButtonText));
                OnPropertyChanged(nameof(OfflineReplayButtonText));
                OnPropertyChanged(nameof(ReplayLaneSummary));
            }
        }
    }

    public string? ActiveReplayRevisionId
    {
        get => activeReplayRevisionId;
        private set
        {
            if (SetProperty(ref activeReplayRevisionId, value))
            {
                OnPropertyChanged(nameof(ReplayLaneSummary));
            }
        }
    }

    public bool HasLiveReplay
    {
        get => hasLiveReplay;
        private set => SetProperty(ref hasLiveReplay, value);
    }

    public bool HasOfflineCompleteReplay
    {
        get => hasOfflineCompleteReplay;
        private set => SetProperty(ref hasOfflineCompleteReplay, value);
    }

    public string ReplayLaneSummary => ActiveReplayLane switch
    {
        ReconstructionLane.Live => $"线路：实时回放 · revision {ActiveReplayRevisionId}",
        ReconstructionLane.OfflineComplete => $"线路：离线完整回放 · revision {ActiveReplayRevisionId}",
        _ => "线路：旧版/未选择"
    };

    /// <summary>
    /// Live edge of the square conductivity surface. The container reports its measured size and
    /// every overlay, the renderer and ROI hit-testing follow this one value.
    /// </summary>
    public double RoiImageCanvasSize
    {
        get => roiImageCanvasSize;
        private set
        {
            var previous = roiImageCanvasSize;
            if (!SetProperty(ref roiImageCanvasSize, value))
            {
                return;
            }

            // The ROI edge is stored in surface pixels, so it is rescaled with the surface.
            // Leaving it absolute would shrink or grow the measured region relative to the mesh
            // and silently move the measurement onto a different amount of tissue.
            if (previous > 0.0 && value > 0.0)
            {
                roiSizePixels = ClampRoiSizePixels(roiSizePixels * (value / previous));
            }

            OnPropertyChanged(nameof(RoiOverlayLeft));
            OnPropertyChanged(nameof(RoiOverlayTop));
            OnPropertyChanged(nameof(RoiOverlaySize));
            OnPropertyChanged(nameof(RoiSizePixels));

            // The ring grid is drawn in surface coordinates, so it is rebuilt rather than scaled.
            fixedRoiGridGeometry = CreateFixedRoiGridGeometry(fixedRoiGrid);
            OnPropertyChanged(nameof(FixedRoiGridGeometry));
            OnPropertyChanged(nameof(FixedRoiSelectionGeometry));
            UpdateFixedRoiMapCellGeometry(RealtimeFixedRoiMapCells);
            UpdateFixedRoiMapCellGeometry(ReplayFixedRoiMapCells);
            SurfaceGeometryChanged?.Invoke();
        }
    }

    /// <summary>Raised after any surface resize so dependent geometry is rebuilt once.</summary>
    public event Action? SurfaceGeometryChanged;

    /// <summary>
    /// Applies a measured container edge for the square conductivity surface.
    ///
    /// Degenerate measurements are ignored rather than treated as a request for the default: a
    /// collapsed page reports zero while laying out, and the visible page's surface must survive
    /// that.
    /// </summary>
    public void ApplyImageSurfaceSize(double measuredEdge)
    {
        if (double.IsNaN(measuredEdge) || double.IsInfinity(measuredEdge) || measuredEdge <= 0.0)
        {
            return;
        }

        RoiImageCanvasSize = VisualizationGeometry.ClampImagePixelSize(measuredEdge);
    }

    public string RoiMode
    {
        get => roiMode;
        set
        {
            if (SetProperty(ref roiMode, NormalizeRoiMode(value)))
            {
                PublishRoiDefinitionChanged(fixedCellChanged: false);
            }
        }
    }

    public string RoiShape
    {
        get => roiShape;
        set
        {
            if (SetProperty(ref roiShape, NormalizeRoiShape(value)))
            {
                PublishRoiDefinitionChanged(fixedCellChanged: false);
            }
        }
    }

    public string RoiSizePreset
    {
        get => roiSizePreset;
        set
        {
            var normalized = NormalizeRoiSizePreset(value);
            if (!SetProperty(ref roiSizePreset, normalized))
            {
                return;
            }

            if (TryGetRoiPresetSize(normalized, out var size))
            {
                SetRoiSizePixels(size, updatePreset: false);
            }
            else
            {
                OnPropertyChanged(nameof(RoiPositionSummary));
            }
        }
    }

    public double RoiSizePixels
    {
        get => roiSizePixels;
        set => SetRoiSizePixels(value, updatePreset: true);
    }

    public double RoiCenterXPercent
    {
        get => roiCenterX * 100.0;
        set
        {
            if (SetProperty(ref roiCenterX, ClampRoiCenter(value / 100.0), nameof(RoiCenterXPercent)))
            {
                PublishRoiDefinitionChanged(fixedCellChanged: false);
            }
        }
    }

    public double RoiCenterYPercent
    {
        get => roiCenterY * 100.0;
        set
        {
            if (SetProperty(ref roiCenterY, ClampRoiCenter(value / 100.0), nameof(RoiCenterYPercent)))
            {
                PublishRoiDefinitionChanged(fixedCellChanged: false);
            }
        }
    }

    public double RoiOverlayLeft => Math.Clamp(
        (roiCenterX * RoiImageCanvasSize) - (roiSizePixels / 2.0),
        0.0,
        Math.Max(0.0, RoiImageCanvasSize - roiSizePixels));

    public double RoiOverlayTop => Math.Clamp(
        (roiCenterY * RoiImageCanvasSize) - (roiSizePixels / 2.0),
        0.0,
        Math.Max(0.0, RoiImageCanvasSize - roiSizePixels));

    public double RoiOverlaySize => roiSizePixels;

    public Visibility RoiCustomControlsVisibility => roiMode == RoiModeCustom
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RoiFixedGridVisibility => roiMode == RoiModeFixedNominal
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RoiSquareVisibility => roiMode == RoiModeCustom && roiShape == RoiShapeSquare
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility RoiCircleVisibility => roiMode == RoiModeCustom && roiShape == RoiShapeCircle
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Geometry FixedRoiGridGeometry => fixedRoiGridGeometry;

    public Geometry FixedRoiSelectionGeometry => FixedRoiTemporalVisualization.CreateCellGeometry(
        selectedFixedRoiCell,
        RoiImageCanvasSize,
        VisualizationGeometry.PaddingFor(RoiImageCanvasSize));

    public string SelectedFixedRoiId => selectedFixedRoiCell.Id;

    public string FixedRoiResolutionProfileId => fixedRoiGrid.ResolutionProfile.Id;

    public string FixedRoiResolutionNotice =>
        "D/10 仅为名义分析网格；电极 1 位于正上方，2→16 与固定 ROI 扇区均按逆时针编号。相邻激励—相邻测量时中心分辨率更低，相邻 ROI 可能串扰。";

    public string FixedRoiTemporalMapMode
    {
        get => fixedRoiTemporalMapMode;
        set
        {
            var normalized = string.Equals(
                value?.Trim(),
                FixedRoiTemporalVisualization.ArrivalMapMode,
                StringComparison.OrdinalIgnoreCase)
                ? FixedRoiTemporalVisualization.ArrivalMapMode
                : FixedRoiTemporalVisualization.ActivityMapMode;
            if (SetProperty(ref fixedRoiTemporalMapMode, normalized))
            {
                TemporalViewOptionsChanged?.Invoke();
            }
        }
    }

    public int FixedRoiAngularRingNumber
    {
        get => fixedRoiAngularRingNumber;
        set
        {
            if (SetProperty(ref fixedRoiAngularRingNumber, Math.Clamp(value, 1, fixedRoiGrid.RingCount)))
            {
                TemporalViewOptionsChanged?.Invoke();
            }
        }
    }

    public FixedRoiTemporalVisualSnapshot RealtimeFixedRoiTemporal
    {
        get => realtimeFixedRoiTemporal;
        internal set
        {
            if (SetProperty(ref realtimeFixedRoiTemporal, value))
            {
                SynchronizeFixedRoiMapCells(RealtimeFixedRoiMapCells, value.MapCells);
            }
        }
    }

    public ObservableCollection<FixedRoiMapCellVisual> RealtimeFixedRoiMapCells { get; } = [];

    public string RoiPositionSummary
    {
        get
        {
            if (roiMode == RoiModeFixedNominal)
            {
                var region = selectedFixedRoiCell.IsCenter
                    ? "中心圆盘"
                    : $"扇区 {selectedFixedRoiCell.SectorNumber}/{selectedFixedRoiCell.SectorCount}";
                return $"固定 ROI {selectedFixedRoiCell.Id} · 第 {selectedFixedRoiCell.RingNumber}/{fixedRoiGrid.RingCount} 环 · {region}";
            }

            return $"{FormatRoiShapeLabel(roiShape)} · 中心 {roiCenterX * 100.0:F0}%, {roiCenterY * 100.0:F0}% · 尺寸 {roiSizePixels:F0}px";
        }
    }

    public string RealtimeRoiSummary
    {
        get => realtimeRoiSummary;
        internal set => SetProperty(ref realtimeRoiSummary, value);
    }

    public Geometry? RealtimeRoiCurveGeometry
    {
        get => realtimeRoiCurveGeometry;
        internal set => SetProperty(ref realtimeRoiCurveGeometry, value);
    }

    public Geometry? RealtimeRoiRawCurveGeometry
    {
        get => realtimeRoiRawCurveGeometry;
        internal set => SetProperty(ref realtimeRoiRawCurveGeometry, value);
    }

    public Geometry? RealtimeRoiNoiseBandGeometry
    {
        get => realtimeRoiNoiseBandGeometry;
        internal set => SetProperty(ref realtimeRoiNoiseBandGeometry, value);
    }

    public IReadOnlyList<RoiCurveMarker> RealtimeRoiMarkers
    {
        get => realtimeRoiMarkers;
        internal set => SetProperty(ref realtimeRoiMarkers, value);
    }

    public string RealtimeRoiAxisStart
    {
        get => realtimeRoiAxisStart;
        internal set => SetProperty(ref realtimeRoiAxisStart, value);
    }

    public string RealtimeRoiAxisMiddle
    {
        get => realtimeRoiAxisMiddle;
        internal set => SetProperty(ref realtimeRoiAxisMiddle, value);
    }

    public string RealtimeRoiAxisEnd
    {
        get => realtimeRoiAxisEnd;
        internal set => SetProperty(ref realtimeRoiAxisEnd, value);
    }

    public string RealtimeImageStats
    {
        get => realtimeImageStats;
        internal set => SetProperty(ref realtimeImageStats, value);
    }

    public string RealtimeReconstructionActivity
    {
        get => realtimeReconstructionActivity;
        internal set => SetProperty(ref realtimeReconstructionActivity, value);
    }

    public string RealtimeRawWaveStats
    {
        get => realtimeRawWaveStats;
        internal set => SetProperty(ref realtimeRawWaveStats, value);
    }

    public string RealtimeDemodStats
    {
        get => realtimeDemodStats;
        internal set => SetProperty(ref realtimeDemodStats, value);
    }

    public IReadOnlyList<RealtimeDemodulationAxisTick> RealtimeDemodYAxisTicks
    {
        get => realtimeDemodYAxisTicks;
        internal set => SetProperty(ref realtimeDemodYAxisTicks, value);
    }

    public Geometry? RealtimeDemodGridGeometry
    {
        get => realtimeDemodGridGeometry;
        internal set => SetProperty(ref realtimeDemodGridGeometry, value);
    }

    public Geometry? RealtimeDemodZeroLineGeometry
    {
        get => realtimeDemodZeroLineGeometry;
        internal set => SetProperty(ref realtimeDemodZeroLineGeometry, value);
    }

    public string RealtimeBoundaryStats
    {
        get => realtimeBoundaryStats;
        internal set => SetProperty(ref realtimeBoundaryStats, value);
    }

    public string RealtimeBoundaryYAxisTop
    {
        get => realtimeBoundaryYAxisTop;
        internal set => SetProperty(ref realtimeBoundaryYAxisTop, value);
    }

    public string RealtimeBoundaryYAxisMiddle
    {
        get => realtimeBoundaryYAxisMiddle;
        internal set => SetProperty(ref realtimeBoundaryYAxisMiddle, value);
    }

    public string RealtimeBoundaryYAxisBottom
    {
        get => realtimeBoundaryYAxisBottom;
        internal set => SetProperty(ref realtimeBoundaryYAxisBottom, value);
    }

    public Geometry? RealtimeRawChannel1Geometry
    {
        get => realtimeRawChannel1Geometry;
        internal set => SetProperty(ref realtimeRawChannel1Geometry, value);
    }

    public Geometry? RealtimeRawChannel2Geometry
    {
        get => realtimeRawChannel2Geometry;
        internal set => SetProperty(ref realtimeRawChannel2Geometry, value);
    }

    public Geometry? RealtimeDemodPrimaryGeometry
    {
        get => realtimeDemodPrimaryGeometry;
        internal set => SetProperty(ref realtimeDemodPrimaryGeometry, value);
    }

    public Geometry? RealtimeDemodSecondaryGeometry
    {
        get => realtimeDemodSecondaryGeometry;
        internal set => SetProperty(ref realtimeDemodSecondaryGeometry, value);
    }

    public Geometry? RealtimeBoundaryTargetGeometry
    {
        get => realtimeBoundaryTargetGeometry;
        internal set => SetProperty(ref realtimeBoundaryTargetGeometry, value);
    }

    public Geometry? RealtimeBoundaryReferenceGeometry
    {
        get => realtimeBoundaryReferenceGeometry;
        internal set => SetProperty(ref realtimeBoundaryReferenceGeometry, value);
    }

    public Geometry? RealtimeBoundaryTemplateGeometry
    {
        get => realtimeBoundaryTemplateGeometry;
        internal set => SetProperty(ref realtimeBoundaryTemplateGeometry, value);
    }

    public ImageSource? RealtimeImageSource
    {
        get => realtimeImageSource;
        internal set
        {
            if (SetProperty(ref realtimeImageSource, value))
            {
                PublishStateSnapshot();
            }
        }
    }

    public IReadOnlyList<SelectionOption> RoiShapeOptions { get; } =
    [
        new("方形", RoiShapeSquare),
        new("圆形", RoiShapeCircle)
    ];

    public IReadOnlyList<SelectionOption> RoiModeOptions { get; } =
    [
        new("自定义 ROI", RoiModeCustom),
        new("固定 D/10", RoiModeFixedNominal)
    ];

    public IReadOnlyList<SelectionOption> FixedRoiTemporalMapModeOptions { get; } =
    [
        new("动态变化", FixedRoiTemporalVisualization.ActivityMapMode),
        new("到达时间", FixedRoiTemporalVisualization.ArrivalMapMode)
    ];

    public IReadOnlyList<int> FixedRoiAngularRingOptions { get; } = [1, 2, 3, 4, 5];

    public IReadOnlyList<SelectionOption> RoiSizePresetOptions { get; } =
    [
        new("中", RoiPresetMedium),
        new("小", RoiPresetSmall),
        new("大", RoiPresetLarge),
        new("自定义", RoiPresetCustom)
    ];

    public AsyncRelayCommand RefreshImagingRunsCommand { get; private set; } = null!;

    public RelayCommand ToggleReplayPlaybackCommand { get; private set; } = null!;

    public RelayCommand ToggleLiveReplayCommand { get; private set; } = null!;

    public RelayCommand ToggleOfflineReplayCommand { get; private set; } = null!;

    public AsyncRelayCommand CalculateReplayRoiCommand { get; private set; } = null!;

    public RelayCommand SaveRoiCurveCommand { get; private set; } = null!;

    public RelayCommand ClearRealtimeRoiCurveCommand { get; private set; } = null!;

    internal FixedRoiGrid FixedRoiGrid => fixedRoiGrid;

    internal FixedRoiCell SelectedFixedRoiCell => selectedFixedRoiCell;

    internal int RoiDefinitionRevision => Volatile.Read(ref roiDefinitionRevision);

    internal ReplayVisualizationController ReplayController { get; private set; } = null!;

    internal void AttachReplayController(ReplayVisualizationController controller)
    {
        ReplayController = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    internal void ConfigureCommands(VisualizationWorkspaceCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        RefreshImagingRunsCommand = commands.RefreshImagingRuns;
        ToggleReplayPlaybackCommand = commands.ToggleReplayPlayback;
        ToggleLiveReplayCommand = commands.ToggleLiveReplay;
        ToggleOfflineReplayCommand = commands.ToggleOfflineReplay;
        CalculateReplayRoiCommand = commands.CalculateReplayRoi;
        SaveRoiCurveCommand = commands.SaveRoiCurve;
        ClearRealtimeRoiCurveCommand = commands.ClearRealtimeRoiCurve;
        OnPropertyChanged(string.Empty);
    }

    internal void SetReplayLaneAvailability(bool hasLive, bool hasOfflineComplete)
    {
        HasLiveReplay = hasLive;
        HasOfflineCompleteReplay = hasOfflineComplete;
    }

    internal void SetActiveReplayLane(string? lane, string? revisionId)
    {
        ActiveReplayLane = lane;
        ActiveReplayRevisionId = revisionId;
    }

    internal void SetSelectedImagingRun(ImagingRunListItem? item, bool notifySelection)
    {
        if (!SetProperty(ref selectedImagingRun, item, nameof(SelectedImagingRun)))
        {
            return;
        }

        PublishStateSnapshot();
        if (notifySelection)
        {
            SelectedImagingRunChanged?.Invoke(item);
        }
    }

    internal void SetReplayFrameCount(int frameCount)
    {
        replayFrameCount = Math.Max(0, frameCount);
        if (replayFrameIndex > ReplayMaxFrameIndex)
        {
            replayFrameIndex = ReplayMaxFrameIndex;
            OnPropertyChanged(nameof(ReplayFrameIndex));
        }

        OnPropertyChanged(nameof(ReplayMaxFrameIndex));
        OnPropertyChanged(nameof(HasReplayFrames));
        PublishStateSnapshot();
    }

    internal void ResetReplayFrameIndex()
    {
        SetReplayFrameIndex(0, notifySelection: false);
    }

    internal void RestoreReplayFrameIndex(int index)
    {
        SetReplayFrameIndex(index, notifySelection: false);
    }

    internal void CommitReplayFramePresentation(
        int index,
        ImageSource? imageSource,
        Geometry? curveGeometry,
        string frameSummary,
        string contactSummary,
        string loadStatus)
    {
        var clamped = Math.Clamp(index, 0, ReplayMaxFrameIndex);
        var indexChanged = replayFrameIndex != clamped;
        var imageChanged = !ReferenceEquals(replayImageSource, imageSource);
        var curveChanged = !ReferenceEquals(replayCurveGeometry, curveGeometry);
        var frameSummaryChanged = !string.Equals(replayFrameSummary, frameSummary, StringComparison.Ordinal);
        var contactSummaryChanged = !string.Equals(replayContactSummary, contactSummary, StringComparison.Ordinal);
        var loadStatusChanged = !string.Equals(replayLoadStatus, loadStatus, StringComparison.Ordinal);

        replayFrameIndex = clamped;
        replayImageSource = imageSource;
        replayCurveGeometry = curveGeometry;
        replayFrameSummary = frameSummary;
        replayContactSummary = contactSummary;
        replayLoadStatus = loadStatus;

        if (indexChanged) OnPropertyChanged(nameof(ReplayFrameIndex));
        if (imageChanged) OnPropertyChanged(nameof(ReplayImageSource));
        if (curveChanged) OnPropertyChanged(nameof(ReplayCurveGeometry));
        if (frameSummaryChanged) OnPropertyChanged(nameof(ReplayFrameSummary));
        if (contactSummaryChanged) OnPropertyChanged(nameof(ReplayContactSummary));
        if (loadStatusChanged) OnPropertyChanged(nameof(ReplayLoadStatus));
        PublishStateSnapshot();
    }

    public void SetRoiCenterFromImagePoint(double x, double y, double width, double height)
    {
        if (width <= 0.0 || height <= 0.0 || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return;
        }

        var pointX = x / width;
        var pointY = y / height;
        if (roiMode == RoiModeFixedNominal)
        {
            var cell = fixedRoiGrid.HitTestNormalizedDisplayPoint(
                pointX,
                pointY,
                VisualizationGeometry.ImagePaddingFraction);
            if (cell is null || string.Equals(cell.Id, selectedFixedRoiCell.Id, StringComparison.Ordinal))
            {
                return;
            }

            selectedFixedRoiCell = cell;
            OnPropertyChanged(nameof(FixedRoiSelectionGeometry));
            OnPropertyChanged(nameof(SelectedFixedRoiId));
            OnPropertyChanged(nameof(RoiPositionSummary));
            PublishRoiDefinitionChanged(fixedCellChanged: true);
            return;
        }

        var changed = false;
        changed |= SetProperty(ref roiCenterX, ClampRoiCenter(pointX), nameof(RoiCenterXPercent));
        changed |= SetProperty(ref roiCenterY, ClampRoiCenter(pointY), nameof(RoiCenterYPercent));
        if (changed)
        {
            PublishRoiDefinitionChanged(fixedCellChanged: false);
        }
    }

    internal VisualizationRoiDefinitionSnapshot CaptureRoiDefinition()
    {
        return new VisualizationRoiDefinitionSnapshot(
            Volatile.Read(ref roiDefinitionRevision),
            roiMode,
            roiShape,
            roiCenterX,
            roiCenterY,
            roiSizePixels,
            selectedFixedRoiCell,
            fixedRoiGrid.ResolutionProfile.NominalResolutionDiameterFraction,
            RoiImageCanvasSize);
    }

    internal void InvalidateRoiRevision()
    {
        Interlocked.Increment(ref roiDefinitionRevision);
    }

    private void SetReplayFrameIndex(int value, bool notifySelection)
    {
        var clamped = Math.Clamp(value, 0, ReplayMaxFrameIndex);
        if (!SetProperty(ref replayFrameIndex, clamped, nameof(ReplayFrameIndex)))
        {
            return;
        }

        PublishStateSnapshot();
        if (notifySelection)
        {
            ReplayFrameIndexChanged?.Invoke(clamped);
        }
    }

    /// <summary>ROI bounds are proportions of the live surface, not absolute pixels.</summary>
    private double ClampRoiSizePixels(double value) => Math.Clamp(
        double.IsFinite(value) ? value : RoiImageCanvasSize * DefaultRoiSizeFraction,
        RoiImageCanvasSize * RoiMinSizeFraction,
        RoiImageCanvasSize * RoiMaxSizeFraction);

    private void SetRoiSizePixels(double value, bool updatePreset)
    {
        var clamped = ClampRoiSizePixels(value);
        if (!SetProperty(ref roiSizePixels, clamped, nameof(RoiSizePixels)))
        {
            return;
        }

        SetProperty(ref roiCenterX, ClampRoiCenter(roiCenterX), nameof(RoiCenterXPercent));
        SetProperty(ref roiCenterY, ClampRoiCenter(roiCenterY), nameof(RoiCenterYPercent));
        if (updatePreset)
        {
            var preset = GetMatchingRoiPreset(clamped);
            if (!string.Equals(roiSizePreset, preset, StringComparison.Ordinal))
            {
                roiSizePreset = preset;
                OnPropertyChanged(nameof(RoiSizePreset));
            }
        }

        PublishRoiDefinitionChanged(fixedCellChanged: false);
    }

    private double ClampRoiCenter(double value)
    {
        var normalized = double.IsFinite(value) ? value : 0.5;
        var half = Math.Min(0.5, (roiSizePixels / RoiImageCanvasSize) / 2.0);
        return Math.Clamp(normalized, half, 1.0 - half);
    }

    private void PublishRoiDefinitionChanged(bool fixedCellChanged)
    {
        Interlocked.Increment(ref roiDefinitionRevision);
        OnPropertyChanged(nameof(RoiOverlayLeft));
        OnPropertyChanged(nameof(RoiOverlayTop));
        OnPropertyChanged(nameof(RoiOverlaySize));
        OnPropertyChanged(nameof(RoiSquareVisibility));
        OnPropertyChanged(nameof(RoiCircleVisibility));
        OnPropertyChanged(nameof(RoiCustomControlsVisibility));
        OnPropertyChanged(nameof(RoiFixedGridVisibility));
        OnPropertyChanged(nameof(FixedRoiSelectionGeometry));
        OnPropertyChanged(nameof(SelectedFixedRoiId));
        OnPropertyChanged(nameof(RoiPositionSummary));
        OnPropertyChanged(nameof(RoiCenterXPercent));
        OnPropertyChanged(nameof(RoiCenterYPercent));
        RoiDefinitionChanged?.Invoke(fixedCellChanged);
    }

    private void PublishStateSnapshot()
    {
        var next = new VisualizationWorkspaceSnapshot(
            SelectedImagingRun?.Summary.ImagingRunId,
            ReplayFrameIndex,
            replayFrameCount,
            IsReplayPlaying,
            ReplayImageSource is not null || RealtimeImageSource is not null,
            ReplayFrameSummary,
            ReplayRoiSummary,
            StateSnapshot.Revision + 1);
        StateSnapshot = next;
        PublishStatus(
            IsReplayPlaying ? "playing" : HasReplayFrames ? "ready" : "idle",
            IsReplayPlaying ? "实验回放中" : HasReplayFrames ? $"已加载 {replayFrameCount} 帧" : "等待选择实验",
            DateTimeOffset.Now);
        StateChanged?.Invoke(next);
    }

    private static string NormalizeRoiMode(string? mode)
    {
        return string.Equals(mode?.Trim(), RoiModeFixedNominal, StringComparison.OrdinalIgnoreCase)
            ? RoiModeFixedNominal
            : RoiModeCustom;
    }

    private static string NormalizeRoiShape(string? shape)
    {
        return string.Equals(shape?.Trim(), RoiShapeCircle, StringComparison.OrdinalIgnoreCase)
            ? RoiShapeCircle
            : RoiShapeSquare;
    }

    private static string NormalizeRoiSizePreset(string? preset)
    {
        return preset?.Trim().ToLowerInvariant() switch
        {
            RoiPresetSmall => RoiPresetSmall,
            RoiPresetLarge => RoiPresetLarge,
            RoiPresetCustom => RoiPresetCustom,
            _ => RoiPresetMedium
        };
    }

    private static bool TryGetRoiPresetSize(string preset, out double size)
    {
        size = NormalizeRoiSizePreset(preset) switch
        {
            RoiPresetSmall => 64.0,
            RoiPresetMedium => 96.0,
            RoiPresetLarge => 144.0,
            _ => double.NaN
        };
        return double.IsFinite(size);
    }

    private static string GetMatchingRoiPreset(double size)
    {
        if (Math.Abs(size - 64.0) < 0.5)
        {
            return RoiPresetSmall;
        }

        if (Math.Abs(size - 96.0) < 0.5)
        {
            return RoiPresetMedium;
        }

        return Math.Abs(size - 144.0) < 0.5 ? RoiPresetLarge : RoiPresetCustom;
    }

    private static string FormatRoiShapeLabel(string shape)
    {
        return NormalizeRoiShape(shape) == RoiShapeCircle ? "圆形 ROI" : "方形 ROI";
    }

    private Geometry CreateFixedRoiGridGeometry(FixedRoiGrid grid)
    {
        var group = new GeometryGroup();
        var center = new Point(RoiImageCanvasSize / 2.0, RoiImageCanvasSize / 2.0);
        var domainRadius = (RoiImageCanvasSize / 2.0) - VisualizationGeometry.PaddingFor(RoiImageCanvasSize);
        for (var ringNumber = 1; ringNumber <= grid.RingCount; ringNumber++)
        {
            var cells = grid.GetRingCells(ringNumber);
            var outerRadius = cells[0].OuterRadiusFraction * domainRadius;
            group.Children.Add(new EllipseGeometry(center, outerRadius, outerRadius));
            if (cells.Count == 1)
            {
                continue;
            }

            foreach (var cell in cells)
            {
                group.Children.Add(new LineGeometry(
                    CreateGeometryPoint(center, domainRadius * cell.InnerRadiusFraction, cell.StartAngleRadians),
                    CreateGeometryPoint(center, domainRadius * cell.OuterRadiusFraction, cell.StartAngleRadians)));
            }
        }

        if (group.CanFreeze)
        {
            group.Freeze();
        }

        return group;
    }

    private static void SynchronizeFixedRoiMapCells(
        ObservableCollection<FixedRoiMapCellVisual> target,
        IReadOnlyList<FixedRoiMapCellVisual> source)
    {
        if (target.Count != source.Count
            || target.Where((item, index) => !string.Equals(
                    item.CellId,
                    source[index].CellId,
                    StringComparison.Ordinal))
                .Any())
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }

            return;
        }

        for (var index = 0; index < target.Count; index++)
        {
            target[index].Apply(source[index]);
        }
    }

    private void UpdateFixedRoiMapCellGeometry(ObservableCollection<FixedRoiMapCellVisual> target)
    {
        if (target.Count == 0)
        {
            return;
        }

        var geometries = FixedRoiTemporalVisualization.GetMapCellGeometries(
            fixedRoiGrid,
            RoiImageCanvasSize,
            VisualizationGeometry.PaddingFor(RoiImageCanvasSize));
        for (var index = 0; index < target.Count && index < geometries.Count; index++)
        {
            target[index].UpdateGeometry(geometries[index]);
        }
    }

    private static Point CreateGeometryPoint(Point center, double radius, double angleRadians)
    {
        return new Point(
            center.X - (radius * Math.Sin(angleRadians)),
            center.Y - (radius * Math.Cos(angleRadians)));
    }
}

/// <param name="CanvasSize">
/// Edge of the surface <c>SizePixels</c> was measured against. The surface is adaptive, so a
/// consumer converting the ROI size back into a fraction cannot assume the default edge.
/// </param>
internal sealed record VisualizationRoiDefinitionSnapshot(
    int Revision,
    string Mode,
    string Shape,
    double CenterX,
    double CenterY,
    double SizePixels,
    FixedRoiCell FixedCell,
    double NominalResolutionDiameterFraction,
    double CanvasSize);

public sealed record VisualizationWorkspaceCommands(
    AsyncRelayCommand RefreshImagingRuns,
    RelayCommand ToggleReplayPlayback,
    RelayCommand ToggleLiveReplay,
    RelayCommand ToggleOfflineReplay,
    AsyncRelayCommand CalculateReplayRoi,
    RelayCommand SaveRoiCurve,
    RelayCommand ClearRealtimeRoiCurve);
