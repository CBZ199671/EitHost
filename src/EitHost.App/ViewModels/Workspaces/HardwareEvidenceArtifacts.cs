using System.IO;
using System.Security.Cryptography;
using System.Text;
using EitHost.Core.Hardware.Pnp;

namespace EitHost.App.ViewModels.Workspaces;

internal static class HardwareEvidenceArtifacts
{
    internal static string CreatePairingManifestMarkdown(
        PairingManifest manifest,
        string jsonPath,
        string currentJsonPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# EIT 会话配对清单");
        builder.AppendLine();
        builder.AppendLine($"生成时间：`{manifest.GeneratedAt:O}`");
        builder.AppendLine($"会话：`{manifest.SessionName}`");
        builder.AppendLine($"SessionId：`{manifest.SessionId}`");
        builder.AppendLine($"绑定套数：`{manifest.SetCount}`");
        builder.AppendLine();
        builder.AppendLine("## 文件");
        builder.AppendLine();
        builder.AppendLine($"- 历史 Markdown：`{manifest.MarkdownPath}`");
        builder.AppendLine($"- 历史 JSON：`{jsonPath}`");
        builder.AppendLine($"- 最新 Markdown：`{manifest.CurrentMarkdownPath}`");
        builder.AppendLine($"- 最新 JSON：`{currentJsonPath}`");
        builder.AppendLine();
        builder.AppendLine("## 当前激励参数");
        builder.AppendLine();
        builder.AppendLine($"- frequency：`{manifest.Excitation.FrequencyHz}` Hz");
        builder.AppendLine($"- DAC：channel `{manifest.Excitation.DacChannel}`，current `{FormatDdsCurrentLabel(manifest.Excitation.DacGain)}`，gain `{manifest.Excitation.DacGain}`，phase `{manifest.Excitation.DacPhaseDegrees}` deg");
        builder.AppendLine($"- PGA：`{manifest.Excitation.PgaGain}`");
        builder.AppendLine($"- mode：`{manifest.Excitation.Mode}`，channel cycles `{manifest.Excitation.ChannelCycles}`，scan times `{manifest.Excitation.ScanTimes}`，overhead `{manifest.Excitation.OverheadUs}` us");
        builder.AppendLine();
        builder.AppendLine("## 当前采集参数");
        builder.AppendLine();
        builder.AppendLine($"- sample-rate：`{manifest.Acquisition.SampleRateHz}` Hz");
        builder.AppendLine($"- range：`{manifest.Acquisition.Range}`");
        builder.AppendLine($"- trigger：`{manifest.Acquisition.TriggerMode}` / `{manifest.Acquisition.TriggerSource}`");
        builder.AppendLine($"- trigger delay/length/level：`{manifest.Acquisition.TriggerDelay}` / `{manifest.Acquisition.TriggerLength}` / `{manifest.Acquisition.TriggerLevel}`");
        builder.AppendLine($"- read rows：`{manifest.Acquisition.ReadSampleRows}`，channels：`{manifest.Acquisition.ChannelCount}`");
        builder.AppendLine();
        builder.AppendLine("## 已绑定设备");
        builder.AppendLine();
        builder.AppendLine("| 标签 | USB2070 SDK 编号 | USB2070 DeviceId | DDS COM | DDS DeviceId | 绑定时间 |");
        builder.AppendLine("|---|---:|---|---|---|---|");
        foreach (var set in manifest.Sets)
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(set.Label)} | {set.Usb2070DeviceNumber} | `{EscapeMarkdown(set.Usb2070DeviceId)}` | `{EscapeMarkdown(set.DdsPortName)}` | `{EscapeMarkdown(set.DdsDeviceId)}` | `{set.CreatedAt:O}` |");
        }

        return builder.ToString();
    }

    internal static string CreateEvidenceIndexMarkdown(
        EvidenceIndex index,
        string jsonPath,
        string currentJsonPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# EIT 会话证据索引");
        builder.AppendLine();
        builder.AppendLine($"生成时间：`{index.GeneratedAt:O}`");
        builder.AppendLine($"会话：`{index.SessionName}`");
        builder.AppendLine($"SessionId：`{index.SessionId}`");
        builder.AppendLine($"绑定套数：`{index.BoundSetCount}`");
        builder.AppendLine($"最近数据记录：`{index.RecentRunCount}`");
        builder.AppendLine();
        builder.AppendLine("## 文件");
        builder.AppendLine();
        builder.AppendLine($"- 历史 Markdown：`{index.MarkdownPath}`");
        builder.AppendLine($"- 历史 JSON：`{jsonPath}`");
        builder.AppendLine($"- 最新 Markdown：`{index.CurrentMarkdownPath}`");
        builder.AppendLine($"- 最新 JSON：`{currentJsonPath}`");
        builder.AppendLine($"- DataRoot：`{index.DataRootPath}`");
        builder.AppendLine($"- Catalog：`{index.CatalogPath}`");
        builder.AppendLine($"- SessionDir：`{index.SessionDirectory}`");
        builder.AppendLine();
        builder.AppendLine("## 关键证据");
        builder.AppendLine();
        builder.AppendLine("| 类型 | 路径 | 存在 | 字节 | SHA256 |");
        builder.AppendLine("|---|---|---:|---:|---|");
        AppendEvidenceFileRow(builder, "硬件报告", index.Artifacts.HardwareSmokeReport);
        AppendEvidenceFileRow(builder, "T25 计划", index.Artifacts.T25SmokePlan);
        AppendEvidenceFileRow(builder, "配对清单", index.Artifacts.PairingManifest);
        builder.AppendLine();
        builder.AppendLine("## 已绑定设备");
        builder.AppendLine();
        if (index.BoundSets.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            builder.AppendLine("| 标签 | USB2070 SDK 编号 | USB2070 DeviceId | DDS COM | DDS DeviceId |");
            builder.AppendLine("|---|---:|---|---|---|");
            foreach (var set in index.BoundSets)
            {
                builder.AppendLine(
                    $"| {EscapeMarkdown(set.Label)} | {set.Usb2070DeviceNumber} | `{EscapeMarkdown(set.Usb2070DeviceId)}` | `{EscapeMarkdown(set.DdsPortName)}` | `{EscapeMarkdown(set.DdsDeviceId)}` |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 最近数据记录");
        builder.AppendLine();
        if (index.RecentRuns.Count == 0)
        {
            builder.AppendLine("- 无");
            return builder.ToString();
        }

        builder.AppendLine("| Set | CapturedAt | Shape | 类型 | 路径 | 存在 | 字节 | SHA256 |");
        builder.AppendLine("|---|---|---|---|---|---:|---:|---|");
        foreach (var run in index.RecentRuns)
        {
            AppendRecentRunFileRow(builder, run, "raw", run.RawHdf5);
            AppendRecentRunFileRow(builder, run, "demod", run.DemodHdf5);
            AppendRecentRunFileRow(builder, run, "csv", run.RawCsv);
        }

        return builder.ToString();
    }

    internal static EvidenceIndexFile CaptureFileEvidence(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new EvidenceIndexFile(string.Empty, false, 0, string.Empty);
        }

        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists)
            {
                return new EvidenceIndexFile(file.FullName, false, 0, string.Empty);
            }

            using var stream = file.OpenRead();
            var hash = SHA256.HashData(stream);
            return new EvidenceIndexFile(file.FullName, true, file.Length, Convert.ToHexString(hash));
        }
        catch
        {
            return new EvidenceIndexFile(path, false, 0, string.Empty);
        }
    }

    internal static string CreateT25SmokePlanMarkdown(T25SmokePlanModel plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# EIT T25 多套验收计划");
        builder.AppendLine();
        builder.AppendLine($"生成时间：`{plan.GeneratedAt:O}`");
        builder.AppendLine($"绑定套数：`{plan.Pairings.Count}/2`");
        builder.AppendLine($"T25 状态：`{(plan.Pairings.Count >= 2 ? "可执行多套 smoke" : "未满足，仍需第二套完整硬件")}`");
        builder.AppendLine();
        builder.AppendLine("## 文件");
        builder.AppendLine();
        builder.AppendLine($"- 历史 Markdown：`{plan.MarkdownPath}`");
        builder.AppendLine($"- 历史 PowerShell：`{plan.ScriptPath}`");
        builder.AppendLine($"- 最新 Markdown：`{plan.CurrentMarkdownPath}`");
        builder.AppendLine($"- 最新 PowerShell：`{plan.CurrentScriptPath}`");
        builder.AppendLine($"- 仓库根目录：`{plan.RepositoryRoot}`");
        builder.AppendLine($"- 输出目录：`{plan.OutputDirectory}`");
        builder.AppendLine();
        builder.AppendLine("## 当前采集与激励设置");
        builder.AppendLine();
        builder.AppendLine($"- rows：`{plan.RowCount}`");
        builder.AppendLine($"- sample-rate：`{plan.SampleRateHz}` Hz");
        builder.AppendLine($"- frequency：`{plan.FrequencyHz}` Hz");
        builder.AppendLine($"- DAC：channel `{plan.DacChannel}`，current `{FormatDdsCurrentLabel(plan.DacGain)}`，gain `{plan.DacGain}`，phase `{plan.DacPhaseDegrees}` deg");
        builder.AppendLine($"- PGA：`{plan.PgaGain}`");
        builder.AppendLine($"- excitation mode：`{plan.ExcitationMode}`");
        builder.AppendLine($"- acquisition range：`{plan.AcquisitionRange}`");
        builder.AppendLine();
        builder.AppendLine("## 已绑定 pair 参数");
        builder.AppendLine();
        builder.AppendLine("| 标签 | USB2070 SDK 编号 | DDS COM | USB2070 PnP 片段 | --pair |");
        builder.AppendLine("|---|---:|---|---|---|");
        foreach (var pairing in plan.Pairings)
        {
            var pair = CreateT25PairArgument(pairing);
            builder.AppendLine(
                $"| {EscapeMarkdown(pairing.Pairing.Label)} | {pairing.Pairing.Usb2070DeviceNumber} | {EscapeMarkdown(pairing.Pairing.DdsSerialCandidate.PortName ?? string.Empty)} | {EscapeMarkdown(CreateUsbPnpFragment(pairing.Pairing.Usb2070Candidate))} | `{EscapeMarkdown(pair)}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## 推荐命令");
        builder.AppendLine();
        builder.AppendLine("```powershell");
        builder.AppendLine(plan.Command);
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## 判定");
        builder.AppendLine();
        builder.AppendLine("- T25 只有在接入并绑定至少两套真实硬件后才可判定完成。");
        builder.AppendLine("- PowerShell 中每个 `--pair` 参数都已经整体加引号；USB PnP 片段里出现 `&` 时不要去掉引号。");
        builder.AppendLine("- 运行脚本后，以 `multi-set-smoke.md` 中 `结论：通过` 和每套独立 raw/demod/CSV 产物为准。");
        return builder.ToString();
    }

    internal static string CreateT25SmokeScript(string repositoryRoot, string command)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by EitHost WPF. Run after binding two real EIT sets.");
        builder.AppendLine("Set-StrictMode -Version Latest");
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine($"Set-Location -LiteralPath {QuotePowerShellArgument(repositoryRoot)}");
        builder.AppendLine(command);
        return builder.ToString();
    }

    internal static string CreateT25SmokeCommand(
        string outputDirectory,
        IReadOnlyList<string> pairs,
        int rowCount,
        int sampleRateHz,
        int frequencyHz)
    {
        var builder = new StringBuilder();
        builder.Append("dotnet run --project ");
        builder.Append(QuotePowerShellArgument(@".\src\EitHost.Tools\EitHost.Tools.csproj"));
        builder.Append(" -- multi-set-smoke --output-dir ");
        builder.Append(QuotePowerShellArgument(outputDirectory));
        foreach (var pair in pairs)
        {
            builder.Append(" --pair ");
            builder.Append(QuotePowerShellArgument(pair));
        }

        builder.Append(" --execute --rows ");
        builder.Append(rowCount);
        builder.Append(" --sample-rate ");
        builder.Append(sampleRateHz);
        builder.Append(" --frequency ");
        builder.Append(frequencyHz);
        return builder.ToString();
    }

    internal static string CreateT25PairArgument(PairingSummaryItem pairing)
    {
        return string.Join(
            ':',
            NormalizeT25PairField(pairing.Pairing.Label),
            pairing.Pairing.Usb2070DeviceNumber.ToString(),
            NormalizeT25PairField(pairing.Pairing.DdsSerialCandidate.PortName ?? string.Empty),
            CreateUsbPnpFragment(pairing.Pairing.Usb2070Candidate));
    }

    internal static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EitHost.slnx"))
                    && File.Exists(Path.Combine(directory.FullName, "src", "EitHost.Tools", "EitHost.Tools.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static void AppendEvidenceFileRow(StringBuilder builder, string kind, EvidenceIndexFile file)
    {
        builder.AppendLine(
            $"| {EscapeMarkdown(kind)} | `{EscapeMarkdown(DisplayPathOrNone(file.Path))}` | {(file.Exists ? "是" : "否")} | {file.LengthBytes} | `{file.Sha256}` |");
    }

    private static void AppendRecentRunFileRow(
        StringBuilder builder,
        EvidenceIndexRecentRun run,
        string kind,
        EvidenceIndexFile file)
    {
        builder.AppendLine(
            $"| {EscapeMarkdown(run.SetLabel)} | `{run.CapturedAt:O}` | {run.SampleRows}x{run.ChannelCount} | {kind} | `{EscapeMarkdown(DisplayPathOrNone(file.Path))}` | {(file.Exists ? "是" : "否")} | {file.LengthBytes} | `{file.Sha256}` |");
    }

    private static string CreateUsbPnpFragment(PnpDeviceCandidate candidate)
    {
        var deviceIdFragment = candidate.DeviceId
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (!string.IsNullOrWhiteSpace(deviceIdFragment))
        {
            return NormalizeT25PairField(deviceIdFragment);
        }

        var locationFragment = candidate.LocationPath
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        return NormalizeT25PairField(locationFragment ?? $"{candidate.Vid}_{candidate.Pid}");
    }

    private static string NormalizeT25PairField(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
        return text.Replace(':', '_');
    }

    private static string QuotePowerShellArgument(string value)
    {
        return $"\"{value.Replace("`", "``").Replace("$", "`$").Replace("\"", "`\"")}\"";
    }

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|");

    private static string DisplayPathOrNone(string path) => string.IsNullOrWhiteSpace(path) ? "无" : path;

    private static string FormatDdsCurrentLabel(double gain) =>
        FormattableString.Invariant($"{gain * 100.0:0} uA");
}

