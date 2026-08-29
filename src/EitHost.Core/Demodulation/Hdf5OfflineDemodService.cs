using EitHost.Core.Storage.Hdf5;
using PureHDF;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Demodulation;

public sealed class Hdf5OfflineDemodService
{
    private readonly OfflineDemodulator demodulator;

    public Hdf5OfflineDemodService(OfflineDemodulator? demodulator = null)
    {
        this.demodulator = demodulator ?? new OfflineDemodulator();
    }

    public OfflineDemodulationResult DemodulateFile(
        string inputHdf5Path,
        string outputHdf5Path,
        OfflineDemodulationSettings? settings = null)
    {
        return DemodulateFileDetailed(inputHdf5Path, outputHdf5Path, settings).Demodulation;
    }

    public Hdf5OfflineDemodulationFileResult DemodulateFileDetailed(
        string inputHdf5Path,
        string outputHdf5Path,
        OfflineDemodulationSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHdf5Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHdf5Path);

        var inputFullPath = Path.GetFullPath(inputHdf5Path);
        var outputFullPath = Path.GetFullPath(outputHdf5Path);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Offline demodulation output HDF5 path must differ from input path.", nameof(outputHdf5Path));
        }

        using var inputFile = Hdf5FileAccess.OpenReadWithRetry(inputHdf5Path);
        var raw = inputFile.Dataset("/raw/adc_counts").Read<ushort[,]>();
        var sourceRunId = Guid.Parse(inputFile.Dataset("/metadata/run/run_id").Read<string>());
        var adRange = inputFile.LinkExists("/metadata/acquisition/ad_range_code")
            ? (Usb2070AdRange)inputFile.Dataset("/metadata/acquisition/ad_range_code").Read<int>()
            : Usb2070AdRange.Bipolar5V;
        var demodulationFrequencyHz = inputFile.LinkExists("/metadata/excitation/actual_frequency_hz")
            ? inputFile.Dataset("/metadata/excitation/actual_frequency_hz").Read<double>()
            : inputFile.Dataset("/metadata/excitation/frequency_hz").Read<int>();
        settings ??= new OfflineDemodulationSettings(
            inputFile.Dataset("/metadata/acquisition/sample_rate_hz").Read<int>(),
            demodulationFrequencyHz,
            channelCycles: inputFile.LinkExists("/metadata/excitation/effective_channel_cycles")
                ? inputFile.Dataset("/metadata/excitation/effective_channel_cycles").Read<double>()
                : inputFile.Dataset("/metadata/excitation/channel_cycles").Read<double>(),
            adRange: adRange);

        var result = demodulator.Demodulate(raw, settings);
        WriteResult(outputHdf5Path, inputHdf5Path, result, settings);
        return new Hdf5OfflineDemodulationFileResult(
            sourceRunId,
            inputFullPath,
            outputFullPath,
            result);
    }

    public Hdf5OfflineDemodulationFileResult WriteDemodulationResult(
        string inputHdf5Path,
        string outputHdf5Path,
        OfflineDemodulationResult result,
        OfflineDemodulationSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHdf5Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHdf5Path);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(settings);

        var inputFullPath = Path.GetFullPath(inputHdf5Path);
        var outputFullPath = Path.GetFullPath(outputHdf5Path);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Offline demodulation output HDF5 path must differ from input path.", nameof(outputHdf5Path));
        }

        using var inputFile = Hdf5FileAccess.OpenReadWithRetry(inputHdf5Path);
        var sourceRunId = Guid.Parse(inputFile.Dataset("/metadata/run/run_id").Read<string>());
        WriteResult(outputHdf5Path, inputHdf5Path, result, settings);
        return new Hdf5OfflineDemodulationFileResult(
            sourceRunId,
            inputFullPath,
            outputFullPath,
            result);
    }

    private static void WriteResult(
        string outputHdf5Path,
        string inputHdf5Path,
        OfflineDemodulationResult result,
        OfflineDemodulationSettings settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputHdf5Path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputHdf5Path))
        {
            File.Delete(outputHdf5Path);
        }

        var reciprocalWindowDurationMs = AdjacentReciprocalTiming.CalculateNominalWindowDurationMs(
            settings.ExcitationFrequencyHz,
            settings.ChannelCycles);
        var effectiveDiscard = settings.ResolveWindowDiscard(
            result.EstimatedWindowSamples,
            Math.Max(0, (int)Math.Round(result.EstimatedWindowSamples)));
        var demodGroup = new H5Group
        {
            ["peak_locations"] = result.PeakLocations.ToArray(),
            ["stim_pairs_16x2"] = AdjacentAmplitudeFrameLayout.CreateStimulusPairsOneBased(),
            ["measurement_pairs_208x2"] = AdjacentAmplitudeFrameLayout.CreateMeasurementPairsOneBased(),
            ["channel_map_208x4"] = AdjacentAmplitudeFrameLayout.CreateChannelMapOneBased(),
            ["full_channel_map_256x4"] = AdjacentAmplitudeFrameLayout.CreateFullChannelMapOneBased(),
            ["excluded_k_indices_zero_based"] = AdjacentAmplitudeFrameLayout.ExcludedKIndices,
            ["mean_amp_16x13"] = result.Average.Amplitudes,
            ["mean_real_16x13"] = result.Average.RealComponents,
            ["mean_imag_16x13"] = result.Average.ImaginaryComponents,
            ["mean_amp_16x16"] = RequireFullMatrix(result.Average.FullAmplitudes, "average full amplitude"),
            ["mean_real_16x16"] = RequireFullMatrix(result.Average.FullRealComponents, "average full real"),
            ["mean_imag_16x16"] = RequireFullMatrix(result.Average.FullImaginaryComponents, "average full imaginary"),
            ["mean_amp_208"] = result.Average.FlattenAmplitudesRowMajor(),
            ["mean_real_208"] = result.Average.FlattenRealRowMajor(),
            ["mean_imag_208"] = result.Average.FlattenImaginaryRowMajor(),
            ["mean_amp_256"] = result.Average.FlattenFullAmplitudesRowMajor(),
            ["mean_real_256"] = result.Average.FlattenFullRealRowMajor(),
            ["mean_imag_256"] = result.Average.FlattenFullImaginaryRowMajor(),
            ["mean_sample_counts_16x13"] = result.Average.SampleCounts,
            ["mean_sample_counts_16x16"] = RequireFullIntMatrix(result.Average.FullSampleCounts, "average full sample counts"),
            ["mean_accepted_frames"] = result.Average.AcceptedFrameNumbers.ToArray(),
            ["mean_rejected_frames"] = result.Average.RejectedFrameNumbers.ToArray()
        };
        if (result.TrustedPartialAverage is { } trustedPartial)
        {
            AddObservationAggregate(demodGroup, "trusted_partial_mean", trustedPartial);
        }
        if (result.DiagnosticAverage is { } diagnostic)
        {
            AddObservationAggregate(demodGroup, "diagnostic_mean", diagnostic);
        }

        if (result.Frames.Count > 0)
        {
            demodGroup["frame_boundaries_samples_Nx2"] = CreateFrameBoundaries(result.Frames);
            demodGroup["frames_amp_256"] = StackFullFrameDoubles(result.Frames, frame => frame.FullAmplitudes, "full amplitude");
            demodGroup["frames_real_256"] = StackFullFrameDoubles(result.Frames, frame => frame.FullRealComponents, "full real");
            demodGroup["frames_imag_256"] = StackFullFrameDoubles(result.Frames, frame => frame.FullImaginaryComponents, "full imaginary");
            demodGroup["frames_sat_256"] = StackFullFrameInts(result.Frames);
        }

        foreach (var frame in result.Frames)
        {
            demodGroup[$"frame_{frame.FrameNumber:000}_amp_16x13"] = frame.Amplitudes;
            demodGroup[$"frame_{frame.FrameNumber:000}_real_16x13"] = frame.RealComponents;
            demodGroup[$"frame_{frame.FrameNumber:000}_imag_16x13"] = frame.ImaginaryComponents;
            demodGroup[$"frame_{frame.FrameNumber:000}_amp_16x16"] = RequireFullMatrix(frame.FullAmplitudes, "frame full amplitude");
            demodGroup[$"frame_{frame.FrameNumber:000}_real_16x16"] = RequireFullMatrix(frame.FullRealComponents, "frame full real");
            demodGroup[$"frame_{frame.FrameNumber:000}_imag_16x16"] = RequireFullMatrix(frame.FullImaginaryComponents, "frame full imaginary");
            demodGroup[$"frame_{frame.FrameNumber:000}_amp_208"] = frame.FlattenAmplitudesRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_real_208"] = frame.FlattenRealRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_imag_208"] = frame.FlattenImaginaryRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_amp_256"] = frame.FlattenFullAmplitudesRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_real_256"] = frame.FlattenFullRealRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_imag_256"] = frame.FlattenFullImaginaryRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_sat_16x16"] = RequireFullIntMatrix(frame.FullSaturationCounts, "frame Sat256");
            demodGroup[$"frame_{frame.FrameNumber:000}_sat_256"] = frame.FlattenFullSaturationCountsRowMajor();
            demodGroup[$"frame_{frame.FrameNumber:000}_quality"] = frame.QualityMatrix();
            demodGroup[$"frame_{frame.FrameNumber:000}_quality_metrics"] = frame.QualityMetricsMatrix();
            demodGroup[$"frame_{frame.FrameNumber:000}_reference_channels"] = frame.ReferenceChannelsOneBased();
            demodGroup[$"frame_{frame.FrameNumber:000}_rejected_windows"] = frame.RejectedWindowIndexesOneBased();
            demodGroup[$"frame_{frame.FrameNumber:000}_stim_counts_16x3"] = frame.StimulationWindowCounts;
            if (frame.DiagnosticObservation is { } diagnosticObservation)
            {
                AddObservationAggregate(
                    demodGroup,
                    $"frame_{frame.FrameNumber:000}_diagnostic",
                    diagnosticObservation);
            }
        }

        var file = new H5File
        {
            ["demod"] = demodGroup,
            ["metadata"] = new H5Group
            {
                ["source_hdf5_path"] = Path.GetFullPath(inputHdf5Path),
                ["sample_rate_hz"] = settings.SampleRateHz,
                ["excitation_frequency_hz"] = settings.ExcitationFrequencyHz,
                ["adc_range"] = settings.AdRange.ToString(),
                ["adc_range_code"] = (int)settings.AdRange,
                ["adc_full_span_volts"] = settings.AdcFullSpanVolts,
                ["adc_lsb_volts"] = settings.AdcLsbVolts,
                ["channel_cycles"] = settings.ChannelCycles,
                ["windows_per_frame"] = settings.WindowsPerFrame,
                ["trim_samples"] = settings.TrimSamples,
                ["discard_mode"] = settings.DiscardMode.ToString(),
                ["discard_leading_cycles"] = settings.DiscardLeadingCycles,
                ["discard_trailing_cycles"] = settings.DiscardTrailingCycles,
                ["effective_discard_leading_samples"] = effectiveDiscard.LeadingSamples,
                ["effective_discard_trailing_samples"] = effectiveDiscard.TrailingSamples,
                ["effective_discard_leading_cycles"] = effectiveDiscard.LeadingCycles,
                ["effective_discard_trailing_cycles"] = effectiveDiscard.TrailingCycles,
                ["measurement_input_mode"] = "adjacent_pair_voltage",
                ["demod_reference_mode"] = EidorsAdjacentPhasorConvention.ReferenceMode,
                ["complex_component_semantics"] = EidorsAdjacentPhasorConvention.ComponentSemantics,
                ["stimulus_definition"] = EidorsAdjacentPhasorConvention.HardwareStimulationDefinition,
                ["eidors_target_stimulus_definition"] = EidorsAdjacentPhasorConvention.EidorsTargetStimulationDefinition,
                ["stimulus_pair_columns"] = EidorsAdjacentPhasorConvention.StimulusPairColumnOrder,
                ["demod_reference_endpoint_order"] = EidorsAdjacentPhasorConvention.ReferenceEndpointOrder,
                ["measurement_definition"] = EidorsAdjacentPhasorConvention.MeasurementDefinition,
                ["phasor_time_convention"] = EidorsAdjacentPhasorConvention.PhasorTimeConvention,
                ["current_reference_provenance"] = EidorsAdjacentPhasorConvention.CurrentReferenceProvenance,
                ["hardware_current_terminal_mapping"] = EidorsAdjacentPhasorConvention.HardwareCurrentTerminalMapping,
                ["signed_complex_eidors_readiness"] = EidorsAdjacentPhasorConvention.SignedComplexEidorsReadiness,
                ["injected_current_phase_measured"] = 0,
                ["signed_complex_eidors_ready"] = 1,
                ["demod_boundary_mode"] = result.BoundaryProvenance ??
                    (result.UsedUniformCadence ? "uniform_cadence_estimated" : "redpoint_or_override"),
                ["frame_boundary_dataset"] = result.Frames.Count > 0
                    ? "/demod/frame_boundaries_samples_Nx2"
                    : string.Empty,
                ["demod_uniform_offset_samples"] = result.UniformOffsetSamples,
                ["demod_estimated_window_samples"] = result.EstimatedWindowSamples,
                ["demod_estimated_frame_samples"] = result.EstimatedWindowSamples * settings.WindowsPerFrame,
                ["demod_average_mode"] = "quality_gated_strict_valid_frame_mean",
                ["demod_average_accept_rule"] = "all_16_windows_valid_top3_contiguous_top1_center_expected_ref_center",
                ["demod_output_tiers"] = "strict+trusted_partial+diagnostic",
                ["demod_trusted_partial_mode"] = "robust_component_median_of_non_rejected_windows",
                ["demod_diagnostic_mode"] = "robust_component_median_of_all_expected_cadence_windows",
                ["demod_projection_mode"] = settings.InterferenceFrequencyHz.Count > 0
                    ? "multi_frequency_least_squares_lockin"
                    : "single_frequency_quadrature_lockin",
                ["interference_frequency_hz"] = settings.InterferenceFrequencyHz.ToArray(),
                ["channel_definition"] = "CH1=V1-V2,...,CH16=V16-V1",
                ["amplitude_frame_layout"] = "adjacent_16_stim_x_13_meas_row_major_208_v1",
                ["frame_layout"] = "adjacent_16_stim_x_13_meas_row_major_208_v1",
                ["full_frame_layout"] = "adjacent_16_stim_x_16_meas_row_major_256_v1",
                ["full_frame_excluded_k_indices_zero_based"] = AdjacentAmplitudeFrameLayout.ExcludedKIndices,
                ["reciprocal_window_duration_ms"] = reciprocalWindowDurationMs,
                ["reciprocal_directed_window_offsets_by_k"] = AdjacentReciprocalTiming.DirectedWindowOffsetsByRelativeChannel,
                ["reciprocal_nearest_window_offsets_by_k"] = AdjacentReciprocalTiming.NearestWindowOffsetsByRelativeChannel,
                ["reciprocal_directed_delay_ms_by_k"] =
                    AdjacentReciprocalTiming.CreateDirectedDelayMsByRelativeChannel(reciprocalWindowDurationMs),
                ["reciprocal_nearest_delay_ms_by_k"] =
                    AdjacentReciprocalTiming.CreateNearestDelayMsByRelativeChannel(reciprocalWindowDurationMs),
                ["quality_matrix_columns"] = "window,expected_ref,top1,triplet_center,top3_1,top3_2,top3_3,top3_contiguous,top1_is_center,state,corrected,rejected,reject_reason",
                ["quality_metrics_columns"] = "window,peak_to_background_ratio,adc_saturation_count",
                ["electrode_index_base"] = 1
            }
        };

        file.Write(outputHdf5Path);
    }

    private static int[,] CreateFrameBoundaries(IReadOnlyList<DemodulatedFrame> frames)
    {
        var boundaries = new int[frames.Count, 2];
        for (var index = 0; index < frames.Count; index++)
        {
            boundaries[index, 0] = frames[index].StartSample;
            boundaries[index, 1] = frames[index].EndSample;
        }

        return boundaries;
    }

    private static void AddObservationAggregate(
        H5Group group,
        string prefix,
        DemodulatedObservationAggregate observation)
    {
        group[$"{prefix}_amp_16x13"] = observation.Amplitudes;
        group[$"{prefix}_real_16x13"] = observation.RealComponents;
        group[$"{prefix}_imag_16x13"] = observation.ImaginaryComponents;
        group[$"{prefix}_sample_counts_16x13"] = observation.SampleCounts;
        group[$"{prefix}_amp_16x16"] = observation.FullAmplitudes;
        group[$"{prefix}_real_16x16"] = observation.FullRealComponents;
        group[$"{prefix}_imag_16x16"] = observation.FullImaginaryComponents;
        group[$"{prefix}_sample_counts_16x16"] = observation.FullSampleCounts;
        group[$"{prefix}_amp_208"] = observation.FlattenAmplitudesRowMajor();
        group[$"{prefix}_real_208"] = observation.FlattenRealRowMajor();
        group[$"{prefix}_imag_208"] = observation.FlattenImaginaryRowMajor();
        group[$"{prefix}_amp_256"] = observation.FlattenFullAmplitudesRowMajor();
        group[$"{prefix}_real_256"] = observation.FlattenFullRealRowMajor();
        group[$"{prefix}_imag_256"] = observation.FlattenFullImaginaryRowMajor();
        group[$"{prefix}_contributing_frames"] = observation.ContributingFrameCount;
        group[$"{prefix}_contributing_windows"] = observation.ContributingWindowCount;
        group[$"{prefix}_total_windows"] = observation.TotalWindowCount;
        group[$"{prefix}_includes_rejected_windows"] = observation.IncludesRejectedWindows ? 1 : 0;
    }

    private static double[,] RequireFullMatrix(double[,]? matrix, string label)
    {
        if (matrix is null)
        {
            throw new InvalidOperationException($"Demodulation result does not contain {label} 16x16 data.");
        }

        if (matrix.GetLength(0) != DemodulatedFrame.StimulationCount ||
            matrix.GetLength(1) != DemodulatedFrame.FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException($"Demodulation result {label} data must be shaped [16, 16].");
        }

        return matrix;
    }

    private static int[,] RequireFullIntMatrix(int[,]? matrix, string label)
    {
        if (matrix is null)
        {
            throw new InvalidOperationException($"Demodulation result does not contain {label} 16x16 data.");
        }

        if (matrix.GetLength(0) != DemodulatedFrame.StimulationCount ||
            matrix.GetLength(1) != DemodulatedFrame.FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException($"Demodulation result {label} data must be shaped [16, 16].");
        }

        return matrix;
    }

    private static double[,,] StackFullFrameDoubles(
        IReadOnlyList<DemodulatedFrame> frames,
        Func<DemodulatedFrame, double[,]?> selector,
        string label)
    {
        var stack = new double[frames.Count, DemodulatedFrame.StimulationCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var matrix = selector(frames[frameIndex]) ?? throw new InvalidOperationException(
                $"Demodulated frame {frames[frameIndex].FrameNumber} does not contain {label} 16x16 data.");
            if (matrix.GetLength(0) != DemodulatedFrame.StimulationCount ||
                matrix.GetLength(1) != DemodulatedFrame.FullMeasurementsPerStimulation)
            {
                throw new InvalidOperationException(
                    $"Demodulated frame {frames[frameIndex].FrameNumber} {label} data must be shaped [16, 16].");
            }

            for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
            {
                for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
                {
                    stack[frameIndex, stimulation, relativeChannel] = matrix[stimulation, relativeChannel];
                }
            }
        }

        return stack;
    }

    private static int[,,] StackFullFrameInts(IReadOnlyList<DemodulatedFrame> frames)
    {
        var stack = new int[frames.Count, DemodulatedFrame.StimulationCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var matrix = frames[frameIndex].FullSaturationCounts ?? throw new InvalidOperationException(
                $"Demodulated frame {frames[frameIndex].FrameNumber} does not contain Sat256 data.");
            if (matrix.GetLength(0) != DemodulatedFrame.StimulationCount ||
                matrix.GetLength(1) != DemodulatedFrame.FullMeasurementsPerStimulation)
            {
                throw new InvalidOperationException(
                    $"Demodulated frame {frames[frameIndex].FrameNumber} Sat256 data must be shaped [16, 16].");
            }

            for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
            {
                for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
                {
                    stack[frameIndex, stimulation, relativeChannel] = matrix[stimulation, relativeChannel];
                }
            }
        }

        return stack;
    }
}
