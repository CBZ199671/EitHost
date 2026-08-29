using EitHost.Core.Domain;

namespace EitHost.Core.Diagnostics;

public sealed record MultiSetSmokeSetPlan(
    SingleSetSmokePairing Pairing,
    Usb2070Device UsbDevice);
