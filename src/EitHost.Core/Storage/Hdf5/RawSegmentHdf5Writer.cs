using EitHost.Core.Domain;
using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

public sealed class RawSegmentHdf5Writer
{
    public void Write(string filePath, RawSegmentData segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        WriteCore(filePath, segment);
    }

    public void Write(string filePath, InterleavedRawSegmentData segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        WriteCore(filePath, segment);
    }

    private static void WriteCore(string filePath, IRawSegmentWriteData segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

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
                ["adc_counts"] = segment.CreateAdcDataset()
            },
            ["metadata"] = CreateMetadataGroup(segment)
        };

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.partial";
        try
        {
            file.Write(temporaryPath);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    internal static H5Group CreateMetadataGroup(IRawSegmentWriteData segment)
    {
        var metadata = new H5Group
        {
            ["run"] = CreateRunGroup(segment),
            ["device"] = CreateDeviceGroup(segment),
            ["excitation"] = CreateExcitationGroup(segment),
            ["acquisition"] = CreateAcquisitionGroup(segment)
        };
        if (segment.Demodulation is { } demodulation)
        {
            metadata["demodulation"] = new H5Group
            {
                ["frames_per_block"] = demodulation.FramesPerBlock,
                ["minimum_accepted_frames"] = demodulation.MinimumAcceptedFrames,
                ["discard_leading_cycles"] = demodulation.DiscardLeadingCycles,
                ["discard_trailing_cycles"] = demodulation.DiscardTrailingCycles,
                ["interference_frequency_hz"] = demodulation.InterferenceFrequencyHz.ToArray(),
                ["settings_contract"] = "production_realtime_v1"
            };
        }

        return metadata;
    }

    private static H5Group CreateRunGroup(IRawSegmentWriteData segment)
    {
        return new H5Group
        {
            ["session_id"] = segment.SessionId.ToString("D"),
            ["run_id"] = segment.ExperimentRunId.ToString("D"),
            ["experiment_run_id"] = segment.ExperimentRunId.ToString("D"),
            ["segment_sequence"] = segment.SegmentSequence,
            ["start_sample_index"] = segment.StartSampleIndex,
            ["end_sample_index"] = segment.EndSampleIndex,
            ["captured_at_utc"] = segment.CapturedAt.ToUniversalTime().ToString("O"),
            ["sample_rows"] = segment.SampleRows,
            ["channel_count"] = segment.ChannelCount
        };
    }

    private static H5Group CreateDeviceGroup(IRawSegmentWriteData segment)
    {
        var device = segment.Device;
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

    private static H5Group CreateExcitationGroup(IRawSegmentWriteData segment)
    {
        var dac = segment.Excitation.Dac;
        var excitation = segment.Excitation.Excitation;
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
            ["pga_gain"] = segment.Excitation.PgaGain,
            ["overhead_us"] = excitation.OverheadUs,
            ["time_us"] = excitation.CalculateTimeUs()
        };

        if (segment.Excitation.Execution is { } execution)
        {
            group["firmware_protocol_version"] = execution.FirmwareProtocolVersion;
            group["firmware_version"] = execution.FirmwareVersion.ToString(3);
            group["firmware_feature_flags"] = execution.FirmwareFeatureFlags;
            group["requested_time_us"] = execution.RequestedTimeUs;
            group["timer_mode"] = "12T";
            group["timer_clock_hz"] = execution.TimerClockHz;
            group["timer_ticks"] = execution.TimerTicks;
            group["effective_time_us"] = execution.EffectiveTimeUs;
            group["effective_channel_cycles"] = execution.CalculateEffectiveChannelCycles(dac.ActualFrequencyHz);
            group["switch_guard_us"] = execution.SwitchGuardMinimumUs;
            group["switch_guard_semantics"] = DdsProtocolConstants.SwitchGuardSemantics;
        }

        return group;
    }

    private static H5Group CreateAcquisitionGroup(IRawSegmentWriteData segment)
    {
        var acquisition = segment.Acquisition;
        var group = new H5Group
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
            ["trigger_source_code"] = (int)acquisition.TriggerSource,
            ["has_discontinuity"] = segment.Discontinuities.Count > 0
        };
        if (segment.Discontinuities.Count > 0)
        {
            var ranges = new long[segment.Discontinuities.Count, 2];
            var detectedAt = new string[segment.Discontinuities.Count];
            var reasons = new string[segment.Discontinuities.Count];
            for (var index = 0; index < segment.Discontinuities.Count; index++)
            {
                var discontinuity = segment.Discontinuities[index];
                ranges[index, 0] = discontinuity.StartSampleIndex;
                ranges[index, 1] = discontinuity.EndSampleIndex;
                detectedAt[index] = discontinuity.DetectedAt.ToUniversalTime().ToString("O");
                reasons[index] = discontinuity.Reason;
            }

            group["overflow_events"] = ranges;
            group["overflow_event_detected_at_utc"] = detectedAt;
            group["overflow_event_reason"] = reasons;
        }

        return group;
    }
}

