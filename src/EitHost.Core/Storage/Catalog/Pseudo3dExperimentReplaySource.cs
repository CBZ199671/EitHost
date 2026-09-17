using System.Text.Json;
using EitHost.Core.Acquisition;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Hdf5;
using PureHDF;
using PureHDF.VOL.Native;

namespace EitHost.Core.Storage.Catalog;

public sealed record Pseudo3dReplayRound(int Round, ProcessingBlockCatalogRecord? Lower,
    ProcessingBlockCatalogRecord? Upper, DerivedArtifactCatalogRecord? Archive);

public sealed record Pseudo3dReplayLayer(Pseudo3dExperimentMember Member, Pseudo3dAcquisitionStamp? Stamp,
    double[] Voltage, RealtimeReconstructionResult? Reconstruction, int? ReferenceEpoch, string Status);

public sealed record Pseudo3dReplayFrame(int Round, Pseudo3dReplayLayer? Lower, Pseudo3dReplayLayer? Upper,
    Pseudo3dArchivedFrame? Volume, string Status);

/// <summary>Acquisition-round replay, independent of UI sampling and dynamic-Kalman disposition.</summary>
public sealed class Pseudo3dExperimentReplaySource
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly Dictionary<(Guid, int, string), DerivedArtifactCatalogRecord> artifacts = [];
    private readonly GlobalReconstructionMeshStore meshes;
    private readonly Dictionary<string, ReconstructionMeshSnapshot> meshCache = [];

    public Pseudo3dExperimentReplaySource(DataRootLayout layout, ExperimentCatalog catalog, Guid groupId)
    {
        this.layout = layout;
        this.catalog = catalog;
        GroupId = groupId;
        Members = catalog.ListPseudo3dMembers().Where(member => member.GroupId == groupId).OrderBy(member => member.Slot).ToArray();
        if (Members.Count == 0) throw new InvalidDataException("伪三维组成员不存在。");
        var blocks = new Dictionary<int, Dictionary<int, ProcessingBlockCatalogRecord>>();
        var archives = new Dictionary<int, DerivedArtifactCatalogRecord>();
        foreach (var member in Members)
        {
            var run = catalog.GetRun(member.RunId) ?? throw new InvalidDataException("伪三维成员实验不存在。");
            if (run.Status == ExperimentCatalog.RecordingStatus)
                throw new InvalidOperationException("伪三维组仍在采集，请停止两套设备后回放。");
            blocks[member.Slot] = catalog.ListProcessingBlocks(member.RunId).ToDictionary(block => block.BlockNumber);
            foreach (var artifact in catalog.ListDerivedArtifacts(member.RunId))
            {
                if (artifact.Kind.StartsWith("pseudo3d:", StringComparison.Ordinal))
                {
                    if (member.Slot != 0 || !archives.TryAdd(artifact.BlockNumber, artifact))
                        throw new InvalidDataException("同轮伪三维归档身份重复或层序错误。");
                }
                else artifacts[(member.RunId, artifact.BlockNumber, artifact.Kind)] = artifact;
            }
        }
        Rounds = blocks.Values.SelectMany(map => map.Keys).Union(archives.Keys).Distinct().Order()
            .Select(round => new Pseudo3dReplayRound(round, blocks.GetValueOrDefault(0)?.GetValueOrDefault(round),
                blocks.GetValueOrDefault(1)?.GetValueOrDefault(round), archives.GetValueOrDefault(round))).ToArray();
        meshes = new GlobalReconstructionMeshStore(layout, new DerivedArtifactHdf5Writer());
    }

    public Guid GroupId { get; }
    public IReadOnlyList<Pseudo3dExperimentMember> Members { get; }
    public IReadOnlyList<Pseudo3dReplayRound> Rounds { get; }
    public int ArchivedFrameCount => Rounds.Count(round => round.Archive is not null);

    public Pseudo3dReplayFrame ReadFrame(int index, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var round = Rounds[index];
        // Each read owns all handles and closes them before returning. A seek never holds a shard open.
        var lower = ReadLayer(0, round.Lower, token);
        var upper = ReadLayer(1, round.Upper, token);
        Pseudo3dArchivedFrame? volume = null;
        var status = "本轮未保存三维结果（参考准备、无合格配对或三维处理未完成）。";
        if (round.Archive is { } artifact)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var saved = new Pseudo3dArchiveStore().Read(layout.ResolveArtifactPath(artifact.ArtifactPath), artifact.DatasetPath);
                if (lower?.Stamp is null || upper?.Stamp is null ||
                    saved.Metadata.Lower.RunId != lower.Member.RunId || saved.Metadata.Upper.RunId != upper.Member.RunId ||
                    saved.Metadata.Lower.BlockNumber != round.Round || saved.Metadata.Upper.BlockNumber != round.Round ||
                    saved.Metadata.Lower.TimeDivision?.SessionId != GroupId || saved.Metadata.Upper.TimeDivision?.SessionId != GroupId ||
                    saved.Metadata.Lower.AcquiredAt != lower.Stamp.SampleMidpoint || saved.Metadata.Upper.AcquiredAt != upper.Stamp.SampleMidpoint)
                    throw new InvalidDataException("三维归档与所选组/轮次/双层身份不一致。");
                volume = saved;
                status = string.IsNullOrEmpty(saved.Metadata.Warning) ? "已加载采集时保存的三维结果。" : saved.Metadata.Warning;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = $"三维结果不可用：{ex.Message}";
            }
        }
        return new(round.Round, lower, upper, volume, status);
    }

    private Pseudo3dReplayLayer? ReadLayer(int slot, ProcessingBlockCatalogRecord? block, CancellationToken token)
    {
        var member = Members.SingleOrDefault(item => item.Slot == slot);
        if (member is null) return null;
        if (block is null) return new(member, null, [], null, null, "本轮缺少该设备数据。");
        Pseudo3dAcquisitionStamp? validatedStamp = null;
        double[] voltage = [];
        try
        {
            token.ThrowIfCancellationRequested();
            if (!artifacts.TryGetValue((member.RunId, block.BlockNumber, "demod"), out var artifact))
                return new(member, null, [], null, null, $"本轮解调未完成：{block.DemodStatus}");
            using var file = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(artifact.ArtifactPath));
            var root = DataRootLayout.GetDerivedBlockRoot(block.BlockNumber);
            if (Guid.Parse(file.Dataset(root + "/metadata/run/experiment_run_id").Read<string>()) != member.RunId ||
                file.Dataset(root + "/metadata/run/block_number").Read<int>() != block.BlockNumber ||
                file.Dataset(root + "/metadata/run/source_start_sample_index").Read<long>() != block.SourceStartSampleIndex ||
                file.Dataset(root + "/metadata/run/source_end_sample_index").Read<long>() != block.SourceEndSampleIndex)
                throw new InvalidDataException("采集块身份或样本范围不匹配。");
            var stamp = JsonSerializer.Deserialize<Pseudo3dAcquisitionStamp>(file.Dataset(root + "/demod/time_division_json").Read<string>());
            if (stamp is null || stamp.SessionId != GroupId || stamp.Slot != slot || stamp.Round != block.BlockNumber)
                throw new InvalidDataException("采集组、设备层序或轮次不匹配。");
            validatedStamp = stamp;
            voltage = file.Dataset(root + "/demod/mean_amplitude_208").Read<double[]>();
            if (!artifacts.TryGetValue((member.RunId, block.BlockNumber, "reconstruction"), out var reconArtifact))
                return new(member, stamp, voltage, null, null, $"本轮无二维重构：{block.ReconstructionStatus}");
            if (!string.Equals(reconArtifact.ArtifactPath, artifact.ArtifactPath, StringComparison.OrdinalIgnoreCase))
            {
                using var reconstruction = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(reconArtifact.ArtifactPath));
                return ReadReconstruction(reconstruction, root, member, stamp, voltage);
            }
            return ReadReconstruction(file, root, member, stamp, voltage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(member, validatedStamp, voltage, null, null, $"设备数据不可用：{ex.Message}");
        }
    }

    private Pseudo3dReplayLayer ReadReconstruction(NativeFile file, string root, Pseudo3dExperimentMember member,
        Pseudo3dAcquisitionStamp stamp, double[] voltage)
    {
        var metadata = JsonSerializer.Deserialize<DerivedReconstructionMetadata>(file.Dataset(root + "/metadata/reconstruction_json").Read<string>())
            ?? throw new InvalidDataException("二维重构元数据缺失。");
        if (metadata.ProcessingMode != "realtime")
            return new(member, stamp, voltage, null, metadata.ReferenceEpoch, "二维结果来自离线处理，未混入实时采集回放。");
        if (metadata.MeshArtifactPath is null || metadata.MeshFingerprint is null)
            throw new InvalidDataException("二维网格索引缺失。");
        if (!meshCache.TryGetValue(metadata.MeshFingerprint, out var mesh))
        {
            mesh = meshes.Load(metadata.MeshArtifactPath, metadata.MeshFingerprint);
            meshCache.Add(metadata.MeshFingerprint, mesh);
        }
        var conductivity = file.Dataset(root + "/reconstruction/conductivity").Read<double[]>();
        if (file.Dataset(root + "/metadata/stages/reconstruction/complete").Read<int>() != 1 ||
            metadata.MeshIndexSchema != mesh.MeshIndexMetadata.MeshIndexSchema ||
            metadata.ParameterEntity != mesh.MeshIndexMetadata.ParameterEntity ||
            metadata.LogicalMeshFingerprint != mesh.MeshIndexMetadata.LogicalMeshFingerprint ||
            metadata.OrderedIndexFingerprint != mesh.MeshIndexMetadata.OrderedIndexFingerprint)
            throw new InvalidDataException("二维重构未完整保存或网格索引与归档不一致。");
        mesh.MeshIndexMetadata.ValidateForResult(mesh.NodeCoords, mesh.CellConnectivity, conductivity.Length, requireCanonical: true);
        var config = catalog.GetRunConfig(member.RunId);
        var result = new RealtimeReconstructionResult((int)stamp.Round, "", conductivity, mesh.NodeCoords, mesh.CellConnectivity,
            stamp.SampleMidpoint, TimeSpan.Zero, OutputPersisted: false,
            ReconstructionScaleStatus: config?.ReconstructionScaleStatus ?? ReconstructionScale.ModelRelative,
            ReconstructionScaleProvenance: config?.ReconstructionScaleProvenance ?? "recorded",
            MeshIndexMetadata: mesh.MeshIndexMetadata);
        return new(member, stamp, voltage, result, metadata.ReferenceEpoch, $"已保存二维重构 · 参考 e{metadata.ReferenceEpoch}");
    }
}
