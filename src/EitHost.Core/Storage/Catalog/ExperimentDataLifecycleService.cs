using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Storage.Catalog;

public interface IExperimentDataLifecycleService
{
    ExperimentRunStorageInspection Inspect(
        Guid experimentRunId,
        DateTimeOffset now,
        int retentionDays = ExperimentDataLifecycleService.DefaultRetentionDays);

    ExperimentArchiveResult Archive(Guid experimentRunId, DateTimeOffset archivedAt);

    ExperimentArchiveResult Archive(
        Guid experimentRunId,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken,
        IProgress<ExperimentArchiveProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Archive(experimentRunId, archivedAt);
    }

    ExperimentDeleteResult Delete(Guid experimentRunId);
}

public sealed class ExperimentDataLifecycleService : IExperimentDataLifecycleService
{
    public const int DefaultRetentionDays = 90;

    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly ExperimentRunOperationGate operationGate;

    public ExperimentDataLifecycleService(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        ExperimentRunOperationGate? operationGate = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.operationGate = operationGate ?? new ExperimentRunOperationGate();
    }

    public ExperimentRunStorageInspection Inspect(
        Guid experimentRunId,
        DateTimeOffset now,
        int retentionDays = DefaultRetentionDays)
    {
        return InspectCore(
            experimentRunId,
            now,
            retentionDays,
            CancellationToken.None,
            progress: null);
    }

