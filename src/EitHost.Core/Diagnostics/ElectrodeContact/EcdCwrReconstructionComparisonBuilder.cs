using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrReconstructionComparisonBuilder
{
    public EcdCwrReconstructionComparisonBuildReport Build(
        EcdCwrSimulationBatchManifest manifest,
        IReadOnlyList<EcdCwrReconstructionResultReference> reconstructionResults)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(reconstructionResults);
        var workItemByScenario = manifest.WorkItems.ToDictionary(
            item => item.ScenarioId,
            StringComparer.OrdinalIgnoreCase);
        var cache = new ComparisonBuildCache();
        var items = reconstructionResults
            .Select(reference => BuildItem(reference, workItemByScenario, cache))
            .ToArray();
        return new EcdCwrReconstructionComparisonBuildReport(
            DateTimeOffset.Now,
            reconstructionResults.Count,
            items.Count(item => item.Passed),
            items.Count(item => !item.Passed),
            items.Where(item => item.Comparison is not null).Select(item => item.Comparison!).ToArray(),
            items);
    }

    public static string ToMarkdown(EcdCwrReconstructionComparisonBuildReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Reconstruction CC",
            "",
            $"- Built at: {report.BuiltAt:O}",
            $"- Inputs: {report.InputCount}",
            $"- Comparisons: {report.Comparisons.Count}",
            $"- Failed: {report.FailedItems}",
            "",
            "## Comparisons",
            "",
            "|scenario|method|cc|",
            "|---|---|---:|"
        };
        foreach (var comparison in report.Comparisons)
        {
            lines.Add(
                $"|{comparison.ScenarioId}|{comparison.Method}|{comparison.CorrelationCoefficient:F6}|");
        }

        lines.Add("");
        lines.Add("## Issues");
        lines.Add("");
        lines.Add("|scenario|method|issues|");
        lines.Add("|---|---|---|");
        foreach (var item in report.Items.Where(item => item.Issues.Count > 0))
        {
            lines.Add(
                $"|{item.ScenarioId}|{item.Method}|{string.Join("<br>", item.Issues)}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static EcdCwrReconstructionComparisonBuildItem BuildItem(
        EcdCwrReconstructionResultReference reference,
        IReadOnlyDictionary<string, EcdCwrSimulationWorkItem> workItemByScenario,
        ComparisonBuildCache cache)
    {
        var issues = new List<string>();
        if (!workItemByScenario.TryGetValue(reference.ScenarioId, out var workItem))
        {
            issues.Add("scenario not found in manifest");
            return Failed(reference, issues);
        }

        if (!File.Exists(workItem.OutputHdf5Path))
        {
            issues.Add("missing simulation HDF5");
        }

        if (!File.Exists(reference.ResultHdf5Path))
        {
            issues.Add("missing reconstruction HDF5");
        }

        if (issues.Count > 0)
        {
            return Failed(reference, issues);
        }

        try
        {
            var truth = ReadTruthVector(workItem.OutputHdf5Path, cache);
            var reconstruction = ReadVector(reference.ResultHdf5Path, "/conductivity");
            var comparableTruth = truth.Length == reconstruction.Length
                ? truth
                : MapTruthToReconstructionMesh(
                    truth,
                    workItem.OutputHdf5Path,
                    reconstruction.Length,
                    reference.ResultHdf5Path,
                    cache);

            var cc = PearsonCorrelation(comparableTruth, reconstruction);
            if (!double.IsFinite(cc))
            {
                issues.Add("correlation is undefined for degenerate truth/reconstruction");
                return Failed(reference, issues);
            }

            var fitSummary = ReadVoltageFitSummary(reference.ResultHdf5Path);
            var comparison = new EcdCwrReconstructionComparison(
                reference.ScenarioId,
                reference.Method,
                cc,
                fitSummary?.ResidualNorm,
                fitSummary?.RelativeResidual,
                fitSummary?.CosineSimilarity,
                fitSummary?.ConditionNumber,
                fitSummary?.ImageQualityScore,
                fitSummary?.ResidualL1Norm,
                fitSummary?.RelativeL1Residual,
                fitSummary?.ResidualLinfNorm,
                fitSummary?.MeasuredNorm,
                fitSummary?.SimulatedNorm,
                fitSummary?.R2,
                fitSummary?.ConductivityRange,
                reference.DiagnosticPolicyVersion,
                reference.MethodPolicyVersion);
            return new EcdCwrReconstructionComparisonBuildItem(
                reference.ScenarioId,
                reference.Method,
                reference.ResultHdf5Path,
                issues,
                comparison);
        }
        catch (Exception ex)
        {
            issues.Add($"comparison failed: {ex.Message}");
            return Failed(reference, issues);
        }
    }

    private static double[] ReadTruthVector(string hdf5Path, ComparisonBuildCache cache)
    {
        var fullPath = Path.GetFullPath(hdf5Path);
        if (!cache.TruthVectors.TryGetValue(fullPath, out var truth))
        {
            truth = ReadVector(fullPath, "/ground_truth_conductivity");
            cache.TruthVectors.Add(fullPath, truth);
        }

        return truth;
    }

    private static VoltageFitSummary? ReadVoltageFitSummary(string hdf5Path)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(hdf5Path);
        if (!file.LinkExists("/measured") || !file.LinkExists("/simulated"))
        {
            return null;
        }

        var measured = ReadVector(hdf5Path, "/measured");
        var simulated = ReadVector(hdf5Path, "/simulated");
        if (measured.Length == 0 || measured.Length != simulated.Length)
        {
            return null;
        }

        var residualSquare = 0.0;
        var residualL1 = 0.0;
        var residualLinf = 0.0;
        var measuredSquare = 0.0;
        var measuredL1 = 0.0;
        var simulatedSquare = 0.0;
        var dot = 0.0;
        var measuredMean = measured.Average();
        var measuredCenteredSquare = 0.0;
        for (var index = 0; index < measured.Length; index++)
        {
            if (!double.IsFinite(measured[index]) || !double.IsFinite(simulated[index]))
            {
                return null;
            }

            var delta = measured[index] - simulated[index];
            residualSquare += delta * delta;
            residualL1 += Math.Abs(delta);
            residualLinf = Math.Max(residualLinf, Math.Abs(delta));
            measuredSquare += measured[index] * measured[index];
            measuredL1 += Math.Abs(measured[index]);
            simulatedSquare += simulated[index] * simulated[index];
            dot += measured[index] * simulated[index];
            var centered = measured[index] - measuredMean;
            measuredCenteredSquare += centered * centered;
        }

        var residualNorm = Math.Sqrt(residualSquare);
        var measuredNorm = Math.Sqrt(measuredSquare);
        var simulatedNorm = Math.Sqrt(simulatedSquare);
        var relativeResidual = residualNorm / Math.Max(measuredNorm, 1.0e-12);
        var relativeL1 = residualL1 / Math.Max(measuredL1, 1.0e-12);
        var cosine = measuredNorm <= 0.0 || simulatedNorm <= 0.0
            ? 0.0
            : Math.Clamp(dot / (measuredNorm * simulatedNorm), -1.0, 1.0);
        var r2 = 1.0 - (residualSquare / Math.Max(measuredCenteredSquare, 1.0e-12));
        var conductivity = ReadVector(hdf5Path, "/conductivity");
        var conductivityRange = conductivity.Length == 0
            ? 0.0
            : conductivity.Max() - conductivity.Min();
        var fitQuality = EcdCwrImageQualityEstimator.ReconstructionFitQuality(
            residualNorm,
            relativeResidual,
            cosine,
            residualL1,
            relativeL1,
            residualLinf,
            measuredNorm,
            simulatedNorm,
            r2,
            conductivityRange);
        var conditionNumber = TryReadDoubleScalar(
            file,
            "/weighted_system_condition_number",
            "/condition_number",
            "/condition_estimate");
        return new VoltageFitSummary(
            residualNorm,
            relativeResidual,
            cosine,
            conditionNumber,
            fitQuality,
            residualL1,
            relativeL1,
            residualLinf,
            measuredNorm,
            simulatedNorm,
            r2,
            conductivityRange);
    }

    private static EcdCwrReconstructionComparisonBuildItem Failed(
        EcdCwrReconstructionResultReference reference,
        IReadOnlyList<string> issues)
    {
        return new EcdCwrReconstructionComparisonBuildItem(
            reference.ScenarioId,
            reference.Method,
            reference.ResultHdf5Path,
            issues,
            null);
    }

    private static double[] ReadVector(string hdf5Path, string datasetPath)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(hdf5Path);
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubleVector))
        {
            return doubleVector;
        }

        if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var floatVector))
        {
            return floatVector.Select(value => (double)value).ToArray();
        }

        throw new InvalidDataException($"Unsupported vector dataset type at {datasetPath}.");
    }

    private static double? TryReadDoubleScalar(IH5Group file, params string[] datasetPaths)
    {
        foreach (var datasetPath in datasetPaths)
        {
            if (!file.LinkExists(datasetPath))
            {
                continue;
            }

            try
            {
                var dataset = file.Dataset(datasetPath);
                if (TryRead(() => dataset.Read<double>(), out var doubleScalar) &&
                    double.IsFinite(doubleScalar))
                {
                    return doubleScalar;
                }

                if (TryRead(() => dataset.Read<float>(), out var floatScalar) &&
                    double.IsFinite(floatScalar))
                {
                    return floatScalar;
                }

                var vector = ReadDoubleVector(dataset);
                if (vector.Length > 0 && double.IsFinite(vector[0]))
                {
                    return vector[0];
                }
            }
            catch
            {
                // Optional reconstruction diagnostic scalar.
            }
        }

        return null;
    }

    private static double[] ReadDoubleVector(IH5Dataset dataset)
    {
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubleVector))
        {
            return doubleVector;
        }

        if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var floatVector))
        {
            return floatVector.Select(value => (double)value).ToArray();
        }

        throw new InvalidDataException("Unsupported scalar/vector dataset type.");
    }

    private static double[] MapTruthToReconstructionMesh(
        IReadOnlyList<double> truth,
        string truthHdf5Path,
        int reconstructionLength,
        string reconstructionHdf5Path,
        ComparisonBuildCache cache)
    {
        var mapKey = new MeshLengthMapKey(truth.Count, reconstructionLength);
        if (!cache.MeshIndexMaps.TryGetValue(mapKey, out var nearestTruthIndices))
        {
            nearestTruthIndices = BuildNearestTruthIndexMap(
                truth,
                truthHdf5Path,
                reconstructionLength,
                reconstructionHdf5Path);
            cache.MeshIndexMaps.Add(mapKey, nearestTruthIndices);
        }

        var mapped = new double[reconstructionLength];
        for (var reconIndex = 0; reconIndex < reconstructionLength; reconIndex++)
        {
            mapped[reconIndex] = truth[nearestTruthIndices[reconIndex]];
        }

        return mapped;
    }

    private static int[] BuildNearestTruthIndexMap(
        IReadOnlyList<double> truth,
        string truthHdf5Path,
        int reconstructionLength,
        string reconstructionHdf5Path)
    {
        var truthMesh = ReadMesh(truthHdf5Path);
        var reconstructionMesh = ReadMesh(reconstructionHdf5Path);
        if (truthMesh.CellConnectivity.GetLength(0) != truth.Count)
        {
            throw new InvalidDataException(
                $"truth conductivity length {truth.Count} != truth mesh cells {truthMesh.CellConnectivity.GetLength(0)}.");
        }

        if (reconstructionMesh.CellConnectivity.GetLength(0) != reconstructionLength)
        {
            throw new InvalidDataException(
                $"reconstruction conductivity length {reconstructionLength} != reconstruction mesh cells {reconstructionMesh.CellConnectivity.GetLength(0)}.");
        }

        var truthCentroids = ComputeCellCentroids(truthMesh);
        var reconstructionCentroids = ComputeCellCentroids(reconstructionMesh);
        var nearestTruthIndices = new int[reconstructionLength];
        for (var reconIndex = 0; reconIndex < reconstructionLength; reconIndex++)
        {
            nearestTruthIndices[reconIndex] = FindNearestCentroid(reconstructionCentroids, reconIndex, truthCentroids);
        }

        return nearestTruthIndices;
    }

    private static MeshData ReadMesh(string hdf5Path)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(hdf5Path);
        return new MeshData(
            ReadDoubleMatrix(file, "/node_coords"),
            ReadIntMatrix(file, "/cell_connectivity"));
    }

    private static double[,] ReadDoubleMatrix(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubleMatrix))
        {
            return doubleMatrix;
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floatMatrix))
        {
            return ConvertToDouble(floatMatrix);
        }

        throw new InvalidDataException($"Unsupported matrix dataset type at {datasetPath}.");
    }

    private static int[,] ReadIntMatrix(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<int[,]>(memoryDims: dimensions), out var intMatrix))
        {
            return intMatrix;
        }

        if (TryRead(() => dataset.Read<long[,]>(memoryDims: dimensions), out var longMatrix))
        {
            return ConvertToInt(longMatrix);
        }

        if (TryRead(() => dataset.Read<uint[,]>(memoryDims: dimensions), out var uintMatrix))
        {
            return ConvertToInt(uintMatrix);
        }

        throw new InvalidDataException($"Unsupported integer matrix dataset type at {datasetPath}.");
    }

    private static double[,] ComputeCellCentroids(MeshData mesh)
    {
        var cellCount = mesh.CellConnectivity.GetLength(0);
        var cellWidth = mesh.CellConnectivity.GetLength(1);
        var dimensions = mesh.NodeCoords.GetLength(1);
        var centroids = new double[cellCount, dimensions];
        for (var cell = 0; cell < cellCount; cell++)
        {
            for (var corner = 0; corner < cellWidth; corner++)
            {
                var nodeIndex = mesh.CellConnectivity[cell, corner];
                if (nodeIndex < 0 || nodeIndex >= mesh.NodeCoords.GetLength(0))
                {
                    throw new InvalidDataException($"cell_connectivity contains node index {nodeIndex} outside node_coords.");
                }

                for (var dimension = 0; dimension < dimensions; dimension++)
                {
                    centroids[cell, dimension] += mesh.NodeCoords[nodeIndex, dimension] / cellWidth;
                }
            }
        }

        return centroids;
    }

    private static int FindNearestCentroid(double[,] sourceCentroids, int sourceIndex, double[,] targetCentroids)
    {
        var dimensions = Math.Min(sourceCentroids.GetLength(1), targetCentroids.GetLength(1));
        var bestIndex = 0;
        var bestDistance = double.PositiveInfinity;
        for (var targetIndex = 0; targetIndex < targetCentroids.GetLength(0); targetIndex++)
        {
            var distance = 0.0;
            for (var dimension = 0; dimension < dimensions; dimension++)
            {
                var delta = sourceCentroids[sourceIndex, dimension] - targetCentroids[targetIndex, dimension];
                distance += delta * delta;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = targetIndex;
            }
        }

        return bestIndex;
    }

    private static double[,] ConvertToDouble(float[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var converted = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                converted[row, column] = values[row, column];
            }
        }

        return converted;
    }

    private static int[,] ConvertToInt(long[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var converted = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                converted[row, column] = checked((int)values[row, column]);
            }
        }

        return converted;
    }

    private static int[,] ConvertToInt(uint[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var converted = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                converted[row, column] = checked((int)values[row, column]);
            }
        }

        return converted;
    }

    private static double PearsonCorrelation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return double.NaN;
        }

        var meanLeft = left.Average();
        var meanRight = right.Average();
        var numerator = 0.0;
        var leftSquare = 0.0;
        var rightSquare = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            var centeredLeft = left[index] - meanLeft;
            var centeredRight = right[index] - meanRight;
            numerator += centeredLeft * centeredRight;
            leftSquare += centeredLeft * centeredLeft;
            rightSquare += centeredRight * centeredRight;
        }

        if (leftSquare <= double.Epsilon)
        {
            return double.NaN;
        }

        if (rightSquare <= double.Epsilon)
        {
            return 0.0;
        }

        return numerator / Math.Sqrt(leftSquare * rightSquare);
    }

    private static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    private sealed record MeshData(
        double[,] NodeCoords,
        int[,] CellConnectivity);

    private sealed class ComparisonBuildCache
    {
        public Dictionary<string, double[]> TruthVectors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<MeshLengthMapKey, int[]> MeshIndexMaps { get; } = [];
    }

    private readonly record struct MeshLengthMapKey(
        int TruthLength,
        int ReconstructionLength);

    private sealed record VoltageFitSummary(
        double ResidualNorm,
        double RelativeResidual,
        double CosineSimilarity,
        double? ConditionNumber,
        double? ImageQualityScore,
        double ResidualL1Norm,
        double RelativeL1Residual,
        double ResidualLinfNorm,
        double MeasuredNorm,
        double SimulatedNorm,
        double R2,
        double ConductivityRange);
}

public sealed record EcdCwrReconstructionResultReference(
    string ScenarioId,
    string Method,
    string ResultHdf5Path,
    string? DiagnosticPolicyVersion = null,
    string? MethodPolicyVersion = null);

public sealed record EcdCwrReconstructionComparisonBuildReport(
    DateTimeOffset BuiltAt,
    int InputCount,
    int PassedItems,
    int FailedItems,
    IReadOnlyList<EcdCwrReconstructionComparison> Comparisons,
    IReadOnlyList<EcdCwrReconstructionComparisonBuildItem> Items)
{
    public bool Passed => FailedItems == 0;
}

public sealed record EcdCwrReconstructionComparisonBuildItem(
    string ScenarioId,
    string Method,
    string ResultHdf5Path,
    IReadOnlyList<string> Issues,
    EcdCwrReconstructionComparison? Comparison)
{
    public bool Passed => (Issues.Count == 0 && Comparison is not null) ||
        Issues.All(issue => issue.Contains("undefined for degenerate", StringComparison.OrdinalIgnoreCase));
}
