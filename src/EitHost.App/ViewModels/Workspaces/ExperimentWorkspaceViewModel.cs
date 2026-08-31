using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using EitHost.App.ViewModels;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

public sealed class ExperimentWorkspaceViewModel : WorkspaceViewModelBase, IExperimentWorkspaceViewModel
{
    private const string LifecycleCatchUpBusyMessage =
        "离线完整重算正在处理数据；请先停止并等待状态消失，或等待重算完成。";
    private ExperimentRunLifecycleController? runLifecycleController;
    private RawAcquisitionPersistenceController? rawAcquisitionPersistenceController;
    private const int RecentRunQueryLimit = 500;
    private readonly DataRootLayout dataLayout;
    private readonly ExperimentCatalog experimentCatalog;
    private readonly IDataRootStorageService dataRootStorageService;
    private readonly IExperimentDataLifecycleService lifecycleService;
    private readonly Action<string> directoryOpener;
    private readonly Func<string, string, bool> lifecycleConfirmation;
    private readonly Action<Guid, string, string> reconciliationScheduler;
    private readonly Func<Task> legacyReplayRefresh;
    private Func<Guid, Task> prepareExperimentDeletion = static _ => Task.CompletedTask;
    private readonly Func<IReadOnlyList<EitCatalogRunSummary>>? legacyRunLoader;
    private readonly IReadOnlyList<EitCatalog> legacyCatalogReaders;
    private readonly Dictionary<Guid, string> imagingRunStorePaths = [];
    private readonly ObservableCollection<ImagingRunListItem> imagingRuns = [];
    private ICollectionView? experimentRunsView;
    private ICollectionView? recentRunsView;
    private ExperimentRunListItem? selectedExperimentRun;
    private DateTime? selectedRunDate;
    private bool catalogReady;
    private bool isRetentionArchiveRunning;
    private bool suppressSelectionNotification;
    private CancellationTokenSource? retentionArchiveCancellation;
    private int legacyRefreshVersion;
    private int retentionArchiveCompletedCount;
    private int retentionArchiveTotalCount;
    private int selectedStorageLoadVersion;
    private string dataRootStorageSummary = "数据目录容量：等待检查。";
    private string selectedExperimentStorageSummary = "选择统一实验后显示占用与保留状态。";
    private string retentionArchiveProgressSummary = "批量归档：未运行。";
    private string catchUpProgressSummary = string.Empty;

    public ExperimentWorkspaceViewModel(
        DataRootLayout dataLayout,
        ExperimentCatalog experimentCatalog,
        IDataRootStorageService dataRootStorageService,
        IExperimentDataLifecycleService lifecycleService,
        Action<string> directoryOpener,
        Func<string, string, bool> lifecycleConfirmation,
        Action<Guid, string, string> reconciliationScheduler,
        Func<Task> legacyReplayRefresh,
        Func<IReadOnlyList<EitCatalogRunSummary>>? legacyRunLoader = null,
        ExperimentDemodCatchUpService? demodCatchUpService = null)
        : base("experiment")
    {
        this.dataLayout = dataLayout ?? throw new ArgumentNullException(nameof(dataLayout));
        this.experimentCatalog = experimentCatalog ?? throw new ArgumentNullException(nameof(experimentCatalog));
        this.dataRootStorageService = dataRootStorageService ??
            throw new ArgumentNullException(nameof(dataRootStorageService));
        this.lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        this.directoryOpener = directoryOpener ?? throw new ArgumentNullException(nameof(directoryOpener));
        this.lifecycleConfirmation = lifecycleConfirmation ??
            throw new ArgumentNullException(nameof(lifecycleConfirmation));
        this.reconciliationScheduler = reconciliationScheduler ??
            throw new ArgumentNullException(nameof(reconciliationScheduler));
        this.legacyReplayRefresh = legacyReplayRefresh ?? throw new ArgumentNullException(nameof(legacyReplayRefresh));
        this.legacyRunLoader = legacyRunLoader;
        legacyCatalogReaders = CreateLegacyCatalogReaders(dataLayout);
        DataTools = new ExperimentDataToolsViewModel(
            dataLayout,
            experimentCatalog,
            demodCatchUpService ?? new ExperimentDemodCatchUpService(
                dataLayout,
                experimentCatalog,
                new DerivedArtifactHdf5Writer()),
            () => catalogReady,
            RefreshCanonicalRuns);
        DataTools.StatusChanged += message => StatusChanged?.Invoke(message);
        DataTools.DiagnosticMessage += message => DiagnosticMessage?.Invoke(message);

        RefreshCatalogRunsCommand = new AsyncRelayCommand(RefreshCatalogRunsAsync, () => catalogReady);
        RefreshDataRootStorageCommand = new AsyncRelayCommand(RefreshDataRootStorageAsync);
        OpenDataRootCommand = new RelayCommand(OpenDataRoot);
        OpenSelectedExperimentDirectoryCommand = new RelayCommand(
            OpenSelectedExperimentDirectory,
            CanOpenSelectedExperimentDirectory);
        ArchiveSelectedExperimentCommand = new AsyncRelayCommand(
            ArchiveSelectedExperimentAsync,
            CanArchiveSelectedExperiment);
        DeleteSelectedExperimentCommand = new AsyncRelayCommand(
            DeleteSelectedExperimentAsync,
            CanDeleteSelectedExperiment);
        ArchiveRetentionCandidatesCommand = new AsyncRelayCommand(
            ArchiveRetentionCandidatesAsync,
            CanArchiveRetentionCandidates);
        CancelRetentionArchiveCommand = new RelayCommand(
            CancelRetentionArchive,
            () => IsRetentionArchiveRunning);
        ReconcileSelectedExperimentCommand = new RelayCommand(
            ReconcileSelectedExperiment,
            CanReconcileSelectedExperiment);
        DeleteOfflineCompleteRevisionCommand = new AsyncRelayCommand(
            DeleteOfflineCompleteRevisionAsync,
            CanDeleteOfflineCompleteRevision);
        CancelCatchUpCommand = new RelayCommand(
            () => RunLifecycleController.CancelCatchUp(),
            () => IsCatchUpRunning);
        ClearRunDateFilterCommand = new RelayCommand(
            () => SelectedRunDate = null,
            () => selectedRunDate is not null);
    }

