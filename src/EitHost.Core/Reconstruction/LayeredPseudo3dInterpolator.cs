namespace EitHost.Core.Reconstruction;

public sealed record LayeredPseudo3dSource(
    string SetLabel,
    DateTimeOffset AcquiredAt,
    RealtimeReconstructionResult Result);

public sealed record LayeredPseudo3dVolume(
    string LowerSetLabel,
    string UpperSetLabel,
    DateTimeOffset LowerAcquiredAt,
    DateTimeOffset UpperAcquiredAt,
    TimeSpan PairSkew,
    double NormalizedHeight,
    double[] DisplayLayerZ,
    double[,] SourceNodeCoords2d,
    int[,] SourceTriangleConnectivity,
    double[,] NodeCoords3d,
    int[,] TetraConnectivity,
    double[] Conductivity,
    double[,] DisplayLayerTriangleConductivity,
    string ReconstructionScaleStatus,
    string ReconstructionScaleProvenance,
    string Algorithm)
{
    public const string AlgorithmId = "layered_2d_noser_rm_z_interpolated_tetra_v1";

    public int DisplayLayerCount => DisplayLayerZ.Length;
}

public static class LayeredPseudo3dInterpolator
{
    private const double CoordinateTolerance = 1.0e-9;

    public static LayeredPseudo3dVolume Interpolate(
        LayeredPseudo3dSource lower,
        LayeredPseudo3dSource upper,
        int displayLayers = 5,
        double normalizedHeight = 2.0)
    {
        ArgumentNullException.ThrowIfNull(lower);
        ArgumentNullException.ThrowIfNull(upper);
        ArgumentException.ThrowIfNullOrWhiteSpace(lower.SetLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(upper.SetLabel);
        if (string.Equals(lower.SetLabel, upper.SetLabel, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Pseudo-3D interpolation requires two distinct set labels.");
        }

        if (displayLayers < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(displayLayers), "Pseudo-3D display layers must be at least two.");
        }

        if (!double.IsFinite(normalizedHeight) || normalizedHeight <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(normalizedHeight), "Pseudo-3D normalized height must be finite and positive.");
        }

        ValidateResult(lower.Result, nameof(lower));
        ValidateResult(upper.Result, nameof(upper));
        ValidateCompatibleScale(lower.Result, upper.Result);
        ValidateCompatibleMeshes(lower.Result, upper.Result);

        var sourceNodes = CopyFirstTwoColumns(lower.Result.NodeCoords);
        var triangles = Triangulate(
            lower.Result.CellConnectivity,
            lower.Result.Conductivity,
            sourceNodes.GetLength(0),
            lower.Result.ParameterEntity,
            out var lowerValues,
            out var valuesAreNodal);
        _ = Triangulate(
            upper.Result.CellConnectivity,
            upper.Result.Conductivity,
            sourceNodes.GetLength(0),
            upper.Result.ParameterEntity,
            out var upperValues,
            out var upperValuesAreNodal);
        if (valuesAreNodal != upperValuesAreNodal || lowerValues.Length != upperValues.Length)
        {
            throw new InvalidDataException("Pseudo-3D layer conductivity representations do not match.");
        }

        var displayLayerZ = CreateDisplayLayerZ(displayLayers, normalizedHeight);
        var displayValues = InterpolateDisplayValues(lowerValues, upperValues, displayLayers);
        var nodeCoords3d = ExtrudeNodes(sourceNodes, displayLayerZ);
        var tetraConnectivity = ExtrudeTriangles(triangles, sourceNodes.GetLength(0), displayLayers);
        var conductivity = valuesAreNodal
            ? FlattenRows(displayValues)
            : CreateTetraConductivity(displayValues);
        var triangleDisplayValues = valuesAreNodal
            ? ProjectNodalValuesToTriangles(displayValues, triangles)
            : displayValues;

        return new LayeredPseudo3dVolume(
            lower.SetLabel,
            upper.SetLabel,
            lower.AcquiredAt,
            upper.AcquiredAt,
            (upper.AcquiredAt - lower.AcquiredAt).Duration(),
            normalizedHeight,
            displayLayerZ,
            sourceNodes,
            triangles,
            nodeCoords3d,
            tetraConnectivity,
            conductivity,
            triangleDisplayValues,
            lower.Result.ReconstructionScaleStatus,
            lower.Result.ReconstructionScaleProvenance,
            LayeredPseudo3dVolume.AlgorithmId);
    }

