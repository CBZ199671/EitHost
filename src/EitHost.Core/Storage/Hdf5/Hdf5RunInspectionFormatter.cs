using System.Text;

namespace EitHost.Core.Storage.Hdf5;

public static class Hdf5RunInspectionFormatter
{
    public static string ToMarkdown(Hdf5RunInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        var builder = new StringBuilder();
        builder.AppendLine("# EIT HDF5 数据检查报告");
        builder.AppendLine();
        builder.AppendLine($"文件：`{inspection.FilePath}`");
        builder.AppendLine($"结论：{(inspection.Passed ? "通过" : "未通过")}");
        builder.AppendLine();
        AppendRaw(builder, inspection);
        AppendRun(builder, inspection);
        AppendDevice(builder, inspection.Device);
        AppendExcitation(builder, inspection.Excitation);
        AppendAcquisition(builder, inspection.Acquisition);
        AppendIssues(builder, inspection.Issues);
        return builder.ToString();
    }

    private static void AppendRaw(StringBuilder builder, Hdf5RunInspection inspection)
    {
        builder.AppendLine("## Raw Dataset");
        builder.AppendLine();
        builder.AppendLine($"- Dataset：`{inspection.RawDatasetPath}`");
        builder.AppendLine($"- 维度：{string.Join(" x ", inspection.RawDimensions)}");
        builder.AppendLine($"- 样本行：{inspection.RawSampleRows}");
        builder.AppendLine($"- 通道数：{inspection.RawChannelCount}");
        builder.AppendLine();
    }

    private static void AppendRun(StringBuilder builder, Hdf5RunInspection inspection)
    {
        builder.AppendLine("## Run 标记");
        builder.AppendLine();
        builder.AppendLine($"- SessionId：`{inspection.SessionId}`");
        builder.AppendLine($"- RunId：`{inspection.RunId}`");
        builder.AppendLine($"- CapturedAtUtc：`{inspection.CapturedAtUtc}`");
        builder.AppendLine($"- Metadata 样本行：{inspection.MetadataSampleRows}");
        builder.AppendLine($"- Metadata 通道数：{inspection.MetadataChannelCount}");
        builder.AppendLine();
    }

    private static void AppendDevice(StringBuilder builder, Hdf5RunInspectionDevice device)
    {
        builder.AppendLine("## 设备标记");
        builder.AppendLine();
        builder.AppendLine($"- SetLabel：`{device.SetLabel}`");
        builder.AppendLine($"- MeasurementChannelCount：{device.MeasurementChannelCount}");
        builder.AppendLine($"- USB2070 SDK 编号：{device.Usb2070DeviceNumber}");
        builder.AppendLine($"- USB2070：`{device.Usb2070DisplayName}` / `{device.Usb2070Vid}` / `{device.Usb2070Pid}`");
        builder.AppendLine($"- USB2070 DeviceId：`{device.Usb2070DeviceId}`");
        builder.AppendLine($"- USB2070 Location：`{device.Usb2070LocationPath}`");
        builder.AppendLine($"- DDS：`{device.DdsPortName}` / `{device.DdsDisplayName}` / `{device.DdsVid}` / `{device.DdsPid}`");
        builder.AppendLine($"- DDS DeviceId：`{device.DdsDeviceId}`");
        builder.AppendLine($"- DDS Location：`{device.DdsLocationPath}`");
        builder.AppendLine();
    }

