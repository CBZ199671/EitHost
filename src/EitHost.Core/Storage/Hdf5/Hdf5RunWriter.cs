using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public sealed class Hdf5RunWriter
{
    public void Write(string filePath, Hdf5RunData runData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(runData);

        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var file = new H5File
        {
            ["raw"] = new H5Group
            {
                ["adc_counts"] = runData.AdcCounts
            },
            ["metadata"] = new H5Group
            {
                ["run"] = CreateRunGroup(runData),
                ["device"] = CreateDeviceGroup(runData),
                ["excitation"] = CreateExcitationGroup(runData),
                ["acquisition"] = CreateAcquisitionGroup(runData)
            }
        };

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.partial";
        try
        {
            file.Write(temporaryPath);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    private static H5Group CreateRunGroup(Hdf5RunData runData)
    {
        return new H5Group
        {
            ["session_id"] = runData.SessionId.ToString("D"),
            ["run_id"] = runData.RunId.ToString("D"),
            ["captured_at_utc"] = runData.CapturedAt.ToUniversalTime().ToString("O"),
            ["sample_rows"] = runData.AdcCounts.GetLength(0),
            ["channel_count"] = runData.AdcCounts.GetLength(1)
        };
    }

    private static H5Group CreateDeviceGroup(Hdf5RunData runData)
    {
        var device = runData.Device;
        return new H5Group
        {
            ["set_label"] = device.SetLabel,
            ["measurement_channel_count"] = device.MeasurementChannelCount,
            ["usb2070_device_number"] = device.UsbDeviceNumber,
            ["usb2070_device_id"] = device.UsbDeviceId,
            ["usb2070_display_name"] = device.UsbDisplayName,
            ["usb2070_vid"] = device.UsbVid,
            ["usb2070_pid"] = device.UsbPid,
            ["usb2070_location_path"] = device.UsbLocationPath,
            ["dds_port_name"] = device.DdsPortName,
            ["dds_device_id"] = device.DdsDeviceId,
            ["dds_display_name"] = device.DdsDisplayName,
            ["dds_vid"] = device.DdsVid,
            ["dds_pid"] = device.DdsPid,
            ["dds_location_path"] = device.DdsLocationPath
        };
    }

    private static H5Group CreateExcitationGroup(Hdf5RunData runData)
    {
        var dac = runData.Excitation.Dac;
        var excitation = runData.Excitation.Excitation;
        var group = new H5Group
        {
            ["mode"] = excitation.Mode.ToString(),
            ["mode_code"] = (int)excitation.Mode,
            ["frequency_hz"] = excitation.FrequencyHz,
            ["requested_frequency_hz"] = dac.FrequencyHz,
            ["frequency_tuning_word"] = dac.FrequencyTuningWord,
            ["actual_frequency_hz"] = dac.ActualFrequencyHz,
            ["frequency_error_hz"] = dac.FrequencyErrorHz,
            ["channel_cycles"] = excitation.ChannelCycles,
            ["scan_times"] = excitation.ScanTimes,
            ["dac_channel"] = dac.Channel,
            ["dac_gain"] = dac.Gain,
            ["dac_phase_deg"] = dac.PhaseDegrees,
            ["pga_gain"] = runData.Excitation.PgaGain,
            ["overhead_us"] = excitation.OverheadUs,
            ["time_us"] = excitation.CalculateTimeUs()
        };

        if (runData.Excitation.Execution is { } execution)
        {
            group["firmware_protocol_version"] = execution.FirmwareProtocolVersion;
            group["firmware_version"] = execution.FirmwareVersion.ToString(3);
            if (execution.FirmwareFeatureFlags != 0)
            {
                group["firmware_feature_flags"] = execution.FirmwareFeatureFlags;
            }

            group["requested_time_us"] = execution.RequestedTimeUs;
            group["timer_mode"] = "12T";
            group["timer_clock_hz"] = execution.TimerClockHz;
            group["timer_ticks"] = execution.TimerTicks;
            group["effective_time_us"] = execution.EffectiveTimeUs;
            group["effective_channel_cycles"] = execution.CalculateEffectiveChannelCycles(dac.ActualFrequencyHz);
            group["switch_guard_us"] = execution.SwitchGuardMinimumUs;
            group["switch_guard_semantics"] = DdsProtocolConstants.SwitchGuardSemantics;
        }

        if (runData.Excitation.ScanStatus is { } scanStatus)
        {
            group["scan_status_state"] = scanStatus.State.ToString();
            group["scan_status_state_code"] = (byte)scanStatus.State;
            group["scan_status_running"] = scanStatus.Running;
            group["scan_status_current_step"] = scanStatus.CurrentStep;
            group["scan_status_target_cycles"] = scanStatus.TargetCycles;
            group["scan_status_completed_cycles"] = scanStatus.CompletedCycles;
        }

        return group;
    }

    private static H5Group CreateAcquisitionGroup(Hdf5RunData runData)
    {
        var acquisition = runData.Acquisition;
        return new H5Group
        {
            ["sample_rate_hz"] = acquisition.SampleRateHz,
            ["ad_range"] = acquisition.Range.ToString(),
            ["ad_range_code"] = (int)acquisition.Range,
            ["ad_full_span_volts"] = Usb2070VoltageScale.GetFullSpanVolts(acquisition.Range),
            ["ad_lsb_volts"] = Usb2070VoltageScale.GetLsbVolts(acquisition.Range),
            ["ad_bit"] = acquisition.AdBit,
            ["enabled_channels"] = acquisition.EnabledOneBasedChannels.ToArray(),
            ["trigger_mode"] = acquisition.TriggerMode.ToString(),
            ["trigger_mode_code"] = (int)acquisition.TriggerMode,
            ["trigger_source"] = acquisition.TriggerSource.ToString(),
            ["trigger_source_code"] = (int)acquisition.TriggerSource
        };
    }
}
