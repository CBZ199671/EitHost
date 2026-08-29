namespace EitHost.Core.Hardware.Pnp;

public sealed record PnpDeviceSnapshot
{
    public PnpDeviceSnapshot(DateTimeOffset capturedAt, IEnumerable<PnpDeviceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        CapturedAt = capturedAt;
        Candidates = candidates
            .DistinctBy(candidate => candidate.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.PortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Usb2070Devices = Candidates.Where(candidate => candidate.Kind == PnpDeviceKind.Usb2070).ToArray();
        SerialDevices = Candidates.Where(candidate => candidate.Kind == PnpDeviceKind.SerialPort).ToArray();
    }

    public DateTimeOffset CapturedAt { get; }

    public IReadOnlyList<PnpDeviceCandidate> Candidates { get; }

    public IReadOnlyList<PnpDeviceCandidate> Usb2070Devices { get; }

    public IReadOnlyList<PnpDeviceCandidate> SerialDevices { get; }
}
