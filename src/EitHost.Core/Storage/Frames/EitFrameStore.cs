using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;
using Microsoft.Data.Sqlite;

namespace EitHost.Core.Storage.Frames;

/// <summary>
/// SQLite-backed realtime imaging frame store (`eit_frames.sqlite`).
/// WAL journal + NORMAL synchronous so batched transactions append sequentially
/// instead of forcing one fsync per realtime block.
/// </summary>
public sealed class EitFrameStore
{
    public const int BoundaryVectorLength = 208;

    private readonly string databasePath;
    private readonly string writeConnectionString;
    private readonly string readConnectionString;

    public EitFrameStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        writeConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath => databasePath;

    public void Initialize()
    {
        SQLitePCL.Batteries_V2.Init();
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenWriteConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS imaging_runs (
                imaging_run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                set_label TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                reconstruction_route TEXT NOT NULL,
                difference_lambda REAL NOT NULL,
                custom_lambda INTEGER NOT NULL,
                mesh_size REAL NOT NULL,
                frequency_hz REAL NOT NULL,
                requested_frequency_hz REAL NULL,
                actual_frequency_hz REAL NULL,
                dds_frequency_tuning_word INTEGER NULL,
                requested_dwell_us REAL NULL,
                effective_dwell_us REAL NULL,
                channel_cycles REAL NOT NULL,
                sample_rate_hz REAL NOT NULL,
                ad_range_code INTEGER NULL,
                adc_full_span_volts REAL NULL,
                adc_lsb_volts REAL NULL,
                difference_orientation TEXT NOT NULL,
                storage_mode TEXT NOT NULL DEFAULT 'legacy',
                reconstruction_scale_status TEXT NOT NULL DEFAULT 'model_relative',
                reconstruction_scale_provenance TEXT NOT NULL DEFAULT 'legacy-unlabeled-model-relative',
                reference_scale_policy TEXT NOT NULL DEFAULT 'legacy_unspecified',
                contact_operating_fingerprint_json TEXT NULL,
                contact_threshold_profile_id TEXT NULL,
                contact_threshold_mode TEXT NOT NULL DEFAULT 'uncalibrated-legacy',
                reference_block_number INTEGER NULL,
                reference_208 BLOB NULL,
                node_coords BLOB NULL,
                node_coord_rows INTEGER NOT NULL DEFAULT 0,
                node_coord_cols INTEGER NOT NULL DEFAULT 0,
                cell_connectivity BLOB NULL,
                cell_rows INTEGER NOT NULL DEFAULT 0,
                cell_cols INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS imaging_frames (
                imaging_run_id TEXT NOT NULL,
                block_number INTEGER NOT NULL,
                captured_at_utc TEXT NOT NULL,
                quality_weight REAL NOT NULL,
                accepted_frames INTEGER NOT NULL,
                rejected_frames INTEGER NOT NULL,
                mean_amplitude_208 BLOB NOT NULL,
                mean_real_208 BLOB NOT NULL,
                mean_imag_208 BLOB NOT NULL,
                mean_full_amplitude_256 BLOB NULL,
                mean_full_real_256 BLOB NULL,
                mean_full_imag_256 BLOB NULL,
                measurement_weight_208 BLOB NULL,
                weight_policy_version TEXT NOT NULL DEFAULT 'all-one-v1',
                image_quality_score REAL NULL,
                reconstruction_condition_number REAL NULL,
                electrode_scores BLOB NULL,
                fault_confidence BLOB NULL,
                electrode_states TEXT NULL,
                fault_types TEXT NULL,
                upgrade_gate_reasons TEXT NULL,
                contact_summary TEXT NULL,
                candidate_diagnostic_json TEXT NULL,
                display_compensation_policy TEXT NULL,
                display_compensation_only INTEGER NOT NULL DEFAULT 0,
                display_compensation_payload_json TEXT NULL,
                reference_invalidated INTEGER NOT NULL DEFAULT 0,
                reference_status TEXT NULL,
                common_scale_normalized INTEGER NOT NULL DEFAULT 0,
                common_scale_normalization_policy TEXT NOT NULL DEFAULT 'none',
                common_scale_normalization_factor REAL NULL,
                conductivity BLOB NULL,
                conductivity_raw BLOB NULL,
                dynamic_kalman_session_id TEXT NULL,
                dynamic_kalman_action TEXT NULL,
                dynamic_kalman_nis_per_dof REAL NULL,
                dynamic_kalman_gain_mean REAL NULL,
                dynamic_kalman_variance_inflation REAL NULL,
                dynamic_kalman_update_count INTEGER NULL,
                dynamic_kalman_total_latency_frames INTEGER NULL,
                dynamic_kalman_mode TEXT NULL,
                dynamic_kalman_fallback INTEGER NULL,
                dynamic_kalman_solve_ms REAL NULL,
                reconstruction_backend_elapsed_ms REAL NULL,
                reference_epoch INTEGER NULL,
                baseline_common_scale REAL NULL,
                baseline_shape_residual_relative REAL NULL,
                baseline_complex_scale_magnitude REAL NULL,
                baseline_complex_phase_degrees REAL NULL,
                baseline_complex_shape_residual_relative REAL NULL,
                baseline_common_mode_energy_fraction REAL NULL,
                baseline_near_drive_scale REAL NULL,
                baseline_remote_scale REAL NULL,
                baseline_classification TEXT NULL,
                baseline_global_noise_score REAL NULL,
                baseline_global_noise_threshold REAL NULL,
                baseline_demod_state_changed INTEGER NULL,
                demod_estimated_window_samples REAL NULL,
                demod_uniform_offset_samples INTEGER NULL,
                demod_rotation_start_channel INTEGER NULL,
                demod_rotation_direction INTEGER NULL,
                PRIMARY KEY (imaging_run_id, block_number)
            );
            CREATE TABLE IF NOT EXISTS imaging_reference_epochs (
                imaging_run_id TEXT NOT NULL,
                reference_epoch INTEGER NOT NULL,
                locked_block_number INTEGER NOT NULL,
                locked_at_utc TEXT NOT NULL,
                retained_frame_count INTEGER NOT NULL,
                rejected_frame_count INTEGER NOT NULL,
                reference_amplitude_208 BLOB NOT NULL,
                reference_full_real_256 BLOB NOT NULL,
                reference_full_imag_256 BLOB NOT NULL,
                noise_global_threshold REAL NULL,
                demod_estimated_window_samples REAL NOT NULL,
                demod_uniform_offset_samples INTEGER NOT NULL,
                demod_rotation_start_channel INTEGER NOT NULL,
                demod_rotation_direction INTEGER NOT NULL,
                frequency_hz REAL NOT NULL,
                dac_gain REAL NOT NULL,
                pga_gain INTEGER NOT NULL,
                lock_kind TEXT NOT NULL,
                common_scale_normalized INTEGER NOT NULL DEFAULT 0,
                common_scale_normalization_policy TEXT NOT NULL DEFAULT 'none',
                median_input_common_scale REAL NOT NULL DEFAULT 1.0,
                reference_scale_policy TEXT NOT NULL DEFAULT 'legacy_unspecified',
                source_candidate_ids_json TEXT NULL,
                selected_window_started_at_utc TEXT NULL,
                selected_window_ended_at_utc TEXT NULL,
                effective_reference_at_utc TEXT NULL,
                selected_window_drift_per_minute REAL NULL,
                selected_window_gap_count INTEGER NOT NULL DEFAULT 0,
                selected_window_saturation_count INTEGER NOT NULL DEFAULT 0,
                selected_window_contact_evidence TEXT NULL,
                noise_estimation_policy TEXT NOT NULL DEFAULT 'raw_reference_dispersion-v1',
                action_group_id TEXT NULL,
                common_action_at_utc TEXT NULL,
                window_skew_ms REAL NULL,
                switch_skew_ms REAL NULL,
                synchronized_set_count INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (imaging_run_id, reference_epoch)
            );
            CREATE TABLE IF NOT EXISTS imaging_reference_candidates (
                imaging_run_id TEXT NOT NULL,
                candidate_sequence INTEGER NOT NULL,
                source_id TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                block_number INTEGER NOT NULL,
                frame_number INTEGER NOT NULL,
                start_sample_index INTEGER NOT NULL,
                end_sample_index INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                gap_before_samples INTEGER NOT NULL,
                saturation_count INTEGER NOT NULL,
                contact_evidence TEXT NOT NULL,
                voltage_208 BLOB NOT NULL,
                full_real_256 BLOB NOT NULL,
                full_imag_256 BLOB NOT NULL,
                PRIMARY KEY (imaging_run_id, candidate_sequence),
                UNIQUE (imaging_run_id, source_id)
            );
            CREATE TABLE IF NOT EXISTS imaging_run_raw_links (
                imaging_run_id TEXT NOT NULL,
                raw_run_id TEXT NOT NULL,
                raw_hdf5_path TEXT NOT NULL,
                linked_at_utc TEXT NOT NULL,
                PRIMARY KEY (imaging_run_id, raw_run_id)
            );
            CREATE INDEX IF NOT EXISTS idx_imaging_runs_started
                ON imaging_runs(started_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_reference_epochs_run_block
                ON imaging_reference_epochs(imaging_run_id, locked_block_number);
            CREATE INDEX IF NOT EXISTS idx_reference_candidates_run_time
                ON imaging_reference_candidates(imaging_run_id, captured_at_utc);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, "imaging_runs", "storage_mode", "TEXT NOT NULL DEFAULT 'legacy'");
        AddColumnIfMissing(
            connection,
            "imaging_runs",
            "reconstruction_scale_status",
            "TEXT NOT NULL DEFAULT 'model_relative'");
        AddColumnIfMissing(
            connection,
            "imaging_runs",
            "reconstruction_scale_provenance",
            "TEXT NOT NULL DEFAULT 'legacy-unlabeled-model-relative'");
        AddColumnIfMissing(
            connection,
            "imaging_runs",
            "reference_scale_policy",
            "TEXT NOT NULL DEFAULT 'legacy_unspecified'");
        AddColumnIfMissing(connection, "imaging_runs", "contact_operating_fingerprint_json", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_runs", "contact_threshold_profile_id", "TEXT NULL");
        AddColumnIfMissing(
            connection,
            "imaging_runs",
            "contact_threshold_mode",
            "TEXT NOT NULL DEFAULT 'uncalibrated-legacy'");
        AddColumnIfMissing(connection, "imaging_runs", "requested_frequency_hz", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_runs", "actual_frequency_hz", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_runs", "dds_frequency_tuning_word", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_runs", "requested_dwell_us", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_runs", "effective_dwell_us", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_runs", "ad_range_code", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_runs", "adc_full_span_volts", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_runs", "adc_lsb_volts", "REAL NULL");
        EnsureImagingFrameDiagnosticColumns(connection);
        AddColumnIfMissing(connection, "imaging_reference_epochs", "common_scale_normalized", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "common_scale_normalization_policy", "TEXT NOT NULL DEFAULT 'none'");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "median_input_common_scale", "REAL NOT NULL DEFAULT 1.0");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "reference_scale_policy", "TEXT NOT NULL DEFAULT 'legacy_unspecified'");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "source_candidate_ids_json", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_started_at_utc", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_ended_at_utc", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "effective_reference_at_utc", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_drift_per_minute", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_gap_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_saturation_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "selected_window_contact_evidence", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "noise_estimation_policy", "TEXT NOT NULL DEFAULT 'raw_reference_dispersion-v1'");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "action_group_id", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "common_action_at_utc", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "window_skew_ms", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "switch_skew_ms", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_reference_epochs", "synchronized_set_count", "INTEGER NOT NULL DEFAULT 1");
    }

