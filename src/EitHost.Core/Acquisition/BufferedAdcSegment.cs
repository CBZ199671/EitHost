using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace EitHost.Core.Acquisition;

internal sealed class BufferedAdcSegment
{
    private readonly object gate = new();
    private ushort[]? rawValues;
    private byte[]? compressedBytes;
    private CompressedAdcPayloadKind compressedPayloadKind;
    private int compressionState;

    private BufferedAdcSegment(ushort[] rawValues)
    {
        this.rawValues = rawValues;
        ValueCount = rawValues.Length;
        RawByteCount = checked((long)rawValues.Length * sizeof(ushort));
    }

    private enum CompressedAdcPayloadKind
    {
        None,
        BytePlanesBrotli
    }

    public int ValueCount { get; }

    public long RawByteCount { get; }

    public long StoredByteCount
    {
        get
        {
            lock (gate)
            {
                return compressedBytes?.LongLength ?? checked((long)(rawValues?.Length ?? 0) * sizeof(ushort));
            }
        }
    }

    public bool IsCompressed
    {
        get
        {
            lock (gate)
            {
                return compressedBytes is not null;
            }
        }
    }

    public static BufferedAdcSegment FromBuffer(ushort[] buffer, int readCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readCount);
        if (readCount > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(readCount));
        }

        var raw = new ushort[readCount];
        Array.Copy(buffer, raw, readCount);
        return new BufferedAdcSegment(raw);
    }

    public bool TryBeginCompression()
    {
        lock (gate)
        {
            if (compressionState != 0 || rawValues is null)
            {
                return false;
            }

            compressionState = 1;
            return true;
        }
    }

    public long CompleteCompression()
    {
        ushort[] raw;
        lock (gate)
        {
            if (rawValues is null)
            {
                compressionState = 2;
                return 0;
            }

            raw = rawValues;
        }

        var rawByteCount = checked((int)RawByteCount);
        var transformed = ArrayPool<byte>.Shared.Rent(rawByteCount);
        try
        {
            var payload = transformed.AsSpan(0, rawByteCount);
            WriteBytePlanes(raw, payload);

            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                brotli.Write(payload);
            }

            var compressed = output.ToArray();
            if (compressed.LongLength >= RawByteCount)
            {
                MarkCompressionComplete();
                return 0;
            }

            lock (gate)
            {
                if (rawValues is null)
                {
                    compressionState = 2;
                    return 0;
                }

                var oldStoredBytes = checked((long)rawValues.Length * sizeof(ushort));
                rawValues = null;
                compressedBytes = compressed;
                compressedPayloadKind = CompressedAdcPayloadKind.BytePlanesBrotli;
                compressionState = 2;
                return compressed.LongLength - oldStoredBytes;
            }
        }
        catch
        {
            MarkCompressionComplete();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(transformed);
        }
    }

    public void CopyTo(ushort[] destination, int destinationOffset)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ushort[]? raw;
        byte[]? compressed;
        CompressedAdcPayloadKind payloadKind;
        lock (gate)
        {
            raw = rawValues;
            compressed = compressedBytes;
            payloadKind = compressedPayloadKind;
        }

        if (raw is not null)
        {
            Array.Copy(raw, 0, destination, destinationOffset, ValueCount);
            return;
        }

        if (compressed is null)
        {
            throw new InvalidOperationException("ADC segment has no raw or compressed payload.");
        }

        var rawByteCount = checked((int)RawByteCount);
        var bytes = ArrayPool<byte>.Shared.Rent(rawByteCount);
        try
        {
            var payload = bytes.AsSpan(0, rawByteCount);
            using var input = new MemoryStream(compressed);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            var offset = 0;
            while (offset < payload.Length)
            {
                var read = brotli.Read(payload[offset..]);
                if (read <= 0)
                {
                    throw new InvalidDataException("Compressed ADC segment ended before expected byte count.");
                }

                offset += read;
            }

            if (payloadKind == CompressedAdcPayloadKind.BytePlanesBrotli)
            {
                ReadBytePlanes(payload, destination.AsSpan(destinationOffset, ValueCount));
                return;
            }

            MemoryMarshal.Cast<byte, ushort>(payload).CopyTo(destination.AsSpan(destinationOffset, ValueCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static void WriteBytePlanes(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        var low = destination[..source.Length];
        var high = destination.Slice(source.Length, source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            low[index] = (byte)(value & 0xFF);
            high[index] = (byte)(value >> 8);
        }
    }

    private static void ReadBytePlanes(ReadOnlySpan<byte> source, Span<ushort> destination)
    {
        var low = source[..destination.Length];
        var high = source.Slice(destination.Length, destination.Length);
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (ushort)(low[index] | (high[index] << 8));
        }
    }

    private void MarkCompressionComplete()
    {
        lock (gate)
        {
            compressionState = 2;
        }
    }
}
