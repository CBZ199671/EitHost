namespace EitHost.Core.Application.Hardware;

public sealed record HardwareSetSnapshot(
    string SetLabel,
    int Usb2070DeviceNumber,
    bool IsExciting,
    bool IsAcquiring);

public sealed record HardwareWorkspaceSnapshot(
    IReadOnlyList<HardwareSetSnapshot> Sets,
    string? SelectedSetLabel,
    string? RealtimeDisplaySetLabel,
    int PendingUsb2070Count,
    int PendingDdsCount,
    long Revision)
{
    public static HardwareWorkspaceSnapshot Empty { get; } = new([], null, null, 0, 0, 0);
}
