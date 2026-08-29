namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrContinuousWeightMapper
{
    private const int ElectrodeCount = 16;
    private const int RetainedMeasurementsPerStimulation = 13;
    private const int RetainedMeasurementCount = ElectrodeCount * RetainedMeasurementsPerStimulation;

    public double[] Map(
        EcdCwrFaultLocalizationResult localization,
        EcdCwrContinuousWeightMapperOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return Map(localization.ElectrodeScores, options);
    }

    public double[] Map(
        IReadOnlyList<double> electrodeScores,
        EcdCwrContinuousWeightMapperOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(electrodeScores);
        options ??= new EcdCwrContinuousWeightMapperOptions();
        if (electrodeScores.Count != ElectrodeCount)
        {
            throw new ArgumentException("Continuous ECD-CWR weights require 16 electrode scores.", nameof(electrodeScores));
        }

        var weights = new double[RetainedMeasurementCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var measurementChannel = Mod(stimulation + relativeChannel);
                var q = new[]
                {
                    Score(electrodeScores, stimulation),
                    Score(electrodeScores, Mod(stimulation + 1)),
                    Score(electrodeScores, measurementChannel),
                    Score(electrodeScores, Mod(measurementChannel + 1))
                }.Max();
                weights[offset++] = MapScore(q, options);
            }
        }

        return weights;
    }

    public static string CreatePolicyVersion(EcdCwrContinuousWeightMapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"ecd-cwr-p2-hill-v1:q0={options.Q0:G4}:p={options.Power:G4}:min={options.MinimumWeight:G4}";
    }

    private static double MapScore(double q, EcdCwrContinuousWeightMapperOptions options)
    {
        if (!double.IsFinite(q) || q <= 0.0)
        {
            return 1.0;
        }

        var q0 = Math.Max(options.Q0, 1e-12);
        var power = Math.Max(options.Power, 1e-12);
        var ratio = Math.Pow(q / q0, power);
        var weight = 1.0 / (1.0 + ratio);
        return Math.Clamp(weight, options.MinimumWeight, 1.0);
    }

    private static double Score(IReadOnlyList<double> scores, int electrode)
    {
        var value = scores[electrode];
        return double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrContinuousWeightMapperOptions(
    double Q0 = 2.0,
    double Power = 2.0,
    double MinimumWeight = 0.02);
