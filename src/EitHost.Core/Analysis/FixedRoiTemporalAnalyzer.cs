namespace EitHost.Core.Analysis;

public sealed record FixedRoiTemporalOptions(
    int BaselineFrameCount = 10,
    double ZDisplayLimit = 5.0,
    double ArrivalZThreshold = 3.0,
    int ArrivalConsecutiveFrames = 3,
    double CenterConfidence = 0.35)
{
    public FixedRoiTemporalOptions Normalize()
    {
        return this with
        {
            BaselineFrameCount = Math.Clamp(BaselineFrameCount, 1, 1000),
            ZDisplayLimit = ClampFinite(ZDisplayLimit, 0.5, 100.0, 5.0),
            ArrivalZThreshold = ClampFinite(ArrivalZThreshold, 0.5, 100.0, 3.0),
            ArrivalConsecutiveFrames = Math.Clamp(ArrivalConsecutiveFrames, 1, 100),
            CenterConfidence = ClampFinite(CenterConfidence, 0.05, 1.0, 0.35)
        };
    }

    private static double ClampFinite(double value, double minimum, double maximum, double fallback)
    {
        return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}

public sealed record FixedRoiTemporalSample(
    int FrameIndex,
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    IReadOnlyList<double> MeanConductivity,
    IReadOnlyList<double> AreaWeights,
    IReadOnlyList<int> SelectedMeshCellCounts,
    int? ReferenceEpoch = null,
    string ReferenceLockKind = "legacy_unknown")
{
    public static FixedRoiTemporalSample FromMeasurements(
        int frameIndex,
        int blockNumber,
        DateTimeOffset capturedAt,
        double qualityWeight,
        IReadOnlyList<RoiConductivityMeasurement> measurements,
        int? referenceEpoch = null,
        string referenceLockKind = "legacy_unknown")
    {
        ArgumentNullException.ThrowIfNull(measurements);
        return new FixedRoiTemporalSample(
            frameIndex,
            blockNumber,
            capturedAt,
            double.IsFinite(qualityWeight) ? qualityWeight : 0.0,
            measurements.Select(measurement => measurement.MeanConductivity).ToArray(),
            measurements.Select(measurement => measurement.AreaWeight).ToArray(),
            measurements.Select(measurement => measurement.SelectedCellCount).ToArray(),
            referenceEpoch,
            referenceLockKind);
    }
}

public enum FixedRoiActivitySign
{
    None,
    Positive,
    Negative
}

public sealed record FixedRoiPropagationMetrics(
    int ActiveCellCount,
    FixedRoiActivitySign DominantSign,
    double CentroidRadiusFraction,
    double CentroidAngleRadians,
    double RadialSpreadFraction,
    double TotalActivityWeight)
{
    public bool HasValue => DominantSign != FixedRoiActivitySign.None
        && double.IsFinite(CentroidRadiusFraction)
        && double.IsFinite(CentroidAngleRadians);

    public static FixedRoiPropagationMetrics Empty { get; } = new(
        0,
        FixedRoiActivitySign.None,
        double.NaN,
        double.NaN,
        double.NaN,
        0.0);
}

public sealed record FixedRoiTemporalFrame(
    int FrameIndex,
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    IReadOnlyList<double> RawMeanConductivity,
    IReadOnlyList<double> BaselineDelta,
    IReadOnlyList<double> ZScores,
    IReadOnlyList<double> AreaWeights,
    IReadOnlyList<int> SelectedMeshCellCounts,
    IReadOnlyList<double> RingMeanZScores,
    double GlobalMeanZScore,
    FixedRoiPropagationMetrics Propagation,
    int? ReferenceEpoch = null,
    string ReferenceLockKind = "legacy_unknown");

public sealed record FixedRoiTemporalCellSummary(
    string CellId,
    double BaselineMedian,
    double RobustScale,
    int? ArrivalSeriesIndex,
    int? ArrivalFrameIndex,
    DateTimeOffset? ArrivalAt,
    double? ArrivalSecondsAfterBaseline,
    double PeakAbsoluteZ,
    double Confidence);

public sealed class FixedRoiTemporalAnalysis
{
    private readonly IReadOnlyDictionary<string, int> indexByCellId;

    internal FixedRoiTemporalAnalysis(
        FixedRoiTemporalOptions options,
        int baselineFrameCount,
        DateTimeOffset? baselineEndedAt,
        IReadOnlyList<FixedRoiTemporalFrame> frames,
        IReadOnlyList<FixedRoiTemporalCellSummary> cells)
    {
        Options = options;
        BaselineFrameCount = baselineFrameCount;
        BaselineEndedAt = baselineEndedAt;
        Frames = frames;
        Cells = cells;
        indexByCellId = cells
            .Select((cell, index) => (cell.CellId, index))
            .ToDictionary(item => item.CellId, item => item.index, StringComparer.Ordinal);
    }

    public FixedRoiTemporalOptions Options { get; }

    public int BaselineFrameCount { get; }

    public DateTimeOffset? BaselineEndedAt { get; }

    public IReadOnlyList<FixedRoiTemporalFrame> Frames { get; }

    public IReadOnlyList<FixedRoiTemporalCellSummary> Cells { get; }

    public int GetCellIndex(string cellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellId);
        return indexByCellId.TryGetValue(cellId, out var index)
            ? index
            : throw new ArgumentOutOfRangeException(nameof(cellId), cellId, "固定 ROI 单元不存在。");
    }
}

