namespace EitHost.Core.Application;

public abstract record WorkspaceEvent(
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    string SourceModule);

public sealed record WorkspaceStatusChanged(
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    WorkspaceModuleSnapshot Snapshot)
    : WorkspaceEvent(CorrelationId, OccurredAt, Snapshot.ModuleId);

public interface IWorkspaceEventSink
{
    ValueTask PublishAsync(WorkspaceEvent workspaceEvent, CancellationToken cancellationToken = default);
}

public sealed class NullWorkspaceEventSink : IWorkspaceEventSink
{
    public static NullWorkspaceEventSink Instance { get; } = new();

    private NullWorkspaceEventSink()
    {
    }

    public ValueTask PublishAsync(WorkspaceEvent workspaceEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
