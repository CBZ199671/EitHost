using System.Windows;
using System.Windows.Media;
using EitHost.App.ViewModels.Workspaces;
using EitHost.Core.Analysis;

namespace EitHost.App.ViewModels;

internal static class RoiVisualizationEngine
{
    private const double RoiCurveMarkerDiameter = 16.0;
    private const int RoiCurveMaxTooltipMarkers = 80;
    private const int RoiDespikingAnalysisWindow = 32;
    private const double RoiNoiseDisplayHalfRangeSigma = 6.0;
    private const string RoiModeCustom = "custom";
    private const string RoiModeFixedNominal = "fixed_nominal";
    private const string RoiShapeSquare = "square";
    private const string RoiShapeCircle = "circle";
    private const double RealtimePreviewCanvasWidth = VisualizationGeometry.DefaultPlotCanvasWidth;
    private const double RealtimePreviewCanvasHeight = 220.0;
    private const double RealtimePreviewCanvasPadding = 14.0;
    private const double RealtimeLowImageQualityThreshold = 0.65;
    private static readonly EcdCwrRealtimeRoiDespiker RealtimeRoiDespiker = new();
    private static readonly EcdCwrRealtimeRoiDespikingOptions RealtimeRoiDespikingOptions = new(
        LowConfidenceThreshold: RealtimeLowImageQualityThreshold);
    private static readonly EcdCwrRealtimeRoiTrendFilter RealtimeRoiTrendFilter = new();
    private static readonly EcdCwrRealtimeRoiTrendOptions RealtimeRoiTrendOptions = new(
        TrustedQualityThreshold: RealtimeLowImageQualityThreshold);

    internal static RoiSelectionSnapshot CaptureSelection(VisualizationWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var state = workspace.CaptureRoiDefinition();
        var mode = NormalizeRoiMode(state.Mode);
        return mode == RoiModeFixedNominal
            ? new RoiSelectionSnapshot(
                state.Revision,
                mode,
                null,
                state.FixedCell,
                state.NominalResolutionDiameterFraction,
                state.CanvasSize)
            : new RoiSelectionSnapshot(
                state.Revision,
                mode,
                CreateCurrentRoiDefinition(state),
                null,
                null,
                state.CanvasSize);
    }

    internal static List<RoiCurvePoint> CreateFixedRoiCurveSeries(
        FixedRoiGrid grid,
        string setLabel,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        RoiSelectionSnapshot roi)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var fixedCell = roi.FixedCell ?? throw new InvalidOperationException("固定 ROI 单元缺失。");
        var cellIndex = GetFixedRoiCellIndex(grid, fixedCell.Id);
        var series = new List<RoiCurvePoint>(samples.Count);
        foreach (var sample in samples)
        {
            var mean = sample.MeanConductivity[cellIndex];
            var measurement = new RoiConductivityMeasurement(
                mean,
                sample.SelectedMeshCellCounts[cellIndex],
                sample.AreaWeights[cellIndex],
                mean,
                mean);
            var point = CreateRoiCurvePointFromMeasurement(
                setLabel,
                sample.FrameIndex,
                sample.BlockNumber,
                sample.CapturedAt,
                sample.QualityWeight,
                sample.ReferenceEpoch,
                sample.ReferenceLockKind,
                measurement,
                roi);
            if (point is not null)
            {
                series.Add(point);
            }
        }