    private ExperimentRunStorageInspection InspectCore(
        Guid experimentRunId,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken cancellationToken,
        IProgress<ExperimentArchiveProgress>? progress)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);
        var run = GetTerminalOrRecordingRun(experimentRunId);
        var directory = layout.ResolveArtifactPath(run.RunDirectory);
        var (managedBytes, filesScanned, complete, errorMessage) = MeasureDirectory(
            experimentRunId,
            directory,
            cancellationToken,
            progress);
        var isArchived = string.Equals(
            run.LifecycleState,
            ExperimentCatalog.ArchivedLifecycleState,
            StringComparison.Ordinal);
        var retentionAnchor = run.EndedAt ?? run.StartedAt;
        return new ExperimentRunStorageInspection(
            experimentRunId,
            directory,
            managedBytes,
            complete,
            isArchived,
            !isArchived &&
            !string.Equals(run.Status, ExperimentCatalog.RecordingStatus, StringComparison.Ordinal) &&
            retentionAnchor <= now.AddDays(-retentionDays),
            run.ArchivedAt,
            errorMessage,
            filesScanned);
    }

    public ExperimentArchiveResult Archive(Guid experimentRunId, DateTimeOffset archivedAt)
    {
        return Archive(
            experimentRunId,
            archivedAt,
            CancellationToken.None,
            progress: null);
    }

    public ExperimentArchiveResult Archive(
        Guid experimentRunId,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken,
        IProgress<ExperimentArchiveProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operationLease = operationGate.Enter(
            experimentRunId,
            ExperimentRunOperation.Archive);
        var run = GetTerminalRun(experimentRunId);
        if (!string.Equals(
                run.LifecycleState,
                ExperimentCatalog.ActiveLifecycleState,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only an active terminal run can be archived.");
        }

        var source = layout.ResolveArtifactPath(run.RunDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Run directory does not exist: {source}");
        }

        var target = layout.GetArchiveDirectory(experimentRunId, run.StartedAt);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"Archive target already exists: {target}");
        }

        var inspection = InspectCore(
            experimentRunId,
            archivedAt,
            DefaultRetentionDays,
            cancellationToken,
            progress);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ExperimentArchiveProgress(
            experimentRunId,
            ExperimentArchivePhase.Moving,
            inspection.FilesScanned,
            inspection.ManagedBytes));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFileCommitter.MoveDirectoryWithRetry(source, target);
        var archiveRelativePath = layout.ToRelativeArtifactPath(target);
        try
        {
            progress?.Report(new ExperimentArchiveProgress(
                experimentRunId,
                ExperimentArchivePhase.CatalogCommit,
                inspection.FilesScanned,
                inspection.ManagedBytes));
            catalog.ArchiveTerminalRun(
                experimentRunId,
                run.RunDirectory,
                archiveRelativePath,
                archivedAt);
        }
        catch (Exception catalogError)
        {
            RollBackMove(target, source, catalogError, "archive catalog commit");
            throw;
        }

        progress?.Report(new ExperimentArchiveProgress(
            experimentRunId,
            ExperimentArchivePhase.Completed,
            inspection.FilesScanned,
            inspection.ManagedBytes));

        return new ExperimentArchiveResult(
            experimentRunId,
            source,
            target,
            inspection.ManagedBytes,
            archivedAt);
    }

    public ExperimentDeleteResult Delete(Guid experimentRunId)
    {
        using var operationLease = operationGate.Enter(
            experimentRunId,
            ExperimentRunOperation.Delete);
        var run = GetTerminalRun(experimentRunId);
        var source = layout.ResolveArtifactPath(run.RunDirectory);
        var inspection = Inspect(experimentRunId, DateTimeOffset.UtcNow);
        string? staging = null;
        if (Directory.Exists(source))
        {
            staging = layout.CreateTrashStagingDirectoryPath(experimentRunId);
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            AtomicFileCommitter.MoveDirectoryWithRetry(source, staging);
        }

        try
        {
            catalog.DeleteTerminalRun(experimentRunId);
        }
        catch (Exception catalogError)
        {
            if (staging is not null)
            {
                RollBackMove(staging, source, catalogError, "delete catalog commit");
            }

            throw;
        }

        if (staging is null)
        {
            return new ExperimentDeleteResult(
                experimentRunId,
                inspection.ManagedBytes,
                CleanupComplete: true,
                RecoveryDirectoryPath: null,
                CleanupErrorMessage: null);
        }

        try
        {
            AtomicFileCommitter.DeleteDirectoryWithRetry(staging, recursive: true);
            return new ExperimentDeleteResult(
                experimentRunId,
                inspection.ManagedBytes,
                CleanupComplete: true,
                RecoveryDirectoryPath: null,
                CleanupErrorMessage: null);
        }
        catch (Exception cleanupError)
        {
            return new ExperimentDeleteResult(
                experimentRunId,
                inspection.ManagedBytes,
                CleanupComplete: false,
                RecoveryDirectoryPath: staging,
                CleanupErrorMessage: cleanupError.Message);
        }
    }

    private ExperimentRunRecord GetTerminalOrRecordingRun(Guid experimentRunId)
    {
        return catalog.GetRun(experimentRunId) ?? throw new KeyNotFoundException(
            $"Experiment run {experimentRunId:D} does not exist.");
    }

    private ExperimentRunRecord GetTerminalRun(Guid experimentRunId)
    {
        var run = GetTerminalOrRecordingRun(experimentRunId);
        if (run.Status is not (
                ExperimentCatalog.CompletedStatus or
                ExperimentCatalog.InterruptedStatus or
                ExperimentCatalog.FailedStatus))
        {
            throw new InvalidOperationException(
                "Recording or non-terminal runs cannot be archived or deleted.");
        }

        return run;
    }

    private static (long ManagedBytes, long FilesScanned, bool Complete, string? ErrorMessage) MeasureDirectory(
        Guid experimentRunId,
        string directory,
        CancellationToken cancellationToken,
        IProgress<ExperimentArchiveProgress>? progress)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0, false, $"Run directory does not exist: {directory}");
        }

        long total = 0;
        long filesScanned = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + new FileInfo(file).Length);
                filesScanned++;
                if (filesScanned == 1 || filesScanned % 256 == 0)
                {
                    progress?.Report(new ExperimentArchiveProgress(
                        experimentRunId,
                        ExperimentArchivePhase.Scanning,
                        filesScanned,
                        total));
                }
            }

            progress?.Report(new ExperimentArchiveProgress(
                experimentRunId,
                ExperimentArchivePhase.Scanning,
                filesScanned,
                total));
            cancellationToken.ThrowIfCancellationRequested();
            return (total, filesScanned, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (total, filesScanned, false, ex.Message);
        }
    }

    private static void RollBackMove(
        string currentPath,
        string originalPath,
        Exception originalError,
        string operation)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            AtomicFileCommitter.MoveDirectoryWithRetry(currentPath, originalPath);
        }
        catch (Exception rollbackError)
        {
            throw new InvalidOperationException(
                $"{operation} failed and directory rollback also failed. " +
                $"Current data path: {currentPath}; expected path: {originalPath}.",
                new AggregateException(originalError, rollbackError));
        }
    }
}

public sealed record ExperimentRunStorageInspection(
    Guid ExperimentRunId,
    string DirectoryPath,
    long ManagedBytes,
    bool SizeComplete,
    bool IsArchived,
    bool RetentionCandidate,
    DateTimeOffset? ArchivedAt,
    string? ErrorMessage,
    long FilesScanned = 0);

public enum ExperimentArchivePhase
{
    Scanning,
    Moving,
    CatalogCommit,
    Completed
}

public sealed record ExperimentArchiveProgress(
    Guid ExperimentRunId,
    ExperimentArchivePhase Phase,
    long FilesScanned,
    long BytesScanned);

public sealed record ExperimentArchiveResult(
    Guid ExperimentRunId,
    string SourceDirectoryPath,
    string ArchiveDirectoryPath,
    long ManagedBytes,
    DateTimeOffset ArchivedAt);

public sealed record ExperimentDeleteResult(
    Guid ExperimentRunId,
    long ManagedBytes,
    bool CleanupComplete,
    string? RecoveryDirectoryPath,
    string? CleanupErrorMessage);
