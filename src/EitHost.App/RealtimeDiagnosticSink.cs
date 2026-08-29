using System.IO;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App;

internal sealed class RealtimeDiagnosticSink
{
    private readonly object gate = new();
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly string globalLogPath;
    private readonly Dictionary<Guid, string> activeRunLogPaths = [];

    internal RealtimeDiagnosticSink(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        string? globalLogPath = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.globalLogPath = string.IsNullOrWhiteSpace(globalLogPath)
            ? layout.ResolveArtifactPath(Path.Combine("diagnostics", "realtime-startup.log"))
            : Path.GetFullPath(globalLogPath);
    }

    internal void BeginRun(Guid experimentRunId, string runRelativeDirectory)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(experimentRunId, Guid.Empty);
        var runLogPath = layout.GetRealtimeDiagnosticPath(runRelativeDirectory);
        lock (gate)
        {
            activeRunLogPaths[experimentRunId] = runLogPath;
        }
    }

    internal void EndRun(Guid experimentRunId)
    {
        lock (gate)
        {
            activeRunLogPaths.Remove(experimentRunId);
        }
    }

    internal void Record(string message)
    {
        GlobalExceptionDiagnostics.RecordRealtimeMessage(message, globalLogPath);
        string[] runLogPaths;
        lock (gate)
        {
            runLogPaths = activeRunLogPaths.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        foreach (var runLogPath in runLogPaths)
        {
            GlobalExceptionDiagnostics.RecordRealtimeMessage(message, runLogPath);
        }
    }

    internal void RecordForRun(Guid experimentRunId, string message)
    {
        GlobalExceptionDiagnostics.RecordRealtimeMessage(message, globalLogPath);
        var runLogPath = GetRunLogPath(experimentRunId);
        if (runLogPath is not null)
        {
            GlobalExceptionDiagnostics.RecordRealtimeMessage(message, runLogPath);
        }
    }

    private string? GetRunLogPath(Guid experimentRunId)
    {
        lock (gate)
        {
            if (activeRunLogPaths.TryGetValue(experimentRunId, out var activePath))
            {
                return activePath;
            }
        }

        try
        {
            var run = catalog.GetRun(experimentRunId);
            return run is null ? null : layout.GetRealtimeDiagnosticPath(run.RunDirectory);
        }
        catch (Exception ex) when (!GlobalExceptionDiagnostics.IsFatal(ex))
        {
            GlobalExceptionDiagnostics.Record(
                "run-diagnostic-path",
                ex,
                logPath: globalLogPath);
            return null;
        }
    }
}
