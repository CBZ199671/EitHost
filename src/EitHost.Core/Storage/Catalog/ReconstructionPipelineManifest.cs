using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EitHost.Core.Storage.Catalog;

public static class ReconstructionPipelineManifestStatus
{
    public const string Recording = "recording";
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
}

public sealed record ReconstructionPipelineManifestCatalogRecord(
    Guid ExperimentRunId,
    string SchemaVersion,
    string AlgorithmFingerprint,
    string ManifestFingerprint,
    string Status,
    string ManifestJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UnavailableReason = null);

public sealed record ReconstructionPipelineManifestPayload(
    Guid ExperimentRunId,
    ReconstructionDemodulationPolicy Demodulation,
    ReconstructionReferencePolicy Reference,
    ReconstructionWeightingPolicy Weighting,
    ReconstructionInversePolicy Inverse,
    ReconstructionDynamicKalmanPolicy DynamicKalman,
    ReconstructionPresentationPolicy Presentation,
    ReconstructionResetPolicy Reset,
    ReconstructionPipelineInputInventory? Inputs = null);

public sealed record ReconstructionDemodulationPolicy(
    string AlgorithmVersion,
    double SampleRateHz,
    double ExcitationFrequencyHz,
    double ChannelCycles,
    double DiscardLeadingCycles,
    double DiscardTrailingCycles,
    int FramesPerBlock,
    int MinimumAcceptedFrames,
    int ReadRows,
    int AdRangeCode,
    double AdcFullSpanVolts,
    double AdcLsbVolts,
    bool FrequencyDivisionLockIn,
    double[] InterferenceFrequencyHz,
    bool OutlierDetection,
    bool PersistAllDemodulatedBlocks);

public sealed record ReconstructionReferencePolicy(
    string ScalePolicy,
    string SelectionPolicyVersion,
    string DiagnosticPolicyVersion,
    string ContactOperatingFingerprintJson,
    string? ContactThresholdProfileId,
    string ContactThresholdMode,
    string NoiseEstimationPolicy,
    string BoundaryNoisePolicyVersion,
    bool PersistNoisePrecisionWeights);

public sealed record ReconstructionWeightingPolicy(
    bool OutlierCompensation,
    bool TemporalDespiking,
    int TemporalWindowSize,
    int TemporalCenterIndex,
    string TemporalPolicyVersion,
    string FinalWeightPolicyVersion,
    bool PersistPreTemporalWeights,
    bool PersistFinalWeights,
    bool AllowAllOneFallback);

public sealed record ReconstructionInversePolicy(
    string Route,
    string BackendProfile,
    string BackendContractVersion,
    double MeshSize,
    double DifferenceLambda,
    bool CustomLambdaEnabled,
    string DifferenceOrientation,
    string ReconstructionScale,
    string ReconstructionScaleProvenance,
    string ModelFamily,
    string MeshPolicyVersion);

public sealed record ReconstructionDynamicKalmanPolicy(
    bool Enabled,
    string Mode,
    int UpstreamLatencyFrames,
    double ProcessNoiseRelativeStd,
    double MeasurementNoiseRelativeStd,
    double InitialRelativeStd,
    double TransitionDecayPerBlock,
    string InnovationGate,
    double NisThresholdPerDof,
    double MaximumVarianceInflation,
    string SessionPolicyVersion,
    bool ImportLiveState);

public sealed record ReconstructionPresentationPolicy(
    string RendererVersion,
    string Colormap,
    string ColorScalePolicyVersion,
    string PolarityPolicy,
    string OverlayPolicyVersion,
    bool PersistPerFramePresentation);

public sealed record ReconstructionResetPolicy(
    string ReferenceEpochBoundary,
    string SampleDiscontinuity,
    string TemporalWindowEdge,
    string KalmanSession,
    string ChronologicalOrder);

