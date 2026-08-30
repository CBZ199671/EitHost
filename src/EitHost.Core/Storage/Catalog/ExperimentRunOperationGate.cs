namespace EitHost.Core.Storage.Catalog;

public enum ExperimentRunOperation
{
    OfflineCatchUp,
    Archive,
    Delete
}

public sealed class ExperimentRunOperationConflictException(
    Guid experimentRunId,
    ExperimentRunOperation activeOperation,
    ExperimentRunOperation requestedOperation)
    : InvalidOperationException(
        $"Experiment run {experimentRunId:D} is busy with {activeOperation}; " +
        $"cannot start {requestedOperation}.")
{
    public Guid ExperimentRunId { get; } = experimentRunId;

    public ExperimentRunOperation ActiveOperation { get; } = activeOperation;

    public ExperimentRunOperation RequestedOperation { get; } = requestedOperation;
}

public sealed class ExperimentRunOperationGate
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ActiveOperation> activeOperations = [];

    public IDisposable Enter(Guid experimentRunId, ExperimentRunOperation operation)
    {
        var token = Guid.NewGuid();
        lock (gate)
        {
            if (activeOperations.TryGetValue(experimentRunId, out var active))
            {
                throw new ExperimentRunOperationConflictException(
                    experimentRunId,
                    active.Operation,
                    operation);
            }

            activeOperations.Add(experimentRunId, new ActiveOperation(token, operation));
        }

        return new Lease(this, experimentRunId, token);
    }

    public bool IsActive(Guid experimentRunId)
    {
        lock (gate)
        {
            return activeOperations.ContainsKey(experimentRunId);
        }
    }

    private void Exit(Guid experimentRunId, Guid token)
    {
        lock (gate)
        {
            if (activeOperations.TryGetValue(experimentRunId, out var active) &&
                active.Token == token)
            {
                activeOperations.Remove(experimentRunId);
            }
        }
    }

    private sealed record ActiveOperation(Guid Token, ExperimentRunOperation Operation);

    private sealed class Lease(
        ExperimentRunOperationGate owner,
        Guid experimentRunId,
        Guid token) : IDisposable
    {
        private ExperimentRunOperationGate? owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Exit(experimentRunId, token);
        }
    }
}
