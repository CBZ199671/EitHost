using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EitHost.Core.Reconstruction;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public sealed record Pseudo3dArchiveSource(Guid RunId, string SetLabel, int BlockNumber,
    long StartSampleIndex, long EndSampleIndex, DateTimeOffset AcquiredAt,
    int ReferenceEpoch, int DynamicGeneration, string ReconstructionArtifactPath, string ReconstructionDatasetPath)
{
    public Acquisition.Pseudo3dAcquisitionStamp? TimeDivision { get; init; }
}

public sealed record Pseudo3dArchiveMetadata(Pseudo3dArchiveSource Lower, Pseudo3dArchiveSource Upper,
    long ConfigurationGeneration, string Algorithm, string AlgorithmProvenance,
    string ScaleStatus, string ScaleProvenance, double NormalizedHeight, int Layers,
    string ColorScale, string Warning, double? ColorScaleCenter = null, double? ColorScaleRange = null);

public sealed record Pseudo3dArchivedFrame(Pseudo3dArchiveMetadata Metadata, double[] DisplayZ,
    double[,] Values, double[,] RelativeVariance)
{
    public double[,] NodeCoords { get; init; } = new double[0, 2];
    public int[,] Triangles { get; init; } = new int[0, 3];

    public LayeredPseudo3dVolume ToVolume() => new(Metadata.Lower.SetLabel, Metadata.Upper.SetLabel,
        Metadata.Lower.AcquiredAt, Metadata.Upper.AcquiredAt,
        (Metadata.Upper.AcquiredAt - Metadata.Lower.AcquiredAt).Duration(), Metadata.NormalizedHeight,
        DisplayZ, NodeCoords, Triangles, new double[0, 3], new int[0, 4], [], Values,
        Metadata.ScaleStatus, Metadata.ScaleProvenance, Metadata.Algorithm, RelativeVariance, Metadata.AlgorithmProvenance);
}

// Shards hold 3000 lower-source blocks. Each pair has an immutable identity and a
// completion marker. Arrays remain in HDF5; the catalog contains references only.
public sealed class Pseudo3dArchiveStore
{
    public const string Schema = "eithost_pseudo3d_pair_v1";
    private static readonly object[] Gates = Enumerable.Range(0, 32).Select(_ => new object()).ToArray();