    private static void ValidateResult(RealtimeReconstructionResult result, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(result, parameterName);
        if (!result.Succeeded)
        {
            throw new InvalidDataException($"Pseudo-3D source {parameterName} is not a successful reconstruction.");
        }

        if (result.NodeCoords.GetLength(0) == 0 || result.NodeCoords.GetLength(1) < 2)
        {
            throw new InvalidDataException($"Pseudo-3D source {parameterName} has no 2D node coordinates.");
        }

        if (result.CellConnectivity.GetLength(0) == 0)
        {
            throw new InvalidDataException($"Pseudo-3D source {parameterName} has no cells.");
        }

        foreach (var value in result.NodeCoords)
        {
            if (!double.IsFinite(value))
            {
                throw new InvalidDataException($"Pseudo-3D source {parameterName} contains non-finite node coordinates.");
            }
        }

        if (result.Conductivity.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException($"Pseudo-3D source {parameterName} contains non-finite conductivity.");
        }
    }

    private static void ValidateCompatibleScale(
        RealtimeReconstructionResult lower,
        RealtimeReconstructionResult upper)
    {
        if (!string.Equals(
                lower.ReconstructionScaleStatus,
                upper.ReconstructionScaleStatus,
                StringComparison.Ordinal) ||
            !string.Equals(
                lower.ReconstructionScaleProvenance,
                upper.ReconstructionScaleProvenance,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Pseudo-3D source reconstruction scale status or provenance does not match.");
        }
    }

    private static void ValidateCompatibleMeshes(
        RealtimeReconstructionResult lower,
        RealtimeReconstructionResult upper)
    {
        if (lower.NodeCoords.GetLength(0) != upper.NodeCoords.GetLength(0) ||
            lower.NodeCoords.GetLength(1) != upper.NodeCoords.GetLength(1))
        {
            throw new InvalidDataException("Pseudo-3D source node-coordinate shapes do not match.");
        }

        if (lower.CellConnectivity.GetLength(0) != upper.CellConnectivity.GetLength(0) ||
            lower.CellConnectivity.GetLength(1) != upper.CellConnectivity.GetLength(1))
        {
            throw new InvalidDataException("Pseudo-3D source cell-connectivity shapes do not match.");
        }

        for (var row = 0; row < lower.NodeCoords.GetLength(0); row++)
        {
            for (var column = 0; column < lower.NodeCoords.GetLength(1); column++)
            {
                if (Math.Abs(lower.NodeCoords[row, column] - upper.NodeCoords[row, column]) > CoordinateTolerance)
                {
                    throw new InvalidDataException("Pseudo-3D source node coordinates do not match.");
                }
            }
        }

        for (var row = 0; row < lower.CellConnectivity.GetLength(0); row++)
        {
            for (var column = 0; column < lower.CellConnectivity.GetLength(1); column++)
            {
                if (lower.CellConnectivity[row, column] != upper.CellConnectivity[row, column])
                {
                    throw new InvalidDataException("Pseudo-3D source cell connectivity does not match.");
                }
            }
        }
    }

    private static double[,] CopyFirstTwoColumns(double[,] source)
    {
        var output = new double[source.GetLength(0), 2];
        for (var row = 0; row < source.GetLength(0); row++)
        {
            output[row, 0] = source[row, 0];
            output[row, 1] = source[row, 1];
        }

        return output;
    }

    private static int[,] Triangulate(
        int[,] cells,
        IReadOnlyList<double> conductivity,
        int nodeCount,
        string parameterEntity,
        out double[] values,
        out bool valuesAreNodal)
    {
        var cellCount = cells.GetLength(0);
        var verticesPerCell = cells.GetLength(1);
        foreach (var nodeIndex in cells)
        {
            if (nodeIndex < 0 || nodeIndex >= nodeCount)
            {
                throw new InvalidDataException("Pseudo-3D source cells contain out-of-range node indices.");
            }
        }

        valuesAreNodal = string.Equals(
            ReconstructionParameterEntity.Normalize(parameterEntity),
            ReconstructionParameterEntity.Node,
            StringComparison.Ordinal);
        var expectedValueCount = valuesAreNodal ? nodeCount : cellCount;
        if (conductivity.Count != expectedValueCount)
        {
            throw new InvalidDataException(
                $"Pseudo-3D conductivity length does not match parameter_entity={parameterEntity}: " +
                $"{conductivity.Count}/{expectedValueCount}.");
        }
        if (verticesPerCell == 3)
        {
            if (!valuesAreNodal && conductivity.Count != cellCount)
            {
                throw new InvalidDataException("Pseudo-3D triangular conductivity must be per-node or per-cell.");
            }

            values = conductivity.ToArray();
            return (int[,])cells.Clone();
        }

        if (verticesPerCell != 4)
        {
            throw new InvalidDataException(
                $"Pseudo-3D interpolation supports triangular or quadrilateral cells, got {verticesPerCell} vertices.");
        }

        if (!valuesAreNodal && conductivity.Count != cellCount)
        {
            throw new InvalidDataException("Pseudo-3D quadrilateral conductivity must be per-node or per-cell.");
        }

        var triangles = new int[checked(cellCount * 2), 3];
        for (var cell = 0; cell < cellCount; cell++)
        {
            triangles[cell * 2, 0] = cells[cell, 0];
            triangles[cell * 2, 1] = cells[cell, 1];
            triangles[cell * 2, 2] = cells[cell, 2];
            triangles[(cell * 2) + 1, 0] = cells[cell, 0];
            triangles[(cell * 2) + 1, 1] = cells[cell, 2];
            triangles[(cell * 2) + 1, 2] = cells[cell, 3];
        }

        values = valuesAreNodal
            ? conductivity.ToArray()
            : conductivity.SelectMany(value => new[] { value, value }).ToArray();
        return triangles;
    }

    private static double[] CreateDisplayLayerZ(int displayLayers, double height)
    {
        var output = new double[displayLayers];
        for (var layer = 0; layer < displayLayers; layer++)
        {
            output[layer] = (-0.5 * height) + (height * layer / (displayLayers - 1));
        }

        return output;
    }

    private static double[,] InterpolateDisplayValues(
        IReadOnlyList<double> lower,
        IReadOnlyList<double> upper,
        int displayLayers)
    {
        var output = new double[displayLayers, lower.Count];
        for (var layer = 0; layer < displayLayers; layer++)
        {
            var fraction = (double)layer / (displayLayers - 1);
            for (var valueIndex = 0; valueIndex < lower.Count; valueIndex++)
            {
                output[layer, valueIndex] = lower[valueIndex] +
                    ((upper[valueIndex] - lower[valueIndex]) * fraction);
            }
        }

        return output;
    }

    private static double[,] ExtrudeNodes(double[,] sourceNodes, IReadOnlyList<double> displayLayerZ)
    {
        var nodeCount = sourceNodes.GetLength(0);
        var output = new double[checked(nodeCount * displayLayerZ.Count), 3];
        for (var layer = 0; layer < displayLayerZ.Count; layer++)
        {
            var offset = layer * nodeCount;
            for (var node = 0; node < nodeCount; node++)
            {
                output[offset + node, 0] = sourceNodes[node, 0];
                output[offset + node, 1] = sourceNodes[node, 1];
                output[offset + node, 2] = displayLayerZ[layer];
            }
        }

        return output;
    }

    private static int[,] ExtrudeTriangles(int[,] triangles, int nodeCount, int displayLayers)
    {
        var triangleCount = triangles.GetLength(0);
        var output = new int[checked((displayLayers - 1) * triangleCount * 3), 4];
        var cursor = 0;
        for (var slab = 0; slab < displayLayers - 1; slab++)
        {
            var lowerOffset = slab * nodeCount;
            var upperOffset = (slab + 1) * nodeCount;
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var a0 = triangles[triangle, 0] + lowerOffset;
                var b0 = triangles[triangle, 1] + lowerOffset;
                var c0 = triangles[triangle, 2] + lowerOffset;
                var a1 = triangles[triangle, 0] + upperOffset;
                var b1 = triangles[triangle, 1] + upperOffset;
                var c1 = triangles[triangle, 2] + upperOffset;

                output[cursor, 0] = a0;
                output[cursor, 1] = b0;
                output[cursor, 2] = c0;
                output[cursor++, 3] = a1;
                output[cursor, 0] = b0;
                output[cursor, 1] = b1;
                output[cursor, 2] = c1;
                output[cursor++, 3] = a1;
                output[cursor, 0] = b0;
                output[cursor, 1] = c0;
                output[cursor, 2] = c1;
                output[cursor++, 3] = a1;
            }
        }

        return output;
    }

