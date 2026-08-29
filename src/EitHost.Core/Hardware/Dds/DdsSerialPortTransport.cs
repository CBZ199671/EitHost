using System.Diagnostics;
using System.IO.Ports;

namespace EitHost.Core.Hardware.Dds;

public sealed class DdsSerialPortTransport : IDdsSerialTransport, IDisposable
{
    private const int ReadSliceTimeoutMs = 50;
    private readonly object syncRoot = new();
    private readonly SerialPort serialPort;
    private readonly int responseTimeoutMs;
    private bool disposed;

    public DdsSerialPortTransport(DdsSerialPortOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        responseTimeoutMs = options.ResponseTimeoutMs;
        serialPort = new SerialPort(options.PortName, options.BaudRate, Parity.None, DdsProtocolConstants.DataBits, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = options.WriteTimeoutMs,
            ReadTimeout = Math.Min(ReadSliceTimeoutMs, options.ResponseTimeoutMs)
        };
    }

    public DdsSerialPortTransport(string portName)
        : this(new DdsSerialPortOptions(portName))
    {
    }

    public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (packet.IsEmpty)
        {
            throw new ArgumentException("DDS packet cannot be empty.", nameof(packet));
        }

        var bytes = packet.ToArray();
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (syncRoot)
                {
                    ThrowIfDisposed();
                    EnsureOpen();
                    serialPort.DiscardInBuffer();
                    serialPort.DiscardOutBuffer();
                    serialPort.Write(bytes, 0, bytes.Length);
                    return ReadResponseFrame(cancellationToken);
                }
            },
            cancellationToken);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            serialPort.Dispose();
            disposed = true;
        }
    }

    private byte[] ReadResponseFrame(CancellationToken cancellationToken)
    {
        var parser = new DdsResponseStreamParser();
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < responseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int value;
            try
            {
                value = serialPort.ReadByte();
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (parser.Feed(checked((byte)value)) is { } response)
            {
                return response;
            }
        }

        throw new TimeoutException(
            $"DDS firmware v2 response was not received within {responseTimeoutMs} ms; " +
            "flash the matching AD9106 firmware and reconnect the COM port.");
    }

    private void EnsureOpen()
    {
        if (!serialPort.IsOpen)
        {
            serialPort.Open();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
