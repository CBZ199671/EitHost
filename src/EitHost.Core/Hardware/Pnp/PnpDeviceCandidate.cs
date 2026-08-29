namespace EitHost.Core.Hardware.Pnp;

public sealed record PnpDeviceCandidate
{
    public PnpDeviceCandidate(
        PnpDeviceKind kind,
        string deviceId,
        string displayName,
        string vid,
        string pid,
        string locationPath,
        string? portName = null,
        string? status = null,
        int? problemCode = null,
        string? problemDescription = null)
    {
        Kind = kind;
        DeviceId = RequireText(deviceId).ToUpperInvariant();
        DisplayName = RequireText(displayName);
        Vid = RequireText(vid).ToUpperInvariant();
        Pid = RequireText(pid).ToUpperInvariant();
        LocationPath = RequireText(locationPath);
        PortName = string.IsNullOrWhiteSpace(portName) ? null : portName.Trim().ToUpperInvariant();
        Status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        ProblemCode = problemCode;
        ProblemDescription = string.IsNullOrWhiteSpace(problemDescription) ? null : problemDescription.Trim();

        if (Kind == PnpDeviceKind.SerialPort && PortName is null)
        {
            throw new ArgumentException("Serial candidates must include a COM port name.", nameof(portName));
        }
    }

    public PnpDeviceKind Kind { get; }

    public string DeviceId { get; }

    public string DisplayName { get; }

    public string Vid { get; }

    public string Pid { get; }

    public string LocationPath { get; }

    public string? PortName { get; }

    public string? Status { get; }

    public int? ProblemCode { get; }

    public string? ProblemDescription { get; }

    public string IdentityKey => $"{Kind}|{DeviceId}|{PortName ?? string.Empty}";

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
