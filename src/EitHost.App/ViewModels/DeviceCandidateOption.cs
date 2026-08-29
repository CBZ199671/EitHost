using EitHost.Core.Hardware.Pnp;

namespace EitHost.App.ViewModels;

public sealed class DeviceCandidateOption
{
    public DeviceCandidateOption(PnpDeviceCandidate candidate)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }

    public PnpDeviceCandidate Candidate { get; }

    public string Title => Candidate.Kind == PnpDeviceKind.SerialPort
        ? $"{Candidate.PortName}  {Candidate.DisplayName}"
        : Candidate.DisplayName;

    public string Details => $"{Candidate.Vid} / {Candidate.Pid} / {Candidate.LocationPath}";

    public string IdentityKey => Candidate.IdentityKey;
}
