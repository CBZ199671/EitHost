using System.IO;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels;

/// <summary>
/// One database-page row representing a complete experiment, or an explicitly
/// labelled legacy raw/replay record that cannot be safely joined.
/// </summary>
public sealed class ExperimentRunListItem
{
    private ExperimentRunListItem(
        string key,
        Guid experimentRunId,
        string setLabel,
        DateTimeOffset startedAt,
        ExperimentRunRecord? run,
        ExperimentCoverageSummary coverage,
        string? primaryRawHdf5Path,
        string locationPath,
        CatalogRunSummaryItem? legacyCatalogRun,
        ImagingRunListItem? imagingRun,
        string sourceKind)
    {
        Key = key;
        ExperimentRunId = experimentRunId;
        SetLabel = setLabel;
        StartedAt = startedAt;
        Run = run;
        Coverage = coverage;
        PrimaryRawHdf5Path = primaryRawHdf5Path;
        LocationPath = locationPath;
        LegacyCatalogRun = legacyCatalogRun;
        ImagingRun = imagingRun;
        SourceKind = sourceKind;
    }

    public string Key { get; }

    public Guid ExperimentRunId { get; }

    public string SetLabel { get; }

    public DateTimeOffset StartedAt { get; }

    public ExperimentRunRecord? Run { get; }

    public ExperimentCoverageSummary Coverage { get; }

    public string? PrimaryRawHdf5Path { get; }

    public string LocationPath { get; }

    public CatalogRunSummaryItem? LegacyCatalogRun { get; }

    public ImagingRunListItem? ImagingRun { get; }

    public string SourceKind { get; }

    public bool IsLegacy => Run is null;

    public bool IsCanonicalTerminal => Run is { } run && IsTerminalStatus(run.Status);

    public bool CanReplay => Run is not null
        ? IsCanonicalTerminal && Coverage.DemodReadyCount > 0
        : ImagingRun is not null;

    internal static bool IsTerminalStatus(string? status)
    {
        return status is ExperimentCatalog.CompletedStatus or
            ExperimentCatalog.InterruptedStatus or
            ExperimentCatalog.FailedStatus;
    }

    public string Title => $"{SetLabel}  {StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

    public string StateLine => Run is { } run
        ? $"状态 {TranslateStatus(run.Status)} · raw {TranslateStatus(run.RawStatus)} · 解调 {TranslateStatus(run.DemodStatus)} · 重构 {TranslateStatus(run.ReconstructionStatus)}" +
          (string.Equals(
              run.LifecycleState,
              ExperimentCatalog.ArchivedLifecycleState,
              StringComparison.Ordinal)
              ? $" · 已归档{(run.ArchivedAt is { } archivedAt ? $" {archivedAt.LocalDateTime:yyyy-MM-dd}" : string.Empty)}"
              : string.Empty)
        : $"旧版只读记录 · {SourceKind}";

    public string CoverageLine
    {
        get
        {
            if (Run is not null)
            {
                return $"raw {Coverage.RawSampleRows} 行 / {Coverage.RawSegmentCount} 段 · " +
                       $"解调 {Coverage.DemodReadyCount}/{Coverage.ProcessingBlockCount}（失败 {Coverage.DemodFailedCount}） · " +
                       $"重构 {Coverage.ReconstructionReadyCount}/{Coverage.ProcessingBlockCount}（待处理 {Coverage.ReconstructionPendingCount}，失败 {Coverage.ReconstructionFailedCount}，参考前 {Coverage.ReconstructionNotApplicableCount}） · " +
                       $"导出 {Coverage.ExportCount}（raw {Coverage.RawCsvExportCount}/{Coverage.RawSegmentCount}）";
            }

            if (LegacyCatalogRun is { } raw && ImagingRun is { } replay)
            {
                return $"raw {raw.Summary.SampleRows} 行 · 回放帧 {replay.Summary.FrameCount} · 重构 {replay.Summary.ReconCount}";
            }

            if (LegacyCatalogRun is { } rawOnly)
            {
                return $"raw {rawOnly.Summary.SampleRows}x{rawOnly.Summary.ChannelCount} · 无可靠实验级回放关联";
            }

            var summary = ImagingRun!.Summary;
            return $"回放帧 {summary.FrameCount} · 重构 {summary.ReconCount} · raw 旧关联 {summary.RawLinkCount}";
        }
    }

    public string ReplayLine => Run is not null
        ? Coverage.DemodReadyCount > 0
            ? $"规范 HDF5 回放就绪 · 解调 {Coverage.DemodReadyCount} · 重构 {Coverage.ReconstructionReadyCount}"
            : "尚无可回放解调块；原始数据仍可检查或离线补算"
        : ImagingRun is { } imaging
            ? $"旧库只读回放 · 帧 {imaging.Summary.FrameCount} · 重构 {imaging.Summary.ReconCount}"
            : "无回放帧；原始数据仍可检查";

    public string LocationLine => $"位置：{LocationPath}";

    public static ExperimentRunListItem CreateCanonical(
        ExperimentRunRecord run,
        ExperimentCoverageSummary coverage,
        string? primaryRawHdf5Path,
        string runDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new ExperimentRunListItem(
            $"canonical:{run.ExperimentRunId:D}",
            run.ExperimentRunId,
            run.SetLabel,
            run.StartedAt,
            run,
            coverage,
            primaryRawHdf5Path,
            Path.GetFullPath(runDirectoryPath),
            null,
            null,
            "统一实验");
    }

    public static ExperimentRunListItem CreateLegacy(
        CatalogRunSummaryItem? raw,
        ImagingRunListItem? imaging,
        string? imagingStorePath = null)
    {
        if (raw is null && imaging is null)
        {
            throw new ArgumentException("A legacy row needs a raw or replay source.");
        }

        var id = imaging?.Summary.ImagingRunId ?? raw!.Summary.RunId;
        var setLabel = imaging?.Summary.SetLabel ?? raw!.Summary.SetLabel;
        var startedAt = imaging?.Summary.StartedAt ?? raw!.Summary.CapturedAt;
        var location = raw?.Summary.Hdf5Path
            ?? imagingStorePath
            ?? "旧版帧库（位置未知）";
        var sourceKind = raw is not null && imaging is not null
            ? "raw + 回放（仅按相同旧 ID 合并）"
            : raw is not null
                ? "raw-only"
                : "replay-only";
        return new ExperimentRunListItem(
            $"legacy:{sourceKind}:{id:D}",
            id,
            setLabel,
            startedAt,
            null,
            ExperimentCoverageSummary.Empty,
            raw?.Summary.Hdf5Path,
            location,
            raw,
            imaging,
            sourceKind);
    }

    private static string TranslateStatus(string status)
    {
        return status switch
        {
            "recording" => "记录中",
            "completed" => "完成",
            "interrupted" => "中断",
            "failed" => "失败",
            "ready" => "就绪",
            "partial" => "不完整",
            "pending" => "待处理",
            "not_applicable" => "参考前不适用",
            "not_requested" => "未请求",
            "incomplete" => "不完整",
            "complete" => "完整",
            "none" => "无",
            _ => status
        };
    }
}
