using System.Buffers.Binary;

namespace EitHost.Core.Hardware.Dds;

public sealed record DdsFirmwareCapabilities(
    Version FirmwareVersion,
    ushort FeatureFlags,
    uint TimerClockHz,
    uint MinimumTimeUs,
    uint MaximumTimeUs,
    byte ScanSteps,
    ushort SwitchGuardMinimumUs)
{
    public bool SupportsScanStatus => (FeatureFlags & DdsProtocolConstants.ScanStatusFeatureFlag) != 0;

    public static DdsFirmwareCapabilities Parse(DdsResponseFrame response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Command != DdsCommand.GetCapabilities || response.Payload.Count != 20)
        {
            throw new DdsProtocolException("DDS capability response has an invalid command or payload length.");
        }

        var payload = response.Payload.ToArray().AsSpan();
        return new DdsFirmwareCapabilities(
            new Version(payload[0], payload[1], payload[2]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[3..5]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[5..9]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[9..13]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[13..17]),
            payload[17],
            BinaryPrimitives.ReadUInt16BigEndian(payload[18..20]));
    }

    public void ValidateRequiredV2Contract()
    {
        if ((FeatureFlags & DdsProtocolConstants.RequiredFeatureFlags) != DdsProtocolConstants.RequiredFeatureFlags ||
            TimerClockHz != DdsProtocolConstants.TimerClockHz ||
            MinimumTimeUs != DdsProtocolConstants.MinimumExcitationTimeUs ||
            MaximumTimeUs != DdsProtocolConstants.MaximumExcitationTimeUs ||
            ScanSteps != 16 ||
            SwitchGuardMinimumUs < 2)
        {
            throw new DdsProtocolException(
                $"DDS firmware {FirmwareVersion} capability contract is incompatible: " +
                $"features=0x{FeatureFlags:X4} timer={TimerClockHz}Hz range={MinimumTimeUs}-{MaximumTimeUs}us " +
                $"steps={ScanSteps} minimum_guard={SwitchGuardMinimumUs}us.");
        }
    }
}
