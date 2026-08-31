using System.Text.Json;
using EitHost.Core.Demodulation;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public sealed class DerivedArtifactHdf5Writer
{
    private static readonly object[] BlockWriteLocks =
        Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static readonly HashSet<string> SupportedLegacyBlockGroups =
    [
        "/metadata",
        "/metadata/run",
        "/metadata/stages",
        "/metadata/stages/demod",
        "/metadata/stages/diagnostics",
        "/metadata/stages/reconstruction",
        "/metadata/stages/reference_candidates",
        "/demod",
        "/quality",
        "/diagnostics",
        "/replay_demod_override",
        "/reconstruction",
        "/input",
        "/candidates"
    ];
    private static readonly HashSet<string> SupportedLegacyBlockDatasets =
        CreateSupportedLegacyBlockDatasets();

    public static IReadOnlyList<ushort> NumericFilterIds => Hdf5StoragePolicy.NumericFilterIds;

    public void WriteDemodulatedBlock(string filePath, DerivedDemodulatedBlockData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var block = data.Block;
        WriteBlockStage(
            filePath,
            new BlockIdentity(
                data.ExperimentRunId,
                block.BlockNumber,
                block.StartSampleIndex,
                block.EndSampleIndex),
            new StageTiming("demod", data.AcquiredAt, data.ProcessedAt),
            snapshot =>
            {
                snapshot.Content["demod"] = new H5Group
                {
                    ["mean_amplitude_208"] = block.MeanAmplitude208,
                    ["mean_real_208"] = block.MeanReal208,
                    ["mean_imaginary_208"] = block.MeanImaginary208,
                    ["mean_full_amplitude_256"] = block.MeanFullAmplitude256,
                    ["mean_full_real_256"] = block.MeanFullReal256,
                    ["mean_full_imaginary_256"] = block.MeanFullImaginary256
                };
                snapshot.Content["quality"] = new H5Group
                {
                    ["weight"] = block.QualityWeight,
                    ["is_high_quality"] = block.IsHighQuality,
                    ["accepted_frames"] = block.AcceptedFrameCount,
                    ["rejected_frames"] = block.RejectedFrameCount,
                    ["uniform_integration_stable"] = block.UniformIntegrationStable,
                    ["uniform_integration_instability"] = block.UniformIntegrationInstability
                };
            });
    }

    public void WriteReconstruction(string filePath, DerivedReconstructionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(data.MeshFingerprint) != string.IsNullOrWhiteSpace(data.MeshArtifactPath))
        {
            throw new InvalidDataException("Reconstruction mesh fingerprint and artifact path must be recorded together.");
        }

        var reconstruction = new H5Group
        {
            ["conductivity"] = data.Conductivity,
            ["image_quality_score"] = data.ImageQualityScore ?? double.NaN,
            ["weighted_system_condition_number"] = data.WeightedSystemConditionNumber ?? double.NaN
        };
        if (data.RawConductivity is { Length: > 0 } rawConductivity)
        {
            reconstruction["raw_conductivity"] = rawConductivity;
        }

        var input = new H5Group();
        if (data.ReferenceVoltage208 is { Length: > 0 } reference)
        {
            input["reference_voltage_208"] = reference;
        }

        if (data.TargetVoltage208 is { Length: > 0 } target)
        {
            input["target_voltage_208"] = target;
        }

        if (data.MeasurementWeight208 is { Length: > 0 } weights)
        {
            input["measurement_weight_208"] = weights;
        }

        WriteBlockStage(
            filePath,
            new BlockIdentity(
                data.ExperimentRunId,
                data.BlockNumber,
                data.SourceStartSampleIndex,
                data.SourceEndSampleIndex),
            new StageTiming(
                "reconstruction",
                data.AcquiredAt,
                data.ProcessedAt,
                data.ProcessingMode,
                data.WeightPolicyVersion,
                data.ReferenceEpoch),
            snapshot =>
            {
                snapshot.Content["reconstruction"] = reconstruction;
                if (input.Count > 0)
                {
                    snapshot.Content["input"] = input;
                }

                snapshot.ReconstructionJson = JsonSerializer.Serialize(
                    DerivedReconstructionMetadata.From(data));
            });
    }

    public void WriteFrameDiagnostics(string filePath, DerivedFrameDiagnosticsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var frame = data.Frame;
        var diagnostics = new H5Group
        {
            ["metadata_json"] = JsonSerializer.Serialize(DerivedFrameDiagnosticsMetadata.From(frame))
        };
        AddOptional(diagnostics, "measurement_weight_208", frame.MeasurementWeight208);
        AddOptional(diagnostics, "electrode_scores", frame.ElectrodeScores);
        AddOptional(diagnostics, "fault_confidence", frame.FaultConfidence);

        H5Group? replayOverride = null;
        if (data.PersistReplayDemodOverride)
        {
            replayOverride = new H5Group
            {
                ["mean_amplitude_208"] = frame.MeanAmplitude208,
                ["mean_real_208"] = frame.MeanReal208,
                ["mean_imaginary_208"] = frame.MeanImaginary208,
                ["mean_full_amplitude_256"] = frame.MeanFullAmplitude256 ?? [],
                ["mean_full_real_256"] = frame.MeanFullReal256 ?? [],
                ["mean_full_imaginary_256"] = frame.MeanFullImaginary256 ?? []
            };
        }

        WriteBlockStage(
            filePath,
            new BlockIdentity(
                data.ExperimentRunId,
                frame.BlockNumber,
                data.SourceStartSampleIndex,
                data.SourceEndSampleIndex),
            new StageTiming("diagnostics", data.AcquiredAt, data.ProcessedAt),
            snapshot =>
            {
                snapshot.Content["diagnostics"] = diagnostics;
                if (replayOverride is not null)
                {
                    snapshot.Content["replay_demod_override"] = replayOverride;
                }
            });
    }

    public void WriteReferenceEpoch(string filePath, ImagingReferenceEpochRecord epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch.LockedStartSampleIndex);
        if (epoch.NoisePrecisionWeight208 is { Length: not 208 })
        {
            throw new ArgumentException("Reference noise precision weights must contain 208 values.", nameof(epoch));
        }

        var referenceGroup = new H5Group
        {
            ["amplitude_208"] = epoch.ReferenceAmplitude208,
            ["full_real_256"] = epoch.ReferenceFullReal256,
            ["full_imaginary_256"] = epoch.ReferenceFullImaginary256
        };
        AddOptional(referenceGroup, "noise_precision_weight_208", epoch.NoisePrecisionWeight208);
        var file = new H5File
        {
            ["reference"] = referenceGroup,
            ["metadata"] = new H5Group
            {
                ["run"] = new H5Group
                {
                    ["experiment_run_id"] = epoch.ImagingRunId.ToString("D"),
                    ["reference_epoch"] = epoch.ReferenceEpoch,
                    ["locked_block_number"] = epoch.LockedBlockNumber,
                    ["locked_start_sample_index"] = epoch.LockedStartSampleIndex,
                    ["locked_at_utc"] = epoch.LockedAt.ToUniversalTime().ToString("O"),
                    ["kind"] = "reference"
                },
                ["reference_json"] = JsonSerializer.Serialize(DerivedReferenceEpochMetadata.From(epoch))
            }
        };
        WriteAtomicIfMissing(filePath, file);
    }

    public void WriteReferenceCandidates(string filePath, DerivedReferenceCandidateBlockData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Candidates.Count == 0)
        {
            throw new ArgumentException("Reference candidate block is empty.", nameof(data));
        }

        if (data.Candidates.Any(candidate =>
                candidate.ImagingRunId != data.ExperimentRunId ||
                candidate.BlockNumber != data.BlockNumber))
        {
            throw new ArgumentException("Reference candidate identity does not match block.", nameof(data));
        }

        WriteBlockStage(
            filePath,
            new BlockIdentity(
                data.ExperimentRunId,
                data.BlockNumber,
                data.SourceStartSampleIndex,
                data.SourceEndSampleIndex),
            new StageTiming("reference_candidates", data.AcquiredAt, data.CreatedAt),
            snapshot => snapshot.Content["candidates"] = new H5Group
            {
                ["voltage_208"] = PackRows(
                    data.Candidates.Select(candidate => candidate.Voltage208).ToArray()),
                ["full_real_256"] = PackRows(
                    data.Candidates.Select(candidate => candidate.FullReal256).ToArray()),
                ["full_imaginary_256"] = PackRows(
                    data.Candidates.Select(candidate => candidate.FullImaginary256).ToArray()),
                ["metadata_json"] = JsonSerializer.Serialize(
                    data.Candidates.Select(DerivedReferenceCandidateMetadata.From).ToArray())
            });
    }

    public void WriteMesh(string filePath, DerivedMeshData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var file = new H5File
        {
            ["mesh"] = new H5Group
            {
                ["node_coords"] = Hdf5StoragePolicy.Numeric(data.NodeCoords),
                ["cell_connectivity"] = Hdf5StoragePolicy.Numeric(data.CellConnectivity)
            },
            ["metadata"] = new H5Group
            {
                ["run"] = new H5Group
                {
                    ["experiment_run_id"] = data.ExperimentRunId.ToString("D"),
                    ["kind"] = "mesh",
                    ["created_at_utc"] = data.CreatedAt.ToUniversalTime().ToString("O"),
                    ["mesh_fingerprint"] = data.Fingerprint ?? string.Empty,
                    ["mesh_index_schema"] = data.MeshIndexSchema ?? string.Empty,
                    ["parameter_entity"] = data.ParameterEntity ?? string.Empty,
                    ["logical_mesh_fingerprint"] = data.LogicalMeshFingerprint ?? string.Empty,
                    ["ordered_index_fingerprint"] = data.OrderedIndexFingerprint ?? string.Empty
                }
            }
        };
        WriteAtomicIfMissing(filePath, file);
    }

    private static void WriteBlockStage(
        string filePath,
        BlockIdentity identity,
        StageTiming timing,
        Action<BlockArtifactSnapshot> applyStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(applyStage);
        var fullPath = Path.GetFullPath(filePath);
        var blockRoot = DataRootLayout.IsCanonicalDerivedShardPath(fullPath) ||
                        DataRootLayout.IsCanonicalOfflineDerivedShardPath(fullPath)
            ? DataRootLayout.GetDerivedBlockRoot(identity.BlockNumber)
            : string.Empty;
        var lockIndex = (int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath) %
                              BlockWriteLocks.Length);
        lock (BlockWriteLocks[lockIndex])
        {
            if (!File.Exists(fullPath))
            {
                var firstStage = CreateStageArtifact(
                    identity,
                    timing,
                    timing.Stage,
                    includeCompletionMarker: false,
                    applyStage);
                WriteNativeAtomic(fullPath, firstStage.Content, timing.Stage, blockRoot);
                return;
            }

            bool hasCommittedStage;
            bool requiresLegacyMigration;
            using (var file = Hdf5FileAccess.OpenReadWithRetry(fullPath))
            {
                if (blockRoot.Length > 0 && file.LinkExists("/metadata/run/block_number"))
                {
                    blockRoot = string.Empty;
                }

                var blockExists = blockRoot.Length == 0 || file.LinkExists(blockRoot);
                if (!blockExists)
                {
                    hasCommittedStage = false;
                    requiresLegacyMigration = false;
                }
                else
                {
                    ValidateBlockIdentity(file, identity, blockRoot);
                    if (IsStageCommitted(file, timing.Stage, blockRoot))
                    {
                        return;
                    }

                    hasCommittedStage = HasAnyCommittedStage(file, blockRoot);
                    requiresLegacyMigration = blockRoot.Length == 0 && !string.Equals(
                        ReadOptionalString(file, "/metadata/run/artifact_format"),
                        "derived_block_v2",
                        StringComparison.Ordinal);
                }
            }

            if (requiresLegacyMigration)
            {
                var legacy = ReadLegacyBlockArtifact(fullPath, identity);
                WriteLegacyMigrationAtomic(fullPath, identity, legacy);
                hasCommittedStage = legacy.Stages.Count > 0;
            }

            var appendedStage = CreateStageArtifact(
                identity,
                timing,
                hasCommittedStage ? "combined" : timing.Stage,
                includeCompletionMarker: false,
                applyStage);
            Hdf5IncrementalStageAppender.Append(
                fullPath,
                appendedStage.Content,
                timing.Stage,
                blockRoot);
        }
    }

    private static BlockArtifactSnapshot CreateStageArtifact(
        BlockIdentity identity,
        StageTiming timing,
        string runStage,
        bool includeCompletionMarker,
        Action<BlockArtifactSnapshot> applyStage)
    {
        var snapshot = new BlockArtifactSnapshot();
        applyStage(snapshot);
        snapshot.Stages[timing.Stage] = timing;
        snapshot.Content["metadata"] = CreateBlockMetadata(
            identity,
            snapshot,
            timing,
            runStage,
            includeCompletionMarker);
        return snapshot;
    }

    private static bool IsStageCommitted(IH5Group file, string stage, string blockRoot = "")
    {
        var completionPath = Prefix(blockRoot, $"/metadata/stages/{stage}/complete");
        if (file.LinkExists(completionPath))
        {
            return file.Dataset(completionPath).Read<int>() == 1;
        }

        var artifactFormat = ReadOptionalString(
            file,
            Prefix(blockRoot, "/metadata/run/artifact_format"));
        return !string.Equals(artifactFormat, "derived_block_v2", StringComparison.Ordinal) &&
               StagePayloadExists(file, stage, blockRoot);
    }

    private static bool HasAnyCommittedStage(IH5Group file, string blockRoot = "") =>
        new[] { "demod", "diagnostics", "reconstruction", "reference_candidates" }
            .Any(stage => IsStageCommitted(file, stage, blockRoot));

    private static bool StagePayloadExists(IH5Group file, string stage, string blockRoot = "") =>
        stage switch
        {
            "demod" => file.LinkExists(Prefix(blockRoot, "/demod")),
            "diagnostics" => file.LinkExists(Prefix(blockRoot, "/diagnostics")),
            "reconstruction" => file.LinkExists(Prefix(blockRoot, "/reconstruction")),
            "reference_candidates" => file.LinkExists(Prefix(blockRoot, "/candidates")),
            _ => false
        };

    private static BlockArtifactSnapshot ReadLegacyBlockArtifact(
        string filePath,
        BlockIdentity identity)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(filePath);
        ValidateBlockIdentity(file, identity);
        ValidateLegacyMigrationSupport(file);
        var snapshot = new BlockArtifactSnapshot();
        if (file.LinkExists("/demod"))
        {
            snapshot.Content["demod"] = new H5Group
            {
                ["mean_amplitude_208"] = file.Dataset("/demod/mean_amplitude_208").Read<double[]>(),
                ["mean_real_208"] = file.Dataset("/demod/mean_real_208").Read<double[]>(),
                ["mean_imaginary_208"] = file.Dataset("/demod/mean_imaginary_208").Read<double[]>(),
                ["mean_full_amplitude_256"] = file.Dataset("/demod/mean_full_amplitude_256").Read<double[]>(),
                ["mean_full_real_256"] = file.Dataset("/demod/mean_full_real_256").Read<double[]>(),
                ["mean_full_imaginary_256"] = file.Dataset("/demod/mean_full_imaginary_256").Read<double[]>()
            };
            snapshot.Content["quality"] = new H5Group
            {
                ["weight"] = ReadOptional(file, "/quality/weight", 1.0),
                ["is_high_quality"] = ReadOptional(file, "/quality/is_high_quality", true),
                ["accepted_frames"] = ReadOptional(file, "/quality/accepted_frames", 0),
                ["rejected_frames"] = ReadOptional(file, "/quality/rejected_frames", 0),
                ["uniform_integration_stable"] = ReadOptional(
                    file,
                    "/quality/uniform_integration_stable",
                    true),
                ["uniform_integration_instability"] = ReadOptional(
                    file,
                    "/quality/uniform_integration_instability",
                    0.0)
            };
            AddLegacyStageTiming(file, snapshot, "demod");
        }

        if (file.LinkExists("/diagnostics"))
        {
            var diagnostics = new H5Group
            {
                ["metadata_json"] = file.Dataset("/diagnostics/metadata_json").Read<string>()
            };
            CopyOptionalLegacyDoubleArray(
                file,
                diagnostics,
                "/diagnostics/measurement_weight_208",
                "measurement_weight_208");
            CopyOptionalLegacyDoubleArray(
                file,
                diagnostics,
                "/diagnostics/electrode_scores",
                "electrode_scores");
            CopyOptionalLegacyDoubleArray(
                file,
                diagnostics,
                "/diagnostics/fault_confidence",
                "fault_confidence");
            snapshot.Content["diagnostics"] = diagnostics;
            if (file.LinkExists("/replay_demod_override"))
            {
                snapshot.Content["replay_demod_override"] = ReadLegacyVoltageGroup(
                    file,
                    "/replay_demod_override");
            }

            AddLegacyStageTiming(file, snapshot, "diagnostics");
        }

        if (file.LinkExists("/reconstruction"))
        {
            var reconstruction = new H5Group
            {
                ["conductivity"] = file.Dataset("/reconstruction/conductivity").Read<double[]>(),
                ["image_quality_score"] = ReadOptional(
                    file,
                    "/reconstruction/image_quality_score",
                    double.NaN),
                ["weighted_system_condition_number"] = ReadOptional(
                    file,
                    "/reconstruction/weighted_system_condition_number",
                    double.NaN)
            };
            CopyOptionalLegacyDoubleArray(
                file,
                reconstruction,
                "/reconstruction/raw_conductivity",
                "raw_conductivity");
            snapshot.Content["reconstruction"] = reconstruction;
            var input = new H5Group();
            CopyOptionalLegacyDoubleArray(file, input, "/input/reference_voltage_208", "reference_voltage_208");
            CopyOptionalLegacyDoubleArray(file, input, "/input/target_voltage_208", "target_voltage_208");
            CopyOptionalLegacyDoubleArray(file, input, "/input/measurement_weight_208", "measurement_weight_208");
            if (input.Count > 0)
            {
                snapshot.Content["input"] = input;
            }

            snapshot.ReconstructionJson = ReadOptionalString(file, "/metadata/reconstruction_json");
            AddLegacyStageTiming(file, snapshot, "reconstruction");
        }

        return snapshot;
    }

    private static H5Group ReadLegacyVoltageGroup(IH5Group file, string path) =>
        new()
        {
            ["mean_amplitude_208"] = file.Dataset($"{path}/mean_amplitude_208").Read<double[]>(),
            ["mean_real_208"] = file.Dataset($"{path}/mean_real_208").Read<double[]>(),
            ["mean_imaginary_208"] = file.Dataset($"{path}/mean_imaginary_208").Read<double[]>(),
            ["mean_full_amplitude_256"] = file.Dataset($"{path}/mean_full_amplitude_256").Read<double[]>(),
            ["mean_full_real_256"] = file.Dataset($"{path}/mean_full_real_256").Read<double[]>(),
            ["mean_full_imaginary_256"] = file.Dataset($"{path}/mean_full_imaginary_256").Read<double[]>()
        };

    private static void AddLegacyStageTiming(
        IH5Group file,
        BlockArtifactSnapshot snapshot,
        string stage)
    {
        var acquiredPath = $"/metadata/stages/{stage}/acquired_at_utc";
        var processedPath = $"/metadata/stages/{stage}/processed_at_utc";
        var acquired = file.LinkExists(acquiredPath)
            ? DateTimeOffset.Parse(file.Dataset(acquiredPath).Read<string>())
            : DateTimeOffset.Parse(file.Dataset("/metadata/run/acquired_at_utc").Read<string>());
        var processed = file.LinkExists(processedPath)
            ? DateTimeOffset.Parse(file.Dataset(processedPath).Read<string>())
            : DateTimeOffset.Parse(file.Dataset("/metadata/run/processed_at_utc").Read<string>());
        snapshot.Stages[stage] = new StageTiming(
            stage,
            acquired,
            processed,
            ReadOptionalString(file, $"/metadata/stages/{stage}/processing_mode") ??
            ReadOptionalString(file, "/metadata/run/processing_mode"),
            ReadOptionalString(file, $"/metadata/stages/{stage}/weight_policy_version") ??
            ReadOptionalString(file, "/metadata/run/weight_policy_version"),
            ReadOptionalNullableInt(file, $"/metadata/stages/{stage}/reference_epoch") ??
            ReadOptionalNullableInt(file, "/metadata/run/reference_epoch"));
    }

    private static void ValidateLegacyMigrationSupport(IH5Group file)
    {
        ValidateLegacyAttributes(file, "/");
        ValidateLegacyChildren(file, "/");
    }

    private static void ValidateLegacyChildren(IH5Group group, string parentPath)
    {
        foreach (var child in group.Children())
        {
            var leafName = child.Name[(child.Name.LastIndexOf('/') + 1)..];
            var path = parentPath == "/" ? $"/{leafName}" : $"{parentPath}/{leafName}";
            ValidateLegacyAttributes(child, path);
            if (child is IH5Group childGroup)
            {
                if (!SupportedLegacyBlockGroups.Contains(path))
                {
                    throw UnsupportedLegacyMigrationPath(path);
                }

                ValidateLegacyChildren(childGroup, path);
            }
            else if (child is not IH5Dataset || !SupportedLegacyBlockDatasets.Contains(path))
            {
                throw UnsupportedLegacyMigrationPath(path);
            }
        }
    }

    private static void ValidateLegacyAttributes(IH5Object item, string path)
    {
        var unsupported = item.Attributes().FirstOrDefault();
        if (unsupported is not null)
        {
            throw UnsupportedLegacyMigrationPath($"{path}@{unsupported.Name}");
        }
    }

    private static InvalidDataException UnsupportedLegacyMigrationPath(string path) =>
        new($"Legacy derived block contains unsupported data that cannot be migrated losslessly: {path}");

    private static HashSet<string> CreateSupportedLegacyBlockDatasets()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal)
        {
            "/metadata/reconstruction_json",
            "/metadata/run/experiment_run_id",
            "/metadata/run/block_number",
            "/metadata/run/source_start_sample_index",
            "/metadata/run/source_end_sample_index",
            "/metadata/run/acquired_at_utc",
            "/metadata/run/processed_at_utc",
            "/metadata/run/stage",
            "/metadata/run/artifact_format",
            "/metadata/run/processing_mode",
            "/metadata/run/weight_policy_version",
            "/metadata/run/reference_epoch",
            "/demod/mean_amplitude_208",
            "/demod/mean_real_208",
            "/demod/mean_imaginary_208",
            "/demod/mean_full_amplitude_256",
            "/demod/mean_full_real_256",
            "/demod/mean_full_imaginary_256",
            "/quality/weight",
            "/quality/is_high_quality",
            "/quality/accepted_frames",
            "/quality/rejected_frames",
            "/quality/uniform_integration_stable",
            "/quality/uniform_integration_instability",
            "/diagnostics/metadata_json",
            "/diagnostics/measurement_weight_208",
            "/diagnostics/electrode_scores",
            "/diagnostics/fault_confidence",
            "/replay_demod_override/mean_amplitude_208",
            "/replay_demod_override/mean_real_208",
            "/replay_demod_override/mean_imaginary_208",
            "/replay_demod_override/mean_full_amplitude_256",
            "/replay_demod_override/mean_full_real_256",
            "/replay_demod_override/mean_full_imaginary_256",
            "/reconstruction/conductivity",
            "/reconstruction/raw_conductivity",
            "/reconstruction/image_quality_score",
            "/reconstruction/weighted_system_condition_number",
            "/input/reference_voltage_208",
            "/input/target_voltage_208",
            "/input/measurement_weight_208",
            "/candidates/voltage_208",
            "/candidates/full_real_256",
            "/candidates/full_imaginary_256",
            "/candidates/metadata_json"
        };
        foreach (var stage in new[] { "demod", "diagnostics", "reconstruction", "reference_candidates" })
        {
            paths.Add($"/metadata/stages/{stage}/acquired_at_utc");
            paths.Add($"/metadata/stages/{stage}/processed_at_utc");
            paths.Add($"/metadata/stages/{stage}/processing_mode");
            paths.Add($"/metadata/stages/{stage}/weight_policy_version");
            paths.Add($"/metadata/stages/{stage}/reference_epoch");
        }

        return paths;
    }

    private static H5Group CreateBlockMetadata(
        BlockIdentity identity,
        BlockArtifactSnapshot snapshot,
        StageTiming latest,
        string runStage,
        bool includeCompletionMarker)
    {
        var run = CreateRunGroup(
            identity.ExperimentRunId,
            identity.BlockNumber,
            identity.SourceStartSampleIndex,
            identity.SourceEndSampleIndex,
            latest.AcquiredAt,
            latest.ProcessedAt,
            runStage);
        run["artifact_format"] = "derived_block_v2";
        if (snapshot.Stages.TryGetValue("reconstruction", out var reconstructionStage))
        {
            AddReconstructionProvenance(run, reconstructionStage);
        }

        var stages = new H5Group();
        foreach (var stage in snapshot.Stages.Values.OrderBy(item => item.Stage, StringComparer.Ordinal))
        {
            var stageGroup = new H5Group
            {
                ["acquired_at_utc"] = stage.AcquiredAt.ToUniversalTime().ToString("O"),
                ["processed_at_utc"] = stage.ProcessedAt.ToUniversalTime().ToString("O")
            };
            AddReconstructionProvenance(stageGroup, stage);
            if (includeCompletionMarker)
            {
                stageGroup["complete"] = 1;
            }

            stages[stage.Stage] = stageGroup;
        }

        var metadata = new H5Group
        {
            ["run"] = run,
            ["stages"] = stages
        };
        if (snapshot.ReconstructionJson is not null)
        {
            metadata["reconstruction_json"] = snapshot.ReconstructionJson;
        }

        return metadata;
    }

    private static void AddReconstructionProvenance(H5Group group, StageTiming stage)
    {
        if (stage.ProcessingMode is not null)
        {
            group["processing_mode"] = stage.ProcessingMode;
        }

        if (stage.WeightPolicyVersion is not null)
        {
            group["weight_policy_version"] = stage.WeightPolicyVersion;
        }

        if (stage.ReferenceEpoch is { } referenceEpoch)
        {
            group["reference_epoch"] = referenceEpoch;
        }
    }

    private static string? ReadOptionalString(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<string>() : null;

    private static int? ReadOptionalNullableInt(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<int>() : null;

    private static void CopyOptionalLegacyDoubleArray(
        IH5Group source,
        H5Group destination,
        string path,
        string name)
    {
        if (source.LinkExists(path))
        {
            destination[name] = source.Dataset(path).Read<double[]>();
        }
    }

    private static T ReadOptional<T>(IH5Group file, string path, T fallback)
        where T : struct =>
        file.LinkExists(path) ? file.Dataset(path).Read<T>() : fallback;

    private static void ValidateBlockIdentity(
        IH5Group file,
        BlockIdentity identity,
        string blockRoot = "")
    {
        var runRoot = Prefix(blockRoot, "/metadata/run");
        var embeddedRunId = Guid.Parse(file.Dataset($"{runRoot}/experiment_run_id").Read<string>());
        var embeddedBlock = file.Dataset($"{runRoot}/block_number").Read<int>();
        var embeddedStart = file.Dataset($"{runRoot}/source_start_sample_index").Read<long>();
        var embeddedEnd = file.Dataset($"{runRoot}/source_end_sample_index").Read<long>();
        if (embeddedRunId != identity.ExperimentRunId ||
            embeddedBlock != identity.BlockNumber ||
            embeddedStart != identity.SourceStartSampleIndex ||
            embeddedEnd != identity.SourceEndSampleIndex)
        {
            throw new InvalidDataException(
                "Derived block HDF5 identity does not match the stage being merged.");
        }
    }

    private static void WriteNativeAtomic(
        string fullPath,
        H5File file,
        string stage,
        string blockRoot = "")
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.partial";
        try
        {
            Hdf5IncrementalStageAppender.Create(temporaryPath, file, stage, blockRoot);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    private static string Prefix(string root, string path)
    {
        var normalizedRoot = string.IsNullOrWhiteSpace(root) || root == "/"
            ? string.Empty
            : $"/{root.Trim().Trim('/')}";
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"{normalizedRoot}{normalizedPath}";
    }

    private static void WriteLegacyMigrationAtomic(
        string fullPath,
        BlockIdentity identity,
        BlockArtifactSnapshot snapshot)
    {
        if (snapshot.Stages.Count == 0)
        {
            throw new InvalidDataException("Legacy derived block contains no recognized stage to migrate.");
        }

        var latest = snapshot.Stages.Values.MaxBy(item => item.ProcessedAt)!;
        snapshot.Content["metadata"] = CreateBlockMetadata(
            identity,
            snapshot,
            latest,
            snapshot.Stages.Count == 1 ? latest.Stage : "combined",
            includeCompletionMarker: true);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.migration.partial";
        try
        {
            Hdf5IncrementalStageAppender.CreateMigrated(temporaryPath, snapshot.Content);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    private sealed class BlockArtifactSnapshot
    {
        public H5File Content { get; } = new();

        public Dictionary<string, StageTiming> Stages { get; } =
            new(StringComparer.Ordinal);

        public string? ReconstructionJson { get; set; }
    }

    private sealed record BlockIdentity(
        Guid ExperimentRunId,
        int BlockNumber,
        long SourceStartSampleIndex,
        long SourceEndSampleIndex);

    private sealed record StageTiming(
        string Stage,
        DateTimeOffset AcquiredAt,
        DateTimeOffset ProcessedAt,
        string? ProcessingMode = null,
        string? WeightPolicyVersion = null,
        int? ReferenceEpoch = null);

    private static H5Group CreateRunGroup(
        Guid experimentRunId,
        int blockNumber,
        long sourceStartSampleIndex,
        long sourceEndSampleIndex,
        DateTimeOffset acquiredAt,
        DateTimeOffset processedAt,
        string stage)
    {
        return new H5Group
        {
            ["experiment_run_id"] = experimentRunId.ToString("D"),
            ["block_number"] = blockNumber,
            ["source_start_sample_index"] = sourceStartSampleIndex,
            ["source_end_sample_index"] = sourceEndSampleIndex,
            ["acquired_at_utc"] = acquiredAt.ToUniversalTime().ToString("O"),
            ["processed_at_utc"] = processedAt.ToUniversalTime().ToString("O"),
            ["stage"] = stage
        };
    }

    private static void AddOptional(H5Group group, string name, double[]? values)
    {
        if (values is { Length: > 0 })
        {
            group[name] = values;
        }
    }

    private static double[,] PackRows(IReadOnlyList<double[]> rows)
    {
        var width = rows[0].Length;
        if (rows.Any(row => row.Length != width))
        {
            throw new InvalidDataException("Reference candidate vectors have inconsistent lengths.");
        }

        var packed = new double[rows.Count, width];
        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < width; column++)
            {
                packed[row, column] = rows[row][column];
            }
        }

        return packed;
    }

    private static void WriteAtomicIfMissing(string filePath, H5File file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (File.Exists(fullPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.partial";
        try
        {
            file.Write(temporaryPath);
            try
            {
                AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                // Idempotent retry: another writer already committed the same deterministic artifact.
            }
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }
}

public sealed record DerivedDemodulatedBlockData(
    Guid ExperimentRunId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ProcessedAt,
    RealtimeDemodulatedBlock Block);

public sealed record DerivedReconstructionData(
    Guid ExperimentRunId,
    int BlockNumber,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ProcessedAt,
    double[] Conductivity,
    double[]? RawConductivity,
    double? ImageQualityScore,
    double? WeightedSystemConditionNumber,
    string ProcessingMode = "realtime",
    int? ReferenceEpoch = null,
    string WeightPolicyVersion = "runtime-recorded",
    double[]? ReferenceVoltage208 = null,
    double[]? TargetVoltage208 = null,
    double[]? MeasurementWeight208 = null,
    string? DynamicKalmanSessionId = null,
    string? DynamicKalmanAction = null,
    double? DynamicKalmanNisPerDof = null,
    double? DynamicKalmanGainMean = null,
    double? DynamicKalmanVarianceInflation = null,
    int? DynamicKalmanUpdateCount = null,
    int? DynamicKalmanTotalLatencyFrames = null,
    string? DynamicKalmanMode = null,
    bool? DynamicKalmanFallback = null,
    double? DynamicKalmanSolveMilliseconds = null,
    double? ReconstructionBackendElapsedMilliseconds = null,
    string? MeshFingerprint = null,
    string? MeshArtifactPath = null,
    string? MeshIndexSchema = null,
    string? ParameterEntity = null,
    string? LogicalMeshFingerprint = null,
    string? OrderedIndexFingerprint = null);

public sealed record DerivedMeshData(
    Guid ExperimentRunId,
    DateTimeOffset CreatedAt,
    double[,] NodeCoords,
    int[,] CellConnectivity,
    string? Fingerprint = null,
    string? MeshIndexSchema = null,
    string? ParameterEntity = null,
    string? LogicalMeshFingerprint = null,
    string? OrderedIndexFingerprint = null);

public sealed record DerivedFrameDiagnosticsData(
    Guid ExperimentRunId,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ProcessedAt,
    ImagingFrameRecord Frame,
    bool PersistReplayDemodOverride);

public sealed record DerivedReferenceCandidateBlockData(
    Guid ExperimentRunId,
    int BlockNumber,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    DateTimeOffset AcquiredAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ImagingReferenceCandidateRecord> Candidates);

public sealed record DerivedFrameDiagnosticsMetadata(
    string WeightPolicyVersion,
    double? ImageQualityScore,
    string[]? ElectrodeStates,
    string[]? FaultTypes,
    string[]? UpgradeGateReasons,
    string? ContactSummary,
    string? CandidateDiagnosticJson,
    string? DisplayCompensationPolicy,
    bool DisplayCompensationOnly,
    string? DisplayCompensationPayloadJson,
    bool ReferenceInvalidated,
    string? ReferenceStatus,
    int? ReferenceEpoch,
    double? BaselineCommonScale,
    double? BaselineShapeResidualRelative,
    double? BaselineComplexScaleMagnitude,
    double? BaselineComplexPhaseDegrees,
    double? BaselineComplexShapeResidualRelative,
    double? BaselineCommonModeEnergyFraction,
    double? BaselineNearDriveScale,
    double? BaselineRemoteScale,
    string? BaselineClassification,
    double? BaselineGlobalNoiseScore,
    double? BaselineGlobalNoiseThreshold,
    bool? BaselineDemodStateChanged,
    double? DemodEstimatedWindowSamples,
    int? DemodUniformOffsetSamples,
    int? DemodRotationStartChannel,
    int? DemodRotationDirection,
    bool CommonScaleNormalized,
    string CommonScaleNormalizationPolicy,
    double? CommonScaleNormalizationFactor)
{
    public static DerivedFrameDiagnosticsMetadata From(ImagingFrameRecord frame) => new(
        frame.WeightPolicyVersion,
        frame.ImageQualityScore,
        frame.ElectrodeStates,
        frame.FaultTypes,
        frame.UpgradeGateReasons,
        frame.ContactSummary,
        frame.CandidateDiagnosticJson,
        frame.DisplayCompensationPolicy,
        frame.DisplayCompensationOnly,
        frame.DisplayCompensationPayloadJson,
        frame.ReferenceInvalidated,
        frame.ReferenceStatus,
        frame.ReferenceEpoch,
        frame.BaselineCommonScale,
        frame.BaselineShapeResidualRelative,
        frame.BaselineComplexScaleMagnitude,
        frame.BaselineComplexPhaseDegrees,
        frame.BaselineComplexShapeResidualRelative,
        frame.BaselineCommonModeEnergyFraction,
        frame.BaselineNearDriveScale,
        frame.BaselineRemoteScale,
        frame.BaselineClassification,
        frame.BaselineGlobalNoiseScore,
        frame.BaselineGlobalNoiseThreshold,
        frame.BaselineDemodStateChanged,
        frame.DemodEstimatedWindowSamples,
        frame.DemodUniformOffsetSamples,
        frame.DemodRotationStartChannel,
        frame.DemodRotationDirection,
        frame.CommonScaleNormalized,
        frame.CommonScaleNormalizationPolicy,
        frame.CommonScaleNormalizationFactor);
}

public sealed record DerivedReferenceEpochMetadata(
    int RetainedFrameCount,
    int RejectedFrameCount,
    double? NoiseGlobalThreshold,
    double DemodEstimatedWindowSamples,
    int DemodUniformOffsetSamples,
    int DemodRotationStartChannel,
    int DemodRotationDirection,
    double FrequencyHz,
    double DacGain,
    int PgaGain,
    string LockKind,
    bool CommonScaleNormalized,
    string CommonScaleNormalizationPolicy,
    double MedianInputCommonScale,
    string ReferenceScalePolicy,
    string[]? SourceCandidateIds,
    DateTimeOffset? SelectedWindowStartedAt,
    DateTimeOffset? SelectedWindowEndedAt,
    DateTimeOffset? EffectiveReferenceAt,
    double? SelectedWindowDriftPerMinute,
    int SelectedWindowGapCount,
    int SelectedWindowSaturationCount,
    string? SelectedWindowContactEvidence,
    string NoiseEstimationPolicy,
    string? ActionGroupId,
    DateTimeOffset? CommonActionAt,
    double? WindowSkewMilliseconds,
    double? SwitchSkewMilliseconds,
    int SynchronizedSetCount)
{
    public static DerivedReferenceEpochMetadata From(ImagingReferenceEpochRecord epoch) => new(
        epoch.RetainedFrameCount,
        epoch.RejectedFrameCount,
        epoch.NoiseGlobalThreshold,
        epoch.DemodEstimatedWindowSamples,
        epoch.DemodUniformOffsetSamples,
        epoch.DemodRotationStartChannel,
        epoch.DemodRotationDirection,
        epoch.FrequencyHz,
        epoch.DacGain,
        epoch.PgaGain,
        epoch.LockKind,
        epoch.CommonScaleNormalized,
        epoch.CommonScaleNormalizationPolicy,
        epoch.MedianInputCommonScale,
        epoch.ReferenceScalePolicy,
        epoch.SourceCandidateIds,
        epoch.SelectedWindowStartedAt,
        epoch.SelectedWindowEndedAt,
        epoch.EffectiveReferenceAt,
        epoch.SelectedWindowDriftPerMinute,
        epoch.SelectedWindowGapCount,
        epoch.SelectedWindowSaturationCount,
        epoch.SelectedWindowContactEvidence,
        epoch.NoiseEstimationPolicy,
        epoch.ActionGroupId,
        epoch.CommonActionAt,
        epoch.WindowSkewMilliseconds,
        epoch.SwitchSkewMilliseconds,
        epoch.SynchronizedSetCount);
}

public sealed record DerivedReferenceCandidateMetadata(
    long Sequence,
    string SourceId,
    DateTimeOffset CapturedAt,
    int FrameNumber,
    long StartSampleIndex,
    long EndSampleIndex,
    string Fingerprint,
    int GapBeforeSamples,
    int SaturationCount,
    string ContactEvidence)
{
    public static DerivedReferenceCandidateMetadata From(ImagingReferenceCandidateRecord candidate) => new(
        candidate.Sequence,
        candidate.SourceId,
        candidate.CapturedAt,
        candidate.FrameNumber,
        candidate.StartSampleIndex,
        candidate.EndSampleIndex,
        candidate.Fingerprint,
        candidate.GapBeforeSamples,
        candidate.SaturationCount,
        candidate.ContactEvidence);
}

public sealed record DerivedReconstructionMetadata(
    string ProcessingMode,
    int? ReferenceEpoch,
    string WeightPolicyVersion,
    string? DynamicKalmanSessionId,
    string? DynamicKalmanAction,
    double? DynamicKalmanNisPerDof,
    double? DynamicKalmanGainMean,
    double? DynamicKalmanVarianceInflation,
    int? DynamicKalmanUpdateCount,
    int? DynamicKalmanTotalLatencyFrames,
    string? DynamicKalmanMode,
    bool? DynamicKalmanFallback,
    double? DynamicKalmanSolveMilliseconds,
    double? ReconstructionBackendElapsedMilliseconds,
    string? MeshFingerprint = null,
    string? MeshArtifactPath = null,
    string? MeshIndexSchema = null,
    string? ParameterEntity = null,
    string? LogicalMeshFingerprint = null,
    string? OrderedIndexFingerprint = null)
{
    public static DerivedReconstructionMetadata From(DerivedReconstructionData data) => new(
        data.ProcessingMode,
        data.ReferenceEpoch,
        data.WeightPolicyVersion,
        data.DynamicKalmanSessionId,
        data.DynamicKalmanAction,
        data.DynamicKalmanNisPerDof,
        data.DynamicKalmanGainMean,
        data.DynamicKalmanVarianceInflation,
        data.DynamicKalmanUpdateCount,
        data.DynamicKalmanTotalLatencyFrames,
        data.DynamicKalmanMode,
        data.DynamicKalmanFallback,
        data.DynamicKalmanSolveMilliseconds,
        data.ReconstructionBackendElapsedMilliseconds,
        data.MeshFingerprint,
        data.MeshArtifactPath,
        data.MeshIndexSchema,
        data.ParameterEntity,
        data.LogicalMeshFingerprint,
        data.OrderedIndexFingerprint);
}
