using System.Diagnostics;
using System.Runtime.InteropServices;
using PureHDF;
using PureHDF.VOL.Native;

namespace EitHost.Core.Storage.Hdf5;

public static class Hdf5FileAccess
{
    public static NativeFile OpenReadWithRetry(string path) =>
        AtomicFileCommitter.ExecuteWithTransientLeaseRetry(path, () => H5File.OpenRead(path));
}

internal static class AtomicFileCommitter
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200)
    ];

    private static readonly TimeSpan[] LeaseRetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800)
    ];

    internal static void MoveWithRetry(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        Action<string, string, bool>? move = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        move ??= static (source, destination, replace) => File.Move(source, destination, replace);
        delay ??= Thread.Sleep;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                move(sourcePath, destinationPath, overwrite);
                return;
            }
            catch (Exception ex) when (ShouldRetry(ex, destinationPath, overwrite, attempt))
            {
                delay(RetryDelays[attempt]);
            }
        }
    }

    internal static void CopyWithRetry(
        string sourcePath,
        string destinationPath,
        bool overwrite,
        Action<string, string, bool>? copy = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        copy ??= static (source, destination, replace) => File.Copy(source, destination, replace);
        delay ??= Thread.Sleep;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                copy(sourcePath, destinationPath, overwrite);
                return;
            }
            catch (Exception ex) when (ShouldRetry(ex, destinationPath, overwrite, attempt))
            {
                delay(RetryDelays[attempt]);
            }
        }
    }

    internal static void DeleteBestEffort(
        string path,
        Action<string>? delete = null,
        Action<Exception>? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        delete ??= File.Delete;
        try
        {
            delete(path);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            try
            {
                diagnostic?.Invoke(ex);
                Trace.TraceWarning($"Atomic file cleanup failed path='{path}': {ex}");
            }
            catch (Exception diagnosticError) when (!IsFatal(diagnosticError))
            {
                // Diagnostics and cleanup must never replace the primary persistence failure.
            }
        }
    }

    internal static T ExecuteWithTransientLeaseRetry<T>(
        string path,
        Func<T> operation,
        Action<TimeSpan>? delay = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(operation);
        delay ??= Thread.Sleep;
        shouldRetry ??= IsTransientLeaseFailure;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (
                attempt < LeaseRetryDelays.Length &&
                shouldRetry(ex))
            {
                delay(LeaseRetryDelays[attempt]);
            }
        }
    }

    internal static bool IsTransientLeaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is FileNotFoundException or DirectoryNotFoundException ||
            exception is not IOException)
        {
            return false;
        }

        var windowsError = exception.HResult & 0xFFFF;
        return windowsError is 0x20 or 0x21;
    }

    internal static bool IsFileBlockedByTransientLease(string path, FileAccess access)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var probe = new FileStream(
                path,
                FileMode.Open,
                access,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (Exception ex) when (IsTransientLeaseFailure(ex))
        {
            return true;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return false;
        }
    }

    internal static void DeleteDirectoryWithRetry(
        string path,
        bool recursive,
        Action<string, bool>? delete = null,
        Action<TimeSpan>? delay = null)
    {
        delete ??= Directory.Delete;
        ExecuteWithTransientLeaseRetry(
            path,
            () =>
            {
                delete(path, recursive);
                return true;
            },
            delay);
    }

    internal static void MoveDirectoryWithRetry(
        string sourcePath,
        string destinationPath,
        Action<string, string>? move = null,
        Action<TimeSpan>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        move ??= Directory.Move;
        ExecuteWithTransientLeaseRetry(
            sourcePath,
            () =>
            {
                move(sourcePath, destinationPath);
                return true;
            },
            delay,
            exception => IsTransientDirectoryMoveFailure(
                exception,
                sourcePath,
                destinationPath));
    }

    private static bool IsTransientDirectoryMoveFailure(
        Exception exception,
        string sourcePath,
        string destinationPath)
    {
        if (!Directory.Exists(sourcePath) || Directory.Exists(destinationPath))
        {
            return false;
        }

        if (exception is UnauthorizedAccessException)
        {
            // Windows reports an open child handle during a directory rename as
            // ERROR_ACCESS_DENIED rather than ERROR_SHARING_VIOLATION on some VFDs.
            return OperatingSystem.IsWindows();
        }

        if (exception is not IOException)
        {
            return false;
        }

        var windowsError = exception.HResult & 0xFFFF;
        return windowsError is 0x05 or 0x20 or 0x21;
    }

    private static bool ShouldRetry(
        Exception exception,
        string destinationPath,
        bool overwrite,
        int attempt)
    {
        if (attempt >= RetryDelays.Length ||
            exception is not (IOException or UnauthorizedAccessException))
        {
            return false;
        }

        return overwrite ||
            exception is not IOException ||
            !File.Exists(destinationPath);
    }

    internal static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or SEHException;
}
