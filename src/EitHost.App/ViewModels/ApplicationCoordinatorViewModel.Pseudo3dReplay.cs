using EitHost.App.ViewModels.Workspaces;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    private Pseudo3dReplayViewModel? pseudo3dReplay;
    public Pseudo3dReplayViewModel Pseudo3dReplay => pseudo3dReplay ??= new(dataLayout, experimentCatalog, PostToUi);

    private void ApplyExperimentSelection(ExperimentRunListItem? experiment)
    {
        Pseudo3dReplay.SelectExperiment(experiment);
        ExperimentWorkspace.DataTools.ApplySelectedExperiment(experiment);
    }

    private async Task ReleaseExperimentReplayAsync(Guid runId)
    {
        if (pseudo3dReplay is { } groupReplay) await groupReplay.ReleaseExperimentAsync(runId);
        await replayController.ReleaseExperimentAsync(runId);
    }

    private void DisposePseudo3dWorkspaces()
    {
        pseudo3dVisualization.Dispose();
        pseudo3dReplay?.Dispose();
    }
}
