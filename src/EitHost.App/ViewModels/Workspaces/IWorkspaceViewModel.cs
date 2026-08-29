using System.ComponentModel;
using EitHost.Core.Application;
using EitHost.Core.Application.Hardware;
using EitHost.Core.Application.Visualization;

namespace EitHost.App.ViewModels.Workspaces;

public interface IWorkspaceViewModel : INotifyPropertyChanged
{
    string ModuleId { get; }

    WorkspaceModuleSnapshot Snapshot { get; }
}

public interface IExperimentWorkspaceViewModel : IWorkspaceViewModel
{
}

public interface IHardwareWorkspaceViewModel : IWorkspaceViewModel
{
    HardwareWorkspaceSnapshot StateSnapshot { get; }

    event Action<HardwareWorkspaceSnapshot>? StateChanged;
}

public interface IRealtimeWorkspaceViewModel : IWorkspaceViewModel
{
}

public interface IVisualizationWorkspaceViewModel : IWorkspaceViewModel
{
    VisualizationWorkspaceSnapshot StateSnapshot { get; }

    event Action<VisualizationWorkspaceSnapshot>? StateChanged;
}
