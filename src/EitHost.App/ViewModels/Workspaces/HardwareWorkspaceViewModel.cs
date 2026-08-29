using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using EitHost.Core.Application.Hardware;

namespace EitHost.App.ViewModels.Workspaces;

public sealed class HardwareWorkspaceViewModel
    : WorkspaceViewModelBase, IHardwareWorkspaceViewModel
{
    private string pairingLabel = "EIT-01";
    private int pairingUsb2070DeviceNumber;
    private DeviceCandidateOption? selectedUsb2070Candidate;
    private DeviceCandidateOption? selectedDdsCandidate;
    private PairingSummaryItem? selectedBoundPairing;
    private PairingSummaryItem? selectedRealtimeDisplayPairing;
    private HardwareWorkspaceSnapshot stateSnapshot = HardwareWorkspaceSnapshot.Empty;
    private AcquisitionSessionController? acquisitionController;
    private DdsRunController? ddsRunController;
    private HardwareEvidenceController? evidenceController;
    private HardwareDiscoveryController? discoveryController;
    private HardwareRunCommandController? runCommandController;
    private RealtimePairingRecoveryController? pairingRecoveryController;
    private DeviceRunParameterEditor? runParameterEditor;
    private string hardwareSmokeReportPath = string.Empty;
    private string hardwareSmokeSummary = "尚未生成硬件报告。";
    private string t25SmokePlanPath = string.Empty;
    private string t25SmokePlanSummary = "尚未生成 T25 验收计划。";
    private string pairingManifestPath = string.Empty;
    private string pairingManifestSummary = "尚未导出配对清单。";
    private string evidenceIndexPath = string.Empty;
    private string evidenceIndexSummary = "尚未导出会话证据索引。";
    private string fieldSnapshotPath = string.Empty;
    private string fieldSnapshotSummary = "尚未导出现场快照。";

    public HardwareWorkspaceViewModel()
        : base("hardware")
    {
        BoundPairings.CollectionChanged += OnBoundPairingsChanged;
        PendingUsb2070Candidates.CollectionChanged += (_, _) => PublishStateSnapshot();
        PendingDdsCandidates.CollectionChanged += (_, _) => PublishStateSnapshot();
    }

    public event Action? PairingInputChanged;

    public event Action<PairingSummaryItem?>? SelectedBoundPairingChanging;

    public event Action<PairingSummaryItem?>? SelectedBoundPairingChanged;

    public event Action<PairingSummaryItem?>? SelectedRealtimeDisplayPairingChanged;

    public event Action<HardwareWorkspaceSnapshot>? StateChanged;

    public HardwareWorkspaceSnapshot StateSnapshot
    {
        get => stateSnapshot;
        private set => SetProperty(ref stateSnapshot, value);
    }

    public string PairingLabel
    {
        get => pairingLabel;
        set
        {
            if (SetProperty(ref pairingLabel, value))
            {
                PairingInputChanged?.Invoke();
            }
        }
    }

    public int PairingUsb2070DeviceNumber
    {
        get => pairingUsb2070DeviceNumber;
        set
        {
            if (SetProperty(ref pairingUsb2070DeviceNumber, value))
            {
                PairingInputChanged?.Invoke();
            }
        }
    }

    public DeviceCandidateOption? SelectedUsb2070Candidate
    {
        get => selectedUsb2070Candidate;
        set
        {
            if (SetProperty(ref selectedUsb2070Candidate, value))
            {
                PairingInputChanged?.Invoke();
            }
        }
    }

    public DeviceCandidateOption? SelectedDdsCandidate
    {
        get => selectedDdsCandidate;
        set
        {
            if (SetProperty(ref selectedDdsCandidate, value))
            {
                PairingInputChanged?.Invoke();
            }
        }
    }

    public PairingSummaryItem? SelectedBoundPairing
    {
        get => selectedBoundPairing;
        set
        {
            SelectedBoundPairingChanging?.Invoke(value);
            if (ReferenceEquals(selectedBoundPairing, value))
            {
                return;
            }

            if (SetProperty(ref selectedBoundPairing, value))
            {
                SelectedBoundPairingChanged?.Invoke(value);
                PublishStateSnapshot();
            }
        }
    }

    public PairingSummaryItem? SelectedRealtimeDisplayPairing
    {
        get => selectedRealtimeDisplayPairing;
        set
        {
            if (SetProperty(ref selectedRealtimeDisplayPairing, value))
            {
                SelectedRealtimeDisplayPairingChanged?.Invoke(value);
                PublishStateSnapshot();
            }
        }
    }

    public ObservableCollection<string> WorkflowSteps { get; } =
    [
        "1. 打开软件并记录当前基线",
        "2. 插入一套 USB2070 + DDS 串口硬件",
        "3. 扫描新增候选并手动绑定标签",
        "4. 重复插入与绑定下一套设备",
        "5. 本次关闭后下次重新配对"
    ];

    public ObservableCollection<DeviceCandidateOption> PendingUsb2070Candidates { get; } = [];

    public ObservableCollection<DeviceCandidateOption> PendingDdsCandidates { get; } = [];

    public ObservableCollection<PairingSummaryItem> BoundPairings { get; } = [];

    public ObservableCollection<string> DdsCommandLogs { get; } = [];

    public ObservableCollection<string> AcquisitionLogs { get; } = [];

    public ObservableCollection<string> HardwareSmokeLogs { get; } = [];

    public ObservableCollection<string> T25SmokePlanLogs { get; } = [];

    public ObservableCollection<string> PairingManifestLogs { get; } = [];

    public ObservableCollection<string> EvidenceIndexLogs { get; } = [];

    public ObservableCollection<string> FieldSnapshotLogs { get; } = [];

    public string HardwareSmokeReportPath
    {
        get => hardwareSmokeReportPath;
        internal set => SetProperty(ref hardwareSmokeReportPath, value);
    }

    public string HardwareSmokeSummary
    {
        get => hardwareSmokeSummary;
        internal set => SetProperty(ref hardwareSmokeSummary, value);
    }

    public string T25SmokePlanPath
    {
        get => t25SmokePlanPath;
        internal set => SetProperty(ref t25SmokePlanPath, value);
    }

    public string T25SmokePlanSummary
    {
        get => t25SmokePlanSummary;
        internal set => SetProperty(ref t25SmokePlanSummary, value);
    }

    public string PairingManifestPath
    {
        get => pairingManifestPath;
        internal set => SetProperty(ref pairingManifestPath, value);
    }

    public string PairingManifestSummary
    {
        get => pairingManifestSummary;
        internal set => SetProperty(ref pairingManifestSummary, value);
    }

    public string EvidenceIndexPath
    {
        get => evidenceIndexPath;
        internal set => SetProperty(ref evidenceIndexPath, value);
    }

    public string EvidenceIndexSummary
    {
        get => evidenceIndexSummary;
        internal set => SetProperty(ref evidenceIndexSummary, value);
    }

    public string FieldSnapshotPath
    {
        get => fieldSnapshotPath;
        internal set => SetProperty(ref fieldSnapshotPath, value);
    }

    public string FieldSnapshotSummary
    {
        get => fieldSnapshotSummary;
        internal set => SetProperty(ref fieldSnapshotSummary, value);
    }

    public AsyncRelayCommand InitializeBaselineCommand { get; private set; } = null!;

    public AsyncRelayCommand DetectNewDevicesCommand { get; private set; } = null!;

    public AsyncRelayCommand ScanUsb2070NumbersCommand { get; private set; } = null!;

    public AsyncRelayCommand GenerateHardwareSmokeReportCommand { get; private set; } = null!;

    public AsyncRelayCommand GenerateT25SmokePlanCommand { get; private set; } = null!;

    public AsyncRelayCommand ExportPairingManifestCommand { get; private set; } = null!;

    public AsyncRelayCommand ExportEvidenceIndexCommand { get; private set; } = null!;

    public AsyncRelayCommand ExportFieldSnapshotCommand { get; private set; } = null!;

    public RelayCommand InstallUsb2070DriverCommand { get; private set; } = null!;

    public AsyncRelayCommand BindSelectedDevicesCommand { get; private set; } = null!;

    public AsyncRelayCommand SetDacCommand { get; private set; } = null!;

    public AsyncRelayCommand StopDacCommand { get; private set; } = null!;

    public AsyncRelayCommand SetPgaCommand { get; private set; } = null!;

    public AsyncRelayCommand StartExcitationCommand { get; private set; } = null!;

    public AsyncRelayCommand StopExcitationCommand { get; private set; } = null!;

    public AsyncRelayCommand StartAcquisitionCommand { get; private set; } = null!;

    public AsyncRelayCommand ReadAcquisitionBlockCommand { get; private set; } = null!;

    public AsyncRelayCommand ReadAllActiveAcquisitionBlocksCommand { get; private set; } = null!;

    public AsyncRelayCommand StopAcquisitionCommand { get; private set; } = null!;

    public AsyncRelayCommand SyncStartCommand { get; private set; } = null!;

    public AsyncRelayCommand StopAllDevicesCommand { get; private set; } = null!;

    internal AcquisitionSessionController AcquisitionController =>
        acquisitionController ?? throw new InvalidOperationException("Acquisition controller has not been attached.");

    internal DdsRunController DdsRunController =>
        ddsRunController ?? throw new InvalidOperationException("DDS run controller has not been attached.");

    internal HardwareEvidenceController EvidenceController =>
        evidenceController ?? throw new InvalidOperationException("Hardware evidence controller has not been attached.");

    internal HardwareDiscoveryController DiscoveryController =>
        discoveryController ?? throw new InvalidOperationException("Hardware discovery controller has not been attached.");

    internal HardwareRunCommandController RunCommandController =>
        runCommandController ?? throw new InvalidOperationException("Hardware run command controller has not been attached.");

    internal RealtimePairingRecoveryController PairingRecoveryController =>
        pairingRecoveryController ?? throw new InvalidOperationException("Realtime pairing recovery controller has not been attached.");

    internal DeviceRunParameterEditor RunParameterEditor =>
        runParameterEditor ?? throw new InvalidOperationException("Device run parameter editor has not been attached.");

    internal void AttachAcquisitionController(AcquisitionSessionController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (acquisitionController is not null)
        {
            throw new InvalidOperationException("Acquisition controller is already attached.");
        }

        acquisitionController = controller;
    }

    internal void AttachDdsRunController(DdsRunController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (ddsRunController is not null)
        {
            throw new InvalidOperationException("DDS run controller is already attached.");
        }

        ddsRunController = controller;
    }

    internal void AttachEvidenceController(HardwareEvidenceController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (evidenceController is not null)
        {
            throw new InvalidOperationException("Hardware evidence controller is already attached.");
        }

        evidenceController = controller;
    }

    internal void AttachDiscoveryController(HardwareDiscoveryController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (discoveryController is not null)
        {
            throw new InvalidOperationException("Hardware discovery controller is already attached.");
        }

        discoveryController = controller;
    }

    internal void AttachRunCommandController(HardwareRunCommandController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (runCommandController is not null)
        {
            throw new InvalidOperationException("Hardware run command controller is already attached.");
        }

        runCommandController = controller;
    }

    internal void AttachPairingRecoveryController(RealtimePairingRecoveryController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (pairingRecoveryController is not null)
        {
            throw new InvalidOperationException("Realtime pairing recovery controller is already attached.");
        }

        pairingRecoveryController = controller;
    }

    internal void AttachRunParameterEditor(DeviceRunParameterEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (runParameterEditor is not null)
        {
            throw new InvalidOperationException("Device run parameter editor is already attached.");
        }

        runParameterEditor = editor;
    }

    internal void ConfigureCommands(HardwareWorkspaceCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        InitializeBaselineCommand = commands.InitializeBaseline;
        DetectNewDevicesCommand = commands.DetectNewDevices;
        ScanUsb2070NumbersCommand = commands.ScanUsb2070Numbers;
        GenerateHardwareSmokeReportCommand = commands.GenerateHardwareSmokeReport;
        GenerateT25SmokePlanCommand = commands.GenerateT25SmokePlan;
        ExportPairingManifestCommand = commands.ExportPairingManifest;
        ExportEvidenceIndexCommand = commands.ExportEvidenceIndex;
        ExportFieldSnapshotCommand = commands.ExportFieldSnapshot;
        InstallUsb2070DriverCommand = commands.InstallUsb2070Driver;
        BindSelectedDevicesCommand = commands.BindSelectedDevices;
        SetDacCommand = commands.SetDac;
        StopDacCommand = commands.StopDac;
        SetPgaCommand = commands.SetPga;
        StartExcitationCommand = commands.StartExcitation;
        StopExcitationCommand = commands.StopExcitation;
        StartAcquisitionCommand = commands.StartAcquisition;
        ReadAcquisitionBlockCommand = commands.ReadAcquisitionBlock;
        ReadAllActiveAcquisitionBlocksCommand = commands.ReadAllActiveAcquisitionBlocks;
        StopAcquisitionCommand = commands.StopAcquisition;
        SyncStartCommand = commands.SyncStart;
        StopAllDevicesCommand = commands.StopAllDevices;
        OnPropertyChanged(string.Empty);
    }

    public void RefreshStateSnapshot()
    {
        PublishStateSnapshot();
    }

    private void OnBoundPairingsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.OldItems is not null)
        {
            foreach (PairingSummaryItem item in args.OldItems)
            {
                item.PropertyChanged -= OnPairingPropertyChanged;
            }
        }

        if (args.NewItems is not null)
        {
            foreach (PairingSummaryItem item in args.NewItems)
            {
                item.PropertyChanged += OnPairingPropertyChanged;
            }
        }

        PublishStateSnapshot();
    }

    private void OnPairingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        PublishStateSnapshot();
    }

    private void PublishStateSnapshot()
    {
        var next = new HardwareWorkspaceSnapshot(
            BoundPairings.Select(item => new HardwareSetSnapshot(
                item.Title,
                item.Pairing.Usb2070DeviceNumber,
                item.IsExciting,
                item.IsAcquiring)).ToArray(),
            SelectedBoundPairing?.Title,
            SelectedRealtimeDisplayPairing?.Title,
            PendingUsb2070Candidates.Count,
            PendingDdsCandidates.Count,
            StateSnapshot.Revision + 1);
        StateSnapshot = next;
        var activeCount = next.Sets.Count(item => item.IsExciting || item.IsAcquiring);
        PublishStatus(
            activeCount > 0 ? "running" : "idle",
            activeCount > 0 ? $"{activeCount} 套硬件运行中" : $"已绑定 {next.Sets.Count} 套硬件",
            DateTimeOffset.Now);
        StateChanged?.Invoke(next);
    }
}

public sealed record HardwareWorkspaceCommands(
    AsyncRelayCommand InitializeBaseline,
    AsyncRelayCommand DetectNewDevices,
    AsyncRelayCommand ScanUsb2070Numbers,
    AsyncRelayCommand GenerateHardwareSmokeReport,
    AsyncRelayCommand GenerateT25SmokePlan,
    AsyncRelayCommand ExportPairingManifest,
    AsyncRelayCommand ExportEvidenceIndex,
    AsyncRelayCommand ExportFieldSnapshot,
    RelayCommand InstallUsb2070Driver,
    AsyncRelayCommand BindSelectedDevices,
    AsyncRelayCommand SetDac,
    AsyncRelayCommand StopDac,
    AsyncRelayCommand SetPga,
    AsyncRelayCommand StartExcitation,
    AsyncRelayCommand StopExcitation,
    AsyncRelayCommand StartAcquisition,
    AsyncRelayCommand ReadAcquisitionBlock,
    AsyncRelayCommand ReadAllActiveAcquisitionBlocks,
    AsyncRelayCommand StopAcquisition,
    AsyncRelayCommand SyncStart,
    AsyncRelayCommand StopAllDevices);
