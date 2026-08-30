using System.Globalization;
using System.Text.Json;
using EitHost.Core.Pairing;
using Microsoft.Data.Sqlite;

namespace EitHost.Core.Storage.Catalog;

public sealed class ExperimentCatalog
{
    public const int CurrentSchemaVersion = 9;
    public const string RecordingStatus = "recording";
    public const string CompletedStatus = "completed";
    public const string InterruptedStatus = "interrupted";
    public const string FailedStatus = "failed";
    public const string ActiveLifecycleState = "active";
    public const string ArchivedLifecycleState = "archived";

    private readonly DataRootLayout layout;
    private readonly string connectionString;

    public ExperimentCatalog(DataRootLayout layout)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = layout.CatalogPath,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
    }

    public void Initialize()
    {
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(layout.RootPath);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """);
        var existingVersion = ReadUserVersion(connection);
        if (existingVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Catalog schema version {existingVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS experiment_sessions (
                session_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS experiment_pairings (
                session_id TEXT NOT NULL,
                set_label TEXT NOT NULL,
                usb_device_number INTEGER NOT NULL,
                usb_identity_key TEXT NOT NULL,
                usb_display_name TEXT NOT NULL,
                usb_vid TEXT NOT NULL,
                usb_pid TEXT NOT NULL,
                usb_location_path TEXT NOT NULL,
                dds_identity_key TEXT NOT NULL,
                dds_port_name TEXT NULL,
                dds_display_name TEXT NOT NULL,
                dds_vid TEXT NOT NULL,
                dds_pid TEXT NOT NULL,
                dds_location_path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(session_id, set_label),
                FOREIGN KEY(session_id) REFERENCES experiment_sessions(session_id)
            );
            CREATE TABLE IF NOT EXISTS experiment_runs (
                experiment_run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                set_label TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                status TEXT NOT NULL,
                storage_mode TEXT NOT NULL,
                run_directory TEXT NOT NULL,
                raw_status TEXT NOT NULL DEFAULT 'pending',
                demod_status TEXT NOT NULL DEFAULT 'pending',
                reconstruction_status TEXT NOT NULL DEFAULT 'pending',
                failure_message TEXT NULL,
                lifecycle_state TEXT NOT NULL DEFAULT 'active',
                archived_at_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS raw_segments (
                experiment_run_id TEXT NOT NULL,
                segment_sequence INTEGER NOT NULL,
                artifact_path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                start_sample_index INTEGER NOT NULL,
                end_sample_index INTEGER NOT NULL,
                sample_rows INTEGER NOT NULL,
                channel_count INTEGER NOT NULL,
                captured_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                has_discontinuity INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(experiment_run_id, segment_sequence),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS processing_blocks (
                experiment_run_id TEXT NOT NULL,
                block_number INTEGER NOT NULL,
                source_start_sample_index INTEGER NOT NULL,
                source_end_sample_index INTEGER NOT NULL,
                acquired_at_utc TEXT NOT NULL,
                demod_processed_at_utc TEXT NULL,
                demod_status TEXT NOT NULL,
                reconstruction_processed_at_utc TEXT NULL,
                reconstruction_status TEXT NOT NULL,
                failure_message TEXT NULL,
                quality_weight REAL NOT NULL DEFAULT 1.0,
                accepted_frames INTEGER NOT NULL DEFAULT 0,
                rejected_frames INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(experiment_run_id, block_number),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS derived_artifacts (
                derived_artifact_id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_run_id TEXT NOT NULL,
                block_number INTEGER NOT NULL,
                kind TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(experiment_run_id, block_number, kind),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS experiment_run_configs (
                experiment_run_id TEXT PRIMARY KEY,
                config_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS reconstruction_pipeline_manifests (
                experiment_run_id TEXT PRIMARY KEY,
                schema_version TEXT NOT NULL,
                algorithm_fingerprint TEXT NOT NULL,
                manifest_fingerprint TEXT NOT NULL,
                status TEXT NOT NULL,
                manifest_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                unavailable_reason TEXT NULL,
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS reconstruction_revisions (
                experiment_run_id TEXT NOT NULL,
                lane TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                status TEXT NOT NULL,
                algorithm_fingerprint TEXT NOT NULL,
                raw_denominator INTEGER NOT NULL DEFAULT 0,
                demod_denominator INTEGER NOT NULL DEFAULT 0,
                terminal_outcome_count INTEGER NOT NULL DEFAULT 0,
                reconstructed_count INTEGER NOT NULL DEFAULT 0,
                neutral_count INTEGER NOT NULL DEFAULT 0,
                excluded_count INTEGER NOT NULL DEFAULT 0,
                estimated_incremental_bytes INTEGER NOT NULL DEFAULT 0,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                published_at_utc TEXT NULL,
                failure_message TEXT NULL,
                PRIMARY KEY(experiment_run_id, lane, revision_id),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS reconstruction_lane_frames (
                experiment_run_id TEXT NOT NULL,
                lane TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                source_block_number INTEGER NOT NULL,
                sequence_number INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                acquired_at_utc TEXT NOT NULL,
                processed_at_utc TEXT NOT NULL,
                algorithm_fingerprint TEXT NOT NULL,
                artifact_path TEXT NULL,
                dataset_path TEXT NULL,
                final_weight_hash TEXT NULL,
                kalman_session_id TEXT NULL,
                kalman_disposition TEXT NULL,
                presentation_json TEXT NULL,
                exclusion_reason TEXT NULL,
                source_start_sample_index INTEGER NULL,
                source_end_sample_index INTEGER NULL,
                result_hash TEXT NULL,
                PRIMARY KEY(experiment_run_id, lane, revision_id, source_block_number),
                UNIQUE(experiment_run_id, lane, revision_id, sequence_number),
                FOREIGN KEY(experiment_run_id, lane, revision_id)
                    REFERENCES reconstruction_revisions(experiment_run_id, lane, revision_id)
                    ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS reference_epochs (
                experiment_run_id TEXT NOT NULL,
                reference_epoch INTEGER NOT NULL,
                locked_block_number INTEGER NOT NULL,
                locked_start_sample_index INTEGER NOT NULL DEFAULT -1,
                locked_at_utc TEXT NOT NULL,
                lock_kind TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(experiment_run_id, reference_epoch),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            CREATE TABLE IF NOT EXISTS experiment_exports (
                export_id INTEGER PRIMARY KEY AUTOINCREMENT,
                experiment_run_id TEXT NOT NULL,
                source_artifact_path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                artifact_path TEXT NOT NULL,
                filter TEXT NOT NULL,
                exported_at_utc TEXT NOT NULL,
                UNIQUE(experiment_run_id, source_artifact_path, dataset_path, artifact_path, filter),
                FOREIGN KEY(experiment_run_id) REFERENCES experiment_runs(experiment_run_id)
            );
            """);
        AddColumnIfMissing(
            connection,
            "raw_segments",
            "has_discontinuity",
            "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            connection,
            "reference_epochs",
            "locked_start_sample_index",
            "INTEGER NOT NULL DEFAULT -1");
        AddColumnIfMissing(
            connection,
            "processing_blocks",
            "quality_weight",
            "REAL NOT NULL DEFAULT 1.0");
        AddColumnIfMissing(
            connection,
            "processing_blocks",
            "accepted_frames",
            "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            connection,
            "processing_blocks",
            "rejected_frames",
            "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(
            connection,
            "experiment_runs",
            "lifecycle_state",
            "TEXT NOT NULL DEFAULT 'active'");
        AddColumnIfMissing(
            connection,
            "experiment_runs",
            "archived_at_utc",
            "TEXT NULL");
        AddColumnIfMissing(
            connection,
            "reconstruction_lane_frames",
            "source_start_sample_index",
            "INTEGER NULL");
        AddColumnIfMissing(
            connection,
            "reconstruction_lane_frames",
            "source_end_sample_index",
            "INTEGER NULL");
        AddColumnIfMissing(
            connection,
            "reconstruction_lane_frames",
            "result_hash",
            "TEXT NULL");
        AddColumnIfMissing(
            connection,
            "reconstruction_pipeline_manifests",
            "manifest_fingerprint",
            "TEXT NOT NULL DEFAULT ''");
        ExecuteNonQuery(
            connection,
            """
            UPDATE reference_epochs
            SET locked_start_sample_index = COALESCE(
                (SELECT source_start_sample_index
                 FROM processing_blocks
                 WHERE processing_blocks.experiment_run_id = reference_epochs.experiment_run_id
                   AND processing_blocks.block_number = reference_epochs.locked_block_number),
                -1)
            WHERE locked_start_sample_index < 0;
            CREATE INDEX IF NOT EXISTS idx_experiment_runs_started
                ON experiment_runs(started_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_experiment_pairings_session
                ON experiment_pairings(session_id, set_label);
            CREATE INDEX IF NOT EXISTS idx_processing_blocks_status
                ON processing_blocks(experiment_run_id, demod_status, reconstruction_status);
            CREATE INDEX IF NOT EXISTS idx_reference_epochs_block
                ON reference_epochs(experiment_run_id, locked_block_number, reference_epoch);
            CREATE INDEX IF NOT EXISTS idx_reference_epochs_sample
                ON reference_epochs(experiment_run_id, locked_start_sample_index, reference_epoch);
            CREATE INDEX IF NOT EXISTS idx_experiment_exports_run
                ON experiment_exports(experiment_run_id, exported_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_experiment_exports_source
                ON experiment_exports(source_artifact_path, dataset_path);
            CREATE INDEX IF NOT EXISTS idx_reconstruction_revisions_published
                ON reconstruction_revisions(experiment_run_id, lane, status, published_at_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_reconstruction_lane_frames_sequence
                ON reconstruction_lane_frames(experiment_run_id, lane, revision_id, sequence_number);
            """);
        SetUserVersion(connection, CurrentSchemaVersion);
        transaction.Commit();
    }

    public void UpsertSession(Guid sessionId, string name, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO experiment_sessions(session_id, name, created_at_utc)
            VALUES($session_id, $name, $created_at_utc)
            ON CONFLICT(session_id) DO UPDATE SET
                name = excluded.name,
                created_at_utc = excluded.created_at_utc;
            """,
            ("$session_id", sessionId.ToString("D")),
            ("$name", name.Trim()),
            ("$created_at_utc", Format(createdAt)));
    }

    public void UpsertPairing(Guid sessionId, EitSetPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO experiment_pairings(
                session_id, set_label, usb_device_number,
                usb_identity_key, usb_display_name, usb_vid, usb_pid, usb_location_path,
                dds_identity_key, dds_port_name, dds_display_name, dds_vid, dds_pid,
                dds_location_path, created_at_utc)
            VALUES(
                $session_id, $set_label, $usb_device_number,
                $usb_identity_key, $usb_display_name, $usb_vid, $usb_pid, $usb_location_path,
                $dds_identity_key, $dds_port_name, $dds_display_name, $dds_vid, $dds_pid,
                $dds_location_path, $created_at_utc)
            ON CONFLICT(session_id, set_label) DO UPDATE SET
                usb_device_number = excluded.usb_device_number,
                usb_identity_key = excluded.usb_identity_key,
                usb_display_name = excluded.usb_display_name,
                usb_vid = excluded.usb_vid,
                usb_pid = excluded.usb_pid,
                usb_location_path = excluded.usb_location_path,
                dds_identity_key = excluded.dds_identity_key,
                dds_port_name = excluded.dds_port_name,
                dds_display_name = excluded.dds_display_name,
                dds_vid = excluded.dds_vid,
                dds_pid = excluded.dds_pid,
                dds_location_path = excluded.dds_location_path,
                created_at_utc = excluded.created_at_utc;
            """,
            ("$session_id", sessionId.ToString("D")),
            ("$set_label", pairing.Label),
            ("$usb_device_number", pairing.Usb2070DeviceNumber),
            ("$usb_identity_key", pairing.Usb2070Candidate.IdentityKey),
            ("$usb_display_name", pairing.Usb2070Candidate.DisplayName),
            ("$usb_vid", pairing.Usb2070Candidate.Vid),
            ("$usb_pid", pairing.Usb2070Candidate.Pid),
            ("$usb_location_path", pairing.Usb2070Candidate.LocationPath),
            ("$dds_identity_key", pairing.DdsSerialCandidate.IdentityKey),
            ("$dds_port_name", pairing.DdsSerialCandidate.PortName),
            ("$dds_display_name", pairing.DdsSerialCandidate.DisplayName),
            ("$dds_vid", pairing.DdsSerialCandidate.Vid),
            ("$dds_pid", pairing.DdsSerialCandidate.Pid),
            ("$dds_location_path", pairing.DdsSerialCandidate.LocationPath),
            ("$created_at_utc", Format(pairing.CreatedAt)));
    }

    public void SaveRunConfig(ExperimentRunConfigRecord config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO experiment_run_configs(experiment_run_id, config_json, updated_at_utc)
            VALUES($experiment_run_id, $config_json, $updated_at_utc)
            ON CONFLICT(experiment_run_id) DO UPDATE SET
                config_json = excluded.config_json,
                updated_at_utc = excluded.updated_at_utc;
            """,
            ("$experiment_run_id", config.ExperimentRunId.ToString("D")),
            ("$config_json", JsonSerializer.Serialize(config)),
            ("$updated_at_utc", Format(DateTimeOffset.UtcNow)));
    }

    public ExperimentRunConfigRecord? GetRunConfig(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT config_json
            FROM experiment_run_configs
            WHERE experiment_run_id = $experiment_run_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        var json = command.ExecuteScalar() as string;
        return json is null
            ? null
            : JsonSerializer.Deserialize<ExperimentRunConfigRecord>(json)
              ?? throw new InvalidDataException("Experiment run config JSON is invalid.");
    }

    public void SavePipelineManifest(ReconstructionPipelineManifestCatalogRecord manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ExperimentRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(manifest.SchemaVersion) ||
            string.IsNullOrWhiteSpace(manifest.AlgorithmFingerprint) ||
            string.IsNullOrWhiteSpace(manifest.ManifestFingerprint) ||
            string.IsNullOrWhiteSpace(manifest.Status) ||
            string.IsNullOrWhiteSpace(manifest.ManifestJson))
        {
            throw new ArgumentException("Pipeline manifest is invalid.", nameof(manifest));
        }

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO reconstruction_pipeline_manifests(
                experiment_run_id, schema_version, algorithm_fingerprint, manifest_fingerprint,
                status, manifest_json, created_at_utc, updated_at_utc, unavailable_reason)
            VALUES(
                $experiment_run_id, $schema_version, $algorithm_fingerprint, $manifest_fingerprint,
                $status, $manifest_json, $created_at_utc, $updated_at_utc, $unavailable_reason)
            ON CONFLICT(experiment_run_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                algorithm_fingerprint = excluded.algorithm_fingerprint,
                manifest_fingerprint = excluded.manifest_fingerprint,
                status = excluded.status,
                manifest_json = excluded.manifest_json,
                updated_at_utc = excluded.updated_at_utc,
                unavailable_reason = excluded.unavailable_reason;
            """,
            ("$experiment_run_id", manifest.ExperimentRunId.ToString("D")),
            ("$schema_version", manifest.SchemaVersion),
            ("$algorithm_fingerprint", manifest.AlgorithmFingerprint),
            ("$manifest_fingerprint", manifest.ManifestFingerprint),
            ("$status", manifest.Status),
            ("$manifest_json", manifest.ManifestJson),
            ("$created_at_utc", Format(manifest.CreatedAt)),
            ("$updated_at_utc", Format(manifest.UpdatedAt)),
            ("$unavailable_reason", manifest.UnavailableReason));
    }

    public ReconstructionPipelineManifestCatalogRecord? GetPipelineManifest(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, schema_version, algorithm_fingerprint, manifest_fingerprint,
                   status, manifest_json, created_at_utc, updated_at_utc, unavailable_reason
            FROM reconstruction_pipeline_manifests
            WHERE experiment_run_id = $experiment_run_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ReconstructionPipelineManifestCatalogRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                Parse(reader.GetString(6)),
                Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    public OfflinePipelineReadiness GetOfflinePipelineReadiness(Guid experimentRunId) =>
        ReconstructionPipelineManifestCodec.EvaluateForOffline(GetPipelineManifest(experimentRunId));

    public void UpsertReconstructionRevision(ReconstructionRevisionCatalogRecord revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ValidateReconstructionRevision(revision);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = GetReconstructionRevision(
            connection,
            revision.ExperimentRunId,
            revision.Lane,
            revision.RevisionId);
        if (existing?.IsPublished == true && existing != revision)
        {
            throw new InvalidOperationException("Published reconstruction revisions are immutable.");
        }

        ExecuteNonQuery(
            connection,
            """
            INSERT INTO reconstruction_revisions(
                experiment_run_id, lane, revision_id, status, algorithm_fingerprint,
                raw_denominator, demod_denominator, terminal_outcome_count,
                reconstructed_count, neutral_count, excluded_count,
                estimated_incremental_bytes, created_at_utc, updated_at_utc,
                published_at_utc, failure_message)
            VALUES(
                $experiment_run_id, $lane, $revision_id, $status, $algorithm_fingerprint,
                $raw_denominator, $demod_denominator, $terminal_outcome_count,
                $reconstructed_count, $neutral_count, $excluded_count,
                $estimated_incremental_bytes, $created_at_utc, $updated_at_utc,
                $published_at_utc, $failure_message)
            ON CONFLICT(experiment_run_id, lane, revision_id) DO UPDATE SET
                status = excluded.status,
                algorithm_fingerprint = excluded.algorithm_fingerprint,
                raw_denominator = excluded.raw_denominator,
                demod_denominator = excluded.demod_denominator,
                terminal_outcome_count = excluded.terminal_outcome_count,
                reconstructed_count = excluded.reconstructed_count,
                neutral_count = excluded.neutral_count,
                excluded_count = excluded.excluded_count,
                estimated_incremental_bytes = excluded.estimated_incremental_bytes,
                updated_at_utc = excluded.updated_at_utc,
                published_at_utc = excluded.published_at_utc,
                failure_message = excluded.failure_message;
            """,
            ("$experiment_run_id", revision.ExperimentRunId.ToString("D")),
            ("$lane", revision.Lane),
            ("$revision_id", revision.RevisionId),
            ("$status", revision.Status),
            ("$algorithm_fingerprint", revision.AlgorithmFingerprint),
            ("$raw_denominator", revision.RawDenominator),
            ("$demod_denominator", revision.DemodDenominator),
            ("$terminal_outcome_count", revision.TerminalOutcomeCount),
            ("$reconstructed_count", revision.ReconstructedCount),
            ("$neutral_count", revision.NeutralCount),
            ("$excluded_count", revision.ExcludedCount),
            ("$estimated_incremental_bytes", revision.EstimatedIncrementalBytes),
            ("$created_at_utc", Format(revision.CreatedAt)),
            ("$updated_at_utc", Format(revision.UpdatedAt)),
            ("$published_at_utc", revision.PublishedAt is { } publishedAt ? Format(publishedAt) : null),
            ("$failure_message", revision.FailureMessage));
        transaction.Commit();
    }

    public ReconstructionRevisionCatalogRecord? GetReconstructionRevision(
        Guid experimentRunId,
        string lane,
        string revisionId)
    {
        ValidateLaneIdentity(lane, revisionId);
        using var connection = OpenConnection();
        return GetReconstructionRevision(connection, experimentRunId, lane, revisionId);
    }

    public IReadOnlyList<ReconstructionRevisionCatalogRecord> ListReconstructionRevisions(
        Guid experimentRunId,
        string? lane = null)
    {
        if (lane is not null && !ReconstructionLane.IsKnown(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown reconstruction lane.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, lane, revision_id, status, algorithm_fingerprint,
                   raw_denominator, demod_denominator, terminal_outcome_count,
                   reconstructed_count, neutral_count, excluded_count,
                   estimated_incremental_bytes, created_at_utc, updated_at_utc,
                   published_at_utc, failure_message
            FROM reconstruction_revisions
            WHERE experiment_run_id = $experiment_run_id
              AND ($lane IS NULL OR lane = $lane)
            ORDER BY COALESCE(published_at_utc, updated_at_utc) DESC, revision_id DESC;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", (object?)lane ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var revisions = new List<ReconstructionRevisionCatalogRecord>();
        while (reader.Read())
        {
            revisions.Add(ReadReconstructionRevision(reader));
        }

        return revisions;
    }

    public ReconstructionRevisionCatalogRecord? GetPublishedReconstructionRevision(
        Guid experimentRunId,
        string lane)
    {
        if (!ReconstructionLane.IsKnown(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown reconstruction lane.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, lane, revision_id, status, algorithm_fingerprint,
                   raw_denominator, demod_denominator, terminal_outcome_count,
                   reconstructed_count, neutral_count, excluded_count,
                   estimated_incremental_bytes, created_at_utc, updated_at_utc,
                   published_at_utc, failure_message
            FROM reconstruction_revisions
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND status = $published
            ORDER BY published_at_utc DESC, revision_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$published", ReconstructionRevisionStatus.Published);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReconstructionRevision(reader) : null;
    }

    public void RecordReconstructionLaneFrame(ReconstructionLaneFrameCatalogRecord frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateReconstructionLaneFrame(frame);
        if (frame.ArtifactPath is not null)
        {
            ValidateRelativeArtifactPath(frame.ArtifactPath);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = GetReconstructionRevision(
            connection,
            frame.ExperimentRunId,
            frame.Lane,
            frame.RevisionId) ?? throw new KeyNotFoundException(
                $"Reconstruction revision {frame.Lane}/{frame.RevisionId} does not exist.");
        if (revision.IsPublished)
        {
            throw new InvalidOperationException("Published reconstruction revision frames are immutable.");
        }

        if (!string.Equals(
                revision.AlgorithmFingerprint,
                frame.AlgorithmFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Frame algorithm fingerprint does not match its revision.");
        }

        ExecuteNonQuery(
            connection,
            """
            INSERT INTO reconstruction_lane_frames(
                experiment_run_id, lane, revision_id, source_block_number,
                sequence_number, outcome, acquired_at_utc, processed_at_utc,
                algorithm_fingerprint, artifact_path, dataset_path, final_weight_hash,
                kalman_session_id, kalman_disposition, presentation_json, exclusion_reason,
                source_start_sample_index, source_end_sample_index, result_hash)
            VALUES(
                $experiment_run_id, $lane, $revision_id, $source_block_number,
                $sequence_number, $outcome, $acquired_at_utc, $processed_at_utc,
                $algorithm_fingerprint, $artifact_path, $dataset_path, $final_weight_hash,
                $kalman_session_id, $kalman_disposition, $presentation_json, $exclusion_reason,
                $source_start_sample_index, $source_end_sample_index, $result_hash)
            ON CONFLICT(experiment_run_id, lane, revision_id, source_block_number) DO UPDATE SET
                sequence_number = excluded.sequence_number,
                outcome = excluded.outcome,
                acquired_at_utc = excluded.acquired_at_utc,
                processed_at_utc = excluded.processed_at_utc,
                algorithm_fingerprint = excluded.algorithm_fingerprint,
                artifact_path = excluded.artifact_path,
                dataset_path = excluded.dataset_path,
                final_weight_hash = excluded.final_weight_hash,
                kalman_session_id = excluded.kalman_session_id,
                kalman_disposition = excluded.kalman_disposition,
                presentation_json = excluded.presentation_json,
                exclusion_reason = excluded.exclusion_reason,
                source_start_sample_index = excluded.source_start_sample_index,
                source_end_sample_index = excluded.source_end_sample_index,
                result_hash = excluded.result_hash;
            """,
            ("$experiment_run_id", frame.ExperimentRunId.ToString("D")),
            ("$lane", frame.Lane),
            ("$revision_id", frame.RevisionId),
            ("$source_block_number", frame.SourceBlockNumber),
            ("$sequence_number", frame.SequenceNumber),
            ("$outcome", frame.Outcome),
            ("$acquired_at_utc", Format(frame.AcquiredAt)),
            ("$processed_at_utc", Format(frame.ProcessedAt)),
            ("$algorithm_fingerprint", frame.AlgorithmFingerprint),
            ("$artifact_path", frame.ArtifactPath),
            ("$dataset_path", frame.DatasetPath),
            ("$final_weight_hash", frame.FinalWeightHash),
            ("$kalman_session_id", frame.KalmanSessionId),
            ("$kalman_disposition", frame.KalmanDisposition),
            ("$presentation_json", frame.PresentationJson),
            ("$exclusion_reason", frame.ExclusionReason),
            ("$source_start_sample_index", frame.SourceStartSampleIndex),
            ("$source_end_sample_index", frame.SourceEndSampleIndex),
            ("$result_hash", frame.ResultHash));
        RefreshReconstructionRevisionCounts(
            connection,
            frame.ExperimentRunId,
            frame.Lane,
            frame.RevisionId,
            frame.ProcessedAt);
        transaction.Commit();
    }

    public IReadOnlyList<ReconstructionLaneFrameCatalogRecord> ListReconstructionLaneFrames(
        Guid experimentRunId,
        string lane,
        string revisionId)
    {
        ValidateLaneIdentity(lane, revisionId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, lane, revision_id, source_block_number,
                   sequence_number, outcome, acquired_at_utc, processed_at_utc,
                   algorithm_fingerprint, artifact_path, dataset_path, final_weight_hash,
                   kalman_session_id, kalman_disposition, presentation_json, exclusion_reason,
                   source_start_sample_index, source_end_sample_index, result_hash
            FROM reconstruction_lane_frames
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id
            ORDER BY sequence_number, source_block_number;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$revision_id", revisionId);
        using var reader = command.ExecuteReader();
        var frames = new List<ReconstructionLaneFrameCatalogRecord>();
        while (reader.Read())
        {
            frames.Add(ReadReconstructionLaneFrame(reader));
        }

        return frames;
    }

    public ReconstructionLaneFrameCatalogRecord? GetReconstructionLaneFrame(
        Guid experimentRunId,
        string lane,
        string revisionId,
        int sourceBlockNumber)
    {
        ValidateLaneIdentity(lane, revisionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceBlockNumber);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, lane, revision_id, source_block_number,
                   sequence_number, outcome, acquired_at_utc, processed_at_utc,
                   algorithm_fingerprint, artifact_path, dataset_path, final_weight_hash,
                   kalman_session_id, kalman_disposition, presentation_json, exclusion_reason,
                   source_start_sample_index, source_end_sample_index, result_hash
            FROM reconstruction_lane_frames
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id
              AND source_block_number = $source_block_number;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$revision_id", revisionId);
        command.Parameters.AddWithValue("$source_block_number", sourceBlockNumber);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReconstructionLaneFrame(reader) : null;
    }

    public ReconstructionRevisionCatalogRecord PublishReconstructionRevision(
        Guid experimentRunId,
        string lane,
        string revisionId,
        long rawDenominator,
        int demodDenominator,
        DateTimeOffset publishedAt)
    {
        ValidateLaneIdentity(lane, revisionId);
        ArgumentOutOfRangeException.ThrowIfNegative(rawDenominator);
        ArgumentOutOfRangeException.ThrowIfNegative(demodDenominator);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = GetReconstructionRevision(connection, experimentRunId, lane, revisionId)
            ?? throw new KeyNotFoundException($"Reconstruction revision {lane}/{revisionId} does not exist.");
        if (revision.IsPublished)
        {
            return revision;
        }

        var counts = ReadReconstructionLaneCounts(connection, experimentRunId, lane, revisionId);
        if (counts.Terminal != demodDenominator ||
            counts.Terminal != counts.Reconstructed + counts.Neutral + counts.Excluded)
        {
            throw new InvalidOperationException(
                $"Revision cannot publish before complete coverage: {counts.Terminal}/{demodDenominator} terminal outcomes.");
        }

        ExecuteNonQuery(
            connection,
            """
            UPDATE reconstruction_revisions
            SET status = $published,
                raw_denominator = $raw_denominator,
                demod_denominator = $demod_denominator,
                terminal_outcome_count = $terminal_outcome_count,
                reconstructed_count = $reconstructed_count,
                neutral_count = $neutral_count,
                excluded_count = $excluded_count,
                updated_at_utc = $published_at_utc,
                published_at_utc = $published_at_utc,
                failure_message = NULL
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id;
            """,
            ("$published", ReconstructionRevisionStatus.Published),
            ("$raw_denominator", rawDenominator),
            ("$demod_denominator", demodDenominator),
            ("$terminal_outcome_count", counts.Terminal),
            ("$reconstructed_count", counts.Reconstructed),
            ("$neutral_count", counts.Neutral),
            ("$excluded_count", counts.Excluded),
            ("$published_at_utc", Format(publishedAt)),
            ("$experiment_run_id", experimentRunId.ToString("D")),
            ("$lane", lane),
            ("$revision_id", revisionId));
        transaction.Commit();
        return GetReconstructionRevision(experimentRunId, lane, revisionId)!;
    }

    public void PromoteReconstructionRevisionArtifacts(
        Guid experimentRunId,
        string lane,
        string revisionId,
        string stagingRelativeDirectory,
        string publishedRelativeDirectory,
        DateTimeOffset updatedAt)
    {
        ValidateLaneIdentity(lane, revisionId);
        ValidateRelativeArtifactPath(stagingRelativeDirectory);
        ValidateRelativeArtifactPath(publishedRelativeDirectory);
        var stagingPrefix = stagingRelativeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        var publishedPrefix = publishedRelativeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var revision = GetReconstructionRevision(connection, experimentRunId, lane, revisionId)
            ?? throw new KeyNotFoundException($"Reconstruction revision {lane}/{revisionId} does not exist.");
        if (revision.IsPublished)
        {
            transaction.Commit();
            return;
        }

        ExecuteNonQuery(
            connection,
            """
            UPDATE reconstruction_lane_frames
            SET artifact_path = $published_prefix || substr(artifact_path, length($staging_prefix) + 1),
                processed_at_utc = $updated_at_utc
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id
              AND artifact_path LIKE $staging_like;
            """,
            ("$published_prefix", publishedPrefix),
            ("$staging_prefix", stagingPrefix),
            ("$staging_like", stagingPrefix + "%"),
            ("$updated_at_utc", Format(updatedAt)),
            ("$experiment_run_id", experimentRunId.ToString("D")),
            ("$lane", lane),
            ("$revision_id", revisionId));
        transaction.Commit();
    }

    public void SetReconstructionRevisionStatus(
        Guid experimentRunId,
        string lane,
        string revisionId,
        string status,
        DateTimeOffset updatedAt,
        string? failureMessage = null)
    {
        ValidateLaneIdentity(lane, revisionId);
        if (!ReconstructionRevisionStatus.IsKnown(status) ||
            string.Equals(status, ReconstructionRevisionStatus.Published, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Use publish for published revisions.");
        }

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            UPDATE reconstruction_revisions
            SET status = $status,
                updated_at_utc = $updated_at_utc,
                failure_message = $failure_message
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id
              AND status <> $published;
            """,
            ("$status", status),
            ("$updated_at_utc", Format(updatedAt)),
            ("$failure_message", failureMessage),
            ("$experiment_run_id", experimentRunId.ToString("D")),
            ("$lane", lane),
            ("$revision_id", revisionId),
            ("$published", ReconstructionRevisionStatus.Published));
    }

    public void RegisterReferenceEpoch(ExperimentReferenceEpochCatalogRecord epoch)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ValidateRelativeArtifactPath(epoch.ArtifactPath);
        if (epoch.ReferenceEpoch <= 0 ||
            epoch.LockedBlockNumber < 0 ||
            epoch.LockedStartSampleIndex < 0)
        {
            throw new ArgumentException("Reference epoch identity is invalid.", nameof(epoch));
        }

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO reference_epochs(
                experiment_run_id, reference_epoch, locked_block_number, locked_start_sample_index, locked_at_utc,
                lock_kind, artifact_path, dataset_path, created_at_utc)
            VALUES(
                $experiment_run_id, $reference_epoch, $locked_block_number, $locked_start_sample_index, $locked_at_utc,
                $lock_kind, $artifact_path, $dataset_path, $created_at_utc)
            ON CONFLICT(experiment_run_id, reference_epoch) DO UPDATE SET
                locked_block_number = excluded.locked_block_number,
                locked_start_sample_index = excluded.locked_start_sample_index,
                locked_at_utc = excluded.locked_at_utc,
                lock_kind = excluded.lock_kind,
                artifact_path = excluded.artifact_path,
                dataset_path = excluded.dataset_path,
                created_at_utc = excluded.created_at_utc;
            """,
            ("$experiment_run_id", epoch.ExperimentRunId.ToString("D")),
            ("$reference_epoch", epoch.ReferenceEpoch),
            ("$locked_block_number", epoch.LockedBlockNumber),
            ("$locked_start_sample_index", epoch.LockedStartSampleIndex),
            ("$locked_at_utc", Format(epoch.LockedAt)),
            ("$lock_kind", epoch.LockKind),
            ("$artifact_path", epoch.ArtifactPath),
            ("$dataset_path", epoch.DatasetPath),
            ("$created_at_utc", Format(epoch.CreatedAt)));
    }

    public IReadOnlyList<ExperimentReferenceEpochCatalogRecord> ListReferenceEpochs(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, reference_epoch, locked_block_number, locked_start_sample_index, locked_at_utc,
                   lock_kind, artifact_path, dataset_path, created_at_utc
            FROM reference_epochs
            WHERE experiment_run_id = $experiment_run_id
            ORDER BY locked_start_sample_index, reference_epoch;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var epochs = new List<ExperimentReferenceEpochCatalogRecord>();
        while (reader.Read())
        {
            epochs.Add(new ExperimentReferenceEpochCatalogRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                Parse(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                Parse(reader.GetString(8)),
                reader.GetInt64(3)));
        }

        return epochs;
    }

    public void BeginRun(ExperimentRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        ValidateRelativeArtifactPath(run.RunDirectory);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO experiment_runs(
                experiment_run_id, session_id, set_label, started_at_utc, ended_at_utc,
                status, storage_mode, run_directory, raw_status, demod_status,
                reconstruction_status, failure_message, lifecycle_state, archived_at_utc)
            VALUES(
                $experiment_run_id, $session_id, $set_label, $started_at_utc, NULL,
                $status, $storage_mode, $run_directory, $raw_status, $demod_status,
                $reconstruction_status, NULL, $lifecycle_state, NULL)
            ON CONFLICT(experiment_run_id) DO UPDATE SET
                session_id = excluded.session_id,
                set_label = excluded.set_label,
                started_at_utc = excluded.started_at_utc,
                ended_at_utc = NULL,
                status = excluded.status,
                storage_mode = excluded.storage_mode,
                run_directory = excluded.run_directory,
                raw_status = excluded.raw_status,
                demod_status = excluded.demod_status,
                reconstruction_status = excluded.reconstruction_status,
                failure_message = NULL,
                lifecycle_state = excluded.lifecycle_state,
                archived_at_utc = NULL;
            """,
            ("$experiment_run_id", run.ExperimentRunId.ToString("D")),
            ("$session_id", run.SessionId.ToString("D")),
            ("$set_label", run.SetLabel),
            ("$started_at_utc", Format(run.StartedAt)),
            ("$status", RecordingStatus),
            ("$storage_mode", run.StorageMode),
            ("$run_directory", run.RunDirectory),
            ("$raw_status", run.RawStatus),
            ("$demod_status", run.DemodStatus),
            ("$reconstruction_status", run.ReconstructionStatus),
            ("$lifecycle_state", ActiveLifecycleState));
    }

    public void EndRun(Guid experimentRunId, DateTimeOffset endedAt, string status, string? failureMessage = null)
    {
        ValidateTerminalStatus(status);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            UPDATE experiment_runs
            SET ended_at_utc = $ended_at_utc,
                status = $status,
                failure_message = $failure_message
            WHERE experiment_run_id = $experiment_run_id;
            """,
            ("$ended_at_utc", Format(endedAt)),
            ("$status", status),
            ("$failure_message", failureMessage),
            ("$experiment_run_id", experimentRunId.ToString("D")));
    }

    public void RegisterRawSegment(RawSegmentCatalogRecord segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ValidateRelativeArtifactPath(segment.ArtifactPath);
        if (segment.StartSampleIndex < 0 ||
            segment.EndSampleIndex <= segment.StartSampleIndex ||
            segment.SampleRows != segment.EndSampleIndex - segment.StartSampleIndex)
        {
            throw new ArgumentException("Raw segment sample range is invalid.", nameof(segment));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureRawSegmentIdentity(connection, segment);
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO raw_segments(
                experiment_run_id, segment_sequence, artifact_path, dataset_path,
                start_sample_index, end_sample_index, sample_rows, channel_count,
                captured_at_utc, status, has_discontinuity)
            VALUES(
                $experiment_run_id, $segment_sequence, $artifact_path, $dataset_path,
                $start_sample_index, $end_sample_index, $sample_rows, $channel_count,
                $captured_at_utc, $status, $has_discontinuity)
            ON CONFLICT(experiment_run_id, segment_sequence) DO UPDATE SET
                end_sample_index = excluded.end_sample_index,
                sample_rows = excluded.sample_rows,
                status = excluded.status,
                has_discontinuity = excluded.has_discontinuity;
            """,
            ("$experiment_run_id", segment.ExperimentRunId.ToString("D")),
            ("$segment_sequence", segment.SegmentSequence),
            ("$artifact_path", segment.ArtifactPath),
            ("$dataset_path", segment.DatasetPath),
            ("$start_sample_index", segment.StartSampleIndex),
            ("$end_sample_index", segment.EndSampleIndex),
            ("$sample_rows", segment.SampleRows),
            ("$channel_count", segment.ChannelCount),
            ("$captured_at_utc", Format(segment.CapturedAt)),
            ("$status", segment.Status),
            ("$has_discontinuity", segment.HasDiscontinuity ? 1 : 0));
        ExecuteNonQuery(
            connection,
            """
            UPDATE experiment_runs
            SET raw_status = 'recording'
            WHERE experiment_run_id = $experiment_run_id;
            """,
            ("$experiment_run_id", segment.ExperimentRunId.ToString("D")));
        transaction.Commit();
    }

    private static void EnsureRawSegmentIdentity(
        SqliteConnection connection,
        RawSegmentCatalogRecord segment)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT artifact_path, dataset_path, start_sample_index, channel_count, captured_at_utc
            FROM raw_segments
            WHERE experiment_run_id = $experiment_run_id
              AND segment_sequence = $segment_sequence;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", segment.ExperimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$segment_sequence", segment.SegmentSequence);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        if (!string.Equals(reader.GetString(0), segment.ArtifactPath, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), segment.DatasetPath, StringComparison.Ordinal) ||
            reader.GetInt64(2) != segment.StartSampleIndex ||
            reader.GetInt32(3) != segment.ChannelCount ||
            Parse(reader.GetString(4)) != segment.CapturedAt)
        {
            throw new InvalidOperationException(
                $"Raw shard identity conflict for run {segment.ExperimentRunId:D}, " +
                $"sequence {segment.SegmentSequence}.");
        }
    }

    private static void EnsureProcessingBlockIdentity(
        SqliteConnection connection,
        ProcessingBlockCatalogRecord block)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_start_sample_index, source_end_sample_index
            FROM processing_blocks
            WHERE experiment_run_id = $experiment_run_id
              AND block_number = $block_number;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", block.ExperimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$block_number", block.BlockNumber);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var existingStart = reader.GetInt64(0);
        var existingEnd = reader.GetInt64(1);
        if (existingStart != block.SourceStartSampleIndex || existingEnd != block.SourceEndSampleIndex)
        {
            throw new InvalidOperationException(
                $"Processing block sample identity conflict for run {block.ExperimentRunId:D}, " +
                $"block {block.BlockNumber}: existing [{existingStart},{existingEnd}), " +
                $"incoming [{block.SourceStartSampleIndex},{block.SourceEndSampleIndex}).");
        }
    }

    public IReadOnlyList<RawSegmentCatalogRecord> ListRawSegments(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, segment_sequence, artifact_path, dataset_path,
                   start_sample_index, end_sample_index, sample_rows, channel_count,
                   captured_at_utc, status, has_discontinuity
            FROM raw_segments
            WHERE experiment_run_id = $experiment_run_id
            ORDER BY segment_sequence;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var segments = new List<RawSegmentCatalogRecord>();
        while (reader.Read())
        {
            segments.Add(new RawSegmentCatalogRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                Parse(reader.GetString(8)),
                reader.GetString(9),
                reader.GetInt32(10) != 0));
        }

        return segments;
    }

    public RawSegmentCatalogRecord? FindRawSegmentByArtifactPath(string artifactPath)
    {
        ValidateRelativeArtifactPath(artifactPath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, segment_sequence, artifact_path, dataset_path,
                   start_sample_index, end_sample_index, sample_rows, channel_count,
                   captured_at_utc, status, has_discontinuity
            FROM raw_segments
            WHERE artifact_path = $artifact_path
            ORDER BY captured_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$artifact_path", artifactPath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRawSegment(reader) : null;
    }

    public Guid? FindRunIdByArtifactPath(string artifactPath)
    {
        ValidateRelativeArtifactPath(artifactPath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id
            FROM (
                SELECT experiment_run_id, artifact_path FROM raw_segments
                UNION ALL
                SELECT experiment_run_id, artifact_path FROM derived_artifacts
                UNION ALL
                SELECT experiment_run_id, artifact_path FROM reference_epochs
            )
            WHERE artifact_path = $artifact_path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$artifact_path", artifactPath);
        return command.ExecuteScalar() is string runId ? Guid.Parse(runId) : null;
    }

    public void RegisterExport(ExperimentExportCatalogRecord export)
    {
        ArgumentNullException.ThrowIfNull(export);
        ArgumentException.ThrowIfNullOrWhiteSpace(export.DatasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(export.Filter);
        ValidateRelativeArtifactPath(export.SourceArtifactPath);
        ValidateRelativeArtifactPath(export.ArtifactPath);
        var run = GetRun(export.ExperimentRunId)
                  ?? throw new InvalidOperationException("Experiment run is not registered.");
        ValidateExportPath(run, export.ArtifactPath);

        using var connection = OpenConnection();
        if (!IsRegisteredArtifact(connection, export.ExperimentRunId, export.SourceArtifactPath))
        {
            throw new InvalidOperationException("Export source is not registered in the canonical catalog.");
        }

        ExecuteNonQuery(
            connection,
            """
            INSERT INTO experiment_exports(
                experiment_run_id, source_artifact_path, dataset_path,
                artifact_path, filter, exported_at_utc)
            VALUES(
                $experiment_run_id, $source_artifact_path, $dataset_path,
                $artifact_path, $filter, $exported_at_utc)
            ON CONFLICT(experiment_run_id, source_artifact_path, dataset_path, artifact_path, filter)
            DO UPDATE SET exported_at_utc = excluded.exported_at_utc;
            """,
            ("$experiment_run_id", export.ExperimentRunId.ToString("D")),
            ("$source_artifact_path", export.SourceArtifactPath),
            ("$dataset_path", export.DatasetPath),
            ("$artifact_path", export.ArtifactPath),
            ("$filter", export.Filter),
            ("$exported_at_utc", Format(export.ExportedAt)));
    }

    public IReadOnlyList<ExperimentExportCatalogRecord> ListExports(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, source_artifact_path, dataset_path,
                   artifact_path, filter, exported_at_utc
            FROM experiment_exports
            WHERE experiment_run_id = $experiment_run_id
            ORDER BY exported_at_utc, export_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var exports = new List<ExperimentExportCatalogRecord>();
        while (reader.Read())
        {
            exports.Add(new ExperimentExportCatalogRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                Parse(reader.GetString(5))));
        }

        return exports;
    }

    public void SetRunStageStatuses(
        Guid experimentRunId,
        string rawStatus,
        string demodStatus,
        string reconstructionStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(demodStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(reconstructionStatus);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            UPDATE experiment_runs
            SET raw_status = $raw_status,
                demod_status = $demod_status,
                reconstruction_status = $reconstruction_status
            WHERE experiment_run_id = $experiment_run_id;
            """,
            ("$raw_status", rawStatus),
            ("$demod_status", demodStatus),
            ("$reconstruction_status", reconstructionStatus),
            ("$experiment_run_id", experimentRunId.ToString("D")));
    }

    public void RecordDemodulatedBlock(
        ProcessingBlockCatalogRecord block,
        DerivedArtifactCatalogRecord? artifact = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ValidateProcessingBlock(block);
        if (artifact is not null)
        {
            ValidateDerivedArtifact(block.ExperimentRunId, block.BlockNumber, artifact);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureProcessingBlockIdentity(connection, block);
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO processing_blocks(
                experiment_run_id, block_number, source_start_sample_index,
                source_end_sample_index, acquired_at_utc, demod_processed_at_utc,
                demod_status, reconstruction_processed_at_utc, reconstruction_status,
                failure_message, quality_weight, accepted_frames, rejected_frames)
            VALUES(
                $experiment_run_id, $block_number, $source_start_sample_index,
                $source_end_sample_index, $acquired_at_utc, $demod_processed_at_utc,
                $demod_status, NULL, 'pending', $failure_message,
                $quality_weight, $accepted_frames, $rejected_frames)
            ON CONFLICT(experiment_run_id, block_number) DO UPDATE SET
                source_start_sample_index = excluded.source_start_sample_index,
                source_end_sample_index = excluded.source_end_sample_index,
                acquired_at_utc = excluded.acquired_at_utc,
                demod_processed_at_utc = excluded.demod_processed_at_utc,
                demod_status = excluded.demod_status,
                failure_message = excluded.failure_message,
                quality_weight = excluded.quality_weight,
                accepted_frames = excluded.accepted_frames,
                rejected_frames = excluded.rejected_frames;
            """,
            ("$experiment_run_id", block.ExperimentRunId.ToString("D")),
            ("$block_number", block.BlockNumber),
            ("$source_start_sample_index", block.SourceStartSampleIndex),
            ("$source_end_sample_index", block.SourceEndSampleIndex),
            ("$acquired_at_utc", Format(block.AcquiredAt)),
            ("$demod_processed_at_utc", Format(block.DemodProcessedAt)),
            ("$demod_status", block.DemodStatus),
            ("$failure_message", block.FailureMessage),
            ("$quality_weight", block.QualityWeight),
            ("$accepted_frames", block.AcceptedFrameCount),
            ("$rejected_frames", block.RejectedFrameCount));
        if (artifact is not null)
        {
            UpsertDerivedArtifact(connection, artifact);
        }

        transaction.Commit();
    }

    public void RecordReconstructionOutcome(
        ProcessingBlockCatalogRecord block,
        string reconstructionStatus,
        DateTimeOffset processedAt,
        DerivedArtifactCatalogRecord? artifact = null,
        string? failureMessage = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(reconstructionStatus);
        ValidateProcessingBlock(block);
        if (artifact is not null)
        {
            ValidateDerivedArtifact(block.ExperimentRunId, block.BlockNumber, artifact);
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        EnsureProcessingBlockIdentity(connection, block);
        RecordDemodPlaceholder(connection, block);
        ExecuteNonQuery(
            connection,
            """
            UPDATE processing_blocks
            SET reconstruction_processed_at_utc = $processed_at_utc,
                reconstruction_status = $reconstruction_status,
                failure_message = $failure_message
            WHERE experiment_run_id = $experiment_run_id AND block_number = $block_number;
            """,
            ("$processed_at_utc", Format(processedAt)),
            ("$reconstruction_status", reconstructionStatus),
            ("$failure_message", failureMessage),
            ("$experiment_run_id", block.ExperimentRunId.ToString("D")),
            ("$block_number", block.BlockNumber));
        if (artifact is not null)
        {
            UpsertDerivedArtifact(connection, artifact);
        }

        transaction.Commit();
    }

    public void RegisterDerivedArtifact(DerivedArtifactCatalogRecord artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateRelativeArtifactPath(artifact.ArtifactPath);
        using var connection = OpenConnection();
        UpsertDerivedArtifact(connection, artifact);
    }

    public ExperimentCoverageSummary GetCoverage(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COALESCE((SELECT SUM(sample_rows) FROM raw_segments WHERE experiment_run_id = $run_id AND status = 'ready'), 0),
                COALESCE((SELECT COUNT(*) FROM raw_segments WHERE experiment_run_id = $run_id AND status = 'ready'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND demod_status = 'ready'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND demod_status = 'failed'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND reconstruction_status = 'ready'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND reconstruction_status = 'pending'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND reconstruction_status = 'failed'), 0),
                COALESCE((SELECT COUNT(*) FROM processing_blocks WHERE experiment_run_id = $run_id AND reconstruction_status = 'not_applicable'), 0),
                COALESCE((SELECT COUNT(*) FROM experiment_exports WHERE experiment_run_id = $run_id), 0),
                COALESCE((SELECT COUNT(DISTINCT source_artifact_path) FROM experiment_exports WHERE experiment_run_id = $run_id AND dataset_path = '/raw/adc_counts' AND filter = 'all'), 0);
            """;
        command.Parameters.AddWithValue("$run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return ExperimentCoverageSummary.Empty;
        }

        return new ExperimentCoverageSummary(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10));
    }

    public IReadOnlyList<ProcessingBlockCatalogRecord> ListProcessingBlocks(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, block_number, source_start_sample_index,
                   source_end_sample_index, acquired_at_utc, demod_processed_at_utc,
                   demod_status, failure_message, reconstruction_processed_at_utc,
                   reconstruction_status, quality_weight, accepted_frames, rejected_frames
            FROM processing_blocks
            WHERE experiment_run_id = $experiment_run_id
            ORDER BY source_start_sample_index, block_number;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var blocks = new List<ProcessingBlockCatalogRecord>();
        while (reader.Read())
        {
            blocks.Add(ReadProcessingBlock(reader));
        }

        return blocks;
    }

    public ProcessingBlockCatalogRecord? GetProcessingBlock(Guid experimentRunId, int blockNumber)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, block_number, source_start_sample_index,
                   source_end_sample_index, acquired_at_utc, demod_processed_at_utc,
                   demod_status, failure_message, reconstruction_processed_at_utc,
                   reconstruction_status, quality_weight, accepted_frames, rejected_frames
            FROM processing_blocks
            WHERE experiment_run_id = $experiment_run_id
              AND block_number = $block_number;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$block_number", blockNumber);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProcessingBlock(reader) : null;
    }

    public (
        ExperimentRunRecord? Run,
        ProcessingBlockCatalogRecord? Block,
        IReadOnlyList<DerivedArtifactCatalogRecord> Artifacts) GetReplayFrameCatalogData(
            Guid experimentRunId,
            int blockNumber)
    {
        using var connection = OpenConnection();
        ExperimentRunRecord? run;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT experiment_run_id, session_id, set_label, started_at_utc, ended_at_utc,
                       status, storage_mode, run_directory, raw_status, demod_status,
                       reconstruction_status, failure_message, lifecycle_state, archived_at_utc
                FROM experiment_runs
                WHERE experiment_run_id = $experiment_run_id;
                """;
            command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
            using var reader = command.ExecuteReader();
            run = reader.Read() ? ReadRun(reader) : null;
        }

        ProcessingBlockCatalogRecord? block;
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT experiment_run_id, block_number, source_start_sample_index,
                       source_end_sample_index, acquired_at_utc, demod_processed_at_utc,
                       demod_status, failure_message, reconstruction_processed_at_utc,
                       reconstruction_status, quality_weight, accepted_frames, rejected_frames
                FROM processing_blocks
                WHERE experiment_run_id = $experiment_run_id
                  AND block_number = $block_number;
                """;
            command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
            command.Parameters.AddWithValue("$block_number", blockNumber);
            using var reader = command.ExecuteReader();
            block = reader.Read() ? ReadProcessingBlock(reader) : null;
        }

        var artifacts = new List<DerivedArtifactCatalogRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT experiment_run_id, block_number, kind, artifact_path, dataset_path, created_at_utc
                FROM derived_artifacts
                WHERE experiment_run_id = $experiment_run_id
                  AND block_number = $block_number
                ORDER BY kind;
                """;
            command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
            command.Parameters.AddWithValue("$block_number", blockNumber);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                artifacts.Add(ReadDerivedArtifact(reader));
            }
        }

        return (run, block, artifacts);
    }

    public IReadOnlyList<DerivedArtifactCatalogRecord> ListDerivedArtifacts(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, block_number, kind, artifact_path, dataset_path, created_at_utc
            FROM derived_artifacts
            WHERE experiment_run_id = $experiment_run_id
            ORDER BY block_number, kind;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        var artifacts = new List<DerivedArtifactCatalogRecord>();
        while (reader.Read())
        {
            artifacts.Add(ReadDerivedArtifact(reader));
        }

        return artifacts;
    }

    public IReadOnlyList<DerivedArtifactCatalogRecord> ListDerivedArtifacts(
        Guid experimentRunId,
        int blockNumber)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, block_number, kind, artifact_path, dataset_path, created_at_utc
            FROM derived_artifacts
            WHERE experiment_run_id = $experiment_run_id
              AND block_number = $block_number
            ORDER BY kind;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$block_number", blockNumber);
        using var reader = command.ExecuteReader();
        var artifacts = new List<DerivedArtifactCatalogRecord>();
        while (reader.Read())
        {
            artifacts.Add(ReadDerivedArtifact(reader));
        }

        return artifacts;
    }

    public void ArchiveTerminalRun(
        Guid experimentRunId,
        string expectedRunDirectory,
        string archiveRunDirectory,
        DateTimeOffset archivedAt)
    {
        ValidateRelativeArtifactPath(expectedRunDirectory);
        ValidateRelativeArtifactPath(archiveRunDirectory);
        if (string.Equals(
                expectedRunDirectory,
                archiveRunDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Archive directory must differ from the active run directory.");
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var (status, lifecycleState, currentDirectory) = ReadRunLifecycle(
            connection,
            experimentRunId);
        EnsureTerminalStatus(status);
        if (!string.Equals(lifecycleState, ActiveLifecycleState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only an active terminal run can be archived.");
        }

        if (!string.Equals(
                currentDirectory,
                expectedRunDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Run directory changed before archive commit.");
        }

        EnsureArtifactPrefixesContained(connection, experimentRunId, expectedRunDirectory);
        RewriteRunArtifactPrefixes(
            connection,
            experimentRunId,
            expectedRunDirectory,
            archiveRunDirectory);
        ExecuteNonQuery(
            connection,
            """
            UPDATE experiment_runs
            SET run_directory = $run_directory,
                lifecycle_state = $lifecycle_state,
                archived_at_utc = $archived_at_utc
            WHERE experiment_run_id = $experiment_run_id;
            """,
            ("$run_directory", archiveRunDirectory),
            ("$lifecycle_state", ArchivedLifecycleState),
            ("$archived_at_utc", Format(archivedAt)),
            ("$experiment_run_id", experimentRunId.ToString("D")));
        transaction.Commit();
    }

    public void DeleteTerminalRun(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var (status, _, _) = ReadRunLifecycle(connection, experimentRunId);
        EnsureTerminalStatus(status);
        foreach (var table in new[]
                 {
                     "experiment_exports",
                     "reference_epochs",
                     "reconstruction_lane_frames",
                     "reconstruction_revisions",
                     "derived_artifacts",
                     "processing_blocks",
                     "raw_segments",
                     "reconstruction_pipeline_manifests",
                     "experiment_run_configs"
                 })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE experiment_run_id = $experiment_run_id;";
            command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
            command.ExecuteNonQuery();
        }

        ExecuteNonQuery(
            connection,
            "DELETE FROM experiment_runs WHERE experiment_run_id = $experiment_run_id;",
            ("$experiment_run_id", experimentRunId.ToString("D")));
        transaction.Commit();
    }

    public int RecoverInterruptedRuns(DateTimeOffset recoveredAt)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE experiment_runs
            SET ended_at_utc = $ended_at_utc,
                status = $interrupted,
                failure_message = COALESCE(failure_message, 'application exited before run completion')
            WHERE status = $recording;
            """;
        command.Parameters.AddWithValue("$ended_at_utc", Format(recoveredAt));
        command.Parameters.AddWithValue("$interrupted", InterruptedStatus);
        command.Parameters.AddWithValue("$recording", RecordingStatus);
        return command.ExecuteNonQuery();
    }

    public ExperimentRunRecord? GetRun(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, session_id, set_label, started_at_utc, ended_at_utc,
                   status, storage_mode, run_directory, raw_status, demod_status,
                   reconstruction_status, failure_message, lifecycle_state, archived_at_utc
            FROM experiment_runs
            WHERE experiment_run_id = $experiment_run_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    public IReadOnlyList<ExperimentRunRecord> ListRuns(int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, session_id, set_label, started_at_utc, ended_at_utc,
                   status, storage_mode, run_directory, raw_status, demod_status,
                   reconstruction_status, failure_message, lifecycle_state, archived_at_utc
            FROM experiment_runs
            ORDER BY started_at_utc DESC, experiment_run_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<ExperimentRunRecord>();
        while (reader.Read())
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public ExperimentRunCatalogSummary? GetRunSummary(Guid experimentRunId)
    {
        return QueryRunSummary(experimentRunId);
    }

    private ExperimentRunCatalogSummary? QueryRunSummary(Guid experimentRunId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH raw_stats AS (
                SELECT COALESCE(SUM(CASE WHEN status='ready' THEN sample_rows ELSE 0 END), 0) AS raw_rows,
                       COALESCE(SUM(CASE WHEN status='ready' THEN 1 ELSE 0 END), 0) AS raw_segments
                FROM raw_segments
                WHERE experiment_run_id = $run_id
            ),
            block_stats AS (
                SELECT COUNT(*) AS block_count,
                       SUM(CASE WHEN demod_status='ready' THEN 1 ELSE 0 END) AS demod_ready,
                       SUM(CASE WHEN demod_status='failed' THEN 1 ELSE 0 END) AS demod_failed,
                       SUM(CASE WHEN reconstruction_status='ready' THEN 1 ELSE 0 END) AS reconstruction_ready,
                       SUM(CASE WHEN reconstruction_status='pending' THEN 1 ELSE 0 END) AS reconstruction_pending,
                       SUM(CASE WHEN reconstruction_status='failed' THEN 1 ELSE 0 END) AS reconstruction_failed,
                       SUM(CASE WHEN reconstruction_status='not_applicable' THEN 1 ELSE 0 END) AS reconstruction_not_applicable
                FROM processing_blocks
                WHERE experiment_run_id = $run_id
            ),
            export_stats AS (
                SELECT COUNT(*) AS export_count,
                       COUNT(DISTINCT CASE WHEN dataset_path='/raw/adc_counts' AND filter='all' THEN source_artifact_path END) AS raw_csv_export_count
                FROM experiment_exports
                WHERE experiment_run_id = $run_id
            )
            SELECT run.experiment_run_id, run.session_id, run.set_label, run.started_at_utc,
                   run.ended_at_utc, run.status, run.storage_mode, run.run_directory,
                   run.raw_status, run.demod_status, run.reconstruction_status, run.failure_message,
                   run.lifecycle_state, run.archived_at_utc,
                   COALESCE(raw_stats.raw_rows, 0),
                   COALESCE(raw_stats.raw_segments, 0),
                   COALESCE(block_stats.block_count, 0),
                   COALESCE(block_stats.demod_ready, 0),
                   COALESCE(block_stats.demod_failed, 0),
                   COALESCE(block_stats.reconstruction_ready, 0),
                   COALESCE(block_stats.reconstruction_pending, 0),
                   COALESCE(block_stats.reconstruction_failed, 0),
                   COALESCE(block_stats.reconstruction_not_applicable, 0),
                   (SELECT artifact_path FROM raw_segments
                    WHERE experiment_run_id = $run_id AND status='ready'
                    ORDER BY segment_sequence LIMIT 1),
                   COALESCE(export_stats.export_count, 0),
                   COALESCE(export_stats.raw_csv_export_count, 0),
                   (SELECT artifact_path FROM experiment_exports
                    WHERE experiment_run_id = $run_id
                    ORDER BY exported_at_utc DESC, export_id DESC LIMIT 1)
            FROM experiment_runs AS run
            CROSS JOIN raw_stats
            CROSS JOIN block_stats
            CROSS JOIN export_stats
            WHERE run.experiment_run_id = $run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRunCatalogSummary(reader) : null;
    }

    public IReadOnlyList<ExperimentRunCatalogSummary> ListRunSummaries(int limit = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return QueryRunSummaries(limit);
    }

    private IReadOnlyList<ExperimentRunCatalogSummary> QueryRunSummaries(int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH raw_stats AS (
                SELECT experiment_run_id,
                       COALESCE(SUM(CASE WHEN status='ready' THEN sample_rows ELSE 0 END), 0) AS raw_rows,
                       COALESCE(SUM(CASE WHEN status='ready' THEN 1 ELSE 0 END), 0) AS raw_segments
                FROM raw_segments
                GROUP BY experiment_run_id
            ),
            first_raw AS (
                SELECT segment.experiment_run_id, segment.artifact_path
                FROM raw_segments AS segment
                JOIN (
                    SELECT experiment_run_id, MIN(segment_sequence) AS first_sequence
                    FROM raw_segments
                    WHERE status='ready'
                    GROUP BY experiment_run_id
                ) AS first
                  ON first.experiment_run_id = segment.experiment_run_id
                 AND first.first_sequence = segment.segment_sequence
            ),
            block_stats AS (
                SELECT experiment_run_id,
                       COUNT(*) AS block_count,
                       SUM(CASE WHEN demod_status='ready' THEN 1 ELSE 0 END) AS demod_ready,
                       SUM(CASE WHEN demod_status='failed' THEN 1 ELSE 0 END) AS demod_failed,
                       SUM(CASE WHEN reconstruction_status='ready' THEN 1 ELSE 0 END) AS reconstruction_ready,
                       SUM(CASE WHEN reconstruction_status='pending' THEN 1 ELSE 0 END) AS reconstruction_pending,
                       SUM(CASE WHEN reconstruction_status='failed' THEN 1 ELSE 0 END) AS reconstruction_failed,
                       SUM(CASE WHEN reconstruction_status='not_applicable' THEN 1 ELSE 0 END) AS reconstruction_not_applicable
                FROM processing_blocks
                GROUP BY experiment_run_id
            ),
            export_stats AS (
                SELECT experiment_run_id,
                       COUNT(*) AS export_count,
                       COUNT(DISTINCT CASE WHEN dataset_path='/raw/adc_counts' AND filter='all' THEN source_artifact_path END) AS raw_csv_export_count
                FROM experiment_exports
                GROUP BY experiment_run_id
            ),
            latest_export AS (
                SELECT experiment_run_id, artifact_path
                FROM (
                    SELECT experiment_run_id, artifact_path,
                           ROW_NUMBER() OVER (
                               PARTITION BY experiment_run_id
                               ORDER BY exported_at_utc DESC, export_id DESC) AS rank
                    FROM experiment_exports
                )
                WHERE rank=1
            )
            SELECT run.experiment_run_id, run.session_id, run.set_label, run.started_at_utc,
                   run.ended_at_utc, run.status, run.storage_mode, run.run_directory,
                   run.raw_status, run.demod_status, run.reconstruction_status, run.failure_message,
                   run.lifecycle_state, run.archived_at_utc,
                   COALESCE(raw_stats.raw_rows, 0),
                   COALESCE(raw_stats.raw_segments, 0),
                   COALESCE(block_stats.block_count, 0),
                   COALESCE(block_stats.demod_ready, 0),
                   COALESCE(block_stats.demod_failed, 0),
                   COALESCE(block_stats.reconstruction_ready, 0),
                   COALESCE(block_stats.reconstruction_pending, 0),
                   COALESCE(block_stats.reconstruction_failed, 0),
                   COALESCE(block_stats.reconstruction_not_applicable, 0),
                   first_raw.artifact_path,
                   COALESCE(export_stats.export_count, 0),
                   COALESCE(export_stats.raw_csv_export_count, 0),
                   latest_export.artifact_path
            FROM experiment_runs AS run
            LEFT JOIN raw_stats ON raw_stats.experiment_run_id = run.experiment_run_id
            LEFT JOIN block_stats ON block_stats.experiment_run_id = run.experiment_run_id
            LEFT JOIN first_raw ON first_raw.experiment_run_id = run.experiment_run_id
            LEFT JOIN export_stats ON export_stats.experiment_run_id = run.experiment_run_id
            LEFT JOIN latest_export ON latest_export.experiment_run_id = run.experiment_run_id
            ORDER BY run.started_at_utc DESC, run.experiment_run_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var summaries = new List<ExperimentRunCatalogSummary>();
        while (reader.Read())
        {
            summaries.Add(ReadRunCatalogSummary(reader));
        }

        return summaries;
    }

    private static ExperimentRunCatalogSummary ReadRunCatalogSummary(SqliteDataReader reader) =>
        new(
            ReadRun(reader),
            new ExperimentCoverageSummary(
                reader.GetInt64(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                reader.GetInt32(18),
                reader.GetInt32(19),
                reader.GetInt32(20),
                reader.GetInt32(21),
                reader.GetInt32(22),
                reader.GetInt32(24),
                reader.GetInt32(25)),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(26) ? null : reader.GetString(26));

    private void ValidateRelativeArtifactPath(string path)
    {
        _ = layout.ResolveArtifactPath(path);
    }

    private static (string Status, string LifecycleState, string RunDirectory) ReadRunLifecycle(
        SqliteConnection connection,
        Guid experimentRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, lifecycle_state, run_directory
            FROM experiment_runs
            WHERE experiment_run_id = $experiment_run_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException($"Experiment run {experimentRunId:D} does not exist.");
        }

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static void EnsureTerminalStatus(string status)
    {
        if (status is not (CompletedStatus or InterruptedStatus or FailedStatus))
        {
            throw new InvalidOperationException("Recording or non-terminal runs cannot be archived or deleted.");
        }
    }

    private static void EnsureArtifactPrefixesContained(
        SqliteConnection connection,
        Guid experimentRunId,
        string expectedPrefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM raw_segments
                 WHERE experiment_run_id = $experiment_run_id AND NOT (
                     artifact_path = $prefix OR
                     substr(artifact_path, 1, length($prefix) + 1) = $prefix || $separator)) +
                (SELECT COUNT(*) FROM derived_artifacts
                 WHERE experiment_run_id = $experiment_run_id AND NOT (
                     artifact_path = $prefix OR
                     substr(artifact_path, 1, length($prefix) + 1) = $prefix || $separator)) +
                (SELECT COUNT(*) FROM reconstruction_lane_frames
                 WHERE experiment_run_id = $experiment_run_id
                   AND artifact_path IS NOT NULL AND NOT (
                     artifact_path = $prefix OR
                     substr(artifact_path, 1, length($prefix) + 1) = $prefix || $separator)) +
                (SELECT COUNT(*) FROM reference_epochs
                 WHERE experiment_run_id = $experiment_run_id AND NOT (
                     artifact_path = $prefix OR
                     substr(artifact_path, 1, length($prefix) + 1) = $prefix || $separator)) +
                (SELECT COUNT(*) FROM experiment_exports
                 WHERE experiment_run_id = $experiment_run_id AND NOT (
                     (source_artifact_path = $prefix OR
                      substr(source_artifact_path, 1, length($prefix) + 1) = $prefix || $separator) AND
                     (artifact_path = $prefix OR
                      substr(artifact_path, 1, length($prefix) + 1) = $prefix || $separator)));
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$prefix", expectedPrefix);
        command.Parameters.AddWithValue("$separator", Path.DirectorySeparatorChar.ToString());
        var outsideCount = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (outsideCount != 0)
        {
            throw new InvalidDataException(
                "One or more canonical artifact paths are outside the run directory; archive was refused.");
        }
    }

    private static void RewriteRunArtifactPrefixes(
        SqliteConnection connection,
        Guid experimentRunId,
        string oldPrefix,
        string newPrefix)
    {
        RewriteArtifactPrefix(connection, "raw_segments", "artifact_path", experimentRunId, oldPrefix, newPrefix);
        RewriteArtifactPrefix(connection, "derived_artifacts", "artifact_path", experimentRunId, oldPrefix, newPrefix);
        RewriteArtifactPrefix(connection, "reconstruction_lane_frames", "artifact_path", experimentRunId, oldPrefix, newPrefix);
        RewriteArtifactPrefix(connection, "reference_epochs", "artifact_path", experimentRunId, oldPrefix, newPrefix);
        RewriteArtifactPrefix(connection, "experiment_exports", "source_artifact_path", experimentRunId, oldPrefix, newPrefix);
        RewriteArtifactPrefix(connection, "experiment_exports", "artifact_path", experimentRunId, oldPrefix, newPrefix);
    }

    private static void RewriteArtifactPrefix(
        SqliteConnection connection,
        string table,
        string column,
        Guid experimentRunId,
        string oldPrefix,
        string newPrefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE {table}
            SET {column} = CASE
                WHEN {column} = $old_prefix THEN $new_prefix
                ELSE $new_prefix || substr({column}, length($old_prefix) + 1)
            END
            WHERE experiment_run_id = $experiment_run_id;
            """;
        command.Parameters.AddWithValue("$old_prefix", oldPrefix);
        command.Parameters.AddWithValue("$new_prefix", newPrefix);
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.ExecuteNonQuery();
    }

    private void ValidateExportPath(ExperimentRunRecord run, string artifactPath)
    {
        var exportsDirectory = layout.ResolveArtifactPath(Path.Combine(run.RunDirectory, "exports"));
        var fullPath = layout.ResolveArtifactPath(artifactPath);
        var relative = Path.GetRelativePath(exportsDirectory, fullPath);
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Export artifact must stay inside the experiment exports directory.", nameof(artifactPath));
        }
    }

    private static bool IsRegisteredArtifact(
        SqliteConnection connection,
        Guid experimentRunId,
        string artifactPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1 FROM raw_segments
                WHERE experiment_run_id=$experiment_run_id AND artifact_path=$artifact_path
                UNION ALL
                SELECT 1 FROM derived_artifacts
                WHERE experiment_run_id=$experiment_run_id AND artifact_path=$artifact_path
                UNION ALL
                SELECT 1 FROM reconstruction_lane_frames
                WHERE experiment_run_id=$experiment_run_id AND artifact_path=$artifact_path
                UNION ALL
                SELECT 1 FROM reference_epochs
                WHERE experiment_run_id=$experiment_run_id AND artifact_path=$artifact_path
            );
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$artifact_path", artifactPath);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void ValidateProcessingBlock(ProcessingBlockCatalogRecord block)
    {
        if (block.BlockNumber < 0 ||
            block.SourceStartSampleIndex < 0 ||
            block.SourceEndSampleIndex <= block.SourceStartSampleIndex ||
            !double.IsFinite(block.QualityWeight) ||
            block.QualityWeight < 0 ||
            block.AcceptedFrameCount < 0 ||
            block.RejectedFrameCount < 0)
        {
            throw new ArgumentException("Processing block sample range is invalid.", nameof(block));
        }
    }

    private void ValidateDerivedArtifact(
        Guid experimentRunId,
        int blockNumber,
        DerivedArtifactCatalogRecord artifact)
    {
        if (artifact.ExperimentRunId != experimentRunId || artifact.BlockNumber != blockNumber)
        {
            throw new ArgumentException("Derived artifact identity does not match processing block.", nameof(artifact));
        }

        ValidateRelativeArtifactPath(artifact.ArtifactPath);
    }

    private static ReconstructionRevisionCatalogRecord? GetReconstructionRevision(
        SqliteConnection connection,
        Guid experimentRunId,
        string lane,
        string revisionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT experiment_run_id, lane, revision_id, status, algorithm_fingerprint,
                   raw_denominator, demod_denominator, terminal_outcome_count,
                   reconstructed_count, neutral_count, excluded_count,
                   estimated_incremental_bytes, created_at_utc, updated_at_utc,
                   published_at_utc, failure_message
            FROM reconstruction_revisions
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id;
            """;
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$revision_id", revisionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadReconstructionRevision(reader) : null;
    }

    private static void RefreshReconstructionRevisionCounts(
        SqliteConnection connection,
        Guid experimentRunId,
        string lane,
        string revisionId,
        DateTimeOffset updatedAt)
    {
        var counts = ReadReconstructionLaneCounts(connection, experimentRunId, lane, revisionId);
        ExecuteNonQuery(
            connection,
            """
            UPDATE reconstruction_revisions
            SET terminal_outcome_count = $terminal,
                reconstructed_count = $reconstructed,
                neutral_count = $neutral,
                excluded_count = $excluded,
                updated_at_utc = $updated_at_utc
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id;
            """,
            ("$terminal", counts.Terminal),
            ("$reconstructed", counts.Reconstructed),
            ("$neutral", counts.Neutral),
            ("$excluded", counts.Excluded),
            ("$updated_at_utc", Format(updatedAt)),
            ("$experiment_run_id", experimentRunId.ToString("D")),
            ("$lane", lane),
            ("$revision_id", revisionId));
    }

    private static (int Terminal, int Reconstructed, int Neutral, int Excluded)
        ReadReconstructionLaneCounts(
            SqliteConnection connection,
            Guid experimentRunId,
            string lane,
            string revisionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*),
                   SUM(CASE WHEN outcome = $reconstructed THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome = $neutral THEN 1 ELSE 0 END),
                   SUM(CASE WHEN outcome IN ($no_reference, $invalid, $discontinuity)
                            THEN 1 ELSE 0 END)
            FROM reconstruction_lane_frames
            WHERE experiment_run_id = $experiment_run_id
              AND lane = $lane
              AND revision_id = $revision_id;
            """;
        command.Parameters.AddWithValue("$reconstructed", ReconstructionFrameOutcome.Reconstructed);
        command.Parameters.AddWithValue("$neutral", ReconstructionFrameOutcome.Neutral);
        command.Parameters.AddWithValue("$no_reference", ReconstructionFrameOutcome.ExcludedNoReference);
        command.Parameters.AddWithValue("$invalid", ReconstructionFrameOutcome.ExcludedInvalid);
        command.Parameters.AddWithValue("$discontinuity", ReconstructionFrameOutcome.ExcludedDiscontinuity);
        command.Parameters.AddWithValue("$experiment_run_id", experimentRunId.ToString("D"));
        command.Parameters.AddWithValue("$lane", lane);
        command.Parameters.AddWithValue("$revision_id", revisionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0, 0, 0);
        }

        return (
            reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }

    private static void ValidateReconstructionRevision(ReconstructionRevisionCatalogRecord revision)
    {
        ValidateLaneIdentity(revision.Lane, revision.RevisionId);
        if (!ReconstructionRevisionStatus.IsKnown(revision.Status) ||
            string.IsNullOrWhiteSpace(revision.AlgorithmFingerprint) ||
            revision.RawDenominator < 0 ||
            revision.DemodDenominator < 0 ||
            revision.TerminalOutcomeCount < 0 ||
            revision.ReconstructedCount < 0 ||
            revision.NeutralCount < 0 ||
            revision.ExcludedCount < 0 ||
            revision.EstimatedIncrementalBytes < 0 ||
            revision.TerminalOutcomeCount !=
            revision.ReconstructedCount + revision.NeutralCount + revision.ExcludedCount ||
            (revision.IsPublished &&
             (revision.PublishedAt is null ||
              revision.DemodDenominator != revision.TerminalOutcomeCount)))
        {
            throw new ArgumentException("Reconstruction revision state is invalid.", nameof(revision));
        }
    }

    private static void ValidateReconstructionLaneFrame(ReconstructionLaneFrameCatalogRecord frame)
    {
        ValidateLaneIdentity(frame.Lane, frame.RevisionId);
        if (frame.SourceBlockNumber <= 0 ||
            frame.SequenceNumber <= 0 ||
            !ReconstructionFrameOutcome.IsKnown(frame.Outcome) ||
            string.IsNullOrWhiteSpace(frame.AlgorithmFingerprint) ||
            ((frame.ArtifactPath is null) != (frame.DatasetPath is null)) ||
            (string.Equals(frame.Outcome, ReconstructionFrameOutcome.Reconstructed, StringComparison.Ordinal) &&
             (string.IsNullOrWhiteSpace(frame.ArtifactPath) || string.IsNullOrWhiteSpace(frame.DatasetPath))) ||
            (ReconstructionFrameOutcome.IsExcluded(frame.Outcome) &&
             string.IsNullOrWhiteSpace(frame.ExclusionReason)) ||
            ((frame.SourceStartSampleIndex is null) != (frame.SourceEndSampleIndex is null)) ||
            (frame.SourceStartSampleIndex is { } sourceStart &&
             (sourceStart < 0 || frame.SourceEndSampleIndex <= sourceStart)) ||
            (string.Equals(frame.Lane, ReconstructionLane.Live, StringComparison.Ordinal) &&
             (frame.Outcome is not (ReconstructionFrameOutcome.Reconstructed or ReconstructionFrameOutcome.Neutral) ||
              string.IsNullOrWhiteSpace(frame.FinalWeightHash) ||
              string.IsNullOrWhiteSpace(frame.ResultHash) ||
              string.IsNullOrWhiteSpace(frame.KalmanSessionId) ||
              !string.Equals(frame.KalmanDisposition, "updated", StringComparison.Ordinal) ||
              string.IsNullOrWhiteSpace(frame.PresentationJson) ||
              frame.SourceStartSampleIndex is null)))
        {
            throw new ArgumentException("Reconstruction lane frame state is invalid.", nameof(frame));
        }
    }

    private static void ValidateLaneIdentity(string lane, string revisionId)
    {
        if (!ReconstructionLane.IsKnown(lane))
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown reconstruction lane.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(revisionId);
        if (revisionId.Length > 80 ||
            revisionId.Any(character => char.IsControl(character) || character is '/' or '\\'))
        {
            throw new ArgumentException("Reconstruction revision id is invalid.", nameof(revisionId));
        }
    }

    private static void RecordDemodPlaceholder(SqliteConnection connection, ProcessingBlockCatalogRecord block)
    {
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO processing_blocks(
                experiment_run_id, block_number, source_start_sample_index,
                source_end_sample_index, acquired_at_utc, demod_processed_at_utc,
                demod_status, reconstruction_processed_at_utc, reconstruction_status,
                failure_message, quality_weight, accepted_frames, rejected_frames)
            VALUES(
                $experiment_run_id, $block_number, $source_start_sample_index,
                $source_end_sample_index, $acquired_at_utc, $demod_processed_at_utc,
                $demod_status, NULL, 'pending', $failure_message,
                $quality_weight, $accepted_frames, $rejected_frames)
            ON CONFLICT(experiment_run_id, block_number) DO NOTHING;
            """,
            ("$experiment_run_id", block.ExperimentRunId.ToString("D")),
            ("$block_number", block.BlockNumber),
            ("$source_start_sample_index", block.SourceStartSampleIndex),
            ("$source_end_sample_index", block.SourceEndSampleIndex),
            ("$acquired_at_utc", Format(block.AcquiredAt)),
            ("$demod_processed_at_utc", Format(block.DemodProcessedAt)),
            ("$demod_status", block.DemodStatus),
            ("$failure_message", block.FailureMessage),
            ("$quality_weight", block.QualityWeight),
            ("$accepted_frames", block.AcceptedFrameCount),
            ("$rejected_frames", block.RejectedFrameCount));
    }

    private void UpsertDerivedArtifact(SqliteConnection connection, DerivedArtifactCatalogRecord artifact)
    {
        ValidateRelativeArtifactPath(artifact.ArtifactPath);
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO derived_artifacts(
                experiment_run_id, block_number, kind, artifact_path, dataset_path, created_at_utc)
            VALUES(
                $experiment_run_id, $block_number, $kind, $artifact_path, $dataset_path, $created_at_utc)
            ON CONFLICT(experiment_run_id, block_number, kind) DO UPDATE SET
                artifact_path = excluded.artifact_path,
                dataset_path = excluded.dataset_path,
                created_at_utc = excluded.created_at_utc;
            """,
            ("$experiment_run_id", artifact.ExperimentRunId.ToString("D")),
            ("$block_number", artifact.BlockNumber),
            ("$kind", artifact.Kind),
            ("$artifact_path", artifact.ArtifactPath),
            ("$dataset_path", artifact.DatasetPath),
            ("$created_at_utc", Format(artifact.CreatedAt)));
    }

    private static void ValidateTerminalStatus(string status)
    {
        if (status is not (CompletedStatus or InterruptedStatus or FailedStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown terminal run status.");
        }
    }

    private static ExperimentRunRecord ReadRun(SqliteDataReader reader)
    {
        return new ExperimentRunRecord(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : Parse(reader.GetString(13)));
    }

    private static ReconstructionRevisionCatalogRecord ReadReconstructionRevision(SqliteDataReader reader)
    {
        return new ReconstructionRevisionCatalogRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            Parse(reader.GetString(12)),
            Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }

    private static ReconstructionLaneFrameCatalogRecord ReadReconstructionLaneFrame(SqliteDataReader reader)
    {
        return new ReconstructionLaneFrameCatalogRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            Parse(reader.GetString(6)),
            Parse(reader.GetString(7)),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));
    }

    private static RawSegmentCatalogRecord ReadRawSegment(SqliteDataReader reader)
    {
        return new RawSegmentCatalogRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            Parse(reader.GetString(8)),
            reader.GetString(9),
            reader.GetInt32(10) != 0);
    }

    private static ProcessingBlockCatalogRecord ReadProcessingBlock(SqliteDataReader reader)
    {
        return new ProcessingBlockCatalogRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            Parse(reader.GetString(4)),
            Parse(reader.GetString(5)),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
            reader.GetString(9),
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12));
    }

    private static DerivedArtifactCatalogRecord ReadDerivedArtifact(SqliteDataReader reader)
    {
        return new DerivedArtifactCatalogRecord(
            Guid.Parse(reader.GetString(0)),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            Parse(reader.GetString(5)));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version={version};";
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        string table,
        string column,
        string declaration)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        using var reader = inspect.ExecuteReader();
        var exists = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        reader.Close();
        if (!exists)
        {
            ExecuteNonQuery(connection, $"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
        }
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Parse(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}

public sealed record ExperimentRunRecord(
    Guid ExperimentRunId,
    Guid SessionId,
    string SetLabel,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    string StorageMode,
    string RunDirectory,
    string RawStatus,
    string DemodStatus,
    string ReconstructionStatus,
    string? FailureMessage,
    string LifecycleState = ExperimentCatalog.ActiveLifecycleState,
    DateTimeOffset? ArchivedAt = null)
{
    public static ExperimentRunRecord CreateRecording(
        Guid experimentRunId,
        Guid sessionId,
        string setLabel,
        DateTimeOffset startedAt,
        string storageMode,
        string runDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        return new ExperimentRunRecord(
            experimentRunId,
            sessionId,
            setLabel.Trim(),
            startedAt,
            null,
            ExperimentCatalog.RecordingStatus,
            storageMode.Trim(),
            runDirectory,
            "pending",
            "pending",
            "pending",
            null);
    }
}

public sealed record RawSegmentCatalogRecord(
    Guid ExperimentRunId,
    int SegmentSequence,
    string ArtifactPath,
    string DatasetPath,
    long StartSampleIndex,
    long EndSampleIndex,
    long SampleRows,
    int ChannelCount,
    DateTimeOffset CapturedAt,
    string Status,
    bool HasDiscontinuity = false);

public sealed record ProcessingBlockCatalogRecord(
    Guid ExperimentRunId,
    int BlockNumber,
    long SourceStartSampleIndex,
    long SourceEndSampleIndex,
    DateTimeOffset AcquiredAt,
    DateTimeOffset DemodProcessedAt,
    string DemodStatus,
    string? FailureMessage = null,
    DateTimeOffset? ReconstructionProcessedAt = null,
    string ReconstructionStatus = "pending",
    double QualityWeight = 1.0,
    int AcceptedFrameCount = 0,
    int RejectedFrameCount = 0);

public sealed record DerivedArtifactCatalogRecord(
    Guid ExperimentRunId,
    int BlockNumber,
    string Kind,
    string ArtifactPath,
    string DatasetPath,
    DateTimeOffset CreatedAt);

public sealed record ExperimentExportCatalogRecord(
    Guid ExperimentRunId,
    string SourceArtifactPath,
    string DatasetPath,
    string ArtifactPath,
    string Filter,
    DateTimeOffset ExportedAt);

public sealed record ExperimentRunConfigRecord(
    Guid ExperimentRunId,
    string ReconstructionRoute,
    double DifferenceLambda,
    bool CustomLambdaEnabled,
    double MeshSize,
    double FrequencyHz,
    double ChannelCycles,
    double SampleRateHz,
    string DifferenceOrientation,
    string ReconstructionScaleStatus,
    string ReconstructionScaleProvenance,
    string ReferenceScalePolicy,
    string? ContactOperatingFingerprintJson,
    string? ContactThresholdProfileId,
    string ContactThresholdMode,
    double? RequestedFrequencyHz = null,
    double? ActualFrequencyHz = null,
    long? DdsFrequencyTuningWord = null,
    double? RequestedDwellUs = null,
    double? EffectiveDwellUs = null,
    int? AdRangeCode = null,
    double? AdcFullSpanVolts = null,
    double? AdcLsbVolts = null);

public sealed record ExperimentReferenceEpochCatalogRecord(
    Guid ExperimentRunId,
    int ReferenceEpoch,
    int LockedBlockNumber,
    DateTimeOffset LockedAt,
    string LockKind,
    string ArtifactPath,
    string DatasetPath,
    DateTimeOffset CreatedAt,
    long LockedStartSampleIndex = -1);

public sealed record ExperimentRunCatalogSummary(
    ExperimentRunRecord Run,
    ExperimentCoverageSummary Coverage,
    string? PrimaryRawArtifactPath,
    string? LatestExportArtifactPath = null);

public sealed record ExperimentCoverageSummary(
    long RawSampleRows,
    int RawSegmentCount,
    int ProcessingBlockCount,
    int DemodReadyCount,
    int DemodFailedCount,
    int ReconstructionReadyCount,
    int ReconstructionPendingCount,
    int ReconstructionFailedCount,
    int ReconstructionNotApplicableCount = 0,
    int ExportCount = 0,
    int RawCsvExportCount = 0)
{
    public static ExperimentCoverageSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
