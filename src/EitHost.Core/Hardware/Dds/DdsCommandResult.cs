namespace EitHost.Core.Hardware.Dds;

public sealed record DdsCommandResult(
    DdsCommand Command,
    IReadOnlyList<byte> PacketBytes,
    string PacketHex,
    DateTimeOffset SentAt,
    DdsResponseFrame? Response = null,
    DdsFirmwareCapabilities? FirmwareCapabilities = null,
    DdsExecutionReceipt? ExecutionReceipt = null);
