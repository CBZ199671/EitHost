using System.Collections.ObjectModel;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

public sealed class RealtimeWorkspaceViewModel
    : WorkspaceViewModelBase, IRealtimeWorkspaceViewModel
{
    private readonly Dictionary<string, RealtimeRunSnapshot> snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReferenceReconstructionSnapshot> referenceSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private RealtimeSessionController? sessionController;
    private RealtimeBackendController? backendController;
    private RealtimeDerivedPersistenceController? derivedPersistenceController;
    private RealtimeAcquisitionLoopController? acquisitionLoopController;
    private RealtimeReconstructionController? reconstructionController;
    private RealtimeTimingGateController? timingGateController;
    private RealtimeBlockConsumerController? blockConsumerController;
    private RealtimeContactDiagnosticController? contactDiagnosticController;
    private RealtimeContactCalibrationController? contactCalibrationController;
    private RealtimeReferenceLifecycleController? referenceLifecycleController;
    private RealtimeReferenceActionController? referenceActionController;
    private RealtimeTemporalAnalysisController? temporalAnalysisController;
    private RealtimeRunCommandController? runCommandController;
    private RealtimeCalibrationArtifactController? calibrationArtifactController;
    private RealtimeReferenceWindowOption? selectedReferenceWindowOption;
    private string referenceWindowPreview = "自动参考：累计 100 个质量合格帧后，主按钮将综合点击前最近同工况连续段的全部有效帧；无需等待对象静止。";
    private string referenceRelockStateText = "重锁：未启动；当前参考持续用于成像与 ROI。";
    private string synchronizedReferenceSummary = "多集合同步：至少两个实时集合运行后可准备。";

    public RealtimeWorkspaceViewModel()
        : base("realtime")
    {
    }

    public ObservableCollection<RealtimeRunSnapshot> RunSnapshots { get; } = [];

    public ObservableCollection<ReferenceReconstructionSnapshot> ReferenceSnapshots { get; } = [];

    public ObservableCollection<RealtimeReferenceWindowOption> ReferenceWindowOptions { get; } = [];

    public RealtimeReferenceWindowOption? SelectedReferenceWindowOption
    {
        get => selectedReferenceWindowOption;
        set
        {
            if (SetProperty(ref selectedReferenceWindowOption, value))
            {
                referenceActionController?.OnSelectedWindowChanged(value);
            }
        }
    }

    public string ReferenceWindowPreview
    {
        get => referenceWindowPreview;
        internal set => SetProperty(ref referenceWindowPreview, value);
    }

    public string ReferenceRelockStateText
    {
        get => referenceRelockStateText;
        internal set => SetProperty(ref referenceRelockStateText, value);
    }

    public string SynchronizedReferenceSummary
    {
        get => synchronizedReferenceSummary;
        internal set => SetProperty(ref synchronizedReferenceSummary, value);
    }

    internal RealtimeSessionController SessionController =>
        sessionController ?? throw new InvalidOperationException("Realtime session controller has not been attached.");

    internal RealtimeBackendController BackendController =>
        backendController ?? throw new InvalidOperationException("Realtime backend controller has not been attached.");

    internal RealtimeDerivedPersistenceController DerivedPersistenceController =>
        derivedPersistenceController ?? throw new InvalidOperationException("Realtime derived persistence controller has not been attached.");

    internal RealtimeAcquisitionLoopController AcquisitionLoopController =>
        acquisitionLoopController ?? throw new InvalidOperationException("Realtime acquisition loop controller has not been attached.");

    internal RealtimeReconstructionController ReconstructionController =>
        reconstructionController ?? throw new InvalidOperationException("Realtime reconstruction controller has not been attached.");

    internal RealtimeTimingGateController TimingGateController =>
        timingGateController ?? throw new InvalidOperationException("Realtime timing gate controller has not been attached.");

    internal RealtimeBlockConsumerController BlockConsumerController =>
        blockConsumerController ?? throw new InvalidOperationException("Realtime block consumer controller has not been attached.");

    internal RealtimeContactDiagnosticController ContactDiagnosticController =>
        contactDiagnosticController ?? throw new InvalidOperationException("Realtime contact diagnostic controller has not been attached.");

    internal RealtimeContactCalibrationController ContactCalibrationController =>
        contactCalibrationController ?? throw new InvalidOperationException("Realtime contact calibration controller has not been attached.");

    internal RealtimeReferenceLifecycleController ReferenceLifecycleController =>
        referenceLifecycleController ?? throw new InvalidOperationException("Realtime reference lifecycle controller has not been attached.");

    internal RealtimeReferenceActionController ReferenceActionController =>
        referenceActionController ?? throw new InvalidOperationException("Realtime reference action controller has not been attached.");

    internal RealtimeTemporalAnalysisController TemporalAnalysisController =>
        temporalAnalysisController ?? throw new InvalidOperationException("Realtime temporal analysis controller has not been attached.");

    internal RealtimeRunCommandController RunCommandController =>
        runCommandController ?? throw new InvalidOperationException("Realtime run command controller has not been attached.");

    internal RealtimeCalibrationArtifactController CalibrationArtifactController =>
        calibrationArtifactController ?? throw new InvalidOperationException("Realtime calibration artifact controller has not been attached.");

    internal void AttachSessionController(RealtimeSessionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (sessionController is not null)
        {
            throw new InvalidOperationException("Realtime session controller is already attached.");
        }

        sessionController = controller;
    }

    internal void AttachBackendController(RealtimeBackendController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (backendController is not null)
        {
            throw new InvalidOperationException("Realtime backend controller is already attached.");
        }

        backendController = controller;
    }

    internal void AttachDerivedPersistenceController(RealtimeDerivedPersistenceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (derivedPersistenceController is not null)
        {
            throw new InvalidOperationException("Realtime derived persistence controller is already attached.");
        }

        derivedPersistenceController = controller;
    }

    internal void AttachAcquisitionLoopController(RealtimeAcquisitionLoopController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (acquisitionLoopController is not null)
        {
            throw new InvalidOperationException("Realtime acquisition loop controller is already attached.");
        }

        acquisitionLoopController = controller;
    }

    internal void AttachReconstructionController(RealtimeReconstructionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (reconstructionController is not null)
        {
            throw new InvalidOperationException("Realtime reconstruction controller is already attached.");
        }

        reconstructionController = controller;
    }

    internal void AttachTimingGateController(RealtimeTimingGateController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (timingGateController is not null)
        {
            throw new InvalidOperationException("Realtime timing gate controller is already attached.");
        }

        timingGateController = controller;
    }

    internal void AttachBlockConsumerController(RealtimeBlockConsumerController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (blockConsumerController is not null)
        {
            throw new InvalidOperationException("Realtime block consumer controller is already attached.");
        }

        blockConsumerController = controller;
    }

    internal void AttachContactDiagnosticController(RealtimeContactDiagnosticController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (contactDiagnosticController is not null)
        {
            throw new InvalidOperationException("Realtime contact diagnostic controller is already attached.");
        }

        contactDiagnosticController = controller;
    }

    internal void AttachContactCalibrationController(RealtimeContactCalibrationController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (contactCalibrationController is not null)
        {
            throw new InvalidOperationException("Realtime contact calibration controller is already attached.");
        }

        contactCalibrationController = controller;
    }

    internal void AttachReferenceLifecycleController(RealtimeReferenceLifecycleController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (referenceLifecycleController is not null)
        {
            throw new InvalidOperationException("Realtime reference lifecycle controller is already attached.");
        }

        referenceLifecycleController = controller;
    }

    internal void AttachReferenceActionController(RealtimeReferenceActionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (referenceActionController is not null)
        {
            throw new InvalidOperationException("Realtime reference action controller is already attached.");
        }

        referenceActionController = controller;
    }

    internal void AttachTemporalAnalysisController(RealtimeTemporalAnalysisController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (temporalAnalysisController is not null)
        {
            throw new InvalidOperationException("Realtime temporal analysis controller is already attached.");
        }

        temporalAnalysisController = controller;
    }

    internal void AttachRunCommandController(RealtimeRunCommandController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (runCommandController is not null)
        {
            throw new InvalidOperationException("Realtime run command controller is already attached.");
        }

        runCommandController = controller;
    }

    internal void AttachCalibrationArtifactController(RealtimeCalibrationArtifactController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (calibrationArtifactController is not null)
        {
            throw new InvalidOperationException("Realtime calibration artifact controller is already attached.");
        }

        calibrationArtifactController = controller;
    }

    public void ApplyRunSnapshot(RealtimeRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshots[snapshot.SetLabel] = snapshot;
        var existingIndex = -1;
        for (var index = 0; index < RunSnapshots.Count; index++)
        {
            if (string.Equals(RunSnapshots[index].SetLabel, snapshot.SetLabel, StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            RunSnapshots[existingIndex] = snapshot;
        }
        else
        {
            RunSnapshots.Add(snapshot);
        }

        var activeCount = snapshots.Values.Count(item => item.IsActive);
        var failedCount = snapshots.Values.Count(item => item.Phase == RealtimeRunPhase.Faulted);
        var state = failedCount > 0
            ? "error"
            : activeCount > 0
                ? "running"
                : "idle";
        var status = activeCount > 0
            ? $"{activeCount} 套实时实验运行中"
            : failedCount > 0
                ? $"{failedCount} 套实时实验异常结束"
                : "实时实验空闲";
        PublishStatus(state, status, DateTimeOffset.Now);
    }

    public void ApplyReferenceSnapshot(ReferenceReconstructionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        referenceSnapshots[snapshot.SetLabel] = snapshot;
        var existingIndex = -1;
        for (var index = 0; index < ReferenceSnapshots.Count; index++)
        {
            if (string.Equals(
                    ReferenceSnapshots[index].SetLabel,
                    snapshot.SetLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            ReferenceSnapshots[existingIndex] = snapshot;
        }
        else
        {
            ReferenceSnapshots.Add(snapshot);
        }
    }
}
