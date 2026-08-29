namespace EitHost.Core.Demodulation;

public static class AdjacentAmplitudeFrameLayout
{
    public const int ElectrodeCount = 16;
    public const int FullMeasurementsPerStimulation = 16;
    public const int MeasurementsPerStimulation = 13;
    public const int FlattenedMeasurementCount = ElectrodeCount * MeasurementsPerStimulation;
    public const int FlattenedFullMeasurementCount = ElectrodeCount * FullMeasurementsPerStimulation;

    public static readonly int[] ExcludedKIndices = [0, 1, 15];

    public static int[,] CreateStimulusPairsOneBased()
    {
        var pairs = new int[ElectrodeCount, 2];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            pairs[stimulation, 0] = stimulation + 1;
            pairs[stimulation, 1] = ((stimulation + 1) % ElectrodeCount) + 1;
        }

        return pairs;
    }

    public static int[,] CreateMeasurementPairsOneBased()
    {
        var pairs = new int[FlattenedMeasurementCount, 2];
        var row = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var frameIndex16 = 2; frameIndex16 <= 14; frameIndex16++)
            {
                var start = (stimulation + frameIndex16) % ElectrodeCount;
                pairs[row, 0] = start + 1;
                pairs[row, 1] = ((start + 1) % ElectrodeCount) + 1;
                row++;
            }
        }

        return pairs;
    }

    public static int[,] CreateChannelMapOneBased()
    {
        var map = new int[FlattenedMeasurementCount, 4];
        var row = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            var stimFirst = stimulation + 1;
            var stimNext = ((stimulation + 1) % ElectrodeCount) + 1;
            for (var frameIndex16 = 2; frameIndex16 <= 14; frameIndex16++)
            {
                var measStart = (stimulation + frameIndex16) % ElectrodeCount;
                map[row, 0] = stimFirst;
                map[row, 1] = stimNext;
                map[row, 2] = measStart + 1;
                map[row, 3] = ((measStart + 1) % ElectrodeCount) + 1;
                row++;
            }
        }

        return map;
    }

    public static int[,] CreateFullChannelMapOneBased()
    {
        var map = new int[FlattenedFullMeasurementCount, 4];
        var row = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            var stimFirst = stimulation + 1;
            var stimNext = ((stimulation + 1) % ElectrodeCount) + 1;
            for (var relativeChannel = 0; relativeChannel < FullMeasurementsPerStimulation; relativeChannel++)
            {
                var measStart = (stimulation + relativeChannel) % ElectrodeCount;
                map[row, 0] = stimFirst;
                map[row, 1] = stimNext;
                map[row, 2] = measStart + 1;
                map[row, 3] = ((measStart + 1) % ElectrodeCount) + 1;
                row++;
            }
        }

        return map;
    }
}
