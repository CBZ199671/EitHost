using System.Diagnostics;
using EitHost.Core.Reconstruction;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationBatchRunner
{
    public const string DefaultDistroName = "Ubuntu-22.04";
    public const string DefaultPyEidorsRoot = "";

    public ProcessStartInfo CreateStartInfo(
        EcdCwrSimulationWorkItem item,
        EcdCwrSimulationBatchRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);

        var requestPath = WslPathMapper.ToWslPath(item.RequestJsonPath);
        var backendCommand = RequireBackendCommand(options);
        var command = string.Join(
            " ",
            "set -e;",
            "cd",
            BashQuote(options.PyEidorsRoot),
            "&&",
            backendCommand,
            "ecd-cwr-simulate-cem",
            "--input",
            BashQuote(requestPath));

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(options.DistroName);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    public ProcessStartInfo CreateServeStartInfo(EcdCwrSimulationBatchRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var backendCommand = RequireBackendCommand(options);
        var command = string.Join(
            " ",
            "set -e;",
            "cd",
            BashQuote(options.PyEidorsRoot),
            "&&",
            backendCommand,
            "serve");

        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(options.DistroName);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    public EcdCwrSimulationBatchRunSelection SelectWorkItems(
        EcdCwrSimulationBatchManifest manifest,
        EcdCwrSimulationBatchRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var candidates = manifest.WorkItems
            .Skip(Math.Max(0, options.StartIndex))
            .Where(item => options.ScenarioIds.Count == 0 || options.ScenarioIds.Contains(item.ScenarioId))
            .ToArray();
        var eligible = candidates
            .Where(item => IsEligible(item, manifest.EmitContactJacobian, manifest.EmitMultiFrequency, options))
            .ToArray();
        var runnable = eligible
            .Take(options.Limit.GetValueOrDefault(int.MaxValue))
            .ToArray();

        return new EcdCwrSimulationBatchRunSelection(
            manifest.WorkItems.Count,
            candidates.Length,
            candidates.Length - eligible.Length,
            runnable.Length,
            runnable);
    }

    public static string ToMarkdown(EcdCwrSimulationBatchRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var lines = new List<string>
        {
            "# ECD-CWR CEM Batch Run",
            "",
            $"- Started: {summary.StartedAt:O}",
            $"- Ended: {summary.EndedAt:O}",
            $"- Manifest: `{summary.ManifestPath}`",
            $"- Distro: `{summary.Options.DistroName}`",
            $"- PyEIDORS root: `{summary.Options.PyEidorsRoot}`",
            $"- Backend command: `{summary.Options.BackendCommand}`",
            $"- Total manifest items: {summary.TotalManifestItems}",
            $"- Selected items: {summary.SelectedItems}",
            $"- Succeeded: {summary.Succeeded}",
            $"- Failed: {summary.Failed}",
            $"- Skipped existing: {summary.SkippedExisting}",
            ""
        };
        lines.Add("## Items");
        lines.Add("");
        lines.Add("|scenario|status|exit|duration_ms|result|label|");
        lines.Add("|---|---:|---:|---:|---|---|");
        foreach (var item in summary.Items)
        {
            lines.Add(
                $"|{item.ScenarioId}|{item.Status}|{item.ExitCode}|{item.Duration.TotalMilliseconds:F0}|`{item.OutputHdf5Path}`|`{item.LabelJsonPath}`|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string BashQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string RequireBackendCommand(EcdCwrSimulationBatchRunOptions options)
    {
        return string.IsNullOrWhiteSpace(options.BackendCommand)
            ? throw new InvalidOperationException(
                "ECD-CWR 仿真未选择 PyEIDORS 后端。请提供 --backend-profile 或 --backend-command。")
            : options.BackendCommand.Trim();
    }

    private static bool IsEligible(
        EcdCwrSimulationWorkItem item,
        bool requireContactJacobian,
        bool requireMultiFrequency,
        EcdCwrSimulationBatchRunOptions options)
    {
        if (options.SkipReadyResults)
        {
            return !EcdCwrSimulationDatasetValidator
                .ValidateWorkItem(item, requireContactJacobian, requireMultiFrequency)
                .Passed;
        }

        return !options.SkipExistingResults ||
            !File.Exists(item.OutputHdf5Path) ||
            !File.Exists(item.LabelJsonPath);
    }
}

public sealed record EcdCwrSimulationBatchRunOptions(
    string DistroName,
    string PyEidorsRoot,
    string BackendCommand,
    int StartIndex,
    int? Limit,
    bool SkipExistingResults,
    bool SkipReadyResults,
    bool ContinueOnError,
    bool CreateMissingRequests,
    bool RefreshRequests,
    bool UsePersistentWorker,
    IReadOnlySet<string> ScenarioIds)
{
    public static EcdCwrSimulationBatchRunOptions Default { get; } = new(
        EcdCwrSimulationBatchRunner.DefaultDistroName,
        EcdCwrSimulationBatchRunner.DefaultPyEidorsRoot,
        string.Empty,
        StartIndex: 0,
        Limit: null,
        SkipExistingResults: false,
        SkipReadyResults: false,
        ContinueOnError: false,
        CreateMissingRequests: false,
        RefreshRequests: false,
        UsePersistentWorker: false,
        ScenarioIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record EcdCwrSimulationBatchRunSelection(
    int TotalManifestItems,
    int CandidateItems,
    int SkippedExisting,
    int SelectedItems,
    IReadOnlyList<EcdCwrSimulationWorkItem> WorkItems);

public sealed record EcdCwrSimulationBatchRunSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string ManifestPath,
    EcdCwrSimulationBatchRunOptions Options,
    int TotalManifestItems,
    int SelectedItems,
    int Succeeded,
    int Failed,
    int SkippedExisting,
    IReadOnlyList<EcdCwrSimulationBatchRunItem> Items);

public sealed record EcdCwrSimulationBatchRunItem(
    string ScenarioId,
    string Status,
    int ExitCode,
    TimeSpan Duration,
    string OutputHdf5Path,
    string LabelJsonPath,
    string StandardOutput,
    string StandardError);
