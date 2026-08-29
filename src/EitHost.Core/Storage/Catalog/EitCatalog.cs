using System.Globalization;
using EitHost.Core.Pairing;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Hardware.Pnp;
using Microsoft.Data.Sqlite;

namespace EitHost.Core.Storage.Catalog;

public sealed class EitCatalog
{
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly string readOnlyConnectionString;

    public EitCatalog(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString();
        readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
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

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS devices (
                device_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                display_name TEXT NOT NULL,
                vid TEXT NOT NULL,
                pid TEXT NOT NULL,
                location_path TEXT NOT NULL,
                port_name TEXT NULL,
                usb_device_number INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS pairings (
                pairing_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                label TEXT NOT NULL,
                usb_device_key TEXT NOT NULL,
                dds_device_key TEXT NOT NULL,
                usb_device_number INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id),
                FOREIGN KEY(usb_device_key) REFERENCES devices(device_key),
                FOREIGN KEY(dds_device_key) REFERENCES devices(device_key)
            );
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                set_label TEXT NOT NULL,
                captured_at_utc TEXT NOT NULL,
                hdf5_path TEXT NOT NULL,
                sample_rows INTEGER NOT NULL,
                channel_count INTEGER NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id)
            );
            CREATE TABLE IF NOT EXISTS files (
                file_id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS exports (
                export_id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                source_hdf5_path TEXT NOT NULL,
                dataset_path TEXT NOT NULL,
                csv_path TEXT NOT NULL,
                filter TEXT NOT NULL,
                exported_at_utc TEXT NOT NULL,
                FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            """);
    }

    public void AddSession(Guid sessionId, string name, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT OR REPLACE INTO sessions(session_id, name, created_at_utc)
            VALUES ($session_id, $name, $created_at_utc);
            """,
            ("$session_id", sessionId.ToString("D")),
            ("$name", name.Trim()),
            ("$created_at_utc", createdAt.ToUniversalTime().ToString("O")));
    }

    public void AddPairing(Guid sessionId, EitSetPairing pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertDevice(connection, pairing.Usb2070Candidate.IdentityKey, "usb2070", pairing.Usb2070Candidate, pairing.Usb2070DeviceNumber);
        UpsertDevice(connection, pairing.DdsSerialCandidate.IdentityKey, "dds_serial", pairing.DdsSerialCandidate, null);
        ExecuteNonQuery(
            connection,
            """
            INSERT OR REPLACE INTO pairings(
                pairing_id, session_id, label, usb_device_key, dds_device_key, usb_device_number, created_at_utc)
            VALUES ($pairing_id, $session_id, $label, $usb_device_key, $dds_device_key, $usb_device_number, $created_at_utc);
            """,
            ("$pairing_id", $"{sessionId:D}:{pairing.Label}"),
            ("$session_id", sessionId.ToString("D")),
            ("$label", pairing.Label),
            ("$usb_device_key", pairing.Usb2070Candidate.IdentityKey),
            ("$dds_device_key", pairing.DdsSerialCandidate.IdentityKey),
            ("$usb_device_number", pairing.Usb2070DeviceNumber),
            ("$created_at_utc", pairing.CreatedAt.ToUniversalTime().ToString("O")));
        transaction.Commit();
    }

    public void AddRun(Hdf5RunData runData, string hdf5Path)
    {
        ArgumentNullException.ThrowIfNull(runData);
        ArgumentException.ThrowIfNullOrWhiteSpace(hdf5Path);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ExecuteNonQuery(
            connection,
            """
            INSERT OR REPLACE INTO runs(
                run_id, session_id, set_label, captured_at_utc, hdf5_path, sample_rows, channel_count)
            VALUES ($run_id, $session_id, $set_label, $captured_at_utc, $hdf5_path, $sample_rows, $channel_count);
            """,
            ("$run_id", runData.RunId.ToString("D")),
            ("$session_id", runData.SessionId.ToString("D")),
            ("$set_label", runData.Device.SetLabel),
            ("$captured_at_utc", runData.CapturedAt.ToUniversalTime().ToString("O")),
            ("$hdf5_path", Path.GetFullPath(hdf5Path)),
            ("$sample_rows", runData.AdcCounts.GetLength(0)),
            ("$channel_count", runData.AdcCounts.GetLength(1)));
        AddFile(connection, runData.RunId, "hdf5", hdf5Path, "/raw/adc_counts", runData.CapturedAt);
        transaction.Commit();
    }

    public void AddExport(
        Guid runId,
        string sourceHdf5Path,
        string datasetPath,
        string csvPath,
        string filter,
        DateTimeOffset exportedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHdf5Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO exports(run_id, source_hdf5_path, dataset_path, csv_path, filter, exported_at_utc)
            VALUES ($run_id, $source_hdf5_path, $dataset_path, $csv_path, $filter, $exported_at_utc);
            """,
            ("$run_id", runId.ToString("D")),
            ("$source_hdf5_path", Path.GetFullPath(sourceHdf5Path)),
            ("$dataset_path", datasetPath),
            ("$csv_path", Path.GetFullPath(csvPath)),
            ("$filter", filter),
            ("$exported_at_utc", exportedAt.ToUniversalTime().ToString("O")));
    }

    public void AddFile(
        Guid runId,
        string kind,
        string path,
        string datasetPath,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);

        using var connection = OpenConnection();
        AddFile(connection, runId, kind, path, datasetPath, createdAt);
    }

    public Guid? FindRunIdForPath(string hdf5Path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hdf5Path);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id FROM runs WHERE hdf5_path = $path
            UNION
            SELECT run_id FROM files WHERE path = $path
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", Path.GetFullPath(hdf5Path));

        var value = command.ExecuteScalar();
        return value is string runId && Guid.TryParse(runId, out var parsed)
            ? parsed
            : null;
    }

    public IReadOnlyList<EitCatalogRunSummary> ListRecentRuns(int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenReadConnection();
        if (!TableExists(connection, "runs") ||
            !TableExists(connection, "files") ||
            !TableExists(connection, "exports"))
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                runs.run_id,
                runs.session_id,
                runs.set_label,
                runs.captured_at_utc,
                runs.hdf5_path,
                runs.sample_rows,
                runs.channel_count,
                (SELECT COUNT(*) FROM files WHERE files.run_id = runs.run_id) AS file_count,
                (SELECT COUNT(*) FROM exports WHERE exports.run_id = runs.run_id) AS export_count,
                (
                    SELECT files.path
                    FROM files
                    WHERE files.run_id = runs.run_id AND files.kind = 'demod_hdf5'
                    ORDER BY files.created_at_utc DESC, files.file_id DESC
                    LIMIT 1
                ) AS latest_demod_hdf5_path,
                (
                    SELECT exports.csv_path
                    FROM exports
                    WHERE exports.run_id = runs.run_id
                    ORDER BY exports.exported_at_utc DESC, exports.export_id DESC
                    LIMIT 1
                ) AS latest_csv_path
            FROM runs
            ORDER BY runs.captured_at_utc DESC, runs.run_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var runs = new List<EitCatalogRunSummary>();
        while (reader.Read())
        {
            runs.Add(new EitCatalogRunSummary(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return runs;
    }

    public EitCatalogSummary GetSummary()
    {
        using var connection = OpenConnection();
        return new EitCatalogSummary(
            CountRows(connection, "sessions"),
            CountRows(connection, "devices"),
            CountRows(connection, "pairings"),
            CountRows(connection, "runs"),
            CountRows(connection, "files"),
            CountRows(connection, "exports"));
    }

    private static void UpsertDevice(
        SqliteConnection connection,
        string deviceKey,
        string kind,
        PnpDeviceCandidate candidate,
        int? usbDeviceNumber)
    {
        ExecuteNonQuery(
            connection,
            """
            INSERT OR REPLACE INTO devices(
                device_key, kind, display_name, vid, pid, location_path, port_name, usb_device_number)
            VALUES ($device_key, $kind, $display_name, $vid, $pid, $location_path, $port_name, $usb_device_number);
            """,
            ("$device_key", deviceKey),
            ("$kind", kind),
            ("$display_name", candidate.DisplayName),
            ("$vid", candidate.Vid),
            ("$pid", candidate.Pid),
            ("$location_path", candidate.LocationPath),
            ("$port_name", (object?)candidate.PortName ?? DBNull.Value),
            ("$usb_device_number", (object?)usbDeviceNumber ?? DBNull.Value));
    }

    private static void AddFile(
        SqliteConnection connection,
        Guid runId,
        string kind,
        string path,
        string datasetPath,
        DateTimeOffset createdAt)
    {
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO files(run_id, kind, path, dataset_path, created_at_utc)
            VALUES ($run_id, $kind, $path, $dataset_path, $created_at_utc);
            """,
            ("$run_id", runId.ToString("D")),
            ("$kind", kind),
            ("$path", Path.GetFullPath(path)),
            ("$dataset_path", datasetPath),
            ("$created_at_utc", createdAt.ToUniversalTime().ToString("O")));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private SqliteConnection OpenReadConnection()
    {
        var connection = new SqliteConnection(readOnlyConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table_name LIMIT 1;";
        command.Parameters.AddWithValue("$table_name", tableName);
        return command.ExecuteScalar() is not null;
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

    private static int CountRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = tableName switch
        {
            "sessions" => "SELECT COUNT(*) FROM sessions;",
            "devices" => "SELECT COUNT(*) FROM devices;",
            "pairings" => "SELECT COUNT(*) FROM pairings;",
            "runs" => "SELECT COUNT(*) FROM runs;",
            "files" => "SELECT COUNT(*) FROM files;",
            "exports" => "SELECT COUNT(*) FROM exports;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown catalog table.")
        };

        return Convert.ToInt32(command.ExecuteScalar());
    }
}

public sealed record EitCatalogSummary(
    int SessionCount,
    int DeviceCount,
    int PairingCount,
    int RunCount,
    int FileCount,
    int ExportCount);

public sealed record EitCatalogRunSummary(
    Guid RunId,
    Guid SessionId,
    string SetLabel,
    DateTimeOffset CapturedAt,
    string Hdf5Path,
    int SampleRows,
    int ChannelCount,
    int FileCount,
    int ExportCount,
    string? LatestDemodHdf5Path,
    string? LatestCsvPath);
