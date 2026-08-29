using System.Runtime.InteropServices;

namespace EitHost.Core.Hardware.Usb2070;

[StructLayout(LayoutKind.Sequential)]
public struct Usb2070AdParameters
{
    public int AdRange;
    public int SampleRateHz;
    public int TriggerMode;
    public int TriggerSource;
    public int TriggerDelay;
    public int TriggerLength;
    public int TriggerLevel;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Usb2070Constants.MaxParameterChannelFlagCount)]
    public int[] EnabledChannels;

    public static Usb2070AdParameters Create(
        Usb2070AdRange range,
        int sampleRateHz,
        Usb2070TriggerMode triggerMode,
        Usb2070TriggerSource triggerSource,
        int triggerDelay,
        int triggerLength,
        int triggerLevel,
        IEnumerable<int> enabledOneBasedChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerDelay);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerLength);
        ArgumentOutOfRangeException.ThrowIfNegative(triggerLevel);

        var enabledChannels = BuildEnabledChannels(enabledOneBasedChannels);

        return new Usb2070AdParameters
        {
            AdRange = (int)range,
            SampleRateHz = sampleRateHz,
            TriggerMode = (int)triggerMode,
            TriggerSource = (int)triggerSource,
            TriggerDelay = triggerDelay,
            TriggerLength = triggerLength,
            TriggerLevel = triggerLevel,
            EnabledChannels = enabledChannels
        };
    }

    private static int[] BuildEnabledChannels(IEnumerable<int> enabledOneBasedChannels)
    {
        ArgumentNullException.ThrowIfNull(enabledOneBasedChannels);

        var channels = new int[Usb2070Constants.MaxParameterChannelFlagCount];
        foreach (var channel in enabledOneBasedChannels.Distinct())
        {
            if (channel is < 1 or > Usb2070Constants.MaxParameterChannelFlagCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(enabledOneBasedChannels),
                    channel,
                    "USB2070 channel numbers are one-based and must fit SDK channel flags 1..80.");
            }

            channels[channel - 1] = 1;
        }

        return channels;
    }
}
