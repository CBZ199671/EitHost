using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.Baseline;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Reconstruction;

namespace EitHost.Core.Application.Realtime;

public sealed record ReferenceReconstructionSnapshot(
    string SetLabel,
    int ReferenceEpoch,
    int ReferenceBlockNumber,
    long ReferenceStartSampleIndex,
    DateTimeOffset? ReferenceLockedAt,
    string ReferenceLockKind,
    bool ReferenceIsProvisional,
    bool ReferenceInvalidated,
    bool ReconstructionSuspended,
    bool ReconstructionActive,
    int ReconstructionFrames,
    int DegradedReconstructionFrames,
    int ConsecutiveReconstructionFailures,
    string Reason,
    long Revision,
    bool ReplacementCollecting = false,
    bool ReplacementPrepared = false,
    bool ReplacementSwitchRequested = false,
    string? ReplacementActionGroupId = null);

public class ReferenceReconstructionCoordinator
{
    private static long nextReconstructionCoordinatorId;
    private readonly object reconstructionGate = new();
    private readonly long reconstructionCoordinatorId = Interlocked.Increment(ref nextReconstructionCoordinatorId);
    private long snapshotRevision;
    private string lastSnapshotReason = "created";

    public ReferenceReconstructionCoordinator(string setLabel, int maximumReferenceCandidateFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReferenceCandidateFrames);
        SetLabel = setLabel.Trim();
        ReferenceCandidateHistory = new EcdCwrReferenceCandidateHistory(maximumReferenceCandidateFrames);
    }

    public event Action<ReferenceReconstructionSnapshot>? ReferenceSnapshotChanged;

    public string SetLabel { get; }

    public Task? ReconstructionTask { get; private set; }

    public int ReconstructionPersistedBlocks;

    public int ReconstructionPersistenceFailures;

    public int DerivedMeshPersisted;

    public RealtimeAdaptiveCadence ReconstructionCadence { get; } = new();

    public double[]? ReferenceVoltage208 { get; set; }

    public double[]? ReferenceReal208 { get; set; }

    public double[]? ReferenceImaginary208 { get; set; }

    public EitDemodulationFingerprint? ReferenceDemodulation { get; set; }

    public DdsExecutionReceipt? ExecutionReceipt { get; set; }

    public DdsTimingValidationResult? LatestTimingValidation { get; set; }

    public DdsTimingConsistencyMonitor TimingConsistency { get; } = new();

    public EcdCwrBoundaryNoiseModel? BaselineIntegrityNoiseModel { get; set; }

    public int ReferenceEpoch { get; set; }

    public DateTimeOffset ReferenceLockedAt { get; set; }

    public string ActiveReferenceLockKind { get; set; } = "initial";

    public string? ActiveReferenceActionGroupId { get; set; }

    public DateTimeOffset? ActiveReferenceCommonActionAt { get; set; }

    public double? ActiveReferenceWindowSkewMilliseconds { get; set; }

    public double? ActiveReferenceSwitchSkewMilliseconds { get; set; }

    public int ActiveReferenceSynchronizedSetCount { get; set; } = 1;

    public string PendingReferenceLockKind { get; set; } = "initial";

    public string? LastBaselineClassification { get; set; }

    public ElectrodeContactMonitor? ContactMonitor { get; set; }

    public EcdCwrPreReferenceContactMonitor PreReferenceContactMonitor { get; } = new();

    public EcdCwrOperatingFingerprint? ContactOperatingFingerprint { get; set; }

    public EcdCwrAdaptiveContactProfileMatch? AdaptiveContactProfileMatch { get; set; }

    public EcdCwrPreReferenceContactMonitor? AdaptiveShadowContactMonitor { get; set; }

    public ElectrodeContactDiagnosticResult? LatestAdaptiveShadowContactResult { get; set; }

    public EcdCwrAdaptiveContactProfile? GeneratedAdaptiveContactProfile { get; set; }

    public EcdCwrStartupDegradedReferenceAccumulator StartupDegradedReferenceAccumulator { get; } = new();

    public EcdCwrStartupDegradedReference? StartupDegradedReference { get; set; }

    public int StartupDegradedReferenceWarmupCount { get; set; }

    public int StartupDegradedReferenceAggregateCount { get; set; }

    public int[] StartupDegradedReferenceFaultElectrodes { get; set; } = [];

    public EcdCwrHealthCalibration? ContactCalibration { get; set; }

    public EcdCwrHealthCalibration? ExportableContactCalibration { get; set; }

    public EcdCwrDeviceCalibration? ExportableDeviceCalibration { get; set; }

    public EcdCwrSessionCalibration? ExportableSessionCalibration { get; set; }

    public List<DemodulatedFrame> ContactCalibrationFrames { get; } = [];

    public List<DemodulatedFrame> ReferenceCandidateFrames { get; } = [];

    public object ReferenceCandidateGate { get; } = new();

    public EcdCwrReferenceCandidateHistory ReferenceCandidateHistory { get; }

    public Dictionary<string, DemodulatedFrame> ReferenceCandidateFrameBySourceId { get; } =
        new(StringComparer.Ordinal);

    public long ReferenceCandidateNextSequence { get; set; }

    public bool ReferenceCandidateContinuityBreakPending { get; set; }

    public EcdCwrReferenceWindow? SelectedReferenceWindow { get; set; }

    public EcdCwrReferenceWindow? AutomaticReferenceWindow { get; set; }

    public EcdCwrReferenceWindow? PendingSelectedReferenceWindow { get; set; }

    public EcdCwrReferenceWindow? ActiveReferenceWindow { get; set; }

    public EcdCwrRobustReferenceObservation[]? PendingSelectedReferenceObservations { get; set; }

    public DemodulatedFrame[] PendingSelectedReferenceFrames { get; set; } = [];

    public object ReplacementReferenceGate { get; } = new();

    public bool ReplacementReferenceCollecting { get; set; }

    public DateTimeOffset? ReplacementReferenceRequestedAt { get; set; }

    public long? ReplacementReferenceStartSequence { get; set; }

    public EcdCwrRobustReference? ReplacementPreparedReference { get; set; }

    public EcdCwrReferenceWindow? ReplacementPreparedWindow { get; set; }

    public DemodulatedFrame[] ReplacementPreparedFrames { get; set; } = [];

    public string ReplacementPreparedLockKind { get; set; } = "manual_relock";

    public string? ReplacementReferenceActionGroupId { get; set; }

    public DateTimeOffset? ReplacementReferenceCommonActionAt { get; set; }

    public double? ReplacementReferenceWindowSkewMilliseconds { get; set; }

    public int ReplacementReferenceSynchronizedSetCount { get; set; } = 1;

    public int ReplacementSwitchRequested;

    public int ReplacementReferenceCandidateCount;

    public int ReferenceCandidateStrictGreenCount;

    public int ReferenceCandidateContinuousCount;

    public int ManualReferenceLockRequested;

    public EcdCwrReferenceStationarityMonitor ReferenceStationarity { get; } = new(
        RealtimeReferenceTolerancePolicy.CreateStationarityOptions());

    public EcdCwrReferenceStationarityResult? LatestReferenceStationarity { get; set; }

    public EcdCwrRobustReference? RobustReference { get; set; }

    public bool ReferenceIsProvisional { get; set; }

    public bool ReferenceUsesCommonScaleNormalization { get; set; }

    public double? LatestCommonScaleNormalizationFactor { get; set; }

    public EcdCwrBoundaryNoiseModel? BoundaryNoiseModel { get; set; }

    public EcdCwrBoundaryChangeGate? BoundaryChangeGate { get; set; }

    public bool BoundaryNoChangeActive { get; set; }

    public int ReferenceBlockNumber { get; set; }

    public long ReferenceStartSampleIndex { get; set; } = -1;

    public ElectrodeContactDiagnosticResult? LatestContactResult { get; set; }

    public EcdCwrContactSubspaceEvidenceInput ContactSubspaceEvidence =
        EcdCwrContactSubspaceEvidenceInput.Unavailable(
            "unavailable: waiting for first successful backend reconstruction");

    public int DynamicKalmanGeneration { get; set; }

    public bool DynamicKalmanResetPending { get; set; } = true;

    public bool DynamicKalmanForceSafeImage { get; set; }

    public bool ReferenceResetRequested { get; set; }

    public bool ReferenceInvalidated { get; set; }

    public int SkippedReconstructionBlocks;

    public int ReconstructionFrames;

    public int DegradedReconstructionFrames;

    public int ConsecutiveReconstructionFailures;

    public bool ReconstructionSuspended;

    public long LastReconstructionScheduleTicks;

    public ReferenceReconstructionSnapshot Snapshot => CreateSnapshot(
        Volatile.Read(ref lastSnapshotReason),
        Volatile.Read(ref snapshotRevision));

    public bool TryScheduleReconstruction(Func<Task> taskFactory, out Task? scheduledTask)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        lock (reconstructionGate)
        {
            if (ReferenceInvalidated ||
                ReconstructionSuspended ||
                ReconstructionTask is { IsCompleted: false } ||
                !ReconstructionCadence.TrySchedule())
            {
                scheduledTask = null;
                return false;
            }

            ReconstructionTask = taskFactory();
            scheduledTask = ReconstructionTask;
        }

        PublishSnapshot("reconstruction_scheduled");
        return true;
    }

    public int RecordReconstructionSuccess(TimeSpan backendElapsed, bool degraded)
    {
        if (ReferenceInvalidated)
        {
            return Volatile.Read(ref ReconstructionFrames);
        }

        var completedFrames = Interlocked.Increment(ref ReconstructionFrames);
        if (completedFrames > 1)
        {
            ReconstructionCadence.ObserveWarmBackend(backendElapsed);
        }

        Interlocked.Exchange(ref ConsecutiveReconstructionFailures, 0);
        ReconstructionSuspended = false;
        if (degraded)
        {
            Interlocked.Increment(ref DegradedReconstructionFrames);
        }

        PublishSnapshot(degraded ? "reconstruction_succeeded_degraded" : "reconstruction_succeeded");
        return completedFrames;
    }

    public bool TryRecordReconstructionSuccess(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch,
        TimeSpan backendElapsed,
        bool degraded,
        out int completedFrames)
    {
        lock (reconstructionGate)
        {
            if (!IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch))
            {
                completedFrames = Volatile.Read(ref ReconstructionFrames);
                return false;
            }

            completedFrames = RecordReconstructionSuccess(backendElapsed, degraded);
            return true;
        }
    }

    public int RecordReconstructionFailure(string message, int suspensionThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(suspensionThreshold);
        var failures = Interlocked.Increment(ref ConsecutiveReconstructionFailures);
        if (failures >= suspensionThreshold)
        {
            ReconstructionSuspended = true;
        }

        PublishSnapshot("reconstruction_failed");
        return failures;
    }

    public bool TryRecordReconstructionFailure(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch,
        string message,
        int suspensionThreshold,
        out int failures)
    {
        lock (reconstructionGate)
        {
            if (!IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch))
            {
                failures = Volatile.Read(ref ConsecutiveReconstructionFailures);
                return false;
            }

            failures = RecordReconstructionFailure(message, suspensionThreshold);
            return true;
        }
    }

    public bool IsReconstructionContextCurrent(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch)
    {
        lock (reconstructionGate)
        {
            return IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch);
        }
    }

    public bool TryAdvanceDynamicKalmanGeneration(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch,
        bool forceSafeImage,
        out int resultingGeneration)
    {
        lock (reconstructionGate)
        {
            if (!IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch))
            {
                resultingGeneration = DynamicKalmanGeneration;
                return false;
            }

            if (forceSafeImage)
            {
                DynamicKalmanForceSafeImage = true;
            }

            resultingGeneration = ++DynamicKalmanGeneration;
            DynamicKalmanResetPending = true;
            return true;
        }
    }

    public int AdvanceDynamicKalmanGeneration(bool forceSafeImage = false)
    {
        lock (reconstructionGate)
        {
            if (forceSafeImage)
            {
                DynamicKalmanForceSafeImage = true;
            }

            var resultingGeneration = ++DynamicKalmanGeneration;
            DynamicKalmanResetPending = true;
            return resultingGeneration;
        }
    }

    public int BeginReferenceTransition()
    {
        lock (reconstructionGate)
        {
            var resultingGeneration = ++DynamicKalmanGeneration;
            DynamicKalmanResetPending = true;
            return resultingGeneration;
        }
    }

    public bool TryClearDynamicKalmanResetPending(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch)
    {
        lock (reconstructionGate)
        {
            if (!IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch))
            {
                return false;
            }

            DynamicKalmanResetPending = false;
            return true;
        }
    }

    public bool TryCommitReconstructionState(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (reconstructionGate)
        {
            if (!IsReconstructionContextCurrentCore(expectedDynamicGeneration, expectedReferenceEpoch))
            {
                return false;
            }

            commit();
            return true;
        }
    }

    public static bool TryCommitReconstructionPair(
        ReferenceReconstructionCoordinator left,
        int leftDynamicGeneration,
        int leftReferenceEpoch,
        ReferenceReconstructionCoordinator right,
        int rightDynamicGeneration,
        int rightReferenceEpoch,
        Action commit)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(commit);
        if (ReferenceEquals(left, right))
        {
            lock (left.reconstructionGate)
            {
                if (!left.IsReconstructionContextCurrentCore(leftDynamicGeneration, leftReferenceEpoch) ||
                    !left.IsReconstructionContextCurrentCore(rightDynamicGeneration, rightReferenceEpoch))
                {
                    return false;
                }

                commit();
                return true;
            }
        }

        var first = left.reconstructionCoordinatorId < right.reconstructionCoordinatorId ? left : right;
        var second = ReferenceEquals(first, left) ? right : left;
        lock (first.reconstructionGate)
        {
            lock (second.reconstructionGate)
            {
                if (!left.IsReconstructionContextCurrentCore(leftDynamicGeneration, leftReferenceEpoch) ||
                    !right.IsReconstructionContextCurrentCore(rightDynamicGeneration, rightReferenceEpoch))
                {
                    return false;
                }

                commit();
                return true;
            }
        }
    }

    private bool IsReconstructionContextCurrentCore(
        int expectedDynamicGeneration,
        int expectedReferenceEpoch) =>
        !ReferenceInvalidated &&
        DynamicKalmanGeneration == expectedDynamicGeneration &&
        ReferenceEpoch == expectedReferenceEpoch;

    public void ResetReconstructionCircuitBreaker(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (reconstructionGate)
        {
            Interlocked.Exchange(ref ConsecutiveReconstructionFailures, 0);
            ReconstructionSuspended = ReferenceInvalidated;
        }

        PublishSnapshot(reason.Trim());
    }

    public void ActivateReferenceEpoch(
        int blockNumber,
        long startSampleIndex,
        DateTimeOffset lockedAt,
        string lockKind)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKind);
        lock (reconstructionGate)
        {
            ReferenceBlockNumber = blockNumber;
            ReferenceStartSampleIndex = startSampleIndex;
            ReferenceEpoch++;
            ReferenceLockedAt = lockedAt;
            ActiveReferenceLockKind = lockKind.Trim();
            ActiveReferenceActionGroupId = null;
            ActiveReferenceCommonActionAt = null;
            ActiveReferenceWindowSkewMilliseconds = null;
            ActiveReferenceSwitchSkewMilliseconds = null;
            ActiveReferenceSynchronizedSetCount = 1;
            ReferenceInvalidated = false;
        }

        PublishSnapshot("reference_epoch_activated");
    }

    public void InvalidateReference(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (reconstructionGate)
        {
            ReferenceInvalidated = true;
            ReconstructionSuspended = true;
            DynamicKalmanGeneration++;
            DynamicKalmanResetPending = true;
        }

        PublishSnapshot(reason.Trim());
    }

    public bool MarkReferenceCandidateContinuityBreak()
    {
        lock (ReferenceCandidateGate)
        {
            var changed = !ReferenceCandidateContinuityBreakPending ||
                Volatile.Read(ref ReferenceCandidateContinuousCount) != 0 ||
                Volatile.Read(ref ReplacementReferenceCandidateCount) != 0 ||
                AutomaticReferenceWindow is not null ||
                SelectedReferenceWindow is not null;
            ReferenceCandidateContinuityBreakPending = true;
            Volatile.Write(ref ReferenceCandidateContinuousCount, 0);
            Volatile.Write(ref ReplacementReferenceCandidateCount, 0);
            AutomaticReferenceWindow = null;
            SelectedReferenceWindow = null;
            return changed;
        }
    }

    public void BeginReplacementPreparation(DateTimeOffset requestedAt) =>
        BeginReplacementPreparation(requestedAt, ReferenceCandidateNextSequence);

    public void BeginReplacementPreparation(DateTimeOffset requestedAt, long startSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSequence);
        lock (ReplacementReferenceGate)
        {
            ReplacementReferenceCollecting = true;
            ReplacementReferenceRequestedAt = requestedAt;
            ReplacementReferenceStartSequence = startSequence;
            ReplacementPreparedReference = null;
            ReplacementPreparedWindow = null;
            ReplacementPreparedFrames = [];
            ReplacementPreparedLockKind = "manual_relock";
            ReplacementReferenceActionGroupId = null;
            ReplacementReferenceCommonActionAt = null;
            ReplacementReferenceWindowSkewMilliseconds = null;
            ReplacementReferenceSynchronizedSetCount = 1;
            Interlocked.Exchange(ref ReplacementSwitchRequested, 0);
            Volatile.Write(ref ReplacementReferenceCandidateCount, 0);
        }

        PublishSnapshot("replacement_preparation_started");
    }

    public bool SetPreparedReplacement(
        EcdCwrRobustReference reference,
        EcdCwrReferenceWindow window,
        IReadOnlyList<DemodulatedFrame> frames,
        string lockKind,
        string actionGroupId,
        DateTimeOffset commonActionAt,
        double windowSkewMilliseconds,
        int synchronizedSetCount)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionGroupId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(synchronizedSetCount);
        lock (ReplacementReferenceGate)
        {
            if (!ReplacementReferenceCollecting)
            {
                return false;
            }

            ReplacementReferenceCollecting = true;
            ReplacementReferenceRequestedAt ??= commonActionAt;
            ReplacementPreparedReference = reference;
            ReplacementPreparedWindow = window;
            ReplacementPreparedFrames = frames.ToArray();
            ReplacementPreparedLockKind = lockKind.Trim();
            ReplacementReferenceActionGroupId = actionGroupId.Trim();
            ReplacementReferenceCommonActionAt = commonActionAt;
            ReplacementReferenceWindowSkewMilliseconds = windowSkewMilliseconds;
            ReplacementReferenceSynchronizedSetCount = synchronizedSetCount;
            Interlocked.Exchange(ref ReplacementSwitchRequested, 0);
        }

        PublishSnapshot("replacement_prepared");
        return true;
    }

    public bool RequestReplacementSwitch()
    {
        lock (ReplacementReferenceGate)
        {
            if (!ReplacementReferenceCollecting || ReplacementPreparedReference is null)
            {
                return false;
            }

            Interlocked.Exchange(ref ReplacementSwitchRequested, 1);
        }

        PublishSnapshot("replacement_switch_requested");
        return true;
    }

    public void ClearReplacementPreparation()
    {
        lock (ReplacementReferenceGate)
        {
            ReplacementReferenceCollecting = false;
            ReplacementReferenceRequestedAt = null;
            ReplacementReferenceStartSequence = null;
            ReplacementPreparedReference = null;
            ReplacementPreparedWindow = null;
            ReplacementPreparedFrames = [];
            ReplacementReferenceActionGroupId = null;
            ReplacementReferenceCommonActionAt = null;
            ReplacementReferenceWindowSkewMilliseconds = null;
            ReplacementReferenceSynchronizedSetCount = 1;
            PendingSelectedReferenceWindow = null;
            PendingSelectedReferenceObservations = null;
            PendingSelectedReferenceFrames = [];
            Interlocked.Exchange(ref ReplacementSwitchRequested, 0);
            Volatile.Write(ref ReplacementReferenceCandidateCount, 0);
        }

        PublishSnapshot("replacement_cleared");
    }

    public void PublishSnapshot(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalized = reason.Trim();
        Volatile.Write(ref lastSnapshotReason, normalized);
        var revision = Interlocked.Increment(ref snapshotRevision);
        ReferenceSnapshotChanged?.Invoke(CreateSnapshot(normalized, revision));
    }

    public static T? SelectCatchUpEpoch<T>(
        IEnumerable<T> epochs,
        long sourceStartSampleIndex,
        Func<T, long> lockedStartSampleIndex,
        Func<T, int> epochNumber)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(epochs);
        ArgumentNullException.ThrowIfNull(lockedStartSampleIndex);
        ArgumentNullException.ThrowIfNull(epochNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceStartSampleIndex);
        var candidates = epochs
            .Select(epoch => new
            {
                Epoch = epoch,
                LockedStartSampleIndex = lockedStartSampleIndex(epoch),
                EpochNumber = epochNumber(epoch)
            })
            .ToArray();
        if (candidates.Any(candidate => candidate.LockedStartSampleIndex < 0))
        {
            throw new InvalidOperationException(
                "Reference catch-up provenance requires a non-negative locked sample anchor for every epoch.");
        }

        return candidates
            .Where(candidate => candidate.LockedStartSampleIndex < sourceStartSampleIndex)
            .OrderBy(candidate => candidate.LockedStartSampleIndex)
            .ThenBy(candidate => candidate.EpochNumber)
            .Select(candidate => candidate.Epoch)
            .LastOrDefault();
    }

    private ReferenceReconstructionSnapshot CreateSnapshot(string reason, long revision)
    {
        return new ReferenceReconstructionSnapshot(
            SetLabel,
            ReferenceEpoch,
            ReferenceBlockNumber,
            ReferenceStartSampleIndex,
            ReferenceEpoch > 0 ? ReferenceLockedAt : null,
            ActiveReferenceLockKind,
            ReferenceIsProvisional,
            ReferenceInvalidated,
            ReconstructionSuspended,
            ReconstructionTask is { IsCompleted: false },
            Volatile.Read(ref ReconstructionFrames),
            Volatile.Read(ref DegradedReconstructionFrames),
            Volatile.Read(ref ConsecutiveReconstructionFailures),
            reason,
            revision,
            ReplacementReferenceCollecting,
            ReplacementPreparedReference is not null,
            Volatile.Read(ref ReplacementSwitchRequested) != 0,
            ReplacementReferenceActionGroupId);
    }
}
