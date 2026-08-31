namespace EitHost.Core.Reconstruction;

public static class ReconstructionParameterEntity
{
    public const string Node = "node";
    public const string Cell = "cell";

    public static string Normalize(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Node => Node,
            Cell => Cell,
            _ => throw new InvalidDataException(
                "Reconstruction parameter_entity must be explicitly 'node' or 'cell'.")
        };
    }
}

public sealed record ReconstructionMeshIndexMetadata(
    string MeshIndexSchema,
    string ParameterEntity,
    string? LogicalMeshFingerprint,
    string? OrderedIndexFingerprint,
    int? CoordinateDecimals,
    double? CoordinateQuantizationStep,
    bool UsesLegacyContract)
{
    public const string CanonicalSchema = "canonical-mesh-index-v2";
    public const string LegacySchema = "legacy-eit-app-reconstruction-result-h5-v1";

    public static ReconstructionMeshIndexMetadata LegacyCell { get; } = new(
        LegacySchema,
        ReconstructionParameterEntity.Cell,
        null,
        null,
        null,
        null,
        UsesLegacyContract: true);

    public static ReconstructionMeshIndexMetadata Canonical(
        string parameterEntity,
        string logicalMeshFingerprint,
        string orderedIndexFingerprint,
        int coordinateDecimals,
        double coordinateQuantizationStep)
    {
        var metadata = new ReconstructionMeshIndexMetadata(
            CanonicalSchema,
            ReconstructionParameterEntity.Normalize(parameterEntity),
            logicalMeshFingerprint,
            orderedIndexFingerprint,
            coordinateDecimals,
            coordinateQuantizationStep,
            UsesLegacyContract: false);
        metadata.ValidateContract();
        return metadata;
    }

    public static ReconstructionMeshIndexMetadata FromPersisted(
        string? meshIndexSchema,
        string? parameterEntity,
        string? logicalMeshFingerprint,
        string? orderedIndexFingerprint,
        int? coordinateDecimals,
        double? coordinateQuantizationStep)
    {
        var hasAny = !string.IsNullOrWhiteSpace(meshIndexSchema) ||
            !string.IsNullOrWhiteSpace(parameterEntity) ||
            !string.IsNullOrWhiteSpace(logicalMeshFingerprint) ||
            !string.IsNullOrWhiteSpace(orderedIndexFingerprint) ||
            coordinateDecimals.HasValue ||
            coordinateQuantizationStep.HasValue;
        if (!hasAny)
        {
            return LegacyCell;
        }

        if (!string.Equals(meshIndexSchema, CanonicalSchema, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parameterEntity) ||
            string.IsNullOrWhiteSpace(logicalMeshFingerprint) ||
            string.IsNullOrWhiteSpace(orderedIndexFingerprint) ||
            coordinateDecimals is null ||
            coordinateQuantizationStep is null)
        {
            throw new InvalidDataException("Canonical mesh-index metadata is incomplete.");
        }

        return Canonical(
            parameterEntity,
            logicalMeshFingerprint,
            orderedIndexFingerprint,
            coordinateDecimals.Value,
            coordinateQuantizationStep.Value);
    }

    public int ExpectedParameterCount(double[,] nodeCoords, int[,] cellConnectivity)
    {
        ArgumentNullException.ThrowIfNull(nodeCoords);
        ArgumentNullException.ThrowIfNull(cellConnectivity);
        return ReconstructionParameterEntity.Normalize(ParameterEntity) == ReconstructionParameterEntity.Node
            ? nodeCoords.GetLength(0)
            : cellConnectivity.GetLength(0);
    }

    public void ValidateForResult(
        double[,] nodeCoords,
        int[,] cellConnectivity,
        int conductivityCount,
        bool requireCanonical)
    {
        ValidateContract();
        if (requireCanonical && UsesLegacyContract)
        {
            throw new InvalidDataException(
                "PyEIDORS result is missing the canonical mesh-index V2 contract.");
        }

        // Historical V1 artifacts did not declare the parameter entity and some early
        // fixtures did not persist a complete mesh. They remain replay-only evidence;
        // only the explicit V2 contract is allowed to establish the global GUI mesh.
        if (UsesLegacyContract)
        {
            return;
        }

        var expected = ExpectedParameterCount(nodeCoords, cellConnectivity);
        if (conductivityCount != expected)
        {
            throw new InvalidDataException(
                $"Reconstruction conductivity length does not match declared parameter_entity={ParameterEntity}: " +
                $"{conductivityCount}/{expected}.");
        }
    }

    public void ValidateContract()
    {
        _ = ReconstructionParameterEntity.Normalize(ParameterEntity);
        if (UsesLegacyContract)
        {
            if (!string.Equals(MeshIndexSchema, LegacySchema, StringComparison.Ordinal) ||
                !string.Equals(ParameterEntity, ReconstructionParameterEntity.Cell, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Legacy reconstruction mesh contract must declare cell parameters.");
            }

            return;
        }

        if (!string.Equals(MeshIndexSchema, CanonicalSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported canonical mesh_index_schema '{MeshIndexSchema}'.");
        }

        ValidateSha256Hex(LogicalMeshFingerprint, nameof(LogicalMeshFingerprint));
        ValidateSha256Hex(OrderedIndexFingerprint, nameof(OrderedIndexFingerprint));
        if (CoordinateDecimals is < 0 or > 15)
        {
            throw new InvalidDataException("Canonical coordinate_decimals must be between 0 and 15.");
        }

        if (CoordinateQuantizationStep is not { } step || !double.IsFinite(step) || step <= 0.0)
        {
            throw new InvalidDataException("Canonical coordinate_quantization_step must be finite and positive.");
        }
    }

    private static void ValidateSha256Hex(string? value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Canonical {name} must be a 64-character SHA-256 hex digest.");
        }
    }
}
