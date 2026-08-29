namespace EitHost.Core.Application.Visualization;

public sealed record VisualizationWorkspaceSnapshot(
    Guid? SelectedRunId,
    int FrameIndex,
    int FrameCount,
    bool IsPlaying,
    bool HasImage,
    string ReplaySummary,
    string RoiSummary,
    long Revision)
{
    public static VisualizationWorkspaceSnapshot Empty { get; } = new(
        null,
        0,
        0,
        false,
        false,
        string.Empty,
        "ROI：选择成像记录后可离线计算。",
        0);
}
