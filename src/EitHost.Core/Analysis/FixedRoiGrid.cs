namespace EitHost.Core.Analysis;

public sealed record FixedRoiRingSpecification(
    double OuterRadiusFraction,
    int SectorCount);

public interface IFixedRoiResolutionProfile
{
    string Id { get; }

    string DisplayName { get; }

    double NominalResolutionDiameterFraction { get; }

    IReadOnlyList<FixedRoiRingSpecification> CreateRings();
}

public sealed class SunflowerNominalD10ResolutionProfile : IFixedRoiResolutionProfile
{
    public const double DefaultResolutionDiameterFraction = 0.10;

    public const int ElectrodeCount = 16;

    public static SunflowerNominalD10ResolutionProfile Instance { get; } = new();

    private SunflowerNominalD10ResolutionProfile()
    {
    }

    public string Id => "sunflower-16-adjacent-adjacent-d10-nominal-ccw-v2";

    public string DisplayName => "向日葵茎杆 16 电极 D/10 名义网格（E1 顶部逆时针）";

    public double NominalResolutionDiameterFraction => DefaultResolutionDiameterFraction;

    public IReadOnlyList<FixedRoiRingSpecification> CreateRings()
    {
        var ringCount = checked((int)Math.Round(
            0.5 / NominalResolutionDiameterFraction,
            MidpointRounding.AwayFromZero));
        var rings = new FixedRoiRingSpecification[ringCount];
        for (var ringIndex = 0; ringIndex < ringCount; ringIndex++)
        {
            var innerRadiusFraction = (double)ringIndex / ringCount;
            var outerRadiusFraction = (double)(ringIndex + 1) / ringCount;
            var middleRadiusDiameterFraction = (innerRadiusFraction + outerRadiusFraction) / 4.0;
            var sectorCount = ringIndex == 0
                ? 1
                : Math.Max(
                    1,
                    checked((int)Math.Round(
                        2.0 * Math.PI * middleRadiusDiameterFraction / NominalResolutionDiameterFraction,
                        MidpointRounding.AwayFromZero)));
            rings[ringIndex] = new FixedRoiRingSpecification(outerRadiusFraction, sectorCount);
        }

        return rings;
    }
}

public sealed record FixedRoiCell(
    string Id,
    int RingNumber,
    int SectorNumber,
    int SectorCount,
    double InnerRadiusFraction,
    double OuterRadiusFraction,
    double StartAngleRadians,
    double EndAngleRadians,
    string ResolutionProfileId)
{
    private const double BoundaryTolerance = 1.0e-12;

    public bool IsCenter => RingNumber == 1;

    public bool ContainsNormalizedDisplayPoint(
        double normalizedX,
        double normalizedY,
        double paddingFraction = 0.05)
    {
        if (!FixedRoiCoordinates.TryMapDisplayPoint(
                normalizedX,
                normalizedY,
                paddingFraction,
                out var radiusFraction,
                out var angleRadians))
        {
            return false;
        }

        var insideRadialBand = radiusFraction + BoundaryTolerance >= InnerRadiusFraction
            && (radiusFraction < OuterRadiusFraction - BoundaryTolerance
                || OuterRadiusFraction >= 1.0 - BoundaryTolerance
                && radiusFraction <= 1.0 + BoundaryTolerance);
        if (!insideRadialBand)
        {
            return false;
        }

        if (SectorCount == 1)
        {
            return true;
        }

        var sectorWidth = 2.0 * Math.PI / SectorCount;
        var sectorIndex = Math.Min(
            SectorCount - 1,
            (int)Math.Floor(FixedRoiCoordinates.NormalizeAngle(angleRadians + (sectorWidth / 2.0)) / sectorWidth));
        return sectorIndex == SectorNumber - 1;
    }
}

public sealed class FixedRoiGrid
{
    private readonly IReadOnlyList<IReadOnlyList<FixedRoiCell>> cellsByRing;

    public FixedRoiGrid(IFixedRoiResolutionProfile? resolutionProfile = null)
    {
        ResolutionProfile = resolutionProfile ?? SunflowerNominalD10ResolutionProfile.Instance;
        var ringSpecifications = ResolutionProfile.CreateRings()
            ?? throw new InvalidOperationException("固定 ROI 分辨率配置未返回环定义。");
        if (ringSpecifications.Count == 0)
        {
            throw new InvalidOperationException("固定 ROI 分辨率配置必须至少包含一个环。");
        }

        var allCells = new List<FixedRoiCell>();
        var ringCells = new List<IReadOnlyList<FixedRoiCell>>(ringSpecifications.Count);
        var innerRadiusFraction = 0.0;
        for (var ringIndex = 0; ringIndex < ringSpecifications.Count; ringIndex++)
        {
            var specification = ringSpecifications[ringIndex];
            ValidateRing(specification, innerRadiusFraction, ringIndex, ringSpecifications.Count);
            var sectorWidth = 2.0 * Math.PI / specification.SectorCount;
            var currentRing = new FixedRoiCell[specification.SectorCount];
            for (var sectorIndex = 0; sectorIndex < specification.SectorCount; sectorIndex++)
            {
                var ringNumber = ringIndex + 1;
                var sectorNumber = sectorIndex + 1;
                var id = specification.SectorCount == 1
                    ? $"R{ringNumber:00}-C"
                    : $"R{ringNumber:00}-S{sectorNumber:00}";
                var cell = new FixedRoiCell(
                    id,
                    ringNumber,
                    sectorNumber,
                    specification.SectorCount,
                    innerRadiusFraction,
                    specification.OuterRadiusFraction,
                    (-sectorWidth / 2.0) + (sectorIndex * sectorWidth),
                    (-sectorWidth / 2.0) + ((sectorIndex + 1) * sectorWidth),
                    ResolutionProfile.Id);
                currentRing[sectorIndex] = cell;
                allCells.Add(cell);
            }

            ringCells.Add(currentRing);
            innerRadiusFraction = specification.OuterRadiusFraction;
        }

        Cells = allCells;
        cellsByRing = ringCells;
        CenterCell = cellsByRing[0][0];
    }

