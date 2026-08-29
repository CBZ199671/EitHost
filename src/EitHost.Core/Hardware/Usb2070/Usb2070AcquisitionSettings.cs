namespace EitHost.Core.Hardware.Usb2070;

public sealed record Usb2070AcquisitionSettings
{
    public Usb2070AcquisitionSettings(
        int sampleRateHz,
        Usb2070AdRange range,
        Usb2070TriggerMode triggerMode,
        Usb2070TriggerSource triggerSource,
        int triggerDelay,
        int triggerLength,
        int triggerLevel,
        IEnumerable<int>? enabledOneBasedChannels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerLength);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerLevel);

        SampleRateHz = sampleRateHz;
        Range = range;
        TriggerMode = triggerMode;
        TriggerSource = triggerSource;
        TriggerDelay = triggerDelay;
        TriggerLength = triggerLength;
        TriggerLevel = triggerLevel;
        EnabledOneBasedChannels = NormalizeChannels(enabledOneBasedChannels);
    }

    public int SampleRateHz { get; }

    public Usb2070AdRange Range { get; }

    public Usb2070TriggerMode TriggerMode { get; }

    public Usb2070TriggerSource TriggerSource { get; }

    public Usb2070TriggerSource EffectiveTriggerSource =>
        TriggerMode == Usb2070TriggerMode.Continue
            ? Usb2070TriggerSource.ExternalRising
            : TriggerSource;

    public int TriggerDelay { get; }

    public int TriggerLength { get; }

    public int TriggerLevel { get; }

    public IReadOnlyList<int> EnabledOneBasedChannels { get; }

    public Usb2070AdParameters ToNativeParameters()
    {
        return Usb2070AdParameters.Create(
            Range,
            SampleRateHz,
            TriggerMode,
            EffectiveTriggerSource,
            TriggerDelay,
            TriggerLength,
            TriggerLevel,
            EnabledOneBasedChannels);
    }

    private static IReadOnlyList<int> NormalizeChannels(IEnumerable<int>? enabledOneBasedChannels)
    {
        var channels = (enabledOneBasedChannels ?? Enumerable.Range(1, Usb2070Constants.RequiredMeasurementChannelCount))
            .Distinct()
            .Order()
            .ToArray();

        if (channels.Length != Usb2070Constants.RequiredMeasurementChannelCount)
        {
            throw new ArgumentException("EIT acquisition requires exactly 16 enabled channels.", nameof(enabledOneBasedChannels));
        }

        if (channels.Any(channel => channel is < 1 or > Usb2070Constants.MaxParameterChannelFlagCount))
        {
            throw new ArgumentOutOfRangeException(nameof(enabledOneBasedChannels), "USB2070 channel numbers must fit SDK channel flags 1..80.");
        }

        return channels;
    }
}
