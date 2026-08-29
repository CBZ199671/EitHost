using System.Text.Encodings.Web;
using System.Text.Json;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

internal static class RealtimeContactDiagnosticSerializer
{
    internal static string? SerializeTemplateDisplayPackage(EcdCwrWaveformTemplateDisplayPackage? package)
    {
        if (package is null || package.Windows.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(package, JsonOptions);
    }

    internal static string? SerializeCandidateDiagnostic(ElectrodeContactDiagnosticResult? result)
    {
        return SerializeCandidateDiagnosticCore(result, null, null, null);
    }

    internal static string? SerializeCandidateDiagnosticWithAdaptiveTrace(
        ElectrodeContactDiagnosticResult? result,
        RealtimeRunState state)
    {
        return SerializeCandidateDiagnosticCore(
            result,
            state.ContactOperatingFingerprint,
            state.AdaptiveContactProfileMatch,
            state.LatestAdaptiveShadowContactResult);
    }

    internal static string? SerializeCandidateDiagnosticCore(
        ElectrodeContactDiagnosticResult? result,
        EcdCwrOperatingFingerprint? operatingFingerprint,
        EcdCwrAdaptiveContactProfileMatch? profileMatch,
        ElectrodeContactDiagnosticResult? adaptiveShadowResult)
    {
        if (result is null)
        {
            return null;
        }

        var supplemental = result.SupplementalEvidence;
        var runtime = result.RuntimeEvidence;
        var dictionary = result.FaultDictionaryTrace;
        var contactSubspace = result.ContactSubspaceEvidence;
        var multiFaultConsensus = result.MultiFaultConsensus;
        var preReferenceConsensus = result.PreReferenceConsensus;
        var guardedAdaptiveActive = profileMatch?.Calibrated == true &&
            adaptiveShadowResult is not null &&
            ReferenceEquals(result, adaptiveShadowResult);
        return JsonSerializer.Serialize(new
        {
            schema_version = "ecd-cwr-candidate-diagnostic-v2",
            diagnostic_policy_version = EcdCwrDiagnosticPolicy.CurrentVersion,
            adaptive_threshold = operatingFingerprint is null
                ? null
                : new
                {
                    operating_fingerprint = new
                    {
                        device_label = operatingFingerprint.DeviceLabel,
                        firmware_version = operatingFingerprint.FirmwareVersion,
                        firmware_build_id = operatingFingerprint.FirmwareBuildId,
                        excitation_frequency_hz = operatingFingerprint.ExcitationFrequencyHz,
                        dac_gain = operatingFingerprint.DacGain,
                        dac_phase_degrees = operatingFingerprint.DacPhaseDegrees,
                        pga_gain = operatingFingerprint.PgaGain,
                        sample_rate_hz = operatingFingerprint.SampleRateHz,
                        channel_cycles = operatingFingerprint.ChannelCycles,
                        discard_leading_cycles = operatingFingerprint.DiscardLeadingCycles,
                        discard_trailing_cycles = operatingFingerprint.DiscardTrailingCycles,
                        subject_profile = operatingFingerprint.SubjectProfile,
                        algorithm_version = operatingFingerprint.AlgorithmVersion,
                        fingerprint_id = operatingFingerprint.FingerprintId
                    },
                    profile_id = profileMatch?.Profile?.ProfileId,
                    profile_schema = profileMatch?.Profile?.SchemaVersion,
                    match_mode = profileMatch?.Mode.ToString(),
                    match_reason = profileMatch?.Reason,
                    effective_thresholds = profileMatch?.Profile is { } matchedProfile
                        ? new
                        {
                            yellow_entry = matchedProfile.Thresholds.YellowEntry,
                            red_entry = matchedProfile.Thresholds.RedEntry,
                            red_release = matchedProfile.Thresholds.RedRelease,
                            direct_a_confirmation = matchedProfile.Thresholds.DirectAConfirmation,
                            drive_pair_active_median = matchedProfile.Thresholds.DrivePairActiveMedian,
                            severe_unilateral_confirmation = matchedProfile.Thresholds
                                .CalculateSevereUnilateralConfirmationMinimumScore()
                        }
                        : null,
                    adaptation_state = guardedAdaptiveActive
                        ? "guarded-active"
                        : profileMatch?.Calibrated == true
                            ? "guarded-ready"
                            : "frozen",
                    adaptation_reason = profileMatch?.Calibrated == true
                        ? guardedAdaptiveActive
                            ? "exact profile persistent Red corroborated by legacy non-Green safety evidence; online baseline update disabled"
                            : "exact profile waiting for persistent Red plus legacy non-Green safety corroboration; online baseline update disabled"
                        : profileMatch?.Reason ?? "no operating fingerprint match",
                    decision_source = guardedAdaptiveActive ? "adaptive-guarded" : "legacy",
                    shadow_states = adaptiveShadowResult?.States.Select(state => state.ToString()).ToArray(),
                    shadow_scores = adaptiveShadowResult?.Scores,
                    shadow_candidates = adaptiveShadowResult?.PreReferenceConsensus?.Candidates
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    shadow_confirmed = adaptiveShadowResult?.PreReferenceConsensus?.Confirmed
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    shadow_safety_mask = (adaptiveShadowResult?.PreReferenceConsensus?.SafetyMask ??
                            adaptiveShadowResult?.PreReferenceConsensus?.Confirmed)
                        ?.Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray()
                },
            pre_reference_only = result.PreReferenceOnly,
            pre_reference_consensus = preReferenceConsensus is null
                ? null
                : new
                {
                    candidate_electrodes = preReferenceConsensus.Candidates
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    confirmed_electrodes = preReferenceConsensus.Confirmed
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    safety_mask_electrodes = (preReferenceConsensus.SafetyMask ?? preReferenceConsensus.Confirmed)
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    stable_update_count = preReferenceConsensus.StableUpdateCount,
                    topology_supported_candidate_count = preReferenceConsensus.TopologySupportedCandidateCount,
                    topology_support_fraction = preReferenceConsensus.TopologySupportFraction,
                    strict_accepted_frame_count = preReferenceConsensus.StrictAcceptedFrameCount,
                    system_level_triggered = preReferenceConsensus.SystemLevelTriggered,
                    status = preReferenceConsensus.Status
                },
            direct_evidence_a_scores = result.DirectEvidenceAScores,
            multi_fault_consensus = multiFaultConsensus is null
                ? null
                : new
                {
                    candidate_electrodes = multiFaultConsensus.Candidates
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    confirmed_electrodes = multiFaultConsensus.Confirmed
                        .Select((selected, electrode) => (selected, electrode))
                        .Where(item => item.selected)
                        .Select(item => item.electrode + 1)
                        .ToArray(),
                    confirmation_levels = multiFaultConsensus.ConfirmationLevels,
                    background_maximum = multiFaultConsensus.BackgroundMaximum,
                    weakest_candidate_score = multiFaultConsensus.WeakestCandidateScore,
                    topology_supported_candidate_count = multiFaultConsensus.TopologySupportedCandidateCount,
                    topology_support_fraction = multiFaultConsensus.TopologySupportFraction,
                    system_level_triggered = multiFaultConsensus.SystemLevelTriggered,
                    status = multiFaultConsensus.Status
                },
            candidate_scores = result.CandidateScores,
            candidate_fault_types = result.CandidateFaultTypes?.Select(type => type.ToString()).ToArray(),
            candidate_evidence_kinds = result.CandidateEvidenceKinds?.Select(kind => kind.ToString()).ToArray(),
            candidate_reasons = result.CandidateReasons,
            supplemental_evidence = supplemental is null
                ? null
                : new
                {
                    evidence_b_available = supplemental.EvidenceBAvailable,
                    evidence_c_available = supplemental.EvidenceCAvailable,
                    reciprocity_dynamic_too_fast = supplemental.ReciprocityDynamicTooFast,
                    reciprocity_violation_count = supplemental.ReciprocityViolationCount,
                    reciprocity_max_whitened_score = supplemental.ReciprocityMaxWhitenedScore,
                    shape_max_score = supplemental.ShapeMaxScore,
                    reciprocity_status = supplemental.ReciprocityStatus,
                    shape_status = supplemental.ShapeStatus
                },
            runtime_evidence = runtime is null
                ? null
                : new
                {
                    evidence_d_available = runtime.EvidenceDAvailable,
                    evidence_d_soft_violation_count = runtime.EvidenceDSoftViolationCount,
                    evidence_d_hard_fault_count = runtime.EvidenceDHardFaultCount,
                    evidence_d_max_score = runtime.EvidenceDMaxScore,
                    raw_global_sentinel_triggered = runtime.RawGlobalSentinelTriggered,
                    raw_contact48_median_z = runtime.RawContact48MedianZ,
                    raw_drive_median_z = runtime.RawDriveMedianZ,
                    saturation_ratio = runtime.SaturationRatio,
                    system_sentinel_reason = runtime.SystemSentinelReason,
                    fault_dictionary_policy_version = runtime.FaultDictionaryPolicyVersion
                },
            fault_dictionary = dictionary is null
                ? null
                : new
                {
                    policy_version = dictionary.PolicyVersion,
                    drive_scores = dictionary.DriveScores,
                    measure_scores = dictionary.MeasureScores,
                    pair_link_scores = dictionary.PairLinkScores,
                    measurement_channel_scores = dictionary.MeasurementChannelScores,
                    residual_rms = dictionary.ResidualRms,
                    observation_count = dictionary.ObservationCount,
                    active_coefficient_count = dictionary.ActiveCoefficientCount()
                },
            contact_subspace_evidence = contactSubspace is null
                ? null
                : new
                {
                    evidence_f_available = contactSubspace.EvidenceFAvailable,
                    candidate_applied = contactSubspace.CandidateApplied,
                    status = contactSubspace.Status,
                    source = contactSubspace.Source,
                    measurement_space = contactSubspace.MeasurementSpace,
                    contact_subspace_score = contactSubspace.ContactSubspaceScore,
                    projected_norm = contactSubspace.ProjectedNorm,
                    residual_norm = contactSubspace.ResidualNorm,
                    contact_coefficients = contactSubspace.ContactCoefficients
                }
        }, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}
