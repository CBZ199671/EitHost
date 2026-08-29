using System.Globalization;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EitHost.Core.Analysis;

namespace EitHost.App.ViewModels;

public sealed class FixedRoiMapCellVisual : ObservableObject
{
    private Geometry geometry;
    private Brush fill;
    private double opacity;
    private string tooltip;

    public FixedRoiMapCellVisual(
        string cellId,
        Geometry geometry,
        Brush fill,
        double opacity,
        string tooltip)
    {
        CellId = cellId;
        this.geometry = geometry;
        this.fill = fill;
        this.opacity = opacity;
        this.tooltip = tooltip;
    }

    public string CellId { get; }

    public Geometry Geometry => geometry;

    public Brush Fill => fill;

    public double Opacity => opacity;

    public string Tooltip => tooltip;

    internal void Apply(FixedRoiMapCellVisual source)
    {
        ArgumentNullException.ThrowIfNull(source);
        SetProperty(ref geometry, source.Geometry, nameof(Geometry));
        SetProperty(ref fill, source.Fill, nameof(Fill));
        SetProperty(ref opacity, source.Opacity, nameof(Opacity));
        SetProperty(ref tooltip, source.Tooltip, nameof(Tooltip));
    }

    internal void UpdateGeometry(Geometry value) => SetProperty(ref geometry, value, nameof(Geometry));
}

public sealed record FixedRoiContextCurveVisual(
    string Label,
    Geometry Geometry,
    Brush Stroke,
    double StrokeThickness,
    DoubleCollection StrokeDashArray);

public sealed record FixedRoiTemporalVisualSnapshot(
    IReadOnlyList<FixedRoiMapCellVisual> MapCells,
    IReadOnlyList<FixedRoiContextCurveVisual> ContextCurves,
    ImageSource? RadialHeatmap,
    ImageSource? AngularHeatmap,
    Geometry? CentroidTrajectory,
    bool HasCentroid,
    double CentroidLeft,
    double CentroidTop,
    string AxisStart,
    string AxisMiddle,
    string AxisEnd,
    string MetricsSummary,
    string AngularSummary)
{
    public Visibility CentroidVisibility => HasCentroid ? Visibility.Visible : Visibility.Collapsed;

    public static FixedRoiTemporalVisualSnapshot Empty { get; } = new(
        [],
        [],
        null,
        null,
        null,
        false,
        0.0,
        0.0,
        string.Empty,
        string.Empty,
        string.Empty,
        "固定 ROI 时序：等待至少 10 帧基线与后续重构帧。",
        "周向：等待数据。");
}

public static class FixedRoiTemporalVisualization
{
    public const string ActivityMapMode = "activity";
    public const string ArrivalMapMode = "arrival";

    private const int HeatmapMaximumWidth = VisualizationGeometry.HeatmapMaximumWidth;
    private const double CurveCanvasWidth = VisualizationGeometry.DefaultPlotCanvasWidth;
    private const double CurveCanvasHeight = 220.0;
    private static readonly Color NegativeColor = Color.FromRgb(37, 99, 235);
    private static readonly Color PositiveColor = Color.FromRgb(249, 115, 22);
    private static readonly Color NeutralColor = Color.FromRgb(248, 250, 252);
    private static readonly Color MissingColor = Color.FromRgb(100, 116, 139);
    private static readonly ConcurrentDictionary<uint, SolidColorBrush> BrushCache = new();
    private static readonly object MapGeometryCacheGate = new();
    private const int MapGeometryCacheCapacity = 8;
    private static readonly Dictionary<MapGeometryCacheKey, IReadOnlyList<Geometry>> MapGeometryCache = [];
    private static readonly LinkedList<MapGeometryCacheKey> MapGeometryCacheLru = new();

    public static FixedRoiTemporalVisualSnapshot Build(
        FixedRoiGrid grid,
        FixedRoiTemporalAnalysis analysis,
        FixedRoiCell selectedCell,
        int requestedFrameIndex,
        int angularRingNumber,
        string? mapMode,
        double canvasSize,
        double paddingPixels)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(selectedCell);
        if (analysis.Frames.Count == 0)
        {
            return FixedRoiTemporalVisualSnapshot.Empty;
        }

