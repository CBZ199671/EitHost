namespace EitHost.Core.Sync;

public sealed record SyncStartResult(IReadOnlyList<SyncSetStartRecord> Records)
{
    public int SetCount => Records.Count;
}