internal interface IRawSegmentWriteData
{
    Guid ExperimentRunId { get; }
    Guid SessionId { get; }
    int SegmentSequence { get; }
    long StartSampleIndex { get; }
    long EndSampleIndex { get; }
    DateTimeOffset CapturedAt { get; }
    DeviceRunMetadata Device { get; }
    Hdf5ExcitationMetadata Excitation { get; }
    Usb2070AcquisitionMetadata Acquisition { get; }
    RawSegmentDemodulationMetadata? Demodulation { get; }
    IReadOnlyList<RawAcquisitionDiscontinuityEvent> Discontinuities { get; }
    int SampleRows { get; }
    int ChannelCount { get; }
    object CreateAdcDataset();
}

public sealed record RawSegmentData : IRawSegmentWriteData
{
    public RawSegmentData(
        Guid experimentRunId,
        Guid sessionId,
        int segmentSequence,
        long startSampleIndex,
        long endSampleIndex,
        DateTimeOffset capturedAt,
        DeviceRunMetadata device,
        Hdf5ExcitationMetadata excitation,
        Usb2070AcquisitionMetadata acquisition,
        ushort[,] adcCounts,
        RawSegmentDemodulationMetadata? demodulation = null,
        IReadOnlyList<RawAcquisitionDiscontinuityEvent>? discontinuities = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(excitation);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(adcCounts);
        if (adcCounts.GetLength(0) <= 0 || adcCounts.GetLength(1) != EitSet.MeasurementChannelCount)
        {
            throw new ArgumentException("Raw segment must contain at least one row and exactly 16 channels.", nameof(adcCounts));
        }

        var expectedEnd = checked(startSampleIndex + adcCounts.GetLength(0));
        if (endSampleIndex != expectedEnd)
        {
            throw new ArgumentException("Raw segment sample range must match ADC row count.", nameof(endSampleIndex));
        }

        var normalizedDiscontinuities = NormalizeDiscontinuities(
            startSampleIndex,
            endSampleIndex,
            discontinuities);

        ExperimentRunId = experimentRunId;
        SessionId = sessionId;
        SegmentSequence = segmentSequence;
        StartSampleIndex = startSampleIndex;
        EndSampleIndex = endSampleIndex;
        CapturedAt = capturedAt;
        Device = device;
        Excitation = excitation;
        Acquisition = acquisition;
        AdcCounts = adcCounts;
        Demodulation = demodulation;
        Discontinuities = normalizedDiscontinuities;
    }

    internal static IReadOnlyList<RawAcquisitionDiscontinuityEvent> NormalizeDiscontinuities(
        long startSampleIndex,
        long endSampleIndex,
        IReadOnlyList<RawAcquisitionDiscontinuityEvent>? discontinuities)
    {
        var normalizedDiscontinuities = (discontinuities ?? [])
            .OrderBy(item => item.StartSampleIndex)
            .ThenBy(item => item.EndSampleIndex)
            .ToArray();
        long previousEnd = startSampleIndex;
        foreach (var discontinuity in normalizedDiscontinuities)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(discontinuity.Reason);
            if (discontinuity.StartSampleIndex < startSampleIndex ||
                discontinuity.EndSampleIndex > endSampleIndex ||
                discontinuity.EndSampleIndex <= discontinuity.StartSampleIndex ||
                discontinuity.StartSampleIndex < previousEnd)
            {
                throw new ArgumentException(
                    "Raw acquisition discontinuities must be ordered, non-overlapping ranges contained by the segment.",
                    nameof(discontinuities));
            }

            previousEnd = discontinuity.EndSampleIndex;
        }

