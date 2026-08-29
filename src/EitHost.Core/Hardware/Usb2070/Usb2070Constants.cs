namespace EitHost.Core.Hardware.Usb2070;

public static class Usb2070Constants
{
    public const string NativeLibraryName = "USB2070.dll";
    public const string VendorId = "VID_1088";
    public const string ProductId = "PID_2070";
    public const int RequiredMeasurementChannelCount = 16;
    public const int StandardAdChannelCount = 48;
    public const int MaxParameterChannelFlagCount = 80;
    public const int TriggerUnitSamples = 8;
    public const uint ReadMaxLength = 1_572_864;
}
