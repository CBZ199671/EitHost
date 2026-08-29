namespace EitHost.Core.Hardware.Usb2070;

public static class Usb2070VoltageScale
{
    public static double GetFullSpanVolts(Usb2070AdRange range)
    {
        return range switch
        {
            Usb2070AdRange.Bipolar5V => 10.0,
            Usb2070AdRange.Bipolar10V => 20.0,
            Usb2070AdRange.Bipolar2_5V => 5.0,
            Usb2070AdRange.Bipolar6_25V => 12.5,
            Usb2070AdRange.Bipolar12_5V => 25.0,
            Usb2070AdRange.Unipolar5V => 5.0,
            Usb2070AdRange.Unipolar10V => 10.0,
            Usb2070AdRange.Unipolar12_5V => 12.5,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unsupported USB2070 AD range.")
        };
    }

    public static double GetLsbVolts(Usb2070AdRange range) =>
        GetFullSpanVolts(range) / ushort.MaxValue;

    public static double ConvertCountToVoltage(ushort count, Usb2070AdRange range)
    {
        var fullSpanVolts = GetFullSpanVolts(range);
        var voltage = count * GetLsbVolts(range);
        return IsBipolar(range) ? voltage - (0.5 * fullSpanVolts) : voltage;
    }

    public static bool IsBipolar(Usb2070AdRange range) =>
        range is Usb2070AdRange.Bipolar5V or
            Usb2070AdRange.Bipolar10V or
            Usb2070AdRange.Bipolar2_5V or
            Usb2070AdRange.Bipolar6_25V or
            Usb2070AdRange.Bipolar12_5V;
}
