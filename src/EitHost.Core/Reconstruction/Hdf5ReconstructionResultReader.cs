using EitHost.Core.Storage.Hdf5;
using PureHDF;
using System.Text.Json;

namespace EitHost.Core.Reconstruction;

public sealed class Hdf5ReconstructionResultReader
{
    public RealtimeReconstructionResult Read(
        string outputHdf5Path,
        int blockNumber,
        TimeSpan backendElapsed,
        bool outputPersisted = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHdf5Path);
        var fullPath = Path.GetFullPath(outputHdf5Path);
        using var file = Hdf5FileAccess.OpenReadWithRetry(fullPath);
        var conductivity = ReadDoubleVector(file.Dataset("/conductivity"));
        var rawConductivity = TryReadDoubleVector(file, "/conductivity_raw");
        var nodeCoords = ReadDoubleMatrix(file.Dataset("/node_coords"));
        var cellConnectivity = ReadIntMatrix(file.Dataset("/cell_connectivity"));
        var meshIndexMetadata = ReadMeshIndexMetadata(file);
        meshIndexMetadata.ValidateForResult(
            nodeCoords,
            cellConnectivity,
            conductivity.Length,
            requireCanonical: false);
        var measured = TryReadDoubleVector(file, "/measured");
        var simulated = TryReadDoubleVector(file, "/simulated");
        var contactJacobian = TryReadContactJacobian(file);
        var fitSummary = VoltageFitSummary.From(measured, simulated, conductivity);
        var conditionNumber = TryReadDoubleScalar(
            file,
            "/weighted_system_condition_number",
            "/condition_number",
            "/condition_estimate");
        var error = conductivity.Length == 0
            ? "PyEIDORS returned an empty conductivity vector."
            : null;
        return new RealtimeReconstructionResult(
            blockNumber,
            fullPath,
            conductivity,
            nodeCoords,
            cellConnectivity,
            DateTimeOffset.Now,
            backendElapsed,
            error,
            outputPersisted,
            measured,
            simulated,
            conditionNumber,
            fitSummary?.ResidualNorm,
            fitSummary?.RelativeResidual,
            fitSummary?.CosineSimilarity,
            fitSummary?.ResidualL1Norm,
            fitSummary?.RelativeL1Residual,
            fitSummary?.ResidualLinfNorm,
            fitSummary?.MeasuredNorm,
            fitSummary?.SimulatedNorm,
            fitSummary?.R2,
            fitSummary?.ConductivityRange,
            rawConductivity,
            DynamicKalmanApplied: rawConductivity is not null,
            DynamicKalmanAction: ActionFromCode(TryReadIntScalar(file, "/dynamic_kalman_action_code")),
            DynamicKalmanNisPerDof: TryReadDoubleScalar(file, "/dynamic_kalman_nis_per_dof"),
            DynamicKalmanGainMean: TryReadDoubleScalar(file, "/dynamic_kalman_gain_mean"),
            DynamicKalmanVarianceInflation: TryReadDoubleScalar(file, "/dynamic_kalman_variance_inflation"),
            DynamicKalmanUpdateCount: TryReadIntScalar(file, "/dynamic_kalman_update_count"),
            DynamicKalmanTotalLatencyFrames: TryReadIntScalar(file, "/dynamic_kalman_total_latency_frames"),
            DynamicKalmanMode: ModeFromCode(TryReadIntScalar(file, "/dynamic_kalman_mode_code")),
            DynamicKalmanFallback: TryReadIntScalar(file, "/dynamic_kalman_fallback_code") is { } fallbackCode
                ? fallbackCode != 0
                : null,
            DynamicKalmanSolveMilliseconds: TryReadDoubleScalar(file, "/dynamic_kalman_solve_seconds") is { } solveSeconds
                ? solveSeconds * 1000.0
                : null,
            ContactJacobian: contactJacobian.Values,
            ContactJacobianMeasurementSpace: contactJacobian.MeasurementSpace,
            ContactJacobianStatus: contactJacobian.Status,
            ContactJacobianSource: contactJacobian.Source,
            MeshIndexMetadata: meshIndexMetadata);
    }

    private static ReconstructionMeshIndexMetadata ReadMeshIndexMetadata(IH5Group file)
    {
        if (!file.AttributeExists("metadata_json"))
        {
            return ReconstructionMeshIndexMetadata.LegacyCell;
        }

        string metadataJson;
        try
        {
            metadataJson = file.Attribute("metadata_json").Read<string>();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("PyEIDORS result metadata_json attribute is unreadable.", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            var metadata = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("metadata", out var nestedMetadata) &&
                nestedMetadata.ValueKind == JsonValueKind.Object
                    ? nestedMetadata
                    : root;
            var names = new[]
            {
                "mesh_index_schema",
                "parameter_entity",
                "logical_mesh_fingerprint",
                "ordered_index_fingerprint",
                "coordinate_decimals",
                "coordinate_quantization_step"
            };
            var present = names.Count(name => metadata.TryGetProperty(name, out _));
            if (present == 0)
            {
                return ReconstructionMeshIndexMetadata.LegacyCell;
            }

            if (present != names.Length)
            {
                throw new InvalidDataException(
                    "PyEIDORS canonical mesh-index metadata is incomplete; all V2 fields are required.");
            }

            return ReconstructionMeshIndexMetadata.FromPersisted(
                metadata.GetProperty("mesh_index_schema").GetString(),
                metadata.GetProperty("parameter_entity").GetString() ?? string.Empty,
                metadata.GetProperty("logical_mesh_fingerprint").GetString() ?? string.Empty,
                metadata.GetProperty("ordered_index_fingerprint").GetString() ?? string.Empty,
                metadata.GetProperty("coordinate_decimals").GetInt32(),
                metadata.GetProperty("coordinate_quantization_step").GetDouble());
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("PyEIDORS canonical mesh-index metadata is invalid.", ex);
        }
    }

    private static ContactJacobianReadResult TryReadContactJacobian(IH5Group file)
    {
        const string datasetPath = "/contact_jacobian_208x16";
        if (!file.LinkExists(datasetPath))
        {
            return ContactJacobianReadResult.Unavailable(
                $"unavailable: backend output missing {datasetPath}",
                datasetPath);
        }

        try
        {
            var dataset = file.Dataset(datasetPath);
            var dimensions = dataset.Space.Dimensions;
            if (!dimensions.SequenceEqual([208UL, 16UL]))
            {
                return ContactJacobianReadResult.Unavailable(
                    $"incompatible: {datasetPath} shape must be (208,16), got ({string.Join(',', dimensions)})",
                    datasetPath);
            }

            if (dataset.Type.Class == H5DataTypeClass.Compound)
            {
                if (TryRead(
                    () => dataset.Read<Hdf5Complex128[,]>(memoryDims: dimensions),
                    out var complex128Values))
                {
                    return ValidateContactJacobian(
                        StackComplexContactJacobian(complex128Values),
                        "complex-stacked416",
                        datasetPath);
                }

                if (TryRead(
                    () => dataset.Read<Hdf5Complex64[,]>(memoryDims: dimensions),
                    out var complex64Values))
                {
                    return ValidateContactJacobian(
                        StackComplexContactJacobian(complex64Values),
                        "complex-stacked416",
                        datasetPath);
                }

                return ContactJacobianReadResult.Unavailable(
                    $"incompatible: unsupported compound type at {datasetPath}",
                    datasetPath);
            }

            return ValidateContactJacobian(
                ReadDoubleMatrix(dataset),
                "amplitude208",
                datasetPath);
        }
        catch (Exception ex)
        {
            return ContactJacobianReadResult.Unavailable(
                $"incompatible: cannot read {datasetPath}: {ex.Message}",
                datasetPath);
        }
    }

    private static ContactJacobianReadResult ValidateContactJacobian(
        double[,] values,
        string measurementSpace,
        string source)
    {
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                if (!double.IsFinite(values[row, column]))
                {
                    return ContactJacobianReadResult.Unavailable(
                        $"incompatible: {source} contains non-finite value at [{row},{column}]",
                        source,
                        measurementSpace);
                }
            }
        }

        return new ContactJacobianReadResult(
            values,
            measurementSpace,
            "available: optional realtime contact Jacobian loaded",
            source);
    }

    private static double[,] StackComplexContactJacobian(Hdf5Complex128[,] values)
    {
        var stacked = new double[416, 16];
        for (var row = 0; row < 208; row++)
        {
            for (var column = 0; column < 16; column++)
            {
                stacked[row, column] = values[row, column].Real;
                stacked[row + 208, column] = values[row, column].Imaginary;
            }
        }

        return stacked;
    }

    private static double[,] StackComplexContactJacobian(Hdf5Complex64[,] values)
    {
        var stacked = new double[416, 16];
        for (var row = 0; row < 208; row++)
        {
            for (var column = 0; column < 16; column++)
            {
                stacked[row, column] = values[row, column].Real;
                stacked[row + 208, column] = values[row, column].Imaginary;
            }
        }

        return stacked;
    }

    private static double[]? TryReadDoubleVector(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            return null;
        }

        try
        {
            return ReadDoubleVector(file.Dataset(datasetPath));
        }
        catch
        {
            return null;
        }
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
                // Optional diagnostic scalar; ignore malformed values.
            }
        }

        return null;
    }

    private static int? TryReadIntScalar(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            return null;
        }

        try
        {
            var dataset = file.Dataset(datasetPath);
            if (TryRead(() => dataset.Read<int>(), out var intValue))
            {
                return intValue;
            }

            if (TryRead(() => dataset.Read<long>(), out var longValue))
            {
                return checked((int)longValue);
            }

            var vector = ReadDoubleVector(dataset);
            return vector.Length > 0 && double.IsFinite(vector[0])
                ? checked((int)Math.Round(vector[0]))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ActionFromCode(int? code)
    {
        return code switch
        {
            0 => "initialize",
            1 => "update",
            2 => "reject",
            3 => "inflate",
            4 => "static_guard_reset",
            _ => null
        };
    }

    private static string? ModeFromCode(int? code)
    {
        return code switch
        {
            0 => "fast_image",
            1 => "measurement",
            _ => null
        };
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

        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubleMatrix))
        {
            return Flatten(doubleMatrix);
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floatMatrix))
        {
            return Flatten(floatMatrix);
        }

        throw new InvalidDataException("Unsupported HDF5 conductivity dataset type.");
    }

    private static double[,] ReadDoubleMatrix(IH5Dataset dataset)
    {
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubleMatrix))
        {
            return doubleMatrix;
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floatMatrix))
        {
            return ConvertToDouble(floatMatrix);
        }

        throw new InvalidDataException("Unsupported HDF5 node coordinate dataset type.");
    }

    private static int[,] ReadIntMatrix(IH5Dataset dataset)
    {
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

        throw new InvalidDataException("Unsupported HDF5 cell connectivity dataset type.");
    }

    private static double[] Flatten(double[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new double[checked(rows * columns)];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[index++] = values[row, column];
            }
        }

        return output;
    }

    private static double[] Flatten(float[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new double[checked(rows * columns)];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[index++] = values[row, column];
            }
        }

        return output;
    }

    private static double[,] ConvertToDouble(float[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = values[row, column];
            }
        }

        return output;
    }

    private static int[,] ConvertToInt(long[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = checked((int)values[row, column]);
            }
        }

        return output;
    }

    private static int[,] ConvertToInt(uint[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = checked((int)values[row, column]);
            }
        }

        return output;
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

    private sealed record ContactJacobianReadResult(
        double[,]? Values,
        string MeasurementSpace,
        string Status,
        string Source)
    {
        public static ContactJacobianReadResult Unavailable(
            string status,
            string source,
            string measurementSpace = "")
        {
            return new ContactJacobianReadResult(null, measurementSpace, status, source);
        }
    }