    public SqliteConnection OpenWriteConnection()
    {
        var connection = new SqliteConnection(writeConnectionString);
        connection.Open();
        ConfigureWriteConnection(connection);
        return connection;
    }

    private static void ConfigureWriteConnection(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            """;
        command.ExecuteNonQuery();
    }

    public void BeginRun(SqliteConnection connection, ImagingRunConfigRecord run)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(run);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO imaging_runs(
                imaging_run_id, session_id, set_label, started_at_utc, reconstruction_route,
                difference_lambda, custom_lambda, mesh_size, frequency_hz,
                requested_frequency_hz, actual_frequency_hz, dds_frequency_tuning_word,
                requested_dwell_us, effective_dwell_us, channel_cycles,
                sample_rate_hz, ad_range_code, adc_full_span_volts, adc_lsb_volts,
                difference_orientation, storage_mode,
                reconstruction_scale_status, reconstruction_scale_provenance,
                reference_scale_policy,
                contact_operating_fingerprint_json, contact_threshold_profile_id, contact_threshold_mode)
            VALUES ($id, $session, $label, $started, $route, $lambda, $custom, $mesh, $freq,
                $requested_frequency, $actual_frequency, $ftw, $requested_dwell, $effective_dwell,
                $cycles, $rate, $ad_range, $adc_span, $adc_lsb, $orientation, $storage_mode,
                $scale_status, $scale_provenance, $reference_scale_policy,
                $contact_fingerprint, $contact_profile, $contact_mode);
            """;
        command.Parameters.AddWithValue("$id", run.ImagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$session", run.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$label", run.SetLabel);
        command.Parameters.AddWithValue("$started", FormatTimestamp(run.StartedAt));
        command.Parameters.AddWithValue("$route", run.ReconstructionRoute);
        command.Parameters.AddWithValue("$lambda", run.DifferenceLambda);
        command.Parameters.AddWithValue("$custom", run.CustomLambdaEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$mesh", run.MeshSize);
        command.Parameters.AddWithValue("$freq", run.FrequencyHz);
        command.Parameters.AddWithValue("$requested_frequency", (object?)run.RequestedFrequencyHz ?? DBNull.Value);
        command.Parameters.AddWithValue("$actual_frequency", (object?)run.ActualFrequencyHz ?? DBNull.Value);
        command.Parameters.AddWithValue("$ftw", (object?)run.DdsFrequencyTuningWord ?? DBNull.Value);
        command.Parameters.AddWithValue("$requested_dwell", (object?)run.RequestedDwellUs ?? DBNull.Value);
        command.Parameters.AddWithValue("$effective_dwell", (object?)run.EffectiveDwellUs ?? DBNull.Value);
        command.Parameters.AddWithValue("$cycles", run.ChannelCycles);
        command.Parameters.AddWithValue("$rate", run.SampleRateHz);
        command.Parameters.AddWithValue("$ad_range", (object?)run.AdRangeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$adc_span", (object?)run.AdcFullSpanVolts ?? DBNull.Value);
        command.Parameters.AddWithValue("$adc_lsb", (object?)run.AdcLsbVolts ?? DBNull.Value);
        command.Parameters.AddWithValue("$orientation", run.DifferenceOrientation);
        command.Parameters.AddWithValue(
            "$scale_status",
            ReconstructionScale.NormalizeStatus(run.ReconstructionScaleStatus));
        command.Parameters.AddWithValue(
            "$scale_provenance",
            ReconstructionScale.NormalizeProvenance(
                run.ReconstructionScaleStatus,
                run.ReconstructionScaleProvenance));
        command.Parameters.AddWithValue(
            "$reference_scale_policy",
            EcdCwrReferenceScalePolicy.Normalize(run.ReferenceScalePolicy));
        command.Parameters.AddWithValue("$storage_mode", RealtimeStoragePolicy.Normalize(run.StorageMode));
        command.Parameters.AddWithValue(
            "$contact_fingerprint",
            string.IsNullOrWhiteSpace(run.ContactOperatingFingerprintJson)
                ? DBNull.Value
                : run.ContactOperatingFingerprintJson);
        command.Parameters.AddWithValue(
            "$contact_profile",
            string.IsNullOrWhiteSpace(run.ContactThresholdProfileId)
                ? DBNull.Value
                : run.ContactThresholdProfileId);
        command.Parameters.AddWithValue(
            "$contact_mode",
            string.IsNullOrWhiteSpace(run.ContactThresholdMode)
                ? "uncalibrated-legacy"
                : run.ContactThresholdMode);
        command.ExecuteNonQuery();
    }

    public void AppendFrame(SqliteConnection connection, ImagingFrameRecord frame)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(frame);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO imaging_frames(
                imaging_run_id, block_number, captured_at_utc, quality_weight,
                accepted_frames, rejected_frames, mean_amplitude_208, mean_real_208, mean_imag_208,
                mean_full_amplitude_256, mean_full_real_256, mean_full_imag_256,
                measurement_weight_208, weight_policy_version, image_quality_score, reconstruction_condition_number, electrode_scores,
                fault_confidence, electrode_states, fault_types, upgrade_gate_reasons, contact_summary,
                candidate_diagnostic_json,
                display_compensation_policy, display_compensation_only, display_compensation_payload_json,
                reference_invalidated, reference_status,
                common_scale_normalized, common_scale_normalization_policy,
                common_scale_normalization_factor,
                reference_epoch, baseline_common_scale, baseline_shape_residual_relative,
                baseline_complex_scale_magnitude, baseline_complex_phase_degrees,
                baseline_complex_shape_residual_relative, baseline_common_mode_energy_fraction,
                baseline_near_drive_scale, baseline_remote_scale, baseline_classification,
                baseline_global_noise_score, baseline_global_noise_threshold, baseline_demod_state_changed,
                demod_estimated_window_samples, demod_uniform_offset_samples,
                demod_rotation_start_channel, demod_rotation_direction)
            VALUES ($run, $block, $captured, $quality, $accepted, $rejected, $amp, $real, $imag,
                $full_amp, $full_real, $full_imag,
                $weights, $policy, $image_quality, $condition_number, $scores, $confidence, $states, $faults, $reasons, $summary,
                $candidate_diagnostic_json,
                $display_compensation_policy, $display_compensation_only, $display_compensation_payload_json,
                $reference_invalidated, $reference_status,
                $common_scale_normalized, $common_scale_normalization_policy,
                $common_scale_normalization_factor,
                $reference_epoch, $baseline_common_scale, $baseline_shape_residual_relative,
                $baseline_complex_scale_magnitude, $baseline_complex_phase_degrees,
                $baseline_complex_shape_residual_relative, $baseline_common_mode_energy_fraction,
                $baseline_near_drive_scale, $baseline_remote_scale, $baseline_classification,
                $baseline_global_noise_score, $baseline_global_noise_threshold, $baseline_demod_state_changed,
                $demod_estimated_window_samples, $demod_uniform_offset_samples,
                $demod_rotation_start_channel, $demod_rotation_direction);
            """;
        command.Parameters.AddWithValue("$run", frame.ImagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$block", frame.BlockNumber);
        command.Parameters.AddWithValue("$captured", FormatTimestamp(frame.CapturedAt));
        command.Parameters.AddWithValue("$quality", frame.QualityWeight);
        command.Parameters.AddWithValue("$accepted", frame.AcceptedFrames);
        command.Parameters.AddWithValue("$rejected", frame.RejectedFrames);
        command.Parameters.AddWithValue("$amp", EncodeDoubles(frame.MeanAmplitude208));
        command.Parameters.AddWithValue("$real", EncodeDoubles(frame.MeanReal208));
        command.Parameters.AddWithValue("$imag", EncodeDoubles(frame.MeanImaginary208));
        command.Parameters.AddWithValue("$full_amp", ToDbBlob(frame.MeanFullAmplitude256));
        command.Parameters.AddWithValue("$full_real", ToDbBlob(frame.MeanFullReal256));
        command.Parameters.AddWithValue("$full_imag", ToDbBlob(frame.MeanFullImaginary256));
        command.Parameters.AddWithValue("$weights", EncodeDoubles(frame.MeasurementWeight208 ?? CreateAllOneWeights()));
        command.Parameters.AddWithValue("$policy", string.IsNullOrWhiteSpace(frame.WeightPolicyVersion) ? "all-one-v1" : frame.WeightPolicyVersion);
        command.Parameters.AddWithValue("$image_quality", (object?)frame.ImageQualityScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$condition_number", (object?)frame.ReconstructionConditionNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$scores", ToDbBlob(frame.ElectrodeScores));
        command.Parameters.AddWithValue("$confidence", ToDbBlob(frame.FaultConfidence));
        command.Parameters.AddWithValue("$states", ToDbText(frame.ElectrodeStates));
        command.Parameters.AddWithValue("$faults", ToDbText(frame.FaultTypes));
        command.Parameters.AddWithValue("$reasons", ToDbText(frame.UpgradeGateReasons));
        command.Parameters.AddWithValue("$summary", (object?)frame.ContactSummary ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidate_diagnostic_json", (object?)frame.CandidateDiagnosticJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$display_compensation_policy", (object?)frame.DisplayCompensationPolicy ?? DBNull.Value);
        command.Parameters.AddWithValue("$display_compensation_only", frame.DisplayCompensationOnly ? 1 : 0);
        command.Parameters.AddWithValue("$display_compensation_payload_json", (object?)frame.DisplayCompensationPayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$reference_invalidated", frame.ReferenceInvalidated ? 1 : 0);
        command.Parameters.AddWithValue("$reference_status", (object?)frame.ReferenceStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$common_scale_normalized", frame.CommonScaleNormalized ? 1 : 0);
        command.Parameters.AddWithValue(
            "$common_scale_normalization_policy",
            string.IsNullOrWhiteSpace(frame.CommonScaleNormalizationPolicy)
                ? "none"
                : frame.CommonScaleNormalizationPolicy);
        command.Parameters.AddWithValue(
            "$common_scale_normalization_factor",
            (object?)frame.CommonScaleNormalizationFactor ?? DBNull.Value);
        command.Parameters.AddWithValue("$reference_epoch", (object?)frame.ReferenceEpoch ?? DBNull.Value);
        command.Parameters.AddWithValue("$baseline_common_scale", (object?)frame.BaselineCommonScale ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_shape_residual_relative",
            (object?)frame.BaselineShapeResidualRelative ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_complex_scale_magnitude",
            (object?)frame.BaselineComplexScaleMagnitude ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_complex_phase_degrees",
            (object?)frame.BaselineComplexPhaseDegrees ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_complex_shape_residual_relative",
            (object?)frame.BaselineComplexShapeResidualRelative ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_common_mode_energy_fraction",
            (object?)frame.BaselineCommonModeEnergyFraction ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_near_drive_scale",
            (object?)frame.BaselineNearDriveScale ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_remote_scale",
            (object?)frame.BaselineRemoteScale ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_classification",
            (object?)frame.BaselineClassification ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_global_noise_score",
            (object?)frame.BaselineGlobalNoiseScore ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_global_noise_threshold",
            (object?)frame.BaselineGlobalNoiseThreshold ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$baseline_demod_state_changed",
            frame.BaselineDemodStateChanged is { } demodChanged
                ? (object)(demodChanged ? 1 : 0)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$demod_estimated_window_samples",
            (object?)frame.DemodEstimatedWindowSamples ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$demod_uniform_offset_samples",
            (object?)frame.DemodUniformOffsetSamples ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$demod_rotation_start_channel",
            (object?)frame.DemodRotationStartChannel ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$demod_rotation_direction",
            (object?)frame.DemodRotationDirection ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void AttachConductivity(
        SqliteConnection connection,
        Guid imagingRunId,
        int blockNumber,
        double[] conductivity,
        double? imageQualityScore = null,
        double? reconstructionConditionNumber = null,
        double[]? rawConductivity = null,
        string? dynamicKalmanSessionId = null,
        string? dynamicKalmanAction = null,
        double? dynamicKalmanNisPerDof = null,
        double? dynamicKalmanGainMean = null,
        double? dynamicKalmanVarianceInflation = null,
        int? dynamicKalmanUpdateCount = null,
        int? dynamicKalmanTotalLatencyFrames = null,
        string? dynamicKalmanMode = null,
        bool? dynamicKalmanFallback = null,
        double? dynamicKalmanSolveMilliseconds = null,
        double? reconstructionBackendElapsedMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(conductivity);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE imaging_frames
            SET conductivity = $conductivity,
                conductivity_raw = $conductivity_raw,
                image_quality_score = COALESCE($image_quality, image_quality_score),
                reconstruction_condition_number = COALESCE($condition_number, reconstruction_condition_number),
                dynamic_kalman_session_id = $dynamic_session,
                dynamic_kalman_action = $dynamic_action,
                dynamic_kalman_nis_per_dof = $dynamic_nis,
                dynamic_kalman_gain_mean = $dynamic_gain,
                dynamic_kalman_variance_inflation = $dynamic_inflation,
                dynamic_kalman_update_count = $dynamic_updates,
                dynamic_kalman_total_latency_frames = $dynamic_latency,
                dynamic_kalman_mode = $dynamic_mode,
                dynamic_kalman_fallback = $dynamic_fallback,
                dynamic_kalman_solve_ms = $dynamic_solve_ms,
                reconstruction_backend_elapsed_ms = $backend_elapsed_ms
            WHERE imaging_run_id = $run AND block_number = $block;
            """;
        command.Parameters.AddWithValue("$conductivity", EncodeDoubles(conductivity));
        command.Parameters.AddWithValue("$conductivity_raw", ToDbBlob(rawConductivity));
        command.Parameters.AddWithValue("$image_quality", (object?)imageQualityScore ?? DBNull.Value);
        command.Parameters.AddWithValue("$condition_number", (object?)reconstructionConditionNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_session", (object?)dynamicKalmanSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_action", (object?)dynamicKalmanAction ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_nis", (object?)dynamicKalmanNisPerDof ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_gain", (object?)dynamicKalmanGainMean ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_inflation", (object?)dynamicKalmanVarianceInflation ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_updates", (object?)dynamicKalmanUpdateCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_latency", (object?)dynamicKalmanTotalLatencyFrames ?? DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_mode", (object?)dynamicKalmanMode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$dynamic_fallback",
            dynamicKalmanFallback is { } fallback
                ? (object)(fallback ? 1 : 0)
                : DBNull.Value);
        command.Parameters.AddWithValue("$dynamic_solve_ms", (object?)dynamicKalmanSolveMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$backend_elapsed_ms", (object?)reconstructionBackendElapsedMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$block", blockNumber);
        command.ExecuteNonQuery();
    }

    public void AttachMeasurementWeights(
        SqliteConnection connection,
        Guid imagingRunId,
        int blockNumber,
        IReadOnlyList<double> measurementWeight208,
        string weightPolicyVersion)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(measurementWeight208);
        if (measurementWeight208.Count != 208 ||
            measurementWeight208.Any(weight => !double.IsFinite(weight) || weight < 0.0 || weight > 1.0))
        {
            throw new ArgumentException("Imaging-frame measurement weights must contain 208 finite values in [0, 1].", nameof(measurementWeight208));
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE imaging_frames
            SET measurement_weight_208 = $weights,
                weight_policy_version = $policy
            WHERE imaging_run_id = $run AND block_number = $block;
            """;
        command.Parameters.AddWithValue("$weights", EncodeDoubles(measurementWeight208.ToArray()));
        command.Parameters.AddWithValue("$policy", string.IsNullOrWhiteSpace(weightPolicyVersion) ? "all-one-v1" : weightPolicyVersion);
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$block", blockNumber);
        command.ExecuteNonQuery();
    }

    public void SetReference(SqliteConnection connection, Guid imagingRunId, int blockNumber, double[] reference208)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(reference208);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE imaging_runs SET reference_208 = $reference, reference_block_number = $block
            WHERE imaging_run_id = $run AND reference_208 IS NULL;
            """;
        command.Parameters.AddWithValue("$reference", EncodeDoubles(reference208));
        command.Parameters.AddWithValue("$block", blockNumber);
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public void AppendReferenceEpoch(SqliteConnection connection, ImagingReferenceEpochRecord reference)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(reference);
        ValidateReferenceEpoch(reference);

        // Backward compatibility for replay clients that only understand the
        // original one-reference-per-run columns. Later epochs remain available
        // exclusively from the immutable epoch table.
        SetReference(
            connection,
            reference.ImagingRunId,
            reference.LockedBlockNumber,
            reference.ReferenceAmplitude208);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO imaging_reference_epochs(
                imaging_run_id, reference_epoch, locked_block_number, locked_at_utc,
                retained_frame_count, rejected_frame_count, reference_amplitude_208,
                reference_full_real_256, reference_full_imag_256, noise_global_threshold,
                demod_estimated_window_samples, demod_uniform_offset_samples,
                demod_rotation_start_channel, demod_rotation_direction,
                frequency_hz, dac_gain, pga_gain, lock_kind,
                common_scale_normalized, common_scale_normalization_policy,
                median_input_common_scale, reference_scale_policy, source_candidate_ids_json,
                selected_window_started_at_utc, selected_window_ended_at_utc,
                effective_reference_at_utc, selected_window_drift_per_minute,
                selected_window_gap_count, selected_window_saturation_count,
                selected_window_contact_evidence, noise_estimation_policy,
                action_group_id, common_action_at_utc, window_skew_ms,
                switch_skew_ms, synchronized_set_count)
            VALUES (
                $run, $epoch, $block, $locked,
                $retained, $rejected, $amplitude,
                $real, $imaginary, $noise_threshold,
                $window, $offset, $rotation_start, $rotation_direction,
                $frequency, $dac_gain, $pga_gain, $lock_kind,
                $common_scale_normalized, $common_scale_normalization_policy,
                $median_input_common_scale, $reference_scale_policy, $source_candidate_ids,
                $selected_window_started, $selected_window_ended,
                $effective_reference_at, $selected_window_drift,
                $selected_window_gaps, $selected_window_saturation,
                $selected_window_contact, $noise_estimation_policy,
                $action_group_id, $common_action_at, $window_skew_ms,
                $switch_skew_ms, $synchronized_set_count);
            """;
        command.Parameters.AddWithValue("$run", reference.ImagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$epoch", reference.ReferenceEpoch);
        command.Parameters.AddWithValue("$block", reference.LockedBlockNumber);
        command.Parameters.AddWithValue("$locked", FormatTimestamp(reference.LockedAt));
        command.Parameters.AddWithValue("$retained", reference.RetainedFrameCount);
        command.Parameters.AddWithValue("$rejected", reference.RejectedFrameCount);
        command.Parameters.AddWithValue("$amplitude", EncodeDoubles(reference.ReferenceAmplitude208));
        command.Parameters.AddWithValue("$real", EncodeDoubles(reference.ReferenceFullReal256));
        command.Parameters.AddWithValue("$imaginary", EncodeDoubles(reference.ReferenceFullImaginary256));
        command.Parameters.AddWithValue(
            "$noise_threshold",
            (object?)reference.NoiseGlobalThreshold ?? DBNull.Value);
        command.Parameters.AddWithValue("$window", reference.DemodEstimatedWindowSamples);
        command.Parameters.AddWithValue("$offset", reference.DemodUniformOffsetSamples);
        command.Parameters.AddWithValue("$rotation_start", reference.DemodRotationStartChannel);
        command.Parameters.AddWithValue("$rotation_direction", reference.DemodRotationDirection);
        command.Parameters.AddWithValue("$frequency", reference.FrequencyHz);
        command.Parameters.AddWithValue("$dac_gain", reference.DacGain);
        command.Parameters.AddWithValue("$pga_gain", reference.PgaGain);
        command.Parameters.AddWithValue("$lock_kind", reference.LockKind);
        command.Parameters.AddWithValue("$common_scale_normalized", reference.CommonScaleNormalized ? 1 : 0);
        command.Parameters.AddWithValue(
            "$common_scale_normalization_policy",
            string.IsNullOrWhiteSpace(reference.CommonScaleNormalizationPolicy)
                ? "none"
                : reference.CommonScaleNormalizationPolicy);
        command.Parameters.AddWithValue("$median_input_common_scale", reference.MedianInputCommonScale);
        command.Parameters.AddWithValue(
            "$reference_scale_policy",
            EcdCwrReferenceScalePolicy.Normalize(reference.ReferenceScalePolicy));
        command.Parameters.AddWithValue(
            "$source_candidate_ids",
            reference.SourceCandidateIds is { Length: > 0 }
                ? JsonSerializer.Serialize(reference.SourceCandidateIds)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$selected_window_started",
            reference.SelectedWindowStartedAt is { } started ? FormatTimestamp(started) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$selected_window_ended",
            reference.SelectedWindowEndedAt is { } ended ? FormatTimestamp(ended) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$effective_reference_at",
            reference.EffectiveReferenceAt is { } effective ? FormatTimestamp(effective) : DBNull.Value);
        command.Parameters.AddWithValue(
            "$selected_window_drift",
            (object?)reference.SelectedWindowDriftPerMinute ?? DBNull.Value);
        command.Parameters.AddWithValue("$selected_window_gaps", reference.SelectedWindowGapCount);
        command.Parameters.AddWithValue("$selected_window_saturation", reference.SelectedWindowSaturationCount);
        command.Parameters.AddWithValue(
            "$selected_window_contact",
            (object?)reference.SelectedWindowContactEvidence ?? DBNull.Value);
        command.Parameters.AddWithValue("$noise_estimation_policy", reference.NoiseEstimationPolicy);
        command.Parameters.AddWithValue(
            "$action_group_id",
            (object?)reference.ActionGroupId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$common_action_at",
            reference.CommonActionAt is { } commonActionAt
                ? FormatTimestamp(commonActionAt)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$window_skew_ms",
            (object?)reference.WindowSkewMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$switch_skew_ms",
            (object?)reference.SwitchSkewMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$synchronized_set_count", reference.SynchronizedSetCount);
        command.ExecuteNonQuery();
    }

    public void AppendReferenceCandidate(
        SqliteConnection connection,
        ImagingReferenceCandidateRecord candidate)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateReferenceCandidate(candidate);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO imaging_reference_candidates(
                imaging_run_id, candidate_sequence, source_id, captured_at_utc,
                block_number, frame_number, start_sample_index, end_sample_index,
                fingerprint, gap_before_samples, saturation_count, contact_evidence,
                voltage_208, full_real_256, full_imag_256)
            VALUES (
                $run, $sequence, $source, $captured,
                $block, $frame, $start_sample, $end_sample,
                $fingerprint, $gap, $saturation, $contact,
                $voltage, $real, $imaginary);
            """;
        command.Parameters.AddWithValue("$run", candidate.ImagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", candidate.Sequence);
        command.Parameters.AddWithValue("$source", candidate.SourceId);
        command.Parameters.AddWithValue("$captured", FormatTimestamp(candidate.CapturedAt));
        command.Parameters.AddWithValue("$block", candidate.BlockNumber);
        command.Parameters.AddWithValue("$frame", candidate.FrameNumber);
        command.Parameters.AddWithValue("$start_sample", candidate.StartSampleIndex);
        command.Parameters.AddWithValue("$end_sample", candidate.EndSampleIndex);
        command.Parameters.AddWithValue("$fingerprint", candidate.Fingerprint);
        command.Parameters.AddWithValue("$gap", candidate.GapBeforeSamples);
        command.Parameters.AddWithValue("$saturation", candidate.SaturationCount);
        command.Parameters.AddWithValue("$contact", candidate.ContactEvidence);
        command.Parameters.AddWithValue("$voltage", EncodeDoubles(candidate.Voltage208));
        command.Parameters.AddWithValue("$real", EncodeDoubles(candidate.FullReal256));
        command.Parameters.AddWithValue("$imaginary", EncodeDoubles(candidate.FullImaginary256));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ImagingReferenceCandidateRecord> ListReferenceCandidates(Guid imagingRunId)
    {
        using var connection = OpenReadConnection();
        if (!TableExists(connection, "imaging_reference_candidates"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT candidate_sequence, source_id, captured_at_utc,
                   block_number, frame_number, start_sample_index, end_sample_index,
                   fingerprint, gap_before_samples, saturation_count, contact_evidence,
                   voltage_208, full_real_256, full_imag_256
            FROM imaging_reference_candidates
            WHERE imaging_run_id = $run
            ORDER BY candidate_sequence ASC;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var candidates = new List<ImagingReferenceCandidateRecord>();
        while (reader.Read())
        {
            candidates.Add(new ImagingReferenceCandidateRecord(
                imagingRunId,
                reader.GetInt64(0),
                reader.GetString(1),
                ParseTimestamp(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetString(10),
                DecodeDoubles((byte[])reader.GetValue(11)),
                DecodeDoubles((byte[])reader.GetValue(12)),
                DecodeDoubles((byte[])reader.GetValue(13))));
        }

        return candidates;
    }

    public IReadOnlyList<ImagingReferenceEpochRecord> ListReferenceEpochs(Guid imagingRunId)
    {
        using var connection = OpenReadConnection();
        if (!TableExists(connection, "imaging_reference_epochs"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT reference_epoch, locked_block_number, locked_at_utc,
                   retained_frame_count, rejected_frame_count, reference_amplitude_208,
                   reference_full_real_256, reference_full_imag_256, noise_global_threshold,
                   demod_estimated_window_samples, demod_uniform_offset_samples,
                   demod_rotation_start_channel, demod_rotation_direction,
                   frequency_hz, dac_gain, pga_gain, lock_kind,
                   common_scale_normalized, common_scale_normalization_policy,
                   median_input_common_scale, reference_scale_policy, source_candidate_ids_json,
                   selected_window_started_at_utc, selected_window_ended_at_utc,
                   effective_reference_at_utc, selected_window_drift_per_minute,
                   selected_window_gap_count, selected_window_saturation_count,
                   selected_window_contact_evidence, noise_estimation_policy,
                   action_group_id, common_action_at_utc, window_skew_ms,
                   switch_skew_ms, synchronized_set_count
            FROM imaging_reference_epochs
            WHERE imaging_run_id = $run
            ORDER BY reference_epoch ASC;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var epochs = new List<ImagingReferenceEpochRecord>();
        while (reader.Read())
        {
            epochs.Add(new ImagingReferenceEpochRecord(
                imagingRunId,
                reader.GetInt32(0),
                reader.GetInt32(1),
                ParseTimestamp(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetInt32(4),
                DecodeDoubles((byte[])reader.GetValue(5)),
                DecodeDoubles((byte[])reader.GetValue(6)),
                DecodeDoubles((byte[])reader.GetValue(7)),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetDouble(13),
                reader.GetDouble(14),
                reader.GetInt32(15),
                reader.GetString(16),
                reader.GetInt32(17) != 0,
                reader.GetString(18),
                reader.GetDouble(19),
                reader.GetString(20),
                reader.IsDBNull(21) ? [] : DecodeStringArray(reader.GetString(21)),
                reader.IsDBNull(22) ? null : ParseTimestamp(reader.GetString(22)),
                reader.IsDBNull(23) ? null : ParseTimestamp(reader.GetString(23)),
                reader.IsDBNull(24) ? null : ParseTimestamp(reader.GetString(24)),
                reader.IsDBNull(25) ? null : reader.GetDouble(25),
                reader.GetInt32(26),
                reader.GetInt32(27),
                reader.IsDBNull(28) ? null : reader.GetString(28),
                reader.GetString(29),
                reader.IsDBNull(30) ? null : reader.GetString(30),
                reader.IsDBNull(31) ? null : ParseTimestamp(reader.GetString(31)),
                reader.IsDBNull(32) ? null : reader.GetDouble(32),
                reader.IsDBNull(33) ? null : reader.GetDouble(33),
                reader.GetInt32(34)));
        }

        return epochs;
    }

    public void SetMesh(SqliteConnection connection, Guid imagingRunId, double[,] nodeCoords, int[,] cellConnectivity)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(nodeCoords);
        ArgumentNullException.ThrowIfNull(cellConnectivity);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE imaging_runs SET
                node_coords = $nodes,
                node_coord_rows = $node_rows,
                node_coord_cols = $node_cols,
                cell_connectivity = $cells,
                cell_rows = $cell_rows,
                cell_cols = $cell_cols
            WHERE imaging_run_id = $run AND node_coords IS NULL;
            """;
        command.Parameters.AddWithValue("$nodes", EncodeDoubles2D(nodeCoords));
        command.Parameters.AddWithValue("$node_rows", nodeCoords.GetLength(0));
        command.Parameters.AddWithValue("$node_cols", nodeCoords.GetLength(1));
        command.Parameters.AddWithValue("$cells", EncodeInts2D(cellConnectivity));
        command.Parameters.AddWithValue("$cell_rows", cellConnectivity.GetLength(0));
        command.Parameters.AddWithValue("$cell_cols", cellConnectivity.GetLength(1));
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public void LinkRawRun(SqliteConnection connection, Guid imagingRunId, Guid rawRunId, string rawHdf5Path, DateTimeOffset linkedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawHdf5Path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO imaging_run_raw_links(imaging_run_id, raw_run_id, raw_hdf5_path, linked_at_utc)
            VALUES ($run, $raw, $path, $linked);
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$raw", rawRunId.ToString("D"));
        command.Parameters.AddWithValue("$path", Path.GetFullPath(rawHdf5Path));
        command.Parameters.AddWithValue("$linked", FormatTimestamp(linkedAt));
        command.ExecuteNonQuery();
    }

    public void EndRun(SqliteConnection connection, Guid imagingRunId, DateTimeOffset endedAt)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE imaging_runs SET ended_at_utc = $ended WHERE imaging_run_id = $run;
            """;
        command.Parameters.AddWithValue("$ended", FormatTimestamp(endedAt));
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ImagingRunSummary> ListImagingRuns(int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = OpenReadConnection();
        var runColumns = ReadColumnNames(connection, "imaging_runs");
        var frameColumns = ReadColumnNames(connection, "imaging_frames");
        var reconstructionCount = frameColumns.Contains("conductivity")
            ? "(SELECT COUNT(*) FROM imaging_frames f WHERE f.imaging_run_id = runs.imaging_run_id AND f.conductivity IS NOT NULL)"
            : "0";
        var rawLinkCount = TableExists(connection, "imaging_run_raw_links")
            ? "(SELECT COUNT(*) FROM imaging_run_raw_links l WHERE l.imaging_run_id = runs.imaging_run_id)"
            : "0";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                runs.imaging_run_id,
                runs.session_id,
                runs.set_label,
                runs.started_at_utc,
                runs.ended_at_utc,
                runs.reconstruction_route,
                (SELECT COUNT(*) FROM imaging_frames f WHERE f.imaging_run_id = runs.imaging_run_id) AS frame_count,
                {reconstructionCount} AS recon_count,
                {rawLinkCount} AS raw_link_count,
                {ColumnOrDefault(runColumns, "storage_mode", "'legacy'", "runs")} AS storage_mode,
                {ColumnOrDefault(runColumns, "reconstruction_scale_status", "'model_relative'", "runs")} AS reconstruction_scale_status,
                {ColumnOrDefault(runColumns, "reconstruction_scale_provenance", "'legacy-unlabeled-model-relative'", "runs")} AS reconstruction_scale_provenance,
                {ColumnOrDefault(runColumns, "reference_scale_policy", "'legacy_unspecified'", "runs")} AS reference_scale_policy,
                {ColumnOrDefault(runColumns, "contact_operating_fingerprint_json", "NULL", "runs")} AS contact_operating_fingerprint_json,
                {ColumnOrDefault(runColumns, "contact_threshold_profile_id", "NULL", "runs")} AS contact_threshold_profile_id,
                {ColumnOrDefault(runColumns, "contact_threshold_mode", "'uncalibrated-legacy'", "runs")} AS contact_threshold_mode
            FROM imaging_runs runs
            ORDER BY runs.started_at_utc DESC, runs.imaging_run_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<ImagingRunSummary>();
        while (reader.Read())
        {
            runs.Add(new ImagingRunSummary(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.GetString(15)));
        }

        return runs;
    }

    public ImagingRunDetail? GetImagingRunDetail(Guid imagingRunId)
    {
        using var connection = OpenReadConnection();
        var runColumns = ReadColumnNames(connection, "imaging_runs");
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                session_id, set_label, started_at_utc, ended_at_utc, reconstruction_route,
                difference_lambda, custom_lambda, mesh_size, frequency_hz, channel_cycles,
                sample_rate_hz, difference_orientation, reference_block_number, reference_208,
                node_coords, node_coord_rows, node_coord_cols,
                cell_connectivity, cell_rows, cell_cols,
                {ColumnOrDefault(runColumns, "storage_mode", "'legacy'")},
                {ColumnOrDefault(runColumns, "reconstruction_scale_status", "'model_relative'")},
                {ColumnOrDefault(runColumns, "reconstruction_scale_provenance", "'legacy-unlabeled-model-relative'")},
                {ColumnOrDefault(runColumns, "reference_scale_policy", "'legacy_unspecified'")},
                {ColumnOrDefault(runColumns, "contact_operating_fingerprint_json", "NULL")},
                {ColumnOrDefault(runColumns, "contact_threshold_profile_id", "NULL")},
                {ColumnOrDefault(runColumns, "contact_threshold_mode", "'uncalibrated-legacy'")},
                {ColumnOrDefault(runColumns, "requested_frequency_hz", "NULL")},
                {ColumnOrDefault(runColumns, "actual_frequency_hz", "NULL")},
                {ColumnOrDefault(runColumns, "dds_frequency_tuning_word", "NULL")},
                {ColumnOrDefault(runColumns, "requested_dwell_us", "NULL")},
                {ColumnOrDefault(runColumns, "effective_dwell_us", "NULL")},
                {ColumnOrDefault(runColumns, "ad_range_code", "NULL")},
                {ColumnOrDefault(runColumns, "adc_full_span_volts", "NULL")},
                {ColumnOrDefault(runColumns, "adc_lsb_volts", "NULL")}
            FROM imaging_runs
            WHERE imaging_run_id = $run;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var nodeCoords = reader.IsDBNull(14)
            ? null
            : DecodeDoubles2D((byte[])reader.GetValue(14), reader.GetInt32(15), reader.GetInt32(16));
        var cellConnectivity = reader.IsDBNull(17)
            ? null
            : DecodeInts2D((byte[])reader.GetValue(17), reader.GetInt32(18), reader.GetInt32(19));
        return new ImagingRunDetail(
            imagingRunId,
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ParseTimestamp(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)),
            reader.GetString(4),
            reader.GetDouble(5),
            reader.GetInt32(6) != 0,
            reader.GetDouble(7),
            reader.GetDouble(8),
            reader.GetDouble(9),
            reader.GetDouble(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.IsDBNull(13) ? null : DecodeDoubles((byte[])reader.GetValue(13)),
            nodeCoords,
            cellConnectivity,
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.GetString(26),
            reader.IsDBNull(27) ? null : reader.GetDouble(27),
            reader.IsDBNull(28) ? null : reader.GetDouble(28),
            reader.IsDBNull(29) ? null : reader.GetInt64(29),
            reader.IsDBNull(30) ? null : reader.GetDouble(30),
            reader.IsDBNull(31) ? null : reader.GetDouble(31),
            reader.IsDBNull(32) ? null : reader.GetInt32(32),
            reader.IsDBNull(33) ? null : reader.GetDouble(33),
            reader.IsDBNull(34) ? null : reader.GetDouble(34));
    }

    public IReadOnlyList<ImagingFrameIndexEntry> ListFrameIndex(Guid imagingRunId)
    {
        using var connection = OpenReadConnection();
        var frameColumns = ReadColumnNames(connection, "imaging_frames");
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT block_number, captured_at_utc, quality_weight, accepted_frames, rejected_frames,
                   {ColumnOrDefault(frameColumns, "conductivity", "NULL")} IS NOT NULL
            FROM imaging_frames
            WHERE imaging_run_id = $run
            ORDER BY block_number ASC;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var frames = new List<ImagingFrameIndexEntry>();
        while (reader.Read())
        {
            frames.Add(new ImagingFrameIndexEntry(
                reader.GetInt32(0),
                ParseTimestamp(reader.GetString(1)),
                reader.GetDouble(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5) != 0));
        }

        return frames;
    }

    public IReadOnlyList<ImagingReferenceStationarityObservation> ListReferenceStationarityObservations(
        Guid imagingRunId)
    {
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT block_number, captured_at_utc, mean_amplitude_208
            FROM imaging_frames
            WHERE imaging_run_id = $run
            ORDER BY block_number ASC;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var observations = new List<ImagingReferenceStationarityObservation>();
        while (reader.Read())
        {
            observations.Add(new ImagingReferenceStationarityObservation(
                reader.GetInt32(0),
                ParseTimestamp(reader.GetString(1)),
                DecodeDoubles((byte[])reader.GetValue(2))));
        }

        return observations;
    }

    public ImagingFrameDetail? GetFrame(Guid imagingRunId, int blockNumber)
    {
        using var connection = OpenReadConnection();
        var frameColumns = ReadColumnNames(connection, "imaging_frames");
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT captured_at_utc, quality_weight, accepted_frames, rejected_frames,
                   mean_amplitude_208, mean_real_208, mean_imag_208,
                   {ColumnOrDefault(frameColumns, "conductivity", "NULL")},
                   {ColumnOrDefault(frameColumns, "mean_full_amplitude_256", "NULL")},
                   {ColumnOrDefault(frameColumns, "mean_full_real_256", "NULL")},
                   {ColumnOrDefault(frameColumns, "mean_full_imag_256", "NULL")},
                   {ColumnOrDefault(frameColumns, "measurement_weight_208", "NULL")},
                   {ColumnOrDefault(frameColumns, "weight_policy_version", "'all-one-v1'")},
                   {ColumnOrDefault(frameColumns, "image_quality_score", "NULL")},
                   {ColumnOrDefault(frameColumns, "reconstruction_condition_number", "NULL")},
                   {ColumnOrDefault(frameColumns, "electrode_scores", "NULL")},
                   {ColumnOrDefault(frameColumns, "fault_confidence", "NULL")},
                   {ColumnOrDefault(frameColumns, "electrode_states", "NULL")},
                   {ColumnOrDefault(frameColumns, "fault_types", "NULL")},
                   {ColumnOrDefault(frameColumns, "upgrade_gate_reasons", "NULL")},
                   {ColumnOrDefault(frameColumns, "contact_summary", "NULL")},
                   {ColumnOrDefault(frameColumns, "candidate_diagnostic_json", "NULL")},
                   {ColumnOrDefault(frameColumns, "display_compensation_policy", "NULL")},
                   {ColumnOrDefault(frameColumns, "display_compensation_only", "0")},
                   {ColumnOrDefault(frameColumns, "display_compensation_payload_json", "NULL")},
                   {ColumnOrDefault(frameColumns, "reference_invalidated", "0")},
                   {ColumnOrDefault(frameColumns, "reference_status", "NULL")},
                   {ColumnOrDefault(frameColumns, "conductivity_raw", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_session_id", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_action", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_nis_per_dof", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_gain_mean", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_variance_inflation", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_update_count", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_total_latency_frames", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_mode", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_fallback", "NULL")},
                   {ColumnOrDefault(frameColumns, "dynamic_kalman_solve_ms", "NULL")},
                   {ColumnOrDefault(frameColumns, "reconstruction_backend_elapsed_ms", "NULL")},
                   {ColumnOrDefault(frameColumns, "reference_epoch", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_common_scale", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_shape_residual_relative", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_complex_scale_magnitude", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_complex_phase_degrees", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_complex_shape_residual_relative", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_common_mode_energy_fraction", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_near_drive_scale", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_remote_scale", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_classification", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_global_noise_score", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_global_noise_threshold", "NULL")},
                   {ColumnOrDefault(frameColumns, "baseline_demod_state_changed", "NULL")},
                   {ColumnOrDefault(frameColumns, "demod_estimated_window_samples", "NULL")},
                   {ColumnOrDefault(frameColumns, "demod_uniform_offset_samples", "NULL")},
                   {ColumnOrDefault(frameColumns, "demod_rotation_start_channel", "NULL")},
                   {ColumnOrDefault(frameColumns, "demod_rotation_direction", "NULL")},
                   {ColumnOrDefault(frameColumns, "common_scale_normalized", "0")},
                   {ColumnOrDefault(frameColumns, "common_scale_normalization_policy", "'none'")},
                   {ColumnOrDefault(frameColumns, "common_scale_normalization_factor", "NULL")}
            FROM imaging_frames
            WHERE imaging_run_id = $run AND block_number = $block;
            """;
        command.Parameters.AddWithValue("$run", imagingRunId.ToString("D"));
        command.Parameters.AddWithValue("$block", blockNumber);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImagingFrameDetail(
            imagingRunId,
            blockNumber,
            ParseTimestamp(reader.GetString(0)),
            reader.GetDouble(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            DecodeDoubles((byte[])reader.GetValue(4)),
            DecodeDoubles((byte[])reader.GetValue(5)),
            DecodeDoubles((byte[])reader.GetValue(6)),
            reader.IsDBNull(7) ? null : DecodeDoubles((byte[])reader.GetValue(7)),
            reader.IsDBNull(8) ? null : DecodeDoubles((byte[])reader.GetValue(8)),
            reader.IsDBNull(9) ? null : DecodeDoubles((byte[])reader.GetValue(9)),
            reader.IsDBNull(10) ? null : DecodeDoubles((byte[])reader.GetValue(10)),
            reader.IsDBNull(11) ? null : DecodeDoubles((byte[])reader.GetValue(11)),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetDouble(13),
            reader.IsDBNull(14) ? null : reader.GetDouble(14),
            reader.IsDBNull(15) ? null : DecodeDoubles((byte[])reader.GetValue(15)),
            reader.IsDBNull(16) ? null : DecodeDoubles((byte[])reader.GetValue(16)),
            reader.IsDBNull(17) ? null : DecodeStringArray(reader.GetString(17)),
            reader.IsDBNull(18) ? null : DecodeStringArray(reader.GetString(18)),
            reader.IsDBNull(19) ? null : DecodeStringArray(reader.GetString(19)),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.GetInt32(23) != 0,
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.GetInt32(25) != 0,
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? null : DecodeDoubles((byte[])reader.GetValue(27)),
            reader.IsDBNull(28) ? null : reader.GetString(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : reader.GetDouble(30),
            reader.IsDBNull(31) ? null : reader.GetDouble(31),
            reader.IsDBNull(32) ? null : reader.GetDouble(32),
            reader.IsDBNull(33) ? null : reader.GetInt32(33),
            reader.IsDBNull(34) ? null : reader.GetInt32(34),
            reader.IsDBNull(35) ? null : reader.GetString(35),
            reader.IsDBNull(36) ? null : reader.GetInt32(36) != 0,
            reader.IsDBNull(37) ? null : reader.GetDouble(37),
            reader.IsDBNull(38) ? null : reader.GetDouble(38),
            reader.IsDBNull(39) ? null : reader.GetInt32(39),
            reader.IsDBNull(40) ? null : reader.GetDouble(40),
            reader.IsDBNull(41) ? null : reader.GetDouble(41),
            reader.IsDBNull(42) ? null : reader.GetDouble(42),
            reader.IsDBNull(43) ? null : reader.GetDouble(43),
            reader.IsDBNull(44) ? null : reader.GetDouble(44),
            reader.IsDBNull(45) ? null : reader.GetDouble(45),
            reader.IsDBNull(46) ? null : reader.GetDouble(46),
            reader.IsDBNull(47) ? null : reader.GetDouble(47),
            reader.IsDBNull(48) ? null : reader.GetString(48),
            reader.IsDBNull(49) ? null : reader.GetDouble(49),
            reader.IsDBNull(50) ? null : reader.GetDouble(50),
            reader.IsDBNull(51) ? null : reader.GetInt32(51) != 0,
            reader.IsDBNull(52) ? null : reader.GetDouble(52),
            reader.IsDBNull(53) ? null : reader.GetInt32(53),
            reader.IsDBNull(54) ? null : reader.GetInt32(54),
            reader.IsDBNull(55) ? null : reader.GetInt32(55),
            reader.GetInt32(56) != 0,
            reader.GetString(57),
            reader.IsDBNull(58) ? null : reader.GetDouble(58));
    }

    private static void EnsureImagingFrameDiagnosticColumns(SqliteConnection connection)
    {
        AddColumnIfMissing(connection, "imaging_frames", "measurement_weight_208", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "mean_full_amplitude_256", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "mean_full_real_256", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "mean_full_imag_256", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "weight_policy_version", "TEXT NOT NULL DEFAULT 'all-one-v1'");
        AddColumnIfMissing(connection, "imaging_frames", "image_quality_score", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "reconstruction_condition_number", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "electrode_scores", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "fault_confidence", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "electrode_states", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "fault_types", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "upgrade_gate_reasons", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "contact_summary", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "candidate_diagnostic_json", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "display_compensation_policy", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "display_compensation_only", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_frames", "display_compensation_payload_json", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "reference_invalidated", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_frames", "reference_status", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "common_scale_normalized", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "imaging_frames", "common_scale_normalization_policy", "TEXT NOT NULL DEFAULT 'none'");
        AddColumnIfMissing(connection, "imaging_frames", "common_scale_normalization_factor", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "conductivity_raw", "BLOB NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_session_id", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_action", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_nis_per_dof", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_gain_mean", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_variance_inflation", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_update_count", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_total_latency_frames", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_mode", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_fallback", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "dynamic_kalman_solve_ms", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "reconstruction_backend_elapsed_ms", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "reference_epoch", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_common_scale", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_shape_residual_relative", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_complex_scale_magnitude", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_complex_phase_degrees", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_complex_shape_residual_relative", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_common_mode_energy_fraction", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_near_drive_scale", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_remote_scale", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_classification", "TEXT NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_global_noise_score", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_global_noise_threshold", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "baseline_demod_state_changed", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "demod_estimated_window_samples", "REAL NULL");
        AddColumnIfMissing(connection, "imaging_frames", "demod_uniform_offset_samples", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "demod_rotation_start_channel", "INTEGER NULL");
        AddColumnIfMissing(connection, "imaging_frames", "demod_rotation_direction", "INTEGER NULL");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static HashSet<string> ReadColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $table COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is not null;
    }

    private static string ColumnOrDefault(
        IReadOnlySet<string> columns,
        string column,
        string defaultExpression,
        string? tableAlias = null)
    {
        return columns.Contains(column)
            ? string.IsNullOrWhiteSpace(tableAlias) ? column : $"{tableAlias}.{column}"
            : defaultExpression;
    }

    private SqliteConnection OpenReadConnection()
    {
        var connection = new SqliteConnection(readConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    internal static byte[] EncodeDoubles(double[] values)
    {
        return MemoryMarshal.AsBytes<double>(values).ToArray();
    }

    internal static double[] DecodeDoubles(byte[] payload)
    {
        return MemoryMarshal.Cast<byte, double>(payload).ToArray();
    }

    private static double[] CreateAllOneWeights()
    {
        return Enumerable.Repeat(1.0, BoundaryVectorLength).ToArray();
    }

    private static void ValidateReferenceEpoch(ImagingReferenceEpochRecord reference)
    {
        var hasSelectedWindow = reference.SourceCandidateIds is { Length: > 0 };
        var hasAnySelectedWindowField = hasSelectedWindow ||
            reference.SelectedWindowStartedAt is not null ||
            reference.SelectedWindowEndedAt is not null ||
            reference.EffectiveReferenceAt is not null ||
            reference.SelectedWindowDriftPerMinute is not null ||
            reference.SelectedWindowGapCount != 0 ||
            reference.SelectedWindowSaturationCount != 0 ||
            reference.SelectedWindowContactEvidence is not null;
        var selectedWindowInvalid = hasAnySelectedWindowField &&
            (!hasSelectedWindow ||
             reference.SourceCandidateIds!.Length != reference.RetainedFrameCount ||
             reference.SourceCandidateIds.Any(string.IsNullOrWhiteSpace) ||
             reference.SourceCandidateIds.Distinct(StringComparer.Ordinal).Count() !=
                reference.SourceCandidateIds.Length ||
             reference.SelectedWindowStartedAt is not { } windowStart ||
             reference.SelectedWindowEndedAt is not { } windowEnd ||
             reference.EffectiveReferenceAt is not { } effectiveAt ||
             windowEnd < windowStart ||
             effectiveAt < windowStart ||
             effectiveAt > windowEnd ||
             reference.SelectedWindowDriftPerMinute is not { } drift ||
             !double.IsFinite(drift) ||
             drift < 0.0 ||
             reference.SelectedWindowGapCount < 0 ||
             reference.SelectedWindowSaturationCount < 0 ||
             string.IsNullOrWhiteSpace(reference.SelectedWindowContactEvidence));
        var hasActionGroup = !string.IsNullOrWhiteSpace(reference.ActionGroupId);
        var hasAnySynchronizedActionField = hasActionGroup
            || reference.CommonActionAt is not null
            || reference.WindowSkewMilliseconds is not null
            || reference.SwitchSkewMilliseconds is not null
            || reference.SynchronizedSetCount != 1;
        var synchronizedActionInvalid = hasAnySynchronizedActionField
            && (!hasActionGroup
                || reference.CommonActionAt is null
                || reference.WindowSkewMilliseconds is not { } windowSkew
                || !double.IsFinite(windowSkew)
                || reference.SwitchSkewMilliseconds is not { } switchSkew
                || !double.IsFinite(switchSkew)
                || reference.SynchronizedSetCount <= 0);
        if (reference.ReferenceEpoch <= 0 ||
            reference.LockedBlockNumber <= 0 ||
            reference.RetainedFrameCount <= 0 ||
            reference.RejectedFrameCount < 0 ||
            reference.ReferenceAmplitude208.Length != BoundaryVectorLength ||
            reference.ReferenceFullReal256.Length != 256 ||
            reference.ReferenceFullImaginary256.Length != 256 ||
            reference.ReferenceAmplitude208.Any(value => !double.IsFinite(value)) ||
            reference.ReferenceFullReal256.Any(value => !double.IsFinite(value)) ||
            reference.ReferenceFullImaginary256.Any(value => !double.IsFinite(value)) ||
            !double.IsFinite(reference.DemodEstimatedWindowSamples) ||
            reference.DemodEstimatedWindowSamples <= 0.0 ||
            !double.IsFinite(reference.FrequencyHz) ||
            reference.FrequencyHz <= 0.0 ||
            !double.IsFinite(reference.DacGain) ||
            reference.DacGain <= 0.0 ||
            reference.PgaGain <= 0 ||
            string.IsNullOrWhiteSpace(reference.LockKind) ||
            !double.IsFinite(reference.MedianInputCommonScale) ||
            reference.MedianInputCommonScale <= 0.0 ||
            (reference.CommonScaleNormalized &&
                (string.IsNullOrWhiteSpace(reference.CommonScaleNormalizationPolicy) ||
                 string.Equals(reference.CommonScaleNormalizationPolicy, "none", StringComparison.OrdinalIgnoreCase))) ||
            string.IsNullOrWhiteSpace(reference.NoiseEstimationPolicy) ||
            selectedWindowInvalid ||
            synchronizedActionInvalid)
        {
            throw new ArgumentException("Reference epoch contains invalid lock provenance.", nameof(reference));
        }
    }

    private static void ValidateReferenceCandidate(ImagingReferenceCandidateRecord candidate)
    {
        if (candidate.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(candidate.SourceId) ||
            candidate.BlockNumber <= 0 ||
            candidate.FrameNumber < 0 ||
            candidate.EndSampleIndex < candidate.StartSampleIndex ||
            string.IsNullOrWhiteSpace(candidate.Fingerprint) ||
            candidate.GapBeforeSamples < 0 ||
            candidate.SaturationCount < 0 ||
            string.IsNullOrWhiteSpace(candidate.ContactEvidence) ||
            candidate.Voltage208.Length != 208 ||
            candidate.FullReal256.Length != 256 ||
            candidate.FullImaginary256.Length != 256 ||
            candidate.Voltage208.Any(value => !double.IsFinite(value)) ||
            candidate.FullReal256.Any(value => !double.IsFinite(value)) ||
            candidate.FullImaginary256.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Reference candidate contains invalid provenance or observation data.", nameof(candidate));
        }
    }

    private static object ToDbBlob(double[]? values)
    {
        return values is null ? DBNull.Value : EncodeDoubles(values);
    }

    private static object ToDbText(string[]? values)
    {
        return values is null ? DBNull.Value : JsonSerializer.Serialize(values);
    }

    private static string[] DecodeStringArray(string payload)
    {
        return JsonSerializer.Deserialize<string[]>(payload) ?? [];
    }

    internal static byte[] EncodeDoubles2D(double[,] values)
    {
        var flat = new double[values.Length];
        Buffer.BlockCopy(values, 0, flat, 0, values.Length * sizeof(double));
        return EncodeDoubles(flat);
    }

    internal static double[,] DecodeDoubles2D(byte[] payload, int rows, int cols)
    {
        var values = new double[rows, cols];
        Buffer.BlockCopy(payload, 0, values, 0, Math.Min(payload.Length, rows * cols * sizeof(double)));
        return values;
    }

    internal static byte[] EncodeInts2D(int[,] values)
    {
        var flat = new int[values.Length];
        Buffer.BlockCopy(values, 0, flat, 0, values.Length * sizeof(int));
        return MemoryMarshal.AsBytes<int>(flat).ToArray();
    }

    internal static int[,] DecodeInts2D(byte[] payload, int rows, int cols)
    {
        var values = new int[rows, cols];
        Buffer.BlockCopy(payload, 0, values, 0, Math.Min(payload.Length, rows * cols * sizeof(int)));
        return values;
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}

public sealed record ImagingRunConfigRecord(
    Guid ImagingRunId,
    Guid SessionId,
    string SetLabel,
    DateTimeOffset StartedAt,
    string ReconstructionRoute,
    double DifferenceLambda,
    bool CustomLambdaEnabled,
    double MeshSize,
    double FrequencyHz,
    double ChannelCycles,
    double SampleRateHz,
    string DifferenceOrientation,
    string StorageMode = RealtimeStoragePolicy.DefaultValue,
    string ReconstructionScaleStatus = ReconstructionScale.ModelRelative,
    string ReconstructionScaleProvenance = ReconstructionScale.NormalizedModelProvenance,
    string? ContactOperatingFingerprintJson = null,
    string? ContactThresholdProfileId = null,
    string ContactThresholdMode = "uncalibrated-legacy",
    string ReferenceScalePolicy = EcdCwrReferenceScalePolicy.PreservePhysicalScale,
    double? RequestedFrequencyHz = null,
    double? ActualFrequencyHz = null,
    long? DdsFrequencyTuningWord = null,
    double? RequestedDwellUs = null,
    double? EffectiveDwellUs = null,
    int? AdRangeCode = null,
    double? AdcFullSpanVolts = null,
    double? AdcLsbVolts = null);

public sealed record ImagingReferenceEpochRecord(
    Guid ImagingRunId,
    int ReferenceEpoch,
    int LockedBlockNumber,
    DateTimeOffset LockedAt,
    int RetainedFrameCount,
    int RejectedFrameCount,
    double[] ReferenceAmplitude208,
    double[] ReferenceFullReal256,
    double[] ReferenceFullImaginary256,
    double? NoiseGlobalThreshold,
    double DemodEstimatedWindowSamples,
    int DemodUniformOffsetSamples,
    int DemodRotationStartChannel,
    int DemodRotationDirection,
    double FrequencyHz,
    double DacGain,
    int PgaGain,
    string LockKind,
    bool CommonScaleNormalized = false,
    string CommonScaleNormalizationPolicy = "none",
    double MedianInputCommonScale = 1.0,
    string ReferenceScalePolicy = EcdCwrReferenceScalePolicy.PreservePhysicalScale,
    string[]? SourceCandidateIds = null,
    DateTimeOffset? SelectedWindowStartedAt = null,
    DateTimeOffset? SelectedWindowEndedAt = null,
    DateTimeOffset? EffectiveReferenceAt = null,
    double? SelectedWindowDriftPerMinute = null,
    int SelectedWindowGapCount = 0,
    int SelectedWindowSaturationCount = 0,
    string? SelectedWindowContactEvidence = null,
    string NoiseEstimationPolicy = "raw_reference_dispersion-v1",
    string? ActionGroupId = null,
    DateTimeOffset? CommonActionAt = null,
    double? WindowSkewMilliseconds = null,
    double? SwitchSkewMilliseconds = null,
    int SynchronizedSetCount = 1,
    long LockedStartSampleIndex = -1,
    double[]? NoisePrecisionWeight208 = null);

public sealed record ImagingReferenceCandidateRecord(
    Guid ImagingRunId,
    long Sequence,
    string SourceId,
    DateTimeOffset CapturedAt,
    int BlockNumber,
    int FrameNumber,
    long StartSampleIndex,
    long EndSampleIndex,
    string Fingerprint,
    int GapBeforeSamples,
    int SaturationCount,
    string ContactEvidence,
    double[] Voltage208,
    double[] FullReal256,
    double[] FullImaginary256);

public sealed record ImagingFrameRecord(
    Guid ImagingRunId,
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    int AcceptedFrames,
    int RejectedFrames,
    double[] MeanAmplitude208,
    double[] MeanReal208,
    double[] MeanImaginary208,
    double[]? MeanFullAmplitude256 = null,
    double[]? MeanFullReal256 = null,
    double[]? MeanFullImaginary256 = null,
    double[]? MeasurementWeight208 = null,
    string WeightPolicyVersion = "all-one-v1",
    double? ImageQualityScore = null,
    double? ReconstructionConditionNumber = null,
    double[]? ElectrodeScores = null,
    double[]? FaultConfidence = null,
    string[]? ElectrodeStates = null,
    string[]? FaultTypes = null,
    string[]? UpgradeGateReasons = null,
    string? ContactSummary = null,
    string? CandidateDiagnosticJson = null,
    string? DisplayCompensationPolicy = null,
    bool DisplayCompensationOnly = false,
    string? DisplayCompensationPayloadJson = null,
    bool ReferenceInvalidated = false,
    string? ReferenceStatus = null,
    int? ReferenceEpoch = null,
    double? BaselineCommonScale = null,
    double? BaselineShapeResidualRelative = null,
    double? BaselineComplexScaleMagnitude = null,
    double? BaselineComplexPhaseDegrees = null,
    double? BaselineComplexShapeResidualRelative = null,
    double? BaselineCommonModeEnergyFraction = null,
    double? BaselineNearDriveScale = null,
    double? BaselineRemoteScale = null,
    string? BaselineClassification = null,
    double? BaselineGlobalNoiseScore = null,
    double? BaselineGlobalNoiseThreshold = null,
    bool? BaselineDemodStateChanged = null,
    double? DemodEstimatedWindowSamples = null,
    int? DemodUniformOffsetSamples = null,
    int? DemodRotationStartChannel = null,
    int? DemodRotationDirection = null,
    bool CommonScaleNormalized = false,
    string CommonScaleNormalizationPolicy = "none",
    double? CommonScaleNormalizationFactor = null);

public sealed record ImagingRunSummary(
    Guid ImagingRunId,
    Guid SessionId,
    string SetLabel,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string ReconstructionRoute,
    int FrameCount,
    int ReconCount,
    int RawLinkCount,
    string StorageMode = "legacy",
    string ReconstructionScaleStatus = ReconstructionScale.ModelRelative,
    string ReconstructionScaleProvenance = "legacy-unlabeled-model-relative",
    string ReferenceScalePolicy = "legacy_unspecified",
    string? ContactOperatingFingerprintJson = null,
    string? ContactThresholdProfileId = null,
    string ContactThresholdMode = "uncalibrated-legacy");

public sealed record ImagingRunDetail(
    Guid ImagingRunId,
    Guid SessionId,
    string SetLabel,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string ReconstructionRoute,
    double DifferenceLambda,
    bool CustomLambdaEnabled,
    double MeshSize,
    double FrequencyHz,
    double ChannelCycles,
    double SampleRateHz,
    string DifferenceOrientation,
    int? ReferenceBlockNumber,
    double[]? Reference208,
    double[,]? NodeCoords,
    int[,]? CellConnectivity,
    string StorageMode = "legacy",
    string ReconstructionScaleStatus = ReconstructionScale.ModelRelative,
    string ReconstructionScaleProvenance = "legacy-unlabeled-model-relative",
    string ReferenceScalePolicy = "legacy_unspecified",
    string? ContactOperatingFingerprintJson = null,
    string? ContactThresholdProfileId = null,
    string ContactThresholdMode = "uncalibrated-legacy",
    double? RequestedFrequencyHz = null,
    double? ActualFrequencyHz = null,
    long? DdsFrequencyTuningWord = null,
    double? RequestedDwellUs = null,
    double? EffectiveDwellUs = null,
    int? AdRangeCode = null,
    double? AdcFullSpanVolts = null,
    double? AdcLsbVolts = null);

public sealed record ImagingFrameIndexEntry(
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    int AcceptedFrames,
    int RejectedFrames,
    bool HasConductivity);

public sealed record ImagingReferenceStationarityObservation(
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double[] MeanAmplitude208);

public sealed record ImagingFrameDetail(
    Guid ImagingRunId,
    int BlockNumber,
    DateTimeOffset CapturedAt,
    double QualityWeight,
    int AcceptedFrames,
    int RejectedFrames,
    double[] MeanAmplitude208,
    double[] MeanReal208,
    double[] MeanImaginary208,
    double[]? Conductivity,
    double[]? MeanFullAmplitude256,
    double[]? MeanFullReal256,
    double[]? MeanFullImaginary256,
    double[]? MeasurementWeight208,
    string WeightPolicyVersion,
    double? ImageQualityScore,
    double? ReconstructionConditionNumber,
    double[]? ElectrodeScores,
    double[]? FaultConfidence,
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
    double[]? RawConductivity,
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
    double? CommonScaleNormalizationFactor,
    string? ReconstructionLane = null,
    string? ReconstructionRevisionId = null,
    string? ReconstructionFrameOutcome = null,
    string? ReconstructionPresentationJson = null,
    string? ReconstructionExclusionReason = null,
    string? ReconstructionAlgorithmFingerprint = null);