    public event Action<ExperimentRunListItem?>? SelectionChanged;

    public event Action<string>? StatusChanged;

    public event Action<string>? DiagnosticMessage;

    internal void AttachExperimentDeletionPreparation(Func<Guid, Task> preparation)
    {
        prepareExperimentDeletion = preparation ?? throw new ArgumentNullException(nameof(preparation));
    }

    public ExperimentDataToolsViewModel DataTools { get; }

    internal ExperimentRunLifecycleController RunLifecycleController =>
        runLifecycleController ?? throw new InvalidOperationException("Experiment run lifecycle controller has not been attached.");

    internal RawAcquisitionPersistenceController RawAcquisitionPersistenceController =>
        rawAcquisitionPersistenceController ?? throw new InvalidOperationException("Raw acquisition persistence controller has not been attached.");

    internal void AttachRunLifecycleController(ExperimentRunLifecycleController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (runLifecycleController is not null)
        {
            throw new InvalidOperationException("Experiment run lifecycle controller is already attached.");
        }

        runLifecycleController = controller;
    }

    internal void AttachRawAcquisitionPersistenceController(RawAcquisitionPersistenceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (rawAcquisitionPersistenceController is not null)
        {
            throw new InvalidOperationException("Raw acquisition persistence controller is already attached.");
        }

        rawAcquisitionPersistenceController = controller;
    }

    public string DataRootPath => dataLayout.RootPath;

    public string DataRootStorageSummary
    {
        get => dataRootStorageSummary;
        private set => SetProperty(ref dataRootStorageSummary, value);
    }

    public string SelectedExperimentStorageSummary
    {
        get => selectedExperimentStorageSummary;
        private set => SetProperty(ref selectedExperimentStorageSummary, value);
    }

    public string RetentionPolicySummary =>
        $"保留策略：{ExperimentDataLifecycleService.DefaultRetentionDays} 天（仅建议标记，不自动归档或删除）；归档后仍可回放。";

    public bool IsRetentionArchiveRunning
    {
        get => isRetentionArchiveRunning;
        private set
        {
            if (!SetProperty(ref isRetentionArchiveRunning, value))
            {
                return;
            }

            ArchiveRetentionCandidatesCommand.RaiseCanExecuteChanged();
            CancelRetentionArchiveCommand.RaiseCanExecuteChanged();
        }
    }

    public int RetentionArchiveCompletedCount
    {
        get => retentionArchiveCompletedCount;
        private set => SetProperty(ref retentionArchiveCompletedCount, value);
    }

    public int RetentionArchiveTotalCount
    {
        get => retentionArchiveTotalCount;
        private set => SetProperty(ref retentionArchiveTotalCount, value);
    }

    public string RetentionArchiveProgressSummary
    {
        get => retentionArchiveProgressSummary;
        private set => SetProperty(ref retentionArchiveProgressSummary, value);
    }

    public ObservableCollection<CatalogRunSummaryItem> RecentRuns { get; } = [];

    public ObservableCollection<ExperimentRunListItem> ExperimentRuns { get; } = [];

    internal Task BackgroundRefreshTask { get; private set; } = Task.CompletedTask;

    internal Task SelectedExperimentStorageRefreshTask { get; private set; } = Task.CompletedTask;

    public ICollectionView ExperimentRunsView => experimentRunsView ??= CreateExperimentRunsView();

    public ICollectionView RecentRunsView => recentRunsView ??= CreateRecentRunsView();

    public ExperimentRunListItem? SelectedExperimentRun
    {
        get => selectedExperimentRun;
        set
        {
            if (!SetProperty(ref selectedExperimentRun, value))
            {
                return;
            }

            RefreshSelectionCommands();
            if (value is null)
            {
                Interlocked.Increment(ref selectedStorageLoadVersion);
                SelectedExperimentStorageSummary = "选择统一实验后显示占用与保留状态。";
                SelectedExperimentStorageRefreshTask = Task.CompletedTask;
            }
            else
            {
                SelectedExperimentStorageRefreshTask = RefreshSelectedExperimentStorageAsync(value);
            }

            if (!suppressSelectionNotification)
            {
                PublishStatus(
                    value is null ? "idle" : "selected",
                    value?.Title ?? string.Empty,
                    DateTimeOffset.Now);
                SelectionChanged?.Invoke(value);
            }
        }
    }

    public DateTime? SelectedRunDate
    {
        get => selectedRunDate;
        set
        {
            if (!SetProperty(ref selectedRunDate, value))
            {
                return;
            }

            RecentRunsView.Refresh();
            ExperimentRunsView.Refresh();
            OnPropertyChanged(nameof(RunFilterSummary));
            ClearRunDateFilterCommand.RaiseCanExecuteChanged();
        }
    }

    public string RunFilterSummary
    {
        get
        {
            var total = ExperimentRuns.Count;
            if (selectedRunDate is null)
            {
                return $"共 {total} 个实验";
            }

            var shown = ExperimentRuns.Count(IsExperimentOnSelectedDate);
            return $"{selectedRunDate.Value:yyyy-MM-dd} · {shown} / {total} 个实验";
        }
    }

    public AsyncRelayCommand RefreshCatalogRunsCommand { get; }

    public AsyncRelayCommand RefreshDataRootStorageCommand { get; }

    public RelayCommand OpenDataRootCommand { get; }

    public RelayCommand OpenSelectedExperimentDirectoryCommand { get; }

    public AsyncRelayCommand ArchiveSelectedExperimentCommand { get; }

