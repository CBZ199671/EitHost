using System.Runtime.InteropServices;

namespace EitHost.Core.Hardware.Usb2070;

[StructLayout(LayoutKind.Sequential)]
public struct Usb2070CardInfo
{
    public int CardVersion;
    public int AdBit;
    public int AdChannelCount;
    public int AdSpeedKhz;
    public int AdFifoSamples;
    public int DaBit;
    public int DaChannelCount;
    public int DaSpeedKhz;
    public int DaFifoSamples;

    public readonly int MaxSampleRateHz => checked(AdSpeedKhz * 1000);
}
