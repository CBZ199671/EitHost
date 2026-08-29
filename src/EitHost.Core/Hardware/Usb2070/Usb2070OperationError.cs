namespace EitHost.Core.Hardware.Usb2070;

public sealed record Usb2070OperationError(
    string Operation,
    string Message,
    int? DeviceNumber = null,
    int? LastWin32Error = null);
