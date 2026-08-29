using EitHost.Core.Hardware.Usb2070;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public sealed class Hdf5RunInspector
{
    public Hdf5RunInspection Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("HDF5 file not found.", fullPath);
        }

        using var file = Hdf5FileAccess.OpenReadWithRetry(fullPath);
        var rawDataset = file.Dataset("/raw/adc_counts");
        var rawDimensions = rawDataset.Space.Dimensions.ToArray();
        var sessionId = ReadString(file, "/metadata/run/session_id");
        var runId = ReadString(file, "/metadata/run/run_id");
        var capturedAtUtc = ReadString(file, "/metadata/run/captured_at_utc");
        var sampleRows = ReadLongCompatible(file, "/metadata/run/sample_rows");
        var channelCount = ReadInt(file, "/metadata/run/channel_count");
        var device = new Hdf5RunInspectionDevice(
            ReadString(file, "/metadata/device/set_label"),
            ReadInt(file, "/metadata/device/measurement_channel_count"),
            ReadInt(file, "/metadata/device/usb2070_device_number"),
            ReadString(file, "/metadata/device/usb2070_device_id"),
            ReadString(file, "/metadata/device/usb2070_display_name"),
            ReadString(file, "/metadata/device/usb2070_vid"),
            ReadString(file, "/metadata/device/usb2070_pid"),
            ReadString(file, "/metadata/device/usb2070_location_path"),
            ReadString(file, "/metadata/device/dds_port_name"),
            ReadString(file, "/metadata/device/dds_device_id"),
            ReadString(file, "/metadata/device/dds_display_name"),
            ReadString(file, "/metadata/device/dds_vid"),
            ReadString(file, "/metadata/device/dds_pid"),
            ReadString(file, "/metadata/device/dds_location_path"));
        var excitation = new Hdf5RunInspectionExcitation(
            ReadString(file, "/metadata/excitation/mode"),
            ReadInt(file, "/metadata/excitation/mode_code"),
            ReadInt(file, "/metadata/excitation/frequency_hz"),
            ReadDouble(file, "/metadata/excitation/channel_cycles"),
            ReadInt(file, "/metadata/excitation/scan_times"),
            ReadByte(file, "/metadata/excitation/dac_channel"),
            ReadDouble(file, "/metadata/excitation/dac_gain"),
            ReadInt(file, "/metadata/excitation/dac_phase_deg"),
            ReadByte(file, "/metadata/excitation/pga_gain"),
            ReadInt(file, "/metadata/excitation/overhead_us"),
            ReadInt(file, "/metadata/excitation/time_us"),
            TryReadByte(file, "/metadata/excitation/firmware_protocol_version"),
            TryReadString(file, "/metadata/excitation/firmware_version"),
            TryReadUShort(file, "/metadata/excitation/firmware_feature_flags"),
            TryReadUInt(file, "/metadata/excitation/requested_time_us"),
            TryReadString(file, "/metadata/excitation/timer_mode"),
            TryReadUInt(file, "/metadata/excitation/timer_clock_hz"),
            TryReadUShort(file, "/metadata/excitation/timer_ticks"),
            TryReadDouble(file, "/metadata/excitation/effective_time_us"),
            TryReadDouble(file, "/metadata/excitation/effective_channel_cycles"),
            TryReadUShort(file, "/metadata/excitation/switch_guard_us"),
            TryReadString(file, "/metadata/excitation/switch_guard_semantics"),
            TryReadString(file, "/metadata/excitation/scan_status_state"),
            TryReadByte(file, "/metadata/excitation/scan_status_state_code"),
            TryReadBool(file, "/metadata/excitation/scan_status_running"),
            TryReadByte(file, "/metadata/excitation/scan_status_current_step"),
            TryReadUInt(file, "/metadata/excitation/scan_status_target_cycles"),
            TryReadUInt(file, "/metadata/excitation/scan_status_completed_cycles"),
            TryReadInt(file, "/metadata/excitation/requested_frequency_hz"),
            TryReadUInt(file, "/metadata/excitation/frequency_tuning_word"),
            TryReadDouble(file, "/metadata/excitation/actual_frequency_hz"),
            TryReadDouble(file, "/metadata/excitation/frequency_error_hz"));
        var acquisition = new Hdf5RunInspectionAcquisition(
            ReadInt(file, "/metadata/acquisition/sample_rate_hz"),
            ReadString(file, "/metadata/acquisition/ad_range"),
            ReadInt(file, "/metadata/acquisition/ad_range_code"),
            ReadInt(file, "/metadata/acquisition/ad_bit"),
            ReadIntArray(file, "/metadata/acquisition/enabled_channels"),
            ReadString(file, "/metadata/acquisition/trigger_mode"),
            ReadInt(file, "/metadata/acquisition/trigger_mode_code"),
            ReadString(file, "/metadata/acquisition/trigger_source"),
            ReadInt(file, "/metadata/acquisition/trigger_source_code"),
            TryReadDouble(file, "/metadata/acquisition/ad_full_span_volts"),
            TryReadDouble(file, "/metadata/acquisition/ad_lsb_volts"));

        return new Hdf5RunInspection(
            fullPath,
            "/raw/adc_counts",
            rawDimensions,
            sessionId,
            runId,
            capturedAtUtc,
            sampleRows,
            channelCount,
            device,
            excitation,
            acquisition,
            CreateIssues(rawDimensions, sessionId, runId, sampleRows, channelCount, device, acquisition));
    }

    private static IReadOnlyList<string> CreateIssues(
        IReadOnlyList<ulong> rawDimensions,
        string sessionId,
        string runId,
        long sampleRows,
        int channelCount,
        Hdf5RunInspectionDevice device,
        Hdf5RunInspectionAcquisition acquisition)
    {
        var issues = new List<string>();
        if (rawDimensions.Count != 2)
        {
            issues.Add($"/raw/adc_counts rank 应为 2，实际为 {rawDimensions.Count}。");
        }
        else
        {
            if (rawDimensions[0] != (ulong)sampleRows)
            {
                issues.Add($"/raw/adc_counts 行数 {rawDimensions[0]} 与 metadata sample_rows {sampleRows} 不一致。");
            }

            if (rawDimensions[1] != (ulong)channelCount)
            {
                issues.Add($"/raw/adc_counts 通道数 {rawDimensions[1]} 与 metadata channel_count {channelCount} 不一致。");
            }
        }

        if (channelCount != Usb2070Constants.RequiredMeasurementChannelCount)
        {
            issues.Add($"metadata channel_count 应为 {Usb2070Constants.RequiredMeasurementChannelCount}，实际为 {channelCount}。");
        }

        if (device.MeasurementChannelCount != Usb2070Constants.RequiredMeasurementChannelCount)
        {
            issues.Add($"device measurement_channel_count 应为 {Usb2070Constants.RequiredMeasurementChannelCount}，实际为 {device.MeasurementChannelCount}。");
        }

        if (!Guid.TryParse(sessionId, out _))
        {
            issues.Add("session_id 不是有效 GUID。");
        }

        if (!Guid.TryParse(runId, out _))
        {
            issues.Add("run_id 不是有效 GUID。");
        }

        if (string.IsNullOrWhiteSpace(device.SetLabel))
        {
            issues.Add("set_label 为空。");
        }

        var expectedChannels = Enumerable.Range(1, Usb2070Constants.RequiredMeasurementChannelCount).ToArray();
        if (!acquisition.EnabledChannels.SequenceEqual(expectedChannels))
        {
            issues.Add("enabled_channels 应为 1..16。");
        }

        return issues;
    }

    private static string ReadString(IH5Group file, string path)
    {
        return file.Dataset(path).Read<string>();
    }

    private static int ReadInt(IH5Group file, string path)
    {
        return file.Dataset(path).Read<int>();
    }

    private static long ReadLongCompatible(IH5Group file, string path)
    {
        try
        {
            return file.Dataset(path).Read<long>();
        }
        catch
        {
            return file.Dataset(path).Read<int>();
        }
    }

    private static byte ReadByte(IH5Group file, string path)
    {
        return file.Dataset(path).Read<byte>();
    }

    private static double ReadDouble(IH5Group file, string path)
    {
        return file.Dataset(path).Read<double>();
    }

    private static IReadOnlyList<int> ReadIntArray(IH5Group file, string path)
    {
        return file.Dataset(path).Read<int[]>();
    }

    private static string? TryReadString(IH5Group file, string path) =>
        file.LinkExists(path) ? ReadString(file, path) : null;

    private static byte? TryReadByte(IH5Group file, string path) =>
        file.LinkExists(path) ? ReadByte(file, path) : null;

    private static int? TryReadInt(IH5Group file, string path) =>
        file.LinkExists(path) ? ReadInt(file, path) : null;

    private static ushort? TryReadUShort(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<ushort>() : null;

    private static bool? TryReadBool(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<bool>() : null;

    private static uint? TryReadUInt(IH5Group file, string path) =>
        file.LinkExists(path) ? file.Dataset(path).Read<uint>() : null;

    private static double? TryReadDouble(IH5Group file, string path) =>
        file.LinkExists(path) ? ReadDouble(file, path) : null;
}

public sealed record Hdf5RunInspection(
    string FilePath,
    string RawDatasetPath,
    IReadOnlyList<ulong> RawDimensions,
    string SessionId,
    string RunId,
    string CapturedAtUtc,
    long MetadataSampleRows,
    int MetadataChannelCount,
    Hdf5RunInspectionDevice Device,
    Hdf5RunInspectionExcitation Excitation,
    Hdf5RunInspectionAcquisition Acquisition,
    IReadOnlyList<string> Issues)
{
    public bool Passed => Issues.Count == 0;

    public ulong RawSampleRows => RawDimensions.Count > 0 ? RawDimensions[0] : 0;

    public ulong RawChannelCount => RawDimensions.Count > 1 ? RawDimensions[1] : 0;
}

public sealed record Hdf5RunInspectionDevice(
    string SetLabel,
    int MeasurementChannelCount,
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

public sealed record Hdf5RunInspectionExcitation(
    string Mode,
    int ModeCode,
    int FrequencyHz,
    double ChannelCycles,
    int ScanTimes,
    int DacChannel,
    double DacGain,
    int DacPhaseDegrees,
    int PgaGain,
    int OverheadUs,
    int TimeUs,
    int? FirmwareProtocolVersion = null,
    string? FirmwareVersion = null,
    int? FirmwareFeatureFlags = null,
    uint? RequestedTimeUs = null,
    string? TimerMode = null,
    uint? TimerClockHz = null,
    int? TimerTicks = null,
    double? EffectiveTimeUs = null,
    double? EffectiveChannelCycles = null,
    int? SwitchGuardMinimumUs = null,
    string? SwitchGuardSemantics = null,
    string? ScanStatusState = null,
    int? ScanStatusStateCode = null,
    bool? ScanStatusRunning = null,
    int? ScanStatusCurrentStep = null,
    uint? ScanStatusTargetCycles = null,
    uint? ScanStatusCompletedCycles = null,
    int? RequestedFrequencyHz = null,
    uint? FrequencyTuningWord = null,
    double? ActualFrequencyHz = null,
    double? FrequencyErrorHz = null);

public sealed record Hdf5RunInspectionAcquisition(
    int SampleRateHz,
    string AdRange,
    int AdRangeCode,
    int AdBit,
    IReadOnlyList<int> EnabledChannels,
    string TriggerMode,
    int TriggerModeCode,
    string TriggerSource,
    int TriggerSourceCode,
    double? AdFullSpanVolts = null,
    double? AdLsbVolts = null);
