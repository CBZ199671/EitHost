using System.Buffers.Binary;
using System.Security.Cryptography;
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

public sealed record ReconstructionMeshReference(string Fingerprint, string ArtifactPath);

public sealed record ReconstructionMeshSnapshot(
    string Fingerprint,
    string ArtifactPath,
    double[,] NodeCoords,
    int[,] CellConnectivity);

public sealed class GlobalReconstructionMeshStore(
    DataRootLayout layout,
    DerivedArtifactHdf5Writer writer)
{
    private readonly DataRootLayout layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly DerivedArtifactHdf5Writer writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly object validationGate = new();
    private readonly HashSet<string> validatedFingerprints = new(StringComparer.Ordinal);

    public ReconstructionMeshReference Ensure(
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

        return new ReconstructionMeshReference(fingerprint, artifactPath);
    }

    public ReconstructionMeshSnapshot Load(string artifactPath, string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        var path = layout.ResolveArtifactPath(artifactPath);
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var nodes = file.Dataset("/mesh/node_coords").Read<double[,]>();
        var cells = file.Dataset("/mesh/cell_connectivity").Read<int[,]>();
        var actual = ReconstructionMeshFingerprint.Compute(nodes, cells);
        if (!string.Equals(actual, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reconstruction mesh fingerprint mismatch: expected {expectedFingerprint}, actual {actual}.");
        }

        return new ReconstructionMeshSnapshot(actual, artifactPath, nodes, cells);
    }

    public ReconstructionMeshSnapshot Load(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        var path = layout.ResolveArtifactPath(artifactPath);
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var nodes = file.Dataset("/mesh/node_coords").Read<double[,]>();
        var cells = file.Dataset("/mesh/cell_connectivity").Read<int[,]>();
        var fingerprint = ReconstructionMeshFingerprint.Compute(nodes, cells);
        return new ReconstructionMeshSnapshot(fingerprint, artifactPath, nodes, cells);
    }
}