    public IFixedRoiResolutionProfile ResolutionProfile { get; }

    public IReadOnlyList<FixedRoiCell> Cells { get; }

    public FixedRoiCell CenterCell { get; }

    public int RingCount => cellsByRing.Count;

    public IReadOnlyList<FixedRoiCell> GetRingCells(int ringNumber)
    {
        if (ringNumber < 1 || ringNumber > cellsByRing.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ringNumber));
        }

        return cellsByRing[ringNumber - 1];
    }

    public FixedRoiCell? HitTestNormalizedDisplayPoint(
        double normalizedX,
        double normalizedY,
        double paddingFraction = 0.05)
    {
        if (!FixedRoiCoordinates.TryMapDisplayPoint(
                normalizedX,
                normalizedY,
                paddingFraction,
                out var radiusFraction,
                out var angleRadians))
        {
            return null;
        }

        var ringIndex = -1;
        for (var index = 0; index < cellsByRing.Count; index++)
        {
            var outerRadius = cellsByRing[index][0].OuterRadiusFraction;
            if (radiusFraction < outerRadius || index == cellsByRing.Count - 1 && radiusFraction <= 1.0)
            {
                ringIndex = index;
                break;
            }
        }

        if (ringIndex < 0)
        {
            return null;
        }

        var candidates = cellsByRing[ringIndex];
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var sectorWidth = 2.0 * Math.PI / candidates.Count;
        var sectorIndex = Math.Min(
            candidates.Count - 1,
            (int)Math.Floor(FixedRoiCoordinates.NormalizeAngle(angleRadians + (sectorWidth / 2.0)) / sectorWidth));
        return candidates[sectorIndex];
    }

    private static void ValidateRing(
        FixedRoiRingSpecification specification,
        double innerRadiusFraction,
        int ringIndex,
        int ringCount)
    {
        if (!double.IsFinite(specification.OuterRadiusFraction)
            || specification.OuterRadiusFraction <= innerRadiusFraction
            || specification.OuterRadiusFraction > 1.0)
        {
            throw new InvalidOperationException($"固定 ROI 第 {ringIndex + 1} 环半径无效。");
        }

        if (specification.SectorCount < 1)
        {
            throw new InvalidOperationException($"固定 ROI 第 {ringIndex + 1} 环扇区数无效。");
        }

        if (ringIndex == 0 && specification.SectorCount != 1)
        {
            throw new InvalidOperationException("固定 ROI 中心环必须是不切分圆盘。");
        }

        if (ringIndex == ringCount - 1 && Math.Abs(specification.OuterRadiusFraction - 1.0) > 1.0e-12)
        {
            throw new InvalidOperationException("固定 ROI 最外环必须覆盖成像圆边界。");
        }
    }
}

internal static class FixedRoiCoordinates
{
    public static bool TryMapDisplayPoint(
        double normalizedX,
        double normalizedY,
        double paddingFraction,
        out double radiusFraction,
        out double angleRadians)
    {
        radiusFraction = double.NaN;
        angleRadians = double.NaN;
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY))
        {
            return false;
        }

        var padding = Math.Clamp(double.IsFinite(paddingFraction) ? paddingFraction : 0.0, 0.0, 0.45);
        var displayRadius = (1.0 - (2.0 * padding)) / 2.0;
        var dx = (normalizedX - 0.5) / displayRadius;
        var dy = (normalizedY - 0.5) / displayRadius;
        radiusFraction = Math.Sqrt((dx * dx) + (dy * dy));
        if (radiusFraction > 1.0 + 1.0e-12)
        {
            return false;
        }

        radiusFraction = Math.Min(radiusFraction, 1.0);
        angleRadians = NormalizeAngle(Math.Atan2(-dx, -dy));
        return true;
    }

    public static double NormalizeAngle(double angleRadians)
    {
        var normalized = angleRadians % (2.0 * Math.PI);
        return normalized < 0.0 ? normalized + (2.0 * Math.PI) : normalized;
    }
}
