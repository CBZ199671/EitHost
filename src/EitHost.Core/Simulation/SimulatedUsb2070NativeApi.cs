using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Simulation;

public sealed class SimulatedUsb2070NativeApi : IUsb2070NativeApi
{
    private readonly Dictionary<nint, byte> openHandles = [];
    private readonly int deviceCount;
    private int nextHandle = 1000;
    private uint sampleCursor;

    public SimulatedUsb2070NativeApi(int deviceCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(deviceCount);
        this.deviceCount = deviceCount;
    }

    public nint Link(byte deviceNumber)
    {
        if (deviceNumber >= deviceCount)
        {
            return Usb2070Native.InvalidHandleValue;
        }

        var handle = (nint)nextHandle++;
        openHandles[handle] = deviceNumber;
        return handle;
    }

    public bool UnLink(nint deviceHandle)
    {
        return openHandles.Remove(deviceHandle);
    }

    public bool GetDeviceInfo(nint deviceHandle, out Usb2070CardInfo cardInfo)
    {
        if (!openHandles.ContainsKey(deviceHandle))
        {
            cardInfo = default;
            return false;
        }

        cardInfo = new Usb2070CardInfo
        {
            CardVersion = 1,
            AdBit = 16,
            AdChannelCount = Usb2070Constants.StandardAdChannelCount,
            AdSpeedKhz = 200,
            AdFifoSamples = 8192,
            DaBit = 12,
            DaChannelCount = 2,
            DaSpeedKhz = 100,
            DaFifoSamples = 1024
        };
        return true;
    }

    public bool InitAd(nint deviceHandle, ref Usb2070AdParameters parameters)
    {
        return openHandles.ContainsKey(deviceHandle)
            && parameters.EnabledChannels.Count(value => value == 1) == Usb2070Constants.RequiredMeasurementChannelCount;
    }

    public bool ReadAd(nint deviceHandle, ushort[] buffer, uint count)
    {
        if (!openHandles.ContainsKey(deviceHandle) || count > buffer.Length)
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            var channel = index % Usb2070Constants.RequiredMeasurementChannelCount;
            var value = 32768.0 + (1000.0 * Math.Sin((sampleCursor + index) * 0.05)) + (channel * 20);
            buffer[index] = checked((ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue));
        }

        sampleCursor += count;
        return true;
    }

    public bool StopAd(nint deviceHandle, byte deviceNumber)
    {
        return openHandles.TryGetValue(deviceHandle, out var linkedDeviceNumber)
            && linkedDeviceNumber == deviceNumber;
    }

    public bool GetBufferOverflow(nint deviceHandle, out int bufferOverflow)
    {
        bufferOverflow = 0;
        return openHandles.ContainsKey(deviceHandle);
    }

    public bool ExecuteSoftTrigger(nint deviceHandle)
    {
        return openHandles.ContainsKey(deviceHandle);
    }
}
