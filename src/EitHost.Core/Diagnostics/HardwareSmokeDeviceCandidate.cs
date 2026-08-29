using EitHost.Core.Hardware.Pnp;

namespace EitHost.Core.Diagnostics;

public sealed record HardwareSmokeDeviceCandidate(
    string Kind,
    string DeviceId,
    string DisplayName,
    string Vid,
    string Pid,
    string LocationPath,
    string? PortName,
    string? Status,
    int? ProblemCode,
    string? ProblemDescription)
{
    public static HardwareSmokeDeviceCandidate FromPnp(PnpDeviceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new HardwareSmokeDeviceCandidate(
            candidate.Kind.ToString(),
            candidate.DeviceId,
            candidate.DisplayName,
            candidate.Vid,
            candidate.Pid,
            candidate.LocationPath,
            candidate.PortName,
            candidate.Status,
            candidate.ProblemCode,
            candidate.ProblemDescription);
    }
}