public static class FixedRoiTemporalAnalyzer
{
    private const double MinimumRobustScale = 1.0e-9;
    private const double MadConsistencyScale = 1.4826;

    public static FixedRoiTemporalAnalysis Analyze(
        FixedRoiGrid grid,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        FixedRoiTemporalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(samples);
        var normalizedOptions = (options ?? new FixedRoiTemporalOptions()).Normalize();
        ValidateSamples(grid, samples);
        if (samples.Count == 0)
        {
            return new FixedRoiTemporalAnalysis(
                normalizedOptions,
                0,
                null,
                [],
                grid.Cells.Select(cell => new FixedRoiTemporalCellSummary(
                    cell.Id,
                    double.NaN,
                    MinimumRobustScale,
                    null,
                    null,
                    null,
                    null,
                    double.NaN,
                    ConfidenceFor(cell, normalizedOptions))).ToArray());
        }

        var baselineSampleIndexes = samples
            .Select((sample, index) => (sample, index))
            .Where(item => item.sample.MeanConductivity.Any(double.IsFinite))
            .Take(normalizedOptions.BaselineFrameCount)
            .Select(item => item.index)
            .ToArray();
        var baselineFrameCount = baselineSampleIndexes.Length;
        var baselineEndSeriesIndex = baselineFrameCount == 0 ? -1 : baselineSampleIndexes[^1];
        var baselineEndedAt = baselineEndSeriesIndex >= 0
            ? samples[baselineEndSeriesIndex].CapturedAt
            : (DateTimeOffset?)null;
        var medians = new double[grid.Cells.Count];
        var localScales = new double[grid.Cells.Count];
        for (var cellIndex = 0; cellIndex < grid.Cells.Count; cellIndex++)
        {
            var values = baselineSampleIndexes
                .Select(index => samples[index].MeanConductivity[cellIndex])
                .Where(double.IsFinite)
                .ToArray();
            medians[cellIndex] = Median(values);
            localScales[cellIndex] = values.Length == 0 || !double.IsFinite(medians[cellIndex])
                ? double.NaN
                : MadConsistencyScale * Median(values
                    .Select(value => Math.Abs(value - medians[cellIndex]))
                    .ToArray());
        }

        var positiveScales = localScales
            .Where(scale => double.IsFinite(scale) && scale > MinimumRobustScale)
            .ToArray();
        var globalScaleFloor = positiveScales.Length == 0
            ? MinimumRobustScale
            : Math.Max(MinimumRobustScale, Median(positiveScales));
        var robustScales = localScales
            .Select(scale => Math.Max(
                globalScaleFloor,
                double.IsFinite(scale) && scale > 0.0 ? scale : MinimumRobustScale))
            .ToArray();

        var frameDrafts = new List<FrameDraft>(samples.Count);
        foreach (var sample in samples)
        {
            var deltas = new double[grid.Cells.Count];
            var zScores = new double[grid.Cells.Count];
            for (var cellIndex = 0; cellIndex < grid.Cells.Count; cellIndex++)
            {
                var raw = sample.MeanConductivity[cellIndex];
                if (!double.IsFinite(raw) || !double.IsFinite(medians[cellIndex]))
                {
                    deltas[cellIndex] = double.NaN;
                    zScores[cellIndex] = double.NaN;
                    continue;
                }

                deltas[cellIndex] = raw - medians[cellIndex];
                zScores[cellIndex] = deltas[cellIndex] / robustScales[cellIndex];
            }

            var ringMeans = CalculateRingMeans(grid, zScores, sample.AreaWeights);
            var globalMean = WeightedMean(zScores, sample.AreaWeights, Enumerable.Repeat(1.0, grid.Cells.Count));
            frameDrafts.Add(new FrameDraft(sample, deltas, zScores, ringMeans, globalMean));
        }

        var cellSummaries = CalculateCellSummaries(
            grid,
            samples,
            frameDrafts,
            medians,
            robustScales,
            baselineEndSeriesIndex,
            baselineEndedAt,
            normalizedOptions);
        var frames = frameDrafts
            .Select(draft => new FixedRoiTemporalFrame(
                draft.Sample.FrameIndex,
                draft.Sample.BlockNumber,
                draft.Sample.CapturedAt,
                draft.Sample.QualityWeight,
                draft.Sample.MeanConductivity,
                draft.Deltas,
                draft.ZScores,
                draft.Sample.AreaWeights,
                draft.Sample.SelectedMeshCellCounts,
                draft.RingMeans,
                draft.GlobalMean,
                CalculatePropagation(grid, draft, normalizedOptions),
                draft.Sample.ReferenceEpoch,
                draft.Sample.ReferenceLockKind))
            .ToArray();
        return new FixedRoiTemporalAnalysis(
            normalizedOptions,
            baselineFrameCount,
            baselineEndedAt,
            frames,
            cellSummaries);
    }

