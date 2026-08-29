using System.Buffers.Binary;

namespace EitHost.Core.Hardware.Dds;

public sealed record DdsExecutionReceipt(
    byte FirmwareProtocolVersion,
    Version FirmwareVersion,
    uint RequestedTimeUs,
    ushort TimerTicks,
    uint TimerClockHz,
    uint EffectiveTimeNs,
    uint ScanTimes,
    DdsExcitationMode Mode,
    ushort SwitchGuardMinimumUs,
    ushort FirmwareFeatureFlags = 0)
{
    public double EffectiveTimeUs => EffectiveTimeNs / 1_000.0;

    public double CalculateEffectiveChannelCycles(double frequencyHz) =>
        EffectiveTimeNs * frequencyHz / 1_000_000_000.0;

    public static DdsExecutionReceipt Parse(
        DdsResponseFrame response,
        DdsFirmwareCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (response.Command != DdsCommand.StartExcitation || response.Payload.Count != 17)
        {
            throw new DdsProtocolException("DDS start response has an invalid command or payload length.");
        }

        var payload = response.Payload.ToArray().AsSpan();
        return new DdsExecutionReceipt(
            response.ProtocolVersion,
            capabilities.FirmwareVersion,
            BinaryPrimitives.ReadUInt32BigEndian(payload[0..4]),
            BinaryPrimitives.ReadUInt16BigEndian(payload[4..6]),
            capabilities.TimerClockHz,
            BinaryPrimitives.ReadUInt32BigEndian(payload[6..10]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[10..14]),
            (DdsExcitationMode)payload[14],
            BinaryPrimitives.ReadUInt16BigEndian(payload[15..17]),
            capabilities.FeatureFlags);
    }
}
