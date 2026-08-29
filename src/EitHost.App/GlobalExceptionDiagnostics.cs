using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EitHost.App;

internal static class GlobalExceptionDiagnostics
{
    private const int MaxDiagnosticCharacters = 64 * 1024;
    private const long DefaultMaxLogBytes = 8L * 1024 * 1024;
    private static readonly object FileGate = new();

    internal static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EitHost",
        "realtime-startup.log");

    internal static bool IsFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or SEHException;
    }

    internal static void Record(
        string source,
        object? error,
        bool terminating = false,
        string? logPath = null,
        long maxLogBytes = DefaultMaxLogBytes)
    {
        try
        {
            var detail = error switch
            {
                Exception exception => exception.ToString(),
                null => "<null>",
                _ => error.ToString() ?? error.GetType().FullName ?? "<unknown>"
            };
            if (detail.Length > MaxDiagnosticCharacters)
            {
                detail = detail[..MaxDiagnosticCharacters] + "\n<truncated>";
            }

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" unhandled source=")
                .Append(source)
                .Append(" terminating=")
                .Append(terminating)
                .AppendLine()
                .AppendLine(detail)
                .ToString();
            Append(entry, logPath, maxLogBytes);
        }
        catch
        {
            // An emergency diagnostic must never replace the original failure.
        }
    }

    internal static void RecordRealtimeMessage(
        string message,
        string? logPath = null,
        long maxLogBytes = DefaultMaxLogBytes)
    {
        try
        {
            var entry = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
            Append(entry, logPath, maxLogBytes);
        }
        catch
        {
            // Diagnostics must never disturb hardware acquisition.
        }
    }

    private static void Append(string entry, string? logPath, long maxLogBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLogBytes);
        var path = string.IsNullOrWhiteSpace(logPath) ? LogPath : Path.GetFullPath(logPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (FileGate)
        {
            if (!RotateIfNeeded(path, entry, maxLogBytes))
            {
                return;
            }

            File.AppendAllText(path, entry, Encoding.UTF8);
        }
    }

    private static bool RotateIfNeeded(string path, string entry, long maxLogBytes)
    {
        if (!File.Exists(path) ||
            new FileInfo(path).Length + Encoding.UTF8.GetByteCount(entry) <= maxLogBytes)
        {
            return true;
        }

        try
        {
            File.Move(path, path + ".1", overwrite: true);
            return true;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Trace.TraceWarning($"Realtime diagnostic rotation failed path='{path}': {ex}");
        }

        try
        {
            File.WriteAllText(path, string.Empty, Encoding.UTF8);
            return true;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Trace.TraceWarning($"Realtime diagnostic truncation failed path='{path}': {ex}");
            return false;
        }
    }
}
