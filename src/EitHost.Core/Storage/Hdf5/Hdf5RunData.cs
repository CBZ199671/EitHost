using EitHost.Core.Domain;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Storage.Hdf5;

public sealed record Hdf5RunData
{
    public Hdf5RunData(
        Guid sessionId,
        Guid runId,
        DateTimeOffset capturedAt,
        DeviceRunMetadata device,
        Hdf5ExcitationMetadata excitation,
        Usb2070AcquisitionMetadata acquisition,
        ushort[,] adcCounts)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(excitation);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(adcCounts);

        if (adcCounts.GetLength(0) == 0)
        {
            throw new ArgumentException("Raw ADC data must contain at least one sample row.", nameof(adcCounts));
        }

        if (adcCounts.GetLength(1) != EitSet.MeasurementChannelCount)
        {
            throw new ArgumentException("Raw ADC data must have exactly 16 channels.", nameof(adcCounts));
        }

        SessionId = sessionId;
        RunId = runId;
        CapturedAt = capturedAt;
        Device = device;
        Excitation = excitation;
        Acquisition = acquisition;
        AdcCounts = adcCounts;
    }

    public Guid SessionId { get; }

    public Guid RunId { get; }

    public DateTimeOffset CapturedAt { get; }

    public DeviceRunMetadata Device { get; }

    public Hdf5ExcitationMetadata Excitation { get; }

    public Usb2070AcquisitionMetadata Acquisition { get; }

    public ushort[,] AdcCounts { get; }
}
