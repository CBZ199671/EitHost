using EitHost.Core.Domain;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed record SingleSetAdCapture(
    Usb2070Device Device,
    Usb2070AcquisitionMetadata Metadata,
    ushort[,] AdcCounts,
    int RawValueCount);
