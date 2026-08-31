using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public static class ReconstructionMeshFingerprint
{
    public static string Compute(double[,] nodeCoords, int[,] cellConnectivity)
    {
        ArgumentNullException.ThrowIfNull(nodeCoords);
        ArgumentNullException.ThrowIfNull(cellConnectivity);
        Validate(nodeCoords, cellConnectivity);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> integerBuffer = stackalloc byte[sizeof(int)];
        Span<byte> doubleBuffer = stackalloc byte[sizeof(long)];
        AppendInt(hash, integerBuffer, nodeCoords.GetLength(0));
        AppendInt(hash, integerBuffer, nodeCoords.GetLength(1));
        for (var row = 0; row < nodeCoords.GetLength(0); row++)
        {
            for (var column = 0; column < nodeCoords.GetLength(1); column++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(
                    doubleBuffer,
                    BitConverter.DoubleToInt64Bits(nodeCoords[row, column]));
                hash.AppendData(doubleBuffer);
            }
        }

        AppendInt(hash, integerBuffer, cellConnectivity.GetLength(0));
        AppendInt(hash, integerBuffer, cellConnectivity.GetLength(1));
        for (var row = 0; row < cellConnectivity.GetLength(0); row++)
        {
            for (var column = 0; column < cellConnectivity.GetLength(1); column++)
            {
                AppendInt(hash, integerBuffer, cellConnectivity[row, column]);
            }
        }

        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static void Validate(double[,] nodeCoords, int[,] cellConnectivity, int? expectedCellCount = null)
    {
        if (nodeCoords.GetLength(0) == 0 || nodeCoords.GetLength(1) < 2)
        {
            throw new InvalidDataException("Reconstruction mesh requires non-empty 2D/3D node coordinates.");
        }

        if (cellConnectivity.GetLength(0) == 0 || cellConnectivity.GetLength(1) < 3)
        {
            throw new InvalidDataException("Reconstruction mesh requires non-empty cell connectivity.");
        }

        if (expectedCellCount is { } count && cellConnectivity.GetLength(0) != count)
        {
            throw new InvalidDataException(
                $"Reconstruction conductivity/cell count mismatch: {count}/{cellConnectivity.GetLength(0)}.");
        }

        for (var row = 0; row < nodeCoords.GetLength(0); row++)
        {
            for (var column = 0; column < nodeCoords.GetLength(1); column++)
            {
                if (!double.IsFinite(nodeCoords[row, column]))
                {
                    throw new InvalidDataException("Reconstruction mesh contains a non-finite node coordinate.");
                }
            }
        }

        for (var row = 0; row < cellConnectivity.GetLength(0); row++)
        {
            for (var column = 0; column < cellConnectivity.GetLength(1); column++)
            {
                var nodeIndex = cellConnectivity[row, column];
                if (nodeIndex < 0 || nodeIndex >= nodeCoords.GetLength(0))
                {
                    throw new InvalidDataException("Reconstruction mesh contains an invalid node index.");
                }
            }
        }
    }

    private static void AppendInt(IncrementalHash hash, Span<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }
}

public sealed record ReconstructionMeshReference(
    string Fingerprint,
    string ArtifactPath,
    ReconstructionMeshIndexMetadata MeshIndexMetadata);

public sealed record ReconstructionMeshSnapshot(
    string Fingerprint,
    string ArtifactPath,
    double[,] NodeCoords,
    int[,] CellConnectivity,
    ReconstructionMeshIndexMetadata MeshIndexMetadata);

public sealed class GlobalReconstructionMeshStore(
    DataRootLayout layout,
    DerivedArtifactHdf5Writer writer)
{
    private static readonly object BindingGate = new();
    private readonly DataRootLayout layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly DerivedArtifactHdf5Writer writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly object validationGate = new();
    private readonly HashSet<string> validatedFingerprints = new(StringComparer.Ordinal);

    internal ReconstructionMeshReference Ensure(
        Guid creatorRunId,
        DateTimeOffset createdAt,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        int expectedCellCount)
    {
        ReconstructionMeshFingerprint.Validate(nodeCoords, cellConnectivity, expectedCellCount);
        var fingerprint = ReconstructionMeshFingerprint.Compute(nodeCoords, cellConnectivity);
        var path = layout.GetGlobalReconstructionMeshPath(fingerprint);
        writer.WriteMesh(
            path,
            new DerivedMeshData(
                creatorRunId,
                createdAt,
                nodeCoords,
                cellConnectivity,
                fingerprint));
        var artifactPath = layout.ToRelativeArtifactPath(path);
        lock (validationGate)
        {
            if (!validatedFingerprints.Contains(fingerprint))
            {
                _ = Load(artifactPath, fingerprint);
                validatedFingerprints.Add(fingerprint);
            }
        }

        return new ReconstructionMeshReference(
            fingerprint,
            artifactPath,
            ReconstructionMeshIndexMetadata.LegacyCell);
    }

    public ReconstructionMeshReference Ensure(
        Guid creatorRunId,
        DateTimeOffset createdAt,
        double[,] nodeCoords,
        int[,] cellConnectivity,
        int conductivityCount,
        ReconstructionMeshIndexMetadata meshIndexMetadata)
    {
        ArgumentNullException.ThrowIfNull(meshIndexMetadata);
        ReconstructionMeshFingerprint.Validate(nodeCoords, cellConnectivity);
        meshIndexMetadata.ValidateForResult(
            nodeCoords,
            cellConnectivity,
            conductivityCount,
            requireCanonical: true);
        var fingerprint = ReconstructionMeshFingerprint.Compute(nodeCoords, cellConnectivity);
        var path = layout.GetGlobalReconstructionMeshPath(fingerprint);
        var artifactPath = layout.ToRelativeArtifactPath(path);
        var candidate = CanonicalMeshBinding.From(
            fingerprint,
            artifactPath,
            nodeCoords,
            cellConnectivity,
            meshIndexMetadata);

        lock (BindingGate)
        {
            var existing = ReadBinding();
            if (existing is not null)
            {
                EnsureBindingMatches(existing, candidate);
            }

            writer.WriteMesh(
                path,
                new DerivedMeshData(
                    creatorRunId,
                    createdAt,
                    nodeCoords,
                    cellConnectivity,
                    fingerprint,
                    meshIndexMetadata.MeshIndexSchema,
                    meshIndexMetadata.ParameterEntity,
                    meshIndexMetadata.LogicalMeshFingerprint,
                    meshIndexMetadata.OrderedIndexFingerprint));
            _ = LoadArtifact(artifactPath, fingerprint, meshIndexMetadata);

            if (existing is null)
            {
                WriteBinding(candidate);
            }
        }

        return new ReconstructionMeshReference(fingerprint, artifactPath, meshIndexMetadata);
    }

    public ReconstructionMeshSnapshot Load(string artifactPath, string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        var binding = ReadBinding();
        var metadata = binding?.ToMetadata() ?? ReconstructionMeshIndexMetadata.LegacyCell;
        var snapshot = LoadArtifact(artifactPath, expectedFingerprint, metadata);
        if (binding is not null)
        {
            EnsureBindingMatches(
                binding,
                CanonicalMeshBinding.From(
                    snapshot.Fingerprint,
                    artifactPath,
                    snapshot.NodeCoords,
                    snapshot.CellConnectivity,
                    snapshot.MeshIndexMetadata));
        }

        return snapshot;
    }

    public ReconstructionMeshSnapshot Load(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        var binding = ReadBinding();
        if (binding is not null &&
            !string.Equals(binding.ArtifactPath, artifactPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Requested reconstruction mesh is not the DataRoot fixed canonical inverse mesh: " +
                $"{artifactPath} / {binding.ArtifactPath}.");
        }

        var metadata = binding?.ToMetadata() ?? ReconstructionMeshIndexMetadata.LegacyCell;
        var expected = binding?.Fingerprint;
        return LoadArtifact(artifactPath, expected, metadata);
    }

    private ReconstructionMeshSnapshot LoadArtifact(
        string artifactPath,
        string? expectedFingerprint,
        ReconstructionMeshIndexMetadata metadata)
    {
        var path = layout.ResolveArtifactPath(artifactPath);
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var nodes = file.Dataset("/mesh/node_coords").Read<double[,]>();
        var cells = file.Dataset("/mesh/cell_connectivity").Read<int[,]>();
        var actual = ReconstructionMeshFingerprint.Compute(nodes, cells);
        if (expectedFingerprint is not null &&
            !string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reconstruction mesh fingerprint mismatch: expected {expectedFingerprint}, actual {actual}.");
        }

        return new ReconstructionMeshSnapshot(actual, artifactPath, nodes, cells, metadata);
    }

    private CanonicalMeshBinding? ReadBinding()
    {
        var path = layout.GlobalReconstructionMeshBindingPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var binding = JsonSerializer.Deserialize<CanonicalMeshBinding>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Canonical inverse mesh binding is empty.");
            binding.ToMetadata().ValidateContract();
            _ = layout.ResolveArtifactPath(binding.ArtifactPath);
            return binding;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (!AtomicFileCommitter.IsFatal(ex))
        {
            throw new InvalidDataException("Canonical inverse mesh binding is unreadable.", ex);
        }
    }

    private void WriteBinding(CanonicalMeshBinding binding)
    {
        var path = layout.GlobalReconstructionMeshBindingPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(binding));
            AtomicFileCommitter.MoveWithRetry(temporaryPath, path, overwrite: false);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    private static void EnsureBindingMatches(
        CanonicalMeshBinding expected,
        CanonicalMeshBinding actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException(
                "Reconstruction result does not match the DataRoot fixed canonical inverse mesh: " +
                $"exact={actual.Fingerprint}/{expected.Fingerprint}, " +
                $"logical={actual.LogicalMeshFingerprint}/{expected.LogicalMeshFingerprint}, " +
                $"ordered={actual.OrderedIndexFingerprint}/{expected.OrderedIndexFingerprint}, " +
                $"entity={actual.ParameterEntity}/{expected.ParameterEntity}, " +
                $"nodes={actual.NodeCount}/{expected.NodeCount}, cells={actual.CellCount}/{expected.CellCount}.");
        }
    }

    private sealed record CanonicalMeshBinding(
        string BindingSchema,
        string Fingerprint,
        string ArtifactPath,
        string MeshIndexSchema,
        string ParameterEntity,
        string LogicalMeshFingerprint,
        string OrderedIndexFingerprint,
        int CoordinateDecimals,
        double CoordinateQuantizationStep,
        int NodeCount,
        int NodeCoordinateCount,
        int CellCount,
        int CellVertexCount)
    {
        private const string Schema = "eithost-canonical-inverse-mesh-binding-v1";

        public static CanonicalMeshBinding From(
            string fingerprint,
            string artifactPath,
            double[,] nodes,
            int[,] cells,
            ReconstructionMeshIndexMetadata metadata)
        {
            metadata.ValidateContract();
            return new CanonicalMeshBinding(
                Schema,
                fingerprint,
                artifactPath,
                metadata.MeshIndexSchema,
                metadata.ParameterEntity,
                metadata.LogicalMeshFingerprint ?? string.Empty,
                metadata.OrderedIndexFingerprint ?? string.Empty,
                metadata.CoordinateDecimals ?? 0,
                metadata.CoordinateQuantizationStep ?? 0.0,
                nodes.GetLength(0),
                nodes.GetLength(1),
                cells.GetLength(0),
                cells.GetLength(1));
        }

        public ReconstructionMeshIndexMetadata ToMetadata()
        {
            if (!string.Equals(BindingSchema, Schema, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported canonical inverse mesh binding '{BindingSchema}'.");
            }

            return ReconstructionMeshIndexMetadata.Canonical(
                ParameterEntity,
                LogicalMeshFingerprint,
                OrderedIndexFingerprint,
                CoordinateDecimals,
                CoordinateQuantizationStep);
        }
    }
}
