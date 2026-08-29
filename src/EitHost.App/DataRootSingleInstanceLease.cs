using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EitHost.App;

public sealed class DataRootSingleInstanceLease : IDisposable
{
    private FileStream? leaseFile;
    private Mutex? mutex;

    private DataRootSingleInstanceLease(
        string dataRootPath,
        string mutexName,
        string leaseFilePath,
        Mutex mutex,
        FileStream leaseFile)
    {
        DataRootPath = dataRootPath;
        MutexName = mutexName;
        LeaseFilePath = leaseFilePath;
        this.mutex = mutex;
        this.leaseFile = leaseFile;
    }

    public string DataRootPath { get; }

    public string MutexName { get; }

    public string LeaseFilePath { get; }

    public static DataRootSingleInstanceLease? TryAcquire(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRootPath));
        var mutexKey = normalized.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mutexKey)));
        var mutexName = $"Global\\EitHost.DataRoot.{hash}";
        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        FileStream? leaseFile = null;
        try
        {
            Directory.CreateDirectory(normalized);
            var leaseFilePath = Path.Combine(normalized, ".eithost.writer.lock");
            leaseFile = new FileStream(
                leaseFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            leaseFile.SetLength(0);
            using (var writer = new StreamWriter(
                       leaseFile,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       leaveOpen: true))
            {
                writer.WriteLine($"pid={Environment.ProcessId}");
                writer.WriteLine($"machine={Environment.MachineName}");
                writer.WriteLine($"acquired_at_utc={DateTimeOffset.UtcNow:O}");
                writer.WriteLine($"data_root={normalized}");
                writer.Flush();
            }

            leaseFile.Flush(flushToDisk: true);
            return new DataRootSingleInstanceLease(
                normalized,
                mutexName,
                leaseFilePath,
                mutex,
                leaseFile);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            ReleaseAcquisition(mutex, leaseFile);
            return null;
        }
        catch
        {
            ReleaseAcquisition(mutex, leaseFile);
            throw;
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }

    private static void ReleaseAcquisition(Mutex mutex, FileStream? leaseFile)
    {
        leaseFile?.Dispose();
        mutex.ReleaseMutex();
        mutex.Dispose();
    }

    public void ValidateDataRoot(string actualDataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualDataRootPath);
        var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualDataRootPath));
        if (!string.Equals(DataRootPath, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Writer lease DataRoot mismatch: lease='{DataRootPath}', runtime='{actual}'.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref leaseFile, null)?.Dispose();
        var ownedMutex = Interlocked.Exchange(ref mutex, null);
        if (ownedMutex is null)
        {
            return;
        }

        try
        {
            ownedMutex.ReleaseMutex();
        }
        finally
        {
            ownedMutex.Dispose();
        }
    }
}