internal sealed record T25SmokePlanModel(
    DateTimeOffset GeneratedAt,
    string MarkdownPath,
    string ScriptPath,
    string CurrentMarkdownPath,
    string CurrentScriptPath,
    string RepositoryRoot,
    string OutputDirectory,
    string Command,
    int RowCount,
    int SampleRateHz,
    int FrequencyHz,
    int DacChannel,
    double DacGain,
    int DacPhaseDegrees,
    int PgaGain,
    string ExcitationMode,
    string AcquisitionRange,
    IReadOnlyList<PairingSummaryItem> Pairings);

internal sealed record PairingManifest(
    Guid SessionId,
    DateTimeOffset SessionStartedAt,
    string SessionName,
    DateTimeOffset GeneratedAt,
    string MarkdownPath,
    string CurrentMarkdownPath,
    int SetCount,
    PairingManifestExcitation Excitation,
    PairingManifestAcquisition Acquisition,
    IReadOnlyList<PairingManifestSet> Sets);

internal sealed record PairingManifestExcitation(
    int DacChannel,
    int FrequencyHz,
    double DacGain,
    int DacPhaseDegrees,
    int PgaGain,
    string Mode,
    double ChannelCycles,
    int ScanTimes,
    int OverheadUs);

internal sealed record PairingManifestAcquisition(
    int SampleRateHz,
    string Range,
    string TriggerMode,
    string TriggerSource,
    int TriggerDelay,
    int TriggerLength,
    int TriggerLevel,
    int ReadSampleRows,
    int ChannelCount);

