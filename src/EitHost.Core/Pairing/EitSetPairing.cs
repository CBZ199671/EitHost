using EitHost.Core.Hardware.Pnp;

namespace EitHost.Core.Pairing;

public sealed record EitSetPairing
{
    public EitSetPairing(
        string label,
        int usb2070DeviceNumber,
        PnpDeviceCandidate usb2070Candidate,
        PnpDeviceCandidate ddsSerialCandidate,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentOutOfRangeException.ThrowIfNegative(usb2070DeviceNumber);
        ArgumentNullException.ThrowIfNull(usb2070Candidate);
        ArgumentNullException.ThrowIfNull(ddsSerialCandidate);

        if (usb2070Candidate.Kind != PnpDeviceKind.Usb2070)
        {
            throw new ArgumentException("Pairing requires one USB2070 candidate.", nameof(usb2070Candidate));
        }

        if (ddsSerialCandidate.Kind != PnpDeviceKind.SerialPort)
        {
            throw new ArgumentException("Pairing requires one DDS serial candidate.", nameof(ddsSerialCandidate));
        }

        Label = label.Trim();
        Usb2070DeviceNumber = usb2070DeviceNumber;
        Usb2070Candidate = usb2070Candidate;
        DdsSerialCandidate = ddsSerialCandidate;
        CreatedAt = createdAt;
    }

    public string Label { get; }

    public int Usb2070DeviceNumber { get; }

    public PnpDeviceCandidate Usb2070Candidate { get; }

    public PnpDeviceCandidate DdsSerialCandidate { get; }

    public DateTimeOffset CreatedAt { get; }
}
