using EitHost.Core.Reconstruction;

namespace EitHost.Core.Analysis;

public enum RoiSelectionShape
{
    Square,
    Circle
}

public sealed record RoiDefinition(
    RoiSelectionShape Shape,
    double CenterX,
    double CenterY,
    double SizeFraction)
{
    public RoiDefinition Normalize()
    {
        return this with
        {
            CenterX = ClampFinite(CenterX, 0.0, 1.0, 0.5),
            CenterY = ClampFinite(CenterY, 0.0, 1.0, 0.5),
            SizeFraction = ClampFinite(SizeFraction, 0.01, 1.0, 0.2)
        };
    }

    internal bool Contains(double x, double y)
    {
        var normalized = Normalize();
        var half = normalized.SizeFraction / 2.0;
        var dx = x - normalized.CenterX;
        var dy = y - normalized.CenterY;
        return normalized.Shape == RoiSelectionShape.Circle
            ? (dx * dx) + (dy * dy) <= half * half
            : Math.Abs(dx) <= half && Math.Abs(dy) <= half;
    }

    private static double ClampFinite(double value, double min, double max, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}

public sealed record RoiConductivityMeasurement(
    double MeanConductivity,
    int SelectedCellCount,
    double AreaWeight,
    double MinConductivity,
    double MaxConductivity)
{
    public bool HasValue => SelectedCellCount > 0 && double.IsFinite(MeanConductivity);
}

public static class RoiConductivityAnalyzer
{
    public static IReadOnlyList<RoiConductivityMeasurement> MeasureAll(
        FixedRoiGrid grid,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        double paddingFraction = 0.05,
        string? parameterEntity = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(nodeCoords);
        ArgumentNullException.ThrowIfNull(cellConnectivity);
        ArgumentNullException.ThrowIfNull(conductivity);
        var measurements = Enumerable.Range(0, grid.Cells.Count)
            .Select(_ => Empty())
            .ToArray();
        if (nodeCoords.GetLength(0) == 0
            || nodeCoords.GetLength(1) == 0
            || cellConnectivity.GetLength(0) == 0
            || cellConnectivity.GetLength(1) < 3
            || conductivity.Count == 0)
        {
            return measurements;
        }

        var bounds = GetMeshBounds(nodeCoords);
        if (!bounds.IsValid)
        {
            return measurements;
        }

        var normalizedPadding = NormalizePadding(paddingFraction);
        var drawable = Math.Max(1.0e-9, 1.0 - (2.0 * normalizedPadding));
        var indexById = grid.Cells
            .Select((cell, index) => (cell.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var selectedCounts = new int[grid.Cells.Count];
        var weightedSums = new double[grid.Cells.Count];
        var totalWeights = new double[grid.Cells.Count];
        var minimums = Enumerable.Repeat(double.PositiveInfinity, grid.Cells.Count).ToArray();
        var maximums = Enumerable.Repeat(double.NegativeInfinity, grid.Cells.Count).ToArray();
        var meshCellCount = GetMeshCellCount(
            nodeCoords,
            cellConnectivity,
            conductivity,
            parameterEntity);
        for (var meshCell = 0; meshCell < meshCellCount; meshCell++)
        {
            var value = GetCellValue(
                meshCell,
                nodeCoords,
                cellConnectivity,
                conductivity,
                parameterEntity);
            if (!double.IsFinite(value))
            {
                continue;
            }

            var a = cellConnectivity[meshCell, 0];
            var b = cellConnectivity[meshCell, 1];
            var c = cellConnectivity[meshCell, 2];
            if (!TryGetDisplayPoint(nodeCoords, a, bounds, normalizedPadding, drawable, out var p0)
                || !TryGetDisplayPoint(nodeCoords, b, bounds, normalizedPadding, drawable, out var p1)
                || !TryGetDisplayPoint(nodeCoords, c, bounds, normalizedPadding, drawable, out var p2))
            {
                continue;
            }

            var centroidX = (p0.X + p1.X + p2.X) / 3.0;
            var centroidY = (p0.Y + p1.Y + p2.Y) / 3.0;
            var fixedCell = grid.HitTestNormalizedDisplayPoint(centroidX, centroidY, normalizedPadding);
            if (fixedCell is null || !indexById.TryGetValue(fixedCell.Id, out var fixedCellIndex))
            {
                continue;
            }

            var area = TriangleArea(p0, p1, p2);
            var weight = area > 1.0e-12 ? area : 1.0;
            selectedCounts[fixedCellIndex]++;
            weightedSums[fixedCellIndex] += value * weight;
            totalWeights[fixedCellIndex] += weight;
            minimums[fixedCellIndex] = Math.Min(minimums[fixedCellIndex], value);
            maximums[fixedCellIndex] = Math.Max(maximums[fixedCellIndex], value);
        }

        for (var index = 0; index < measurements.Length; index++)
        {
            if (selectedCounts[index] == 0 || totalWeights[index] <= 0.0)
            {
                continue;
            }

            measurements[index] = new RoiConductivityMeasurement(
                weightedSums[index] / totalWeights[index],
                selectedCounts[index],
                totalWeights[index],
                minimums[index],
                maximums[index]);
        }

        return measurements;
    }

    public static RoiConductivityMeasurement Measure(
        RoiDefinition roi,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        double paddingFraction = 0.05,
        string? parameterEntity = null)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var normalizedRoi = roi.Normalize();
        return MeasureCore(
            normalizedRoi.Contains,
            nodeCoords,
            cellConnectivity,
            conductivity,
            paddingFraction,
            parameterEntity);
    }

    public static RoiConductivityMeasurement Measure(
        FixedRoiCell roi,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        double paddingFraction = 0.05,
        string? parameterEntity = null)
    {
        ArgumentNullException.ThrowIfNull(roi);
        var normalizedPadding = NormalizePadding(paddingFraction);
        return MeasureCore(
            (x, y) => roi.ContainsNormalizedDisplayPoint(x, y, normalizedPadding),
            nodeCoords,
            cellConnectivity,
            conductivity,
            normalizedPadding,
            parameterEntity);
    }

    private static RoiConductivityMeasurement MeasureCore(
        Func<double, double, bool> contains,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        double paddingFraction,
        string? parameterEntity)
    {
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(nodeCoords);
        ArgumentNullException.ThrowIfNull(cellConnectivity);
        ArgumentNullException.ThrowIfNull(conductivity);
        if (nodeCoords.GetLength(0) == 0
            || nodeCoords.GetLength(1) == 0
            || cellConnectivity.GetLength(0) == 0
            || cellConnectivity.GetLength(1) < 3
            || conductivity.Count == 0)
        {
            return Empty();
        }

        var bounds = GetMeshBounds(nodeCoords);
        if (!bounds.IsValid)
        {
            return Empty();
        }

        var normalizedPadding = NormalizePadding(paddingFraction);
        var drawable = Math.Max(1.0e-9, 1.0 - (2.0 * normalizedPadding));
        var selectedCount = 0;
        var weightedSum = 0.0;
        var totalWeight = 0.0;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var cellCount = GetMeshCellCount(
            nodeCoords,
            cellConnectivity,
            conductivity,
            parameterEntity);

        for (var cell = 0; cell < cellCount; cell++)
        {
            var value = GetCellValue(
                cell,
                nodeCoords,
                cellConnectivity,
                conductivity,
                parameterEntity);
            if (!double.IsFinite(value))
            {
                continue;
            }

            var a = cellConnectivity[cell, 0];
            var b = cellConnectivity[cell, 1];
            var c = cellConnectivity[cell, 2];
            if (!TryGetDisplayPoint(nodeCoords, a, bounds, normalizedPadding, drawable, out var p0)
                || !TryGetDisplayPoint(nodeCoords, b, bounds, normalizedPadding, drawable, out var p1)
                || !TryGetDisplayPoint(nodeCoords, c, bounds, normalizedPadding, drawable, out var p2))
            {
                continue;
            }

            var centroidX = (p0.X + p1.X + p2.X) / 3.0;
            var centroidY = (p0.Y + p1.Y + p2.Y) / 3.0;
            if (!contains(centroidX, centroidY))
            {
                continue;
            }

            var area = TriangleArea(p0, p1, p2);
            var weight = area > 1.0e-12 ? area : 1.0;
            selectedCount++;
            weightedSum += value * weight;
            totalWeight += weight;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        if (selectedCount == 0 || totalWeight <= 0.0)
        {
            return Empty();
        }

        return new RoiConductivityMeasurement(
            weightedSum / totalWeight,
            selectedCount,
            totalWeight,
            min,
            max);
    }

    private static int GetMeshCellCount(
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        string? parameterEntity)
    {
        if (parameterEntity is null)
        {
            return Math.Min(cellConnectivity.GetLength(0), conductivity.Count);
        }

        var normalized = ReconstructionParameterEntity.Normalize(parameterEntity);
        var expected = normalized == ReconstructionParameterEntity.Node
            ? nodeCoords.GetLength(0)
            : cellConnectivity.GetLength(0);
        if (conductivity.Count != expected)
        {
            throw new InvalidDataException(
                $"ROI conductivity length does not match parameter_entity={normalized}: " +
                $"{conductivity.Count}/{expected}.");
        }

        return cellConnectivity.GetLength(0);
    }

    private static double GetCellValue(
        int cell,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        IReadOnlyList<double> conductivity,
        string? parameterEntity)
    {
        if (parameterEntity is null)
        {
            return conductivity[cell];
        }

        var normalized = ReconstructionParameterEntity.Normalize(parameterEntity);
        if (normalized == ReconstructionParameterEntity.Cell)
        {
            return conductivity[cell];
        }

        var sum = 0.0;
        for (var vertex = 0; vertex < cellConnectivity.GetLength(1); vertex++)
        {
            var node = cellConnectivity[cell, vertex];
            if (node < 0 || node >= nodeCoords.GetLength(0))
            {
                throw new InvalidDataException("ROI mesh contains an out-of-range node index.");
            }

            sum += conductivity[node];
        }

        return sum / cellConnectivity.GetLength(1);
    }

    private static RoiConductivityMeasurement Empty()
    {
        return new RoiConductivityMeasurement(double.NaN, 0, 0.0, double.NaN, double.NaN);
    }

    private static double NormalizePadding(double paddingFraction)
    {
        return Math.Clamp(
            double.IsFinite(paddingFraction) ? paddingFraction : 0.0,
            0.0,
            0.45);
    }

    private static MeshBounds GetMeshBounds(double[,] nodeCoords)
    {
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var minY = double.PositiveInfinity;
        var maxY = double.NegativeInfinity;
        var yColumn = Math.Min(1, nodeCoords.GetLength(1) - 1);
        for (var node = 0; node < nodeCoords.GetLength(0); node++)
        {
            var x = nodeCoords[node, 0];
            var y = nodeCoords[node, yColumn];
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                continue;
            }

            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        return new MeshBounds(minX, maxX, minY, maxY);
    }

    private static bool TryGetDisplayPoint(
        double[,] nodeCoords,
        int node,
        MeshBounds bounds,
        double padding,
        double drawable,
        out DisplayPoint point)
    {
        point = default;
        if (node < 0 || node >= nodeCoords.GetLength(0))
        {
            return false;
        }

        var yColumn = Math.Min(1, nodeCoords.GetLength(1) - 1);
        var x = nodeCoords[node, 0];
        var y = nodeCoords[node, yColumn];
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            return false;
        }

        var spanX = Math.Max(bounds.MaxX - bounds.MinX, 1.0e-12);
        var spanY = Math.Max(bounds.MaxY - bounds.MinY, 1.0e-12);
        point = new DisplayPoint(
            padding + ((x - bounds.MinX) / spanX * drawable),
            1.0 - padding - ((y - bounds.MinY) / spanY * drawable));
        return true;
    }

    private static double TriangleArea(DisplayPoint p0, DisplayPoint p1, DisplayPoint p2)
    {
        return Math.Abs(((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p2.X - p0.X) * (p1.Y - p0.Y))) / 2.0;
    }

    private readonly record struct DisplayPoint(double X, double Y);

    private readonly record struct MeshBounds(double MinX, double MaxX, double MinY, double MaxY)
    {
        public bool IsValid => double.IsFinite(MinX)
            && double.IsFinite(MaxX)
            && double.IsFinite(MinY)
            && double.IsFinite(MaxY);
    }
}
