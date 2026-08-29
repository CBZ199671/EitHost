using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrWaveformShapeAnalyzer
{
    public EcdCwrWaveformShapeResult Analyze(
        DemodulatedFrame frame,
        EcdCwrHealthCalibration calibration,
        EcdCwrWaveformShapeAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibration);
        options ??= new EcdCwrWaveformShapeAnalyzerOptions();
        ValidateRetainedAmplitudeFrame(frame);

        var templates = calibration.WaveformTemplates.ToDictionary(
            template => template.StimulationIndex,
            template => template);
        var measurementScores = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var windowScores = new List<EcdCwrWaveformShapeWindowScore>(DemodulatedFrame.StimulationCount);
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            if (!templates.TryGetValue(stimulation, out var template) ||
                template.NormalizedMedianAmplitudes.Count != DemodulatedFrame.MeasurementsPerStimulation)
            {
                continue;
            }

            var observed = NormalizeRow(frame, stimulation, options);
            var expected = template.NormalizedMedianAmplitudes.Select(SanitizeFinite).ToArray();
            var residuals = observed.Zip(expected, (left, right) => left - right).ToArray();
            var correlationPenalty = Math.Max(0.0, 1.0 - PearsonCorrelation(observed, expected));
            var localSpike = CalculateLocalSpikeScore(residuals);
            var symmetryDeviation = CalculateResidualSymmetryDeviation(residuals);
            var score =
                (options.CorrelationWeight * correlationPenalty) +
                (options.LocalSpikeWeight * localSpike) +
                (options.SymmetryWeight * symmetryDeviation);

            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                var index = checked((stimulation * DemodulatedFrame.MeasurementsPerStimulation) + column);
                measurementScores[index] = Math.Max(Math.Abs(residuals[column]), score);
            }

            windowScores.Add(new EcdCwrWaveformShapeWindowScore(
                stimulation,
                correlationPenalty,
                localSpike,
                symmetryDeviation,
                score));
        }

        return new EcdCwrWaveformShapeResult(
            windowScores,
            measurementScores,
            windowScores.Count == 0 ? 0.0 : windowScores.Max(score => score.Score),
            Median(windowScores.Select(score => score.Score).ToArray()));
    }

    private static void ValidateRetainedAmplitudeFrame(DemodulatedFrame frame)
    {
        if (frame.Amplitudes.GetLength(0) != DemodulatedFrame.StimulationCount ||
            frame.Amplitudes.GetLength(1) != DemodulatedFrame.MeasurementsPerStimulation)
        {
            throw new ArgumentException("Waveform shape evidence requires a [16,13] retained amplitude frame.", nameof(frame));
        }
    }

    private static double[] NormalizeRow(
        DemodulatedFrame frame,
        int stimulation,
        EcdCwrWaveformShapeAnalyzerOptions options)
    {
        var row = Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
            .Select(column => SanitizeFinite(frame.Amplitudes[stimulation, column]))
            .ToArray();
        var normalization = Median(row.Where(value => value > 0.0).ToArray());
        normalization = Math.Max(options.NormalizationFloor, normalization);
        return row.Select(value => value / normalization).ToArray();
    }

    private static double CalculateLocalSpikeScore(IReadOnlyList<double> residuals)
    {
        if (residuals.Count < 3)
        {
            return 0.0;
        }

        var max = 0.0;
        for (var index = 1; index < residuals.Count - 1; index++)
        {
            var neighborMean = 0.5 * (residuals[index - 1] + residuals[index + 1]);
            max = Math.Max(max, Math.Abs(residuals[index] - neighborMean));
        }

        return max;
    }

    private static double CalculateResidualSymmetryDeviation(IReadOnlyList<double> residuals)
    {
        if (residuals.Count == 0)
        {
            return 0.0;
        }

        var pairCount = residuals.Count / 2;
        if (pairCount == 0)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var index = 0; index < pairCount; index++)
        {
            sum += Math.Abs(residuals[index] - residuals[residuals.Count - 1 - index]);
        }

        return sum / pairCount;
    }

    private static double PearsonCorrelation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var count = Math.Min(left.Count, right.Count);
        if (count < 2)
        {
            return 1.0;
        }

        var meanLeft = left.Take(count).Average();
        var meanRight = right.Take(count).Average();
        var numerator = 0.0;
        var sumLeft = 0.0;
        var sumRight = 0.0;
        for (var index = 0; index < count; index++)
        {
            var dl = left[index] - meanLeft;
            var dr = right[index] - meanRight;
            numerator += dl * dr;
            sumLeft += dl * dl;
            sumRight += dr * dr;
        }

        var denominator = Math.Sqrt(sumLeft * sumRight);
        return denominator <= double.Epsilon ? 1.0 : Math.Clamp(numerator / denominator, -1.0, 1.0);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return 0.0;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 1
            ? finite[middle]
            : (finite[middle - 1] + finite[middle]) / 2.0;
    }

    private static double SanitizeFinite(double value)
    {
        return double.IsFinite(value) ? value : 0.0;
    }
}

public sealed record EcdCwrWaveformShapeAnalyzerOptions(
    double NormalizationFloor = 1e-12,
    double CorrelationWeight = 1.0,
    double LocalSpikeWeight = 1.0,
    double SymmetryWeight = 0.5);

public sealed record EcdCwrWaveformShapeResult(
    IReadOnlyList<EcdCwrWaveformShapeWindowScore> WindowScores,
    double[] MeasurementScores208,
    double MaxScore,
    double MedianScore);

public sealed record EcdCwrWaveformShapeWindowScore(
    int StimulationIndex,
    double CorrelationPenalty,
    double LocalSpikeScore,
    double SymmetryDeviation,
    double Score);
