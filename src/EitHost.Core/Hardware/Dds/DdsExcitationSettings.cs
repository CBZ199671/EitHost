namespace EitHost.Core.Hardware.Dds;

public sealed record DdsExcitationSettings
{
    public DdsExcitationSettings(
        DdsExcitationMode mode,
        int frequencyHz,
        double channelCycles = DdsProtocolConstants.DefaultExcitationChannelCycles,
        int scanTimes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(scanTimes);
        if (!double.IsFinite(channelCycles))
        {
            throw new ArgumentOutOfRangeException(nameof(channelCycles), "Channel cycles must be finite.");
        }

        Mode = mode;
        FrequencyHz = frequencyHz;
        ChannelCycles = channelCycles;
        ScanTimes = scanTimes;
        _ = CalculateTimeUs();
    }

    public DdsExcitationMode Mode { get; }

    public int FrequencyHz { get; }

    public double ChannelCycles { get; }

    public int ScanTimes { get; }

    public int OverheadUs => 0;

    public uint CalculateTimeUs()
    {
        if (!TryCalculateTimeUs(FrequencyHz, ChannelCycles, out var roundedTimeUs))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChannelCycles),
                $"Excitation dwell {ChannelCycles * 1_000_000.0 / FrequencyHz:0.###}us is outside firmware range " +
                $"{DdsProtocolConstants.MinimumExcitationTimeUs}-{DdsProtocolConstants.MaximumExcitationTimeUs}us.");
        }

        return roundedTimeUs;
    }

    public static bool TryCalculateTimeUs(
        int frequencyHz,
        double channelCycles,
        out uint roundedTimeUs)
    {
        roundedTimeUs = 0;
        if (frequencyHz <= 0 || channelCycles <= 0 || !double.IsFinite(channelCycles))
        {
            return false;
        }

        var candidate = Math.Round(
            channelCycles * 1_000_000.0 / frequencyHz,
            MidpointRounding.AwayFromZero);
        if (!double.IsFinite(candidate) ||
            candidate < DdsProtocolConstants.MinimumExcitationTimeUs ||
            candidate > DdsProtocolConstants.MaximumExcitationTimeUs)
        {
            return false;
        }

        roundedTimeUs = (uint)candidate;
        return true;
    }
}