internal sealed record PairingManifestSet(
    string Label,
    DateTimeOffset CreatedAt,
    int Usb2070DeviceNumber,
    string Usb2070DeviceId,
    string Usb2070DisplayName,
    string Usb2070Vid,
    string Usb2070Pid,
    string Usb2070LocationPath,
    string DdsPortName,
    string DdsDeviceId,
    string DdsDisplayName,
    string DdsVid,
    string DdsPid,
    string DdsLocationPath);

internal sealed record EvidenceIndex(
    Guid SessionId,
    DateTimeOffset SessionStartedAt,
    string SessionName,
    DateTimeOffset GeneratedAt,
    string DataRootPath,
    string CatalogPath,
    string SessionDirectory,
    string MarkdownPath,
    string CurrentMarkdownPath,
    EvidenceIndexArtifacts Artifacts,
    int BoundSetCount,
    int RecentRunCount,
    IReadOnlyList<EvidenceIndexBoundSet> BoundSets,
    IReadOnlyList<EvidenceIndexRecentRun> RecentRuns);

internal sealed record EvidenceIndexArtifacts(
    EvidenceIndexFile HardwareSmokeReport,
    EvidenceIndexFile T25SmokePlan,
    EvidenceIndexFile PairingManifest);

internal sealed record EvidenceIndexFile(string Path, bool Exists, long LengthBytes, string Sha256);

internal sealed record EvidenceIndexBoundSet(
    string Label,
    int Usb2070DeviceNumber,
    string Usb2070DeviceId,
    string DdsPortName,
    string DdsDeviceId);

internal sealed record EvidenceIndexRecentRun(
    Guid RunId,
    string SetLabel,
    DateTimeOffset CapturedAt,
    string Hdf5Path,
    int SampleRows,
    int ChannelCount,
    int FileCount,
    int ExportCount,
    EvidenceIndexFile RawHdf5,
    EvidenceIndexFile DemodHdf5,
    EvidenceIndexFile RawCsv);
