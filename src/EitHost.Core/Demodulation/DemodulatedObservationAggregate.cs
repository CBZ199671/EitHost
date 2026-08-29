namespace EitHost.Core.Demodulation;

public sealed record DemodulatedObservationAggregate
{
    public DemodulatedObservationAggregate(
        double[,] amplitudes,
        double[,] realComponents,
        double[,] imaginaryComponents,
        int[,] sampleCounts,
        double[,] fullAmplitudes,
        double[,] fullRealComponents,
        double[,] fullImaginaryComponents,
        int[,] fullSampleCounts,
        int contributingFrameCount,
        int contributingWindowCount,
        int totalWindowCount,
        bool includesRejectedWindows)
    {
        ValidateMatrix(amplitudes, DemodulatedFrame.MeasurementsPerStimulation, nameof(amplitudes));
        ValidateMatrix(realComponents, DemodulatedFrame.MeasurementsPerStimulation, nameof(realComponents));
        ValidateMatrix(imaginaryComponents, DemodulatedFrame.MeasurementsPerStimulation, nameof(imaginaryComponents));
        ValidateMatrix(sampleCounts, DemodulatedFrame.MeasurementsPerStimulation, nameof(sampleCounts));
        ValidateMatrix(fullAmplitudes, DemodulatedFrame.FullMeasurementsPerStimulation, nameof(fullAmplitudes));
        ValidateMatrix(fullRealComponents, DemodulatedFrame.FullMeasurementsPerStimulation, nameof(fullRealComponents));
        ValidateMatrix(fullImaginaryComponents, DemodulatedFrame.FullMeasurementsPerStimulation, nameof(fullImaginaryComponents));
        ValidateMatrix(fullSampleCounts, DemodulatedFrame.FullMeasurementsPerStimulation, nameof(fullSampleCounts));
        ArgumentOutOfRangeException.ThrowIfNegative(contributingFrameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(contributingWindowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalWindowCount);
        if (contributingWindowCount > totalWindowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contributingWindowCount),
                "Contributing window count cannot exceed total window count.");
        }

        Amplitudes = amplitudes;
        RealComponents = realComponents;
        ImaginaryComponents = imaginaryComponents;
        SampleCounts = sampleCounts;
        FullAmplitudes = fullAmplitudes;
        FullRealComponents = fullRealComponents;
        FullImaginaryComponents = fullImaginaryComponents;
        FullSampleCounts = fullSampleCounts;
        ContributingFrameCount = contributingFrameCount;
        ContributingWindowCount = contributingWindowCount;
        TotalWindowCount = totalWindowCount;
        IncludesRejectedWindows = includesRejectedWindows;
    }

    public double[,] Amplitudes { get; }

    public double[,] RealComponents { get; }

    public double[,] ImaginaryComponents { get; }

    public int[,] SampleCounts { get; }

    public double[,] FullAmplitudes { get; }

    public double[,] FullRealComponents { get; }

    public double[,] FullImaginaryComponents { get; }

    public int[,] FullSampleCounts { get; }

    public int ContributingFrameCount { get; }

    public int ContributingWindowCount { get; }

    public int TotalWindowCount { get; }

    public bool IncludesRejectedWindows { get; }

    public int FiniteMeasurementCount => CountFinite(Amplitudes, SampleCounts);

    public int FiniteFullMeasurementCount => CountFinite(FullAmplitudes, FullSampleCounts);

    public int FiniteStimulationCount => Enumerable.Range(0, DemodulatedFrame.StimulationCount)
        .Count(row => Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
            .All(column => SampleCounts[row, column] > 0 && double.IsFinite(Amplitudes[row, column])));

    public int MaximumSampleCount => FlattenSampleCountsRowMajor().DefaultIfEmpty(0).Max();

    public double[] FlattenAmplitudesRowMajor()
    {
        return Flatten(Amplitudes, DemodulatedFrame.MeasurementsPerStimulation);
    }

    public double[] FlattenRealRowMajor()
    {
        return Flatten(RealComponents, DemodulatedFrame.MeasurementsPerStimulation);
    }

    public double[] FlattenImaginaryRowMajor()
    {
        return Flatten(ImaginaryComponents, DemodulatedFrame.MeasurementsPerStimulation);
    }

    public int[] FlattenSampleCountsRowMajor()
    {
        return Flatten(SampleCounts, DemodulatedFrame.MeasurementsPerStimulation);
    }

    public double[] FlattenFullAmplitudesRowMajor()
    {
        return Flatten(FullAmplitudes, DemodulatedFrame.FullMeasurementsPerStimulation);
    }

    public double[] FlattenFullRealRowMajor()
    {
        return Flatten(FullRealComponents, DemodulatedFrame.FullMeasurementsPerStimulation);
    }

    public double[] FlattenFullImaginaryRowMajor()
    {
        return Flatten(FullImaginaryComponents, DemodulatedFrame.FullMeasurementsPerStimulation);
    }

    private static int CountFinite(double[,] values, int[,] counts)
    {
        var finite = 0;
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                if (counts[row, column] > 0 && double.IsFinite(values[row, column]))
                {
                    finite++;
                }
            }
        }

        return finite;
    }

    private static double[] Flatten(double[,] values, int columns)
    {
        var flattened = new double[DemodulatedFrame.StimulationCount * columns];
        var offset = 0;
        for (var row = 0; row < DemodulatedFrame.StimulationCount; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                flattened[offset++] = values[row, column];
            }
        }

        return flattened;
    }

    private static int[] Flatten(int[,] values, int columns)
    {
        var flattened = new int[DemodulatedFrame.StimulationCount * columns];
        var offset = 0;
        for (var row = 0; row < DemodulatedFrame.StimulationCount; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                flattened[offset++] = values[row, column];
            }
        }

        return flattened;
    }

    private static void ValidateMatrix(Array values, int expectedColumns, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Rank != 2 ||
            values.GetLength(0) != DemodulatedFrame.StimulationCount ||
            values.GetLength(1) != expectedColumns)
        {
            throw new ArgumentException(
                $"Demodulated observation matrix must be shaped [16, {expectedColumns}].",
                parameterName);
        }
    }
}
