using System.Text;

namespace EitHost.Core.Diagnostics;

public static class DdsTimingSmokeReportFormatter
{
    public static string ToMarkdown(DdsTimingSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# DDS/扫描时序硬件验证报告");
        builder.AppendLine();
        builder.AppendLine($"- 开始：`{report.StartedAt:O}`");
        builder.AppendLine($"- 结束：`{report.FinishedAt:O}`");
        builder.AppendLine($"- Execute：`{report.ExecuteRequested}`");
        builder.AppendLine($"- Ready：`{report.Ready}`");
        builder.AppendLine($"- Passed：`{report.Passed}`");
        builder.AppendLine($"- 状态：{report.Status}");
        builder.AppendLine();
        AppendOptions(builder, report.Options);
        AppendCases(builder, report.Cases);
        AppendItems(builder, "阻断项", report.Blockers);
        AppendItems(builder, "警告", report.Warnings);
        return builder.ToString();
    }

    private static void AppendOptions(StringBuilder builder, DdsTimingSmokeOptions options)
    {
        builder.AppendLine("## 验证矩阵");
        builder.AppendLine();
        builder.AppendLine($"- DDS：`{options.DdsPortName}`");
        builder.AppendLine($"- USB2070：`#{options.Usb2070DeviceNumber}`");
        builder.AppendLine($"- 频率：{options.FrequencyHz} Hz");
        builder.AppendLine($"- 周期：{string.Join(", ", options.ChannelCycles.Select(value => value.ToString("0.###")))}");
        builder.AppendLine($"- 采样率：{options.SampleRateHz} Hz");
        builder.AppendLine($"- 电流：{options.CurrentUa:0.###} uA");
        builder.AppendLine($"- PGA：{options.PgaGain}");
        builder.AppendLine($"- 前/后丢弃：{options.DiscardLeadingCycles:0.###} / {options.DiscardTrailingCycles:0.###} 周期");
        builder.AppendLine($"- 严格目标：{options.TargetBlocks} blocks × {options.FramesPerBlock} frames");
        builder.AppendLine();
    }

    private static void AppendCases(StringBuilder builder, IReadOnlyList<DdsTimingSmokeCaseReport> cases)
    {
        builder.AppendLine("## 结果");
        builder.AppendLine();
        if (cases.Count == 0)
        {
            builder.AppendLine("- 未执行硬件矩阵。");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| 周期 | 请求/实际 us | Timer tick | 实测窗口/期望/容差 | 载波 Hz | 16步 | strict | 结论 |");
        builder.AppendLine("|---:|---:|---:|---:|---:|:---:|---:|:---:|");
        foreach (var item in cases)
        {
            builder.AppendLine(
                $"| {item.RequestedChannelCycles:0.###} | {item.RequestedTimeUs}/{item.EffectiveTimeUs:0.###} | {item.TimerTicks} | " +
                $"{item.ObservedWindowSamples:0.###}/{item.ExpectedWindowSamples:0.###}/{item.TimingToleranceSamples:0.###} | " +
                $"{item.MeasuredCarrierHz:0.###} | {(item.StepOrderMatched ? "通过" : "失败")} | " +
                $"{item.StrictAcceptedFrames}/{item.RequiredStrictFrames} | {(item.Passed ? "通过" : "失败")} |");
        }

        builder.AppendLine();
        foreach (var item in cases)
        {
            builder.AppendLine($"### {item.RequestedChannelCycles:0.###} 周期");
            builder.AppendLine();
            builder.AppendLine($"- 固件：protocol v{item.FirmwareProtocolVersion} / `{item.FirmwareVersion}`");
            builder.AppendLine($"- ACK：`{item.AcknowledgementHex}`");
            builder.AppendLine($"- 请求：`{item.RequestHex}`");
            builder.AppendLine(
                $"- 12T Timer：{item.TimerClockHz} Hz / {item.TimerTicks} tick / " +
                $"minimum guard {item.SwitchGuardMinimumUs} us（总窗口起始死区尚待示波器实测）");
            builder.AppendLine($"- 有效周期：{item.EffectiveChannelCycles:0.######}");
            builder.AppendLine($"- 时序一致性：{(item.TimingMatched ? "通过" : "ExcitationTimingMismatch")}");
            builder.AppendLine($"- 载波误差：{item.CarrierErrorPercent:0.###}%");
            builder.AppendLine($"- 观测顺序：`{string.Join(",", item.ObservedStepOrder)}`");
            builder.AppendLine($"- Top3：valid {item.ValidTop3Windows}/{item.TotalWindows}，rejected frames {item.RejectedFrames}");
            builder.AppendLine($"- Raw HDF5：`{item.RawHdf5Path}` / SHA256 `{item.RawHdf5Sha256}`");
            builder.AppendLine($"- Demod HDF5：`{item.DemodHdf5Path}` / SHA256 `{item.DemodHdf5Sha256}`");
            builder.AppendLine($"- ACK JSON：`{item.AckJsonPath}` / SHA256 `{item.AckJsonSha256}`");
            if (!string.IsNullOrWhiteSpace(item.Failure))
            {
                builder.AppendLine($"- Failure：{item.Failure}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendItems(StringBuilder builder, string title, IReadOnlyList<string> items)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (items.Count == 0)
        {
            builder.AppendLine("- 无");
        }
        else
        {
            foreach (var item in items)
            {
                builder.AppendLine($"- {item}");
            }
        }

        builder.AppendLine();
    }
}
