namespace EitHost.Core.Hardware.Dds;

public sealed class DdsPacket
{
    private readonly byte[] payload;
    private readonly byte[] bytes;

    public DdsPacket(DdsCommand command, IEnumerable<byte> payload, IEnumerable<byte> bytes)
    {
        Command = command;
        this.payload = payload?.ToArray() ?? throw new ArgumentNullException(nameof(payload));
        this.bytes = bytes?.ToArray() ?? throw new ArgumentNullException(nameof(bytes));
    }

    public DdsCommand Command { get; }

    public IReadOnlyList<byte> Payload => payload;

    public IReadOnlyList<byte> Bytes => bytes;

    public string Hex => BitConverter.ToString(bytes);

    public byte[] ToArray()
    {
        return bytes.ToArray();
    }
}
