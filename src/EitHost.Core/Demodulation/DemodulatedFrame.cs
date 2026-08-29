namespace EitHost.Core.Demodulation;

public sealed record DemodulatedFrame(
    int FrameNumber,
    int StartSample,
    int EndSample,
    double[,] Amplitudes,
    double[,] RealComponents,
    double[,] ImaginaryComponents,
    IReadOnlyList<DemodulatedWindowQuality> WindowQualities,
    int[,] StimulationWindowCounts,
    double[,]? FullAmplitudes = null,
    double[,]? FullRealComponents = null,
    double[,]? FullImaginaryComponents = null,
    int[,]? FullSaturationCounts = null,
    IReadOnlyList<DemodulatedFrequencyFrame>? FrequencyFrames = null,
    DemodulatedObservationAggregate? DiagnosticObservation = null)
{
    public const int StimulationCount = 16;
    public const int FullMeasurementsPerStimulation = 16;
    public const int MeasurementsPerStimulation = 13;
    public const int FlattenedMeasurementCount = StimulationCount * MeasurementsPerStimulation;
    public const int FlattenedFullMeasurementCount = StimulationCount * FullMeasurementsPerStimulation;

    public double[] FlattenAmplitudesRowMajor()
    {
        if (Amplitudes.GetLength(0) != StimulationCount || Amplitudes.GetLength(1) != MeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                "Demodulated amplitude frame must be shaped [16, 13] before flattening to 208 points.");
        }

        return FlattenRowMajor(Amplitudes, "amplitude");
    }

    public double[] FlattenRealRowMajor()
    {
        return FlattenRowMajor(RealComponents, "real");
    }

    public double[] FlattenImaginaryRowMajor()
    {
        return FlattenRowMajor(ImaginaryComponents, "imaginary");
    }

    public double[] FlattenFullAmplitudesRowMajor()
    {
        return FlattenFullRowMajor(FullAmplitudes, "full amplitude");
    }

    public double[] FlattenFullRealRowMajor()
    {
        return FlattenFullRowMajor(FullRealComponents, "full real");
    }

    public double[] FlattenFullImaginaryRowMajor()
    {
        return FlattenFullRowMajor(FullImaginaryComponents, "full imaginary");
    }

    public int[] FlattenFullSaturationCountsRowMajor()
    {
        if (FullSaturationCounts is null)
        {
            throw new InvalidOperationException(
                "Demodulated full saturation frame does not contain the 16x16 full-observation side channel.");
        }

        if (FullSaturationCounts.GetLength(0) != StimulationCount ||
            FullSaturationCounts.GetLength(1) != FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                "Demodulated full saturation frame must be shaped [16, 16] before flattening to 256 points.");
        }

        var flattened = new int[FlattenedFullMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < StimulationCount; stimulation++)
        {
            for (var measurement = 0; measurement < FullMeasurementsPerStimulation; measurement++)
            {
                flattened[offset++] = FullSaturationCounts[stimulation, measurement];
            }
        }

        return flattened;
    }

    public double[] SliceFullAmplitudesByRelativeChannel(int relativeChannel)
    {
        return SliceFullColumn(FullAmplitudes, relativeChannel, "full amplitude");
    }

    public double[] SliceFullRealByRelativeChannel(int relativeChannel)
    {
        return SliceFullColumn(FullRealComponents, relativeChannel, "full real");
    }

    public double[] SliceFullImaginaryByRelativeChannel(int relativeChannel)
    {
        return SliceFullColumn(FullImaginaryComponents, relativeChannel, "full imaginary");
    }

    public int[] SliceFullSaturationCountsByRelativeChannel(int relativeChannel)
    {
        if (FullSaturationCounts is null)
        {
            throw new InvalidOperationException(
                "Demodulated full saturation frame does not contain the 16x16 full-observation side channel.");
        }

        ValidateRelativeChannel(relativeChannel);
        if (FullSaturationCounts.GetLength(0) != StimulationCount ||
            FullSaturationCounts.GetLength(1) != FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                "Demodulated full saturation frame must be shaped [16, 16] before slicing.");
        }

        var values = new int[StimulationCount];
        for (var stimulation = 0; stimulation < StimulationCount; stimulation++)
        {
            values[stimulation] = FullSaturationCounts[stimulation, relativeChannel];
        }

        return values;
    }

    public int[] ReferenceChannelsOneBased()
    {
        return WindowQualities
            .Select(quality => quality.ExpectedReferenceChannel + 1)
            .ToArray();
    }

    public int[] RejectedWindowIndexesOneBased()
    {
        return WindowQualities
            .Where(quality => quality.Rejected)
            .Select(quality => quality.WindowIndex + 1)
            .ToArray();
    }

    public int[,] QualityMatrix()
    {
        var matrix = new int[WindowQualities.Count, 13];
        for (var row = 0; row < WindowQualities.Count; row++)
        {
            var quality = WindowQualities[row];
            matrix[row, 0] = quality.WindowIndex + 1;
            matrix[row, 1] = quality.ExpectedReferenceChannel + 1;
            matrix[row, 2] = quality.DetectedTop1Channel + 1;
            matrix[row, 3] = quality.TripletCenterChannel >= 0 ? quality.TripletCenterChannel + 1 : 0;
            matrix[row, 4] = quality.Top3Channels.Length > 0 ? quality.Top3Channels[0] + 1 : 0;
            matrix[row, 5] = quality.Top3Channels.Length > 1 ? quality.Top3Channels[1] + 1 : 0;
            matrix[row, 6] = quality.Top3Channels.Length > 2 ? quality.Top3Channels[2] + 1 : 0;
            matrix[row, 7] = quality.Top3Contiguous ? 1 : 0;
            matrix[row, 8] = quality.Top1IsTripletCenter ? 1 : 0;
            matrix[row, 9] = (int)quality.State;
            matrix[row, 10] = quality.Corrected ? 1 : 0;
            matrix[row, 11] = quality.Rejected ? 1 : 0;
            matrix[row, 12] = (int)quality.RejectReason;
        }

        return matrix;
    }

    public double[,] QualityMetricsMatrix()
    {
        var matrix = new double[WindowQualities.Count, 3];
        for (var row = 0; row < WindowQualities.Count; row++)
        {
            var quality = WindowQualities[row];
            matrix[row, 0] = quality.WindowIndex + 1;
            matrix[row, 1] = quality.PeakToBackgroundRatio;
            matrix[row, 2] = quality.AdcSaturationCount;
        }

        return matrix;
    }

    private static double[] FlattenRowMajor(double[,] values, string label)
    {
        if (values.GetLength(0) != StimulationCount || values.GetLength(1) != MeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                $"Demodulated {label} frame must be shaped [16, 13] before flattening to 208 points.");
        }

        var flattened = new double[FlattenedMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < StimulationCount; stimulation++)
        {
            for (var measurement = 0; measurement < MeasurementsPerStimulation; measurement++)
            {
                flattened[offset++] = values[stimulation, measurement];
            }
        }

        return flattened;
    }

    private static double[] FlattenFullRowMajor(double[,]? values, string label)
    {
        if (values is null)
        {
            throw new InvalidOperationException(
                $"Demodulated {label} frame does not contain the 16x16 full-observation side channel.");
        }

        if (values.GetLength(0) != StimulationCount || values.GetLength(1) != FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                $"Demodulated {label} frame must be shaped [16, 16] before flattening to 256 points.");
        }

        var flattened = new double[FlattenedFullMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < StimulationCount; stimulation++)
        {
            for (var measurement = 0; measurement < FullMeasurementsPerStimulation; measurement++)
            {
                flattened[offset++] = values[stimulation, measurement];
            }
        }

        return flattened;
    }

    private static double[] SliceFullColumn(double[,]? values, int relativeChannel, string label)
    {
        if (values is null)
        {
            throw new InvalidOperationException(
                $"Demodulated {label} frame does not contain the 16x16 full-observation side channel.");
        }

        ValidateRelativeChannel(relativeChannel);
        if (values.GetLength(0) != StimulationCount ||
            values.GetLength(1) != FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                $"Demodulated {label} frame must be shaped [16, 16] before slicing.");
        }

        var sliced = new double[StimulationCount];
        for (var stimulation = 0; stimulation < StimulationCount; stimulation++)
        {
            sliced[stimulation] = values[stimulation, relativeChannel];
        }

        return sliced;
    }

    private static void ValidateRelativeChannel(int relativeChannel)
    {
        if (relativeChannel < 0 || relativeChannel >= FullMeasurementsPerStimulation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativeChannel),
                "Full-observation relative channel index must be within 0..15.");
        }
    }
}

public sealed record DemodulatedFrequencyFrame(
    double FrequencyHz,
    double[,] FullAmplitudes,
    double[,] FullRealComponents,
    double[,] FullImaginaryComponents);
