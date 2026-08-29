namespace EitHost.Core.Hardware.Usb2070;

public sealed class Usb2070NativeException : InvalidOperationException
{
    public Usb2070NativeException(Usb2070OperationError error)
        : base(error.Message)
    {
        Error = error;
    }

    public Usb2070OperationError Error { get; }
}
