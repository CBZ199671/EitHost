namespace EitHost.Core.Demodulation;

public static class AdjacentReciprocalTiming
{
    public const int ElectrodeCount = AdjacentAmplitudeFrameLayout.ElectrodeCount;

    public static readonly int[] DirectedWindowOffsetsByRelativeChannel = CreateDirectedWindowOffsets();

    public static readonly int[] NearestWindowOffsetsByRelativeChannel = CreateNearestWindowOffsets();

    public static ReciprocalObservation MapReciprocal(int stimulationIndex, int relativeChannelIndex)
    {
        ValidateStimulationIndex(stimulationIndex);
        ValidateRelativeChannelIndex(relativeChannelIndex);

        return new ReciprocalObservation(
            (stimulationIndex + relativeChannelIndex) % ElectrodeCount,
            (ElectrodeCount - relativeChannelIndex) % ElectrodeCount);
    }

    public static int GetDirectedWindowOffset(int relativeChannelIndex)
    {
        ValidateRelativeChannelIndex(relativeChannelIndex);
        return DirectedWindowOffsetsByRelativeChannel[relativeChannelIndex];
    }

    public static int GetNearestWindowOffset(int relativeChannelIndex)
    {
        ValidateRelativeChannelIndex(relativeChannelIndex);
        return NearestWindowOffsetsByRelativeChannel[relativeChannelIndex];
    }

    public static int GetSameFrameSignedWindowOffset(int stimulationIndex, int relativeChannelIndex)
    {
        ValidateStimulationIndex(stimulationIndex);
        ValidateRelativeChannelIndex(relativeChannelIndex);

        var reciprocal = MapReciprocal(stimulationIndex, relativeChannelIndex);
        return reciprocal.StimulationIndex - stimulationIndex;
    }

    public static int GetSameFrameAbsoluteWindowOffset(int stimulationIndex, int relativeChannelIndex)
    {
        return Math.Abs(GetSameFrameSignedWindowOffset(stimulationIndex, relativeChannelIndex));
    }

    public static double CalculateNominalWindowDurationMs(double excitationFrequencyHz, double channelCycles)
    {
        if (!double.IsFinite(excitationFrequencyHz) || excitationFrequencyHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(excitationFrequencyHz), "Excitation frequency must be positive.");
        }

        if (!double.IsFinite(channelCycles) || channelCycles <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCycles), "Channel cycles must be positive.");
        }

        return 1000.0 * channelCycles / excitationFrequencyHz;
    }

    public static double[] CreateDirectedDelayMsByRelativeChannel(double windowDurationMs)
    {
        ValidateWindowDuration(windowDurationMs);
        return DirectedWindowOffsetsByRelativeChannel
            .Select(offset => offset * windowDurationMs)
            .ToArray();
    }

    public static double[] CreateNearestDelayMsByRelativeChannel(double windowDurationMs)
    {
        ValidateWindowDuration(windowDurationMs);
        return NearestWindowOffsetsByRelativeChannel
            .Select(offset => offset * windowDurationMs)
            .ToArray();
    }

    public static ReciprocalDelay[] CreateDelayTable(double windowDurationMs)
    {
        ValidateWindowDuration(windowDurationMs);
        return Enumerable.Range(1, ElectrodeCount - 1)
            .Select(relativeChannel => new ReciprocalDelay(
                relativeChannel,
                MapReciprocal(0, relativeChannel).RelativeChannelIndex,
                GetDirectedWindowOffset(relativeChannel),
                GetNearestWindowOffset(relativeChannel),
                GetDirectedWindowOffset(relativeChannel) * windowDurationMs,
                GetNearestWindowOffset(relativeChannel) * windowDurationMs))
            .ToArray();
    }

    private static int[] CreateDirectedWindowOffsets()
    {
        var offsets = new int[ElectrodeCount];
        for (var relativeChannel = 0; relativeChannel < ElectrodeCount; relativeChannel++)
        {
            offsets[relativeChannel] = relativeChannel;
        }

        return offsets;
    }

    private static int[] CreateNearestWindowOffsets()
    {
        var offsets = new int[ElectrodeCount];
        for (var relativeChannel = 0; relativeChannel < ElectrodeCount; relativeChannel++)
        {
            offsets[relativeChannel] = Math.Min(relativeChannel, ElectrodeCount - relativeChannel);
        }

        return offsets;
    }

    private static void ValidateStimulationIndex(int stimulationIndex)
    {
        if (stimulationIndex < 0 || stimulationIndex >= ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(stimulationIndex), "Stimulation index must be within 0..15.");
        }
    }

    private static void ValidateRelativeChannelIndex(int relativeChannelIndex)
    {
        if (relativeChannelIndex < 0 || relativeChannelIndex >= ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeChannelIndex), "Relative channel index must be within 0..15.");
        }
    }

    private static void ValidateWindowDuration(double windowDurationMs)
    {
        if (!double.IsFinite(windowDurationMs) || windowDurationMs <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDurationMs), "Window duration must be positive.");
        }
    }
}

public sealed record ReciprocalObservation(int StimulationIndex, int RelativeChannelIndex);

public sealed record ReciprocalDelay(
    int RelativeChannelIndex,
    int ReciprocalRelativeChannelIndex,
    int DirectedWindowOffset,
    int NearestWindowOffset,
    double DirectedDelayMs,
    double NearestDelayMs);
