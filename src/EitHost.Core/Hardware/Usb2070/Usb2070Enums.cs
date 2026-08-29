namespace EitHost.Core.Hardware.Usb2070;

public enum Usb2070AdRange
{
    Bipolar5V = 0,
    Bipolar10V = 1,
    Bipolar2_5V = 2,
    Bipolar6_25V = 3,
    Bipolar12_5V = 4,
    Unipolar5V = 5,
    Unipolar10V = 6,
    Unipolar12_5V = 7
}

public enum Usb2070TriggerMode
{
    Continue = 0,
    Post = 1,
    Delay = 2,
    Pre = 3,
    Middle = 4
}

public enum Usb2070TriggerSource
{
    ExternalRising = 0,
    ExternalFalling = 1,
    Software = 2
}
