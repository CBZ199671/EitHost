namespace EitHost.Core.Hardware.Dds;

public sealed record DdsSerialPortOptions
{
    public DdsSerialPortOptions(
        string portName,
        int baudRate = DdsProtocolConstants.BaudRate,
        int writeTimeoutMs = 1000,
        int responseTimeoutMs = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writeTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(responseTimeoutMs);

        PortName = portName.Trim().ToUpperInvariant();
        BaudRate = baudRate;
        WriteTimeoutMs = writeTimeoutMs;
        ResponseTimeoutMs = responseTimeoutMs;
    }

    public string PortName { get; }

    public int BaudRate { get; }

    public int WriteTimeoutMs { get; }

    public int ResponseTimeoutMs { get; }
}
