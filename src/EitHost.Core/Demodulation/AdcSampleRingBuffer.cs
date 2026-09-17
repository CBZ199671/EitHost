namespace EitHost.Core.Demodulation;

// Single demodulation consumer owns this buffer. A block presented to the solver
// is still an independent matrix, so cadence refresh never sees overwritten data.
internal sealed class AdcSampleRingBuffer
{
    private const int Channels = 16;
    private ushort[] storage = [];
    private int head;
    public int Count { get; private set; }
    private int Capacity => storage.Length / Channels;
    public ushort this[int row, int channel] => storage[((head + row) % Capacity) * Channels + channel];

    public void Append(ushort[,] source)
    {
        var rows = source.GetLength(0);
        if (source.GetLength(1) != Channels) throw new ArgumentException("Expected 16 channels.", nameof(source));
        if (rows == 0) return;
        var required = checked(Count + rows);
        if (required > Capacity)
        {
            var next = new ushort[checked(Math.Max(required, Math.Max(256, Capacity * 2)) * Channels)];
            CopyRows(next, Count);
            storage = next;
            head = 0;
        }
        var tail = (head + Count) % Capacity;
        var first = Math.Min(rows, Capacity - tail);
        Buffer.BlockCopy(source, 0, storage, tail * Channels * sizeof(ushort), first * Channels * sizeof(ushort));
        if (first < rows)
            Buffer.BlockCopy(source, first * Channels * sizeof(ushort), storage, 0, (rows - first) * Channels * sizeof(ushort));
        Count = required;
    }

    public void Consume(int rows)
    {
        if (rows < 0 || rows > Count) throw new ArgumentOutOfRangeException(nameof(rows));
        if (rows == 0) return;
        head = (head + rows) % Capacity;
        Count -= rows;
    }

    public void Clear() { Count = 0; head = 0; }

    public ushort[,] Materialize(int rows)
    {
        rows = Math.Min(rows, Count);
        var result = new ushort[rows, Channels];
        CopyRows(result, rows);
        return result;
    }

    private void CopyRows(Array target, int rows)
    {
        if (rows == 0) return;
        var first = Math.Min(rows, Capacity - head);
        Buffer.BlockCopy(storage, head * Channels * sizeof(ushort), target, 0, first * Channels * sizeof(ushort));
        if (first < rows)
            Buffer.BlockCopy(storage, 0, target, first * Channels * sizeof(ushort), (rows - first) * Channels * sizeof(ushort));
    }
}
