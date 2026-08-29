using System.IO;
using EitHost.Core.Export;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

/// <summary>
/// Owns the canonical experiment offline-processing tools shown beside the catalog.
/// All writes stay inside the selected experiment's DataRoot directory and catalog-v2 ledger.
/// </summary>
public sealed class ExperimentDataToolsViewModel : ObservableObject
{
    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog experimentCatalog;
    private readonly ExperimentDemodCatchUpService demodCatchUpService;
    private readonly Func<bool> isCatalogReady;
    private readonly Action refreshRuns;
    private string exportSourceHdf5Path = string.Empty;
    private string exportDatasetPath = "/raw/adc_counts";
    private string exportCsvPath = string.Empty;
    private string exportFilter = "all";
    private string demodInputHdf5Path = string.Empty;
    private string demodOutputHdf5Path = string.Empty;
    private string demodulationSummary = "尚未执行离线解调。";
    private string hdf5InspectPath = string.Empty;
    private string hdf5InspectionSummary = "尚未检查 HDF5。";
    private Guid? selectedCanonicalRunId;
    private OfflineReadCatalogSnapshot offlineReadCatalogSnapshot = OfflineReadCatalogSnapshot.Unavailable;

    public ExperimentDataToolsViewModel(
        DataRootLayout dataLayout,
        ExperimentCatalog experimentCatalog,
        ExperimentDemodCatchUpService demodCatchUpService,
        Func<bool> isCatalogReady,
        Action refreshRuns)
    {
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.experimentCatalog = experimentCatalog ?? throw new ArgumentNullException(nameof(experimentCatalog));
        this.demodCatchUpService = demodCatchUpService ?? throw new ArgumentNullException(nameof(demodCatchUpService));
        this.isCatalogReady = isCatalogReady ?? throw new ArgumentNullException(nameof(isCatalogReady));
        this.refreshRuns = refreshRuns ?? throw new ArgumentNullException(nameof(refreshRuns));

        DemodulateHdf5Command = new AsyncRelayCommand(DemodulateHdf5Async, CanDemodulateHdf5);
        DemodulateRecentRunsCommand = new AsyncRelayCommand(DemodulateRecentRunsAsync, CanDemodulateRecentRuns);
        InspectHdf5Command = new AsyncRelayCommand(InspectHdf5Async, CanInspectHdf5);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, CanExportCsv);
        ExportRecentRawCsvCommand = new AsyncRelayCommand(ExportRecentRawCsvAsync, CanExportRecentRawCsv);
        BrowseDemodInputCommand = new RelayCommand(BrowseDemodInput);
        BrowseHdf5InspectCommand = new RelayCommand(BrowseHdf5Inspect);
        BrowseExportSourceCommand = new RelayCommand(BrowseExportSource);
    }

    public event Action<string>? StatusChanged;

    public event Action<string>? DiagnosticMessage;

    internal event Action<string>? DemodulationLogged;

    internal event Action<string>? InspectionLogged;

    internal event Action<string>? ExportLogged;

    public string ExportSourceHdf5Path
    {
        get => exportSourceHdf5Path;
        set
        {
            if (SetProperty(ref exportSourceHdf5Path, value))
            {
                ExportCsvCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ExportDatasetPath
    {
        get => exportDatasetPath;
        set
        {
            if (SetProperty(ref exportDatasetPath, value))
            {
                ExportCsvCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ExportCsvPath
    {
        get => exportCsvPath;
        set => SetProperty(ref exportCsvPath, value);
    }

    public string ExportFilter
    {
        get => exportFilter;
        set => SetProperty(ref exportFilter, value);
    }

    public string DemodInputHdf5Path
    {
        get => demodInputHdf5Path;
        set
        {
            if (SetProperty(ref demodInputHdf5Path, value))
            {
                DemodulateHdf5Command.RaiseCanExecuteChanged();
            }
        }
    }

    public string DemodOutputHdf5Path
    {
        get => demodOutputHdf5Path;
        set => SetProperty(ref demodOutputHdf5Path, value);
    }

    public string DemodulationSummary
    {
        get => demodulationSummary;
        private set => SetProperty(ref demodulationSummary, value);
    }

    public string Hdf5InspectPath
    {
        get => hdf5InspectPath;
        set
        {
            if (SetProperty(ref hdf5InspectPath, value))
            {
                InspectHdf5Command.RaiseCanExecuteChanged();
            }
        }
    }

    public string Hdf5InspectionSummary
    {
        get => hdf5InspectionSummary;
        private set => SetProperty(ref hdf5InspectionSummary, value);
    }

    public AsyncRelayCommand DemodulateHdf5Command { get; }

    public AsyncRelayCommand DemodulateRecentRunsCommand { get; }

    public AsyncRelayCommand InspectHdf5Command { get; }

    public AsyncRelayCommand ExportCsvCommand { get; }

    public AsyncRelayCommand ExportRecentRawCsvCommand { get; }

    public RelayCommand BrowseDemodInputCommand { get; }

    public RelayCommand BrowseHdf5InspectCommand { get; }

    public RelayCommand BrowseExportSourceCommand { get; }

    internal void NotifyCatalogStateChanged(IEnumerable<ExperimentRunListItem> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        RefreshOfflineReadCatalogSnapshot(runs);
        DemodulateHdf5Command.RaiseCanExecuteChanged();
        DemodulateRecentRunsCommand.RaiseCanExecuteChanged();
        InspectHdf5Command.RaiseCanExecuteChanged();
        ExportCsvCommand.RaiseCanExecuteChanged();
        ExportRecentRawCsvCommand.RaiseCanExecuteChanged();
    }

    internal void ApplySelectedExperiment(
        ExperimentRunListItem? experiment,
        bool publishBlockedStatus = true)
    {
        UpdateOfflineReadOwner(experiment);
        selectedCanonicalRunId = experiment?.Run?.ExperimentRunId;
        var allowsOfflineRead = experiment?.Run is null || experiment.IsCanonicalTerminal;
        if (!allowsOfflineRead)
        {
            ClearOfflineSourcePaths();
            if (publishBlockedStatus)
            {
                PublishStatus(string.Equals(
                    experiment!.Run!.Status,
                    ExperimentCatalog.RecordingStatus,
                    StringComparison.Ordinal)
                    ? "所选实验仍在记录中，已禁用离线解调、HDF5 检查和 CSV 导出；请停止采集后再操作。"
                    : $"所选实验状态“{experiment.Run.Status}”不是可离线读取终态，已禁用离线解调、HDF5 检查和 CSV 导出。");
            }

            return;
        }

        if (experiment?.Run is not { } run
            || experiment.PrimaryRawHdf5Path is not { } rawPath
            || !File.Exists(rawPath))
        {
            ClearOfflineSourcePaths();
            return;
        }

        DemodInputHdf5Path = rawPath;
        Hdf5InspectPath = rawPath;
        ExportSourceHdf5Path = rawPath;
        if (!experiment.IsLegacy)
        {
            DemodOutputHdf5Path = GetCanonicalDerivedDirectoryPath(run.ExperimentRunId);
            ExportCsvPath = CreateCanonicalCsvPath(
                run.ExperimentRunId,
                rawPath,
                ExportDatasetPath,
                ExportFilter);
        }
        else
        {
            DemodOutputHdf5Path = string.Empty;
            ExportCsvPath = string.Empty;
        }
    }

    internal void ApplySavedRawArtifact(Guid experimentRunId, string hdf5Path, bool catalogRegistered)
    {
        if (selectedCanonicalRunId is { } selectedRunId && selectedRunId != experimentRunId)
        {
            return;
        }

        if (catalogRegistered)
        {
            var registeredRun = experimentCatalog.GetRun(experimentRunId);
            if (registeredRun is null || !ExperimentRunListItem.IsTerminalStatus(registeredRun.Status))
            {
                UpdateOfflineReadOwner(registeredRun);
                ClearOfflineSourcePaths();
                return;
            }

            UpdateOfflineReadOwner(registeredRun);
        }
        else if (selectedCanonicalRunId is not null)
        {
            return;
        }

        DemodInputHdf5Path = hdf5Path;
        DemodOutputHdf5Path = catalogRegistered
            ? GetCanonicalDerivedDirectoryPath(experimentRunId)
            : string.Empty;
        Hdf5InspectPath = hdf5Path;
        ExportSourceHdf5Path = hdf5Path;
        ExportDatasetPath = "/raw/adc_counts";
        ExportCsvPath = catalogRegistered
            ? CreateCanonicalCsvPath(experimentRunId, hdf5Path, ExportDatasetPath, ExportFilter)
            : string.Empty;
    }

    private async Task DemodulateHdf5Async()
    {
        if (!isCatalogReady())
        {
            PublishStatus("SQLite catalog 未准备好，不能登记离线解调结果。");
            return;
        }

        if (!TryAdmitOfflineRead(DemodInputHdf5Path, "离线解调"))
        {
            return;
        }

        try
        {
            var sourcePath = Path.GetFullPath(DemodInputHdf5Path);
            var sourceRelativePath = dataLayout.ToRelativeArtifactPath(sourcePath);
            var segment = experimentCatalog.FindRawSegmentByArtifactPath(sourceRelativePath);
            if (segment is null)
            {
                PublishStatus("离线解调已拒绝：该 HDF5 不是统一实验的 raw 段；旧版数据须先导入，当前保持只读。");
                return;
            }

            var report = await Task.Run(() => demodCatchUpService.Run(segment.ExperimentRunId)).ConfigureAwait(true);
            var latestDemod = experimentCatalog.ListDerivedArtifacts(segment.ExperimentRunId)
                .Where(artifact => string.Equals(artifact.Kind, "demod", StringComparison.Ordinal))
                .OrderByDescending(artifact => artifact.BlockNumber)
                .FirstOrDefault();
            DemodOutputHdf5Path = latestDemod is null
                ? GetCanonicalDerivedDirectoryPath(segment.ExperimentRunId)
                : dataLayout.ResolveArtifactPath(latestDemod.ArtifactPath);
            DemodulationSummary =
                $"统一补算：新增 {report.RecoveredBlockCount} 块，跳过 {report.SkippedBlockCount} 块，" +
                $"待处理 {report.PendingRawRows} 行，失败 {report.FailedBlockCount} 块。";
            refreshRuns();
            var log = $"canonical demod run={segment.ExperimentRunId:D} source={sourcePath} recovered={report.RecoveredBlockCount} pending={report.PendingRawRows}";
            DemodulationLogged?.Invoke(log);
            PublishDiagnostic(log);
            PublishStatus(report.FailedBlockCount == 0 && report.MissingSegmentCount == 0
                ? "离线解调完成，结果已写入统一实验 derived 目录和处理账本。"
                : $"离线解调结束：失败 {report.FailedBlockCount} 块，缺失 raw 段 {report.MissingSegmentCount} 个。");
        }
        catch (Exception ex)
        {
            PublishStatus($"离线解调失败：{ex.Message}");
        }
    }

    private async Task DemodulateRecentRunsAsync()
    {
        if (!isCatalogReady())
        {
            PublishStatus("SQLite catalog 未准备好，不能批量登记离线解调结果。");
            return;
        }

        try
        {
            var runs = experimentCatalog.ListRunSummaries(30)
                .Where(run => ExperimentRunListItem.IsTerminalStatus(run.Run.Status) &&
                              run.Coverage.RawSegmentCount > 0 &&
                              run.Run.DemodStatus is not ("complete" or "not_requested"))
                .ToArray();
            if (runs.Length == 0)
            {
                PublishStatus("最近统一实验中没有待补算的 raw 数据。");
                return;
            }

            var reports = await Task.Run(() =>
                runs.Select(run => demodCatchUpService.Run(run.Run.ExperimentRunId)).ToArray()).ConfigureAwait(true);
            foreach (var report in reports)
            {
                var log = $"batch canonical demod run={report.ExperimentRunId:D} recovered={report.RecoveredBlockCount} pending={report.PendingRawRows}";
                DemodulationLogged?.Invoke(log);
                PublishDiagnostic(log);
            }

            var lastRun = runs[^1];
            if (lastRun.PrimaryRawArtifactPath is { } relativeRawPath)
            {
                DemodInputHdf5Path = dataLayout.ResolveArtifactPath(relativeRawPath);
            }

            DemodOutputHdf5Path = GetCanonicalDerivedDirectoryPath(lastRun.Run.ExperimentRunId);
            DemodulationSummary = $"批量解调 {reports.Length} 个统一实验；新增 {reports.Sum(report => report.RecoveredBlockCount)} 块。";
            refreshRuns();
            PublishStatus($"批量离线解调完成：{reports.Length} 个实验，缺失 raw 段 {reports.Sum(report => report.MissingSegmentCount)} 个。");
        }
        catch (Exception ex)
        {
            PublishStatus($"批量离线解调失败：{ex.Message}");
        }
    }

    private bool CanDemodulateHdf5()
    {
        return isCatalogReady()
            && !string.IsNullOrWhiteSpace(DemodInputHdf5Path)
            && File.Exists(DemodInputHdf5Path)
            && IsOfflineReadAllowed(DemodInputHdf5Path, out _);
    }

    private bool CanDemodulateRecentRuns()
    {
        return isCatalogReady() && Volatile.Read(ref offlineReadCatalogSnapshot).CanDemodulateRecentRuns;
    }

    private async Task InspectHdf5Async()
    {
        if (!TryAdmitOfflineRead(Hdf5InspectPath, "HDF5 检查"))
        {
            return;
        }

        try
        {
            var inputPath = Hdf5InspectPath;
            var inspection = await Task.Run(() => new Hdf5RunInspector().Inspect(inputPath)).ConfigureAwait(true);
            Hdf5InspectionSummary =
                $"{(inspection.Passed ? "通过" : "未通过")}：{inspection.Device.SetLabel} raw {inspection.RawSampleRows}x{inspection.RawChannelCount}，RunId {inspection.RunId}，请求激励 {inspection.Excitation.FrequencyHz}Hz，实际 {(inspection.Excitation.ActualFrequencyHz ?? inspection.Excitation.FrequencyHz):0.########}Hz，采样 {inspection.Acquisition.SampleRateHz}Hz，问题 {inspection.Issues.Count}";
            var log = $"{Path.GetFileName(inputPath)} {inspection.Device.SetLabel} raw={inspection.RawSampleRows}x{inspection.RawChannelCount} issues={inspection.Issues.Count}";
            InspectionLogged?.Invoke(log);
            PublishDiagnostic($"inspect {log}");
            PublishStatus($"HDF5 检查完成：{inspection.Device.SetLabel}。");
        }
        catch (Exception ex)
        {
            Hdf5InspectionSummary = $"HDF5 检查失败：{ex.Message}";
            PublishStatus(Hdf5InspectionSummary);
        }
    }

    private bool CanInspectHdf5()
    {
        return !string.IsNullOrWhiteSpace(Hdf5InspectPath)
            && File.Exists(Hdf5InspectPath)
            && IsOfflineReadAllowed(Hdf5InspectPath, out _);
    }

    private async Task ExportCsvAsync()
    {
        if (!isCatalogReady())
        {
            PublishStatus("SQLite catalog 未准备好，不能登记 CSV 导出。");
            return;
        }

        if (!TryAdmitOfflineRead(ExportSourceHdf5Path, "CSV 导出"))
        {
            return;
        }

        try
        {
            var sourcePath = Path.GetFullPath(ExportSourceHdf5Path);
            var sourceRelativePath = dataLayout.ToRelativeArtifactPath(sourcePath);
            var runId = experimentCatalog.FindRunIdByArtifactPath(sourceRelativePath);
            if (runId is null)
            {
                PublishStatus("CSV 导出已拒绝：源 HDF5 不属于统一实验；旧版数据须先导入，当前保持只读。");
                return;
            }

            var csvPath = CreateCanonicalCsvPath(runId.Value, sourcePath, ExportDatasetPath, ExportFilter);
            var result = await Task.Run(() => new Hdf5CsvExporter().Export(new CsvExportRequest(
                sourcePath,
                ExportDatasetPath,
                csvPath,
                ExportFilter))).ConfigureAwait(true);
            experimentCatalog.RegisterExport(new ExperimentExportCatalogRecord(
                runId.Value,
                sourceRelativePath,
                result.DatasetPath,
                dataLayout.ToRelativeArtifactPath(result.CsvPath),
                result.Filter,
                DateTimeOffset.UtcNow));
            ExportCsvPath = result.CsvPath;
            refreshRuns();
            var log = $"{result.DatasetPath} -> {result.CsvPath} ({result.RowCount}x{result.ColumnCount})";
            ExportLogged?.Invoke(log);
            PublishDiagnostic($"export {log}");
            PublishStatus("CSV 导出完成，文件与来源已登记统一实验导出账本。");
        }
        catch (Exception ex)
        {
            PublishStatus($"CSV 导出失败：{ex.Message}");
        }
    }

    private async Task ExportRecentRawCsvAsync()
    {
        if (!isCatalogReady())
        {
            PublishStatus("SQLite catalog 未准备好，不能批量登记 CSV 导出。");
            return;
        }

        try
        {
            var runs = experimentCatalog.ListRunSummaries(30)
                .Where(run => ExperimentRunListItem.IsTerminalStatus(run.Run.Status) &&
                              run.Coverage.RawSegmentCount > run.Coverage.RawCsvExportCount)
                .ToArray();
            if (runs.Length == 0)
            {
                PublishStatus("最近统一实验中没有待导出的 raw 段。");
                return;
            }

            var batch = await Task.Run(() => ExportRawCsvBatch(runs)).ConfigureAwait(true);
            foreach (var result in batch.Results)
            {
                var log = $"batch {result.DatasetPath} -> {result.CsvPath} ({result.RowCount}x{result.ColumnCount})";
                ExportLogged?.Invoke(log);
                PublishDiagnostic($"export {log}");
            }

            if (batch.Results.Count > 0)
            {
                var last = batch.Results[^1];
                ExportSourceHdf5Path = last.SourceHdf5Path;
                ExportDatasetPath = last.DatasetPath;
                ExportCsvPath = last.CsvPath;
                ExportFilter = last.Filter;
            }

            refreshRuns();
            PublishStatus($"批量 CSV 导出完成：{batch.Results.Count} 个文件，跳过缺失 raw {batch.MissingRawCount} 个。");
        }
        catch (Exception ex)
        {
            PublishStatus($"批量 CSV 导出失败：{ex.Message}");
        }
    }

    private CanonicalCsvExportBatch ExportRawCsvBatch(IReadOnlyList<ExperimentRunCatalogSummary> runs)
    {
        var exporter = new Hdf5CsvExporter();
        var completed = new List<CsvExportResult>();
        var missing = 0;
        foreach (var run in runs)
        {
            var existingExports = experimentCatalog.ListExports(run.Run.ExperimentRunId);
            foreach (var segment in experimentCatalog.ListRawSegments(run.Run.ExperimentRunId)
                         .Where(segment => string.Equals(segment.Status, "ready", StringComparison.Ordinal)))
            {
                var alreadyExported = existingExports.Any(export =>
                    string.Equals(export.SourceArtifactPath, segment.ArtifactPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(export.DatasetPath, "/raw/adc_counts", StringComparison.Ordinal) &&
                    string.Equals(export.Filter, "all", StringComparison.Ordinal));
                if (alreadyExported)
                {
                    continue;
                }

                var sourcePath = dataLayout.ResolveArtifactPath(segment.ArtifactPath);
                if (!File.Exists(sourcePath))
                {
                    missing++;
                    continue;
                }

                var csvPath = CreateCanonicalCsvPath(run.Run.ExperimentRunId, sourcePath, "/raw/adc_counts", "all");
                var result = exporter.Export(new CsvExportRequest(sourcePath, "/raw/adc_counts", csvPath, "all"));
                experimentCatalog.RegisterExport(new ExperimentExportCatalogRecord(
                    run.Run.ExperimentRunId,
                    segment.ArtifactPath,
                    result.DatasetPath,
                    dataLayout.ToRelativeArtifactPath(result.CsvPath),
                    result.Filter,
                    DateTimeOffset.UtcNow));
                completed.Add(result);
            }
        }

        return new CanonicalCsvExportBatch(completed, missing);
    }

    private bool CanExportCsv()
    {
        return isCatalogReady()
            && !string.IsNullOrWhiteSpace(ExportSourceHdf5Path)
            && File.Exists(ExportSourceHdf5Path)
            && !string.IsNullOrWhiteSpace(ExportDatasetPath)
            && IsOfflineReadAllowed(ExportSourceHdf5Path, out _);
    }

    private bool CanExportRecentRawCsv()
    {
        return isCatalogReady() && Volatile.Read(ref offlineReadCatalogSnapshot).CanExportRecentRawCsv;
    }

    private void BrowseDemodInput()
    {
        if (PromptOpenHdf5(DemodInputHdf5Path) is { } path)
        {
            if (TryAdmitOfflineRead(path, "离线解调"))
            {
                DemodInputHdf5Path = path;
                DemodOutputHdf5Path = string.Empty;
            }
            else
            {
                DemodInputHdf5Path = string.Empty;
            }
        }
    }

    private void BrowseHdf5Inspect()
    {
        if (PromptOpenHdf5(Hdf5InspectPath) is { } path)
        {
            Hdf5InspectPath = TryAdmitOfflineRead(path, "HDF5 检查")
                ? path
                : string.Empty;
        }
    }

    private void BrowseExportSource()
    {
        if (PromptOpenHdf5(ExportSourceHdf5Path) is { } path)
        {
            if (TryAdmitOfflineRead(path, "CSV 导出"))
            {
                ExportSourceHdf5Path = path;
                ExportCsvPath = string.Empty;
            }
            else
            {
                ExportSourceHdf5Path = string.Empty;
            }
        }
    }

    private string GetCanonicalDerivedDirectoryPath(Guid experimentRunId)
    {
        var run = experimentCatalog.GetRun(experimentRunId);
        return run is null
            ? string.Empty
            : Path.Combine(dataLayout.ResolveArtifactPath(run.RunDirectory), "derived");
    }

    private void ClearOfflineSourcePaths()
    {
        DemodInputHdf5Path = string.Empty;
        DemodOutputHdf5Path = string.Empty;
        Hdf5InspectPath = string.Empty;
        ExportSourceHdf5Path = string.Empty;
        ExportCsvPath = string.Empty;
    }

    private bool TryAdmitOfflineRead(string path, string operation)
    {
        if (IsOfflineReadAllowed(path, out var blockedStatus))
        {
            return true;
        }

        PublishStatus(blockedStatus is null
            ? $"{operation}已拒绝：源文件不存在或 catalog 尚未准备好。"
            : $"{operation}已拒绝：该文件属于状态“{blockedStatus}”的实验；仅 completed、interrupted、failed 终态允许离线读取。");
        return false;
    }

    private bool IsOfflineReadAllowed(string path, out string? blockedStatus)
    {
        blockedStatus = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!IsContainedPath(dataLayout.RootPath, fullPath))
        {
            return true;
        }

        if (!isCatalogReady())
        {
            return false;
        }

        var snapshot = Volatile.Read(ref offlineReadCatalogSnapshot);
        if (!snapshot.IsAvailable)
        {
            return false;
        }

        var owner = snapshot.Owners
            .Where(item => IsContainedPath(item.Directory, fullPath))
            .OrderByDescending(item => item.Directory.Length)
            .FirstOrDefault();
        if (owner is null || ExperimentRunListItem.IsTerminalStatus(owner.Status))
        {
            return true;
        }

        blockedStatus = owner.Status;
        return false;
    }

    private void RefreshOfflineReadCatalogSnapshot(IEnumerable<ExperimentRunListItem> runs)
    {
        if (!isCatalogReady())
        {
            Volatile.Write(ref offlineReadCatalogSnapshot, OfflineReadCatalogSnapshot.Unavailable);
            return;
        }

        var canonicalRuns = runs
            .Where(run => run.Run is not null)
            .ToArray();
        Volatile.Write(
            ref offlineReadCatalogSnapshot,
            new OfflineReadCatalogSnapshot(
                IsAvailable: true,
                canonicalRuns.Select(CreateOfflineReadOwner).ToArray(),
                canonicalRuns.Any(run =>
                    run.IsCanonicalTerminal &&
                    run.Coverage.RawSegmentCount > 0 &&
                    run.Run!.DemodStatus is not ("complete" or "not_requested")),
                canonicalRuns.Any(run =>
                    run.IsCanonicalTerminal &&
                    run.Coverage.RawSegmentCount > run.Coverage.RawCsvExportCount)));
    }

    internal void UpdateOfflineReadOwner(ExperimentRunListItem? item)
    {
        if (item?.Run is { } run)
        {
            UpdateOfflineReadOwner(run, item.LocationPath);
        }
    }

    private void UpdateOfflineReadOwner(ExperimentRunRecord? run)
    {
        if (run is not null)
        {
            UpdateOfflineReadOwner(run, dataLayout.ResolveArtifactPath(run.RunDirectory));
        }
    }

    private void UpdateOfflineReadOwner(ExperimentRunRecord run, string directory)
    {
        var snapshot = Volatile.Read(ref offlineReadCatalogSnapshot);
        if (!snapshot.IsAvailable)
        {
            return;
        }

        var owner = new OfflineReadOwner(
            run.ExperimentRunId,
            Path.GetFullPath(directory),
            run.Status);
        var owners = snapshot.Owners
            .Where(item => item.ExperimentRunId != run.ExperimentRunId)
            .Append(owner)
            .ToArray();
        Volatile.Write(ref offlineReadCatalogSnapshot, snapshot with { Owners = owners });
    }

    private static OfflineReadOwner CreateOfflineReadOwner(ExperimentRunListItem item) =>
        new(
            item.ExperimentRunId,
            item.LocationPath,
            item.Run!.Status);

    private static bool IsContainedPath(string directory, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private string CreateCanonicalCsvPath(Guid experimentRunId, string sourceHdf5Path, string datasetPath, string filter)
    {
        var run = experimentCatalog.GetRun(experimentRunId)
                  ?? throw new InvalidOperationException("Experiment run is not registered.");
        var exportsDirectory = Path.Combine(dataLayout.ResolveArtifactPath(run.RunDirectory), "exports");
        var sourceName = Path.GetFileNameWithoutExtension(sourceHdf5Path);
        var datasetToken = CreateArtifactFileToken(datasetPath, "dataset");
        var filterToken = CreateArtifactFileToken(filter, "all");
        return Path.Combine(exportsDirectory, $"{sourceName}_{datasetToken}_{filterToken}.csv");
    }

    private static string CreateArtifactFileToken(string value, string fallback)
    {
        var token = string.Concat(value.Trim().Select(character =>
            char.IsLetterOrDigit(character) ? character : '_')).Trim('_');
        return string.IsNullOrWhiteSpace(token) ? fallback : token;
    }

    private string? PromptOpenHdf5(string currentPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "HDF5 文件 (*.h5;*.hdf5)|*.h5;*.hdf5|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        ApplyDialogStartLocation(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ApplyDialogStartLocation(Microsoft.Win32.FileDialog dialog, string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            dialog.FileName = Path.GetFileName(currentPath);
        }
        else if (Directory.Exists(dataLayout.RootPath))
        {
            dialog.InitialDirectory = dataLayout.RootPath;
        }
    }

    private void PublishStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void PublishDiagnostic(string message)
    {
        DiagnosticMessage?.Invoke(message);
    }

    private sealed record CanonicalCsvExportBatch(IReadOnlyList<CsvExportResult> Results, int MissingRawCount);

    private sealed record OfflineReadOwner(Guid ExperimentRunId, string Directory, string Status);

    private sealed record OfflineReadCatalogSnapshot(
        bool IsAvailable,
        IReadOnlyList<OfflineReadOwner> Owners,
        bool CanDemodulateRecentRuns,
        bool CanExportRecentRawCsv)
    {
        internal static OfflineReadCatalogSnapshot Unavailable { get; } =
            new(false, [], false, false);
    }
}
