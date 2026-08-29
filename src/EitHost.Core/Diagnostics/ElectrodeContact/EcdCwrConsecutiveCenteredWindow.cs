namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrConsecutiveCenteredWindow<T>
{
    private readonly Queue<T> items = new();
    private int? lastBlockNumber;

    public int Count => items.Count;

    public IReadOnlyList<T>? Push(int blockNumber, T item)
    {
        if (lastBlockNumber is { } previous && blockNumber != previous + 1)
        {
            Reset();
        }

        lastBlockNumber = blockNumber;
        items.Enqueue(item);
        if (items.Count < EcdCwrCenteredTemporalDespiker.WindowSize)
        {
            return null;
        }

        var window = items.ToArray();
        items.Dequeue();
        return window;
    }

    public void Reset()
    {
        items.Clear();
        lastBlockNumber = null;
    }
}
