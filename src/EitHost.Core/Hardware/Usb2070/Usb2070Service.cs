using EitHost.Core.Domain;

namespace EitHost.Core.Hardware.Usb2070;

public sealed class Usb2070Service
{
    private readonly IUsb2070NativeApi nativeApi;

    public Usb2070Service(IUsb2070NativeApi nativeApi)
    {
        this.nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public IReadOnlyList<Usb2070Device> Scan(byte maxDeviceNumberExclusive = 16)
    {
        var devices = new List<Usb2070Device>();

        for (byte deviceNumber = 0; deviceNumber < maxDeviceNumberExclusive; deviceNumber++)
        {
            var handle = nativeApi.Link(deviceNumber);
            if (IsInvalidHandle(handle))
            {
                continue;
            }

            try
            {
                if (!nativeApi.GetDeviceInfo(handle, out var cardInfo))
                {
                    throw CreateException("GetDeviceInfo", "读取 USB2070 设备信息失败。", deviceNumber);
                }

                devices.Add(CreateDevice(deviceNumber, cardInfo));
            }
            finally
            {
                nativeApi.UnLink(handle);
            }
        }

        return devices;
    }

    public Usb2070Session Open(Usb2070Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var deviceNumber = checked((byte)device.DeviceNumber);
        var handle = nativeApi.Link(deviceNumber);
        if (IsInvalidHandle(handle))
        {
            throw CreateException("Link", $"打开 USB2070 #{device.DeviceNumber} 失败。", device.DeviceNumber);
        }

        try
        {
            if (!nativeApi.GetDeviceInfo(handle, out var cardInfo))
            {
                throw CreateException("GetDeviceInfo", $"读取 USB2070 #{device.DeviceNumber} 设备信息失败。", device.DeviceNumber);
            }

            return new Usb2070Session(nativeApi, handle, device, cardInfo);
        }
        catch
        {
            nativeApi.UnLink(handle);
            throw;
        }
    }

    private static Usb2070Device CreateDevice(int deviceNumber, Usb2070CardInfo cardInfo)
    {
        return new Usb2070Device(
            deviceNumber,
            $"USB2070:{deviceNumber}",
            $"FCFR-USB2070 #{deviceNumber}",
            Usb2070Constants.VendorId,
            Usb2070Constants.ProductId,
            $"USB2070:{deviceNumber}",
            cardInfo.AdChannelCount,
            cardInfo.AdBit,
            cardInfo.MaxSampleRateHz);
    }

    private static bool IsInvalidHandle(nint handle)
    {
        return handle == nint.Zero || handle == Usb2070Native.InvalidHandleValue;
    }

    private static Usb2070NativeException CreateException(string operation, string message, int? deviceNumber)
    {
        return new Usb2070NativeException(new Usb2070OperationError(operation, message, deviceNumber));
    }
}
