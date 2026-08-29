using System.Text;
using EitHost.Core.Storage.Catalog;

namespace EitHost.Core.Diagnostics;

public static class MultiSetSmokeReportFormatter
{
    public static string ToMarkdown(MultiSetSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# EIT T25 多套真机冒烟报告");
        builder.AppendLine();
        builder.AppendLine($"开始时间：`{report.StartedAt:O}`");
        builder.AppendLine($"结束时间：`{report.FinishedAt:O}`");
        builder.AppendLine($"执行请求：{(report.ExecuteRequested ? "是" : "否")}");
        builder.AppendLine($"结论：{(report.Passed ? "通过" : "未通过")}");
        builder.AppendLine($"状态：{report.Status}");
        builder.AppendLine();
        AppendHardware(builder, report);
        AppendSets(builder, report);
        AppendSync(builder, report);
        AppendWarnings(builder, report.Warnings);
        return builder.ToString();
    }

    private static void AppendHardware(StringBuilder builder, MultiSetSmokeReport report)
    {
        builder.AppendLine("## 硬件就绪");
        builder.AppendLine();
        builder.AppendLine($"- PnP USB2070：{report.Hardware.PnpUsb2070Count}");
        builder.AppendLine($"- PnP DDS 串口：{report.Hardware.PnpDdsSerialCount}");
        builder.AppendLine($"- OS 串口：{report.Hardware.OsSerialPortCount}");
        builder.AppendLine($"- USB2070 SDK 可打开设备：{report.Hardware.Usb2070SdkDeviceCount}");
        builder.AppendLine($"- 多套就绪：{(report.Ready ? "是" : "否")}");
        if (report.Blockers.Count == 0)
        {
            builder.AppendLine("- 阻断项：无");
        }
        else
        {
            builder.AppendLine("- 阻断项：");
            foreach (var blocker in report.Blockers)
            {
                builder.AppendLine($"  - {blocker}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendSets(StringBuilder builder, MultiSetSmokeReport report)
    {
        builder.AppendLine("## 套件");
        builder.AppendLine();
        if (report.Sets.Count == 0)
        {
            builder.AppendLine("- 未生成配对");
            builder.AppendLine();
            return;
        }

        foreach (var set in report.Sets)
        {
            builder.AppendLine($"### {set.Pairing.SetLabel}");
            builder.AppendLine();
            builder.AppendLine($"- USB2070 SDK 编号：`{set.Pairing.Usb2070DeviceNumber}`");
            builder.AppendLine($"- USB2070：`{set.Pairing.Usb2070DisplayName}` / `{set.Pairing.Usb2070DeviceId}`");
            builder.AppendLine($"- USB2070 标识：`{set.Pairing.Usb2070Vid}` / `{set.Pairing.Usb2070Pid}` / `{set.Pairing.Usb2070LocationPath}`");
            builder.AppendLine($"- DDS：`{set.Pairing.DdsPortName}` / `{set.Pairing.DdsDisplayName}` / `{set.Pairing.DdsDeviceId}`");
            builder.AppendLine($"- DDS 标识：`{set.Pairing.DdsVid}` / `{set.Pairing.DdsPid}` / `{set.Pairing.DdsLocationPath}`");
            AppendCommand(builder, "启动激励", set.StartExcitationCommand);
            AppendCommand(builder, "停止激励", set.StopExcitationCommand);
            if (set.Acquisition is { } acquisition)
            {
                builder.AppendLine($"- AD：{acquisition.SampleRows} x {acquisition.ChannelCount}, {acquisition.RawValueCount} values, {acquisition.SampleRateHz} Hz, `{acquisition.Range}`, {acquisition.AdBit} bit");
            }
            else
            {
                builder.AppendLine("- AD：未执行");
            }

            if (set.Artifacts is { } artifacts)
            {
                builder.AppendLine($"- Raw HDF5：`{artifacts.RawHdf5Path}`");
                builder.AppendLine($"- Demod HDF5：`{artifacts.DemodHdf5Path}`");
                builder.AppendLine($"- Raw CSV：`{artifacts.RawCsvPath}`");
                builder.AppendLine($"- SQLite catalog：`{artifacts.CatalogPath}`");
                builder.AppendLine($"- 解调：frames={artifacts.DemodFrameCount}, peaks={artifacts.DemodPeakCount}");
                builder.AppendLine($"- CSV 尺寸：{artifacts.CsvRowCount} x {artifacts.CsvColumnCount}");
                AppendCatalogSummary(builder, artifacts.CatalogSummary);
            }
            else
            {
                builder.AppendLine("- 文件产物：未生成");
            }

            builder.AppendLine();
        }
    }

    private static void AppendSync(StringBuilder builder, MultiSetSmokeReport report)
    {
        builder.AppendLine("## 同步记录");
        builder.AppendLine();
        if (report.SyncRecords.Count == 0)
        {
            builder.AppendLine("- 未执行");
            builder.AppendLine();
            return;
        }

        foreach (var record in report.SyncRecords)
        {
            builder.AppendLine($"- `{record.Label}` AD=`{record.AcquisitionStartRequestedAt:O}` DDS=`{record.ExcitationStartRequestedAt:O}`");
        }

        builder.AppendLine();
    }

    private static void AppendWarnings(StringBuilder builder, IReadOnlyList<string> warnings)
    {
        builder.AppendLine("## 警告");
        builder.AppendLine();
        if (warnings.Count == 0)
        {
            builder.AppendLine("- 无");
            builder.AppendLine();
            return;
        }

        foreach (var warning in warnings)
        {
            builder.AppendLine($"- {warning}");
        }

        builder.AppendLine();
    }

    private static void AppendCatalogSummary(StringBuilder builder, EitCatalogSummary? summary)
    {
        if (summary is null)
        {
            return;
        }

        builder.AppendLine(
            $"- SQLite catalog 行数：sessions={summary.SessionCount}, devices={summary.DeviceCount}, pairings={summary.PairingCount}, runs={summary.RunCount}, files={summary.FileCount}, exports={summary.ExportCount}");
    }

    private static void AppendCommand(
        StringBuilder builder,
        string label,
        SingleSetSmokeDdsCommand? command)
    {
        if (command is null)
        {
            builder.AppendLine($"- {label}：未执行");
            return;
        }

        builder.AppendLine($"- {label}：`{command.Command}` / `{command.PacketHex}` / `{command.SentAt:O}`");
    }
}