public sealed record ReconstructionPipelineInputInventory(
    long RawSampleRows,
    int RawSegmentCount,
    int DemodBlockCount,
    int ReferenceEpochCount,
    bool AllDemodBlocksHaveDiagnosticsWeights,
    bool AllReferenceEpochsHaveNoisePrecision,
    bool MeshArtifactAvailable,
    ReconstructionRawInputIdentity[] RawSegments,
    ReconstructionDemodInputIdentity[] DemodBlocks,
    ReconstructionReferenceInputIdentity[] ReferenceEpochs,
    string? MeshArtifactPath);

public sealed record ReconstructionRawInputIdentity(
    int SegmentSequence,
    string ArtifactPath,
    string DatasetPath,
    long StartSampleIndex,
    long EndSampleIndex,
    long SampleRows,
    long ArtifactBytes,
    bool HasDiscontinuity);

public sealed record ReconstructionDemodInputIdentity(
    int BlockNumber,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    string DemodStatus,
    string? DemodArtifactPath,
    string? DiagnosticsArtifactPath,
    bool HasPersistedPreTemporalWeights);

public sealed record ReconstructionReferenceInputIdentity(
    int ReferenceEpoch,
    int LockedBlockNumber,
    long LockedStartSampleIndex,
    string ArtifactPath,
    string DatasetPath,
    bool HasNoisePrecisionWeights);

public sealed record OfflinePipelineReadiness(
    bool Available,
    string Reason,
    ReconstructionPipelineManifestPayload? Manifest,
    string? AlgorithmFingerprint)
{
    public static OfflinePipelineReadiness Unavailable(string reason) => new(false, reason, null, null);
}

