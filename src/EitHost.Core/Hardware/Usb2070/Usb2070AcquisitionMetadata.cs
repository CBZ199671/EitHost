namespace EitHost.Core.Hardware.Usb2070;

public sealed record Usb2070AcquisitionMetadata(
    int SampleRateHz,
    Usb2070AdRange Range,
    int AdBit,
    IReadOnlyList<int> EnabledOneBasedChannels,
    Usb2070TriggerMode TriggerMode,
    Usb2070TriggerSource TriggerSource);
