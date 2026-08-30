using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EitHost.App;
using EitHost.App.ViewModels.Workspaces;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Acquisition;
using EitHost.Core.Analysis;
using EitHost.Core.Concurrency;
using EitHost.Core.Deployment;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.Baseline;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Domain;
using EitHost.Core.Export;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Pairing;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;
using EitHost.Core.Sync;
using static EitHost.App.ViewModels.RealtimeVisualizationProjection;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel : ObservableObject, IDisposable
{
    public ApplicationWorkspaces Workspaces { get; }

    public ExperimentWorkspaceViewModel ExperimentWorkspace => Workspaces.Experiment;

    public HardwareWorkspaceViewModel HardwareWorkspace => Workspaces.Hardware;

    public RealtimeWorkspaceViewModel RealtimeWorkspace => Workspaces.Realtime;

    public VisualizationWorkspaceViewModel VisualizationWorkspace => Workspaces.Visualization;

    private string roiMode => VisualizationWorkspace.RoiMode;

    private string roiShape => VisualizationWorkspace.RoiShape;

    private const int PanelLogEntryLimit = 80;
    private const int ActivityLogEntryLimit = 300;
    private const int RecentRunQueryLimit = 500;
    private const int RealtimeReadRowsPerBlock = 2048;
    private const long BytesPerAdcValue = sizeof(ushort);
    private const long MinAutoFlushBytes = 32L * 1024L * 1024L;
    private const long MaxAutoFlushBytes = 256L * 1024L * 1024L;
    private const double MemoryPressureFlushRatio = 0.95;
    private const int RealtimeContactCalibrationMinimumFrames = 100;
    private const int RealtimeContactCalibrationMaximumFrames = 300;
    private const double RealtimeLowImageQualityThreshold = 0.65;
    private static readonly EcdCwrDegradedDemodulationSelector RealtimeDegradedDemodulationSelector = new();
    private static readonly EitBaselineIntegrityAnalyzer RealtimeBaselineIntegrityAnalyzer = new();
    private static readonly EcdCwrRealtimeRoiDespiker RealtimeRoiDespiker = new();
    private static readonly EcdCwrRealtimeRoiDespikingOptions RealtimeRoiDespikingOptions = new(
        LowConfidenceThreshold: RealtimeLowImageQualityThreshold);
    private static readonly EcdCwrRealtimeRoiTrendFilter RealtimeRoiTrendFilter = new();
    private static readonly EcdCwrRealtimeRoiTrendOptions RealtimeRoiTrendOptions = new(
        TrustedQualityThreshold: RealtimeLowImageQualityThreshold);
    private const double RoiCurveMarkerDiameter = 16.0;
    private const int RoiCurveMaxTooltipMarkers = 80;
    private const int RoiRealtimeSeriesLimit = 2000;
    private const int RoiDespikingAnalysisWindow = 32;
    private const double RoiNoiseDisplayHalfRangeSigma = 6.0;
    private const string RoiModeCustom = "custom";
    private const string RoiModeFixedNominal = "fixed_nominal";
    private const string RoiShapeSquare = "square";
    private const string RoiShapeCircle = "circle";
    private const string RealtimeSignalViewModeDemod = "demod";
    private const string RealtimeSignalViewModeReference = "reference";
    private const string RealtimeSignalViewModeTarget = "target";
    private const string RealtimeDemodDisplayModeRectangular = "rectangular";
    private const string RealtimeDemodDisplayModePolar = "polar";
    private static readonly TimeSpan RealtimeShutdownWait = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan RealtimeUiFlushInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan BufferedAcquisitionPreviewInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReplayPlaybackInterval = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan MemoryPressureProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly object MemoryPressureGate = new();
    private static readonly DdsCurrentOption[] DdsGainOptionValues =
        DdsDacSettings.SupportedGains.Select(gain => new DdsCurrentOption(DeviceRunParameterEditor.FormatCurrentLabel(gain), gain)).ToArray();
    private static readonly int[] DdsPhaseDegreeOptionValues = DdsDacSettings.SupportedPhaseDegrees.ToArray();
    private static readonly int[] DdsPgaGainOptionValues = [1, 2, 5, 10];
    private static readonly ContactSubjectProfileOption[] ContactSubjectProfileOptionValues =
    [
        new("水桶实验", "water-tank"),
        new("向日葵茎秆", "sunflower-stem")
    ];
    private static readonly string[] RunParameterPropertyNames =
    [
        nameof(DdsDacChannel),
        nameof(DdsFrequencyHz),
        nameof(DdsGain),
        nameof(RealtimeOperatingPointHint),
        nameof(DdsPhaseDegrees),
        nameof(DdsPgaGain),
        nameof(SelectedExcitationMode),
        nameof(ExcitationChannelCycles),
        nameof(DemodDiscardLeadingCycles),
        nameof(DemodDiscardTrailingCycles),
        nameof(DemodEffectiveDiscardSummary),
        nameof(ExcitationScanTimes),
        nameof(ExcitationOverheadUs),
        nameof(AcquisitionSampleRateHz),
        nameof(AcquisitionRange),
        nameof(AcquisitionTriggerMode),
        nameof(AcquisitionTriggerSource),
        nameof(AcquisitionTriggerDelay),
        nameof(AcquisitionTriggerLength),
        nameof(AcquisitionTriggerLevel),
        nameof(AcquisitionReadSampleRows),
        nameof(RealtimeFramesPerBlock),
        nameof(RealtimeMinimumAcceptedFrames),
        nameof(RealtimeBlockModeCode),
        nameof(RealtimeBlockLatencySummary),
        nameof(RealtimeMeshSize),
        nameof(RealtimeDifferenceLambda),
        nameof(RealtimeStorageMode),
        nameof(RealtimeSaveRawAcquisitionHdf5),
        nameof(RealtimeSaveReconstructionResults),
        nameof(RealtimePersistImagingFrames),
        nameof(RealtimeEnableOutlierDetection),
        nameof(RealtimeEnableOutlierCompensation),
        nameof(RealtimeEnableTemporalDespiking),
        nameof(RealtimeEnableDynamicKalman),
        nameof(RealtimeDynamicKalmanMode),
        nameof(RealtimeReconstructionRoute),
        nameof(RealtimeUseCustomLambda),
        nameof(RealtimeUseFrequencyDivisionLockIn),
        nameof(RealtimeDifferenceOrientation),
        nameof(RealtimeReferenceScalePolicy)
    ];

    private readonly SerializedUiActionDispatcher uiActions;
    private readonly RawSegmentHdf5Writer rawSegmentHdf5Writer = new();
    private AcquisitionSessionController acquisitionController => HardwareWorkspace.AcquisitionController;
    private BufferedAcquisitionPreviewPump bufferedAcquisitionPreviewPump => VisualizationWorkspace.BufferedAcquisitionPreviewPump;
    private DdsRunController ddsRuns => HardwareWorkspace.DdsRunController;
    private RealtimeSessionController realtimeSessions => RealtimeWorkspace.SessionController;
    private RealtimeBackendController realtimeBackend => RealtimeWorkspace.BackendController;
    private RealtimeDerivedPersistenceController derivedPersistence => RealtimeWorkspace.DerivedPersistenceController;
    private RealtimeAcquisitionLoopController realtimeAcquisitionLoop => RealtimeWorkspace.AcquisitionLoopController;
    private RealtimeReconstructionController realtimeReconstruction => RealtimeWorkspace.ReconstructionController;
    private RealtimeTimingGateController realtimeTimingGate => RealtimeWorkspace.TimingGateController;
    private RealtimeBlockConsumerController realtimeBlockConsumer => RealtimeWorkspace.BlockConsumerController;
    private RealtimeContactDiagnosticController realtimeContactDiagnostics => RealtimeWorkspace.ContactDiagnosticController;
    private RealtimeContactCalibrationController realtimeContactCalibration => RealtimeWorkspace.ContactCalibrationController;
    private RealtimeReferenceLifecycleController realtimeReferenceLifecycle => RealtimeWorkspace.ReferenceLifecycleController;
    private RealtimeReferenceActionController realtimeReferenceActions => RealtimeWorkspace.ReferenceActionController;
    private RealtimeTemporalAnalysisController realtimeTemporalAnalysis => RealtimeWorkspace.TemporalAnalysisController;
    private RealtimeRunCommandController realtimeRunCommands => RealtimeWorkspace.RunCommandController;
    private RealtimeCalibrationArtifactController realtimeCalibrationArtifacts => RealtimeWorkspace.CalibrationArtifactController;
    private ExperimentRunLifecycleController experimentRunLifecycle => ExperimentWorkspace.RunLifecycleController;
    private RawAcquisitionPersistenceController rawPersistence => ExperimentWorkspace.RawAcquisitionPersistenceController;
    private HardwareEvidenceController hardwareEvidence => HardwareWorkspace.EvidenceController;
    private HardwareDiscoveryController hardwareDiscovery => HardwareWorkspace.DiscoveryController;
    private HardwareRunCommandController hardwareRunCommands => HardwareWorkspace.RunCommandController;
    private RealtimePairingRecoveryController pairingRecovery => HardwareWorkspace.PairingRecoveryController;
    private DeviceRunParameterEditor runParameters => HardwareWorkspace.RunParameterEditor;
    private WslPyEidorsReconstructionOptions realtimeReconstructionOptions => realtimeBackend.Options;
    private RealtimePreviewStateStore realtimePreviewState => VisualizationWorkspace.RealtimePreviewState;
    private RealtimePreviewPump realtimePreviewPump => VisualizationWorkspace.RealtimePreviewPump;
    private RealtimePreviewController realtimePreview => VisualizationWorkspace.RealtimePreviewController;
    private RoiInteractionController roiInteractions => VisualizationWorkspace.RoiInteractionController;
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly Func<bool> memoryPressureProbe;
    private readonly object panelLogGate = new();
    private readonly object synchronizedReferenceActionGate = new();
    private readonly Guid currentSessionId = Guid.NewGuid();
    private readonly DateTimeOffset currentSessionStartedAt = DateTimeOffset.Now;
    private readonly string sessionDirectory;
    private readonly string operatorContactSettingsPath;
    private readonly DataRootLayout dataLayout;
    private readonly IDataRootStorageService dataRootStorageService;
    private readonly ExperimentCatalog experimentCatalog;
    private readonly RealtimeDiagnosticSink realtimeDiagnostics;
    private readonly RealtimeRawPersistenceService realtimeRawPersistenceService;
    private readonly DataStoreStartupController dataStoreStartup;
    private readonly CanonicalExperimentReplaySource canonicalReplaySource;
    private ReplayVisualizationController replayController => VisualizationWorkspace.ReplayController;
    private RealtimeRoiController realtimeRoi => VisualizationWorkspace.RealtimeRoiController;
    private readonly long autoFlushByteThreshold;
    private static DateTimeOffset lastMemoryPressureProbeAt = DateTimeOffset.MinValue;
    private static bool cachedMemoryPressureHigh;
    private bool catalogReady;
    private string currentSessionName = "未开始实验";
    private readonly OperatorStatusPresenter operatorStatus;
    private string lastCaptureSummary = "尚未读取采集块。";
    private CatalogRunSummaryItem? selectedRun;
    private string contactSubjectProfile = "water-tank";
    private string contactFirmwareBuildId = string.Empty;
    private bool contactKnownAllConnectedCalibrationArmed;
    private int usb2070DeviceNumber;
    private string realtimeImagingSummary = "尚未启动实时成像。";
    private string realtimeReferenceSummary = "参考帧：未锁定。";
    private string realtimeBaselineIntegritySummary = "基线诊断：等待参考。";
    private string realtimeContactSummary = "接触诊断：等待 qc_ref。";
    private string realtimeMultiFrequencySummary = "多频证据：单频模式，证据 E 未启用。";
    private string realtimeDataQualityStatus = "数据质量：等待采集";
    private string realtimeReferenceModeStatus = "参考模式：尚未锁定";
    private string realtimeReconstructionQualityStatus = "重构质量：尚未开始";
    private string realtimeRoiReadinessStatus = "ROI 就绪：否 · 等待参考与重构";
    private bool realtimeReferenceInvalidated;
    private bool realtimeLowConfidenceImage;
    private string realtimeSignalViewMode = RealtimeSignalViewModeDemod;
    private string realtimeDemodDisplayMode = RealtimeDemodDisplayModeRectangular;
    private string realtimeImagePolarity = "normal";
    private double realtimeImageGain = 1.0;
    private bool disposed;

    public ApplicationCoordinatorViewModel()
        : this(new PnpInsertionMonitor(new WindowsPnpDeviceScanner()), new Usb2070NativeApi())
    {
    }

    public ApplicationCoordinatorViewModel(
        IUsb2070NativeApi usb2070NativeApi,
        string? dataRootPath = null,
        string? catalogPath = null,
        Func<CancellationToken, Task<HardwareSmokeReport>>? hardwareSmokeCapture = null,
        long? autoFlushByteThreshold = null,
        IRealtimeReconstructionBackend? realtimeReconstructionBackend = null,
        Func<bool>? memoryPressureProbe = null,
        IDataRootStorageService? dataRootStorageService = null,
        Action<string>? directoryOpener = null,
        IExperimentDataLifecycleService? experimentDataLifecycleService = null,
        Func<string, string, bool>? lifecycleConfirmation = null,
        string? legacyApplicationDataPath = null,
        string? realtimeDiagnosticLogPath = null)
        : this(
            new PnpInsertionMonitor(new WindowsPnpDeviceScanner()),
            usb2070NativeApi,
            dataRootPath,
            catalogPath,
            hardwareSmokeCapture,
            autoFlushByteThreshold,
            realtimeReconstructionBackend,
            memoryPressureProbe,
            dataRootStorageService,
            directoryOpener,
            experimentDataLifecycleService,
            lifecycleConfirmation,
            legacyApplicationDataPath,
            realtimeDiagnosticLogPath)
    {
    }

    internal ApplicationCoordinatorViewModel(PnpInsertionMonitor insertionMonitor)
        : this(insertionMonitor, new Usb2070NativeApi())
    {
    }

    internal ApplicationCoordinatorViewModel(
        DataRootLayout resolvedDataLayout,
        string? realtimeDiagnosticLogPath = null)
        : this(
            new PnpInsertionMonitor(new WindowsPnpDeviceScanner()),
            new Usb2070NativeApi(),
            realtimeDiagnosticLogPath: realtimeDiagnosticLogPath,
            resolvedDataLayout: resolvedDataLayout,
            deferDataStoreInitialization: true)
    {
    }

    private ApplicationCoordinatorViewModel(
        PnpInsertionMonitor insertionMonitor,
        IUsb2070NativeApi usb2070NativeApi,
        string? dataRootPath = null,
        string? catalogPath = null,
        Func<CancellationToken, Task<HardwareSmokeReport>>? hardwareSmokeCapture = null,
        long? autoFlushByteThreshold = null,
        IRealtimeReconstructionBackend? realtimeReconstructionBackend = null,
        Func<bool>? memoryPressureProbe = null,
        IDataRootStorageService? dataRootStorageService = null,
        Action<string>? directoryOpener = null,
        IExperimentDataLifecycleService? experimentDataLifecycleService = null,
        Func<string, string, bool>? lifecycleConfirmation = null,
        string? legacyApplicationDataPath = null,
        string? realtimeDiagnosticLogPath = null,
        DataRootLayout? resolvedDataLayout = null,
        bool deferDataStoreInitialization = false)
    {
        uiActions = new SerializedUiActionDispatcher(
            SynchronizationContext.Current,
            Application.Current?.Dispatcher,
            static () => Application.Current?.Dispatcher);
        // Composed first: startup steps below already report operator status.
        operatorStatus = new OperatorStatusPresenter(message => AddPanelLog(ActivityLogs, message));
        operatorStatus.PropertyChanged += OnChildWorkspacePropertyChanged;
        ArgumentNullException.ThrowIfNull(insertionMonitor);
        this.usb2070NativeApi = usb2070NativeApi ?? throw new ArgumentNullException(nameof(usb2070NativeApi));
        this.memoryPressureProbe = memoryPressureProbe ?? IsMemoryPressureHigh;
        var resolvedHardwareSmokeCapture = hardwareSmokeCapture ??
            HardwareEvidenceController.CreateRealSmokeCapture(usb2070NativeApi);
        this.autoFlushByteThreshold = autoFlushByteThreshold ?? CalculateDefaultAutoFlushByteThreshold();
        dataLayout = resolvedDataLayout ?? DataRootLayout.Create(
            dataRootPath,
            catalogPath,
            currentSessionStartedAt,
            localApplicationDataPath: legacyApplicationDataPath);
        DataRootPath = dataLayout.RootPath;
        this.dataRootStorageService = dataRootStorageService ?? new DataRootStorageService(DataRootPath);
        var openDirectory = directoryOpener ?? OpenDirectoryInShell;
        sessionDirectory = Path.Combine(DataRootPath, "sessions", currentSessionId.ToString("N"));
        CatalogPath = dataLayout.CatalogPath;
        var confirmLifecycle = lifecycleConfirmation ?? ConfirmExperimentLifecycle;
        operatorContactSettingsPath = Path.Combine(
            Path.GetDirectoryName(CatalogPath) ?? DataRootPath,
            "operator-contact-settings.json");
        var restoredContactSettings = OperatorContactSettingsStore.Load(operatorContactSettingsPath);
        contactFirmwareBuildId = restoredContactSettings.ContactFirmwareBuildId;
        contactSubjectProfile = restoredContactSettings.ContactSubjectProfile;
        FrameStorePath = dataLayout.CurrentFrameStorePath;
        var realtimeExchangeDirectory = dataLayout.BackendExchangeDirectory;
        var backendController = new RealtimeBackendController(
            realtimeExchangeDirectory,
            realtimeReconstructionBackend);
        experimentCatalog = new ExperimentCatalog(dataLayout);
        realtimeDiagnostics = new RealtimeDiagnosticSink(
            dataLayout,
            experimentCatalog,
            realtimeDiagnosticLogPath);
        var derivedArtifactHdf5Writer = new DerivedArtifactHdf5Writer();
        realtimeRawPersistenceService = new RealtimeRawPersistenceService(
            dataLayout,
            experimentCatalog,
            this.dataRootStorageService,
            operatorDiagnostic: ReportRealtimeOperatorDiagnostic);
        dataStoreStartup = new DataStoreStartupController(
            new DataStoreStartupService(
                experimentCatalog,
                realtimeRawPersistenceService,
                this.dataRootStorageService,
                currentSessionId,
                currentSessionStartedAt),
            deferDataStoreInitialization,
            new DataStoreStartupCallbacks(
                ApplyDataStoreInitialization,
                ApplyDataStoreInitializationFailure,
                StartPostDataStoreInitialization,
                AddRealtimeDiagnostic));
        var backendExchangeArchiver = new ExperimentBackendExchangeArchiver(dataLayout, experimentCatalog);
        var experimentRunOperationGate = new ExperimentRunOperationGate();
        var lifecycleService = experimentDataLifecycleService ??
            new ExperimentDataLifecycleService(dataLayout, experimentCatalog, experimentRunOperationGate);
        var experimentDemodCatchUpService = new ExperimentDemodCatchUpService(
            dataLayout,
            experimentCatalog,
            derivedArtifactHdf5Writer);
        var experimentReconstructionCatchUpService = new ExperimentReconstructionCatchUpService(
            dataLayout,
            experimentCatalog,
            backendController,
            derivedArtifactHdf5Writer);
        var experimentRunLifecycleController = new ExperimentRunLifecycleController(
            dataLayout,
            experimentCatalog,
            experimentDemodCatchUpService,
            experimentReconstructionCatchUpService,
            experimentRunOperationGate,
            currentSessionId,
            new ExperimentRunLifecycleCallbacks(
                AddRealtimeDiagnostic,
                realtimeDiagnostics.BeginRun,
                realtimeDiagnostics.EndRun,
                realtimeDiagnostics.RecordForRun,
                () => PostToUi(ReplaceExperimentRunsFromCurrentSources),
                message => PostToUi(() => StatusMessage = message),
                message => PostToUi(() => ExperimentWorkspace.ApplyCatchUpProgress(message))));
        canonicalReplaySource = new CanonicalExperimentReplaySource(dataLayout, experimentCatalog);
        Workspaces = new ApplicationWorkspaces(
            new ExperimentWorkspaceViewModel(
                dataLayout,
                experimentCatalog,
                this.dataRootStorageService,
                lifecycleService,
                openDirectory,
                confirmLifecycle,
                experimentRunLifecycleController.QueueCatchUp,
                RefreshImagingRunsAsync,
                demodCatchUpService: experimentDemodCatchUpService),
            new HardwareWorkspaceViewModel(),
            new RealtimeWorkspaceViewModel(),
            new VisualizationWorkspaceViewModel());
        pseudo3dVisualization = new Pseudo3dVisualizationController(
            presentation => PostToUi(() => ApplyPseudo3dPresentation(presentation)),
            AddRealtimeDiagnostic);
        ExperimentWorkspace.AttachRunLifecycleController(experimentRunLifecycleController);
        HardwareWorkspace.AttachRunParameterEditor(new DeviceRunParameterEditor(
            () => SelectedBoundPairing,
            message => StatusMessage = message));
        runParameters.PropertyChanged += OnRunParameterPropertyChanged;
        var replay = new ReplayVisualizationController(VisualizationWorkspace, dataLayout, canonicalReplaySource,
            () => RealtimeImagePolarity, () => RealtimeImageGain, PostToUi);
        VisualizationWorkspace.AttachReplayController(replay);
        VisualizationWorkspace.AttachRealtimePreviewController(new RealtimePreviewController(
            VisualizationWorkspace,
            realtimePreviewState,
            new RealtimePreviewCallbacks(
                IsRealtimeDisplaySet,
                () => RealtimeSignalViewMode,
                () => RealtimeDemodDisplayMode,
                () => RealtimeImagePolarity,
                () => RealtimeImageGain,
                () => PostToUi(() => OnPropertyChanged(nameof(RealtimeContactCalibrationExportStateText))),
                AddRealtimeDiagnostic)));
        VisualizationWorkspace.AttachRealtimeRoiController(new RealtimeRoiController(
            VisualizationWorkspace,
            realtimePreviewState,
            new RealtimeRoiCallbacks(
                IsRealtimeDisplaySet,
                RealtimePreviewController.ShouldUpdateRoiPreview,
                RealtimePreviewController.ShouldUpdateFixedRoiTemporal,
                (setLabel, readiness) => realtimePreview.PublishQualityAxes(setLabel, roiReadiness: readiness),
                realtimePreview.RequestFlush,
                () => PostToUi(() => SaveRoiCurveCommand?.RaiseCanExecuteChanged()),
                AddRealtimeDiagnostic)));
        VisualizationWorkspace.AttachRoiInteractionController(new RoiInteractionController(
            VisualizationWorkspace,
            realtimePreviewState,
            realtimePreview,
            replayController,
            DataRootPath,
            sessionDirectory,
            new RoiInteractionCallbacks(
                () => SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title,
                IsRealtimeDisplaySet,
                PromptSaveFile,
                message => StatusMessage = message,
                message => AddPanelLog(ExportLogs, message),
                PostToUi)));
        RealtimeWorkspace.AttachSessionController(new RealtimeSessionController());
        RealtimeWorkspace.AttachCalibrationArtifactController(new RealtimeCalibrationArtifactController(
            realtimeSessions,
            DataRootPath,
            new RealtimeCalibrationArtifactCallbacks(
                () => SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title,
                () =>
                {
                    var pairing = SelectedRealtimeDisplayPairing ?? SelectedBoundPairing;
                    return pairing is null ? DdsFrequencyHz : runParameters.Get(pairing).DdsFrequencyHz;
                },
                PromptSaveFile,
                realtimePreview.PublishReferenceSummary,
                summary => RealtimeReferenceSummary = summary,
                PostToUi,
                AddRealtimeDiagnostic,
                message => AddPanelLog(RealtimeImagingLogs, message),
                message => StatusMessage = message,
                () =>
                {
                    ResetRealtimeReferenceCommand?.RaiseCanExecuteChanged();
                    SaveRealtimeContactCalibrationCommand?.RaiseCanExecuteChanged();
                    SaveRealtimeDeviceCalibrationCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(RealtimeContactCalibrationExportStateText));
                },
                NotifyRealtimeRunStateChanged)));
        RealtimeWorkspace.AttachBackendController(backendController);
        backendController.StateChanged += () => PostToUi(RaiseRealtimeBackendPropertiesChanged);
        RealtimeWorkspace.AttachDerivedPersistenceController(new RealtimeDerivedPersistenceController(
            dataLayout,
            experimentCatalog,
            derivedArtifactHdf5Writer,
            backendExchangeArchiver,
            AddRealtimeDiagnostic,
            ReportRealtimeOperatorDiagnostic));
        RealtimeWorkspace.AttachReconstructionController(new RealtimeReconstructionController(
            backendController,
            derivedPersistence,
            new RealtimeReconstructionCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.PublishQualityAxes,
                realtimePreview.PublishReconstructionActivity,
                pseudo3dVisualization.PublishLayer,
                (setLabel, result, qualityWeight, state) =>
                    realtimeRoi.PublishMeasurement(setLabel, result, qualityWeight, state),
                realtimeRoi.PublishProvisionalUnavailable,
                realtimePreview.QueueLog,
                (lines, status) => PostToUi(() =>
                {
                    foreach (var line in lines)
                    {
                        AddPanelLog(RealtimeImagingLogs, line);
                    }

                    if (status is not null)
                    {
                        StatusMessage = status;
                    }
                }),
                RealtimePreviewController.ShouldUpdateBoundaryFitPreview,
                RealtimePreviewController.ShouldUpdateImagePreview,
                RealtimePreviewController.ShouldUpdateStatus)));
        RealtimeWorkspace.AttachContactDiagnosticController(new RealtimeContactDiagnosticController(
            new RealtimeContactDiagnosticCallbacks(
                AddRealtimeDiagnostic,
                TryCaptureRealtimeRawRing,
                realtimePreview.PublishContactSummary,
                realtimePreview.PublishMultiFrequencySummary,
                realtimePreview.PublishReferenceInvalidated,
                realtimePreview.PublishBoundaryUnavailable,
                realtimePreview.PublishReferenceSummary,
                realtimePreview.PublishReconstructionActivity,
                realtimePreview.PublishPreReferenceContactDiagnostic,
                realtimePreview.QueueLog,
                realtimeCalibrationArtifacts.Invalidate)));
        RealtimeWorkspace.AttachContactCalibrationController(new RealtimeContactCalibrationController(
            DataRootPath,
            new RealtimeContactCalibrationCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.PublishReferenceSummary,
                realtimePreview.PublishContactSummary,
                realtimePreview.PublishReferenceInvalidated,
                realtimeCalibrationArtifacts.RaiseStateChanged)));
        RealtimeWorkspace.AttachReferenceLifecycleController(new RealtimeReferenceLifecycleController(
            synchronizedReferenceActionGate,
            derivedPersistence,
            realtimeContactCalibration,
            new RealtimeReferenceLifecycleCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.PublishReferenceInvalidated,
                realtimePreview.PublishReferenceSummary,
                realtimePreview.PublishContactSummary,
                realtimePreview.PublishBoundaryUnavailable,
                realtimeRoi.PublishUnavailable,
                realtimePreview.PublishQualityAxes,
                realtimeRoi.PublishProvisionalUnavailable,
                realtimePreview.QueueLog,
                realtimePreview.ClearLowConfidenceImage,
                realtimePreview.PublishReferenceNeutralImage,
                CreateRealtimeReferenceModeStatus,
                realtimeCalibrationArtifacts.ClearCompleted,
                NotifyRealtimeReferenceUi,
                PublishRealtimeReferenceSwitchUi)));
        RealtimeWorkspace.AttachReferenceActionController(new RealtimeReferenceActionController(
            RealtimeWorkspace,
            realtimeSessions,
            synchronizedReferenceActionGate,
            new RealtimeReferenceActionCallbacks(
                () => SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title,
                message => StatusMessage = message,
                realtimePreview.PublishReferenceSummary,
                message => AddPanelLog(RealtimeImagingLogs, message),
                AddRealtimeDiagnostic,
                () =>
                {
                    UseSelectedRealtimeReferenceCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(RealtimeManualReferenceActionText));
                },
                () =>
                {
                    UseCurrentRealtimeReferenceCommand?.RaiseCanExecuteChanged();
                    UseSelectedRealtimeReferenceCommand?.RaiseCanExecuteChanged();
                    OnPropertyChanged(nameof(RealtimeReferenceWindowSelectorVisibility));
                    OnPropertyChanged(nameof(RealtimeManualReferenceActionText));
                },
                RaiseRealtimeCanExecuteChanged)));
        RealtimeWorkspace.AttachTemporalAnalysisController(new RealtimeTemporalAnalysisController(
            new RealtimeTemporalAnalysisCallbacks(
                AddRealtimeDiagnostic,
                realtimeReferenceLifecycle.InvalidateProvisionalReference,
                realtimeRoi.PublishNeutral,
                realtimePreview.QueueNeutralImage,
                realtimePreview.PublishReconstructionActivity,
                realtimePreview.QueueLog,
                RealtimePreviewController.ShouldUpdateStatus)));
        RealtimeWorkspace.AttachTimingGateController(new RealtimeTimingGateController(
            new RealtimeTimingGateCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.QueueLog,
                message => PostToUi(() => StatusMessage = message),
                realtimePreview.PublishSummary,
                realtimePreview.PublishBoundaryUnavailable,
                realtimePreview.PublishQualityAxes,
                realtimeReferenceLifecycle.InvalidateProvisionalReference,
                RealtimeTemporalAnalysisController.ResetWindow,
                CreateRealtimeReferenceModeStatus)));
        RealtimeWorkspace.AttachBlockConsumerController(new RealtimeBlockConsumerController(
            derivedPersistence,
            realtimeTimingGate,
            realtimeReconstruction,
            new RealtimeBlockAnalysisCallbacks(
                realtimeTemporalAnalysis.ApplyPendingDiscontinuities,
                realtimePreview.TryPublishDemodAlignedRaw,
                realtimeContactDiagnostics.UpdateDiagnostics,
                realtimeReferenceLifecycle.ResetIncompatibleStartupReference,
                RealtimeReferenceLifecycleController.ResetStartupProgress,
                realtimeReferenceLifecycle.AccumulateCandidatesAsync,
                realtimeContactCalibration.UpdateAccumulator,
                realtimeContactCalibration.BuildTemplateDisplayPackage,
                RealtimeContactDiagnosticSerializer.SerializeTemplateDisplayPackage,
                (config, state, block) => pairingRecovery.UpdateSelfCheck(config, state, block),
                realtimeReferenceLifecycle.CommitPreparedSwitch,
                RealtimeReferenceLifecycleController.AnalyzeBaseline,
                RealtimeContactDiagnosticSerializer.SerializeCandidateDiagnosticWithAdaptiveTrace,
                RealtimeReferenceLifecycleController.CreateReferenceStatus,
                RealtimeReferenceLifecycleController.TryEstimateCommonScale,
                realtimeReferenceLifecycle.TryLockStartupDegradedReference,
                RealtimeTemporalAnalysisController.ResetWindow,
                realtimeReferenceLifecycle.TryLockReference,
                realtimeTemporalAnalysis.CreateSelection,
                realtimeTemporalAnalysis.HandleNoChange),
            new RealtimeBlockPresentationCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.QueueLog,
                message => PostToUi(() => StatusMessage = message),
                realtimePreview.PublishQualityAxes,
                realtimePreview.PublishReferenceSummary,
                realtimePreview.PublishReconstructionActivity,
                realtimePreview.PublishBaselineIntegritySummary,
                realtimePreview.PublishSignal,
                RaiseRealtimeReferenceCommandsChanged,
                CreateRealtimeReferenceModeStatus,
                RealtimePreviewController.ComposeImagingSummary)));
        RealtimeWorkspace.AttachAcquisitionLoopController(new RealtimeAcquisitionLoopController(
            usb2070NativeApi,
            new RealtimeAcquisitionLoopCallbacks(
                AddRealtimeDiagnostic,
                realtimePreview.PublishSummary,
                message => PostToUi(() => StatusMessage = message),
                message => PostToUi(() => AddPanelLog(RealtimeImagingLogs, message)),
                experimentRunLifecycle.BeginRun,
                ConfigureRealtimeDdsAsync,
                realtimeContactCalibration.InitializeAdaptiveThresholdState,
                experimentRunLifecycle.RegisterConfig,
                realtimeBlockConsumer.ConsumeAsync,
                 (sampleRateHz, readRows) => rawPersistence.GetRealtimeFlushByteThreshold(sampleRateHz, readRows),
                 (batch, config, state) => rawPersistence.PersistRealtimeAsync(batch, config, state),
                 (config, state, publishReady) =>
                     rawPersistence.CompleteRealtimeAsync(config, state, publishReady),
                 derivedPersistence.DrainAsync,
                 SendRealtimeDdsCommandAsync,
                experimentRunLifecycle.CompleteRun,
                CompleteRealtimeAcquisitionLoopUi)));
        VisualizationWorkspace.AttachRealtimePreviewPump(new RealtimePreviewPump(
            GetUiDispatcher,
            () => realtimeSessions.ActiveSetCount > 0,
            FlushRealtimePreviewSnapshots,
            AddRealtimeDiagnostic,
            RealtimeUiFlushInterval));
        var acquisitionSessions = new AcquisitionSessionController(
            usb2070NativeApi,
            this.memoryPressureProbe,
            (session, values, capturedAt, reason) => rawPersistence.AutoSave(session, values, capturedAt, reason),
            (session, droppedValues, totalDroppedValues) =>
                rawPersistence.NotifyDroppedValues(session, droppedValues, totalDroppedValues),
            message => AddPanelLog(AcquisitionLogs, message));
        HardwareWorkspace.AttachAcquisitionController(acquisitionSessions);
        HardwareWorkspace.AttachDdsRunController(new DdsRunController(
            PostToUi,
            (pairing, status) => hardwareRunCommands.ApplyFiniteScanStatus(pairing, status),
            (pairing, status) => hardwareRunCommands.CompleteFiniteScanAsync(pairing, status),
            (pairing, ex) =>
            {
                AddPanelLog(DdsCommandLogs, $"{DateTime.Now:HH:mm:ss} {pairing.Title} 有限扫描状态监控失败 {ex.Message}");
                StatusMessage = $"{pairing.Title} 有限扫描状态监控失败：{ex.Message}；请手动停止激励。";
            }));
        HardwareWorkspace.AttachEvidenceController(new HardwareEvidenceController(
            HardwareWorkspace,
            CreateHardwareEvidenceSnapshot,
            resolvedHardwareSmokeCapture,
            message => StatusMessage = message));
        HardwareWorkspace.AttachDiscoveryController(new HardwareDiscoveryController(
            HardwareWorkspace,
            insertionMonitor,
            usb2070NativeApi,
            experimentCatalog,
            currentSessionId,
            IsCatalogReady,
            new HardwareDiscoveryCallbacks(
                message => StatusMessage = message,
                message => AddPanelLog(AcquisitionLogs, message),
                runParameters.Initialize,
                RaiseHardwarePairingCommandStates,
                value => CurrentSessionName = value,
                RaiseRealtimeDashboardStateChanged)));
        ExperimentWorkspace.AttachRawAcquisitionPersistenceController(new RawAcquisitionPersistenceController(
            ExperimentWorkspace,
            acquisitionController,
            dataLayout,
            experimentCatalog,
            this.dataRootStorageService,
            rawSegmentHdf5Writer,
            realtimeRawPersistenceService,
            currentSessionId,
            this.autoFlushByteThreshold,
            new RawAcquisitionPersistenceCallbacks(
                () => SelectedBoundPairing,
                IsCatalogReady,
                UpsertCanonicalExperimentRun,
                ReplaceExperimentRunsFromCurrentSources,
                PostToUi,
                InvokeOnUiAsync,
                 message => AddPanelLog(AcquisitionLogs, message),
                 message => AddPanelLog(RealtimeImagingLogs, message),
                 message => StatusMessage = message,
                 AddRealtimeDiagnostic)));
        RealtimeWorkspace.AttachRunCommandController(new RealtimeRunCommandController(
            realtimeSessions,
            acquisitionController,
            ddsRuns,
            realtimeAcquisitionLoop,
            realtimePreview,
            new RealtimeRunCommandCallbacks(
                () => SelectedBoundPairing,
                () => SelectedRealtimeDisplayPairing,
                () => BoundPairings.ToArray(),
                IsCatalogReady,
                runParameters.SaveSelected,
                runParameters.Get,
                rawPersistence.EnsureRealtimeStartCapacity,
                realtimeCalibrationArtifacts.ClearCompleted,
                pairing => SelectedRealtimeDisplayPairing = pairing,
                IsRealtimeDisplaySet,
                ResetRealtimeStartPresentation,
                pairing => hardwareRunCommands.CreateUsbDevice(pairing),
                GetRealtimeReadRows,
                GetInterferenceFrequencyHzForPairing,
                () => RealtimeBackendProfile,
                () => ContactSubjectProfile,
                () => ContactFirmwareBuildId,
                () => ContactKnownAllConnectedCalibrationArmed,
                () => pairingRecovery.CreatePairingMapSummary(),
                PublishRealtimeRunSnapshot,
                snapshot => PostToUi(() => RealtimeWorkspace.ApplyReferenceSnapshot(snapshot)),
                state => _ = realtimeCalibrationArtifacts.ObserveRunAsync(state),
                AddRealtimeDiagnostic,
                message => AddPanelLog(RealtimeImagingLogs, message),
                message => StatusMessage = message,
                NotifyRealtimeRunStateChanged,
                RaiseRealtimeCanExecuteChanged)));
        HardwareWorkspace.AttachPairingRecoveryController(new RealtimePairingRecoveryController(
            HardwareWorkspace,
            hardwareDiscovery,
            experimentCatalog,
            currentSessionId,
            realtimeSessions,
            realtimeRunCommands,
            realtimePreview,
            new RealtimePairingRecoveryCallbacks(
                IsCatalogReady,
                PostToUi,
                AddRealtimeDiagnostic,
                message => AddPanelLog(RealtimeImagingLogs, message),
                message => StatusMessage = message,
                () =>
                {
                    RaiseRealtimeDashboardStateChanged();
                    RaiseRunStateChanged();
                    RaiseHardwarePairingCommandStates();
                })));
        HardwareWorkspace.AttachRunCommandController(new HardwareRunCommandController(
            acquisitionController,
            ddsRuns,
            realtimeSessions,
            realtimeRunCommands,
            this.autoFlushByteThreshold,
            new HardwareRunCommandCallbacks(
                () => SelectedBoundPairing,
                () => BoundPairings.ToArray(),
                runParameters.Get,
                IsCatalogReady,
                StartBufferedAcquisitionPreviewPump,
                StopBufferedAcquisitionPreviewPumpIfIdle,
                RaiseAcquisitionCanExecuteChanged,
                RaiseSaveCanExecuteChanged,
                RaiseRunStateChanged,
                summary => LastCaptureSummary = summary,
                message => AddPanelLog(DdsCommandLogs, message),
                message => AddPanelLog(AcquisitionLogs, message),
                message => AddPanelLog(RealtimeImagingLogs, message),
                message => StatusMessage = message)));
        VisualizationWorkspace.AttachBufferedAcquisitionPreviewPump(new BufferedAcquisitionPreviewPump(
            acquisitionSessions,
            GetUiDispatcher,
            () => SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title,
            () => checked(GetRealtimeReadRows(AcquisitionReadSampleRows) * Usb2070Constants.RequiredMeasurementChannelCount),
            realtimePreview.CreateBufferedRawSnapshot,
            realtimePreview.PublishRaw,
            BufferedAcquisitionPreviewInterval));
        replay.StatusChanged += message => StatusMessage = message;
        replay.DiagnosticMessage += AddRealtimeDiagnostic;
        replay.LegacyRunsChanged += ReplaceExperimentRunsFromCurrentSources;
        replay.ReplayDataChanged += () => SaveRoiCurveCommand?.RaiseCanExecuteChanged();
        ExperimentWorkspace.SelectionChanged += ApplySelectedExperiment;
        ExperimentWorkspace.StatusChanged += message => StatusMessage = message;
        ExperimentWorkspace.DiagnosticMessage += AddRealtimeDiagnostic;
        ExperimentWorkspace.PropertyChanged += OnChildWorkspacePropertyChanged;
        ExperimentWorkspace.DataTools.DemodulationLogged += message =>
            AddPanelLog(DemodulationLogs, $"{DateTime.Now:HH:mm:ss} {message}");
        ExperimentWorkspace.DataTools.InspectionLogged += message =>
            AddPanelLog(Hdf5InspectionLogs, $"{DateTime.Now:HH:mm:ss} {message}");
        ExperimentWorkspace.DataTools.ExportLogged += message =>
            AddPanelLog(ExportLogs, $"{DateTime.Now:HH:mm:ss} {message}");
        HardwareWorkspace.PropertyChanged += OnChildWorkspacePropertyChanged;
        HardwareWorkspace.PairingInputChanged += OnPairingInputChanged;
        HardwareWorkspace.BoundPairings.CollectionChanged += OnPseudo3dBoundPairingsChanged;
        HardwareWorkspace.SelectedBoundPairingChanging += _ => runParameters.SaveSelected();
        HardwareWorkspace.SelectedBoundPairingChanged += OnSelectedBoundPairingChanged;
        HardwareWorkspace.SelectedRealtimeDisplayPairingChanged += OnSelectedRealtimeDisplayPairingChanged;
        RealtimeWorkspace.PropertyChanged += OnChildWorkspacePropertyChanged;
        VisualizationWorkspace.PropertyChanged += OnChildWorkspacePropertyChanged;
        VisualizationWorkspace.SelectedImagingRunChanged += item => _ = replayController.LoadLegacyRunAsync(item);
        VisualizationWorkspace.ReplayFrameIndexChanged += index => _ = replayController.ShowReplayFrameAsync(index);
        VisualizationWorkspace.RoiDefinitionChanged += roiInteractions.HandleDefinitionChanged;
        VisualizationWorkspace.TemporalViewOptionsChanged += roiInteractions.RebuildTemporalViews;
        InitializeBaselineCommand = new AsyncRelayCommand(hardwareDiscovery.InitializeBaselineAsync);
        DetectNewDevicesCommand = new AsyncRelayCommand(hardwareDiscovery.DetectNewDevicesAsync);
        ScanUsb2070NumbersCommand = new AsyncRelayCommand(hardwareDiscovery.ScanUsb2070NumbersAsync);
        GenerateHardwareSmokeReportCommand = new AsyncRelayCommand(hardwareEvidence.GenerateHardwareSmokeReportAsync);
        GenerateT25SmokePlanCommand = new AsyncRelayCommand(hardwareEvidence.GenerateT25SmokePlanAsync, hardwareEvidence.CanGenerateT25SmokePlan);
        ExportPairingManifestCommand = new AsyncRelayCommand(hardwareEvidence.ExportPairingManifestAsync, hardwareEvidence.CanExportPairingManifest);
        ExportEvidenceIndexCommand = new AsyncRelayCommand(hardwareEvidence.ExportEvidenceIndexAsync);
        ExportFieldSnapshotCommand = new AsyncRelayCommand(hardwareEvidence.ExportFieldSnapshotAsync);
        InstallUsb2070DriverCommand = new RelayCommand(hardwareDiscovery.InstallUsb2070Driver);
        BindSelectedDevicesCommand = new AsyncRelayCommand(hardwareDiscovery.BindSelectedDevicesAsync, hardwareDiscovery.CanBindSelectedDevices);
        SetDacCommand = new AsyncRelayCommand(hardwareRunCommands.SetDacAsync, hardwareRunCommands.CanConfigureExcitation);
        StopDacCommand = new AsyncRelayCommand(hardwareRunCommands.StopDacAsync, hardwareRunCommands.CanConfigureExcitation);
        SetPgaCommand = new AsyncRelayCommand(hardwareRunCommands.SetPgaAsync, hardwareRunCommands.CanConfigureExcitation);
        StartExcitationCommand = new AsyncRelayCommand(hardwareRunCommands.StartExcitationAsync, hardwareRunCommands.CanStartExcitation);
        StopExcitationCommand = new AsyncRelayCommand(hardwareRunCommands.StopExcitationAsync, hardwareRunCommands.CanStopExcitation);
        StartAcquisitionCommand = new AsyncRelayCommand(hardwareRunCommands.StartAcquisitionAsync, hardwareRunCommands.CanStartSelectedAcquisition);
        ReadAcquisitionBlockCommand = new AsyncRelayCommand(hardwareRunCommands.ReadAcquisitionBlockAsync, hardwareRunCommands.CanReadSelectedAcquisitionBlock);
        ReadAllActiveAcquisitionBlocksCommand = new AsyncRelayCommand(
            hardwareRunCommands.ReadAllActiveAcquisitionBlocksAsync,
            hardwareRunCommands.CanReadAllActiveAcquisitionBlocks);
        StopAcquisitionCommand = new AsyncRelayCommand(hardwareRunCommands.StopAcquisitionAsync, hardwareRunCommands.CanStopSelectedAcquisition);
        StartRealtimeImagingCommand = new RelayCommand(realtimeRunCommands.StartSelected, realtimeRunCommands.CanStartSelected);
        StartAllRealtimeImagingCommand = new RelayCommand(realtimeRunCommands.StartAll, realtimeRunCommands.CanStartAll);
        StopRealtimeImagingCommand = new RelayCommand(realtimeRunCommands.StopSelected, realtimeRunCommands.CanStopSelected);
        ResetRealtimeReferenceCommand = new RelayCommand(realtimeReferenceActions.ResetReference, realtimeReferenceActions.CanResetReference);
        ConfirmRealtimeReferenceSwitchCommand = new RelayCommand(
            realtimeReferenceActions.ConfirmReferenceSwitch,
            realtimeReferenceActions.CanConfirmReferenceSwitch);
        CancelRealtimeReferenceRelockCommand = new RelayCommand(
            realtimeReferenceActions.CancelReferenceRelock,
            realtimeReferenceActions.CanCancelReferenceRelock);
        PrepareSynchronizedRealtimeReferencesCommand = new RelayCommand(
            realtimeReferenceActions.PrepareSynchronizedReferences,
            realtimeReferenceActions.CanPrepareSynchronizedReferences);
        ConfirmSynchronizedRealtimeReferenceSwitchCommand = new RelayCommand(
            realtimeReferenceActions.ConfirmSynchronizedReferenceSwitch,
            realtimeReferenceActions.CanConfirmSynchronizedReferenceSwitch);
        CancelSynchronizedRealtimeReferenceRelockCommand = new RelayCommand(
            realtimeReferenceActions.CancelSynchronizedReferenceRelock,
            realtimeReferenceActions.CanCancelSynchronizedReferenceRelock);
        UseCurrentRealtimeReferenceCommand = new RelayCommand(
            realtimeReferenceActions.UseCurrentReference,
            realtimeReferenceActions.CanUseCurrentReference);
        UseSelectedRealtimeReferenceCommand = new RelayCommand(
            realtimeReferenceActions.UseSelectedReference,
            realtimeReferenceActions.CanUseSelectedReference);
        CaptureRealtimeRawRingCommand = new RelayCommand(CaptureRealtimeRawRing, CanCaptureRealtimeRawRing);
        SaveRealtimeContactCalibrationCommand = new RelayCommand(
            realtimeCalibrationArtifacts.SaveSelectedSessionCalibration,
            realtimeCalibrationArtifacts.CanSaveSelectedSessionCalibration);
        SaveRealtimeDeviceCalibrationCommand = new RelayCommand(
            realtimeCalibrationArtifacts.SaveSelectedDeviceCalibration,
            realtimeCalibrationArtifacts.CanSaveSelectedDeviceCalibration);
        SyncStartCommand = new AsyncRelayCommand(hardwareRunCommands.SyncStartAsync, hardwareRunCommands.CanSyncStart);
        StopAllDevicesCommand = new AsyncRelayCommand(hardwareRunCommands.StopAllAsync, hardwareRunCommands.CanStopAll);
        SaveCapturedBlockCommand = new AsyncRelayCommand(rawPersistence.SaveSelectedAsync, rawPersistence.CanSaveSelected);
        SaveAllCapturedBlocksCommand = new AsyncRelayCommand(rawPersistence.SaveAllAsync, rawPersistence.CanSaveAll);
        DemodulateHdf5Command = ExperimentWorkspace.DataTools.DemodulateHdf5Command;
        DemodulateRecentRunsCommand = ExperimentWorkspace.DataTools.DemodulateRecentRunsCommand;
        InspectHdf5Command = ExperimentWorkspace.DataTools.InspectHdf5Command;
        ExportCsvCommand = ExperimentWorkspace.DataTools.ExportCsvCommand;
        ExportRecentRawCsvCommand = ExperimentWorkspace.DataTools.ExportRecentRawCsvCommand;
        RefreshCatalogRunsCommand = ExperimentWorkspace.RefreshCatalogRunsCommand;
        RefreshDataRootStorageCommand = ExperimentWorkspace.RefreshDataRootStorageCommand;
        OpenDataRootCommand = ExperimentWorkspace.OpenDataRootCommand;
        OpenSelectedExperimentDirectoryCommand = ExperimentWorkspace.OpenSelectedExperimentDirectoryCommand;
        ArchiveSelectedExperimentCommand = ExperimentWorkspace.ArchiveSelectedExperimentCommand;
        DeleteSelectedExperimentCommand = ExperimentWorkspace.DeleteSelectedExperimentCommand;
        ReconcileSelectedExperimentCommand = ExperimentWorkspace.ReconcileSelectedExperimentCommand;
        BrowseDemodInputCommand = ExperimentWorkspace.DataTools.BrowseDemodInputCommand;
        BrowseHdf5InspectCommand = ExperimentWorkspace.DataTools.BrowseHdf5InspectCommand;
        BrowseExportSourceCommand = ExperimentWorkspace.DataTools.BrowseExportSourceCommand;
        BrowseRealtimeBackendPathCommand = new AsyncRelayCommand(
            BrowseRealtimeBackendPathAsync,
            CanBrowseRealtimeBackendPath);
        ClearRunDateFilterCommand = ExperimentWorkspace.ClearRunDateFilterCommand;
        AcknowledgeErrorsCommand = new RelayCommand(operatorStatus.Acknowledge, () => HasUnreviewedErrors);
        operatorStatus.AcknowledgeAvailabilityChanged += AcknowledgeErrorsCommand.RaiseCanExecuteChanged;
        RefreshImagingRunsCommand = replayController.RefreshCommand;
        ToggleReplayPlaybackCommand = replayController.TogglePlaybackCommand;
        CalculateReplayRoiCommand = replayController.CalculateRoiCommand;
        SaveRoiCurveCommand = new RelayCommand(roiInteractions.Save, roiInteractions.CanSave);
        ClearRealtimeRoiCurveCommand = new RelayCommand(roiInteractions.Clear);
        HardwareWorkspace.ConfigureCommands(new HardwareWorkspaceCommands(
            InitializeBaselineCommand,
            DetectNewDevicesCommand,
            ScanUsb2070NumbersCommand,
            GenerateHardwareSmokeReportCommand,
            GenerateT25SmokePlanCommand,
            ExportPairingManifestCommand,
            ExportEvidenceIndexCommand,
            ExportFieldSnapshotCommand,
            InstallUsb2070DriverCommand,
            BindSelectedDevicesCommand,
            SetDacCommand,
            StopDacCommand,
            SetPgaCommand,
            StartExcitationCommand,
            StopExcitationCommand,
            StartAcquisitionCommand,
            ReadAcquisitionBlockCommand,
            ReadAllActiveAcquisitionBlocksCommand,
            StopAcquisitionCommand,
            SyncStartCommand,
            StopAllDevicesCommand));
        VisualizationWorkspace.ConfigureCommands(new VisualizationWorkspaceCommands(
            RefreshImagingRunsCommand,
            ToggleReplayPlaybackCommand,
            CalculateReplayRoiCommand,
            SaveRoiCurveCommand,
            ClearRealtimeRoiCurveCommand));
        RecentRuns.CollectionChanged += (_, _) =>
        {
            DemodulateRecentRunsCommand.RaiseCanExecuteChanged();
            ExportRecentRawCsvCommand.RaiseCanExecuteChanged();
        };
        if (!deferDataStoreInitialization)
        {
            dataStoreStartup.InitializeSynchronously();
        }
        else
        {
            StatusMessage = "正在打开工作站；数据目录将在界面显示后继续准备。";
        }
    }

    public string ApplicationTitle { get; } = "EIT 多设备上位机";

    public string CurrentSessionName
    {
        get => currentSessionName;
        private set => SetProperty(ref currentSessionName, value);
    }

    public string PairingLabel
    {
        get => HardwareWorkspace.PairingLabel;
        set => HardwareWorkspace.PairingLabel = value;
    }

    public string StatusMessage
    {
        get => operatorStatus.StatusMessage;
        private set => operatorStatus.StatusMessage = value;
    }

    public StatusSeverity StatusMessageSeverity => operatorStatus.StatusMessageSeverity;

    public int UnreviewedErrorCount => operatorStatus.UnreviewedErrorCount;

    public bool HasUnreviewedErrors => operatorStatus.HasUnreviewedErrors;

    public string UnreviewedErrorSummary => operatorStatus.UnreviewedErrorSummary;

    public string DataRootStorageSummary => ExperimentWorkspace.DataRootStorageSummary;

    public string SelectedExperimentStorageSummary => ExperimentWorkspace.SelectedExperimentStorageSummary;

    public string RetentionPolicySummary => ExperimentWorkspace.RetentionPolicySummary;

    public string DataRootPath { get; }

    public string CatalogPath { get; }

    public string FrameStorePath { get; }

    public string LastCaptureSummary
    {
        get => lastCaptureSummary;
        private set => SetProperty(ref lastCaptureSummary, value);
    }

    public int PairingUsb2070DeviceNumber
    {
        get => HardwareWorkspace.PairingUsb2070DeviceNumber;
        set => HardwareWorkspace.PairingUsb2070DeviceNumber = value;
    }

    public DeviceCandidateOption? SelectedUsb2070Candidate
    {
        get => HardwareWorkspace.SelectedUsb2070Candidate;
        set => HardwareWorkspace.SelectedUsb2070Candidate = value;
    }

    public DeviceCandidateOption? SelectedDdsCandidate
    {
        get => HardwareWorkspace.SelectedDdsCandidate;
        set => HardwareWorkspace.SelectedDdsCandidate = value;
    }

    public PairingSummaryItem? SelectedBoundPairing
    {
        get => HardwareWorkspace.SelectedBoundPairing;
        set => HardwareWorkspace.SelectedBoundPairing = value;
    }

    public PairingSummaryItem? SelectedRealtimeDisplayPairing
    {
        get => HardwareWorkspace.SelectedRealtimeDisplayPairing;
        set => HardwareWorkspace.SelectedRealtimeDisplayPairing = value;
    }

    private void OnChildWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.PropertyName))
        {
            OnPropertyChanged(MapChildWorkspacePropertyName(sender, args.PropertyName));
        }
    }

    private string MapChildWorkspacePropertyName(object? sender, string propertyName)
    {
        if (!ReferenceEquals(sender, RealtimeWorkspace))
        {
            return propertyName;
        }

        return propertyName switch
        {
            nameof(RealtimeWorkspaceViewModel.ReferenceWindowOptions) =>
                nameof(RealtimeReferenceWindowOptions),
            nameof(RealtimeWorkspaceViewModel.SelectedReferenceWindowOption) =>
                nameof(SelectedRealtimeReferenceWindow),
            nameof(RealtimeWorkspaceViewModel.ReferenceWindowPreview) =>
                nameof(RealtimeReferenceWindowPreview),
            nameof(RealtimeWorkspaceViewModel.ReferenceRelockStateText) =>
                nameof(RealtimeReferenceRelockStateText),
            nameof(RealtimeWorkspaceViewModel.SynchronizedReferenceSummary) =>
                nameof(RealtimeSynchronizedReferenceSummary),
            _ => propertyName
        };
    }

    private void OnRunParameterPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        RaiseRunParameterPropertyChanges();
        OnPropertyChanged(nameof(RealtimeOperatingPointHint));
        OnPropertyChanged(nameof(DemodEffectiveDiscardSummary));
        OnPropertyChanged(nameof(RealtimeBlockModeCode));
        OnPropertyChanged(nameof(RealtimeBlockLatencySummary));
        OnPropertyChanged(nameof(RealtimeSaveRawAcquisitionHdf5));
        OnPropertyChanged(nameof(RealtimePersistImagingFrames));
        OnPropertyChanged(nameof(CanRetainBackendExchange));
        OnPropertyChanged(nameof(RealtimeStorageModeSummary));
        OnPropertyChanged(nameof(RealtimeReferenceScalePolicyWarning));
    }

    private void OnSelectedBoundPairingChanged(PairingSummaryItem? value)
    {
        if (value is not null)
        {
            Usb2070DeviceNumber = value.Pairing.Usb2070DeviceNumber;
            runParameters.Load(value);
        }

        RaiseDdsCanExecuteChanged();
        RaiseSaveCanExecuteChanged();
        RaiseRunStateChanged();
    }

    private void OnSelectedRealtimeDisplayPairingChanged(PairingSummaryItem? value)
    {
        OnPropertyChanged(nameof(SelectedRealtimeDisplayLabel));
        ApplyRealtimeDisplayFromCache(value?.Title);
        realtimeReferenceActions.RefreshWindowOptions(value?.Title);
        SaveRoiCurveCommand.RaiseCanExecuteChanged();
        RaiseRealtimeCanExecuteChanged();
    }

    public string SelectedRealtimeDisplayLabel =>
        SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title ?? "未选择";

    public string CurrentRunParameterProfileSummary =>
        SelectedBoundPairing is { } pairing
            ? $"{pairing.Title} 独立参数档 · {DdsFrequencyHz}Hz · {DeviceRunParameterEditor.FormatCurrentLabel(DdsGain)} · AD {AcquisitionSampleRateHz}Hz · {DescribeOption(RealtimeBlockModeOptions, RealtimeBlockModeCode)} {RealtimeFramesPerBlock}/{RealtimeMinimumAcceptedFrames} · {DescribeOption(RealtimeReconstructionRouteOptions, RealtimeReconstructionRoute)}"
            : "未选择目标套，步骤二参数暂不归属到设备套。";

    /// <summary>
    /// Resolves a stored option code to the operator-facing label already shown in the matching
    /// selector, so status text never leaks an internal code. The label is trimmed at its first
    /// separator because selectors spell out details that surrounding status text repeats.
    /// </summary>
    internal static string DescribeOption(IReadOnlyList<SelectionOption> options, string value)
    {
        var label = options
            .FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))?
            .Label;
        if (string.IsNullOrWhiteSpace(label))
        {
            return value;
        }

        var separator = label.IndexOfAny(['：', '·']);
        return separator > 0 ? label[..separator].Trim() : label.Trim();
    }

    public CatalogRunSummaryItem? SelectedRun
    {
        get => selectedRun;
        set
        {
            if (SetProperty(ref selectedRun, value) && value is { } run)
            {
                ApplySelectedRunPaths(run);
            }
        }
    }

    // True while the selected set's DDS excitation is running. Drives the
    // 启动/停止激励 button visuals and locks the excitation parameter inputs.
    public bool IsSelectedExciting =>
        SelectedBoundPairing is { } pairing && ddsRuns.IsActive(pairing.Title);

    // True while the selected set is acquiring. Drives the 启动/停止采集 button
    // visuals and locks the acquisition parameter inputs.
    public bool IsSelectedAcquiring =>
        SelectedBoundPairing is { } pairing
        && (acquisitionController.IsActive(pairing.Title)
            || realtimeSessions.IsSetActive(pairing.Title));

    public bool CanEditExcitationSettings => !IsSelectedExciting;

    public bool CanEditAcquisitionSettings => !IsSelectedAcquiring;

    public bool CanEditRealtimeRunSettings =>
        SelectedBoundPairing is not { } pairing
        || !realtimeSessions.IsSetActive(pairing.Title);

    public bool CanEditRealtimeBackendSettings => !IsRealtimeImagingActive;

    public string RealtimeBackendDistroName => realtimeBackend.Options.DistroName;

    public string RealtimeBackendRepositoryPath => realtimeBackend.Options.BackendRepositoryPath;

    public IReadOnlyList<SelectionOption> RealtimeBackendProfileOptions => realtimeBackend.ProfileOptions;

    public string RealtimeBackendProfile
    {
        get => realtimeBackend.Options.BackendProfile;
        set => _ = SelectRealtimeBackendProfileAsync(value);
    }

    public string RealtimeBackendProfileLabel => realtimeBackend.ProfileLabel;

    public string RealtimeBackendNixProfile => realtimeBackend.NixProfile;

    public string RealtimeBackendDisplayPath => realtimeBackend.DisplayPath;

    public string RealtimeBackendConfigPath => realtimeBackend.ConfigPath;

    public string RealtimeBackendStatus => realtimeBackend.Status;

    public int DdsDacChannel { get => runParameters.DdsDacChannel; set => runParameters.DdsDacChannel = value; }

    public int DdsFrequencyHz { get => runParameters.DdsFrequencyHz; set => runParameters.DdsFrequencyHz = value; }

    public IReadOnlyList<DdsCurrentOption> DdsGainOptions => DdsGainOptionValues;

    public double DdsGain { get => runParameters.DdsGain; set => runParameters.DdsGain = value; }

    public string RealtimeExcitationSummary =>
        runParameters.CreateExcitationSummary(BoundPairings, ddsRuns.IsActive);

    public string RealtimeOperatingPointHint =>
        DdsFrequencyHz == 3_125 && Math.Abs(DdsGain - 0.3) < 1e-9
            ? "植物稳定优先起点：3125 Hz / 30 µA，解调前/后裁剪建议 3/2 周期。频率和电流会影响电极极化与稳定时间；请为不同对象保留独立参数档。"
            : $"当前为自定义工作点：{DdsFrequencyHz} Hz / {DeviceRunParameterEditor.FormatCurrentLabel(DdsGain)}。若稳定困难，可从 3125 Hz / 30 µA 重新评估。";

    public int DdsPhaseDegrees { get => runParameters.DdsPhaseDegrees; set => runParameters.DdsPhaseDegrees = value; }

    public IReadOnlyList<int> DdsPhaseDegreeOptions => DdsPhaseDegreeOptionValues;

    public IReadOnlyList<int> DdsPgaGainOptions => DdsPgaGainOptionValues;

    public int DdsPgaGain { get => runParameters.DdsPgaGain; set => runParameters.DdsPgaGain = value; }

    public IReadOnlyList<ContactSubjectProfileOption> ContactSubjectProfileOptions =>
        ContactSubjectProfileOptionValues;

    public string ContactSubjectProfile
    {
        get => contactSubjectProfile;
        set
        {
            if (SetProperty(ref contactSubjectProfile, value))
            {
                PersistOperatorContactSettings();
            }
        }
    }

    public string ContactFirmwareBuildId
    {
        get => contactFirmwareBuildId;
        set
        {
            if (SetProperty(ref contactFirmwareBuildId, value))
            {
                PersistOperatorContactSettings();
            }
        }
    }

    private void PersistOperatorContactSettings()
    {
        try
        {
            OperatorContactSettingsStore.Save(
                operatorContactSettingsPath,
                new OperatorContactSettings(
                    SchemaVersion: 1,
                    ContactFirmwareBuildId: contactFirmwareBuildId.Trim(),
                    ContactSubjectProfile: string.IsNullOrWhiteSpace(contactSubjectProfile)
                        ? "water-tank"
                        : contactSubjectProfile.Trim()));
        }
        catch (Exception ex)
        {
            StatusMessage = $"接触诊断配置保存失败：{ex.Message}";
        }
    }

    public bool ContactKnownAllConnectedCalibrationArmed
    {
        get => contactKnownAllConnectedCalibrationArmed;
        set => SetProperty(ref contactKnownAllConnectedCalibrationArmed, value);
    }

    public DdsExcitationMode SelectedExcitationMode { get => runParameters.ExcitationMode; set => runParameters.ExcitationMode = value; }

    public double ExcitationChannelCycles { get => runParameters.ExcitationChannelCycles; set => runParameters.ExcitationChannelCycles = value; }

    public double DemodDiscardLeadingCycles { get => runParameters.DemodDiscardLeadingCycles; set => runParameters.DemodDiscardLeadingCycles = value; }

    public double DemodDiscardTrailingCycles { get => runParameters.DemodDiscardTrailingCycles; set => runParameters.DemodDiscardTrailingCycles = value; }

    public int ExcitationScanTimes { get => runParameters.ExcitationScanTimes; set => runParameters.ExcitationScanTimes = value; }

    public int ExcitationOverheadUs { get => runParameters.ExcitationOverheadUs; set => runParameters.ExcitationOverheadUs = value; }

    public int Usb2070DeviceNumber
    {
        get => usb2070DeviceNumber;
        set => SetProperty(ref usb2070DeviceNumber, value);
    }

    public int AcquisitionSampleRateHz { get => runParameters.AcquisitionSampleRateHz; set => runParameters.AcquisitionSampleRateHz = value; }

    public Usb2070AdRange AcquisitionRange { get => runParameters.AcquisitionRange; set => runParameters.AcquisitionRange = value; }

    public Usb2070TriggerMode AcquisitionTriggerMode { get => runParameters.AcquisitionTriggerMode; set => runParameters.AcquisitionTriggerMode = value; }

    public Usb2070TriggerSource AcquisitionTriggerSource { get => runParameters.AcquisitionTriggerSource; set => runParameters.AcquisitionTriggerSource = value; }

    public int AcquisitionTriggerDelay { get => runParameters.AcquisitionTriggerDelay; set => runParameters.AcquisitionTriggerDelay = value; }

    public int AcquisitionTriggerLength { get => runParameters.AcquisitionTriggerLength; set => runParameters.AcquisitionTriggerLength = value; }

    public int AcquisitionTriggerLevel { get => runParameters.AcquisitionTriggerLevel; set => runParameters.AcquisitionTriggerLevel = value; }

    public int AcquisitionReadSampleRows { get => runParameters.AcquisitionReadSampleRows; set => runParameters.AcquisitionReadSampleRows = value; }

    public int RealtimeFramesPerBlock { get => runParameters.RealtimeFramesPerBlock; set => runParameters.RealtimeFramesPerBlock = value; }

    public int RealtimeMinimumAcceptedFrames { get => runParameters.RealtimeMinimumAcceptedFrames; set => runParameters.RealtimeMinimumAcceptedFrames = value; }

    public string RealtimeBlockModeCode { get => runParameters.RealtimeBlockModeCode; set => runParameters.RealtimeBlockModeCode = value; }

    public string RealtimeBlockLatencySummary => runParameters.RealtimeBlockLatencySummary;

    public string DemodEffectiveDiscardSummary => runParameters.DemodEffectiveDiscardSummary;

    public double RealtimeMeshSize { get => runParameters.RealtimeMeshSize; set => runParameters.RealtimeMeshSize = value; }

    public double RealtimeDifferenceLambda { get => runParameters.RealtimeDifferenceLambda; set => runParameters.RealtimeDifferenceLambda = value; }

    public bool RealtimeSaveRawAcquisitionHdf5
    {
        get => RealtimeStoragePolicy.From(RealtimeStorageMode).PersistContinuousRaw;
        set => RealtimeStorageMode = value
            ? RealtimeStoragePolicy.FullRecordValue
            : RealtimeStoragePolicy.PreviewValue;
    }

    public bool RealtimeSaveReconstructionResults { get => runParameters.RealtimeSaveReconstructionResults; set => runParameters.RealtimeSaveReconstructionResults = value; }

    // V411: full record is the safe default; preview is the explicit no-persistence mode.
    public bool RealtimePersistImagingFrames
    {
        get => RealtimeStoragePolicy.From(RealtimeStorageMode).PersistImagingFrames;
        set
        {
            if (value && RealtimeStorageMode == RealtimeStoragePolicy.PreviewValue)
            {
                RealtimeStorageMode = RealtimeStoragePolicy.FullRecordValue;
            }
            else if (!value)
            {
                RealtimeStorageMode = RealtimeStoragePolicy.PreviewValue;
            }
        }
    }

    public string RealtimeStorageMode { get => runParameters.RealtimeStorageMode; set => runParameters.RealtimeStorageMode = value; }

    public string RealtimeStorageModeSummary => RealtimeStorageMode switch
    {
        RealtimeStoragePolicy.PreviewValue => "仅预览：不写入数据库，不自动保存 raw/解调/重构文件",
        _ => "完整记录：连续保存全部原始采集、解调状态与成功的电导率结果"
    };

    public bool CanRetainBackendExchange =>
        RealtimeStorageMode == RealtimeStoragePolicy.FullRecordValue && CanEditRealtimeRunSettings;

    public string RealtimeReferenceScalePolicy { get => runParameters.RealtimeReferenceScalePolicy; set => runParameters.RealtimeReferenceScalePolicy = value; }

    public string RealtimeReferenceScalePolicyWarning =>
        EcdCwrReferenceScalePolicy.UsesCommonScaleNormalization(RealtimeReferenceScalePolicy)
            ? "警告：公共尺度归一化会移除每个目标相对参考的公共幅值 α；若全局电导变化本身是实验信号，请改用“保留物理尺度”。"
            : "保留物理尺度：不会从目标数据中扣除公共趋势；慢变可作为真实对象变化进入成像与 ROI。";

    public bool RealtimeEnableOutlierDetection { get => runParameters.RealtimeEnableOutlierDetection; set => runParameters.RealtimeEnableOutlierDetection = value; }

    public bool RealtimeEnableOutlierCompensation { get => runParameters.RealtimeEnableOutlierCompensation; set => runParameters.RealtimeEnableOutlierCompensation = value; }

    public bool RealtimeEnableTemporalDespiking { get => runParameters.RealtimeEnableTemporalDespiking; set => runParameters.RealtimeEnableTemporalDespiking = value; }

    public bool RealtimeEnableDynamicKalman { get => runParameters.RealtimeEnableDynamicKalman; set => runParameters.RealtimeEnableDynamicKalman = value; }

    public string RealtimeDynamicKalmanMode { get => runParameters.RealtimeDynamicKalmanMode; set => runParameters.RealtimeDynamicKalmanMode = value; }

    public string RealtimeConnectionStateText => BoundPairings.Count > 0 ? "已连接" : "断开";

    public string RealtimePowerStateText => BoundPairings.Count > 0 ? "ON" : "未知";

    public string RealtimeRecordingStateText => RealtimeStorageMode == RealtimeStoragePolicy.PreviewValue
        ? "关 · 仅预览"
        : $"开 · {DescribeOption(RealtimeStorageModeOptions, RealtimeStorageMode)}";

    public string RealtimeContactCalibrationExportStateText =>
        realtimeCalibrationArtifacts.CreateExportStateText();

    public string RealtimeAcquisitionStateText => acquisitionController.ActiveCount > 0 || IsRealtimeImagingActive ? "RUN" : "空闲";

    public string RealtimeReconstructionRoute { get => runParameters.RealtimeReconstructionRoute; set => runParameters.RealtimeReconstructionRoute = value; }

    public bool RealtimeUseCustomLambda { get => runParameters.RealtimeUseCustomLambda; set => runParameters.RealtimeUseCustomLambda = value; }

    public bool RealtimeUseFrequencyDivisionLockIn { get => runParameters.RealtimeUseFrequencyDivisionLockIn; set => runParameters.RealtimeUseFrequencyDivisionLockIn = value; }

    public string RealtimeDifferenceOrientation { get => runParameters.RealtimeDifferenceOrientation; set => runParameters.RealtimeDifferenceOrientation = value; }

    public string RealtimeSignalViewMode
    {
        get => realtimeSignalViewMode;
        set
        {
            if (SetProperty(ref realtimeSignalViewMode, VisualizationRenderer.NormalizeRealtimeSignalViewMode(value)))
            {
                OnPropertyChanged(nameof(RealtimeSignalViewTitle));
                OnPropertyChanged(nameof(RealtimeDemodLegend));
                OnPropertyChanged(nameof(IsRealtimeDemodViewSelected));
                realtimePreview.RefreshSignalFromCache(SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title);
            }
        }
    }

    public string RealtimeSignalViewTitle => RealtimeSignalViewMode switch
    {
        RealtimeSignalViewModeReference => "参考帧",
        RealtimeSignalViewModeTarget => "目标帧",
        _ when RealtimeDemodDisplayMode == RealtimeDemodDisplayModePolar => "解调极坐标",
        _ => "解调复数"
    };

    public string RealtimeDemodDisplayMode
    {
        get => realtimeDemodDisplayMode;
        set
        {
            if (SetProperty(ref realtimeDemodDisplayMode, VisualizationRenderer.NormalizeRealtimeDemodDisplayMode(value)))
            {
                OnPropertyChanged(nameof(RealtimeSignalViewTitle));
                OnPropertyChanged(nameof(RealtimeDemodLegend));
                realtimePreview.RefreshSignalFromCache(SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title);
            }
        }
    }

    public bool IsRealtimeDemodViewSelected => RealtimeSignalViewMode == RealtimeSignalViewModeDemod;

    public string RealtimeDemodLegend => RealtimeSignalViewMode switch
    {
        RealtimeSignalViewModeReference => "蓝：参考边界电压幅值（V）。208 点是电极测量序列。",
        RealtimeSignalViewModeTarget => "蓝：目标边界电压幅值（V）。208 点是电极测量序列。",
        _ when RealtimeDemodDisplayMode == RealtimeDemodDisplayModePolar =>
            "专家极坐标 · 上半区蓝：幅值 |V|（V）；下半区红：相位 φ（°）。低幅值点不绘制相位；坐标与 EIDORS {ad} 有符号边界电压一致。",
        _ => "EIDORS {ad} 有符号边界电压 · 蓝：实部 Re(V)；红：虚部 Im(V)。first=-I、next=+I，测量为 V(first)-V(next)；独立电流探头仅用于绝对阻抗相角溯源。"
    };

    public string RealtimeImagePolarity
    {
        get => realtimeImagePolarity;
        set => SetProperty(ref realtimeImagePolarity, VisualizationRenderer.NormalizeRealtimeImagePolarity(value));
    }

    public double RealtimeImageGain
    {
        get => realtimeImageGain;
        set => SetProperty(ref realtimeImageGain, Math.Clamp(value, 0.1, 5.0));
    }

    public bool IsRealtimeImagingActive =>
        realtimeSessions.IsAnyActive;

    public string RealtimeImagingSummary
    {
        get => realtimeImagingSummary;
        private set => SetProperty(ref realtimeImagingSummary, value);
    }

    public string RealtimeReferenceSummary
    {
        get => realtimeReferenceSummary;
        private set => SetProperty(ref realtimeReferenceSummary, value);
    }

    public RealtimeReferenceWindowOption? SelectedRealtimeReferenceWindow
    {
        get => RealtimeWorkspace.SelectedReferenceWindowOption;
        set => RealtimeWorkspace.SelectedReferenceWindowOption = value;
    }

    public string RealtimeReferenceWindowPreview
    {
        get => RealtimeWorkspace.ReferenceWindowPreview;
        private set => RealtimeWorkspace.ReferenceWindowPreview = value;
    }

    public string RealtimeManualReferenceActionText => realtimeReferenceActions.ManualActionText;

    public string RealtimeReferenceRelockStateText
    {
        get => RealtimeWorkspace.ReferenceRelockStateText;
        private set => realtimeReferenceActions.SetReferenceRelockStateText(value);
    }

    public string RealtimeSynchronizedReferenceSummary
    {
        get => RealtimeWorkspace.SynchronizedReferenceSummary;
        private set => realtimeReferenceActions.SetSynchronizedReferenceSummary(value);
    }

    public Visibility RealtimeReferenceSwitchControlsVisibility =>
        realtimeReferenceActions.ShouldShowSwitchControls ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RealtimeReferenceWindowSelectorVisibility =>
        RealtimeReferenceWindowOptions.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility RealtimeSynchronizedReferenceControlsVisibility =>
        realtimeReferenceActions.ShouldShowSynchronizedControls ? Visibility.Visible : Visibility.Collapsed;

    public string RealtimeBaselineIntegritySummary
    {
        get => realtimeBaselineIntegritySummary;
        private set => SetProperty(ref realtimeBaselineIntegritySummary, value);
    }

    public string RealtimeImageStats
    {
        get => VisualizationWorkspace.RealtimeImageStats;
        private set => VisualizationWorkspace.RealtimeImageStats = value;
    }

    public string RealtimeReconstructionActivity
    {
        get => VisualizationWorkspace.RealtimeReconstructionActivity;
        private set => VisualizationWorkspace.RealtimeReconstructionActivity = value;
    }

    public string RealtimeContactSummary
    {
        get => realtimeContactSummary;
        private set => SetProperty(ref realtimeContactSummary, value);
    }

    public string RealtimeMultiFrequencySummary
    {
        get => realtimeMultiFrequencySummary;
        private set => SetProperty(ref realtimeMultiFrequencySummary, value);
    }

    public string RealtimeDataQualityStatus
    {
        get => realtimeDataQualityStatus;
        private set => SetProperty(ref realtimeDataQualityStatus, value);
    }

    public string RealtimeReferenceModeStatus
    {
        get => realtimeReferenceModeStatus;
        private set => SetProperty(ref realtimeReferenceModeStatus, value);
    }

    public string RealtimeReconstructionQualityStatus
    {
        get => realtimeReconstructionQualityStatus;
        private set => SetProperty(ref realtimeReconstructionQualityStatus, value);
    }

    public string RealtimeRoiReadinessStatus
    {
        get => realtimeRoiReadinessStatus;
        private set => SetProperty(ref realtimeRoiReadinessStatus, value);
    }

    public Visibility RealtimeReferenceInvalidatedVisibility =>
        realtimeReferenceInvalidated ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RealtimeLowConfidenceImageVisibility =>
        realtimeLowConfidenceImage && !realtimeReferenceInvalidated ? Visibility.Visible : Visibility.Collapsed;

    public string RealtimeRawWaveStats
    {
        get => VisualizationWorkspace.RealtimeRawWaveStats;
        private set => VisualizationWorkspace.RealtimeRawWaveStats = value;
    }

    public string RealtimeDemodStats
    {
        get => VisualizationWorkspace.RealtimeDemodStats;
        private set => VisualizationWorkspace.RealtimeDemodStats = value;
    }

    public IReadOnlyList<RealtimeDemodulationAxisTick> RealtimeDemodYAxisTicks
    {
        get => VisualizationWorkspace.RealtimeDemodYAxisTicks;
        private set => VisualizationWorkspace.RealtimeDemodYAxisTicks = value;
    }

    public Geometry? RealtimeDemodGridGeometry
    {
        get => VisualizationWorkspace.RealtimeDemodGridGeometry;
        private set => VisualizationWorkspace.RealtimeDemodGridGeometry = value;
    }

    public Geometry? RealtimeDemodZeroLineGeometry
    {
        get => VisualizationWorkspace.RealtimeDemodZeroLineGeometry;
        private set => VisualizationWorkspace.RealtimeDemodZeroLineGeometry = value;
    }

    public string RealtimeBoundaryStats
    {
        get => VisualizationWorkspace.RealtimeBoundaryStats;
        private set => VisualizationWorkspace.RealtimeBoundaryStats = value;
    }

    public string RealtimeBoundaryYAxisTop
    {
        get => VisualizationWorkspace.RealtimeBoundaryYAxisTop;
        private set => VisualizationWorkspace.RealtimeBoundaryYAxisTop = value;
    }

    public string RealtimeBoundaryYAxisMiddle
    {
        get => VisualizationWorkspace.RealtimeBoundaryYAxisMiddle;
        private set => VisualizationWorkspace.RealtimeBoundaryYAxisMiddle = value;
    }

    public string RealtimeBoundaryYAxisBottom
    {
        get => VisualizationWorkspace.RealtimeBoundaryYAxisBottom;
        private set => VisualizationWorkspace.RealtimeBoundaryYAxisBottom = value;
    }

    public Geometry? RealtimeRawChannel1Geometry
    {
        get => VisualizationWorkspace.RealtimeRawChannel1Geometry;
        private set => VisualizationWorkspace.RealtimeRawChannel1Geometry = value;
    }

    public Geometry? RealtimeRawChannel2Geometry
    {
        get => VisualizationWorkspace.RealtimeRawChannel2Geometry;
        private set => VisualizationWorkspace.RealtimeRawChannel2Geometry = value;
    }

    public Geometry? RealtimeDemodPrimaryGeometry
    {
        get => VisualizationWorkspace.RealtimeDemodPrimaryGeometry;
        private set => VisualizationWorkspace.RealtimeDemodPrimaryGeometry = value;
    }

    public Geometry? RealtimeDemodSecondaryGeometry
    {
        get => VisualizationWorkspace.RealtimeDemodSecondaryGeometry;
        private set => VisualizationWorkspace.RealtimeDemodSecondaryGeometry = value;
    }

    public Geometry? RealtimeBoundaryTargetGeometry
    {
        get => VisualizationWorkspace.RealtimeBoundaryTargetGeometry;
        private set => VisualizationWorkspace.RealtimeBoundaryTargetGeometry = value;
    }

    public Geometry? RealtimeBoundaryReferenceGeometry
    {
        get => VisualizationWorkspace.RealtimeBoundaryReferenceGeometry;
        private set => VisualizationWorkspace.RealtimeBoundaryReferenceGeometry = value;
    }

    public Geometry? RealtimeBoundaryTemplateGeometry
    {
        get => VisualizationWorkspace.RealtimeBoundaryTemplateGeometry;
        private set => VisualizationWorkspace.RealtimeBoundaryTemplateGeometry = value;
    }

    public ImageSource? RealtimeImageSource
    {
        get => VisualizationWorkspace.RealtimeImageSource;
        private set => VisualizationWorkspace.RealtimeImageSource = value;
    }

    public double RoiImageCanvasSize => VisualizationWorkspace.RoiImageCanvasSize;

    public string RoiMode
    {
        get => VisualizationWorkspace.RoiMode;
        set => VisualizationWorkspace.RoiMode = value;
    }

    public string RoiShape
    {
        get => VisualizationWorkspace.RoiShape;
        set => VisualizationWorkspace.RoiShape = value;
    }

    public string RoiSizePreset
    {
        get => VisualizationWorkspace.RoiSizePreset;
        set => VisualizationWorkspace.RoiSizePreset = value;
    }

    public double RoiSizePixels
    {
        get => VisualizationWorkspace.RoiSizePixels;
        set => VisualizationWorkspace.RoiSizePixels = value;
    }

    public double RoiCenterXPercent
    {
        get => VisualizationWorkspace.RoiCenterXPercent;
        set => VisualizationWorkspace.RoiCenterXPercent = value;
    }

    public double RoiCenterYPercent
    {
        get => VisualizationWorkspace.RoiCenterYPercent;
        set => VisualizationWorkspace.RoiCenterYPercent = value;
    }

    public double RoiOverlayLeft => VisualizationWorkspace.RoiOverlayLeft;

    public double RoiOverlayTop => VisualizationWorkspace.RoiOverlayTop;

    public double RoiOverlaySize => VisualizationWorkspace.RoiOverlaySize;

    public Visibility RoiCustomControlsVisibility => VisualizationWorkspace.RoiCustomControlsVisibility;

    public Visibility RoiFixedGridVisibility => VisualizationWorkspace.RoiFixedGridVisibility;

    public Visibility RoiSquareVisibility => VisualizationWorkspace.RoiSquareVisibility;

    public Visibility RoiCircleVisibility => VisualizationWorkspace.RoiCircleVisibility;

    public Geometry FixedRoiGridGeometry => VisualizationWorkspace.FixedRoiGridGeometry;

    public Geometry FixedRoiSelectionGeometry => VisualizationWorkspace.FixedRoiSelectionGeometry;

    public string SelectedFixedRoiId => VisualizationWorkspace.SelectedFixedRoiId;

    public string FixedRoiResolutionProfileId => VisualizationWorkspace.FixedRoiResolutionProfileId;

    public string FixedRoiResolutionNotice => VisualizationWorkspace.FixedRoiResolutionNotice;

    public string FixedRoiTemporalMapMode
    {
        get => VisualizationWorkspace.FixedRoiTemporalMapMode;
        set => VisualizationWorkspace.FixedRoiTemporalMapMode = value;
    }

    public int FixedRoiAngularRingNumber
    {
        get => VisualizationWorkspace.FixedRoiAngularRingNumber;
        set => VisualizationWorkspace.FixedRoiAngularRingNumber = value;
    }

    public FixedRoiTemporalVisualSnapshot RealtimeFixedRoiTemporal
    {
        get => VisualizationWorkspace.RealtimeFixedRoiTemporal;
        private set => VisualizationWorkspace.RealtimeFixedRoiTemporal = value;
    }

    public ObservableCollection<FixedRoiMapCellVisual> RealtimeFixedRoiMapCells => VisualizationWorkspace.RealtimeFixedRoiMapCells;

    public string RoiPositionSummary => VisualizationWorkspace.RoiPositionSummary;

    public string RealtimeRoiSummary
    {
        get => VisualizationWorkspace.RealtimeRoiSummary;
        private set => VisualizationWorkspace.RealtimeRoiSummary = value;
    }

    public Geometry? RealtimeRoiCurveGeometry
    {
        get => VisualizationWorkspace.RealtimeRoiCurveGeometry;
        private set => VisualizationWorkspace.RealtimeRoiCurveGeometry = value;
    }

    public Geometry? RealtimeRoiRawCurveGeometry
    {
        get => VisualizationWorkspace.RealtimeRoiRawCurveGeometry;
        private set => VisualizationWorkspace.RealtimeRoiRawCurveGeometry = value;
    }

    public Geometry? RealtimeRoiNoiseBandGeometry
    {
        get => VisualizationWorkspace.RealtimeRoiNoiseBandGeometry;
        private set => VisualizationWorkspace.RealtimeRoiNoiseBandGeometry = value;
    }

    public IReadOnlyList<RoiCurveMarker> RealtimeRoiMarkers
    {
        get => VisualizationWorkspace.RealtimeRoiMarkers;
        private set => VisualizationWorkspace.RealtimeRoiMarkers = value;
    }

    public string RealtimeRoiAxisStart
    {
        get => VisualizationWorkspace.RealtimeRoiAxisStart;
        private set => VisualizationWorkspace.RealtimeRoiAxisStart = value;
    }

    public string RealtimeRoiAxisMiddle
    {
        get => VisualizationWorkspace.RealtimeRoiAxisMiddle;
        private set => VisualizationWorkspace.RealtimeRoiAxisMiddle = value;
    }

    public string RealtimeRoiAxisEnd
    {
        get => VisualizationWorkspace.RealtimeRoiAxisEnd;
        private set => VisualizationWorkspace.RealtimeRoiAxisEnd = value;
    }

    public string ExportSourceHdf5Path
    {
        get => ExperimentWorkspace.DataTools.ExportSourceHdf5Path;
        set => ExperimentWorkspace.DataTools.ExportSourceHdf5Path = value;
    }

    public string ExportDatasetPath
    {
        get => ExperimentWorkspace.DataTools.ExportDatasetPath;
        set => ExperimentWorkspace.DataTools.ExportDatasetPath = value;
    }

    public string ExportCsvPath
    {
        get => ExperimentWorkspace.DataTools.ExportCsvPath;
        set => ExperimentWorkspace.DataTools.ExportCsvPath = value;
    }

    public string ExportFilter
    {
        get => ExperimentWorkspace.DataTools.ExportFilter;
        set => ExperimentWorkspace.DataTools.ExportFilter = value;
    }

    public string DemodInputHdf5Path
    {
        get => ExperimentWorkspace.DataTools.DemodInputHdf5Path;
        set => ExperimentWorkspace.DataTools.DemodInputHdf5Path = value;
    }

    public string DemodOutputHdf5Path
    {
        get => ExperimentWorkspace.DataTools.DemodOutputHdf5Path;
        set => ExperimentWorkspace.DataTools.DemodOutputHdf5Path = value;
    }

    public string DemodulationSummary
    {
        get => ExperimentWorkspace.DataTools.DemodulationSummary;
    }

    public string Hdf5InspectPath
    {
        get => ExperimentWorkspace.DataTools.Hdf5InspectPath;
        set => ExperimentWorkspace.DataTools.Hdf5InspectPath = value;
    }

    public string Hdf5InspectionSummary
    {
        get => ExperimentWorkspace.DataTools.Hdf5InspectionSummary;
    }

    public string HardwareSmokeReportPath
    {
        get => HardwareWorkspace.HardwareSmokeReportPath;
    }

    public string HardwareSmokeSummary
    {
        get => HardwareWorkspace.HardwareSmokeSummary;
    }

    public string T25SmokePlanPath
    {
        get => HardwareWorkspace.T25SmokePlanPath;
    }

    public string T25SmokePlanSummary
    {
        get => HardwareWorkspace.T25SmokePlanSummary;
    }

    public string PairingManifestPath
    {
        get => HardwareWorkspace.PairingManifestPath;
    }

    public string PairingManifestSummary
    {
        get => HardwareWorkspace.PairingManifestSummary;
    }

    public string EvidenceIndexPath
    {
        get => HardwareWorkspace.EvidenceIndexPath;
    }

    public string EvidenceIndexSummary
    {
        get => HardwareWorkspace.EvidenceIndexSummary;
    }

    public string FieldSnapshotPath
    {
        get => HardwareWorkspace.FieldSnapshotPath;
    }

    public string FieldSnapshotSummary
    {
        get => HardwareWorkspace.FieldSnapshotSummary;
    }

    public ObservableCollection<string> WorkflowSteps => HardwareWorkspace.WorkflowSteps;

    public ObservableCollection<DeviceCandidateOption> PendingUsb2070Candidates =>
        HardwareWorkspace.PendingUsb2070Candidates;

    public ObservableCollection<DeviceCandidateOption> PendingDdsCandidates =>
        HardwareWorkspace.PendingDdsCandidates;

    public ObservableCollection<PairingSummaryItem> BoundPairings => HardwareWorkspace.BoundPairings;

    // Unified, auto-maintained activity feed. Every panel log is mirrored here
    // so the 系统日志 page shows a live timeline without any manual export step.
    public ObservableCollection<string> ActivityLogs { get; } = [];

    public ObservableCollection<string> DdsCommandLogs => HardwareWorkspace.DdsCommandLogs;

    public ObservableCollection<string> AcquisitionLogs => HardwareWorkspace.AcquisitionLogs;

    public ObservableCollection<string> HardwareSmokeLogs => HardwareWorkspace.HardwareSmokeLogs;

    public ObservableCollection<string> T25SmokePlanLogs => HardwareWorkspace.T25SmokePlanLogs;

    public ObservableCollection<string> PairingManifestLogs => HardwareWorkspace.PairingManifestLogs;

    public ObservableCollection<string> EvidenceIndexLogs => HardwareWorkspace.EvidenceIndexLogs;

    public ObservableCollection<string> FieldSnapshotLogs => HardwareWorkspace.FieldSnapshotLogs;

    public ObservableCollection<string> DemodulationLogs { get; } = [];

    public ObservableCollection<string> RealtimeImagingLogs { get; } = [];

    public ObservableCollection<RealtimeReferenceWindowOption> RealtimeReferenceWindowOptions =>
        RealtimeWorkspace.ReferenceWindowOptions;

    public ObservableCollection<string> Hdf5InspectionLogs { get; } = [];

    public ObservableCollection<string> ExportLogs { get; } = [];

    public ObservableCollection<CatalogRunSummaryItem> RecentRuns => ExperimentWorkspace.RecentRuns;

    public ObservableCollection<ExperimentRunListItem> ExperimentRuns => ExperimentWorkspace.ExperimentRuns;

    public ICollectionView ExperimentRunsView => ExperimentWorkspace.ExperimentRunsView;

    public ExperimentRunListItem? SelectedExperimentRun
    {
        get => ExperimentWorkspace.SelectedExperimentRun;
        set => ExperimentWorkspace.SelectedExperimentRun = value;
    }

    public ICollectionView RecentRunsView => ExperimentWorkspace.RecentRunsView;

    public DateTime? SelectedRunDate
    {
        get => ExperimentWorkspace.SelectedRunDate;
        set => ExperimentWorkspace.SelectedRunDate = value;
    }

    public string RunFilterSummary => ExperimentWorkspace.RunFilterSummary;

    public IReadOnlyList<DdsExcitationMode> ExcitationModes { get; } = Enum.GetValues<DdsExcitationMode>();

    public IReadOnlyList<SelectionOption> RealtimeBlockModeOptions { get; } =
    [
        new("快速 · 1 帧 / 至少 1 帧", "fast"),
        new("平衡 · 2 帧 / 至少 2 帧", "balanced"),
        new("稳定（推荐）· 3 帧 / 至少 3 帧", "stable"),
        new("容错 · 3 帧 / 至少 2 帧", "tolerant")
    ];

    public IReadOnlyList<Usb2070AdRange> AcquisitionRanges { get; } = Enum.GetValues<Usb2070AdRange>();

    public IReadOnlyList<Usb2070TriggerMode> AcquisitionTriggerModes { get; } = Enum.GetValues<Usb2070TriggerMode>();

    public IReadOnlyList<Usb2070TriggerSource> AcquisitionTriggerSources { get; } = Enum.GetValues<Usb2070TriggerSource>();

    public IReadOnlyList<SelectionOption> RealtimeReconstructionRouteOptions { get; } =
    [
        new("NOSER RM", "noser_rm"),
        new("Laplace RM", "laplace_rm"),
        new("Curvature RM", "curvature_rm")
    ];

    public IReadOnlyList<SelectionOption> RealtimeDynamicKalmanModeOptions { get; } =
    [
        new("自动（安全 NOSER 图像域）", "auto"),
        new("测量域动态（实验）", "measurement"),
        new("快速图像域", "fast_image")
    ];

    public IReadOnlyList<SelectionOption> RealtimeStorageModeOptions { get; } =
    [
        new("完整记录：raw + 解调 + 重构", RealtimeStoragePolicy.FullRecordValue),
        new("仅预览：不入库、不自动保存", RealtimeStoragePolicy.PreviewValue)
    ];

    public IReadOnlyList<SelectionOption> RealtimeDifferenceOrientationOptions { get; } =
    [
        new("目标 - 参考", "target_minus_reference"),
        new("参考 - 目标", "reference_minus_target")
    ];

    public IReadOnlyList<SelectionOption> RealtimeReferenceScalePolicyOptions { get; } =
    [
        new("保留物理尺度（推荐）", EcdCwrReferenceScalePolicy.PreservePhysicalScale),
        new("公共尺度归一化（会移除全局 α）", EcdCwrReferenceScalePolicy.CommonScaleNormalized)
    ];

    public IReadOnlyList<SelectionOption> RealtimeSignalViewOptions { get; } =
    [
        new("解调", RealtimeSignalViewModeDemod),
        new("参考帧", RealtimeSignalViewModeReference),
        new("目标帧", RealtimeSignalViewModeTarget)
    ];

    public IReadOnlyList<SelectionOption> RealtimeDemodDisplayModeOptions { get; } =
    [
        new("实部 + 虚部（默认）", RealtimeDemodDisplayModeRectangular),
        new("幅值 + 相位（专家）", RealtimeDemodDisplayModePolar)
    ];

    public IReadOnlyList<SelectionOption> RoiShapeOptions => VisualizationWorkspace.RoiShapeOptions;

    public IReadOnlyList<SelectionOption> RoiModeOptions => VisualizationWorkspace.RoiModeOptions;

    public IReadOnlyList<SelectionOption> FixedRoiTemporalMapModeOptions =>
        VisualizationWorkspace.FixedRoiTemporalMapModeOptions;

    public IReadOnlyList<int> FixedRoiAngularRingOptions => VisualizationWorkspace.FixedRoiAngularRingOptions;

    public IReadOnlyList<SelectionOption> RoiSizePresetOptions => VisualizationWorkspace.RoiSizePresetOptions;

    public AsyncRelayCommand InitializeBaselineCommand { get; }

    public AsyncRelayCommand DetectNewDevicesCommand { get; }

    public AsyncRelayCommand ScanUsb2070NumbersCommand { get; }

    public AsyncRelayCommand GenerateHardwareSmokeReportCommand { get; }

    public AsyncRelayCommand GenerateT25SmokePlanCommand { get; }

    public AsyncRelayCommand ExportPairingManifestCommand { get; }

    public AsyncRelayCommand ExportEvidenceIndexCommand { get; }

    public AsyncRelayCommand ExportFieldSnapshotCommand { get; }

    public RelayCommand InstallUsb2070DriverCommand { get; }

    public AsyncRelayCommand BindSelectedDevicesCommand { get; }

    public AsyncRelayCommand SetDacCommand { get; }

    public AsyncRelayCommand StopDacCommand { get; }

    public AsyncRelayCommand SetPgaCommand { get; }

    public AsyncRelayCommand StartExcitationCommand { get; }

    public AsyncRelayCommand StopExcitationCommand { get; }

    public AsyncRelayCommand StartAcquisitionCommand { get; }

    public AsyncRelayCommand ReadAcquisitionBlockCommand { get; }

    public AsyncRelayCommand ReadAllActiveAcquisitionBlocksCommand { get; }

    public AsyncRelayCommand StopAcquisitionCommand { get; }

    public RelayCommand StartRealtimeImagingCommand { get; }

    public RelayCommand StartAllRealtimeImagingCommand { get; }

    public RelayCommand StopRealtimeImagingCommand { get; }

    public RelayCommand ResetRealtimeReferenceCommand { get; }

    public RelayCommand ConfirmRealtimeReferenceSwitchCommand { get; }

    public RelayCommand CancelRealtimeReferenceRelockCommand { get; }

    public RelayCommand PrepareSynchronizedRealtimeReferencesCommand { get; }

    public RelayCommand ConfirmSynchronizedRealtimeReferenceSwitchCommand { get; }

    public RelayCommand CancelSynchronizedRealtimeReferenceRelockCommand { get; }

    public RelayCommand UseCurrentRealtimeReferenceCommand { get; }

    public RelayCommand UseSelectedRealtimeReferenceCommand { get; }

    public RelayCommand CaptureRealtimeRawRingCommand { get; }

    public RelayCommand SaveRealtimeContactCalibrationCommand { get; }

    public RelayCommand SaveRealtimeDeviceCalibrationCommand { get; }

    public AsyncRelayCommand SyncStartCommand { get; }

    public AsyncRelayCommand StopAllDevicesCommand { get; }

    public AsyncRelayCommand SaveCapturedBlockCommand { get; }

    public AsyncRelayCommand SaveAllCapturedBlocksCommand { get; }

    public AsyncRelayCommand DemodulateHdf5Command { get; }

    public AsyncRelayCommand DemodulateRecentRunsCommand { get; }

    public AsyncRelayCommand InspectHdf5Command { get; }

    public AsyncRelayCommand ExportCsvCommand { get; }

    public AsyncRelayCommand ExportRecentRawCsvCommand { get; }

    public AsyncRelayCommand RefreshCatalogRunsCommand { get; }

    public AsyncRelayCommand RefreshDataRootStorageCommand { get; }

    public RelayCommand OpenDataRootCommand { get; }

    public RelayCommand OpenSelectedExperimentDirectoryCommand { get; }

    public AsyncRelayCommand ArchiveSelectedExperimentCommand { get; }

    public AsyncRelayCommand DeleteSelectedExperimentCommand { get; }

    public RelayCommand ReconcileSelectedExperimentCommand { get; }

    public RelayCommand BrowseDemodInputCommand { get; }

    public RelayCommand BrowseHdf5InspectCommand { get; }

    public RelayCommand BrowseExportSourceCommand { get; }

    public AsyncRelayCommand BrowseRealtimeBackendPathCommand { get; }

    public RelayCommand ClearRunDateFilterCommand { get; }

    public RelayCommand AcknowledgeErrorsCommand { get; }

    public AsyncRelayCommand RefreshImagingRunsCommand { get; }

    public RelayCommand ToggleReplayPlaybackCommand { get; }

    public AsyncRelayCommand CalculateReplayRoiCommand { get; }

    public RelayCommand SaveRoiCurveCommand { get; }

    public RelayCommand ClearRealtimeRoiCurveCommand { get; }

    public ObservableCollection<ImagingRunListItem> ImagingRuns => VisualizationWorkspace.ImagingRuns;

    public ImagingRunListItem? SelectedImagingRun
    {
        get => VisualizationWorkspace.SelectedImagingRun;
        set => VisualizationWorkspace.SelectedImagingRun = value;
    }

    public int ReplayFrameIndex
    {
        get => VisualizationWorkspace.ReplayFrameIndex;
        set => VisualizationWorkspace.ReplayFrameIndex = value;
    }

    public int ReplayMaxFrameIndex => VisualizationWorkspace.ReplayMaxFrameIndex;

    public bool HasReplayFrames => VisualizationWorkspace.HasReplayFrames;

    public ImageSource? ReplayImageSource
    {
        get => VisualizationWorkspace.ReplayImageSource;
        private set => VisualizationWorkspace.ReplayImageSource = value;
    }

    public Geometry? ReplayCurveGeometry
    {
        get => VisualizationWorkspace.ReplayCurveGeometry;
        private set => VisualizationWorkspace.ReplayCurveGeometry = value;
    }

    public FixedRoiTemporalVisualSnapshot ReplayFixedRoiTemporal
    {
        get => VisualizationWorkspace.ReplayFixedRoiTemporal;
        private set => VisualizationWorkspace.ReplayFixedRoiTemporal = value;
    }

    public Geometry? ReplayRoiCurveGeometry
    {
        get => VisualizationWorkspace.ReplayRoiCurveGeometry;
        private set => VisualizationWorkspace.ReplayRoiCurveGeometry = value;
    }

    public IReadOnlyList<RoiCurveMarker> ReplayRoiMarkers
    {
        get => VisualizationWorkspace.ReplayRoiMarkers;
        private set => VisualizationWorkspace.ReplayRoiMarkers = value;
    }

    public string ReplayRoiAxisStart
    {
        get => VisualizationWorkspace.ReplayRoiAxisStart;
        private set => VisualizationWorkspace.ReplayRoiAxisStart = value;
    }

    public string ReplayRoiAxisMiddle
    {
        get => VisualizationWorkspace.ReplayRoiAxisMiddle;
        private set => VisualizationWorkspace.ReplayRoiAxisMiddle = value;
    }

    public string ReplayRoiAxisEnd
    {
        get => VisualizationWorkspace.ReplayRoiAxisEnd;
        private set => VisualizationWorkspace.ReplayRoiAxisEnd = value;
    }

    public string ReplayFrameSummary
    {
        get => VisualizationWorkspace.ReplayFrameSummary;
        private set => VisualizationWorkspace.ReplayFrameSummary = value;
    }

    public string ReplayContactSummary
    {
        get => VisualizationWorkspace.ReplayContactSummary;
        private set => VisualizationWorkspace.ReplayContactSummary = value;
    }

    public string ReplayLoadStatus
    {
        get => VisualizationWorkspace.ReplayLoadStatus;
        private set => VisualizationWorkspace.ReplayLoadStatus = value;
    }

    public string ReplayRunSummary
    {
        get => VisualizationWorkspace.ReplayRunSummary;
        private set => VisualizationWorkspace.ReplayRunSummary = value;
    }

    public string ReplayRoiSummary
    {
        get => VisualizationWorkspace.ReplayRoiSummary;
        private set => VisualizationWorkspace.ReplayRoiSummary = value;
    }

    public bool IsReplayPlaying
    {
        get => VisualizationWorkspace.IsReplayPlaying;
        private set => VisualizationWorkspace.IsReplayPlaying = value;
    }

    public string ReplayPlayButtonText => IsReplayPlaying ? "暂停" : "播放";

    internal Task InitializeAfterFirstRenderAsync() =>
        dataStoreStartup.InitializeAfterFirstRenderAsync();

    private void ApplyDataStoreInitialization(DataStoreStartupResult result)
    {
        foreach (var warning in result.RawRecovery.Warnings)
        {
            AddRealtimeDiagnostic(warning);
        }

        Volatile.Write(ref catalogReady, true);
        replayController.SetReady(ready: true);
        ExperimentWorkspace.SetCatalogReady(
            ready: true,
            result.Storage,
            result.CanonicalRuns);
        RefreshImagingRunsCommand.RaiseCanExecuteChanged();
        RefreshCatalogRunsCommand.RaiseCanExecuteChanged();
        RaiseAcquisitionCanExecuteChanged();
        RaiseSaveCanExecuteChanged();
        SyncStartCommand.RaiseCanExecuteChanged();
        var recoverySummary = result.RawRecovery.RecoveredShardCount > 0
            ? $"；已按 HDF5 实际范围恢复 {result.RawRecovery.RecoveredShardCount} 个 raw 尾分片"
            : string.Empty;
        StatusMessage = result.RecoveredRunCount == 0
            ? $"统一数据根目录已准备：{DataRootPath}{recoverySummary}"
            : $"统一数据根目录已准备：{DataRootPath}{recoverySummary}；已恢复标记 {result.RecoveredRunCount} 条中断实验。";
        foreach (var run in result.InterruptedRuns)
        {
            experimentRunLifecycle.QueueCatchUp(run.ExperimentRunId, run.SetLabel, "startup-recovery");
        }
    }

    private void ApplyDataStoreInitializationFailure(Exception exception)
    {
        Volatile.Write(ref catalogReady, false);
        replayController.SetReady(ready: false);
        ExperimentWorkspace.SetCatalogReady(ready: false);
        RefreshCatalogRunsCommand.RaiseCanExecuteChanged();
        RaiseAcquisitionCanExecuteChanged();
        RaiseSaveCanExecuteChanged();
        SyncStartCommand.RaiseCanExecuteChanged();
        StatusMessage = $"数据目录初始化失败：{exception.Message}";
        AddRealtimeDiagnostic($"startup data store failed: {exception}");
    }

    private void StartPostDataStoreInitialization()
    {
        hardwareDiscovery.ApplyFirstRunCheck();
        if (realtimeBackend.OwnsBackend)
        {
            realtimeBackend.BeginManifestProbe(
                PostToUi,
                () => IsRealtimeImagingActive,
                AddRealtimeDiagnostic);
        }
    }

    private bool IsCatalogReady() => Volatile.Read(ref catalogReady);

    private Task RefreshImagingRunsAsync()
    {
        return replayController.RefreshImagingRunsAsync();
    }

    public void SetRoiCenterFromImagePoint(double x, double y, double width, double height)
    {
        VisualizationWorkspace.SetRoiCenterFromImagePoint(x, y, width, height);
    }
    private static string SanitizeFileNameComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "unnamed" : safe;
    }

    private static bool ConfirmExperimentLifecycle(string title, string message)
    {
        return MessageBox.Show(
                   message,
                   title,
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static void OpenDirectoryInShell(string directory)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void ReplaceExperimentRunsFromCurrentSources()
    {
        ExperimentWorkspace.UpdateLegacyImagingRuns(ImagingRuns, replayController.LegacyStorePaths);
    }

    private void UpsertCanonicalExperimentRun(ExperimentRunCatalogSummary summary)
    {
        ExperimentWorkspace.UpsertCanonicalRun(summary);
    }


    private HardwareEvidenceSnapshot CreateHardwareEvidenceSnapshot()
    {
        return new HardwareEvidenceSnapshot(
            currentSessionId,
            currentSessionStartedAt,
            CurrentSessionName,
            DataRootPath,
            CatalogPath,
            sessionDirectory,
            AcquisitionReadSampleRows,
            AcquisitionSampleRateHz,
            AcquisitionRange.ToString(),
            AcquisitionTriggerMode.ToString(),
            AcquisitionTriggerSource.ToString(),
            AcquisitionTriggerDelay,
            AcquisitionTriggerLength,
            AcquisitionTriggerLevel,
            DdsFrequencyHz,
            DdsDacChannel,
            DdsGain,
            DdsPhaseDegrees,
            DdsPgaGain,
            SelectedExcitationMode.ToString(),
            ExcitationChannelCycles,
            ExcitationScanTimes,
            ExcitationOverheadUs,
            BoundPairings.ToArray(),
            RecentRuns.ToArray());
    }


    private void RaiseHardwarePairingCommandStates()
    {
        SyncStartCommand.RaiseCanExecuteChanged();
        StopAllDevicesCommand.RaiseCanExecuteChanged();
        GenerateT25SmokePlanCommand.RaiseCanExecuteChanged();
        ExportPairingManifestCommand.RaiseCanExecuteChanged();
    }

    private void RaiseRunParameterPropertyChanges()
    {
        foreach (var propertyName in RunParameterPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }

        OnPropertyChanged(nameof(RealtimeExcitationSummary));
        OnPropertyChanged(nameof(RealtimeRecordingStateText));
        OnPropertyChanged(nameof(CurrentRunParameterProfileSummary));
        RaiseRealtimeDashboardStateChanged();
        RaiseDdsCanExecuteChanged();
        RaiseAcquisitionCanExecuteChanged();
    }

    private void RaiseDdsCanExecuteChanged()
    {
        SetDacCommand.RaiseCanExecuteChanged();
        StopDacCommand.RaiseCanExecuteChanged();
        SetPgaCommand.RaiseCanExecuteChanged();
        StartExcitationCommand.RaiseCanExecuteChanged();
        StopExcitationCommand.RaiseCanExecuteChanged();
        RaiseRealtimeCanExecuteChanged();
        RaiseAcquisitionCanExecuteChanged();
        SyncStartCommand.RaiseCanExecuteChanged();
        GenerateT25SmokePlanCommand.RaiseCanExecuteChanged();
        ExportPairingManifestCommand.RaiseCanExecuteChanged();
        SaveAllCapturedBlocksCommand.RaiseCanExecuteChanged();
    }

    private void RaiseAcquisitionCanExecuteChanged()
    {
        StartAcquisitionCommand.RaiseCanExecuteChanged();
        ReadAcquisitionBlockCommand.RaiseCanExecuteChanged();
        ReadAllActiveAcquisitionBlocksCommand.RaiseCanExecuteChanged();
        StopAcquisitionCommand.RaiseCanExecuteChanged();
        RaiseRealtimeCanExecuteChanged();
        StopAllDevicesCommand.RaiseCanExecuteChanged();
    }

    private void RaiseRealtimeCanExecuteChanged()
    {
        StartRealtimeImagingCommand.RaiseCanExecuteChanged();
        StartAllRealtimeImagingCommand.RaiseCanExecuteChanged();
        StopRealtimeImagingCommand.RaiseCanExecuteChanged();
        ResetRealtimeReferenceCommand.RaiseCanExecuteChanged();
        ConfirmRealtimeReferenceSwitchCommand.RaiseCanExecuteChanged();
        CancelRealtimeReferenceRelockCommand.RaiseCanExecuteChanged();
        PrepareSynchronizedRealtimeReferencesCommand.RaiseCanExecuteChanged();
        ConfirmSynchronizedRealtimeReferenceSwitchCommand.RaiseCanExecuteChanged();
        CancelSynchronizedRealtimeReferenceRelockCommand.RaiseCanExecuteChanged();
        UseCurrentRealtimeReferenceCommand.RaiseCanExecuteChanged();
        UseSelectedRealtimeReferenceCommand.RaiseCanExecuteChanged();
        CaptureRealtimeRawRingCommand.RaiseCanExecuteChanged();
        SaveRealtimeContactCalibrationCommand.RaiseCanExecuteChanged();
        SaveRealtimeDeviceCalibrationCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(RealtimeContactCalibrationExportStateText));
        OnPropertyChanged(nameof(RealtimeReferenceSwitchControlsVisibility));
        OnPropertyChanged(nameof(RealtimeSynchronizedReferenceControlsVisibility));
        OnPropertyChanged(nameof(RealtimeReferenceWindowSelectorVisibility));
        OnPropertyChanged(nameof(RealtimeManualReferenceActionText));
        BrowseRealtimeBackendPathCommand.RaiseCanExecuteChanged();
        StopAllDevicesCommand.RaiseCanExecuteChanged();
    }

    private void ResetRealtimeStartPresentation(RealtimeStartPresentation presentation)
    {
        RealtimeImageSource = null;
        RealtimeImageStats = presentation.ImageStats;
        SetRealtimeReferenceInvalidated(false);
        SetRealtimeLowConfidenceImage(false);
        RealtimeContactSummary = presentation.ContactSummary;
        RealtimeMultiFrequencySummary = presentation.MultiFrequencySummary;
        RealtimeReferenceSummary = presentation.ReferenceSummary;
        RealtimeBaselineIntegritySummary = presentation.BaselineIntegritySummary;
        RealtimeImagingSummary = presentation.ImagingSummary;
    }

    private void NotifyRealtimeRunStateChanged()
    {
        OnPropertyChanged(nameof(IsRealtimeImagingActive));
        RaiseRealtimeDashboardStateChanged();
        RaiseRunStateChanged();
        RaiseRealtimeCanExecuteChanged();
    }

    private void PublishRealtimeRunSnapshot(RealtimeRunState state)
    {
        var snapshot = state.RunCoordinator.Snapshot;
        PostToUi(() => RealtimeWorkspace.ApplyRunSnapshot(snapshot));
    }


    private double[] GetInterferenceFrequencyHzForPairing(
        PairingSummaryItem pairing,
        DeviceRunParameterProfile parameters)
    {
        return BoundPairings
            .Where(other => !string.Equals(other.Title, pairing.Title, StringComparison.Ordinal))
            .Select(runParameters.Get)
            .Select(otherParameters => DdsFrequencyPlan.CalculateActualFrequencyHz(
                DdsFrequencyPlan.CalculateTuningWord(otherParameters.DdsFrequencyHz)))
            .Where(frequency => Math.Abs(frequency - DdsFrequencyPlan.CalculateActualFrequencyHz(
                DdsFrequencyPlan.CalculateTuningWord(parameters.DdsFrequencyHz))) > 1e-9)
            .Distinct()
            .Order()
            .ToArray();
    }

    private bool TryValidateDemodDiscardCycles(out string? message)
    {
        return runParameters.CreateProfile().TryValidateDemodDiscardCycles(out message);
    }

    private void CaptureRealtimeRawRing()
    {
        var label = SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title;
        if (label is null ||
            !realtimeSessions.TryGetState(label, out var state) ||
            state.Config is not { } config)
        {
            StatusMessage = "当前没有运行中的原始环形缓冲。";
            return;
        }

        if (!TryCaptureRealtimeRawRing(config, state, "manual"))
        {
            StatusMessage = $"{label} 原始环形缓冲尚无可保存数据。";
        }
    }

    private bool CanCaptureRealtimeRawRing()
    {
        var label = SelectedRealtimeDisplayPairing?.Title ?? SelectedBoundPairing?.Title;
        return label is not null &&
            realtimeSessions.TryGetState(label, out var state) &&
            state.IsActive &&
            state.Config?.StoragePolicy.KeepRawRingBuffer == true;
    }

    private void CompleteRealtimeAcquisitionLoopUi(
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        PostToUi(() =>
        {
            realtimeSessions.MarkLoopStopped(config.SetLabel);
            ddsRuns.MarkStopped(config.SetLabel);
            if (config.PersistImagingFrames)
            {
                _ = RefreshImagingRunsAsync();
            }

            FlushRealtimePreviewSnapshots();
            _ = FinalizeLiveReplayAsync(config.ImagingRunId, state.TotalRawSamples, config.SetLabel);
            realtimePreview.StopPump();
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} {config.SetLabel} realtime imaging stopped");
            RaiseRunStateChanged();
        });
    }

    private void RaiseRealtimeReferenceCommandsChanged()
    {
        PostToUi(() =>
        {
            ResetRealtimeReferenceCommand.RaiseCanExecuteChanged();
            UseCurrentRealtimeReferenceCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(RealtimeManualReferenceActionText));
        });
    }

    internal static void EnsureRealtimeConsumerRunning(Task consumerTask)
    {
        ArgumentNullException.ThrowIfNull(consumerTask);
        if (!consumerTask.IsCompleted)
        {
            return;
        }

        consumerTask.GetAwaiter().GetResult();
        throw new InvalidOperationException("Realtime demodulation consumer stopped unexpectedly.");
    }

    internal static int GetRealtimeReadRows(int configuredRows)
    {
        return Math.Max(RealtimeReadRowsPerBlock, configuredRows);
    }

    internal static TimeSpan GetRealtimeReconstructionTimeout(int completedReconstructionFrames)
    {
        return RealtimeReconstructionController.GetRequestTimeout(completedReconstructionFrames);
    }

    private async Task<DdsCommandResult> SendRealtimeDdsCommandAsync(
        string setLabel,
        string actionName,
        Func<Task<DdsCommandResult>> send)
    {
        AddRealtimeDiagnostic($"{setLabel} DDS {actionName} begin");
        var result = await send().ConfigureAwait(false);
        var acknowledgement = result.Response is null ? "ACK=-" : $"ACK={result.Response.Hex}";
        AddRealtimeDiagnostic($"{setLabel} DDS {actionName} ok {result.PacketHex} {acknowledgement}");
        PostToUi(() => AddPanelLog(
            RealtimeImagingLogs,
            $"{DateTime.Now:HH:mm:ss} {setLabel} {actionName} {result.PacketHex} {acknowledgement}"));
        return result;
    }

    private async Task<RealtimeDdsStartupResult> ConfigureRealtimeDdsAsync(
        RealtimeImagingRunConfig config,
        CancellationToken cancellationToken)
    {
        await TrySendRealtimeDdsStopWithFreshPortAsync(
            config.SetLabel,
            config.DdsPortName,
            cancellationToken).ConfigureAwait(false);

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            DdsSerialPortTransport? transport = null;
            var ownershipTransferred = false;
            try
            {
                transport = new DdsSerialPortTransport(config.DdsPortName);
                var ddsClient = new DdsProtocolClient(transport);
                await SendRealtimeDdsCommandAsync(
                    config.SetLabel,
                    "设置 DAC",
                    () => ddsClient.SetDacAsync(config.DacSettings, cancellationToken)).ConfigureAwait(false);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                await SendRealtimeDdsCommandAsync(
                    config.SetLabel,
                    "设置 PGA",
                    () => ddsClient.SetPgaAsync(config.PgaGain, cancellationToken)).ConfigureAwait(false);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                var startResult = await SendRealtimeDdsCommandAsync(
                    config.SetLabel,
                    "启动激励",
                    () => ddsClient.StartExcitationAsync(config.ExcitationSettings, cancellationToken)).ConfigureAwait(false);
                var execution = startResult.ExecutionReceipt ?? throw new DdsProtocolException(
                    "DDS firmware v2 ACK did not include a validated execution receipt.");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                var startupResult = new RealtimeDdsStartupResult(
                    transport,
                    execution,
                    startResult.FirmwareCapabilities ?? throw new DdsProtocolException(
                        "DDS firmware v2 capability response was not retained."),
                    "configured-v2-ack");
                ownershipTransferred = true;
                return startupResult;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRetryableDdsStartupException(ex))
            {
                lastFailure = ex;
                AddRealtimeDiagnostic($"{config.SetLabel} DDS configure attempt {attempt} failed: {ex}");
                PostToUi(() => AddPanelLog(
                    RealtimeImagingLogs,
                    $"{DateTime.Now:HH:mm:ss} {config.SetLabel} DDS configure retry {attempt}/2 {ex.Message}"));
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    transport?.Dispose();
                }
            }

            if (attempt < 2)
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                continue;
            }

            break;
        }

        throw new InvalidOperationException(
            $"{config.SetLabel} DDS 串口 {config.DdsPortName} 未获得 firmware v2 ACK，已禁止启动采集：{lastFailure?.Message ?? "未知错误"}。请升级或重新刷写 DDS 固件，并在连接步骤重新扫描/绑定。",
            lastFailure);
    }

    private async Task TrySendRealtimeDdsStopWithFreshPortAsync(
        string setLabel,
        string portName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var stopTransport = new DdsSerialPortTransport(portName);
            var stopClient = new DdsProtocolClient(stopTransport);
            await SendRealtimeDdsCommandAsync(
                setLabel,
                "停止旧激励",
                () => stopClient.StopExcitationAsync(cancellationToken)).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRetryableDdsStartupException(ex))
        {
            AddRealtimeDiagnostic($"{setLabel} DDS best-effort stop ignored: {ex}");
            PostToUi(() => AddPanelLog(
                RealtimeImagingLogs,
                $"{DateTime.Now:HH:mm:ss} {setLabel} DDS stop preflight warning {ex.Message}"));
        }
    }

    private static bool IsRetryableDdsStartupException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TimeoutException;
    }

    private void UpdateSynchronizedReferenceSwitchSummary(string? actionGroupId)
    {
        if (string.IsNullOrWhiteSpace(actionGroupId))
        {
            return;
        }

        var switched = realtimeSessions.States
            .Where(candidate => string.Equals(
                candidate.ActiveReferenceActionGroupId,
                actionGroupId,
                StringComparison.Ordinal))
            .OrderBy(candidate => candidate.SetLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (switched.Length == 0)
        {
            return;
        }

        var total = switched.Max(candidate => candidate.ActiveReferenceSynchronizedSetCount);
        RealtimeSynchronizedReferenceSummary =
            $"多集合同步切换 {switched.Length}/{total} · action {actionGroupId[..Math.Min(8, actionGroupId.Length)]}\n" +
            string.Join(
                "\n",
                switched.Select(candidate =>
                    $"{candidate.SetLabel}: e{candidate.ReferenceEpoch} · " +
                    $"窗口 skew {candidate.ActiveReferenceWindowSkewMilliseconds.GetValueOrDefault() / 1000.0:+0.000;-0.000;0.000}s · " +
                    $"switch skew {candidate.ActiveReferenceSwitchSkewMilliseconds.GetValueOrDefault() / 1000.0:+0.000;-0.000;0.000}s"));
    }

    private void NotifyRealtimeReferenceUi(string setLabel, RealtimeReferenceUiChange change)
    {
        PostToUi(() =>
        {
            if (change is RealtimeReferenceUiChange.RefreshWindows or
                RealtimeReferenceUiChange.RefreshWindowsAndUseCurrentCommand or
                RealtimeReferenceUiChange.RefreshWindowsAndAllCommands)
            {
                realtimeReferenceActions.RefreshWindowOptions(setLabel);
            }

            if (change is RealtimeReferenceUiChange.RefreshWindowsAndUseCurrentCommand or
                RealtimeReferenceUiChange.UseCurrentCommand)
            {
                UseCurrentRealtimeReferenceCommand.RaiseCanExecuteChanged();
            }

            if (change == RealtimeReferenceUiChange.RefreshWindowsAndAllCommands)
            {
                RaiseRealtimeCanExecuteChanged();
            }

            if (change == RealtimeReferenceUiChange.CalibrationState)
            {
                OnPropertyChanged(nameof(RealtimeContactCalibrationExportStateText));
            }
        });
    }

    private void PublishRealtimeReferenceSwitchUi(RealtimeReferenceSwitchUiUpdate update)
    {
        PostToUi(() =>
        {
            RealtimeReferenceRelockStateText =
                $"重锁：已在 block {update.BlockNumber} 原子切换 e{update.OldEpoch} → e{update.NewEpoch}；ROI 已分段。";
            UpdateSynchronizedReferenceSwitchSummary(update.ActionGroupId);
            StatusMessage = $"{update.SetLabel} 新参考 e{update.NewEpoch} 已生效；下一有效目标开始成像与 ROI。";
            RaiseRealtimeCanExecuteChanged();
        });
    }


    private void SetRealtimeReferenceInvalidated(bool invalidated)
    {
        if (realtimeReferenceInvalidated == invalidated)
        {
            return;
        }

        realtimeReferenceInvalidated = invalidated;
        OnPropertyChanged(nameof(RealtimeReferenceInvalidatedVisibility));
        OnPropertyChanged(nameof(RealtimeLowConfidenceImageVisibility));
    }

    private void SetRealtimeLowConfidenceImage(bool lowConfidence)
    {
        if (realtimeLowConfidenceImage == lowConfidence)
        {
            return;
        }

        realtimeLowConfidenceImage = lowConfidence;
        OnPropertyChanged(nameof(RealtimeLowConfidenceImageVisibility));
    }

    private void StartBufferedAcquisitionPreviewPump()
    {
        bufferedAcquisitionPreviewPump.Start();
    }

    private void StopBufferedAcquisitionPreviewPumpIfIdle()
    {
        bufferedAcquisitionPreviewPump.StopIfIdle();
    }

    private void RequestRealtimePreviewFlush()
    {
        realtimePreviewPump.RequestFlush();
    }

    private static string CreateRealtimeReferenceModeStatus(
        RealtimeImagingRunConfig config,
        RealtimeRunState state)
    {
        var scale = config.ReferenceScalePolicy == EcdCwrReferenceScalePolicy.CommonScaleNormalized
            ? "公共尺度归一化"
            : "保留物理尺度";
        if (state.ReplacementReferenceCollecting)
        {
            var scope = state.ReplacementReferenceSynchronizedSetCount > 1
                ? $"多集合 action {state.ReplacementReferenceActionGroupId?[..8]}"
                : "单集合";
            var readiness = state.ReplacementPreparedReference is null
                ? "后台收集中"
                : Volatile.Read(ref state.ReplacementSwitchRequested) == 0
                    ? "新参考待确认"
                    : "已确认，待有效边界";
            return $"参考模式：当前 e{state.ReferenceEpoch} 保持活动 · {scope} {readiness} · {scale}";
        }

        if (state.StartupDegradedReference is not null)
        {
            return $"参考模式：故障降级 · {scale}";
        }

        if (state.ReferenceIsProvisional)
        {
            return $"参考模式：快速预览（临时） · {scale}";
        }

        if (state.ReferenceVoltage208 is null)
        {
            return $"参考模式：尚未锁定 · 候选窗口收集中 · {scale}";
        }

        return string.Equals(state.ActiveReferenceLockKind, "user_selected", StringComparison.Ordinal)
            ? $"参考模式：用户选定高质量窗口 · 正常置信 · {scale}"
            : $"参考模式：自动稳定锁定 · {scale}";
    }

    private RealtimeDevicePreviewCache GetRealtimePreviewCacheUnsafe(string setLabel)
    {
        return realtimePreviewState.GetOrCreateUnsafe(setLabel);
    }

    private bool IsRealtimeDisplaySet(string setLabel)
    {
        var selected = SelectedRealtimeDisplayPairing?.Title
            ?? SelectedBoundPairing?.Title
            ?? string.Empty;
        return string.Equals(selected, setLabel, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRealtimeDisplayFromCache(string? setLabel)
    {
        var dispatcher = GetUiDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyRealtimeDisplayFromCache(setLabel), DispatcherPriority.Render);
            return;
        }

        var update = realtimePreview.CreateDisplayUpdate(setLabel);
        ApplyRealtimePreviewUiUpdate(update);
    }

    private void ApplyRealtimePreviewUiUpdate(RealtimePreviewUiUpdate update)
    {
        if (!string.IsNullOrWhiteSpace(update.ReferenceSummary))
        {
            RealtimeReferenceSummary = update.ReferenceSummary;
        }

        if (!string.IsNullOrWhiteSpace(update.BaselineIntegritySummary))
        {
            RealtimeBaselineIntegritySummary = update.BaselineIntegritySummary;
        }

        if (!string.IsNullOrWhiteSpace(update.ContactSummary))
        {
            RealtimeContactSummary = update.ContactSummary;
        }

        if (!string.IsNullOrWhiteSpace(update.MultiFrequencySummary))
        {
            RealtimeMultiFrequencySummary = update.MultiFrequencySummary;
        }

        if (!string.IsNullOrWhiteSpace(update.DataQualityStatus))
        {
            RealtimeDataQualityStatus = update.DataQualityStatus;
        }

        if (!string.IsNullOrWhiteSpace(update.ReferenceModeStatus))
        {
            RealtimeReferenceModeStatus = update.ReferenceModeStatus;
        }

        if (!string.IsNullOrWhiteSpace(update.ReconstructionQualityStatus))
        {
            RealtimeReconstructionQualityStatus = update.ReconstructionQualityStatus;
        }

        if (!string.IsNullOrWhiteSpace(update.RoiReadinessStatus))
        {
            RealtimeRoiReadinessStatus = update.RoiReadinessStatus;
        }

        if (update.ReferenceInvalidated is { } invalidated)
        {
            SetRealtimeReferenceInvalidated(invalidated);
        }

        if (update.LowConfidenceImage is { } lowConfidence)
        {
            SetRealtimeLowConfidenceImage(lowConfidence);
        }

        if (!string.IsNullOrWhiteSpace(update.ImagingSummary))
        {
            RealtimeImagingSummary = update.ImagingSummary;
        }

        foreach (var line in update.LogLines)
        {
            AddPanelLog(RealtimeImagingLogs, line);
        }

        if (update.LiveFrameCommit is { } liveCommit &&
            IsRealtimeDisplaySet(liveCommit.Frame.SetLabel))
        {
            _ = derivedPersistence.CommitLivePresentationAsync(liveCommit);
        }
    }

    private async Task FinalizeLiveReplayAsync(Guid experimentRunId, long rawDenominator, string setLabel)
    {
        try
        {
            await derivedPersistence
                .PublishLiveRevisionAsync(experimentRunId, rawDenominator)
                .ConfigureAwait(false);
            PostToUi(() => _ = RefreshImagingRunsAsync());
        }
        catch (Exception ex)
        {
            PostToUi(() => AddRealtimeDiagnostic(
                $"{setLabel} live replay finalization failed: {ex.Message}"));
        }
    }

    private void FlushRealtimePreviewSnapshots()
    {
        var dispatcher = GetUiDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(FlushRealtimePreviewSnapshots, DispatcherPriority.Render);
            return;
        }

        var update = realtimePreview.CreatePendingUpdate();
        ApplyRealtimePreviewUiUpdate(update);
    }

    private void RaiseSaveCanExecuteChanged()
    {
        SaveCapturedBlockCommand.RaiseCanExecuteChanged();
        SaveAllCapturedBlocksCommand.RaiseCanExecuteChanged();
    }

    private bool TryCaptureRealtimeRawRing(
        RealtimeImagingRunConfig config,
        RealtimeRunState state,
        string reason)
    {
        if (state.RawRingBuffer?.Snapshot() is not { } snapshot ||
            state.RawRingAcquisitionMetadata is not { } acquisition)
        {
            return false;
        }

        var batch = new RealtimeRawBatch<RealtimeRawPersistenceContext>(
            new RealtimeRawPersistenceContext(
                config.Pairing,
                config.ExcitationMetadata,
                acquisition),
            snapshot.Segments,
            snapshot.ValueCount,
            state.RunCoordinator.AllocateRawSegmentSequence(),
            Math.Max(0, state.TotalRawSamples - (snapshot.ValueCount / Usb2070Constants.RequiredMeasurementChannelCount)),
            state.TotalRawSamples,
            snapshot.StartedAt,
            $"raw-ring-{reason}",
            []);
        var persistenceTask = rawPersistence.PersistRealtimeAsync(batch, config, state);
        state.RunCoordinator.TrackRawPersistence(persistenceTask);

        AddRealtimeDiagnostic(
            $"{config.SetLabel} raw ring snapshot reason={reason} values={snapshot.ValueCount} bytes={snapshot.ValueCount * BytesPerAdcValue}");
        PostToUi(() =>
        {
            StatusMessage = $"{config.SetLabel} 已请求保存最近原始片段（{snapshot.ValueCount * BytesPerAdcValue / 1024.0 / 1024.0:F1} MiB，{reason}）。";
            AddPanelLog(
                RealtimeImagingLogs,
                $"{DateTime.Now:HH:mm:ss} {config.SetLabel} raw ring capture {reason}");
        });
        return true;
    }


    private void RaiseRunStateChanged()
    {
        OnPropertyChanged(nameof(IsSelectedExciting));
        OnPropertyChanged(nameof(IsSelectedAcquiring));
        OnPropertyChanged(nameof(CanEditExcitationSettings));
        OnPropertyChanged(nameof(CanEditAcquisitionSettings));
        OnPropertyChanged(nameof(CanEditRealtimeRunSettings));
        OnPropertyChanged(nameof(CanRetainBackendExchange));
        OnPropertyChanged(nameof(CanEditRealtimeBackendSettings));
        RaiseRealtimeDashboardStateChanged();
        RaiseDdsCanExecuteChanged();

        foreach (var pairing in BoundPairings)
        {
            pairing.IsExciting = ddsRuns.IsActive(pairing.Title);
            pairing.IsAcquiring = acquisitionController.IsActive(pairing.Title)
                || realtimeSessions.IsSetActive(pairing.Title);
        }
    }

    private void RaiseRealtimeDashboardStateChanged()
    {
        OnPropertyChanged(nameof(RealtimeConnectionStateText));
        OnPropertyChanged(nameof(RealtimePowerStateText));
        OnPropertyChanged(nameof(RealtimeExcitationSummary));
        OnPropertyChanged(nameof(RealtimeRecordingStateText));
        OnPropertyChanged(nameof(RealtimeContactCalibrationExportStateText));
        OnPropertyChanged(nameof(RealtimeAcquisitionStateText));
    }

    private void ApplySelectedRunPaths(CatalogRunSummaryItem run)
    {
        var rawPath = run.Summary.Hdf5Path;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        if (!run.RawHdf5Exists)
        {
            DemodInputHdf5Path = string.Empty;
            DemodOutputHdf5Path = string.Empty;
            Hdf5InspectPath = string.Empty;
            ExportSourceHdf5Path = string.Empty;
            ExportCsvPath = string.Empty;
            StatusMessage = $"catalog 记录仍在，但 raw HDF5 已缺失：{rawPath}";
            return;
        }

        DemodInputHdf5Path = rawPath;
        DemodOutputHdf5Path = string.Empty;
        Hdf5InspectPath = rawPath;
        ExportSourceHdf5Path = rawPath;
        ExportCsvPath = string.Empty;
    }

    private void ApplySelectedExperiment(ExperimentRunListItem? experiment)
    {
        ExperimentWorkspace.DataTools.ApplySelectedExperiment(experiment);
        if (experiment is null)
        {
            VisualizationWorkspace.SetSelectedImagingRun(null, notifySelection: false);
            replayController.StopPlayback();
            replayController.Clear();
            return;
        }

        var rawPath = experiment.PrimaryRawHdf5Path;
        if (!string.IsNullOrWhiteSpace(rawPath) && !File.Exists(rawPath))
        {
            StatusMessage = $"实验记录仍在，但首个 raw HDF5 已缺失：{rawPath}";
        }

        if (experiment.Run is not null)
        {
            if (SelectedImagingRun is not null)
            {
                VisualizationWorkspace.SetSelectedImagingRun(null, notifySelection: false);
            }

            _ = replayController.LoadCanonicalExperimentAsync(experiment);
        }
        else
        {
            SelectedImagingRun = experiment?.ImagingRun;
        }
    }

    private bool CanBrowseRealtimeBackendPath()
    {
        return !IsRealtimeImagingActive;
    }

    private async Task SelectRealtimeBackendProfileAsync(string? profileName)
    {
        var normalized = profileName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }
        if (string.Equals(realtimeBackend.Options.BackendProfile, normalized, StringComparison.Ordinal))
        {
            return;
        }

        if (IsRealtimeImagingActive)
        {
            OnPropertyChanged(nameof(RealtimeBackendProfile));
            return;
        }

        try
        {
            if (await realtimeBackend.SelectProfileAsync(normalized).ConfigureAwait(true) is null)
            {
                return;
            }

            StatusMessage = $"PyEIDORS 后端路线已切换为 {RealtimeBackendProfileLabel}。";
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} PyEIDORS backend profile set {realtimeBackend.Options.BackendProfile}");
        }
        catch (Exception ex)
        {
            realtimeBackend.SetStatus($"PyEIDORS 后端路线切换失败：{ex.Message}");
            StatusMessage = RealtimeBackendStatus;
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} PyEIDORS backend profile failed {ex.Message}");
            OnPropertyChanged(nameof(RealtimeBackendProfile));
        }
    }

    private async Task BrowseRealtimeBackendPathAsync()
    {
        try
        {
            if (PromptOpenRealtimeBackendDirectory() is not { } selectedPath)
            {
                return;
            }

            if (IsRealtimeImagingActive)
            {
                realtimeBackend.SetStatus("实时成像运行中，已取消 PyEIDORS 后端路径修改。");
                StatusMessage = RealtimeBackendStatus;
                return;
            }

            await realtimeBackend.SelectRepositoryPathAsync(selectedPath).ConfigureAwait(true);
            StatusMessage = "PyEIDORS 后端路径已更新；下一次实时重构会使用新路径。";
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} PyEIDORS backend path set {realtimeBackend.Options.DistroName}:{realtimeBackend.Options.BackendRepositoryPath}");
        }
        catch (Exception ex)
        {
            realtimeBackend.SetStatus($"PyEIDORS 后端路径设置失败：{ex.Message}");
            StatusMessage = RealtimeBackendStatus;
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} PyEIDORS backend path failed {ex.Message}");
        }
    }

    private string? PromptOpenRealtimeBackendDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 WSL2 中的 PyEIDORS 仓库目录"
        };
        var initialDirectory = realtimeBackend.InitialDirectory;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void RaiseRealtimeBackendPropertiesChanged()
    {
        OnPropertyChanged(nameof(RealtimeBackendDistroName));
        OnPropertyChanged(nameof(RealtimeBackendRepositoryPath));
        OnPropertyChanged(nameof(RealtimeBackendProfileOptions));
        OnPropertyChanged(nameof(RealtimeBackendProfile));
        OnPropertyChanged(nameof(RealtimeBackendProfileLabel));
        OnPropertyChanged(nameof(RealtimeBackendNixProfile));
        OnPropertyChanged(nameof(RealtimeBackendDisplayPath));
        OnPropertyChanged(nameof(RealtimeBackendConfigPath));
        OnPropertyChanged(nameof(RealtimeBackendStatus));
        OnPropertyChanged(nameof(CanEditRealtimeBackendSettings));
        BrowseRealtimeBackendPathCommand.RaiseCanExecuteChanged();
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

    private string? PromptSaveFile(string currentPath, string filter, string defaultExtension)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExtension,
            OverwritePrompt = true,
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
        else if (Directory.Exists(DataRootPath))
        {
            dialog.InitialDirectory = DataRootPath;
        }
    }

    private static bool IsMemoryPressureHigh()
    {
        var now = DateTimeOffset.UtcNow;
        lock (MemoryPressureGate)
        {
            if (now - lastMemoryPressureProbeAt < MemoryPressureProbeInterval)
            {
                return cachedMemoryPressureHigh;
            }

            var info = GC.GetGCMemoryInfo();
            cachedMemoryPressureHigh = info.HighMemoryLoadThresholdBytes > 0
                && info.MemoryLoadBytes >= (long)(info.HighMemoryLoadThresholdBytes * MemoryPressureFlushRatio);
            lastMemoryPressureProbeAt = now;
            return cachedMemoryPressureHigh;
        }
    }

    private void AddRealtimeDiagnostic(string message) => realtimeDiagnostics.Record(message);

    private void ReportRealtimeOperatorDiagnostic(string message)
    {
        AddRealtimeDiagnostic(message);
        PostToUi(() =>
        {
            StatusMessage = message;
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} {message}");
        });
    }

    private Dispatcher? GetUiDispatcher() => uiActions.Dispatcher;

    private void PostToUi(Action action) => uiActions.Post(action);

    internal Task InvokeOnUiAsync(Action action) => uiActions.InvokeAsync(action);

    private void AddPanelLog(ObservableCollection<string> logs, string message) =>
        uiActions.Post(() => AddPanelLogCore(logs, message));

    private void AddPanelLogCore(ObservableCollection<string> logs, string message)
    {
        lock (panelLogGate)
        {
            logs.Insert(0, message);
            var entryLimit = ReferenceEquals(logs, ActivityLogs)
                ? ActivityLogEntryLimit
                : PanelLogEntryLimit;
            while (logs.Count > entryLimit)
            {
                logs.RemoveAt(logs.Count - 1);
            }
        }
    }

    private static long CalculateDefaultAutoFlushByteThreshold()
    {
        var info = GC.GetGCMemoryInfo();
        var availableBytes = info.TotalAvailableMemoryBytes > 0
            ? info.TotalAvailableMemoryBytes
            : 8L * 1024L * 1024L * 1024L;
        return Math.Clamp(availableBytes / 32, MinAutoFlushBytes, MaxAutoFlushBytes);
    }

    public async Task ShutdownAsync(TimeSpan? shutdownWait = null)
    {
        if (disposed)
        {
            return;
        }

        var wait = NormalizeShutdownWait(shutdownWait);
        realtimeRunCommands.RequestStop(showIdleMessage: false);
        await WaitForRealtimeImagingShutdownAsync(wait).ConfigureAwait(true);
        await StopTrackedExcitationsWithTimeoutAsync(wait).ConfigureAwait(true);
        Dispose();
    }

    private static TimeSpan NormalizeShutdownWait(TimeSpan? shutdownWait)
    {
        if (shutdownWait is not { } wait || wait <= TimeSpan.Zero)
        {
            return RealtimeShutdownWait;
        }

        return wait;
    }

    private async Task WaitForRealtimeImagingShutdownAsync(TimeSpan wait)
    {
        var tasks = realtimeSessions.GetTrackedTasks().ToArray();

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(wait).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (TimeoutException)
        {
            AddPanelLog(
                RealtimeImagingLogs,
                $"{DateTime.Now:HH:mm:ss} realtime imaging shutdown timeout after {wait.TotalMilliseconds:F0}ms, continuing close");
            StatusMessage = "实时成像关闭等待超时，软件将继续释放可释放资源。";
        }
        catch (Exception ex)
        {
            AddPanelLog(RealtimeImagingLogs, $"{DateTime.Now:HH:mm:ss} realtime imaging shutdown warning {ex.Message}");
        }
    }

    private async Task StopTrackedExcitationsWithTimeoutAsync(TimeSpan wait)
    {
        var stopTask = hardwareRunCommands.StopTrackedExcitationsAsync();
        var completed = await Task.WhenAny(stopTask, Task.Delay(wait)).ConfigureAwait(true);
        if (completed == stopTask)
        {
            await stopTask.ConfigureAwait(true);
            return;
        }

        AddPanelLog(DdsCommandLogs, $"{DateTime.Now:HH:mm:ss} shutdown DDS stop timeout after {wait.TotalMilliseconds:F0}ms");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        realtimeSessions.Dispose();
        realtimeRawPersistenceService.Dispose();
        try
        {
            if (!derivedPersistence.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
            {
                AddRealtimeDiagnostic("derived persistence shutdown timeout");
            }
        }
        catch (Exception ex)
        {
            AddRealtimeDiagnostic($"derived persistence shutdown warning: {ex.Message}");
        }

        ddsRuns.Dispose();

        bufferedAcquisitionPreviewPump.Dispose();
        acquisitionController.Dispose();
        realtimePreviewPump.Dispose();
        roiInteractions.Dispose();
        HardwareWorkspace.BoundPairings.CollectionChanged -= OnPseudo3dBoundPairingsChanged;
        pseudo3dVisualization.Dispose();
        replayController.Dispose();
        realtimeBackend.Dispose();
    }

    public sealed record DdsCurrentOption(string Label, double Gain)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    public sealed record ContactSubjectProfileOption(string Label, string Value)
    {
        public override string ToString()
        {
            return Label;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
}
