using EitHost.Core.Application;

namespace EitHost.App.ViewModels.Workspaces;

public abstract class WorkspaceViewModelBase(string moduleId) : ObservableObject, IWorkspaceViewModel
{
    private WorkspaceModuleSnapshot snapshot = WorkspaceModuleSnapshot.Idle(moduleId);

    public string ModuleId => snapshot.ModuleId;

    public WorkspaceModuleSnapshot Snapshot
    {
        get => snapshot;
        protected set => SetProperty(ref snapshot, value);
    }

    protected void PublishStatus(string state, string status, DateTimeOffset updatedAt)
    {
        Snapshot = Snapshot.Next(state, status, updatedAt);
    }
}