    public AsyncRelayCommand DeleteSelectedExperimentCommand { get; }

    public AsyncRelayCommand ArchiveRetentionCandidatesCommand { get; }

    public RelayCommand CancelRetentionArchiveCommand { get; }

    public RelayCommand ReconcileSelectedExperimentCommand { get; }

    public AsyncRelayCommand DeleteOfflineCompleteRevisionCommand { get; }

    public RelayCommand CancelCatchUpCommand { get; }

    /// <summary>
    /// The manual offline-complete job can take minutes on a long run. Empty while idle so the
    /// indicator only occupies the panel while operator-requested work is in flight.
    /// </summary>
    public string CatchUpProgressSummary
    {
        get => catchUpProgressSummary;
        private set
        {
            if (SetProperty(ref catchUpProgressSummary, value))
            {
                OnPropertyChanged(nameof(IsCatchUpRunning));
                CancelCatchUpCommand.RaiseCanExecuteChanged();
                ArchiveSelectedExperimentCommand.RaiseCanExecuteChanged();
                DeleteSelectedExperimentCommand.RaiseCanExecuteChanged();
                DeleteOfflineCompleteRevisionCommand.RaiseCanExecuteChanged();
                ArchiveRetentionCandidatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCatchUpRunning => !string.IsNullOrEmpty(CatchUpProgressSummary);

    internal void ApplyCatchUpProgress(string summary) => CatchUpProgressSummary = summary;

    public RelayCommand ClearRunDateFilterCommand { get; }

    public void SetCatalogReady(
        bool ready,
        DataRootStorageSnapshot? initialStorage = null,
        IReadOnlyList<ExperimentRunCatalogSummary>? initialCanonicalRuns = null)
    {
        catalogReady = ready;
        RefreshCatalogRunsCommand.RaiseCanExecuteChanged();
        DataTools.NotifyCatalogStateChanged(ExperimentRuns);
        if (!ready)
        {
            return;
        }

        if (initialStorage is not null)
        {
            UpdateDataRootStorageSummary(initialStorage);
        }

        if (initialCanonicalRuns is not null)
        {
            ReplaceExperimentRunsFromCurrentSources(initialCanonicalRuns);
            RefreshFromCurrentSources(initialCanonicalRuns);
        }
        else
        {
            RefreshFromCurrentSources();
        }
    }

    public void UpdateLegacyImagingRuns(
        IEnumerable<ImagingRunListItem> runs,
        IReadOnlyDictionary<Guid, string> storePaths)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(storePaths);
        imagingRuns.Clear();
        imagingRunStorePaths.Clear();
        foreach (var run in runs)
        {
            imagingRuns.Add(run);
            if (storePaths.TryGetValue(run.Summary.ImagingRunId, out var path))
            {
                imagingRunStorePaths[run.Summary.ImagingRunId] = path;
            }
        }

        ReplaceExperimentRunsFromCurrentSources();
    }

    public void RefreshFromCurrentSources()
    {
        var requestVersion = Interlocked.Increment(ref legacyRefreshVersion);
        BackgroundRefreshTask = RefreshFromCurrentSourcesAsync(requestVersion, initialCanonicalRuns: null);
    }

    private void RefreshFromCurrentSources(IReadOnlyList<ExperimentRunCatalogSummary> initialCanonicalRuns)
    {
        var requestVersion = Interlocked.Increment(ref legacyRefreshVersion);
        BackgroundRefreshTask = RefreshFromCurrentSourcesAsync(requestVersion, initialCanonicalRuns);
    }

    private async Task RefreshFromCurrentSourcesAsync(
        int requestVersion,
        IReadOnlyList<ExperimentRunCatalogSummary>? initialCanonicalRuns)
    {
        if (!catalogReady)
        {
            return;
        }

        try
        {
            var legacyRunsTask = Task.Run(LoadLegacyRecentRuns);
            var canonicalRunsTask = initialCanonicalRuns is null
                ? Task.Run(() => experimentCatalog.ListRunSummaries(RecentRunQueryLimit))
                : Task.FromResult(initialCanonicalRuns);
            await Task.WhenAll(legacyRunsTask, canonicalRunsTask).ConfigureAwait(true);
            if (requestVersion != Volatile.Read(ref legacyRefreshVersion))
            {
                return;
            }

            ReplaceRecentRuns(legacyRunsTask.Result, canonicalRunsTask.Result);
        }
        catch (Exception ex)
        {
            if (requestVersion == Volatile.Read(ref legacyRefreshVersion))
            {
                UpdateStatus($"刷新最近数据记录失败：{ex.Message}", "error");
            }
        }
    }

    public void RefreshCanonicalRuns()
    {
        ReplaceExperimentRunsFromCurrentSources();
    }

    public void UpsertCanonicalRun(ExperimentRunCatalogSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var item = CreateCanonicalExperimentRunItem(summary);
        var existingIndex = -1;
        for (var index = 0; index < ExperimentRuns.Count; index++)
        {
            if (string.Equals(ExperimentRuns[index].Key, item.Key, StringComparison.Ordinal))
            {
                existingIndex = index;
                break;
            }
        }

        var wasSelected = selectedExperimentRun?.Key == item.Key;
        suppressSelectionNotification = wasSelected;
        try
        {
            if (existingIndex >= 0)
            {
                ExperimentRuns[existingIndex] = item;
            }
            else
            {
                var insertAt = ExperimentRuns.Count;
                for (var index = 0; index < ExperimentRuns.Count; index++)
                {
                    if (ExperimentRuns[index].StartedAt < item.StartedAt)
                    {
                        insertAt = index;
                        break;
                    }
                }

                ExperimentRuns.Insert(insertAt, item);
            }

            if (wasSelected)
            {
                SelectedExperimentRun = item;
                DataTools.ApplySelectedExperiment(item, publishBlockedStatus: false);
            }
        }
        finally
        {
            suppressSelectionNotification = false;
        }

        OnPropertyChanged(nameof(RunFilterSummary));
        ArchiveRetentionCandidatesCommand.RaiseCanExecuteChanged();
        DataTools.NotifyCatalogStateChanged(ExperimentRuns);
    }

    private async Task RefreshCatalogRunsAsync()
    {
        if (!catalogReady)
        {
            UpdateStatus("SQLite catalog 未准备好。", "unavailable");
            return;
        }

        try
        {
            var requestVersion = Interlocked.Increment(ref legacyRefreshVersion);
            var legacyRunsTask = Task.Run(LoadLegacyRecentRuns);
            var canonicalRunsTask = Task.Run(() => experimentCatalog.ListRunSummaries(RecentRunQueryLimit));
            await Task.WhenAll(legacyRunsTask, canonicalRunsTask).ConfigureAwait(true);
            if (requestVersion != Volatile.Read(ref legacyRefreshVersion))
            {
                return;
            }

            ReplaceRecentRuns(legacyRunsTask.Result, canonicalRunsTask.Result);
            await legacyReplayRefresh().ConfigureAwait(true);
            await RefreshDataRootStorageAsync(updateStatusMessage: false).ConfigureAwait(true);
            UpdateStatus($"已刷新实验数据库：{ExperimentRuns.Count} 个实验（含只读旧记录）。", "ready");
        }
        catch (Exception ex)
        {
            UpdateStatus($"刷新实验数据库失败：{ex.Message}", "error");
        }
    }

    private Task RefreshDataRootStorageAsync()
    {
        return RefreshDataRootStorageAsync(updateStatusMessage: true);
    }

    private async Task RefreshDataRootStorageAsync(bool updateStatusMessage)
    {
        try
        {
            var snapshot = await Task.Run(() => dataRootStorageService.Inspect(includeManagedSize: true))
                .ConfigureAwait(true);
            UpdateDataRootStorageSummary(snapshot);
            if (updateStatusMessage)
            {
                UpdateStatus(
                    snapshot.State == DataRootCapacityState.Unavailable
                        ? $"数据目录容量检查失败：{snapshot.ErrorMessage}"
                        : "数据目录容量已刷新。",
                    snapshot.State == DataRootCapacityState.Unavailable ? "error" : "ready");
            }
        }
        catch (Exception ex)
        {
            DataRootStorageSummary = $"数据目录容量：检查失败 · {ex.Message}";
            if (updateStatusMessage)
            {
                UpdateStatus($"数据目录容量检查失败：{ex.Message}", "error");
            }
        }
    }

    private void UpdateDataRootStorageSummary(DataRootStorageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = snapshot.State switch
        {
            DataRootCapacityState.Normal => "正常",
            DataRootCapacityState.Warning => "警告（可用空间低于 10 GiB）",
            DataRootCapacityState.Critical => "严重不足（低于 2 GiB 保底）",
            _ => "不可用"
        };
        var managed = snapshot.ManagedBytes is { } managedBytes
            ? $"目录占用 {FormatStorageBytes(managedBytes)}" +
              (snapshot.ManagedSizeComplete ? string.Empty : "（部分）")
            : "目录占用待扫描";
        var volume = snapshot.AvailableBytes is { } availableBytes && snapshot.TotalBytes is { } totalBytes
            ? $"磁盘可用 {FormatStorageBytes(availableBytes)} / {FormatStorageBytes(totalBytes)}"
            : "磁盘可用空间未知";
        var error = string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
            ? string.Empty
            : $" · {snapshot.ErrorMessage}";
        DataRootStorageSummary = $"{managed} · {volume} · {state}{error}";
    }

    private void OpenDataRoot()
    {
        OpenExistingDirectory(DataRootPath, "统一数据目录");
    }

    private void OpenSelectedExperimentDirectory()
    {
        if (selectedExperimentRun?.Run is not { } run)
        {
            UpdateStatus("请选择统一实验记录；旧版只读记录不提供规范实验目录入口。", "unavailable");
            return;
        }

        OpenExistingDirectory(dataLayout.ResolveArtifactPath(run.RunDirectory), "所选实验目录");
    }

    private bool CanOpenSelectedExperimentDirectory()
    {
        return selectedExperimentRun is { IsLegacy: false, Run: not null };
    }

    private bool CanArchiveSelectedExperiment()
    {
        return selectedExperimentRun?.Run is { } run &&
               !IsCatchUpRunning &&
               IsTerminalExperimentRun(run) &&
               string.Equals(run.LifecycleState, ExperimentCatalog.ActiveLifecycleState, StringComparison.Ordinal);
    }

    private bool CanDeleteSelectedExperiment()
    {
        return !IsCatchUpRunning &&
               selectedExperimentRun?.Run is { } run &&
               IsTerminalExperimentRun(run);
    }

    private bool CanReconcileSelectedExperiment()
    {
        return selectedExperimentRun is { IsLegacy: false, Run: { } run } &&
               IsTerminalExperimentRun(run);
    }

    private bool CanDeleteOfflineCompleteRevision()
    {
        return !IsCatchUpRunning &&
               selectedExperimentRun is { IsLegacy: false, Run: { } run } &&
               IsTerminalExperimentRun(run);
    }

    private bool CanArchiveRetentionCandidates()
    {
        return catalogReady &&
               !IsRetentionArchiveRunning &&
               !IsCatchUpRunning &&
               GetRetentionCandidates(DateTimeOffset.Now).Count > 0;
    }

    private IReadOnlyList<ExperimentRunRecord> GetRetentionCandidates(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-ExperimentDataLifecycleService.DefaultRetentionDays);
        return ExperimentRuns
            .Where(item => !item.IsLegacy && item.Run is not null)
            .Select(item => item.Run!)
            .Where(IsTerminalExperimentRun)
            .Where(run => string.Equals(
                run.LifecycleState,
                ExperimentCatalog.ActiveLifecycleState,
                StringComparison.Ordinal))
            .Where(run => (run.EndedAt ?? run.StartedAt) <= cutoff)
            .GroupBy(run => run.ExperimentRunId)
            .Select(group => group.First())
            .OrderBy(run => run.EndedAt ?? run.StartedAt)
            .ToArray();
    }

    private async Task ArchiveRetentionCandidatesAsync()
    {
        if (IsCatchUpRunning)
        {
            UpdateStatus(LifecycleCatchUpBusyMessage, "warning");
            return;
        }

        var candidates = GetRetentionCandidates(DateTimeOffset.Now);
        if (candidates.Count == 0)
        {
            UpdateStatus("没有超过保留建议期限且可归档的终态实验。", "idle");
            return;
        }

        var oldest = candidates.Min(run => run.EndedAt ?? run.StartedAt);
        var message =
            $"将归档 {candidates.Count} 个超过 {ExperimentDataLifecycleService.DefaultRetentionDays} 天的终态实验。\n" +
            $"最早记录：{oldest.LocalDateTime:yyyy-MM-dd HH:mm}\n\n" +
            "归档会移动完整实验目录并事务更新 catalog；仍可回放，也不会释放磁盘空间。";
        if (!lifecycleConfirmation("批量归档超期实验", message))
        {
            UpdateStatus("已取消批量归档。", "idle");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        retentionArchiveCancellation = cancellation;
        RetentionArchiveCompletedCount = 0;
        RetentionArchiveTotalCount = candidates.Count;
        RetentionArchiveProgressSummary = $"批量归档：准备 0 / {candidates.Count}。";
        IsRetentionArchiveRunning = true;
        var archived = new List<ExperimentArchiveResult>();
        var failures = new List<string>();
        var progressByRun = candidates
            .Select((run, index) => (IProgress<ExperimentArchiveProgress>)new Progress<ExperimentArchiveProgress>(
                progress => UpdateRetentionArchiveProgress(
                    cancellation,
                    index + 1,
                    candidates.Count,
                    run,
                    progress)))
            .ToArray();
        var canceled = false;
        try
        {
            try
            {
                await Task.Run(() =>
                {
                    for (var index = 0; index < candidates.Count; index++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var run = candidates[index];
                        try
                        {
                            archived.Add(lifecycleService.Archive(
                                run.ExperimentRunId,
                                DateTimeOffset.UtcNow,
                                cancellation.Token,
                                progressByRun[index]));
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{run.ExperimentRunId:D}: {ex.Message}");
                        }
                    }
                }).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                canceled = true;
            }

            ReplaceExperimentRunsFromCurrentSources();
            await RefreshDataRootStorageAsync(updateStatusMessage: false).ConfigureAwait(true);
            RetentionArchiveCompletedCount = archived.Count;
            if (canceled)
            {
                RetentionArchiveProgressSummary =
                    $"批量归档：已取消，完成 {archived.Count} / {candidates.Count}，失败 {failures.Count}。";
                UpdateStatus(RetentionArchiveProgressSummary, "warning");
            }
            else
            {
                RetentionArchiveProgressSummary = failures.Count == 0
                    ? $"批量归档：完成 {archived.Count} / {candidates.Count}。"
                    : $"批量归档：完成 {archived.Count} / {candidates.Count}，失败 {failures.Count}。";
                UpdateStatus(
                    failures.Count == 0
                        ? $"批量归档完成：{archived.Count} 个实验。"
                        : $"批量归档部分完成：成功 {archived.Count}，失败 {failures.Count}；{string.Join("；", failures.Take(3))}",
                    failures.Count == 0 ? "ready" : "warning");
            }
        }
        finally
        {
            if (ReferenceEquals(retentionArchiveCancellation, cancellation))
            {
                retentionArchiveCancellation = null;
            }

            IsRetentionArchiveRunning = false;
        }
    }

    private void CancelRetentionArchive()
    {
        retentionArchiveCancellation?.Cancel();
        RetentionArchiveProgressSummary =
            $"批量归档：正在取消，已完成 {RetentionArchiveCompletedCount} / {RetentionArchiveTotalCount}。";
    }

    private void UpdateRetentionArchiveProgress(
        CancellationTokenSource owner,
        int ordinal,
        int total,
        ExperimentRunRecord run,
        ExperimentArchiveProgress progress)
    {
        if (!IsRetentionArchiveRunning || !ReferenceEquals(retentionArchiveCancellation, owner))
        {
            return;
        }

        var phase = progress.Phase switch
        {
            ExperimentArchivePhase.Scanning =>
                $"扫描 {progress.FilesScanned} 个文件 / {FormatStorageBytes(progress.BytesScanned)}",
            ExperimentArchivePhase.Moving => "移动实验目录",
            ExperimentArchivePhase.CatalogCommit => "提交 catalog",
            ExperimentArchivePhase.Completed => "完成",
            _ => progress.Phase.ToString()
        };
        if (progress.Phase == ExperimentArchivePhase.Completed)
        {
            RetentionArchiveCompletedCount = Math.Max(RetentionArchiveCompletedCount, ordinal);
        }

        RetentionArchiveProgressSummary =
            $"批量归档：{ordinal} / {total} · {run.SetLabel} · {phase}";
    }

    private async Task ArchiveSelectedExperimentAsync()
    {
        if (IsCatchUpRunning)
        {
            UpdateStatus(LifecycleCatchUpBusyMessage, "warning");
            return;
        }

        if (selectedExperimentRun?.Run is not { } run)
        {
            UpdateStatus("请选择可归档的统一实验记录。", "unavailable");
            return;
        }

        var sourcePath = dataLayout.ResolveArtifactPath(run.RunDirectory);
        var message =
            $"实验 ID：{run.ExperimentRunId:D}\n" +
            $"当前目录：{sourcePath}\n\n" +
            "归档会将实验完整移动到 DataRoot/archives，并同步更新 catalog；" +
            "归档后仍可回放，但不会释放磁盘空间。是否继续？";
        if (!lifecycleConfirmation("归档实验", message))
        {
            UpdateStatus("已取消归档。", "idle");
            return;
        }

        try
        {
            var result = await Task.Run(() => lifecycleService.Archive(run.ExperimentRunId, DateTimeOffset.UtcNow))
                .ConfigureAwait(true);
            ReplaceExperimentRunsFromCurrentSources();
            await RefreshDataRootStorageAsync(updateStatusMessage: false).ConfigureAwait(true);
            UpdateStatus(
                $"归档完成：{run.ExperimentRunId:D} · {FormatStorageBytes(result.ManagedBytes)} · " +
                result.ArchiveDirectoryPath,
                "ready");
        }
        catch (Exception ex)
        {
            UpdateStatus(
                ex is ExperimentRunOperationConflictException
                    ? LifecycleCatchUpBusyMessage
                    : $"归档失败，原实验记录保持不变：{ex.Message}",
                ex is ExperimentRunOperationConflictException ? "warning" : "error");
        }
    }

    private async Task DeleteSelectedExperimentAsync()
    {
        if (IsCatchUpRunning)
        {
            UpdateStatus(LifecycleCatchUpBusyMessage, "warning");
            return;
        }

        if (selectedExperimentRun?.Run is not { } run)
        {
            UpdateStatus("请选择可删除的统一实验记录。", "unavailable");
            return;
        }

        var sourcePath = dataLayout.ResolveArtifactPath(run.RunDirectory);
        var message =
            $"实验 ID：{run.ExperimentRunId:D}\n" +
            $"数据目录：{sourcePath}\n\n" +
            "这会永久删除该实验的 raw、解调、重构、导出和 catalog 记录，操作不可恢复。是否继续？";
        if (!lifecycleConfirmation("永久删除实验", message))
        {
            UpdateStatus("已取消删除。", "idle");
            return;
        }

        try
        {
            UpdateStatus("正在停止该实验的回放与 ROI 读取…", "busy");
            await prepareExperimentDeletion(run.ExperimentRunId).ConfigureAwait(true);
            var result = await Task.Run(() => lifecycleService.Delete(run.ExperimentRunId)).ConfigureAwait(true);
            if (selectedExperimentRun?.ExperimentRunId == run.ExperimentRunId)
            {
                SelectedExperimentRun = null;
            }

            ReplaceExperimentRunsFromCurrentSources();
            await RefreshDataRootStorageAsync(updateStatusMessage: false).ConfigureAwait(true);
            UpdateStatus(
                result.CleanupComplete
                    ? $"实验已永久删除：{run.ExperimentRunId:D} · 释放约 {FormatStorageBytes(result.ManagedBytes)}。"
                    : $"catalog 已删除，但物理清理失败；数据保留于 {result.RecoveryDirectoryPath}：" +
                      result.CleanupErrorMessage,
                result.CleanupComplete ? "ready" : "warning");
        }
        catch (Exception ex)
        {
            if (selectedExperimentRun?.ExperimentRunId == run.ExperimentRunId)
            {
                SelectionChanged?.Invoke(selectedExperimentRun);
            }

            UpdateStatus(
                ex is ExperimentRunOperationConflictException
                    ? LifecycleCatchUpBusyMessage
                    : $"删除未完成：{ex.Message}。实验仍在列表中，可重试。",
                ex is ExperimentRunOperationConflictException ? "warning" : "error");
        }
    }

    private async Task RefreshSelectedExperimentStorageAsync(ExperimentRunListItem experiment)
    {
        var version = Interlocked.Increment(ref selectedStorageLoadVersion);
        if (experiment.Run is null)
        {
            SelectedExperimentStorageSummary = "旧版只读记录不参与统一保留策略。";
            return;
        }

        SelectedExperimentStorageSummary = "所选实验占用：扫描中…";
        try
        {
            var inspection = await Task.Run(() => lifecycleService.Inspect(
                    experiment.ExperimentRunId,
                    DateTimeOffset.UtcNow))
                .ConfigureAwait(true);
            if (version != Volatile.Read(ref selectedStorageLoadVersion) ||
                selectedExperimentRun?.ExperimentRunId != experiment.ExperimentRunId)
            {
                return;
            }

            var lifecycle = inspection.IsArchived
                ? $"已归档{(inspection.ArchivedAt is { } archivedAt ? $" {archivedAt.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty)}"
                : inspection.RetentionCandidate
                    ? $"超过 {ExperimentDataLifecycleService.DefaultRetentionDays} 天保留建议，可归档或显式删除"
                    : "保留中";
            var completeness = inspection.SizeComplete ? string.Empty : "（部分）";
            var error = string.IsNullOrWhiteSpace(inspection.ErrorMessage)
                ? string.Empty
                : $" · {inspection.ErrorMessage}";
            SelectedExperimentStorageSummary =
                $"所选实验占用 {FormatStorageBytes(inspection.ManagedBytes)}{completeness} · {lifecycle}{error}";
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref selectedStorageLoadVersion))
            {
                SelectedExperimentStorageSummary = $"所选实验占用检查失败：{ex.Message}";
            }
        }
    }

    private void ReconcileSelectedExperiment()
    {
        if (selectedExperimentRun?.Run is not { } run || !IsTerminalExperimentRun(run))
        {
            UpdateStatus("仅终态 catalog-v2 实验可以生成离线完整回放；录制中的实验必须先停止。", "unavailable");
            return;
        }

        var coverage = experimentCatalog.GetCoverage(run.ExperimentRunId);
        var preflight = runLifecycleController?.PreflightOfflineComplete(run.ExperimentRunId) ??
            new OfflineCompletePreflight(
                run.ExperimentRunId,
                true,
                "preflight delegated at execution",
                coverage.RawSampleRows,
                0,
                coverage.DemodReadyCount,
                0,
                (long)coverage.DemodReadyCount * 512L * 1024L,
                -1,
                null,
                null);
        var available = preflight.AvailableBytes < 0 ? "未知" : FormatStorageBytes(preflight.AvailableBytes);
        var message =
            $"将为 {run.SetLabel} 手动执行完整离线链：先补齐解调，再按原始时间顺序使用已记录权重策略和独立动态 Kalman 重算。\n\n" +
            $"原始数据：{preflight.RawSampleRows:N0} 行 / {FormatStorageBytes(preflight.RawArtifactBytes)}\n" +
            $"当前解调块：{preflight.DemodBlockCount:N0}\n" +
            $"预计新增：{FormatStorageBytes(preflight.EstimatedIncrementalBytes)}；磁盘可用：{available}\n" +
            (preflight.ResumableRevisionId is null
                ? "将创建新的暂存 revision。"
                : $"将继续暂存 revision：{preflight.ResumableRevisionId}") +
            "\n\n中途取消不会覆盖实时回放，也不会发布不完整结果。是否继续？";
        if (!lifecycleConfirmation("生成离线完整回放", message))
        {
            return;
        }

        reconciliationScheduler(run.ExperimentRunId, run.SetLabel, "operator-request");
        UpdateStatus($"{run.SetLabel} 已进入离线完整重算队列：暂存完成并校验后才会发布。", "processing");
    }

    private async Task DeleteOfflineCompleteRevisionAsync()
    {
        if (IsCatchUpRunning)
        {
            UpdateStatus(LifecycleCatchUpBusyMessage, "warning");
            return;
        }

        if (selectedExperimentRun?.Run is not { } run || !IsTerminalExperimentRun(run))
        {
            UpdateStatus("请选择包含离线 revision 的终态统一实验。", "unavailable");
            return;
        }

        var revision = await Task.Run(() =>
                experimentCatalog.GetPublishedReconstructionRevision(
                    run.ExperimentRunId,
                    ReconstructionLane.OfflineComplete) ??
                experimentCatalog.ListReconstructionRevisions(
                        run.ExperimentRunId,
                        ReconstructionLane.OfflineComplete)
                    .OrderByDescending(item => item.UpdatedAt)
                    .FirstOrDefault())
            .ConfigureAwait(true);
        if (revision is null)
        {
            UpdateStatus($"{run.SetLabel} 没有可删除的离线 revision。", "unavailable");
            return;
        }

        var message =
            $"实验：{run.SetLabel}\n离线 revision：{revision.RevisionId}\n状态：{revision.Status}\n\n" +
            "仅删除这个离线 revision 的重构结果、展示参数和索引；raw、解调数据、实时回放及其他 revision 均保持不变。是否继续？";
        if (!lifecycleConfirmation("删除离线版本", message))
        {
            UpdateStatus("已取消删除离线版本。", "idle");
            return;
        }

        try
        {
            var report = await Task.Run(() =>
                    RunLifecycleController.DeleteOfflineCompleteRevision(
                        run.ExperimentRunId,
                        revision.RevisionId))
                .ConfigureAwait(true);
            ReplaceExperimentRunsFromCurrentSources();
            SelectionChanged?.Invoke(SelectedExperimentRun);
            await RefreshDataRootStorageAsync(updateStatusMessage: false).ConfigureAwait(true);
            UpdateStatus(
                report.CleanupComplete
                    ? $"离线版本已删除：{revision.RevisionId}；实时回放和共享原始数据未改变。"
                    : $"离线版本索引已删除，但残留文件保留于 {report.RecoveryDirectory}：{report.CleanupError}",
                report.CleanupComplete ? "ready" : "warning");
        }
        catch (Exception ex)
        {
            UpdateStatus(
                ex is ExperimentRunOperationConflictException
                    ? LifecycleCatchUpBusyMessage
                    : $"删除离线版本失败；原 revision 保持不变：{ex.Message}",
                ex is ExperimentRunOperationConflictException ? "warning" : "error");
        }
    }

    private IReadOnlyList<EitCatalogRunSummary> LoadLegacyRecentRuns()
    {
        if (legacyRunLoader is not null)
        {
            return legacyRunLoader();
        }

        var runs = new List<EitCatalogRunSummary>();
        foreach (var reader in legacyCatalogReaders)
        {
            try
            {
                runs.AddRange(reader.ListRecentRuns(RecentRunQueryLimit));
            }
            catch (Exception ex)
            {
                DiagnosticMessage?.Invoke($"legacy catalog read skipped path={reader.DatabasePath}: {ex.Message}");
            }
        }

        return runs
            .GroupBy(run => run.RunId)
            .Select(group => group.OrderByDescending(run => run.CapturedAt).First())
            .OrderByDescending(run => run.CapturedAt)
            .ThenBy(run => run.RunId)
            .Take(RecentRunQueryLimit)
            .ToArray();
    }

    private static IReadOnlyList<EitCatalog> CreateLegacyCatalogReaders(DataRootLayout layout)
    {
        return new[] { layout.CatalogPath, layout.LegacyCatalogPath }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new EitCatalog(path))
            .ToArray();
    }

    private void ReplaceRecentRuns(
        IEnumerable<EitCatalogRunSummary> runs,
        IReadOnlyList<ExperimentRunCatalogSummary> canonicalRuns)
    {
        RecentRuns.Clear();
        foreach (var run in runs)
        {
            RecentRuns.Add(new CatalogRunSummaryItem(run));
        }

        recentRunsView?.Refresh();
        ReplaceExperimentRunsFromCurrentSources(canonicalRuns);
        OnPropertyChanged(nameof(RunFilterSummary));
    }

    private void ReplaceExperimentRunsFromCurrentSources()
    {
        if (!catalogReady)
        {
            return;
        }

        ReplaceExperimentRunsFromCurrentSources(experimentCatalog.ListRunSummaries(RecentRunQueryLimit));
    }

    private void ReplaceExperimentRunsFromCurrentSources(
        IReadOnlyList<ExperimentRunCatalogSummary> canonicalRuns)
    {
        if (!catalogReady)
        {
            return;
        }

        var previousKey = selectedExperimentRun?.Key;
        var imagingById = imagingRuns
            .GroupBy(item => item.Summary.ImagingRunId)
            .ToDictionary(group => group.Key, group => group.First());
        var canonicalIds = canonicalRuns.Select(summary => summary.Run.ExperimentRunId).ToHashSet();
        var usedImagingIds = new HashSet<Guid>();
        var items = new List<ExperimentRunListItem>();

        items.AddRange(canonicalRuns.Select(CreateCanonicalExperimentRunItem));
        foreach (var raw in RecentRuns.Where(item => !canonicalIds.Contains(item.Summary.RunId)))
        {
            imagingById.TryGetValue(raw.Summary.RunId, out var imaging);
            if (imaging is not null)
            {
                usedImagingIds.Add(imaging.Summary.ImagingRunId);
            }

            items.Add(ExperimentRunListItem.CreateLegacy(
                raw,
                imaging,
                imaging is null ? null : imagingRunStorePaths.GetValueOrDefault(imaging.Summary.ImagingRunId)));
        }

        foreach (var imaging in imagingRuns.Where(item =>
                     !canonicalIds.Contains(item.Summary.ImagingRunId) &&
                     !usedImagingIds.Contains(item.Summary.ImagingRunId)))
        {
            items.Add(ExperimentRunListItem.CreateLegacy(
                null,
                imaging,
                imagingRunStorePaths.GetValueOrDefault(imaging.Summary.ImagingRunId)));
        }

        ExperimentRuns.Clear();
        foreach (var item in items
                     .OrderByDescending(item => item.StartedAt)
                     .ThenBy(item => item.SetLabel, StringComparer.OrdinalIgnoreCase)
                     .Take(RecentRunQueryLimit))
        {
            ExperimentRuns.Add(item);
        }

        experimentRunsView?.Refresh();
        OnPropertyChanged(nameof(RunFilterSummary));
        ArchiveRetentionCandidatesCommand.RaiseCanExecuteChanged();
        if (previousKey is not null)
        {
            suppressSelectionNotification = true;
            try
            {
                SelectedExperimentRun = ExperimentRuns.FirstOrDefault(item => item.Key == previousKey);
                DataTools.ApplySelectedExperiment(
                    SelectedExperimentRun,
                    publishBlockedStatus: false);
            }
            finally
            {
                suppressSelectionNotification = false;
            }
        }

        DataTools.NotifyCatalogStateChanged(ExperimentRuns);
    }

    private ExperimentRunListItem CreateCanonicalExperimentRunItem(ExperimentRunCatalogSummary summary)
    {
        return ExperimentRunListItem.CreateCanonical(
            summary.Run,
            summary.Coverage,
            summary.PrimaryRawArtifactPath is null
                ? null
                : dataLayout.ResolveArtifactPath(summary.PrimaryRawArtifactPath),
            dataLayout.ResolveArtifactPath(summary.Run.RunDirectory));
    }

    private ICollectionView CreateExperimentRunsView()
    {
        var view = CollectionViewSource.GetDefaultView(ExperimentRuns);
        view.Filter = item => item is not ExperimentRunListItem run ||
                              selectedRunDate is null ||
                              IsExperimentOnSelectedDate(run);
        return view;
    }

    private bool IsExperimentOnSelectedDate(ExperimentRunListItem run)
    {
        return selectedRunDate is { } date && run.StartedAt.LocalDateTime.Date == date.Date;
    }

    private ICollectionView CreateRecentRunsView()
    {
        var view = CollectionViewSource.GetDefaultView(RecentRuns);
        view.Filter = item => item is not CatalogRunSummaryItem run ||
                              selectedRunDate is null ||
                              run.Summary.CapturedAt.LocalDateTime.Date == selectedRunDate.Value.Date;
        return view;
    }

    private void OpenExistingDirectory(string directory, string description)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
            if (!Directory.Exists(fullPath))
            {
                UpdateStatus($"{description}不存在：{fullPath}", "unavailable");
                return;
            }

            directoryOpener(fullPath);
            UpdateStatus($"已打开{description}：{fullPath}", "ready");
        }
        catch (Exception ex)
        {
            UpdateStatus($"打开{description}失败：{ex.Message}", "error");
        }
    }

    private void RefreshSelectionCommands()
    {
        ReconcileSelectedExperimentCommand.RaiseCanExecuteChanged();
        DeleteOfflineCompleteRevisionCommand.RaiseCanExecuteChanged();
        OpenSelectedExperimentDirectoryCommand.RaiseCanExecuteChanged();
        ArchiveSelectedExperimentCommand.RaiseCanExecuteChanged();
        DeleteSelectedExperimentCommand.RaiseCanExecuteChanged();
        ArchiveRetentionCandidatesCommand.RaiseCanExecuteChanged();
    }

    private void UpdateStatus(string status, string state)
    {
        PublishStatus(state, status, DateTimeOffset.Now);
        StatusChanged?.Invoke(status);
    }

    private static bool IsTerminalExperimentRun(ExperimentRunRecord run)
    {
        return ExperimentRunListItem.IsTerminalStatus(run.Status);
    }

    private static string FormatStorageBytes(long bytes)
    {
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var display = (double)Math.Max(0, bytes);
        var unitIndex = 0;
        while (display >= 1024.0 && unitIndex < units.Length - 1)
        {
            display /= 1024.0;
            unitIndex++;
        }

        return $"{display:F1} {units[unitIndex]}";
    }
}