    private static IReadOnlyList<FixedRoiTemporalCellSummary> CalculateCellSummaries(
        FixedRoiGrid grid,
        IReadOnlyList<FixedRoiTemporalSample> samples,
        IReadOnlyList<FrameDraft> frames,
        IReadOnlyList<double> medians,
        IReadOnlyList<double> robustScales,
        int baselineEndSeriesIndex,
        DateTimeOffset? baselineEndedAt,
        FixedRoiTemporalOptions options)
    {
        var summaries = new FixedRoiTemporalCellSummary[grid.Cells.Count];
        for (var cellIndex = 0; cellIndex < grid.Cells.Count; cellIndex++)
        {
            int? arrivalSeriesIndex = null;
            var runLength = 0;
            var runStartIndex = -1;
            int? previousFrameIndex = null;
            for (var frameIndex = baselineEndSeriesIndex + 1; frameIndex < frames.Count; frameIndex++)
            {
                if (previousFrameIndex is { } previous
                    && samples[frameIndex].FrameIndex != previous + 1)
                {
                    runLength = 0;
                    runStartIndex = -1;
                }

                var z = frames[frameIndex].ZScores[cellIndex];
                if (double.IsFinite(z) && Math.Abs(z) >= options.ArrivalZThreshold)
                {
                    if (runLength == 0)
                    {
                        runStartIndex = frameIndex;
                    }

                    runLength++;
                    if (runLength >= options.ArrivalConsecutiveFrames)
                    {
                        arrivalSeriesIndex = runStartIndex;
                        break;
                    }
                }
                else
                {
                    runLength = 0;
                    runStartIndex = -1;
                }

                previousFrameIndex = samples[frameIndex].FrameIndex;
            }

            var arrivalSample = arrivalSeriesIndex is { } index ? samples[index] : null;
            var peakAbsoluteZ = frames
                .Skip(Math.Max(0, baselineEndSeriesIndex + 1))
                .Select(frame => frame.ZScores[cellIndex])
                .Where(double.IsFinite)
                .Select(Math.Abs)
                .DefaultIfEmpty(double.NaN)
                .Max();
            summaries[cellIndex] = new FixedRoiTemporalCellSummary(
                grid.Cells[cellIndex].Id,
                medians[cellIndex],
                robustScales[cellIndex],
                arrivalSeriesIndex,
                arrivalSample?.FrameIndex,
                arrivalSample?.CapturedAt,
                arrivalSample is null || baselineEndedAt is null
                    ? null
                    : Math.Max(0.0, (arrivalSample.CapturedAt - baselineEndedAt.Value).TotalSeconds),
                peakAbsoluteZ,
                ConfidenceFor(grid.Cells[cellIndex], options));
        }

        return summaries;
    }

