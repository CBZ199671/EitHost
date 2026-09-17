using System.Text;

namespace EitHost.Core.Reconstruction;

internal sealed class WorkerBinaryProtocol(Stream stream)
{
    internal const string Transport = "memory_hdf5_v1";
    internal const int MaxPayloadBytes = 256 * 1024 * 1024;
    internal const int MaxHeaderBytes = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] buffer = new byte[8192];
    private int offset;
    private int available;

    internal async Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        using var header = new MemoryStream();
        while (true)
        {
            if (offset == available)
            {
                available = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (available == 0)
                {
                    if (header.Length == 0)
                        return null;
                    throw new EndOfStreamException("Truncated worker protocol header.");
                }
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', offset, available - offset);
            var end = newline < 0 ? available : newline;
            if (header.Length + end - offset > MaxHeaderBytes)
                throw new InvalidDataException("Worker protocol header exceeds byte limit.");
            header.Write(buffer, offset, end - offset);
            offset = newline < 0 ? end : end + 1;
            if (newline >= 0)
                return StrictUtf8.GetString(header.GetBuffer(), 0, checked((int)header.Length)).TrimEnd('\r');
        }
    }

    internal async Task<byte[]> ReadPayloadAsync(int size, CancellationToken cancellationToken = default)
    {
        ValidatePayloadSize(size);
        var result = new byte[size];
        var buffered = Math.Min(size, available - offset);
        buffer.AsSpan(offset, buffered).CopyTo(result);
        offset += buffered;
        await stream.ReadExactlyAsync(result.AsMemory(buffered), cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal static void ValidatePayloadSize(int size)
    {
        if (size <= 0 || size > MaxPayloadBytes)
            throw new InvalidDataException("Worker payload size must be positive and at most 256 MiB.");
    }
}

internal sealed class WorkerPayloadBuffer : MemoryStream
{
    private void CheckWrite(int count)
    {
        if (count > WorkerBinaryProtocol.MaxPayloadBytes - Position)
            throw new InvalidDataException("Worker payload exceeds 256 MiB.");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckWrite(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        CheckWrite(1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        if (value > WorkerBinaryProtocol.MaxPayloadBytes)
            throw new InvalidDataException("Worker payload exceeds 256 MiB.");
        base.SetLength(value);
    }
}