        return series;
    }

    internal static IReadOnlyList<FixedRoiTemporalAnalysis> AnalyzeFixedRoiEpochSegments(
        FixedRoiGrid grid,
        IReadOnlyList<FixedRoiTemporalSample> samples)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return SegmentFixedRoiSamplesByReferenceEpoch(samples)
            .Select(segment => FixedRoiTemporalAnalyzer.Analyze(grid, segment))
            .ToArray();
    }

    internal static FixedRoiTemporalAnalysis AnalyzeLatestFixedRoiEpoch(
        FixedRoiGrid grid,
        IReadOnlyList<FixedRoiTemporalSample> samples)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var segments = SegmentFixedRoiSamplesByReferenceEpoch(samples);
        return FixedRoiTemporalAnalyzer.Analyze(grid, segments.Count == 0 ? [] : segments[^1]);
    }

    internal static IReadOnlyList<IReadOnlyList<FixedRoiTemporalSample>> SegmentFixedRoiSamplesByReferenceEpoch(
        IReadOnlyList<FixedRoiTemporalSample> samples)
    {
        var segments = new List<IReadOnlyList<FixedRoiTemporalSample>>();
        var current = new List<FixedRoiTemporalSample>();
        foreach (var sample in samples)
        {
            if (current.Count > 0
                && (current[^1].ReferenceEpoch != sample.ReferenceEpoch
                    || !string.Equals(
                        current[^1].ReferenceLockKind,
                        sample.ReferenceLockKind,
                        StringComparison.Ordinal)))
            {
                segments.Add(current.ToArray());
                current.Clear();
            }

            current.Add(sample);
        }

        if (current.Count > 0)
        {
            segments.Add(current.ToArray());
        }

        return segments;
    }

    private static RoiDefinition CreateCurrentRoiDefinition(VisualizationRoiDefinitionSnapshot state)
    {
        return new RoiDefinition(
            NormalizeRoiShape(state.Shape) == RoiShapeCircle ? RoiSelectionShape.Circle : RoiSelectionShape.Square,
            state.CenterX,
            state.CenterY,
            state.SizePixels / state.CanvasSize).Normalize();
    }

    internal static int GetFixedRoiCellIndex(FixedRoiGrid grid, string cellId)
    {
        for (var index = 0; index < grid.Cells.Count; index++)
        {
            if (string.Equals(grid.Cells[index].Id, cellId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(cellId), cellId, "固定 ROI 单元不存在。");
    }

    internal static string NormalizeRoiMode(string? mode)
    {
        return string.Equals(mode?.Trim(), RoiModeFixedNominal, StringComparison.OrdinalIgnoreCase)
            ? RoiModeFixedNominal
            : RoiModeCustom;
    }

    internal static string NormalizeRoiShape(string? shape)
    {
        return string.Equals(shape?.Trim(), RoiShapeCircle, StringComparison.OrdinalIgnoreCase)
            ? RoiShapeCircle
            : RoiShapeSquare;
    }

    internal static RoiCurvePoint? CreateRoiCurvePoint(
        string setLabel,
        int frameIndex,
        int blockNumber,
        DateTimeOffset capturedAt,
        double qualityWeight,
        int? referenceEpoch,
        string referenceLockKind,
        IReadOnlyList<double> conductivity,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        RoiSelectionSnapshot roi)
    {
        var paddingFraction = VisualizationGeometry.ImagePaddingFraction;
        var measurement = roi.FixedCell is { } fixedCell
            ? RoiConductivityAnalyzer.Measure(
                fixedCell,
                nodeCoords,
                cellConnectivity,
                conductivity,
                paddingFraction)
            : RoiConductivityAnalyzer.Measure(
                roi.CustomDefinition ?? throw new InvalidOperationException("自定义 ROI 定义缺失。"),
                nodeCoords,
                cellConnectivity,
                conductivity,
                paddingFraction);
        return CreateRoiCurvePointFromMeasurement(
            setLabel,
            frameIndex,
            blockNumber,
            capturedAt,
            qualityWeight,
            referenceEpoch,
            referenceLockKind,
            measurement,
            roi);
    }

    internal static RoiCurvePoint? CreateRoiCurvePointFromMeasurement(
        string setLabel,
        int frameIndex,
        int blockNumber,
        DateTimeOffset capturedAt,
        double qualityWeight,
        int? referenceEpoch,
        string referenceLockKind,
        RoiConductivityMeasurement measurement,
        RoiSelectionSnapshot roi,
        string valueSource = RoiValueSource.InverseReconstruction)
    {
        return measurement.HasValue
            ? new RoiCurvePoint(
                setLabel,
                frameIndex,
                blockNumber,
                capturedAt,
                qualityWeight,
                referenceEpoch,
                referenceLockKind,
                valueSource,
                measurement.MeanConductivity,
                measurement.MeanConductivity,
                measurement.MeanConductivity,
                EcdCwrRoiFilterState.Raw,
                0.0,
                0.0,
                measurement.MeanConductivity,
                0.0,
                RealtimeRoiTrendOptions.NoiseSigmaMultiplier,
                0,
                false,
                false,
                false,
                measurement.SelectedCellCount,
                measurement.AreaWeight,
                measurement.MinConductivity,
                measurement.MaxConductivity,
                roi)
            : null;
    }

    internal static void ApplyRealtimeRoiFilteringUnsafe(List<RoiCurvePoint> series)
    {
        if (series.Count == 0)
        {
            return;
        }

        var currentEpochStart = FindCurrentRoiEpochStart(series);
        var analysisStart = Math.Max(currentEpochStart, series.Count - RoiDespikingAnalysisWindow);
        var analysisSamples = series
            .Skip(analysisStart)
            .Select(point => new EcdCwrRoiCurveSample(
                point.RawMeanConductivity,
                point.QualityWeight,
                point.ValueSource == RoiValueSource.TrustedNeutral))
            .ToArray();
        var priorNoisePoint = series
            .Skip(currentEpochStart)
            .LastOrDefault(point => point.NoiseBandReady);
        var priorNoiseModel = priorNoisePoint is null
            ? null
            : new EcdCwrRoiNoiseModel(
                priorNoisePoint.NoiseCenter,
                priorNoisePoint.NoiseSigma,
                priorNoisePoint.NoiseSigmaMultiplier,
                priorNoisePoint.NoiseSampleCount,
                EcdCwrRealtimeRoiTrendFilter.PolicyVersion);
        priorNoiseModel ??= RealtimeRoiTrendFilter.TryCreateNoiseModel(
            series
                .Skip(currentEpochStart)
                .Select(point => new EcdCwrRoiCurveSample(
                    point.RawMeanConductivity,
                    point.QualityWeight,
                    point.ValueSource == RoiValueSource.TrustedNeutral))
                .ToArray(),
            series
                .Skip(currentEpochStart)
                .Select(point => point.DespikedMeanConductivity)
                .ToArray(),
            RealtimeRoiTrendOptions);
        var filtered = RealtimeRoiDespiker.Analyze(
            analysisSamples,
            RealtimeRoiDespikingOptions,
            noiseModel: priorNoiseModel);
        var trend = RealtimeRoiTrendFilter.Analyze(
            analysisSamples,
            filtered,
            priorNoiseModel,
            RealtimeRoiTrendOptions);
        var firstMutableIndex = Math.Max(
            currentEpochStart,
            series.Count - RealtimeRoiDespikingOptions.MaximumDecisionLag - 1);
        for (var index = firstMutableIndex; index < series.Count; index++)
        {
            var localIndex = index - analysisStart;
            var decision = filtered[localIndex];
            var trendPoint = trend.Points[localIndex];
            var noiseModel = trend.NoiseModel;
            series[index] = series[index] with
            {
                MeanConductivity = trendPoint.TrendMeanConductivity,
                DespikedMeanConductivity = trendPoint.DespikedMeanConductivity,
                RawMeanConductivity = decision.RawMeanConductivity,
                FilterState = decision.State,
                FilterScore = decision.ExcursionScore,
                FilterReturnScore = decision.ReturnScore,
                NoiseCenter = noiseModel?.Center ?? trendPoint.TrendMeanConductivity,
                NoiseSigma = noiseModel?.Sigma ?? 0.0,
                NoiseSigmaMultiplier = noiseModel?.SigmaMultiplier ?? RealtimeRoiTrendOptions.NoiseSigmaMultiplier,
                NoiseSampleCount = noiseModel?.SampleCount ?? 0,
                NoiseBandReady = noiseModel is not null,
                IsOutsideNoiseBand = trendPoint.IsOutsideNoiseBand,
                IsSustainedEvent = trendPoint.IsSustainedEvent,
            };
        }
    }

    internal static Geometry? CreateRoiSeriesGeometry(IReadOnlyList<RoiCurvePoint> points)
    {
        return BuildRoiCurveChart(points).Geometry;
    }

    internal static int FindCurrentRoiEpochStart(IReadOnlyList<RoiCurvePoint> points)
    {
        if (points.Count == 0)
        {
            return 0;
        }

        var latest = points[^1];
        var index = points.Count - 1;
        while (index > 0 && IsSameRoiReferenceEpoch(points[index - 1], latest))
        {
            index--;
        }

        return index;
    }

    internal static RoiCurveChart BuildRoiCurveChart(IReadOnlyList<RoiCurvePoint> points)
    {
        if (points.Count == 0)
        {
            return new RoiCurveChart(null, null, null, [], string.Empty, string.Empty, string.Empty, 0);
        }

        var values = points.Select(point => point.MeanConductivity).ToArray();
        var rawValues = points.Select(point => point.RawMeanConductivity).ToArray();
        var noisePoint = points.LastOrDefault(point => point.NoiseBandReady);
        var range = noisePoint is null
            ? FindFiniteRange(values)
            : FindFiniteRange(
                values,
                [
                    noisePoint.NoiseCenter - (RoiNoiseDisplayHalfRangeSigma * noisePoint.NoiseSigma),
                    noisePoint.NoiseCenter + (RoiNoiseDisplayHalfRangeSigma * noisePoint.NoiseSigma),
                ]);
        var geometry = CreateRoiEpochSegmentedGeometry(
            points,
            static point => point.MeanConductivity,
            range.Min,
            range.Max);
        var rawGeometry = CreateRoiEpochSegmentedGeometry(
            points,
            static point => point.RawMeanConductivity,
            range.Min,
            range.Max);
        var noiseBandGeometry = noisePoint is null
            ? null
            : CreateRoiNoiseBandGeometry(
                noisePoint.NoiseCenter,
                noisePoint.NoiseSigma,
                noisePoint.NoiseSigmaMultiplier,
                range.Min,
                range.Max);
        var markerIndexes = CreateRoiTooltipMarkerIndexes(points.Count);
        for (var index = 1; index < points.Count; index++)
        {
            if (!IsSameRoiReferenceEpoch(points[index - 1], points[index]))
            {
                markerIndexes.Add(index);
            }
        }
        var markers = new List<RoiCurveMarker>(markerIndexes.Count);
        var xScale = points.Count == 1
            ? 0.0
            : (RealtimePreviewCanvasWidth - (2.0 * RealtimePreviewCanvasPadding)) / (points.Count - 1);
        var yScale = (RealtimePreviewCanvasHeight - (2.0 * RealtimePreviewCanvasPadding)) / (range.Max - range.Min);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.MeanConductivity))
            {
                continue;
            }

            var x = RealtimePreviewCanvasPadding + (index * xScale);
            var y = RealtimePreviewCanvasHeight
                - RealtimePreviewCanvasPadding
                - ((point.MeanConductivity - range.Min) * yScale);
            y = Math.Clamp(y, RealtimePreviewCanvasPadding, RealtimePreviewCanvasHeight - RealtimePreviewCanvasPadding);
            if (markerIndexes.Contains(index))
            {
                markers.Add(new RoiCurveMarker(
                    x - (RoiCurveMarkerDiameter / 2.0),
                    y - (RoiCurveMarkerDiameter / 2.0),
                    RoiCurveMarkerDiameter,
                    FormatRoiTooltip(
                        point,
                        index > 0 && !IsSameRoiReferenceEpoch(points[index - 1], point))));
            }
        }

        var middle = points.Count / 2;
        var epochSegmentCount = 1 + Enumerable.Range(1, points.Count - 1)
            .Count(index => !IsSameRoiReferenceEpoch(points[index - 1], points[index]));
        return new RoiCurveChart(
            geometry,
            rawGeometry,
            noiseBandGeometry,
            markers,
            FormatRoiAxisTime(points[0].CapturedAt),
            FormatRoiAxisTime(points[middle].CapturedAt),
            FormatRoiAxisTime(points[^1].CapturedAt),
            epochSegmentCount);
    }

    internal static Geometry? CreateRoiEpochSegmentedGeometry(
        IReadOnlyList<RoiCurvePoint> points,
        Func<RoiCurvePoint, double> valueSelector,
        double min,
        double max)
    {
        if (points.Count == 0)
        {
            return null;
        }

        if (Math.Abs(max - min) < double.Epsilon)
        {
            min -= 1.0;
            max += 1.0;
        }

        var xScale = points.Count == 1
            ? 0.0
            : (RealtimePreviewCanvasWidth - (2.0 * RealtimePreviewCanvasPadding)) / (points.Count - 1);
        var yScale = (RealtimePreviewCanvasHeight - (2.0 * RealtimePreviewCanvasPadding)) / (max - min);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var segment = new List<Point>();
            void FlushSegment()
            {
                if (segment.Count == 0)
                {
                    return;
                }

                context.BeginFigure(segment[0], isFilled: false, isClosed: false);
                if (segment.Count > 1)
                {
                    context.PolyLineTo(segment.Skip(1).ToArray(), isStroked: true, isSmoothJoin: false);
                }

                segment.Clear();
            }

            for (var index = 0; index < points.Count; index++)
            {
                if (index > 0 && !IsSameRoiReferenceEpoch(points[index - 1], points[index]))
                {
                    FlushSegment();
                }

                var value = valueSelector(points[index]);
                if (!double.IsFinite(value))
                {
                    FlushSegment();
                    continue;
                }

                var x = RealtimePreviewCanvasPadding + (index * xScale);
                var y = RealtimePreviewCanvasHeight
                    - RealtimePreviewCanvasPadding
                    - ((value - min) * yScale);
                segment.Add(new Point(
                    x,
                    Math.Clamp(
                        y,
                        RealtimePreviewCanvasPadding,
                        RealtimePreviewCanvasHeight - RealtimePreviewCanvasPadding)));
            }

            FlushSegment();
        }

        geometry.Freeze();
        return geometry;
    }

    internal static bool IsSameRoiReferenceEpoch(RoiCurvePoint left, RoiCurvePoint right)
    {
        return left.ReferenceEpoch == right.ReferenceEpoch
            && string.Equals(left.ReferenceLockKind, right.ReferenceLockKind, StringComparison.Ordinal);
    }

    internal static Geometry CreateRoiNoiseBandGeometry(
        double center,
        double sigma,
        double sigmaMultiplier,
        double min,
        double max)
    {
        var yScale = (RealtimePreviewCanvasHeight - (2.0 * RealtimePreviewCanvasPadding)) / (max - min);
        var upper = center + (sigmaMultiplier * sigma);
        var lower = center - (sigmaMultiplier * sigma);
        var top = RealtimePreviewCanvasHeight
            - RealtimePreviewCanvasPadding
            - ((upper - min) * yScale);
        var bottom = RealtimePreviewCanvasHeight
            - RealtimePreviewCanvasPadding
            - ((lower - min) * yScale);
        top = Math.Clamp(top, RealtimePreviewCanvasPadding, RealtimePreviewCanvasHeight - RealtimePreviewCanvasPadding);
        bottom = Math.Clamp(bottom, RealtimePreviewCanvasPadding, RealtimePreviewCanvasHeight - RealtimePreviewCanvasPadding);
        var geometry = new RectangleGeometry(new Rect(
            RealtimePreviewCanvasPadding,
            Math.Min(top, bottom),
            RealtimePreviewCanvasWidth - (2.0 * RealtimePreviewCanvasPadding),
            Math.Max(1.0, Math.Abs(bottom - top))));
        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    internal static HashSet<int> CreateRoiTooltipMarkerIndexes(int pointCount)
    {
        if (pointCount <= 0)
        {
            return [];
        }

        if (pointCount <= RoiCurveMaxTooltipMarkers)
        {
            return Enumerable.Range(0, pointCount).ToHashSet();
        }

        var indexes = new HashSet<int> { 0, pointCount - 1 };
        var step = (pointCount - 1.0) / (RoiCurveMaxTooltipMarkers - 1);
        for (var marker = 1; marker < RoiCurveMaxTooltipMarkers - 1; marker++)
        {
            indexes.Add((int)Math.Round(marker * step));
        }

        return indexes;
    }

    internal static string FormatRoiTooltip(RoiCurvePoint point, bool epochBoundary = false)
    {
        var noise = point.NoiseBandReady
            ? $"\n基线 {point.NoiseCenter:F6} · 噪声 σ {point.NoiseSigma:F6} · z {(point.MeanConductivity - point.NoiseCenter) / point.NoiseSigma:F2}"
            : "\n噪声带预热中";
        var epoch = point.ReferenceEpoch is { } value
            ? $"e{value} / {point.ReferenceLockKind}"
            : $"未记录 / {point.ReferenceLockKind}";
        var boundary = epochBoundary ? "\n参考 epoch 边界：曲线在此断开" : string.Empty;
        return $"时间 {point.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}\nblock {point.BlockNumber}\n参考 {epoch}{boundary}\n来源 {FormatRoiValueSourceLabel(point.ValueSource)}\n趋势重构值 {point.MeanConductivity:F6}\n去尖刺值 {point.DespikedMeanConductivity:F6} · 原始值 {point.RawMeanConductivity:F6}\n{FormatRoiFilterStateLabel(point.FilterState)} · 离群 {point.FilterScore:F2} · 回归 {point.FilterReturnScore:F2}{noise}\n单元 {point.SelectedCellCount} · 质量 {point.QualityWeight:F2}";
    }

    internal static string FormatRoiAxisTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("HH:mm:ss");
    }

    internal static string FormatRoiSeriesSummary(string title, IReadOnlyList<RoiCurvePoint> series)
    {
        if (series.Count == 0)
        {
            return $"{title}：当前 ROI 未选中任何重构单元。";
        }

        var values = series.Select(point => point.MeanConductivity).ToArray();
        var filterSummary = FormatRoiFilterCountSummary(series);
        var noiseSummary = FormatRoiNoiseSummary(series);
        var epochCount = series
            .Select(point => (point.ReferenceEpoch, point.ReferenceLockKind))
            .Distinct()
            .Count();
        var latest = series[^1];
        var epochSummary = latest.ReferenceEpoch is { } epoch
            ? $" · 当前 e{epoch}/{latest.ReferenceLockKind} · epoch 分段 {epochCount}"
            : $" · 当前参考未记录/{latest.ReferenceLockKind}";
        return $"{title}：{series.Count} 帧 · 重构值 {values.Min():F4} ~ {values.Max():F4} · 当前 {values[^1]:F4}{epochSummary}{filterSummary}{noiseSummary}";
    }

    internal static string FormatRoiFilterCountSummary(IReadOnlyList<RoiCurvePoint> series)
    {
        var repairedCount = series.Count(point => point.FilterState is
            EcdCwrRoiFilterState.RepairedIsolated or
            EcdCwrRoiFilterState.RepairedShortBurst);
        var pendingCount = series.Count(point => point.FilterState == EcdCwrRoiFilterState.ProvisionalHold);
        return repairedCount > 0 || pendingCount > 0
            ? $" · 去尖刺 {repairedCount} · 待确认 {pendingCount}"
            : string.Empty;
    }

    internal static string FormatRoiNoiseSummary(IReadOnlyList<RoiCurvePoint> series)
    {
        var latest = series[^1];
        if (!latest.NoiseBandReady)
        {
            return " · 噪声带预热";
        }

        var currentEpoch = series.Skip(FindCurrentRoiEpochStart(series)).ToArray();
        var outsideCount = currentEpoch.Count(point => point.NoiseBandReady && point.IsOutsideNoiseBand);
        var sustainedCount = currentEpoch.Count(point => point.NoiseBandReady && point.IsSustainedEvent);
        var currentEvent = latest.IsSustainedEvent ? " · 当前持续事件" : string.Empty;
        return $" · 噪声 σ {latest.NoiseSigma:F6} · 基线 ±{latest.NoiseSigmaMultiplier:F0}σ · 带外 {outsideCount} · 持续 {sustainedCount}{currentEvent}";
    }

    internal static string FormatRoiFilterStateLabel(EcdCwrRoiFilterState state)
    {
        return state switch
        {
            EcdCwrRoiFilterState.Raw => "原始",
            EcdCwrRoiFilterState.ProvisionalHold => "尖刺暂存",
            EcdCwrRoiFilterState.RepairedIsolated => "孤立尖刺已修复",
            EcdCwrRoiFilterState.RepairedShortBurst => "短脉冲已修复",
            EcdCwrRoiFilterState.RestoredNonIsolated => "持续变化已保留",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }

    internal static string FormatRoiValueSourceLabel(string valueSource)
    {
        return valueSource == RoiValueSource.TrustedNeutral
            ? "可信中性基准"
            : "逆求解重构值";
    }

    internal static string ResolveReferenceLockKind(
        int? referenceEpoch,
        IReadOnlyDictionary<int, string> referenceLockKinds)
    {
        return referenceEpoch is { } epoch
            && referenceLockKinds.TryGetValue(epoch, out var lockKind)
            && !string.IsNullOrWhiteSpace(lockKind)
                ? lockKind
                : "legacy_unknown";
    }

    internal static string FormatRoiFilterStateCode(EcdCwrRoiFilterState state)
    {
        return state switch
        {
            EcdCwrRoiFilterState.Raw => "raw",
            EcdCwrRoiFilterState.ProvisionalHold => "provisional_hold",
            EcdCwrRoiFilterState.RepairedIsolated => "repaired_isolated",
            EcdCwrRoiFilterState.RepairedShortBurst => "repaired_short_burst",
            EcdCwrRoiFilterState.RestoredNonIsolated => "restored_non_isolated",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }

    private static (double Min, double Max) FindFiniteRange(params IReadOnlyList<double>[] series)
    {
        var finite = series.SelectMany(values => values).Where(double.IsFinite).ToArray();
        if (finite.Length == 0)
        {
            return (-1.0, 1.0);
        }

        var min = finite.Min();
        var max = finite.Max();
        if (Math.Abs(max - min) < 1.0e-12)
        {
            var padding = Math.Max(1.0e-6, Math.Abs(max) * 0.05);
            return (min - padding, max + padding);
        }

        var margin = (max - min) * 0.05;
        return (min - margin, max + margin);
    }
}
