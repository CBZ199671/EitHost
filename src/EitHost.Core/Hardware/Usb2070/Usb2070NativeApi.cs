namespace EitHost.Core.Hardware.Usb2070;

public sealed class Usb2070NativeApi : IUsb2070NativeApi
{
    public nint Link(byte deviceNumber)
    {
        return Usb2070Native.Link(deviceNumber);
    }

    public bool UnLink(nint deviceHandle)
    {
        return Usb2070Native.UnLink(deviceHandle);
    }

    public bool GetDeviceInfo(nint deviceHandle, out Usb2070CardInfo cardInfo)
    {
        return Usb2070Native.GetDeviceInfo(deviceHandle, out cardInfo);
    }

    public bool InitAd(nint deviceHandle, ref Usb2070AdParameters parameters)
    {
        return Usb2070Native.InitAd(deviceHandle, ref parameters);
    }

    public bool ReadAd(nint deviceHandle, ushort[] buffer, uint count)
    {
        return Usb2070Native.ReadAd(deviceHandle, buffer, count);
    }

    public bool StopAd(nint deviceHandle, byte deviceNumber)
    {
        return Usb2070Native.StopAd(deviceHandle, deviceNumber);
    }

    public bool GetBufferOverflow(nint deviceHandle, out int bufferOverflow)
    {
        return Usb2070Native.GetBufferOverflow(deviceHandle, out bufferOverflow);
    }

    public bool ExecuteSoftTrigger(nint deviceHandle)
    {
        return Usb2070Native.ExecuteSoftTrigger(deviceHandle);
    }
}
