namespace EitHost.Core.Hardware.Pnp;

public sealed record PnpDeviceChange
{
    public PnpDeviceChange(
        PnpDeviceSnapshot previous,
        PnpDeviceSnapshot current,
        IEnumerable<PnpDeviceCandidate> added,
        IEnumerable<PnpDeviceCandidate> removed)
    {
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Added = added?.ToArray() ?? throw new ArgumentNullException(nameof(added));
        Removed = removed?.ToArray() ?? throw new ArgumentNullException(nameof(removed));
    }

    public PnpDeviceSnapshot Previous { get; }

    public PnpDeviceSnapshot Current { get; }

    public IReadOnlyList<PnpDeviceCandidate> Added { get; }

    public IReadOnlyList<PnpDeviceCandidate> Removed { get; }

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;

    public IReadOnlyList<PnpDeviceCandidate> AddedUsb2070Devices =>
        Added.Where(candidate => candidate.Kind == PnpDeviceKind.Usb2070).ToArray();

    public IReadOnlyList<PnpDeviceCandidate> AddedSerialDevices =>
        Added.Where(candidate => candidate.Kind == PnpDeviceKind.SerialPort).ToArray();

    public static PnpDeviceChange FromSnapshots(PnpDeviceSnapshot previous, PnpDeviceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousKeys = previous.Candidates
            .Select(candidate => candidate.IdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentKeys = current.Candidates
            .Select(candidate => candidate.IdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = current.Candidates.Where(candidate => !previousKeys.Contains(candidate.IdentityKey)).ToArray();
        var removed = previous.Candidates.Where(candidate => !currentKeys.Contains(candidate.IdentityKey)).ToArray();

        return new PnpDeviceChange(previous, current, added, removed);
    }
}
