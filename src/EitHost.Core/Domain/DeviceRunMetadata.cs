namespace EitHost.Core.Domain;

public sealed record DeviceRunMetadata(
    string SetLabel,
    int MeasurementChannelCount,
    int UsbDeviceNumber,
    string UsbDeviceId,
    string UsbDisplayName,
    string UsbVid,
    string UsbPid,
    string UsbLocationPath,
    string DdsPortName,
    string DdsDeviceId,
    string DdsDisplayName,
    string DdsVid,
    string DdsPid,
    string DdsLocationPath);
