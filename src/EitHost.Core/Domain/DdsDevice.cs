namespace EitHost.Core.Domain;

public sealed record DdsDevice
{
    public DdsDevice(
        string portName,
        string deviceId,
        string displayName,
        string vid,
        string pid,
        string locationPath)
    {
        PortName = RequireText(portName).ToUpperInvariant();
        DeviceId = RequireText(deviceId);
        DisplayName = RequireText(displayName);
        Vid = RequireText(vid);
        Pid = RequireText(pid);
        LocationPath = RequireText(locationPath);
    }

    public string PortName { get; }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public string Vid { get; }

    public string Pid { get; }

    public string LocationPath { get; }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
