using System.Text;
using EitHost.Core.Storage.Catalog;

namespace EitHost.Core.Diagnostics;

public static class SingleSetSmokeReportFormatter
{
    public static string ToMarkdown(SingleSetSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# EIT T24 单套真机冒烟报告");
        builder.AppendLine();
        builder.AppendLine($"开始时间：`{report.StartedAt:O}`");
        builder.AppendLine($"结束时间：`{report.FinishedAt:O}`");
        builder.AppendLine($"结论：{(report.Passed ? "通过" : "未通过")}");
        builder.AppendLine($"状态：{report.Status}");
        builder.AppendLine();
        AppendHardware(builder, report.Hardware);
        AppendPairing(builder, report.Pairing);
        AppendDds(
            builder,
            report.SetDacCommand,
            report.SetPgaCommand,
            report.StartExcitationCommand,
            report.StopExcitationCommand);
        AppendAcquisition(builder, report.Acquisition);
        AppendArtifacts(builder, report.Artifacts);
        AppendWarnings(builder, report.Warnings);
        return builder.ToString();
    }

    private static void AppendHardware(StringBuilder builder, SingleSetSmokeHardwareSummary hardware)
    {
        builder.AppendLine("## 硬件就绪");
        builder.AppendLine();
        builder.AppendLine($"- PnP USB2070：{hardware.PnpUsb2070Count}");
        builder.AppendLine($"- PnP DDS 串口：{hardware.PnpDdsSerialCount}");
        builder.AppendLine($"- OS 串口：{hardware.OsSerialPortCount}");
        builder.AppendLine($"- USB2070 SDK 可打开设备：{hardware.Usb2070SdkDeviceCount}");
        builder.AppendLine($"- T24 就绪：{(hardware.ReadyForSingleSetSmoke ? "是" : "否")}");
        if (hardware.Blockers.Count == 0)
        {
            builder.AppendLine("- 阻断项：无");
        }
        else
        {
            builder.AppendLine("- 阻断项：");
            foreach (var blocker in hardware.Blockers)
            {
                builder.AppendLine($"  - {blocker}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendPairing(StringBuilder builder, SingleSetSmokePairing? pairing)
    {
        builder.AppendLine("## 配对");
        builder.AppendLine();
        if (pairing is null)
        {
            builder.AppendLine("- 未执行");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- 标签：`{pairing.SetLabel}`");
        builder.AppendLine($"- USB2070 SDK 编号：`{pairing.Usb2070DeviceNumber}`");
        builder.AppendLine($"- USB2070：`{pairing.Usb2070DisplayName}` / `{pairing.Usb2070DeviceId}`");
        builder.AppendLine($"- USB2070 标识：`{pairing.Usb2070Vid}` / `{pairing.Usb2070Pid}` / `{pairing.Usb2070LocationPath}`");
        builder.AppendLine($"- DDS：`{pairing.DdsPortName}` / `{pairing.DdsDisplayName}` / `{pairing.DdsDeviceId}`");
        builder.AppendLine($"- DDS 标识：`{pairing.DdsVid}` / `{pairing.DdsPid}` / `{pairing.DdsLocationPath}`");
        builder.AppendLine();
    }

    private static void AppendDds(
        StringBuilder builder,
        SingleSetSmokeDdsCommand? setDacCommand,
        SingleSetSmokeDdsCommand? setPgaCommand,
        SingleSetSmokeDdsCommand? startCommand,
        SingleSetSmokeDdsCommand? stopCommand)
    {
        builder.AppendLine("## DDS 命令");
        builder.AppendLine();
        if (setDacCommand is null && setPgaCommand is null && startCommand is null && stopCommand is null)
        {
            builder.AppendLine("- 未执行");
            builder.AppendLine();
            return;
        }

        AppendDdsCommand(builder, "设置 DAC", setDacCommand);
        AppendDdsCommand(builder, "设置 PGA", setPgaCommand);
        AppendDdsCommand(builder, "启动激励", startCommand);
        AppendDdsCommand(builder, "停止激励", stopCommand);
        builder.AppendLine();
    }

    private static void AppendDdsCommand(
        StringBuilder builder,
        string title,
        SingleSetSmokeDdsCommand? command)
    {
        if (command is null)
        {
            builder.AppendLine($"- {title}：未执行");
            return;
        }

        builder.AppendLine($"- {title}");
        builder.AppendLine($"- 命令：`{command.Command}`");
        builder.AppendLine($"- Packet：`{command.PacketHex}`");
        builder.AppendLine($"- 发送时间：`{command.SentAt:O}`");
    }

    private static void AppendAcquisition(StringBuilder builder, SingleSetSmokeAcquisition? acquisition)
    {
        builder.AppendLine("## AD 采集");
        builder.AppendLine();
        if (acquisition is null)
        {
            builder.AppendLine("- 未执行");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- 样本行：{acquisition.SampleRows}");
        builder.AppendLine($"- 通道数：{acquisition.ChannelCount}");
        builder.AppendLine($"- 原始值数量：{acquisition.RawValueCount}");
        builder.AppendLine($"- 采样率：{acquisition.SampleRateHz} Hz");
        builder.AppendLine($"- 量程：`{acquisition.Range}`");
        builder.AppendLine($"- AD 位数：{acquisition.AdBit}");
        builder.AppendLine();
    }

    private static void AppendArtifacts(StringBuilder builder, SingleSetSmokeArtifacts? artifacts)
    {
        builder.AppendLine("## 文件产物");
        builder.AppendLine();
        if (artifacts is null)
        {
            builder.AppendLine("- 未生成");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- Raw HDF5：`{artifacts.RawHdf5Path}`");
        builder.AppendLine($"- Demod HDF5：`{artifacts.DemodHdf5Path}`");
        builder.AppendLine($"- Raw CSV：`{artifacts.RawCsvPath}`");
        builder.AppendLine($"- SQLite catalog：`{artifacts.CatalogPath}`");
        builder.AppendLine($"- 解调帧数：{artifacts.DemodFrameCount}");
        builder.AppendLine($"- 解调峰值数：{artifacts.DemodPeakCount}");
        builder.AppendLine($"- CSV 尺寸：{artifacts.CsvRowCount} x {artifacts.CsvColumnCount}");
        AppendCatalogSummary(builder, artifacts.CatalogSummary);
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
}