public static class ReconstructionPipelineManifestCodec
{
    public const string CurrentSchemaVersion = "reconstruction-pipeline-v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static ReconstructionPipelineManifestCatalogRecord Create(
        ReconstructionPipelineManifestPayload payload,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload);
        if (status is not (ReconstructionPipelineManifestStatus.Recording or
            ReconstructionPipelineManifestStatus.Ready or
            ReconstructionPipelineManifestStatus.Unavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (status == ReconstructionPipelineManifestStatus.Ready && payload.Inputs is null)
        {
            throw new ArgumentException("Ready pipeline manifest requires finalized input inventory.", nameof(payload));
        }

        if (status == ReconstructionPipelineManifestStatus.Unavailable && string.IsNullOrWhiteSpace(unavailableReason))
        {
            throw new ArgumentException("Unavailable pipeline manifest requires an exact reason.", nameof(unavailableReason));
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var algorithmJson = JsonSerializer.Serialize(payload with { Inputs = null }, JsonOptions);
        return new ReconstructionPipelineManifestCatalogRecord(
            payload.ExperimentRunId,
            CurrentSchemaVersion,
            CalculateFingerprint(algorithmJson),
            CalculateFingerprint(json),
            status,
            json,
            createdAt,
            updatedAt,
            unavailableReason);
    }

    public static OfflinePipelineReadiness EvaluateForOffline(
        ReconstructionPipelineManifestCatalogRecord? record)
    {
        if (record is null)
        {
            return OfflinePipelineReadiness.Unavailable(
                "缺少 reconstruction-pipeline-v1 清单；禁止使用当前默认值补算。");
        }

        ReconstructionPipelineManifestPayload? payload;
        try
        {
            payload = ReadPayload(record);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return OfflinePipelineReadiness.Unavailable($"算法清单无效：{ex.Message}");
        }

        if (!string.Equals(record.Status, ReconstructionPipelineManifestStatus.Ready, StringComparison.Ordinal))
        {
            return OfflinePipelineReadiness.Unavailable(
                record.UnavailableReason ?? $"算法清单尚未就绪：{record.Status}。");
        }

        var inputs = payload.Inputs;
        if (inputs is null)
        {
            return OfflinePipelineReadiness.Unavailable("算法清单缺少终态输入清单。");
        }

        if (inputs.RawSegmentCount == 0 || inputs.RawSampleRows == 0)
        {
            return OfflinePipelineReadiness.Unavailable("实验没有可恢复的原始采集区间。");
        }

        if (inputs.DemodBlockCount == 0)
        {
            return OfflinePipelineReadiness.Unavailable("实验没有可用于完整重算的解调块。");
        }

        if (inputs.ReferenceEpochCount == 0)
        {
            return OfflinePipelineReadiness.Unavailable("实验没有已落盘的参考帧 epoch。");
        }

        if (!inputs.AllDemodBlocksHaveDiagnosticsWeights || !payload.Weighting.PersistPreTemporalWeights)
        {
            return OfflinePipelineReadiness.Unavailable(
                "存在缺失的时序前诊断权重；禁止回退为 all-one 权重。");
        }

        if (!inputs.AllReferenceEpochsHaveNoisePrecision || !payload.Reference.PersistNoisePrecisionWeights)
        {
            return OfflinePipelineReadiness.Unavailable(
                "存在缺失的参考噪声 precision 权重；无法复现最终权重策略。");
        }

        if (!inputs.MeshArtifactAvailable)
        {
            return OfflinePipelineReadiness.Unavailable("缺少本次运行对应的网格/模型工件。");
        }

        if (payload.Weighting.AllowAllOneFallback)
        {
            return OfflinePipelineReadiness.Unavailable("算法清单允许 all-one 回退，不满足等价重算要求。");
        }

        if (payload.DynamicKalman.ImportLiveState)
        {
            return OfflinePipelineReadiness.Unavailable("算法清单错误地要求导入实时 Kalman 状态。");
        }

        return new OfflinePipelineReadiness(true, "ready", payload, record.AlgorithmFingerprint);
    }

    public static ReconstructionPipelineManifestPayload ReadPayload(
        ReconstructionPipelineManifestCatalogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"算法清单版本不兼容：{record.SchemaVersion}；需要 {CurrentSchemaVersion}。");
        }

        if (!string.Equals(record.ManifestFingerprint, CalculateFingerprint(record.ManifestJson), StringComparison.Ordinal))
        {
            throw new InvalidDataException("算法清单 SHA-256 校验失败。");
        }

        var payload = JsonSerializer.Deserialize<ReconstructionPipelineManifestPayload>(record.ManifestJson, JsonOptions)
            ?? throw new InvalidDataException("算法清单内容为空。");
        ValidatePayload(payload);
        var algorithmJson = JsonSerializer.Serialize(payload with { Inputs = null }, JsonOptions);
        if (!string.Equals(record.AlgorithmFingerprint, CalculateFingerprint(algorithmJson), StringComparison.Ordinal))
        {
            throw new InvalidDataException("算法策略 SHA-256 校验失败。");
        }

        return payload;
    }

    private static string CalculateFingerprint(string manifestJson) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));

    private static void ValidatePayload(ReconstructionPipelineManifestPayload payload)
    {
        if (payload.ExperimentRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(payload.Demodulation.AlgorithmVersion) ||
            payload.Demodulation.SampleRateHz <= 0 ||
            payload.Demodulation.ExcitationFrequencyHz <= 0 ||
            payload.Demodulation.FramesPerBlock <= 0 ||
            payload.Weighting.TemporalWindowSize <= 0 ||
            payload.Weighting.TemporalCenterIndex < 0 ||
            payload.Weighting.TemporalCenterIndex >= payload.Weighting.TemporalWindowSize ||
            string.IsNullOrWhiteSpace(payload.Weighting.TemporalPolicyVersion) ||
            string.IsNullOrWhiteSpace(payload.Inverse.Route) ||
            string.IsNullOrWhiteSpace(payload.Inverse.BackendProfile) ||
            string.IsNullOrWhiteSpace(payload.Presentation.RendererVersion) ||
            (payload.DynamicKalman.Enabled &&
             (string.IsNullOrWhiteSpace(payload.DynamicKalman.Mode) ||
              payload.DynamicKalman.UpstreamLatencyFrames != 2)))
        {
            throw new ArgumentException("Pipeline manifest is incomplete.", nameof(payload));
        }
    }
}
