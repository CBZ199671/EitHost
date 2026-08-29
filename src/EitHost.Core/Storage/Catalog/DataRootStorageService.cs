namespace EitHost.Core.Storage.Catalog;

public enum DataRootCapacityState
{
    Normal,
    Warning,
    Critical,
    Unavailable
}

public sealed record DataRootVolumeInfo(
    long TotalBytes,
    long AvailableBytes);

public sealed record DataRootStorageSnapshot(
    string DataRootPath,
    long? ManagedBytes,
    long? TotalBytes,
    long? AvailableBytes,
    DataRootCapacityState State,
    bool ManagedSizeComplete,
    string? ErrorMessage,
    DateTimeOffset InspectedAt);

public interface IDataRootVolumeProbe
{
    DataRootVolumeInfo Inspect(string dataRootPath);
}

public interface IDataRootStorageService
{
    DataRootStorageSnapshot Inspect(bool includeManagedSize);

    void EnsureWriteCapacity(long estimatedArtifactBytes);
}

public sealed class DriveInfoDataRootVolumeProbe : IDataRootVolumeProbe
{
    public DataRootVolumeInfo Inspect(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        var fullPath = Path.GetFullPath(dataRootPath);
        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new IOException($"无法确定 DataRoot 所在卷：{fullPath}");
        }

        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady)
        {
            throw new IOException($"DataRoot 所在卷尚未就绪：{volumeRoot}");
        }

        return new DataRootVolumeInfo(drive.TotalSize, drive.AvailableFreeSpace);
    }
}

public sealed class DataRootStorageService : IDataRootStorageService
{
    public const long MinimumFreeReserveBytes = 2L * 1024L * 1024L * 1024L;
    public const long WarningFreeSpaceBytes = 10L * 1024L * 1024L * 1024L;

    private readonly string dataRootPath;
    private readonly IDataRootVolumeProbe volumeProbe;

    public DataRootStorageService(
        string dataRootPath,
        IDataRootVolumeProbe? volumeProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        this.dataRootPath = Path.GetFullPath(dataRootPath);
        this.volumeProbe = volumeProbe ?? new DriveInfoDataRootVolumeProbe();
    }

    public DataRootStorageSnapshot Inspect(bool includeManagedSize)
    {
        long? managedBytes = null;
        var managedSizeComplete = !includeManagedSize;
        string? managedSizeError = null;
        if (includeManagedSize)
        {
            (managedBytes, managedSizeComplete, managedSizeError) = CalculateManagedSize();
        }

        try
        {
            var volume = volumeProbe.Inspect(dataRootPath);
            ValidateVolume(volume);
            return new DataRootStorageSnapshot(
                dataRootPath,
                managedBytes,
                volume.TotalBytes,
                volume.AvailableBytes,
                Classify(volume.AvailableBytes),
                managedSizeComplete,
                managedSizeError,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            var error = string.IsNullOrWhiteSpace(managedSizeError)
                ? ex.Message
                : $"{ex.Message}；目录用量扫描：{managedSizeError}";
            return new DataRootStorageSnapshot(
                dataRootPath,
                managedBytes,
                null,
                null,
                DataRootCapacityState.Unavailable,
                managedSizeComplete,
                error,
                DateTimeOffset.UtcNow);
        }
    }

    public void EnsureWriteCapacity(long estimatedArtifactBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedArtifactBytes);
        DataRootVolumeInfo volume;
        try
        {
            volume = volumeProbe.Inspect(dataRootPath);
            ValidateVolume(volume);
        }
        catch (Exception ex)
        {
            throw InsufficientDataRootCapacityException.ProbeFailed(estimatedArtifactBytes, ex);
        }

        var requiredBytes = estimatedArtifactBytes > long.MaxValue - MinimumFreeReserveBytes
            ? long.MaxValue
            : estimatedArtifactBytes + MinimumFreeReserveBytes;
        if (volume.AvailableBytes < requiredBytes)
        {
            throw new InsufficientDataRootCapacityException(
                estimatedArtifactBytes,
                volume.AvailableBytes,
                MinimumFreeReserveBytes);
        }
    }

    private static DataRootCapacityState Classify(long availableBytes)
    {
        if (availableBytes < MinimumFreeReserveBytes)
        {
            return DataRootCapacityState.Critical;
        }

        return availableBytes < WarningFreeSpaceBytes
            ? DataRootCapacityState.Warning
            : DataRootCapacityState.Normal;
    }

    private static void ValidateVolume(DataRootVolumeInfo volume)
    {
        if (volume.TotalBytes <= 0 || volume.AvailableBytes < 0 || volume.AvailableBytes > volume.TotalBytes)
        {
            throw new IOException(
                $"DataRoot 容量探测返回无效结果：total={volume.TotalBytes}, available={volume.AvailableBytes}。");
        }
    }

    private (long Bytes, bool Complete, string? Error) CalculateManagedSize()
    {
        if (!Directory.Exists(dataRootPath))
        {
            return (0, true, null);
        }

        var bytes = 0L;
        var complete = true;
        string? firstError = null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var path in Directory.EnumerateFiles(dataRootPath, "*", options))
            {
                try
                {
                    var length = new FileInfo(path).Length;
                    bytes = length > long.MaxValue - bytes ? long.MaxValue : bytes + length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    firstError ??= ex.Message;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            complete = false;
            firstError ??= ex.Message;
        }

        return (bytes, complete, firstError);
    }
}

public sealed class InsufficientDataRootCapacityException : IOException
{
    public InsufficientDataRootCapacityException(
        long estimatedArtifactBytes,
        long availableBytes,
        long reserveBytes)
        : base(CreateCapacityMessage(estimatedArtifactBytes, availableBytes, reserveBytes))
    {
        EstimatedArtifactBytes = estimatedArtifactBytes;
        AvailableBytes = availableBytes;
        ReserveBytes = reserveBytes;
    }

    private InsufficientDataRootCapacityException(
        long estimatedArtifactBytes,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        EstimatedArtifactBytes = estimatedArtifactBytes;
    }

    public long EstimatedArtifactBytes { get; }

    public long? AvailableBytes { get; }

    public long? ReserveBytes { get; }

    internal static InsufficientDataRootCapacityException ProbeFailed(
        long estimatedArtifactBytes,
        Exception innerException)
    {
        return new InsufficientDataRootCapacityException(
            estimatedArtifactBytes,
            $"无法确认 DataRoot 磁盘余量，已拒绝写入以保护实验数据：{innerException.Message}",
            innerException);
    }

    private static string CreateCapacityMessage(
        long estimatedArtifactBytes,
        long availableBytes,
        long reserveBytes)
    {
        return $"DataRoot 磁盘空间不足：预计写入 {FormatBytes(estimatedArtifactBytes)}，" +
               $"当前可用 {FormatBytes(availableBytes)}，写入后必须保留至少 {FormatBytes(reserveBytes)}（2 GiB）。";
    }

    private static string FormatBytes(long bytes)
    {
        const double gib = 1024.0 * 1024.0 * 1024.0;
        const double mib = 1024.0 * 1024.0;
        return bytes >= gib
            ? $"{bytes / gib:F1} GiB"
            : $"{bytes / mib:F1} MiB";
    }
}
