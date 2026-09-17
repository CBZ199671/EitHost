using System.Text.Json;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Reconstruction;

public sealed class Hdf5Pseudo3dKrigingProtocol
{
    public const string RequestSchema = "pyeidors_pseudo3d_kriging_request_h5_v1";
    public const string ResultSchema = "pyeidors_pseudo3d_kriging_result_h5_v1";

    public void WriteRequest(string path, Pseudo3dKrigingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        CreateRequestFile(request).Write(fullPath);
    }

    public void WriteRequest(Stream stream, Pseudo3dKrigingRequest request)
    {
        ArgumentNullException.ThrowIfNull(stream);
        CreateRequestFile(request).Write(stream);
    }

    private static H5File CreateRequestFile(Pseudo3dKrigingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var configJson = JsonSerializer.Serialize(new
        {
            axial_range_factor = request.AxialRangeFactor,
            spatial_range_factor = request.SpatialRangeFactor,
            neighborhood_size = request.NeighborhoodSize
        });
        return new H5File
        {
            Attributes = new Dictionary<string, object>
            {
                ["schema"] = RequestSchema,
                ["parameter_entity"] = request.ParameterEntity,
                ["config_json"] = configJson,
                ["metadata_json"] = request.MetadataJson
            },
            ["node_coords"] = request.NodeCoords,
            ["cell_connectivity"] = request.CellConnectivity,
            ["layer_values"] = request.LayerValues,
            ["source_z"] = request.SourceZ,
            ["display_z"] = request.DisplayZ,
            ["layer_quality"] = request.LayerQuality
        };
    }

    public Pseudo3dKrigingResult ReadResult(
        string path,
        TimeSpan backendElapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var file = Hdf5FileAccess.OpenReadWithRetry(fullPath);
        return ReadResultCore(file, backendElapsed);
    }

    public Pseudo3dKrigingResult ReadResult(Stream stream, TimeSpan backendElapsed)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var file = H5File.Open(stream, leaveOpen: true);
        return ReadResultCore(file, backendElapsed);
    }

    private static Pseudo3dKrigingResult ReadResultCore(IH5Group file, TimeSpan backendElapsed)
    {
        var schema = ReadRequiredStringAttribute(file, "schema");
        if (!string.Equals(schema, ResultSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Pseudo-3D Kriging result schema mismatch: expected {ResultSchema}, got {schema}.");
        }

        var displayValues = ReadDoubleMatrix(file.Dataset("/display_layer_values"));
        var relativeVariance = ReadDoubleMatrix(file.Dataset("/relative_variance"));
        if (displayValues.GetLength(0) == 0 || displayValues.GetLength(1) == 0)
        {
            throw new InvalidDataException("Pseudo-3D Kriging result values are empty.");
        }

        if (displayValues.GetLength(0) != relativeVariance.GetLength(0)
            || displayValues.GetLength(1) != relativeVariance.GetLength(1))
        {
            throw new InvalidDataException(
                "Pseudo-3D Kriging relative-variance shape does not match its mean field.");
        }

        foreach (var value in displayValues)
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException("Pseudo-3D Kriging result contains non-finite means.");
            }
        }

        foreach (var variance in relativeVariance)
        {
            if (!double.IsFinite(variance) || variance < 0.0 || variance > 1.0)
            {
                throw new InvalidDataException(
                    "Pseudo-3D Kriging result contains invalid normalized relative variance.");
            }
        }

        var metadataJson = ReadRequiredStringAttribute(file, "metadata_json");
        using var metadataDocument = JsonDocument.Parse(metadataJson);
        return new Pseudo3dKrigingResult(
            displayValues,
            relativeVariance,
            metadataJson,
            backendElapsed);
    }

    private static void ValidateRequest(Pseudo3dKrigingRequest request)
    {
        if (request.NodeCoords.GetLength(0) == 0 || request.NodeCoords.GetLength(1) < 2)
        {
            throw new ArgumentException("Pseudo-3D Kriging requires non-empty 2D node coordinates.", nameof(request));
        }

        if (request.CellConnectivity.GetLength(0) == 0
            || request.CellConnectivity.GetLength(1) is not (3 or 4))
        {
            throw new ArgumentException("Pseudo-3D Kriging requires triangle or quad cells.", nameof(request));
        }

        if (request.LayerValues.GetLength(0) != 2
            || request.LayerValues.GetLength(1) == 0
            || request.SourceZ.Length != 2
            || request.DisplayZ.Length < 2
            || request.LayerQuality.Length != 2)
        {
            throw new ArgumentException(
                "Pseudo-3D Kriging requires exactly two source fields and at least two display layers.",
                nameof(request));
        }

        if (request.LayerValues.GetLength(1) != (
                string.Equals(request.ParameterEntity, ReconstructionParameterEntity.Node, StringComparison.Ordinal)
                    ? request.NodeCoords.GetLength(0)
                    : request.CellConnectivity.GetLength(0)))
        {
            throw new ArgumentException(
                "Pseudo-3D Kriging parameter count does not match its mesh entity.",
                nameof(request));
        }

        if (!double.IsFinite(request.AxialRangeFactor) || request.AxialRangeFactor <= 0.0
            || !double.IsFinite(request.SpatialRangeFactor) || request.SpatialRangeFactor <= 0.0
            || request.NeighborhoodSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Pseudo-3D Kriging ranges and neighborhood are invalid.");
        }

        if (!request.SourceZ.All(double.IsFinite)
            || !request.DisplayZ.All(double.IsFinite)
            || !request.LayerQuality.All(value => double.IsFinite(value) && value >= 0.0 && value <= 1.0))
        {
            throw new ArgumentException("Pseudo-3D Kriging z/quality values are invalid.", nameof(request));
        }

        try
        {
            using var metadata = JsonDocument.Parse(request.MetadataJson);
            if (metadata.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Pseudo-3D Kriging metadata must be a JSON object.", nameof(request));
            }
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Pseudo-3D Kriging metadata is not valid JSON.", nameof(request), ex);
        }
    }

    private static string ReadRequiredStringAttribute(IH5Group file, string name)
    {
        if (!file.AttributeExists(name))
        {
            throw new InvalidDataException($"Pseudo-3D Kriging result omitted attribute {name}.");
        }

        return file.Attribute(name).Read<string>();
    }

    private static double[,] ReadDoubleMatrix(IH5Dataset dataset)
    {
        var dimensions = dataset.Space.Dimensions;
        try
        {
            return dataset.Read<double[,]>(memoryDims: dimensions);
        }
        catch (Exception first) when (first is InvalidCastException or NotSupportedException)
        {
            try
            {
                var source = dataset.Read<float[,]>(memoryDims: dimensions);
                var output = new double[source.GetLength(0), source.GetLength(1)];
                for (var row = 0; row < source.GetLength(0); row++)
                {
                    for (var column = 0; column < source.GetLength(1); column++)
                    {
                        output[row, column] = source[row, column];
                    }
                }

                return output;
            }
            catch (Exception second)
            {
                throw new InvalidDataException(
                    "Unsupported pseudo-3D Kriging HDF5 matrix type.",
                    new AggregateException(first, second));
            }
        }
    }
}
