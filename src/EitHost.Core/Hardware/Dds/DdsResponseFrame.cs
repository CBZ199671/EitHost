namespace EitHost.Core.Hardware.Dds;

public sealed record DdsResponseFrame(
    byte ProtocolVersion,
    DdsCommand Command,
    DdsResponseStatus Status,
    IReadOnlyList<byte> Payload,
    IReadOnlyList<byte> Bytes)
{
    public string Hex => BitConverter.ToString(Bytes.ToArray());

    public static DdsResponseFrame Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DdsProtocolConstants.ResponseFrameOverhead)
        {
            throw new DdsProtocolException($"DDS response is too short: {bytes.Length} bytes.");
        }

        if (bytes[0] != DdsProtocolConstants.ResponseFrameHeader)
        {
            throw new DdsProtocolException($"DDS response header 0x{bytes[0]:X2} is invalid.");
        }

        var payloadLength = bytes[4];
        var expectedLength = payloadLength + DdsProtocolConstants.ResponseFrameOverhead;
        if (bytes.Length != expectedLength)
        {
            throw new DdsProtocolException(
                $"DDS response length {bytes.Length} does not match payload length {payloadLength}.");
        }

        byte checksum = 0;
        foreach (var value in bytes[..^1])
        {
            checksum ^= value;
        }

        if (checksum != bytes[^1])
        {
            throw new DdsProtocolException(
                $"DDS response checksum 0x{bytes[^1]:X2} does not match 0x{checksum:X2}.");
        }

        return new DdsResponseFrame(
            bytes[1],
            (DdsCommand)bytes[2],
            (DdsResponseStatus)bytes[3],
            bytes.Slice(5, payloadLength).ToArray(),
            bytes.ToArray());
    }
}