        var frameIndex = Math.Clamp(requestedFrameIndex, 0, analysis.Frames.Count - 1);
        var frame = analysis.Frames[frameIndex];
        var arrivalMode = string.Equals(mapMode, ArrivalMapMode, StringComparison.OrdinalIgnoreCase);
        var mapCells = BuildMapCells(grid, analysis, frame, arrivalMode, canvasSize, paddingPixels);
        var contextCurves = BuildContextCurves(grid, analysis, selectedCell);
        var radialHeatmap = BuildRadialHeatmap(grid, analysis);
        var normalizedRing = Math.Clamp(angularRingNumber, 1, grid.RingCount);
        var angularHeatmap = BuildAngularHeatmap(grid, analysis, normalizedRing);
        var trajectory = BuildCentroidTrajectory(
            analysis,
            frameIndex,
            canvasSize,
            paddingPixels,
            out var hasCentroid,
            out var centroidLeft,
            out var centroidTop);
        var axis = FormatTimeAxis(analysis.Frames);
        var metrics = FormatMetricsSummary(grid, analysis, frame, selectedCell, arrivalMode);
        var angularSummary = $"第 {normalizedRing} 环 · {grid.GetRingCells(normalizedRing).Count} 区 · 上=S01，向下按逆时针递增";
        return new FixedRoiTemporalVisualSnapshot(
            mapCells,
            contextCurves,
            radialHeatmap,
            angularHeatmap,
            trajectory,
            hasCentroid,
            centroidLeft,
            centroidTop,
            axis.Start,
            axis.Middle,
            axis.End,
            metrics,
            angularSummary);
    }

    public static Geometry CreateCellGeometry(
        FixedRoiCell cell,
        double canvasSize,
        double paddingPixels)
    {
        ArgumentNullException.ThrowIfNull(cell);
        var center = new Point(canvasSize / 2.0, canvasSize / 2.0);
        var domainRadius = (canvasSize / 2.0) - paddingPixels;
        var outerRadius = domainRadius * cell.OuterRadiusFraction;
        if (cell.IsCenter)
        {
            return Freeze(new EllipseGeometry(center, outerRadius, outerRadius));
        }

        var innerRadius = domainRadius * cell.InnerRadiusFraction;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var outerStart = CreatePoint(center, outerRadius, cell.StartAngleRadians);
            var outerEnd = CreatePoint(center, outerRadius, cell.EndAngleRadians);
            var innerEnd = CreatePoint(center, innerRadius, cell.EndAngleRadians);
            var innerStart = CreatePoint(center, innerRadius, cell.StartAngleRadians);
            context.BeginFigure(outerStart, isFilled: true, isClosed: true);
            context.ArcTo(
                outerEnd,
                new Size(outerRadius, outerRadius),
                0.0,
                isLargeArc: false,
                SweepDirection.Counterclockwise,
                isStroked: true,
                isSmoothJoin: false);
            context.LineTo(innerEnd, isStroked: true, isSmoothJoin: false);
            context.ArcTo(
                innerStart,
                new Size(innerRadius, innerRadius),
                0.0,
                isLargeArc: false,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        return Freeze(geometry);
    }

    private static IReadOnlyList<FixedRoiMapCellVisual> BuildMapCells(
        FixedRoiGrid grid,
        FixedRoiTemporalAnalysis analysis,
        FixedRoiTemporalFrame frame,
        bool arrivalMode,
        double canvasSize,
        double paddingPixels)
    {
        var arrived = analysis.Cells
            .Where(cell => cell.ArrivalSecondsAfterBaseline is not null)
            .Select(cell => cell.ArrivalSecondsAfterBaseline!.Value)
            .ToArray();
        var maximumArrivalSeconds = arrived.Length == 0 ? 1.0 : Math.Max(1.0e-9, arrived.Max());
        var geometries = GetMapCellGeometries(grid, canvasSize, paddingPixels);
        var visuals = new FixedRoiMapCellVisual[grid.Cells.Count];
        for (var index = 0; index < grid.Cells.Count; index++)
        {
            var cell = grid.Cells[index];
            var summary = analysis.Cells[index];
            Brush fill;
            double opacity;
            if (arrivalMode)
            {
                if (summary.ArrivalSecondsAfterBaseline is { } arrivalSeconds)
                {
                    fill = CreateBrush(Interpolate(NegativeColor, PositiveColor, arrivalSeconds / maximumArrivalSeconds));
                    opacity = cell.IsCenter ? 0.46 : 0.78;
                }
                else
                {
                    fill = CreateBrush(MissingColor);
                    opacity = cell.IsCenter ? 0.20 : 0.34;
                }
            }
            else
            {
                var z = frame.ZScores[index];
                fill = CreateBrush(ColorForZ(z, analysis.Options.ZDisplayLimit));
                var magnitude = double.IsFinite(z)
                    ? Math.Clamp(Math.Abs(z) / analysis.Options.ZDisplayLimit, 0.0, 1.0)
                    : 0.0;
                opacity = (cell.IsCenter ? 0.16 : 0.24) + (0.56 * magnitude);
            }

            visuals[index] = new FixedRoiMapCellVisual(
                cell.Id,
                geometries[index],
                fill,
                opacity,
                FormatCellTooltip(cell, frame, summary, index));
        }

        return visuals;
    }

    private static IReadOnlyList<FixedRoiContextCurveVisual> BuildContextCurves(
        FixedRoiGrid grid,
        FixedRoiTemporalAnalysis analysis,
        FixedRoiCell selectedCell)
    {
        var selectedIndex = analysis.GetCellIndex(selectedCell.Id);
        var context = new List<NamedSeries>
        {
            new($"选中 {selectedCell.Id}", analysis.Frames.Select(frame => frame.ZScores[selectedIndex]).ToArray(), SeriesKind.Selected)
        };
        var neighborCells = FindContextNeighbors(grid, selectedCell);
        foreach (var neighbor in neighborCells.Take(4))
        {
            var index = analysis.GetCellIndex(neighbor.Id);
            context.Add(new NamedSeries(
                $"邻近 {neighbor.Id}",
                analysis.Frames.Select(frame => frame.ZScores[index]).ToArray(),
                SeriesKind.Neighbor));
        }

        if (context.Count < 6)
        {
            context.Add(selectedCell.IsCenter
                ? new NamedSeries("全域均值", analysis.Frames.Select(frame => frame.GlobalMeanZScore).ToArray(), SeriesKind.Mean)
                : new NamedSeries(
                    $"第 {selectedCell.RingNumber} 环均值",
                    analysis.Frames.Select(frame => frame.RingMeanZScores[selectedCell.RingNumber - 1]).ToArray(),
                    SeriesKind.Mean));
        }

        var neighborPalette = new[]
        {
            Color.FromRgb(37, 99, 235),
            Color.FromRgb(14, 116, 144),
            Color.FromRgb(71, 85, 105),
            Color.FromRgb(100, 116, 139)
        };
        var neighborIndex = 0;
        var visuals = new List<FixedRoiContextCurveVisual>(context.Count);
        foreach (var series in context.Take(6))
        {
            var color = series.Kind switch
            {
                SeriesKind.Selected => PositiveColor,
                SeriesKind.Mean => Color.FromRgb(180, 126, 20),
                _ => neighborPalette[Math.Min(neighborIndex++, neighborPalette.Length - 1)]
            };
            var dash = series.Kind switch
            {
                SeriesKind.Mean => CreateDash(8.0, 4.0),
                SeriesKind.Neighbor => CreateDash(3.0, 3.0),
                _ => CreateDash()
            };
            visuals.Add(new FixedRoiContextCurveVisual(
                series.Label,
                BuildCurveGeometry(series.Values, analysis.Options.ZDisplayLimit),
                CreateBrush(color),
                series.Kind == SeriesKind.Selected ? 2.8 : series.Kind == SeriesKind.Mean ? 2.0 : 1.35,
                dash));
        }

        return visuals;
    }

    private static IReadOnlyList<FixedRoiCell> FindContextNeighbors(FixedRoiGrid grid, FixedRoiCell selectedCell)
    {
        var neighbors = new List<FixedRoiCell>();
        if (selectedCell.SectorCount > 1)
        {
            var ring = grid.GetRingCells(selectedCell.RingNumber);
            neighbors.Add(ring[(selectedCell.SectorNumber - 2 + ring.Count) % ring.Count]);
            neighbors.Add(ring[selectedCell.SectorNumber % ring.Count]);
        }

        var angle = CellCenterAngle(selectedCell);
        if (selectedCell.RingNumber > 1)
        {
            neighbors.Add(NearestCellAtAngle(grid.GetRingCells(selectedCell.RingNumber - 1), angle));
        }

        if (selectedCell.RingNumber < grid.RingCount)
        {
            neighbors.Add(NearestCellAtAngle(grid.GetRingCells(selectedCell.RingNumber + 1), angle));
        }

        return neighbors
            .Where(cell => !string.Equals(cell.Id, selectedCell.Id, StringComparison.Ordinal))
            .DistinctBy(cell => cell.Id)
            .ToArray();
    }

    private static FixedRoiCell NearestCellAtAngle(IReadOnlyList<FixedRoiCell> cells, double targetAngle)
    {
        return cells.MinBy(cell => CircularDistance(CellCenterAngle(cell), targetAngle))!;
    }

    private static double CircularDistance(double first, double second)
    {
        var distance = Math.Abs(NormalizeAngle(first) - NormalizeAngle(second));
        return Math.Min(distance, (2.0 * Math.PI) - distance);
    }

    private static ImageSource? BuildRadialHeatmap(FixedRoiGrid grid, FixedRoiTemporalAnalysis analysis)
    {
        if (analysis.Frames.Count == 0)
        {
            return null;
        }

        var values = new double[grid.RingCount, analysis.Frames.Count];
        for (var row = 0; row < grid.RingCount; row++)
        {
            var ringIndex = grid.RingCount - row - 1;
            for (var frame = 0; frame < analysis.Frames.Count; frame++)
            {
                values[row, frame] = analysis.Frames[frame].RingMeanZScores[ringIndex];
            }
        }

        return BuildHeatmap(values, analysis.Options.ZDisplayLimit, lowConfidenceLastRow: true);
    }

    private static ImageSource? BuildAngularHeatmap(
        FixedRoiGrid grid,
        FixedRoiTemporalAnalysis analysis,
        int ringNumber)
    {
        if (analysis.Frames.Count == 0)
        {
            return null;
        }

        var cells = grid.GetRingCells(ringNumber);
        var values = new double[cells.Count, analysis.Frames.Count];
        for (var row = 0; row < cells.Count; row++)
        {
            var cellIndex = analysis.GetCellIndex(cells[row].Id);
            for (var frame = 0; frame < analysis.Frames.Count; frame++)
            {
                values[row, frame] = analysis.Frames[frame].ZScores[cellIndex];
            }
        }

        return BuildHeatmap(values, analysis.Options.ZDisplayLimit, lowConfidenceLastRow: ringNumber == 1);
    }

    private static ImageSource BuildHeatmap(double[,] values, double zLimit, bool lowConfidenceLastRow)
    {
        var rowCount = Math.Max(1, values.GetLength(0));
        var frameCount = Math.Max(1, values.GetLength(1));
        var width = Math.Min(HeatmapMaximumWidth, frameCount);
        var pixels = new int[checked(width * rowCount)];
        for (var row = 0; row < rowCount; row++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceFrame = Math.Min(frameCount - 1, (int)((long)x * frameCount / width));
                var color = ColorForZ(values[row, sourceFrame], zLimit);
                if (lowConfidenceLastRow && row == rowCount - 1)
                {
                    color = Interpolate(color, MissingColor, 0.45);
                }

                pixels[(row * width) + x] = ToArgb(color);
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            rowCount,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * sizeof(int));
        bitmap.Freeze();
        return bitmap;
    }

    private static Geometry? BuildCentroidTrajectory(
        FixedRoiTemporalAnalysis analysis,
        int currentFrameIndex,
        double canvasSize,
        double paddingPixels,
        out bool hasCentroid,
        out double centroidLeft,
        out double centroidTop)
    {
        hasCentroid = false;
        centroidLeft = 0.0;
        centroidTop = 0.0;
        var center = new Point(canvasSize / 2.0, canvasSize / 2.0);
        var radius = (canvasSize / 2.0) - paddingPixels;
        var geometry = new StreamGeometry();
        var hasFigure = false;
        var previousSign = FixedRoiActivitySign.None;
        using (var context = geometry.Open())
        {
            for (var index = analysis.BaselineFrameCount; index <= currentFrameIndex; index++)
            {
                var metrics = analysis.Frames[index].Propagation;
                if (!metrics.HasValue)
                {
                    hasFigure = false;
                    previousSign = FixedRoiActivitySign.None;
                    continue;
                }

                var point = CreatePoint(
                    center,
                    metrics.CentroidRadiusFraction * radius,
                    metrics.CentroidAngleRadians);
                if (!hasFigure || previousSign != metrics.DominantSign)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    hasFigure = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }

                previousSign = metrics.DominantSign;
            }
        }

        var current = analysis.Frames[currentFrameIndex].Propagation;
        if (current.HasValue)
        {
            var point = CreatePoint(center, current.CentroidRadiusFraction * radius, current.CentroidAngleRadians);
            hasCentroid = true;
            centroidLeft = point.X - 6.0;
            centroidTop = point.Y - 6.0;
        }

        return geometry.Bounds.IsEmpty ? null : Freeze(geometry);
    }

    private static string FormatMetricsSummary(
        FixedRoiGrid grid,
        FixedRoiTemporalAnalysis analysis,
        FixedRoiTemporalFrame frame,
        FixedRoiCell selectedCell,
        bool arrivalMode)
    {
        var selectedIndex = analysis.GetCellIndex(selectedCell.Id);
        var selectedZ = frame.ZScores[selectedIndex];
        var metrics = frame.Propagation;
        var modeText = arrivalMode ? "到达时间图" : "动态 z 图";
        var selectedText = double.IsFinite(selectedZ) ? $"{selectedZ:+0.00;-0.00;0.00}" : "无值";
        var confidence = selectedCell.IsCenter ? " · 中心低置信度" : string.Empty;
        if (!metrics.HasValue)
        {
            return $"{modeText} · 基线 {analysis.BaselineFrameCount}/10 帧 · {selectedCell.Id} z={selectedText} · 尚无连续 3 帧 |z|≥3 活动{confidence}";
        }

        var sign = metrics.DominantSign == FixedRoiActivitySign.Positive ? "+" : "−";
        var angleDegrees = metrics.CentroidAngleRadians * 180.0 / Math.PI;
        var earliest = analysis.Cells
            .Where(cell => cell.ArrivalSecondsAfterBaseline is not null)
            .MinBy(cell => cell.ArrivalSecondsAfterBaseline);
        var earliestText = earliest is null
            ? "未到达"
            : $"最早 {earliest.CellId}@{earliest.ArrivalSecondsAfterBaseline:0.###}s";
        return $"{modeText} · {selectedCell.Id} z={selectedText} · 主导{sign} · 活跃 {metrics.ActiveCellCount}/{grid.Cells.Count} · 重心 r/R={metrics.CentroidRadiusFraction:0.00}, θ={angleDegrees:0.#}° · 径向扩散={metrics.RadialSpreadFraction:0.00} · {earliestText}{confidence}";
    }

    private static string FormatCellTooltip(
        FixedRoiCell cell,
        FixedRoiTemporalFrame frame,
        FixedRoiTemporalCellSummary summary,
        int index)
    {
        var raw = FormatValue(frame.RawMeanConductivity[index]);
        var delta = FormatSigned(frame.BaselineDelta[index]);
        var z = FormatSigned(frame.ZScores[index]);
        var arrival = summary.ArrivalSecondsAfterBaseline is { } seconds
            ? $"{seconds:0.###}s"
            : "未到达";
        var confidence = cell.IsCenter ? " · 中心低置信度" : string.Empty;
        return $"{cell.Id} · 重构值={raw} · Δ={delta} · z={z} · 到达={arrival}{confidence}";
    }

    private static Geometry BuildCurveGeometry(IReadOnlyList<double> values, double zLimit)
    {
        var geometry = new StreamGeometry();
        var hasFigure = false;
        using (var context = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                if (!double.IsFinite(value))
                {
                    hasFigure = false;
                    continue;
                }

                var x = values.Count <= 1 ? 0.0 : index * CurveCanvasWidth / (values.Count - 1.0);
                var clamped = Math.Clamp(value, -zLimit, zLimit);
                var y = (zLimit - clamped) * CurveCanvasHeight / (2.0 * zLimit);
                var point = new Point(x, y);
                if (!hasFigure)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    hasFigure = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        return Freeze(geometry);
    }

    private static (string Start, string Middle, string End) FormatTimeAxis(
        IReadOnlyList<FixedRoiTemporalFrame> frames)
    {
        if (frames.Count == 0)
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        return (
            frames[0].CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            frames[frames.Count / 2].CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            frames[^1].CapturedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static Color ColorForZ(double value, double zLimit)
    {
        if (!double.IsFinite(value))
        {
            return MissingColor;
        }

        var normalized = Math.Clamp(value / zLimit, -1.0, 1.0);
        return normalized < 0.0
            ? Interpolate(NeutralColor, NegativeColor, -normalized)
            : Interpolate(NeutralColor, PositiveColor, normalized);
    }

    private static Color Interpolate(Color start, Color end, double amount)
    {
        var t = Math.Clamp(double.IsFinite(amount) ? amount : 0.0, 0.0, 1.0);
        return Color.FromRgb(
            (byte)Math.Round(start.R + ((end.R - start.R) * t)),
            (byte)Math.Round(start.G + ((end.G - start.G) * t)),
            (byte)Math.Round(start.B + ((end.B - start.B) * t)));
    }

    private static Point CreatePoint(Point center, double radius, double angleRadians)
    {
        return new Point(
            center.X - (radius * Math.Sin(angleRadians)),
            center.Y - (radius * Math.Cos(angleRadians)));
    }

    private static double CellCenterAngle(FixedRoiCell cell)
    {
        return cell.IsCenter
            ? 0.0
            : NormalizeAngle((cell.StartAngleRadians + cell.EndAngleRadians) / 2.0);
    }

    private static double NormalizeAngle(double angleRadians)
    {
        var normalized = angleRadians % (2.0 * Math.PI);
        return normalized < 0.0 ? normalized + (2.0 * Math.PI) : normalized;
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var key = ((uint)color.A << 24)
            | ((uint)color.R << 16)
            | ((uint)color.G << 8)
            | color.B;
        return BrushCache.GetOrAdd(key, _ =>
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        });
    }

    internal static IReadOnlyList<Geometry> GetMapCellGeometries(
        FixedRoiGrid grid,
        double canvasSize,
        double paddingPixels)
    {
        lock (MapGeometryCacheGate)
        {
            var key = new MapGeometryCacheKey(
                grid.ResolutionProfile.Id,
                grid.Cells.Count,
                canvasSize,
                paddingPixels);
            if (MapGeometryCache.TryGetValue(key, out var cached))
            {
                MapGeometryCacheLru.Remove(key);
                MapGeometryCacheLru.AddLast(key);
                return cached;
            }

            var geometries = grid.Cells
                .Select(cell => CreateCellGeometry(cell, canvasSize, paddingPixels))
                .ToArray();
            if (MapGeometryCache.Count >= MapGeometryCacheCapacity)
            {
                var oldestKey = MapGeometryCacheLru.First!.Value;
                MapGeometryCacheLru.RemoveFirst();
                MapGeometryCache.Remove(oldestKey);
            }

            MapGeometryCache[key] = geometries;
            MapGeometryCacheLru.AddLast(key);
            return geometries;
        }
    }

    private static DoubleCollection CreateDash(params double[] values)
    {
        var collection = new DoubleCollection(values);
        collection.Freeze();
        return collection;
    }

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        if (freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return freezable;
    }

    private static int ToArgb(Color color)
    {
        return unchecked((int)(0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
    }

    private static string FormatValue(double value)
    {
        return double.IsFinite(value) ? value.ToString("G5", CultureInfo.InvariantCulture) : "NaN";
    }

    private static string FormatSigned(double value)
    {
        return double.IsFinite(value)
            ? value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture)
            : "NaN";
    }

    private sealed record NamedSeries(string Label, IReadOnlyList<double> Values, SeriesKind Kind);

    private sealed record MapGeometryCacheKey(
        string ProfileId,
        int CellCount,
        double CanvasSize,
        double PaddingPixels);

    private enum SeriesKind
    {
        Selected,
        Neighbor,
        Mean
    }
}
