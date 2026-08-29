using System.Buffers.Binary;

namespace EitHost.Core.Hardware.Dds;

public sealed class DdsPacketBuilder
{
    public DdsPacket BuildSetDac(DdsDacSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!DdsDacSettings.IsSupportedGain(settings.Gain))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.Gain,
                "DDS DAC gain must be one of the supported hardware current levels: 0.1, 0.2, 0.3, 0.5, 1.0.");
        }

        var gain = checked((ushort)Math.Round(settings.Gain * 10.0, MidpointRounding.AwayFromZero));
        Span<byte> payload = stackalloc byte[9];
        payload[0] = settings.Channel;
        BinaryPrimitives.WriteUInt32BigEndian(payload[1..5], settings.FrequencyTuningWord);
        BinaryPrimitives.WriteUInt16BigEndian(payload[5..7], checked((ushort)settings.PhaseDegrees));
        BinaryPrimitives.WriteUInt16BigEndian(payload[7..9], gain);

        return Build(DdsCommand.SetDac, payload);
    }

    public DdsPacket BuildStopDac(byte channel)
    {
        if (channel == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "DDS DAC channel is one-based.");
        }

        Span<byte> payload = stackalloc byte[] { channel };
        return Build(DdsCommand.StopDac, payload);
    }

    public DdsPacket BuildStartExcitation(DdsExcitationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Span<byte> payload = stackalloc byte[9];
        payload[0] = (byte)settings.Mode;
        BinaryPrimitives.WriteUInt32BigEndian(payload[1..5], settings.CalculateTimeUs());
        BinaryPrimitives.WriteInt32BigEndian(payload[5..9], settings.ScanTimes);

        return Build(DdsCommand.StartExcitation, payload);
    }

    public DdsPacket BuildStopExcitation()
    {
        return Build(DdsCommand.StopExcitation, []);
    }

    public DdsPacket BuildSetPga(byte gain)
    {
        if (gain is not (1 or 2 or 5 or 10))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "PGA gain must be one of: 1, 2, 5, 10.");
        }

        Span<byte> payload = stackalloc byte[] { gain };
        return Build(DdsCommand.SetPga, payload);
    }

    public DdsPacket BuildGetCapabilities()
    {
        return Build(DdsCommand.GetCapabilities, []);
    }

    public DdsPacket BuildGetScanStatus()
    {
        return Build(DdsCommand.GetScanStatus, []);
    }

    private static DdsPacket Build(DdsCommand command, ReadOnlySpan<byte> payload)
    {
        var bytes = new byte[payload.Length + 3];
        bytes[0] = DdsProtocolConstants.FrameHeader;
        bytes[1] = (byte)command;
        payload.CopyTo(bytes.AsSpan(2));
        bytes[^1] = CalculateXor(bytes.AsSpan(0, bytes.Length - 1));

        return new DdsPacket(command, payload.ToArray(), bytes);
    }

    private static byte CalculateXor(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0;
        foreach (var value in bytes)
        {
            checksum ^= value;
        }

        return checksum;
    }
}
