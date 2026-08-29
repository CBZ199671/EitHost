namespace EitHost.Core.Diagnostics;

public sealed record HardwareSmokeReport(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<HardwareSmokeDeviceCandidate> PnpUsb2070Devices,
    IReadOnlyList<HardwareSmokeDeviceCandidate> PnpDdsSerialDevices,
    IReadOnlyList<string> OsSerialPorts,
    IReadOnlyList<HardwareSmokeUsb2070Device> Usb2070SdkDevices,
    Usb2070DriverPreflight DriverPreflight,
    HardwareSmokeReadiness Readiness,
    IReadOnlyList<string> Warnings)
{
    public HardwareSmokeMultiSetReadiness MultiSetReadiness =>
        HardwareSmokeMultiSetReadiness.Create(
            requiredSetCount: 2,
            PnpUsb2070Devices.Count,
            PnpDdsSerialDevices.Count,
            OsSerialPorts.Count,
            Usb2070SdkDevices.Count,
            Readiness.Blockers);

    public int EstimatedCompleteSetCount => Math.Min(
        Math.Min(PnpUsb2070Devices.Count, PnpDdsSerialDevices.Count),
        Math.Min(OsSerialPorts.Count, Usb2070SdkDevices.Count));
}