    public string Write(string path, Pseudo3dArchiveMetadata metadata, LayeredPseudo3dVolume volume)
    {
        if (volume.Algorithm != LayeredPseudo3dVolume.KrigingAlgorithmId ||
            volume.DisplayLayerTriangleRelativeVariance is null)
            throw new InvalidDataException("A pseudo-3D archive requires a validated backend Kriging result.");
        ValidateFrame(metadata, volume.DisplayLayerZ, volume.DisplayLayerTriangleConductivity,
            volume.DisplayLayerTriangleRelativeVariance);
        if (metadata.NormalizedHeight != volume.NormalizedHeight ||
            metadata.ScaleStatus != volume.ReconstructionScaleStatus ||
            metadata.ScaleProvenance != volume.ReconstructionScaleProvenance ||
            metadata.Lower.SetLabel != volume.LowerSetLabel || metadata.Upper.SetLabel != volume.UpperSetLabel ||
            metadata.Lower.AcquiredAt != volume.LowerAcquiredAt || metadata.Upper.AcquiredAt != volume.UpperAcquiredAt)
            throw new InvalidDataException("Pseudo-3D metadata does not describe the supplied result.");
        ReconstructionMeshFingerprint.Validate(volume.SourceNodeCoords2d, volume.SourceTriangleConnectivity,
            volume.DisplayLayerTriangleConductivity.GetLength(1));
        var meshFingerprint = ReconstructionMeshFingerprint.Compute(volume.SourceNodeCoords2d, volume.SourceTriangleConnectivity);
        var json = JsonSerializer.Serialize(metadata);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var root = "/pairs/" + id;
        var content = new H5File
        {
            ["pseudo3d"] = new H5Group
            {
                ["schema"] = Schema,
                ["metadata_json"] = json,
                ["display_z"] = volume.DisplayLayerZ,
                ["layer_triangle_values"] = volume.DisplayLayerTriangleConductivity,
                ["relative_variance"] = volume.DisplayLayerTriangleRelativeVariance
            },
            ["metadata"] = new H5Group
            {
                ["run"] = new H5Group { ["artifact_format"] = Schema },
                ["stages"] = new H5Group { ["pseudo3d"] = new H5Group { ["schema"] = Schema } }
            }
        };
        path = Path.GetFullPath(path);
        lock (Gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % Gates.Length])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                using (var file = Hdf5FileAccess.OpenReadWithRetry(path))
                {
                    if (!file.LinkExists("/mesh/fingerprint") || file.Dataset("/mesh/fingerprint").Read<string>() != meshFingerprint)
                        throw new InvalidDataException("Pseudo-3D archive mesh changed within a shard.");
                    if (file.LinkExists(root + "/metadata/stages/pseudo3d/complete") &&
                        file.Dataset(root + "/metadata/stages/pseudo3d/complete").Read<int>() == 1)
                        return root + "/pseudo3d";
                }
                Hdf5IncrementalStageAppender.Append(path, content, "pseudo3d", root);
            }
            else
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                try
                {
                    // One mesh per shard, outside per-pair values. It is already validated
                    // against both independent 2D results by CreateKrigingRequest.
                    var initial = new H5File
                    {
                        ["mesh"] = new H5Group
                        {
                            ["node_coords"] = volume.SourceNodeCoords2d,
                            ["triangle_connectivity"] = volume.SourceTriangleConnectivity,
                            ["fingerprint"] = meshFingerprint
                        }
                    };
                    initial.Write(temporary);
                    Hdf5IncrementalStageAppender.Append(temporary, content, "pseudo3d", root);
                    AtomicFileCommitter.MoveWithRetry(temporary, path, overwrite: false);
                }
                finally { AtomicFileCommitter.DeleteBestEffort(temporary); }
            }
        }
        return root + "/pseudo3d";
    }

    public Pseudo3dArchivedFrame Read(string path, string datasetPath)
    {
        if (datasetPath.Length != 80 || !datasetPath.StartsWith("/pairs/", StringComparison.Ordinal) ||
            !datasetPath.EndsWith("/pseudo3d", StringComparison.Ordinal) || datasetPath.AsSpan(7, 64).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Invalid pseudo-3D pair locator.", nameof(datasetPath));
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var root = datasetPath[..^"/pseudo3d".Length];
        if (file.Dataset(root + "/metadata/stages/pseudo3d/complete").Read<int>() != 1 ||
            file.Dataset(datasetPath + "/schema").Read<string>() != Schema)
            throw new InvalidDataException("Pseudo-3D pair was not completely archived.");
        var json = file.Dataset(datasetPath + "/metadata_json").Read<string>();
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) != datasetPath.Substring(7, 64))
            throw new InvalidDataException("Pseudo-3D source identity does not match the pair locator.");
        var metadata = JsonSerializer.Deserialize<Pseudo3dArchiveMetadata>(json)
            ?? throw new InvalidDataException("Missing pseudo-3D metadata.");
        var frame = new Pseudo3dArchivedFrame(metadata, file.Dataset(datasetPath + "/display_z").Read<double[]>(),
            file.Dataset(datasetPath + "/layer_triangle_values").Read<double[,]>(),
            file.Dataset(datasetPath + "/relative_variance").Read<double[,]>());
        ValidateFrame(metadata, frame.DisplayZ, frame.Values, frame.RelativeVariance);
        var nodes = file.Dataset("/mesh/node_coords").Read<double[,]>();
        var triangles = file.Dataset("/mesh/triangle_connectivity").Read<int[,]>();
        ReconstructionMeshFingerprint.Validate(nodes, triangles, frame.Values.GetLength(1));
        if (ReconstructionMeshFingerprint.Compute(nodes, triangles) != file.Dataset("/mesh/fingerprint").Read<string>())
            throw new InvalidDataException("Pseudo-3D archive mesh fingerprint does not match.");
        return frame with { NodeCoords = nodes, Triangles = triangles };
    }

    private static void ValidateFrame(Pseudo3dArchiveMetadata metadata, double[] displayZ, double[,] values, double[,] variance)
    {
        if (metadata.Algorithm != LayeredPseudo3dVolume.KrigingAlgorithmId || metadata.Layers < 2 ||
            !double.IsFinite(metadata.NormalizedHeight) || metadata.NormalizedHeight <= 0 ||
            displayZ.Length != metadata.Layers || values.GetLength(0) != metadata.Layers || values.GetLength(1) == 0 ||
            variance.GetLength(0) != values.GetLength(0) || variance.GetLength(1) != values.GetLength(1) ||
            metadata.Lower is null || metadata.Upper is null || metadata.Lower.RunId == Guid.Empty || metadata.Upper.RunId == Guid.Empty)
            throw new InvalidDataException("Invalid pseudo-3D frame dimensions or source metadata.");
        if (metadata.Lower.TimeDivision is not null || metadata.Upper.TimeDivision is not null)
        {
            Pseudo3dTimeDivisionContract.ValidateAcquisitionPair(metadata.Lower.TimeDivision, metadata.Lower.AcquiredAt,
                metadata.Upper.TimeDivision, metadata.Upper.AcquiredAt);
            if (metadata.Lower.BlockNumber != metadata.Lower.TimeDivision!.Round || metadata.Upper.BlockNumber != metadata.Upper.TimeDivision!.Round)
                throw new InvalidDataException("Pseudo-3D archive round does not match its source blocks.");
        }
        for (var i = 0; i < displayZ.Length; i++)
            if (!double.IsFinite(displayZ[i]) || (i > 0 && displayZ[i] <= displayZ[i - 1]))
                throw new InvalidDataException("Pseudo-3D display heights must be finite and increasing.");
        if (Math.Abs(displayZ[^1] - displayZ[0] - metadata.NormalizedHeight) > 1e-9 * metadata.NormalizedHeight)
            throw new InvalidDataException("Pseudo-3D display heights do not match the configured height.");
        foreach (double value in values)
            if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite pseudo-3D value.");
        foreach (double value in variance)
            if (!double.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid pseudo-3D relative variance.");
    }
}
