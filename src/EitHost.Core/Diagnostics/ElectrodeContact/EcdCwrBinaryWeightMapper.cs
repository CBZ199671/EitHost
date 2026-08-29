namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrBinaryWeightMapper
{
    private const int ElectrodeCount = 16;
    private const int RetainedMeasurementsPerStimulation = 13;

    public double[] Map(
        IReadOnlyList<ElectrodeContactState>? states,
        EcdCwrBinaryWeightMapperOptions? options = null)
    {
        options ??= new EcdCwrBinaryWeightMapperOptions();
        if (states is not { Count: ElectrodeCount })
        {
            return Enumerable.Repeat(1.0, ElectrodeCount * RetainedMeasurementsPerStimulation).ToArray();
        }

        var weights = new double[ElectrodeCount * RetainedMeasurementsPerStimulation];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var measurementChannel = Mod(stimulation + relativeChannel);
                weights[offset++] = new[]
                {
                    StateWeight(states[stimulation], options),
                    StateWeight(states[Mod(stimulation + 1)], options),
                    StateWeight(states[measurementChannel], options),
                    StateWeight(states[Mod(measurementChannel + 1)], options)
                }.Min();
            }
        }

        return weights;
    }

    public static string CreatePolicyVersion(EcdCwrBinaryWeightMapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"ecd-cwr-p1-binary-v2:yellow={options.YellowWeight:G4}:critical={options.CriticalWeight:G4}";
    }

    private static double StateWeight(
        ElectrodeContactState state,
        EcdCwrBinaryWeightMapperOptions options)
    {
        return state switch
        {
            ElectrodeContactState.SystemLevel or
            ElectrodeContactState.DarkRed or
            ElectrodeContactState.Red => Math.Clamp(options.CriticalWeight, 0.0, 1.0),
            ElectrodeContactState.Yellow => Math.Clamp(options.YellowWeight, 0.0, 1.0),
            _ => 1.0
        };
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrBinaryWeightMapperOptions(
    double YellowWeight = 0.5,
    double CriticalWeight = 0.02);
