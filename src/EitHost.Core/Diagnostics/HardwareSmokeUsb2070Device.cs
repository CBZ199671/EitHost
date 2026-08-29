using EitHost.Core.Domain;

namespace EitHost.Core.Diagnostics;

public sealed record HardwareSmokeUsb2070Device(
    int DeviceNumber,
    int AvailableChannelCount,
    int AdBit,
    int MaxSampleRateHz)
{
    public static HardwareSmokeUsb2070Device FromDevice(Usb2070Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        return new HardwareSmokeUsb2070Device(
            device.DeviceNumber,
            device.AvailableChannelCount,
            device.AdBit,
            device.MaxSampleRateHz);
    }
}
