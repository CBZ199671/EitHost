namespace EitHost.Core.Hardware.Usb2070;

public interface IUsb2070NativeApi
{
    nint Link(byte deviceNumber);

    bool UnLink(nint deviceHandle);

    bool GetDeviceInfo(nint deviceHandle, out Usb2070CardInfo cardInfo);

    bool InitAd(nint deviceHandle, ref Usb2070AdParameters parameters);

    bool ReadAd(nint deviceHandle, ushort[] buffer, uint count);

    bool StopAd(nint deviceHandle, byte deviceNumber);

    bool GetBufferOverflow(nint deviceHandle, out int bufferOverflow);

    bool ExecuteSoftTrigger(nint deviceHandle);
}
