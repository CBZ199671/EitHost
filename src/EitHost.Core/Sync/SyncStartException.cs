namespace EitHost.Core.Sync;

public sealed class SyncStartException : Exception
{
    public SyncStartException(string message, SyncStartResult partialResult, Exception innerException)
        : base(message, innerException)
    {
        PartialResult = partialResult;
    }

    public SyncStartResult PartialResult { get; }
}