    private static void AppendExcitation(StringBuilder builder, Hdf5RunInspectionExcitation excitation)
    {
        builder.AppendLine("## 激励参数");
        builder.AppendLine();
        builder.AppendLine($"- Mode：`{excitation.Mode}` ({excitation.ModeCode})");
        builder.AppendLine($"- FrequencyHz：{excitation.FrequencyHz}");
        if (excitation.RequestedFrequencyHz is { } requestedFrequencyHz)
        {
            builder.AppendLine($"- RequestedFrequencyHz：{requestedFrequencyHz}");
            builder.AppendLine($"- FrequencyTuningWord：{excitation.FrequencyTuningWord}");
            builder.AppendLine($"- ActualFrequencyHz：{excitation.ActualFrequencyHz:G17}");
            builder.AppendLine($"- FrequencyErrorHz：{excitation.FrequencyErrorHz:G17}");
        }

        builder.AppendLine($"- ChannelCycles：{excitation.ChannelCycles}");
        builder.AppendLine($"- ScanTimes：{excitation.ScanTimes}");
        builder.AppendLine($"- DacChannel：{excitation.DacChannel}");
        builder.AppendLine($"- DacGain：{excitation.DacGain}");
        builder.AppendLine($"- DacPhaseDegrees：{excitation.DacPhaseDegrees}");
        builder.AppendLine($"- PgaGain：{excitation.PgaGain}");
        builder.AppendLine($"- OverheadUs：{excitation.OverheadUs}");
        builder.AppendLine($"- TimeUs：{excitation.TimeUs}");
        if (excitation.FirmwareProtocolVersion is not null)
        {
            builder.AppendLine($"- FirmwareProtocolVersion：{excitation.FirmwareProtocolVersion}");
            builder.AppendLine($"- FirmwareVersion：`{excitation.FirmwareVersion}`");
            if (excitation.FirmwareFeatureFlags is { } featureFlags)
            {
                builder.AppendLine($"- FirmwareFeatureFlags：`0x{featureFlags:X4}`");
            }

            builder.AppendLine($"- RequestedTimeUs：{excitation.RequestedTimeUs}");
            builder.AppendLine($"- TimerMode：`{excitation.TimerMode}`");
            builder.AppendLine($"- TimerClockHz：{excitation.TimerClockHz}");
            builder.AppendLine($"- TimerTicks：{excitation.TimerTicks}");
            builder.AppendLine($"- EffectiveTimeUs：{excitation.EffectiveTimeUs:0.###}");
            builder.AppendLine($"- EffectiveChannelCycles：{excitation.EffectiveChannelCycles:0.######}");
            builder.AppendLine($"- SwitchGuardMinimumUs：{excitation.SwitchGuardMinimumUs}");
            if (excitation.SwitchGuardSemantics is not null)
            {
                builder.AppendLine($"- SwitchGuardSemantics：`{excitation.SwitchGuardSemantics}`");
            }
        }

        if (excitation.ScanStatusState is not null)
        {
            builder.AppendLine(
                $"- ScanStatus：`{excitation.ScanStatusState}`，{excitation.ScanStatusCompletedCycles}/{excitation.ScanStatusTargetCycles} 圈，step {excitation.ScanStatusCurrentStep}");
        }

        builder.AppendLine();
    }

    private static void AppendAcquisition(StringBuilder builder, Hdf5RunInspectionAcquisition acquisition)
    {
        builder.AppendLine("## 采集参数");
        builder.AppendLine();
        builder.AppendLine($"- SampleRateHz：{acquisition.SampleRateHz}");
        builder.AppendLine($"- AdRange：`{acquisition.AdRange}` ({acquisition.AdRangeCode})");
        if (acquisition.AdFullSpanVolts is { } adFullSpanVolts)
        {
            builder.AppendLine($"- AdFullSpanVolts：{adFullSpanVolts:G17}");
            builder.AppendLine($"- AdLsbVolts：{acquisition.AdLsbVolts:G17}");
        }

        builder.AppendLine($"- AdBit：{acquisition.AdBit}");
        builder.AppendLine($"- EnabledChannels：{string.Join(", ", acquisition.EnabledChannels)}");
        builder.AppendLine($"- TriggerMode：`{acquisition.TriggerMode}` ({acquisition.TriggerModeCode})");
        builder.AppendLine($"- TriggerSource：`{acquisition.TriggerSource}` ({acquisition.TriggerSourceCode})");
        builder.AppendLine();
    }

    private static void AppendIssues(StringBuilder builder, IReadOnlyList<string> issues)
    {
        builder.AppendLine("## 问题");
        builder.AppendLine();
        if (issues.Count == 0)
        {
            builder.AppendLine("- 无");
            builder.AppendLine();
            return;
        }

        foreach (var issue in issues)
        {
            builder.AppendLine($"- {issue}");
        }

        builder.AppendLine();
    }
}
