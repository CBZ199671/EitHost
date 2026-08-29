namespace EitHost.Core.Hardware.Dds;

public interface IDdsSerialTransport
{
    Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
}