#pragma warning disable CS0649
    private struct Hdf5Complex128
    {
        [H5Name("r")]
        public double Real;

        [H5Name("i")]
        public double Imaginary;
    }

    private struct Hdf5Complex64
    {
        [H5Name("r")]
        public float Real;

        [H5Name("i")]
        public float Imaginary;
    }
#pragma warning restore CS0649

    private sealed record VoltageFitSummary(
        double ResidualNorm,
        double RelativeResidual,
        double CosineSimilarity,
        double ResidualL1Norm,
        double RelativeL1Residual,
        double ResidualLinfNorm,
        double MeasuredNorm,
        double SimulatedNorm,
        double R2,
        double ConductivityRange)
    {
        public static VoltageFitSummary? From(
            IReadOnlyList<double>? measured,
            IReadOnlyList<double>? simulated,
            IReadOnlyList<double> conductivity)
        {
            if (measured is null ||
                simulated is null ||
                measured.Count == 0 ||
                measured.Count != simulated.Count)
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
            for (var index = 0; index < measured.Count; index++)
            {
                var measuredValue = measured[index];
                var simulatedValue = simulated[index];
                if (!double.IsFinite(measuredValue) || !double.IsFinite(simulatedValue))
                {
                    return null;
                }

                var delta = measuredValue - simulatedValue;
                residualSquare += delta * delta;
                residualL1 += Math.Abs(delta);
                residualLinf = Math.Max(residualLinf, Math.Abs(delta));
                measuredSquare += measuredValue * measuredValue;
                measuredL1 += Math.Abs(measuredValue);
                simulatedSquare += simulatedValue * simulatedValue;
                dot += measuredValue * simulatedValue;
                var centered = measuredValue - measuredMean;
                measuredCenteredSquare += centered * centered;
            }

            var residualNorm = Math.Sqrt(residualSquare);
            var measuredNorm = Math.Sqrt(measuredSquare);
            var simulatedNorm = Math.Sqrt(simulatedSquare);
            var relativeResidual = residualNorm / Math.Max(measuredNorm, 1.0e-12);
            var relativeL1 = residualL1 / Math.Max(measuredL1, 1.0e-12);
            var cosine = measuredNorm <= 0.0 || simulatedNorm <= 0.0
                ? 0.0
                : dot / (measuredNorm * simulatedNorm);
            var r2 = 1.0 - (residualSquare / Math.Max(measuredCenteredSquare, 1.0e-12));
            var conductivityRange = conductivity.Count == 0
                ? 0.0
                : conductivity.Max() - conductivity.Min();
            return new VoltageFitSummary(
                residualNorm,
                relativeResidual,
                Math.Clamp(cosine, -1.0, 1.0),
                residualL1,
                relativeL1,
                residualLinf,
                measuredNorm,
                simulatedNorm,
                r2,
                conductivityRange);
        }
    }
}
