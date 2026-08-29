namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrMultiFrequencyConsistencyAnalyzer
{
    private const int ElectrodeCount = 16;

    public EcdCwrMultiFrequencyConsistencyResult Analyze(
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame> frames,
        EcdCwrMultiFrequencyConsistencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new EcdCwrMultiFrequencyConsistencyOptions();
        if (frames.Count < 2)
        {
            throw new ArgumentException("Multi-frequency consistency requires at least two frequency frames.", nameof(frames));
        }

        var ordered = frames.OrderBy(frame => frame.FrequencyHz).ToArray();
        foreach (var frame in ordered)
        {
            if (!double.IsFinite(frame.FrequencyHz) || frame.FrequencyHz <= 0.0)
            {
                throw new ArgumentException("Frequency evidence frames must use positive finite frequencies.", nameof(frames));
            }

            if (frame.ElectrodeMagnitudeScores.Count != ElectrodeCount)
            {
                throw new ArgumentException("Each frequency frame must contain 16 electrode magnitude scores.", nameof(frames));
            }

            if (frame.ElectrodePhaseScores is not null && frame.ElectrodePhaseScores.Count != ElectrodeCount)
            {
                throw new ArgumentException("Each frequency frame must contain 16 electrode phase scores.", nameof(frames));
            }
        }

        var consistency = new double[ElectrodeCount];
        var activeFractions = new double[ElectrodeCount];
        var logMagnitudeSlopes = new double[ElectrodeCount];
        var phaseSlopes = new double[ElectrodeCount];
        var contactMultipliers = new double[ElectrodeCount];
        var logFrequency = ordered.Select(frame => Math.Log(frame.FrequencyHz)).ToArray();
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var magnitudes = ordered
                .Select(frame => SanitizeMagnitude(frame.ElectrodeMagnitudeScores[electrode]))
                .ToArray();
            var mean = magnitudes.Average();
            var std = StandardDeviation(magnitudes, mean);
            consistency[electrode] = mean <= options.Epsilon ? 0.0 : mean / (mean + std);
            activeFractions[electrode] = magnitudes.Count(value => value >= options.ActiveMagnitudeThreshold) /
                (double)magnitudes.Length;
            logMagnitudeSlopes[electrode] = LinearSlope(
                logFrequency,
                magnitudes.Select(value => Math.Log(Math.Max(value, options.Epsilon))).ToArray());
            phaseSlopes[electrode] = PhaseSlope(ordered, electrode, logFrequency);

            var strength = mean / (mean + options.ActiveMagnitudeThreshold);
            var boost = options.MaxContactLikelihoodBoost *
                consistency[electrode] *
                activeFractions[electrode] *
                strength;
            contactMultipliers[electrode] = activeFractions[electrode] >= options.MinimumActiveFraction
                ? 1.0 + boost
                : 1.0;
        }

        return new EcdCwrMultiFrequencyConsistencyResult(
            ordered.Select(frame => frame.FrequencyHz).ToArray(),
            consistency,
            activeFractions,
            logMagnitudeSlopes,
            phaseSlopes,
            contactMultipliers);
    }

    private static double SanitizeMagnitude(double value)
    {
        return double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
    }

    private static double PhaseSlope(
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame> frames,
        int electrode,
        IReadOnlyList<double> logFrequency)
    {
        var phases = frames
            .Select(frame => frame.ElectrodePhaseScores is null ? double.NaN : frame.ElectrodePhaseScores[electrode])
            .Where(double.IsFinite)
            .ToArray();
        if (phases.Length != frames.Count)
        {
            return 0.0;
        }

        return LinearSlope(logFrequency, phases);
    }

    private static double StandardDeviation(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        var variance = values.Sum(value => (value - mean) * (value - mean)) / values.Count;
        return Math.Sqrt(variance);
    }

    private static double LinearSlope(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var xMean = x.Average();
        var yMean = y.Average();
        var numerator = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < x.Count; index++)
        {
            var dx = x[index] - xMean;
            numerator += dx * (y[index] - yMean);
            denominator += dx * dx;
        }

        return denominator <= double.Epsilon ? 0.0 : numerator / denominator;
    }
}

public sealed record EcdCwrFrequencyEvidenceFrame(
    double FrequencyHz,
    IReadOnlyList<double> ElectrodeMagnitudeScores,
    IReadOnlyList<double>? ElectrodePhaseScores = null);

public sealed record EcdCwrMultiFrequencyConsistencyOptions(
    double ActiveMagnitudeThreshold = 2.0,
    double MinimumActiveFraction = 0.5,
    double MaxContactLikelihoodBoost = 1.0,
    double Epsilon = 1.0e-9);

public sealed record EcdCwrMultiFrequencyConsistencyResult(
    double[] FrequenciesHz,
    double[] StructuralConsistency,
    double[] ActiveFrequencyFraction,
    double[] LogMagnitudeSlope,
    double[] PhaseSlope,
    double[] ContactLikelihoodMultiplier);