    private static FixedRoiPropagationMetrics CalculatePropagation(
        FixedRoiGrid grid,
        FrameDraft frame,
        FixedRoiTemporalOptions options)
    {
        var positiveWeight = 0.0;
        var negativeWeight = 0.0;
        var activeCellCount = 0;
        for (var index = 0; index < grid.Cells.Count; index++)
        {
            var z = frame.ZScores[index];
            var area = frame.Sample.AreaWeights[index];
            if (!double.IsFinite(z) || !double.IsFinite(area) || area <= 0.0 || Math.Abs(z) < options.ArrivalZThreshold)
            {
                continue;
            }

            activeCellCount++;
            var weight = Math.Abs(z) * area * ConfidenceFor(grid.Cells[index], options);
            if (z > 0.0)
            {
                positiveWeight += weight;
            }
            else
            {
                negativeWeight += weight;
            }
        }

        var sign = positiveWeight <= 0.0 && negativeWeight <= 0.0
            ? FixedRoiActivitySign.None
            : positiveWeight >= negativeWeight
                ? FixedRoiActivitySign.Positive
                : FixedRoiActivitySign.Negative;
        if (sign == FixedRoiActivitySign.None)
        {
            return FixedRoiPropagationMetrics.Empty;
        }

        var totalWeight = 0.0;
        var xWeighted = 0.0;
        var yWeighted = 0.0;
        var radiusWeighted = 0.0;
        for (var index = 0; index < grid.Cells.Count; index++)
        {
            var z = frame.ZScores[index];
            var area = frame.Sample.AreaWeights[index];
            if (!double.IsFinite(z)
                || !double.IsFinite(area)
                || area <= 0.0
                || Math.Abs(z) < options.ArrivalZThreshold
                || sign == FixedRoiActivitySign.Positive && z <= 0.0
                || sign == FixedRoiActivitySign.Negative && z >= 0.0)
            {
                continue;
            }

            var cell = grid.Cells[index];
            var radius = (cell.InnerRadiusFraction + cell.OuterRadiusFraction) / 2.0;
            var angle = CellCenterAngle(cell);
            var weight = Math.Abs(z) * area * ConfidenceFor(cell, options);
            totalWeight += weight;
            xWeighted += -Math.Sin(angle) * radius * weight;
            yWeighted += -Math.Cos(angle) * radius * weight;
            radiusWeighted += radius * weight;
        }

        if (totalWeight <= 0.0)
        {
            return FixedRoiPropagationMetrics.Empty;
        }

        var centroidX = xWeighted / totalWeight;
        var centroidY = yWeighted / totalWeight;
        var centroidRadius = Math.Min(1.0, Math.Sqrt((centroidX * centroidX) + (centroidY * centroidY)));
        var centroidAngle = FixedRoiCoordinates.NormalizeAngle(Math.Atan2(-centroidX, -centroidY));
        var radialMean = radiusWeighted / totalWeight;
        var spreadWeighted = 0.0;
        for (var index = 0; index < grid.Cells.Count; index++)
        {
            var z = frame.ZScores[index];
            var area = frame.Sample.AreaWeights[index];
            if (!double.IsFinite(z)
                || !double.IsFinite(area)
                || area <= 0.0
                || Math.Abs(z) < options.ArrivalZThreshold
                || sign == FixedRoiActivitySign.Positive && z <= 0.0
                || sign == FixedRoiActivitySign.Negative && z >= 0.0)
            {
                continue;
            }

            var cell = grid.Cells[index];
            var radius = (cell.InnerRadiusFraction + cell.OuterRadiusFraction) / 2.0;
            var weight = Math.Abs(z) * area * ConfidenceFor(cell, options);
            spreadWeighted += (radius - radialMean) * (radius - radialMean) * weight;
        }

        return new FixedRoiPropagationMetrics(
            activeCellCount,
            sign,
            centroidRadius,
            centroidAngle,
            Math.Sqrt(spreadWeighted / totalWeight),
            totalWeight);
    }