        return normalizedDiscontinuities;
    }

    public Guid ExperimentRunId { get; }

    public Guid SessionId { get; }

    public int SegmentSequence { get; }

    public long StartSampleIndex { get; }

    public long EndSampleIndex { get; }

    public DateTimeOffset CapturedAt { get; }

    public DeviceRunMetadata Device { get; }

    public Hdf5ExcitationMetadata Excitation { get; }

    public Usb2070AcquisitionMetadata Acquisition { get; }

    public ushort[,] AdcCounts { get; }

    public RawSegmentDemodulationMetadata? Demodulation { get; }

    public IReadOnlyList<RawAcquisitionDiscontinuityEvent> Discontinuities { get; }

    int IRawSegmentWriteData.SampleRows => AdcCounts.GetLength(0);

    int IRawSegmentWriteData.ChannelCount => AdcCounts.GetLength(1);

    object IRawSegmentWriteData.CreateAdcDataset() => Hdf5StoragePolicy.Numeric(AdcCounts);
}

public sealed record InterleavedRawSegmentData : IRawSegmentWriteData
{
    public InterleavedRawSegmentData(
        Guid experimentRunId,
        Guid sessionId,
        int segmentSequence,
        long startSampleIndex,
        long endSampleIndex,
        DateTimeOffset capturedAt,
        DeviceRunMetadata device,
        Hdf5ExcitationMetadata excitation,
        Usb2070AcquisitionMetadata acquisition,
        ushort[] interleavedValues,
        int valueCount,
        int channelCount,
        RawSegmentDemodulationMetadata? demodulation = null,
        IReadOnlyList<RawAcquisitionDiscontinuityEvent>? discontinuities = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(segmentSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(excitation);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(interleavedValues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);
        if (valueCount != interleavedValues.Length || valueCount % channelCount != 0)
        {
            throw new ArgumentException("Interleaved raw payload must contain exact complete channel rows.", nameof(valueCount));
        }

        var sampleRows = valueCount / channelCount;
        if (channelCount != EitSet.MeasurementChannelCount ||
            endSampleIndex != checked(startSampleIndex + sampleRows))
        {
            throw new ArgumentException("Interleaved raw sample range must match exact 16-channel rows.", nameof(endSampleIndex));
        }

        var normalizedDiscontinuities = RawSegmentData.NormalizeDiscontinuities(
            startSampleIndex,
            endSampleIndex,
            discontinuities);
        ExperimentRunId = experimentRunId;
        SessionId = sessionId;
        SegmentSequence = segmentSequence;
        StartSampleIndex = startSampleIndex;
        EndSampleIndex = endSampleIndex;
        CapturedAt = capturedAt;
        Device = device;
        Excitation = excitation;
        Acquisition = acquisition;
        InterleavedValues = interleavedValues;
        ValueCount = valueCount;
        ChannelCount = channelCount;
        SampleRows = sampleRows;
        Demodulation = demodulation;
        Discontinuities = normalizedDiscontinuities;
    }

    public Guid ExperimentRunId { get; }
    public Guid SessionId { get; }
    public int SegmentSequence { get; }
    public long StartSampleIndex { get; }
    public long EndSampleIndex { get; }
    public DateTimeOffset CapturedAt { get; }
    public DeviceRunMetadata Device { get; }
    public Hdf5ExcitationMetadata Excitation { get; }
    public Usb2070AcquisitionMetadata Acquisition { get; }
    public ushort[] InterleavedValues { get; }
    public int ValueCount { get; }
    public int SampleRows { get; }
    public int ChannelCount { get; }
    public RawSegmentDemodulationMetadata? Demodulation { get; }
    public IReadOnlyList<RawAcquisitionDiscontinuityEvent> Discontinuities { get; }

    object IRawSegmentWriteData.CreateAdcDataset() =>
        Hdf5StoragePolicy.Numeric(InterleavedValues, SampleRows, ChannelCount);
}

public sealed record RawAcquisitionDiscontinuityEvent(
    long StartSampleIndex,
    long EndSampleIndex,
    DateTimeOffset DetectedAt,
    string Reason = RawAcquisitionDiscontinuityEvent.UsbBufferOverflowReason)
{
    public const string UsbBufferOverflowReason = "usb-buffer-overflow";
}

public sealed record RawSegmentDemodulationMetadata(
    int FramesPerBlock,
    int MinimumAcceptedFrames,
    double DiscardLeadingCycles,
    double DiscardTrailingCycles,
    IReadOnlyList<double> InterferenceFrequencyHz)
{
    public static RawSegmentDemodulationMetadata From(RealtimeDemodulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new RawSegmentDemodulationMetadata(
            settings.FramesPerBlock,
            settings.MinimumAcceptedFrames,
            settings.DiscardLeadingCycles,
            settings.DiscardTrailingCycles,
            settings.InterferenceFrequencyHz.ToArray());
    }
}
