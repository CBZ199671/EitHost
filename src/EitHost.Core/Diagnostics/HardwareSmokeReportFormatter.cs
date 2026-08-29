using System.Text;

namespace EitHost.Core.Diagnostics;

public static class HardwareSmokeReportFormatter
{
    public static string ToMarkdown(HardwareSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# EIT 硬件冒烟报告");
        builder.AppendLine();
        builder.AppendLine($"生成时间：`{report.GeneratedAt:O}`");
        builder.AppendLine();
        AppendSummary(builder, report);
        AppendDevices(builder, "PnP USB2070", report.PnpUsb2070Devices);
        AppendDevices(builder, "PnP DDS 串口", report.PnpDdsSerialDevices);
        AppendSerialPorts(builder, report.OsSerialPorts);
        AppendUsb2070SdkDevices(builder, report.Usb2070SdkDevices);
        AppendDriverPreflight(builder, report.DriverPreflight);
        AppendReadiness(builder, report.Readiness);
        AppendMultiSetReadiness(builder, report.MultiSetReadiness);
        AppendWarnings(builder, report.Warnings);
        return builder.ToString();
    }

    private static void AppendSummary(StringBuilder builder, HardwareSmokeReport report)
    {
        builder.AppendLine("## 汇总");
        builder.AppendLine();
        builder.AppendLine($"- PnP USB2070：{report.PnpUsb2070Devices.Count}");
        builder.AppendLine($"- PnP DDS 串口：{report.PnpDdsSerialDevices.Count}");
        builder.AppendLine($"- OS 串口：{report.OsSerialPorts.Count}");
        builder.AppendLine($"- USB2070 SDK 可打开设备：{report.Usb2070SdkDevices.Count}");
        builder.AppendLine($"- 估算完整 EIT 套数：{report.EstimatedCompleteSetCount}");
        builder.AppendLine($"- 当前进程管理员：{(report.DriverPreflight.IsAdministrator ? "是" : "否")}");
        builder.AppendLine($"- Driver Store USB2070 匹配：{report.DriverPreflight.DriverStoreMatches.Count}");
        builder.AppendLine($"- T24 单套冒烟就绪：{(report.Readiness.ReadyForSingleSetSmoke ? "是" : "否")}");
        builder.AppendLine($"- T25 多套冒烟就绪：{(report.MultiSetReadiness.ReadyForMultiSetSmoke ? "是" : "否")}");
        builder.AppendLine($"- 警告：{report.Warnings.Count}");
        builder.AppendLine();
    }

    private static void AppendDevices(
        StringBuilder builder,
        string title,
        IReadOnlyList<HardwareSmokeDeviceCandidate> devices)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        if (devices.Count == 0)
        {
            builder.AppendLine("- 未发现");
            builder.AppendLine();
            return;
        }

        foreach (var device in devices)
        {
            builder.AppendLine($"- `{device.DisplayName}`");
            builder.AppendLine($"  - DeviceId: `{device.DeviceId}`");
            builder.AppendLine($"  - VID/PID: `{device.Vid}` / `{device.Pid}`");
            builder.AppendLine($"  - Location: `{device.LocationPath}`");
            if (!string.IsNullOrWhiteSpace(device.PortName))
            {
                builder.AppendLine($"  - Port: `{device.PortName}`");
            }

            builder.AppendLine($"  - Status: `{device.Status ?? "unknown"}`");
            if (device.ProblemCode is not null)
            {
                builder.AppendLine($"  - ProblemCode: `{device.ProblemCode}`");
            }

            if (!string.IsNullOrWhiteSpace(device.ProblemDescription))
            {
                builder.AppendLine($"  - ProblemDescription: `{device.ProblemDescription}`");
            }
        }

        builder.AppendLine();
    }

    private static void AppendSerialPorts(StringBuilder builder, IReadOnlyList<string> serialPorts)
    {
        builder.AppendLine("## OS 串口");
        builder.AppendLine();
        if (serialPorts.Count == 0)
        {
            builder.AppendLine("- 未发现");
            builder.AppendLine();
            return;
        }

        foreach (var port in serialPorts)
        {
            builder.AppendLine($"- `{port}`");
        }

        builder.AppendLine();
    }

    private static void AppendUsb2070SdkDevices(
        StringBuilder builder,
        IReadOnlyList<HardwareSmokeUsb2070Device> devices)
    {
        builder.AppendLine("## USB2070 SDK 扫描");
        builder.AppendLine();
        if (devices.Count == 0)
        {
            builder.AppendLine("- 未发现可打开设备");
            builder.AppendLine();
            return;
        }

        foreach (var device in devices)
        {
            builder.AppendLine(
                $"- SDK #{device.DeviceNumber}: {device.AvailableChannelCount} ch, {device.AdBit} bit, max {device.MaxSampleRateHz} Hz");
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

    private static void AppendReadiness(StringBuilder builder, HardwareSmokeReadiness readiness)
    {
        builder.AppendLine("## T24 单套冒烟就绪");
        builder.AppendLine();
        builder.AppendLine($"- 就绪：{(readiness.ReadyForSingleSetSmoke ? "是" : "否")}");
        if (readiness.Blockers.Count == 0)
        {
            builder.AppendLine("- 阻断项：无");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("- 阻断项：");
        foreach (var blocker in readiness.Blockers)
        {
            builder.AppendLine($"  - {blocker}");
        }

        builder.AppendLine();
    }

    private static void AppendMultiSetReadiness(StringBuilder builder, HardwareSmokeMultiSetReadiness readiness)
    {
        builder.AppendLine("## T25 多套冒烟就绪");
        builder.AppendLine();
        builder.AppendLine($"- 需要套数：{readiness.RequiredSetCount}");
        builder.AppendLine($"- 就绪：{(readiness.ReadyForMultiSetSmoke ? "是" : "否")}");
        if (readiness.Blockers.Count == 0)
        {
            builder.AppendLine("- 阻断项：无");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("- 阻断项：");
        foreach (var blocker in readiness.Blockers)
        {
            builder.AppendLine($"  - {blocker}");
        }

        builder.AppendLine();
    }

    private static void AppendDriverPreflight(StringBuilder builder, Usb2070DriverPreflight preflight)
    {
        builder.AppendLine("## USB2070 驱动预检");
        builder.AppendLine();
        builder.AppendLine($"- 当前进程管理员：{(preflight.IsAdministrator ? "是" : "否")}");
        builder.AppendLine($"- INF 路径：`{(string.IsNullOrWhiteSpace(preflight.InfPath) ? "unknown" : preflight.InfPath)}`");
        builder.AppendLine($"- INF 存在：{(preflight.InfExists ? "是" : "否")}");
        if (preflight.DriverStoreMatches.Count == 0)
        {
            builder.AppendLine("- Driver Store 匹配：未发现");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("- Driver Store 匹配：");
        foreach (var match in preflight.DriverStoreMatches)
        {
            builder.AppendLine($"  - `{match}`");
        }

        builder.AppendLine();
    }
}
