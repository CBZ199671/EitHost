namespace EitHost.Core.Demodulation;

public sealed record DemodulatedFrameAverage(
    IReadOnlyList<int> AcceptedFrameNumbers,
    IReadOnlyList<int> RejectedFrameNumbers,
    double[,] Amplitudes,
    double[,] RealComponents,
    double[,] ImaginaryComponents,
    int[,] SampleCounts,
    double[,]? FullAmplitudes = null,
    double[,]? FullRealComponents = null,
    double[,]? FullImaginaryComponents = null,
    int[,]? FullSampleCounts = null,
    IReadOnlyList<DemodulatedFrequencyFrame>? FrequencyFrames = null)
{
    public int AcceptedFrameCount => AcceptedFrameNumbers.Count;

    public int RejectedFrameCount => RejectedFrameNumbers.Count;

    public bool HasAcceptedFrames => AcceptedFrameCount > 0;

    public double[] FlattenAmplitudesRowMajor()
    {
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

    private static double[] FlattenRowMajor(double[,] values, string label)
    {
        if (values.GetLength(0) != DemodulatedFrame.StimulationCount ||
            values.GetLength(1) != DemodulatedFrame.MeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                $"Averaged demodulated {label} frame must be shaped [16, 13] before flattening to 208 points.");
        }

        var flattened = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            for (var measurement = 0; measurement < DemodulatedFrame.MeasurementsPerStimulation; measurement++)
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
                $"Averaged demodulated {label} frame does not contain the 16x16 full-observation side channel.");
        }

        if (values.GetLength(0) != DemodulatedFrame.StimulationCount ||
            values.GetLength(1) != DemodulatedFrame.FullMeasurementsPerStimulation)
        {
            throw new InvalidOperationException(
                $"Averaged demodulated {label} frame must be shaped [16, 16] before flattening to 256 points.");
        }

        var flattened = new double[DemodulatedFrame.FlattenedFullMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            for (var measurement = 0; measurement < DemodulatedFrame.FullMeasurementsPerStimulation; measurement++)
            {
                flattened[offset++] = values[stimulation, measurement];
            }
        }

        return flattened;
    }
}
