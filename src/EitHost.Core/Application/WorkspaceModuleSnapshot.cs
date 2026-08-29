namespace EitHost.Core.Application;

public sealed record WorkspaceModuleSnapshot(
    string ModuleId,
    long Revision,
    string State,
    string Status,
    DateTimeOffset UpdatedAt)
{
    public static WorkspaceModuleSnapshot Idle(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return new WorkspaceModuleSnapshot(moduleId, 0, "idle", string.Empty, DateTimeOffset.MinValue);
    }

    public WorkspaceModuleSnapshot Next(string state, string status, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(status);
        return this with
        {
            Revision = checked(Revision + 1),
            State = state,
            Status = status,
            UpdatedAt = updatedAt
        };
    }
}