    private static double[] CalculateRingMeans(
        FixedRoiGrid grid,
        IReadOnlyList<double> values,
        IReadOnlyList<double> areaWeights)
    {
        var ringMeans = new double[grid.RingCount];
        for (var ringNumber = 1; ringNumber <= grid.RingCount; ringNumber++)
        {
            var indexes = grid.Cells
                .Select((cell, index) => (cell, index))
                .Where(item => item.cell.RingNumber == ringNumber)
                .Select(item => item.index)
                .ToArray();
            ringMeans[ringNumber - 1] = WeightedMean(
                indexes.Select(index => values[index]).ToArray(),
                indexes.Select(index => areaWeights[index]).ToArray(),
                Enumerable.Repeat(1.0, indexes.Length));
        }

        return ringMeans;
    }

    private static double WeightedMean(
        IEnumerable<double> values,
        IEnumerable<double> areaWeights,
        IEnumerable<double> confidenceWeights)
    {
        var weightedSum = 0.0;
        var totalWeight = 0.0;
        using var valueEnumerator = values.GetEnumerator();
        using var areaEnumerator = areaWeights.GetEnumerator();
        using var confidenceEnumerator = confidenceWeights.GetEnumerator();
        while (valueEnumerator.MoveNext() && areaEnumerator.MoveNext() && confidenceEnumerator.MoveNext())
        {
            var value = valueEnumerator.Current;
            var area = areaEnumerator.Current;
            var confidence = confidenceEnumerator.Current;
            if (!double.IsFinite(value)
                || !double.IsFinite(area)
                || !double.IsFinite(confidence)
                || area <= 0.0
                || confidence <= 0.0)
            {
                continue;
            }

            var weight = area * confidence;
            weightedSum += value * weight;
            totalWeight += weight;
        }

        return totalWeight > 0.0 ? weightedSum / totalWeight : double.NaN;
    }

    private static double CellCenterAngle(FixedRoiCell cell)
    {
        return cell.IsCenter
            ? 0.0
            : FixedRoiCoordinates.NormalizeAngle((cell.StartAngleRadians + cell.EndAngleRadians) / 2.0);
    }

    private static double ConfidenceFor(FixedRoiCell cell, FixedRoiTemporalOptions options)
    {
        return cell.IsCenter ? options.CenterConfidence : 1.0;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return double.NaN;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 0
            ? (finite[middle - 1] + finite[middle]) / 2.0
            : finite[middle];
    }

    private static void ValidateSamples(FixedRoiGrid grid, IReadOnlyList<FixedRoiTemporalSample> samples)
    {
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index] ?? throw new InvalidOperationException($"固定 ROI 第 {index + 1} 帧为空。");
            if (sample.MeanConductivity.Count != grid.Cells.Count
                || sample.AreaWeights.Count != grid.Cells.Count
                || sample.SelectedMeshCellCounts.Count != grid.Cells.Count)
            {
                throw new InvalidOperationException($"固定 ROI 第 {index + 1} 帧必须包含 {grid.Cells.Count} 个单元值。");
            }
        }
    }

    private sealed record FrameDraft(
        FixedRoiTemporalSample Sample,
        IReadOnlyList<double> Deltas,
        IReadOnlyList<double> ZScores,
        IReadOnlyList<double> RingMeans,
        double GlobalMean);
}
