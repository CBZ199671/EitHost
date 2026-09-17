using System.Text.Json;
using EitHost.Core.Acquisition;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Storage.Catalog;

public sealed record Pseudo3dExperimentMember(Guid GroupId, Guid RunId, int Slot, string SetLabel);

public sealed partial class ExperimentCatalog
{
    private readonly HashSet<Guid> nonPseudo3dRuns = [];
    private readonly object pseudo3dRecoveryGate = new();
    public void RegisterPseudo3dMember(Guid runId, Guid groupId, int slot)
    {
        if (runId == Guid.Empty || groupId == Guid.Empty || slot is < 0 or > 1)
            throw new ArgumentException("Invalid pseudo-3D group identity.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT group_id, slot FROM pseudo3d_members WHERE experiment_run_id=$run";
        query.Parameters.AddWithValue("$run", runId.ToString("D"));
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                if (Guid.Parse(reader.GetString(0)) != groupId || reader.GetInt32(1) != slot)
                    throw new InvalidDataException("Pseudo-3D run already belongs to a different group or layer.");
                transaction.Commit();
                return;
            }
        }
        ExecuteNonQuery(connection,
            "INSERT INTO pseudo3d_members(experiment_run_id,group_id,slot) VALUES($run,$group,$slot)",
            ("$run", runId.ToString("D")), ("$group", groupId.ToString("D")), ("$slot", slot));
        transaction.Commit();
    }

    public IReadOnlyList<Pseudo3dExperimentMember> ListPseudo3dMembers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.group_id,m.experiment_run_id,m.slot,r.set_label
            FROM pseudo3d_members m JOIN experiment_runs r USING(experiment_run_id)
            ORDER BY m.group_id,m.slot
            """;
        using var reader = command.ExecuteReader();
        var members = new List<Pseudo3dExperimentMember>();
        while (reader.Read()) members.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetInt32(2), reader.GetString(3)));
        return members;
    }

    /// <summary>Recover only explicit persisted session/slot identities, never timing guesses.</summary>
    public int RecoverPseudo3dMembers(Action<string>? diagnostic = null, IReadOnlySet<Guid>? runIds = null)
    {
        lock (pseudo3dRecoveryGate) return RecoverPseudo3dMembersCore(diagnostic, runIds);
    }

    private int RecoverPseudo3dMembersCore(Action<string>? diagnostic, IReadOnlySet<Guid>? runIds)
    {
        var candidates = new List<(Guid RunId, int Block, string Path, string Dataset)>();
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.experiment_run_id,a.block_number,a.artifact_path,a.dataset_path
                FROM derived_artifacts a JOIN experiment_runs r USING(experiment_run_id)
                WHERE a.kind='demod' AND r.status<>'recording'
                AND NOT EXISTS(SELECT 1 FROM pseudo3d_members m WHERE m.experiment_run_id=a.experiment_run_id)
                AND a.block_number=(SELECT MIN(b.block_number) FROM derived_artifacts b
                    WHERE b.experiment_run_id=a.experiment_run_id AND b.kind='demod')
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
                reader.GetString(2), reader.GetString(3)));
        }
        var recovered = 0;
        foreach (var candidate in candidates)
        {
            if (nonPseudo3dRuns.Contains(candidate.RunId) || (runIds is not null && !runIds.Contains(candidate.RunId))) continue;
            try
            {
                Pseudo3dAcquisitionStamp? stamp;
                using (var file = Hdf5FileAccess.OpenReadWithRetry(layout.ResolveArtifactPath(candidate.Path)))
                {
                    var root = DataRootLayout.GetDerivedBlockRoot(candidate.Block);
                    var stampPath = root + "/demod/time_division_json";
                    if (!file.LinkExists(stampPath)) { nonPseudo3dRuns.Add(candidate.RunId); continue; }
                    if (Guid.Parse(file.Dataset(root + "/metadata/run/experiment_run_id").Read<string>()) != candidate.RunId ||
                        file.Dataset(root + "/metadata/run/block_number").Read<int>() != candidate.Block)
                        throw new InvalidDataException("Pseudo-3D demodulation identity mismatch.");
                    stamp = JsonSerializer.Deserialize<Pseudo3dAcquisitionStamp>(file.Dataset(stampPath).Read<string>());
                }
                if (stamp is null || stamp.Round != candidate.Block)
                    throw new InvalidDataException("Pseudo-3D round identity is missing or inconsistent.");
                RegisterPseudo3dMember(candidate.RunId, stamp.SessionId, stamp.Slot);
                var run = GetRun(candidate.RunId)!;
                var coverage = GetCoverage(candidate.RunId);
                if (run.Status == CompletedStatus && run.RawStatus == "empty" && coverage.RawSampleRows > 0 &&
                    ListRawSegments(candidate.RunId).All(segment => segment.Status == "ready"))
                    SetRunStageStatuses(candidate.RunId, "complete", run.DemodStatus, run.ReconstructionStatus);
                recovered++;
            }
            catch (Exception ex)
            {
                diagnostic?.Invoke($"伪三维历史分组恢复失败：{candidate.RunId:D} · {ex.Message}");
            }
        }
        return recovered;
    }
}
