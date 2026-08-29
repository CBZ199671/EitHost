using EitHost.Core.Storage.Frames;

namespace EitHost.App.ViewModels;

public sealed class ImagingRunListItem
{
    public ImagingRunListItem(ImagingRunSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    public ImagingRunSummary Summary { get; }

    public string Title => $"{Summary.SetLabel} · {Summary.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    public string DetailLine =>
        $"帧 {Summary.FrameCount} · 重构 {Summary.ReconCount} · raw 关联 {Summary.RawLinkCount} · {Summary.StorageMode} · {Summary.ReconstructionRoute}";

    public string StateLine => Summary.EndedAt is { } endedAt
        ? $"已结束 {endedAt.ToLocalTime():HH:mm:ss}"
        : "进行中 / 未正常结束";
}
