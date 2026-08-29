namespace EitHost.Core.Domain;

public sealed record EitSet
{
    public const int MeasurementChannelCount = 16;

    public EitSet(string label, Usb2070Device usb2070, DdsDevice dds)
    {
        Label = RequireText(label);
        Usb2070 = usb2070 ?? throw new ArgumentNullException(nameof(usb2070));
        Dds = dds ?? throw new ArgumentNullException(nameof(dds));
    }

    public string Label { get; }

    public Usb2070Device Usb2070 { get; }

    public DdsDevice Dds { get; }

    public DeviceRunMetadata CreateRunMetadata()
    {
        return new DeviceRunMetadata(
            Label,
            MeasurementChannelCount,
            Usb2070.DeviceNumber,
            Usb2070.DeviceId,
            Usb2070.DisplayName,
            Usb2070.Vid,
            Usb2070.Pid,
            Usb2070.LocationPath,
            Dds.PortName,
            Dds.DeviceId,
            Dds.DisplayName,
            Dds.Vid,
            Dds.Pid,
            Dds.LocationPath);
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
