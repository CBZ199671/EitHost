namespace EitHost.Core.Hardware.Dds;

public sealed record DdsDacSettings
{
    public static readonly IReadOnlyList<double> SupportedGains = [0.1, 0.2, 0.3, 0.5, 1.0];
    public static readonly IReadOnlyList<int> SupportedPhaseDegrees = [0, 45, 90, 180, 270];

    public DdsDacSettings(byte channel, int frequencyHz, double gain, int phaseDegrees)
    {
        if (channel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "DDS DAC channel is one-based.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegative(gain);

        if (!SupportedPhaseDegrees.Contains(phaseDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(phaseDegrees),
                phaseDegrees,
                "DDS phase must be one of: 0, 45, 90, 180, 270 degrees.");
        }

        Channel = channel;
        FrequencyHz = frequencyHz;
        FrequencyTuningWord = DdsFrequencyPlan.CalculateTuningWord(frequencyHz);
        ActualFrequencyHz = DdsFrequencyPlan.CalculateActualFrequencyHz(FrequencyTuningWord);
        Gain = gain;
        PhaseDegrees = phaseDegrees;
    }

    public byte Channel { get; }

    public int FrequencyHz { get; }

    public uint FrequencyTuningWord { get; }

    public double ActualFrequencyHz { get; }

    public double FrequencyErrorHz => ActualFrequencyHz - FrequencyHz;

    public double Gain { get; }

    public int PhaseDegrees { get; }

    public static bool IsSupportedGain(double gain)
    {
        return SupportedGains.Any(candidate => Math.Abs(candidate - gain) < 0.000_000_1);
    }
}