    private static double[] CreateTetraConductivity(double[,] displayValues)
    {
        var displayLayers = displayValues.GetLength(0);
        var triangleCount = displayValues.GetLength(1);
        var output = new double[checked((displayLayers - 1) * triangleCount * 3)];
        var cursor = 0;
        for (var slab = 0; slab < displayLayers - 1; slab++)
        {
            for (var triangle = 0; triangle < triangleCount; triangle++)
            {
                var value = 0.5 * (displayValues[slab, triangle] + displayValues[slab + 1, triangle]);
                output[cursor++] = value;
                output[cursor++] = value;
                output[cursor++] = value;
            }
        }

        return output;
    }

    private static double[,] ProjectNodalValuesToTriangles(double[,] nodalValues, int[,] triangles)
    {
        var output = new double[nodalValues.GetLength(0), triangles.GetLength(0)];
        for (var layer = 0; layer < nodalValues.GetLength(0); layer++)
        {
            for (var triangle = 0; triangle < triangles.GetLength(0); triangle++)
            {
                output[layer, triangle] = (
                    nodalValues[layer, triangles[triangle, 0]] +
                    nodalValues[layer, triangles[triangle, 1]] +
                    nodalValues[layer, triangles[triangle, 2]]) / 3.0;
            }
        }

        return output;
    }

    private static double[] FlattenRows(double[,] values)
    {
        var output = new double[checked(values.GetLength(0) * values.GetLength(1))];
        var cursor = 0;
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                output[cursor++] = values[row, column];
            }
        }

        return output;
    }
}
