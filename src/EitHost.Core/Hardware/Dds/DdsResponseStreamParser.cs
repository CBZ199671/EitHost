namespace EitHost.Core.Hardware.Dds;

public sealed class DdsResponseStreamParser
{
    private readonly List<byte> buffer = [];

    public byte[]? Feed(byte value)
    {
        buffer.Add(value);
        while (buffer.Count > 0)
        {
            var headerIndex = buffer.IndexOf(DdsProtocolConstants.ResponseFrameHeader);
            if (headerIndex < 0)
            {
                buffer.Clear();
                return null;
            }

            if (headerIndex > 0)
            {
                buffer.RemoveRange(0, headerIndex);
            }

            if (buffer.Count >= 2 && (buffer[1] == 0 || buffer[1] > DdsProtocolConstants.ProtocolVersion))
            {
                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count >= 3 && !IsKnownCommand(buffer[2]))
            {
                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count >= 4 && buffer[3] > (byte)DdsResponseStatus.InternalError)
            {
                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count < 5)
            {
                return null;
            }

            var expectedLength = buffer[4] + DdsProtocolConstants.ResponseFrameOverhead;
            if (expectedLength > DdsProtocolConstants.MaximumResponsePayloadLength + DdsProtocolConstants.ResponseFrameOverhead)
            {
                buffer.RemoveAt(0);
                continue;
            }

            if (buffer.Count < expectedLength)
            {
                return null;
            }

            byte checksum = 0;
            for (var index = 0; index < expectedLength - 1; index++)
            {
                checksum ^= buffer[index];
            }

            if (checksum == buffer[expectedLength - 1])
            {
                var frame = buffer.Take(expectedLength).ToArray();
                buffer.RemoveRange(0, expectedLength);
                return frame;
            }

            buffer.RemoveAt(0);
        }

        return null;
    }

    private static bool IsKnownCommand(byte command) => command is
        (byte)DdsCommand.SetDac or
        (byte)DdsCommand.StopDac or
        (byte)DdsCommand.StartExcitation or
        (byte)DdsCommand.StopExcitation or
        (byte)DdsCommand.SetPga or
        (byte)DdsCommand.GetCapabilities or
        (byte)DdsCommand.GetScanStatus;
}
