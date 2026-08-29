using EitHost.Core.Acquisition;
using EitHost.Core.Analysis;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Concurrency;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Frames;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels;

internal sealed class RealtimeRunState : ReferenceReconstructionCoordinator
{
    private const int ContactCalibrationMaximumFrames = 300;

    public RealtimeRunState(string setLabel)
        : base(setLabel, ContactCalibrationMaximumFrames)
    {
        RunCoordinator = new RealtimeRunCoordinator(setLabel);
    }

    internal RealtimeRunCoordinator RunCoordinator { get; }

    public CancellationTokenSource Cancellation => RunCoordinator.Cancellation;

    public Task? Task { get; set; }

    internal bool IsStopRequested => Cancellation.IsCancellationRequested;

    internal void RequestStop()
    {
        if (!RunCoordinator.RequestStop() && !Cancellation.IsCancellationRequested)
        {
            Cancellation.Cancel();
        }
    }

    internal RealtimeImagingRunConfig? Config { get; set; }

    internal bool ExperimentCatalogRunStarted { get; set; }

    internal DateTimeOffset ExperimentStartedAt { get; set; }

    internal long TotalRawSamples
    {
        get => RunCoordinator.Snapshot.TotalRawSamples;
        set => RunCoordinator.RecordRawProgress(value);
    }

    internal void RecordAcquisitionDiscontinuity(RawAcquisitionDiscontinuityEvent discontinuity)
    {
        RunCoordinator.RecordAcquisitionDiscontinuity(discontinuity);
    }

    internal DateTimeOffset CalculateAcquiredAt(DateTimeOffset startedAt, long sampleIndex, int sampleRateHz)
    {
        return RunCoordinator.CalculateAcquiredAt(startedAt, sampleIndex, sampleRateHz);
    }

    internal int DemodPersistedBlocks;
    internal int DemodPersistenceFailures;
    internal RealtimeRawRingBuffer? RawRingBuffer { get; set; }
    internal RealtimeRawChannelBuffer? RawPreviewBuffer { get; set; }
    internal Usb2070AcquisitionMetadata? RawRingAcquisitionMetadata { get; set; }
    internal LatestOnlyAsyncWorker<RealtimeVisualizationWorkItem>? VisualizationWorker { get; set; }
    internal VisualizationRenderer.RealtimeImageRasterCache ImageRasterCache { get; } = new();
    internal volatile RealtimeRoiGeometry? RoiGeometry;
    internal EcdCwrConsecutiveCenteredWindow<RealtimeTemporalCandidate> TemporalWindow { get; } = new();
    internal RealtimeSampleContinuityMonitor SampleContinuity { get; } = new();
    internal int BlocksProcessed;
    internal int HighQualityBlocks;
    internal int LowQualityBlocks;
    internal int ConsecutiveLowQualityBlocks;
    internal int RawPreviewSliceMisses;
    internal int ConsecutivePairingMismatchBlocks;
    internal int FixedRoiTemporalRebuildPending;
    internal bool PairingMismatchWarningRaised;
    internal bool TimingMismatchWarningRaised;
    internal double[]? PreviousDemodulationReal208 { get; set; }
    internal double[]? PreviousDemodulationImaginary208 { get; set; }
    internal int PreviousDemodulationBlockNumber { get; set; }
    internal RealtimeDemodulationStepStability? LatestDemodulationStepStability { get; set; }
    internal long LastPreviewTicks;
    internal long LastDemodPreviewTicks;
    internal long LastBoundaryFitPreviewTicks;
    internal long LastImagePreviewTicks;
    internal long LastRoiPreviewTicks;
    internal long LastFixedRoiTemporalTicks;
    internal long LastStatusTicks;
    internal long LastContactAnalysisTicks;
    internal long LastDisplayCompletedTicks;
    internal long DisplayFrameCount;
    internal double RenderEwmaMilliseconds;
    internal long ContactDiagnosticsRunCount;
    internal long ContactDiagnosticsSkippedCount;
    internal double LastContactDiagnosticElapsedMs;
    internal double MaxContactDiagnosticElapsedMs;
    internal long LastContactDiagnosticLogTicks;
    internal long LastSampleDiscontinuityLogTicks;
    internal long UnloggedSampleDiscontinuityCount;
    internal long UnloggedMissingSampleRows;
    internal long UnloggedUsbOverflowCount;
    internal RealtimeSampleDiscontinuity? LatestSampleDiscontinuity;
    internal bool SampleContinuityRecoveryPending;
    internal int LastContactDiagnosticSeverity = -1;
    internal bool ContactDiagnosticSystemEscalationLogged;
    internal bool LastContactDiagnosticWasDegraded;
    internal long PipelineDroppedBlocks;
    internal long PipelineDroppedSampleRows;
    internal long PipelineSampleGaps;
    internal long PipelineUsbOverflows;
    internal int PipelineQueuedSamples;
    internal int PipelineQueueHighWater;
    internal int PipelineCadenceRefreshRejected;
    internal long LastReconImageStatsTicks;
    internal long RawMetricLastTimestamp;
    internal long RawMetricLastAllocatedBytes;

    internal bool IsActive => RunCoordinator.IsActive || Task is { IsCompleted: false };
}

internal sealed record RealtimeTemporalCandidate(
    RealtimeDemodulatedBlock Block,
    double[] Target,
    double[] BaseWeights,
    string BaseWeightPolicyVersion,
    ElectrodeContactDiagnosticResult? ContactResult,
    EcdCwrWaveformTemplateDisplayPackage? TemplateDisplayPackage);

internal sealed record RealtimeImagingRunConfig(
    PairingSummaryItem Pairing,
    string SetLabel,
    string DdsPortName,
    Usb2070Device UsbDevice,
    DdsDacSettings DacSettings,
    DdsExcitationSettings ExcitationSettings,
    byte PgaGain,
    Usb2070AcquisitionSettings AcquisitionSettings,
    int ReadRows,
    int FramesPerBlock,
    int MinimumAcceptedFrames,
    double DemodDiscardLeadingCycles,
    double DemodDiscardTrailingCycles,
    double MeshSize,
    double DifferenceLambda,
    string ReconstructionRoute,
    bool CustomLambdaEnabled,
    string DifferenceOrientation,
    RealtimeStoragePolicy StoragePolicy,
    bool PersistReconstructionResults,
    bool EnableOutlierDetection,
    bool EnableOutlierCompensation,
    bool EnableTemporalDespiking,
    bool EnableDynamicKalman,
    string DynamicKalmanMode,
    string BackendProfile,
    Guid ImagingRunId,
    Hdf5ExcitationMetadata ExcitationMetadata,
    IReadOnlyList<double> InterferenceFrequencyHz,
    bool UseFrequencyDivisionLockIn,
    string ContactSubjectProfile,
    string ContactFirmwareBuildId,
    bool ContactHealthyCalibrationAuthorized,
    string PairingMapSummary,
    string ReferenceScalePolicy)
{
    internal bool PersistRawAcquisitionHdf5 => StoragePolicy.PersistContinuousRaw;

    internal bool PersistImagingFrames => StoragePolicy.PersistImagingFrames;

    internal bool PersistAllDemodulatedBlocks => StoragePolicy.PersistAllDemodulatedBlocks;
}
