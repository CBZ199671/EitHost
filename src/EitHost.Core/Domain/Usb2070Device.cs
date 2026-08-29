namespace EitHost.Core.Domain;

public sealed record Usb2070Device
{
    public Usb2070Device(
        int deviceNumber,
        string deviceId,
        string displayName,
        string vid,
        string pid,
        string locationPath,
        int availableChannelCount,
        int adBit,
        int maxSampleRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableChannelCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(adBit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSampleRateHz);

        DeviceNumber = deviceNumber;
        DeviceId = RequireText(deviceId);
        DisplayName = RequireText(displayName);
        Vid = RequireText(vid);
        Pid = RequireText(pid);
        LocationPath = RequireText(locationPath);
        AvailableChannelCount = availableChannelCount;
        AdBit = adBit;
        MaxSampleRateHz = maxSampleRateHz;
    }

    public int DeviceNumber { get; }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public string Vid { get; }

    public string Pid { get; }

    public string LocationPath { get; }

    public int AvailableChannelCount { get; }

    public int AdBit { get; }

    public int MaxSampleRateHz { get; }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
