namespace EitHost.App.ViewModels.Workspaces;

public sealed record ApplicationWorkspaces(
    ExperimentWorkspaceViewModel Experiment,
    HardwareWorkspaceViewModel Hardware,
    RealtimeWorkspaceViewModel Realtime,
    VisualizationWorkspaceViewModel Visualization);
