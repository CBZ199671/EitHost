using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrPreReferenceContactOptions(
    double Relative48CandidateThreshold = 4.0,
    double ConfirmedRelative48Threshold = 7.0,
    double ConfirmedRelative48ReleaseThreshold = 6.0,
    int MinimumTopologyObservations = 2,
    double MinimumTopologyViolationFraction = 2.0 / 3.0,
    double MinimumConfirmedSetTopologyFraction = 0.5,
    int TopologySupportHoldUpdates = 6,
    int ConfirmationUpdates = 3,
    int DirectAOnlyConfirmationUpdates = 12,
    double DirectAOnlyMinimumScore = 7.5,
    double DirectAOnlyMinimumBackgroundGap = 2.0,
    double DirectAHalfSplitMinimumRatio = 1.5,
    int DrivePairConsensusWindowUpdates = 24,
    double DrivePairConsensusMinimumSupportFraction = 0.5,
    double DrivePairConsensusMinimumActiveMedianScore = 4.5,
    double DrivePairConsensusMaximumCompetingSupportFraction = 0.25,
    int DrivePairConsensusReleaseClearUpdates = 24,
    double DrivePairEndpointConfirmationMinimumSupportFraction = 0.25,
    double SevereUnilateralConfirmationMinimumScore = double.PositiveInfinity,
    int SevereUnilateralConfirmationWindowUpdates = 24,
    double SevereUnilateralConfirmationMinimumSupportFraction = 0.75,
    int MaximumConfirmedCandidateCount = 8,
    int SystemLevelSaturationElectrodeCount = 8,
    double HardFaultScore = 15.0);

public sealed record EcdCwrPreReferenceConsensusResult(
    bool[] Candidates,
    bool[] Confirmed,
    int StableUpdateCount,
    int TopologySupportedCandidateCount,
    double TopologySupportFraction,
    int StrictAcceptedFrameCount,
    bool SystemLevelTriggered,
    string Status,
    bool[]? SafetyMask = null);

public sealed class EcdCwrPreReferenceContactMonitor
{
    private const int ElectrodeCount = ElectrodeContactBaseline.ElectrodeCount;
    private const string DiagnosticOnlyWeightPolicyVersion = "pre-reference-diagnostic-no-reconstruction-v1";
    private const string ConfirmedMaskWeightPolicyVersion = "ecd-cwr-pre-reference-safety-mask-v2:critical=0.02";
    private const double ConfirmedCriticalWeight = 0.02;
    private const double DegradedImageQualityCap = 0.55;

    private readonly EcdCwrPreReferenceContactOptions options;
    private readonly bool[] trackedCandidates = new bool[ElectrodeCount];
    private readonly bool[] confirmedCandidates = new bool[ElectrodeCount];
    private readonly int[] confirmedCandidateClearUpdates = new int[ElectrodeCount];
    private readonly int[] topologySupportHold = new int[ElectrodeCount];
    private readonly Queue<double[]> drivePairScoreHistory = new();
    private readonly Queue<double[]> endpointSpecificScoreHistory = new();
    private readonly Queue<bool[]> severeUnilateralHistory = new();
    private readonly bool[] severeUnilateralConfirmed = new bool[ElectrodeCount];
    private readonly int[] severeUnilateralClearUpdates = new int[ElectrodeCount];
    private int stableCandidateUpdates;
    private int halfSplitStableUpdates;
    private int? confirmedDrivePairStimulation;
    private int confirmedDrivePairClearUpdates;

    public EcdCwrPreReferenceContactMonitor(EcdCwrPreReferenceContactOptions? options = null)
    {
        this.options = options ?? new EcdCwrPreReferenceContactOptions();
    }

    public ElectrodeContactDiagnosticResult Update(
        double[,]? fullAmplitudes256,
        IReadOnlyList<DemodulatedWindowQuality> windowQualities,
        int strictAcceptedFrameCount = 0)
    {
        ArgumentNullException.ThrowIfNull(windowQualities);
        ArgumentOutOfRangeException.ThrowIfNegative(strictAcceptedFrameCount);
        if (fullAmplitudes256 is not null)
        {
            ElectrodeContactBaseline.ValidateFullMatrix(fullAmplitudes256, nameof(fullAmplitudes256));
        }

        var scores = new double[ElectrodeCount];
        var faultTypes = Enumerable.Repeat(ElectrodeFaultType.None, ElectrodeCount).ToArray();
        var evidenceKinds = new ElectrodeEvidenceKind[ElectrodeCount];
        var reasons = Enumerable.Repeat("green", ElectrodeCount).ToArray();
        var drivePairScores = new double[ElectrodeCount];
        var endpointSpecificScores = new double[ElectrodeCount];

        if (fullAmplitudes256 is not null)
        {
            ApplyRelative48Candidates(
                fullAmplitudes256,
                scores,
                faultTypes,
                evidenceKinds,
                reasons,
                drivePairScores,
                endpointSpecificScores);
        }

        var evidenceD = new EcdCwrEvidenceDAnalyzer().Analyze(
            windowQualities,
            new EcdCwrEvidenceDOptions(HardFaultScore: options.HardFaultScore));
        ApplyPersistentTopologyCandidates(evidenceD, scores, faultTypes, evidenceKinds, reasons);
        ApplySaturationFaults(evidenceD, scores, faultTypes, evidenceKinds, reasons);
        var consensus = UpdateConsensus(
            scores,
            evidenceKinds,
            strictAcceptedFrameCount,
            drivePairScores,
            endpointSpecificScores);
        ApplyConfirmedConsensus(consensus, scores, faultTypes, evidenceKinds, reasons);

        var states = new ElectrodeContactState[ElectrodeCount];
        var confidence = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            confidence[electrode] = Math.Clamp(
                scores[electrode] / Math.Max(options.HardFaultScore, double.Epsilon),
                0.0,
                1.0);
            states[electrode] = (evidenceKinds[electrode] & ElectrodeEvidenceKind.Saturation) != 0
                ? ElectrodeContactState.DarkRed
                : (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceConsensus) != 0 &&
                    consensus.Confirmed[electrode]
                    ? ElectrodeContactState.Red
                : scores[electrode] >= options.Relative48CandidateThreshold
                    ? ElectrodeContactState.Yellow
                    : ElectrodeContactState.Green;
            if (states[electrode] == ElectrodeContactState.Green)
            {
                faultTypes[electrode] = ElectrodeFaultType.None;
                reasons[electrode] = "green";
            }
        }

        if (consensus.SystemLevelTriggered && !consensus.Confirmed.Any(selected => selected))
        {
            Array.Fill(states, ElectrodeContactState.SystemLevel);
            Array.Fill(faultTypes, ElectrodeFaultType.SystemLevel);
            Array.Fill(reasons, "pre-reference persistent half-split system sentinel");
        }

        var safetyMask = consensus.SafetyMask ?? consensus.Confirmed;
        var confirmedWeightStates = safetyMask
            .Select(masked => masked ? ElectrodeContactState.Red : ElectrodeContactState.Green)
            .ToArray();
        var hasSafetyMask = safetyMask.Any(masked => masked);
        var measurementWeights = consensus.SystemLevelTriggered
            ? Enumerable.Repeat(0.0, ElectrodeContactBaseline.RetainedObservationCount).ToArray()
            : hasSafetyMask
            ? new EcdCwrBinaryWeightMapper().Map(
                confirmedWeightStates,
                new EcdCwrBinaryWeightMapperOptions(YellowWeight: 1.0, CriticalWeight: ConfirmedCriticalWeight))
            : Enumerable.Repeat(1.0, ElectrodeContactBaseline.RetainedObservationCount).ToArray();
        var effectiveMeasurementCount = measurementWeights.Count(weight => weight >= 0.05);
        var imageQualityScore = hasSafetyMask
            ? Math.Min(
                DegradedImageQualityCap,
                0.65 * effectiveMeasurementCount / ElectrodeContactBaseline.RetainedObservationCount)
            : 0.0;
        var hardFaultElectrodeCount = states.Count(state => state == ElectrodeContactState.DarkRed);
        var systemLevel = hardFaultElectrodeCount >= options.SystemLevelSaturationElectrodeCount ||
            consensus.SystemLevelTriggered;
        var saturationRatio = evidenceD.WindowScores.Count == 0
            ? 0.0
            : evidenceD.WindowScores.Count(score => score.HardFault) /
                (double)evidenceD.WindowScores.Count;
        var runtimeEvidence = new EcdCwrRuntimeEvidenceSummary(
            EvidenceDAvailable: true,
            EvidenceDSoftViolationCount: evidenceD.WindowScores.Count(score => !score.HardFault && score.Score > 0.0),
            EvidenceDHardFaultCount: evidenceD.WindowScores.Count(score => score.HardFault),
            EvidenceDMaxScore: evidenceD.MaxScore,
            RawGlobalSentinelTriggered: systemLevel,
            RawContact48MedianZ: 0.0,
            RawDriveMedianZ: 0.0,
            SaturationRatio: saturationRatio,
            SystemSentinelReason: systemLevel
                ? consensus.SystemLevelTriggered
                    ? "pre-reference confirmed-count/half-split sentinel"
                    : "pre-reference saturation sentinel"
                : "pre-reference sentinel clear",
            FaultDictionaryPolicyVersion: "not-applicable-pre-reference");

        return new ElectrodeContactDiagnosticResult(
            States: states,
            FaultTypes: faultTypes,
            Scores: scores,
            FaultConfidence: confidence,
            UpgradeGateReasons: reasons,
            MeasurementWeight208: measurementWeights,
            ImageQualityScore: imageQualityScore,
            WeightPolicyVersion: consensus.SystemLevelTriggered
                ? "pre-reference-system-sentinel-v1"
                : hasSafetyMask
                ? ConfirmedMaskWeightPolicyVersion
                : DiagnosticOnlyWeightPolicyVersion,
            Summary: CreateSummary(states, systemLevel),
            SystemLevel: systemLevel,
            ReferenceInvalidated: false,
            CandidateScores: scores.ToArray(),
            CandidateFaultTypes: faultTypes.ToArray(),
            CandidateEvidenceKinds: evidenceKinds,
            CandidateReasons: reasons.ToArray(),
            RuntimeEvidence: runtimeEvidence,
            ContactSubspaceEvidence: EcdCwrContactSubspaceEvidenceSummary.NotApplicable(
                "pre-reference startup diagnosis"),
            PreReferenceOnly: true,
            PreReferenceConsensus: consensus);
    }

    private EcdCwrPreReferenceConsensusResult UpdateConsensus(
        IReadOnlyList<double> scores,
        IReadOnlyList<ElectrodeEvidenceKind> evidenceKinds,
        int strictAcceptedFrameCount,
        IReadOnlyList<double> drivePairScores,
        IReadOnlyList<double> endpointSpecificScores)
    {
        var halfSplitDetected = evidenceKinds.Any(kind =>
            (kind & ElectrodeEvidenceKind.PreReferenceHalfSplit) != 0);
        halfSplitStableUpdates = halfSplitDetected ? halfSplitStableUpdates + 1 : 0;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var confirmedStrongRelative = confirmedCandidates[electrode] &&
                scores[electrode] >= options.ConfirmedRelative48ReleaseThreshold &&
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceRelative48) != 0;
            topologySupportHold[electrode] =
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PersistentTopology) != 0
                    ? options.TopologySupportHoldUpdates
                    : confirmedStrongRelative && topologySupportHold[electrode] > 0
                        ? options.TopologySupportHoldUpdates
                    : Math.Max(0, topologySupportHold[electrode] - 1);
        }

        var displayCandidates = Enumerable.Range(0, ElectrodeCount)
            .Select(electrode =>
            {
                var threshold = trackedCandidates[electrode] || confirmedCandidates[electrode]
                    ? options.ConfirmedRelative48ReleaseThreshold
                    : options.ConfirmedRelative48Threshold;
                return scores[electrode] >= threshold &&
                    (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceRelative48) != 0;
            })
            .ToArray();
        var candidates = Enumerable.Range(0, ElectrodeCount)
            .Select(electrode => displayCandidates[electrode] &&
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceShared48) != 0)
            .ToArray();
        var drivePairConsensus = UpdateDrivePairConsensus(drivePairScores);
        var rollingEndpointConfirmation = UpdateRollingEndpointConfirmation(
            drivePairConsensus,
            endpointSpecificScores);
        var severeUnilateralConfirmation = UpdateSevereUnilateralConfirmation(
            evidenceKinds,
            endpointSpecificScores,
            drivePairScores);
        SuppressUncorroboratedUnilateralCandidates(
            candidates,
            evidenceKinds,
            rollingEndpointConfirmation.ConfirmedElectrodes,
            severeUnilateralConfirmation.ConfirmedElectrodes);
        var drivePairConsensusApplies = drivePairConsensus.Confirmed;
        if (drivePairConsensusApplies)
        {
            displayCandidates[drivePairConsensus.Stimulation] = true;
            displayCandidates[Mod(drivePairConsensus.Stimulation + 1)] = true;
        }

        var candidateCount = candidates.Count(selected => selected);
        var topologySupportedCandidateCount = Enumerable.Range(0, ElectrodeCount)
            .Count(electrode => candidates[electrode] && topologySupportHold[electrode] > 0);
        var topologySupportFraction = candidateCount == 0
            ? 0.0
            : topologySupportedCandidateCount / (double)candidateCount;

        string status;
        if (candidateCount == 0)
        {
            Array.Clear(trackedCandidates);
            stableCandidateUpdates = 0;
            UpdateConfirmedCandidateClearState(drivePairScores);
            var heldConfirmedCount = confirmedCandidates.Count(selected => selected);
            var maximumClearUpdates = confirmedCandidateClearUpdates.DefaultIfEmpty(0).Max();
            status = heldConfirmedCount > 0
                ? $"held-confirmed-floating-mode physical-confirmed={heldConfirmedCount} clear={maximumClearUpdates}/{options.DrivePairConsensusReleaseClearUpdates} accepted={strictAcceptedFrameCount}"
                : halfSplitDetected
                    ? $"tracking-half-split stable={halfSplitStableUpdates}/{options.DirectAOnlyConfirmationUpdates} accepted={strictAcceptedFrameCount}"
                    : drivePairConsensus.HasCandidate
                        ? $"tracking-drive-pair-ambiguous-one-or-both pair={drivePairConsensus.Stimulation + 1}-{Mod(drivePairConsensus.Stimulation + 1) + 1} support={drivePairConsensus.SupportCount}/{drivePairConsensus.ObservationCount} median={drivePairConsensus.ActiveMedianScore:F2} accepted={strictAcceptedFrameCount}"
                        : $"yellow-only no-electrode-specific-relative48-set accepted={strictAcceptedFrameCount}";
        }
        else
        {
            var previousConfirmed = confirmedCandidates.ToArray();
            var previousConfirmedCount = previousConfirmed.Count(selected => selected);
            var sameConfirmedSet = previousConfirmedCount > 0 &&
                previousConfirmed.SequenceEqual(candidates);
            var confirmedSubsetRecovery = candidateCount > 0 &&
                candidateCount < previousConfirmedCount &&
                Enumerable.Range(0, ElectrodeCount)
                    .All(electrode => !candidates[electrode] || previousConfirmed[electrode]);
            if (trackedCandidates.SequenceEqual(candidates))
            {
                stableCandidateUpdates++;
            }
            else
            {
                Array.Copy(candidates, trackedCandidates, ElectrodeCount);
                Array.Clear(confirmedCandidates);
                Array.Clear(confirmedCandidateClearUpdates);
                stableCandidateUpdates = 1;
            }

            var topologyGate = topologySupportFraction >=
                options.MinimumConfirmedSetTopologyFraction;
            var weakestCandidateScore = Enumerable.Range(0, ElectrodeCount)
                .Where(electrode => candidates[electrode])
                .Select(electrode => scores[electrode])
                .DefaultIfEmpty(0.0)
                .Min();
            var backgroundMaximum = Enumerable.Range(0, ElectrodeCount)
                .Where(electrode => !candidates[electrode])
                .Select(electrode => scores[electrode])
                .DefaultIfEmpty(0.0)
                .Max();
            var directABackgroundGap = Math.Max(0.0, weakestCandidateScore - backgroundMaximum);
            var directAOnlyGate = Enumerable.Range(0, ElectrodeCount)
                .Where(electrode => candidates[electrode])
                .All(electrode =>
                    scores[electrode] >= options.DirectAOnlyMinimumScore &&
                    (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceBilateralShared48) != 0) &&
                directABackgroundGap >= options.DirectAOnlyMinimumBackgroundGap;
            var confirmedByTopology = topologyGate &&
                stableCandidateUpdates >= options.ConfirmationUpdates;
            var confirmedByDirectA = directAOnlyGate &&
                stableCandidateUpdates >= options.DirectAOnlyConfirmationUpdates;
            if (sameConfirmedSet || confirmedSubsetRecovery || confirmedByTopology || confirmedByDirectA)
            {
                Array.Copy(candidates, confirmedCandidates, ElectrodeCount);
                Array.Clear(confirmedCandidateClearUpdates);
            }

            status = confirmedSubsetRecovery
                ? $"confirmed-subset-recovery parent={previousConfirmedCount} current={candidateCount} topology={topologySupportedCandidateCount}/{candidateCount} accepted={strictAcceptedFrameCount}"
                : confirmedByDirectA
                    ? $"confirmed-directA-stable stable={stableCandidateUpdates} gap={directABackgroundGap:F2} topology={topologySupportedCandidateCount}/{candidateCount} accepted={strictAcceptedFrameCount}"
                : confirmedCandidates.Any(selected => selected)
                    ? $"confirmed stable={stableCandidateUpdates} topology={topologySupportedCandidateCount}/{candidateCount} accepted={strictAcceptedFrameCount}"
                : topologyGate
                    ? $"tracking stable={stableCandidateUpdates}/{options.ConfirmationUpdates} topology={topologySupportedCandidateCount}/{candidateCount} accepted={strictAcceptedFrameCount}"
                    : directAOnlyGate
                        ? $"tracking-directA stable={stableCandidateUpdates}/{options.DirectAOnlyConfirmationUpdates} gap={directABackgroundGap:F2} topology={topologySupportedCandidateCount}/{candidateCount} accepted={strictAcceptedFrameCount}"
                        : $"yellow-only topology={topologySupportedCandidateCount}/{candidateCount} gap={directABackgroundGap:F2} accepted={strictAcceptedFrameCount}";
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if ((!rollingEndpointConfirmation.ConfirmedElectrodes[electrode] &&
                    !severeUnilateralConfirmation.ConfirmedElectrodes[electrode]) ||
                confirmedCandidates[electrode])
            {
                continue;
            }

            confirmedCandidates[electrode] = true;
            confirmedCandidateClearUpdates[electrode] = 0;
        }

        var persistentHalfSplit = halfSplitStableUpdates >= options.DirectAOnlyConfirmationUpdates;
        if (persistentHalfSplit && !confirmedCandidates.Any(selected => selected))
        {
            status = $"system-level-directA-half-split stable={halfSplitStableUpdates} accepted={strictAcceptedFrameCount}";
        }

        var systemLevel = persistentHalfSplit;
        var safetyMask = confirmedCandidates.ToArray();
        if (drivePairConsensusApplies)
        {
            safetyMask[drivePairConsensus.Stimulation] = true;
            safetyMask[Mod(drivePairConsensus.Stimulation + 1)] = true;
            status = rollingEndpointConfirmation.ConfirmedElectrodes.Any(selected => selected)
                ? $"confirmed-rolling-endpoint pair={drivePairConsensus.Stimulation + 1}-{Mod(drivePairConsensus.Stimulation + 1) + 1} endpoint-support={rollingEndpointConfirmation.LeftSupportCount}/{rollingEndpointConfirmation.RightSupportCount}/{rollingEndpointConfirmation.ObservationCount} physical-confirmed={confirmedCandidates.Count(selected => selected)} accepted={strictAcceptedFrameCount}"
                : $"safety-masked-drive-pair-ambiguous-one-or-both pair={drivePairConsensus.Stimulation + 1}-{Mod(drivePairConsensus.Stimulation + 1) + 1} support={drivePairConsensus.SupportCount}/{drivePairConsensus.ObservationCount} median={drivePairConsensus.ActiveMedianScore:F2} clear={drivePairConsensus.ClearUpdates}/{options.DrivePairConsensusReleaseClearUpdates} physical-confirmed={confirmedCandidates.Count(selected => selected)} accepted={strictAcceptedFrameCount}";
        }

        if (severeUnilateralConfirmation.ConfirmedElectrodes.Any(selected => selected))
        {
            var confirmed = Enumerable.Range(0, ElectrodeCount)
                .Where(electrode => severeUnilateralConfirmation.ConfirmedElectrodes[electrode])
                .Select(electrode => $"{electrode + 1}:{severeUnilateralConfirmation.SupportCounts[electrode]}");
            status = $"confirmed-severe-unilateral endpoints=[{string.Join(',', confirmed)}]/{severeUnilateralConfirmation.ObservationCount} threshold={options.SevereUnilateralConfirmationMinimumScore:F2} physical-confirmed={confirmedCandidates.Count(selected => selected)} accepted={strictAcceptedFrameCount}";
        }

        var reportedCandidates = displayCandidates
            .Select((selected, electrode) => selected || candidates[electrode] || safetyMask[electrode])
            .ToArray();
        return new EcdCwrPreReferenceConsensusResult(
            Candidates: reportedCandidates,
            Confirmed: confirmedCandidates.ToArray(),
            StableUpdateCount: Math.Max(
                Math.Max(stableCandidateUpdates, halfSplitStableUpdates),
                drivePairConsensus.SupportCount),
            TopologySupportedCandidateCount: topologySupportedCandidateCount,
            TopologySupportFraction: topologySupportFraction,
            StrictAcceptedFrameCount: strictAcceptedFrameCount,
            SystemLevelTriggered: systemLevel,
            Status: status,
            SafetyMask: safetyMask);
    }

    private void SuppressUncorroboratedUnilateralCandidates(
        bool[] candidates,
        IReadOnlyList<ElectrodeEvidenceKind> evidenceKinds,
        IReadOnlyList<bool> rollingEndpointConfirmed,
        IReadOnlyList<bool> severeUnilateralEndpointConfirmed)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!candidates[electrode] ||
                confirmedCandidates[electrode] ||
                rollingEndpointConfirmed[electrode] ||
                severeUnilateralEndpointConfirmed[electrode])
            {
                continue;
            }

            var hasBilateralEvidence =
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceBilateralShared48) != 0;
            if (!hasBilateralEvidence)
            {
                candidates[electrode] = false;
            }
        }
    }

    private SevereUnilateralConfirmationState UpdateSevereUnilateralConfirmation(
        IReadOnlyList<ElectrodeEvidenceKind> evidenceKinds,
        IReadOnlyList<double> endpointSpecificScores,
        IReadOnlyList<double> drivePairScores)
    {
        var current = Enumerable.Range(0, ElectrodeCount)
            .Select(electrode =>
                endpointSpecificScores[electrode] >= options.SevereUnilateralConfirmationMinimumScore &&
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceShared48) != 0 &&
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceBilateralShared48) == 0)
            .ToArray();
        severeUnilateralHistory.Enqueue(current);
        while (severeUnilateralHistory.Count > options.SevereUnilateralConfirmationWindowUpdates)
        {
            severeUnilateralHistory.Dequeue();
        }

        var supportCounts = Enumerable.Range(0, ElectrodeCount)
            .Select(electrode => severeUnilateralHistory.Count(observation => observation[electrode]))
            .ToArray();
        var requiredSupport = (int)Math.Ceiling(
            options.SevereUnilateralConfirmationWindowUpdates *
            options.SevereUnilateralConfirmationMinimumSupportFraction);
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var coveredByCurrentDrivePair = Enumerable.Range(0, ElectrodeCount)
                .Any(stimulation =>
                    drivePairScores[stimulation] >= options.Relative48CandidateThreshold &&
                    (stimulation == electrode || Mod(stimulation + 1) == electrode));
            var hasCurrentEndpointEvidence =
                endpointSpecificScores[electrode] >= options.Relative48CandidateThreshold &&
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.PreReferenceShared48) != 0;
            if (severeUnilateralConfirmed[electrode])
            {
                severeUnilateralClearUpdates[electrode] = hasCurrentEndpointEvidence || coveredByCurrentDrivePair
                    ? 0
                    : severeUnilateralClearUpdates[electrode] + 1;
                if (severeUnilateralClearUpdates[electrode] >= options.DrivePairConsensusReleaseClearUpdates)
                {
                    severeUnilateralConfirmed[electrode] = false;
                    severeUnilateralClearUpdates[electrode] = 0;
                }

                continue;
            }

            if (severeUnilateralHistory.Count >= options.SevereUnilateralConfirmationWindowUpdates &&
                current[electrode] &&
                supportCounts[electrode] >= requiredSupport)
            {
                severeUnilateralConfirmed[electrode] = true;
                severeUnilateralClearUpdates[electrode] = 0;
            }
        }

        return new SevereUnilateralConfirmationState(
            severeUnilateralConfirmed.ToArray(),
            supportCounts,
            severeUnilateralHistory.Count);
    }

    private void UpdateConfirmedCandidateClearState(IReadOnlyList<double> drivePairScores)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!confirmedCandidates[electrode])
            {
                confirmedCandidateClearUpdates[electrode] = 0;
                continue;
            }

            var coveredByCurrentDrivePair = Enumerable.Range(0, ElectrodeCount)
                .Any(stimulation =>
                    drivePairScores[stimulation] >= options.Relative48CandidateThreshold &&
                    (stimulation == electrode || Mod(stimulation + 1) == electrode));
            confirmedCandidateClearUpdates[electrode] = coveredByCurrentDrivePair
                ? 0
                : confirmedCandidateClearUpdates[electrode] + 1;
            if (confirmedCandidateClearUpdates[electrode] < options.DrivePairConsensusReleaseClearUpdates)
            {
                continue;
            }

            confirmedCandidates[electrode] = false;
            confirmedCandidateClearUpdates[electrode] = 0;
        }
    }

    private void ApplyConfirmedConsensus(
        EcdCwrPreReferenceConsensusResult consensus,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        if (!consensus.Confirmed.Any(selected => selected))
        {
            ApplyAmbiguousSafetyMask(consensus, scores, faultTypes, evidenceKinds, reasons);
            return;
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!consensus.Confirmed[electrode])
            {
                continue;
            }

            AddCandidate(
                electrode,
                Math.Max(scores[electrode], options.ConfirmedRelative48Threshold),
                ElectrodeFaultType.ElectrodeContact,
                ElectrodeEvidenceKind.PreReferenceConsensus,
                "pre-reference persistent set consensus",
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!consensus.Confirmed[electrode])
            {
                continue;
            }

            foreach (var neighbor in new[] { Mod(electrode - 1), Mod(electrode + 1) })
            {
                if (consensus.Confirmed[neighbor] ||
                    (evidenceKinds[neighbor] & ElectrodeEvidenceKind.Saturation) != 0)
                {
                    continue;
                }

                AddCandidate(
                    neighbor,
                    options.Relative48CandidateThreshold,
                    ElectrodeFaultType.UncertainStructured,
                    ElectrodeEvidenceKind.PreReferenceConsensus | ElectrodeEvidenceKind.MultiFaultNeighbor,
                    "pre-reference confirmed-set ring neighbor",
                    scores,
                    faultTypes,
                    evidenceKinds,
                    reasons);
            }
        }

        ApplyAmbiguousSafetyMask(consensus, scores, faultTypes, evidenceKinds, reasons);
    }

    private void ApplyAmbiguousSafetyMask(
        EcdCwrPreReferenceConsensusResult consensus,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var safetyMask = consensus.SafetyMask ?? consensus.Confirmed;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!safetyMask[electrode] || consensus.Confirmed[electrode])
            {
                continue;
            }

            AddCandidate(
                electrode,
                options.Relative48CandidateThreshold,
                ElectrodeFaultType.DrivePairLink,
                ElectrodeEvidenceKind.PreReferenceDrivePair48,
                "pre-reference drive-pair ambiguity: either or both endpoints may be open",
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }
    }

    private void ApplyRelative48Candidates(
        double[,] amplitudes,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons,
        double[] drivePairScores,
        double[] endpointSpecificScores)
    {
        var driveScores = RobustAbsoluteScores(ReadColumn(amplitudes, 0));
        var rightValues = ReadColumn(amplitudes, 1);
        var leftValues = ReadColumn(amplitudes, ElectrodeCount - 1);
        var rightScores = RobustAbsoluteScores(rightValues);
        var leftScores = RobustAbsoluteScores(leftValues);
        var sharedScores = new double[ElectrodeCount];
        var sharedValues = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            sharedScores[electrode] = Math.Max(leftScores[electrode], rightScores[Mod(electrode - 1)]);
            endpointSpecificScores[electrode] = sharedScores[electrode];
            sharedValues[electrode] = Math.Max(
                Math.Abs(leftValues[electrode]),
                Math.Abs(rightValues[Mod(electrode - 1)]));
            if (sharedScores[electrode] >= options.Relative48CandidateThreshold)
            {
                var bilateral = leftScores[electrode] >= options.DirectAOnlyMinimumScore &&
                    rightScores[Mod(electrode - 1)] >= options.DirectAOnlyMinimumScore;
                AddCandidate(
                    electrode,
                    sharedScores[electrode],
                    ElectrodeFaultType.UncertainStructured,
                    ElectrodeEvidenceKind.PreReferenceRelative48 |
                        ElectrodeEvidenceKind.PreReferenceShared48 |
                        (bilateral ? ElectrodeEvidenceKind.PreReferenceBilateralShared48 : ElectrodeEvidenceKind.None),
                    "pre-reference relative48 shared candidate",
                    scores,
                    faultTypes,
                    evidenceKinds,
                    reasons);
            }
        }

        if (TryFindHalfSplit(sharedValues, out var splitRatio) &&
            !scores.Any(score => score >= options.ConfirmedRelative48Threshold))
        {
            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                AddCandidate(
                    electrode,
                    options.Relative48CandidateThreshold,
                    ElectrodeFaultType.UncertainStructured,
                    ElectrodeEvidenceKind.PreReferenceHalfSplit,
                    $"pre-reference relative48 ambiguous half-split ratio={splitRatio:F2}",
                    scores,
                    faultTypes,
                    evidenceKinds,
                    reasons);
            }
        }

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            if (driveScores[stimulation] < options.Relative48CandidateThreshold)
            {
                continue;
            }

            var leftElectrode = stimulation;
            var rightElectrode = Mod(stimulation + 1);
            if (sharedScores[leftElectrode] >= options.Relative48CandidateThreshold ||
                sharedScores[rightElectrode] >= options.Relative48CandidateThreshold)
            {
                continue;
            }

            drivePairScores[stimulation] = driveScores[stimulation];

            AddCandidate(
                leftElectrode,
                driveScores[stimulation],
                ElectrodeFaultType.DrivePairLink,
                ElectrodeEvidenceKind.PreReferenceRelative48 |
                    ElectrodeEvidenceKind.PreReferenceDrivePair48,
                "pre-reference relative48 drive-pair candidate",
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
            AddCandidate(
                rightElectrode,
                driveScores[stimulation],
                ElectrodeFaultType.DrivePairLink,
                ElectrodeEvidenceKind.PreReferenceRelative48 |
                    ElectrodeEvidenceKind.PreReferenceDrivePair48,
                "pre-reference relative48 drive-pair candidate",
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }
    }

    private DrivePairConsensusState UpdateDrivePairConsensus(IReadOnlyList<double> drivePairScores)
    {
        if (drivePairScores.Count != ElectrodeCount)
        {
            throw new ArgumentException($"Expected {ElectrodeCount} drive-pair scores.", nameof(drivePairScores));
        }

        drivePairScoreHistory.Enqueue(drivePairScores.ToArray());
        while (drivePairScoreHistory.Count > options.DrivePairConsensusWindowUpdates)
        {
            drivePairScoreHistory.Dequeue();
        }

        if (confirmedDrivePairStimulation is { } heldStimulation)
        {
            confirmedDrivePairClearUpdates = drivePairScores[heldStimulation] >= options.Relative48CandidateThreshold
                ? 0
                : confirmedDrivePairClearUpdates + 1;
            if (confirmedDrivePairClearUpdates >= options.DrivePairConsensusReleaseClearUpdates)
            {
                confirmedDrivePairStimulation = null;
                confirmedDrivePairClearUpdates = 0;
            }
        }

        var supportCounts = new int[ElectrodeCount];
        var activeScores = Enumerable.Range(0, ElectrodeCount)
            .Select(_ => new List<double>())
            .ToArray();
        foreach (var observation in drivePairScoreHistory)
        {
            for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
            {
                if (observation[stimulation] < options.Relative48CandidateThreshold)
                {
                    continue;
                }

                supportCounts[stimulation]++;
                activeScores[stimulation].Add(observation[stimulation]);
            }
        }

        var ordered = Enumerable.Range(0, ElectrodeCount)
            .OrderByDescending(stimulation => supportCounts[stimulation])
            .ThenBy(stimulation => stimulation)
            .ToArray();
        var bestStimulation = ordered[0];
        var bestSupport = supportCounts[bestStimulation];
        var competingSupport = supportCounts[ordered[1]];
        var activeMedian = activeScores[bestStimulation].Count == 0
            ? 0.0
            : MedianSorted(activeScores[bestStimulation].Order().ToArray());
        var observationCount = drivePairScoreHistory.Count;
        var requiredSupport = (int)Math.Ceiling(
            options.DrivePairConsensusWindowUpdates * options.DrivePairConsensusMinimumSupportFraction);
        var maximumCompetingSupport = (int)Math.Floor(
            options.DrivePairConsensusWindowUpdates * options.DrivePairConsensusMaximumCompetingSupportFraction);
        var justConfirmed = false;
        if (confirmedDrivePairStimulation is null &&
            observationCount >= options.DrivePairConsensusWindowUpdates &&
            bestSupport >= requiredSupport &&
            competingSupport <= maximumCompetingSupport &&
            activeMedian >= options.DrivePairConsensusMinimumActiveMedianScore)
        {
            confirmedDrivePairStimulation = bestStimulation;
            confirmedDrivePairClearUpdates = 0;
            justConfirmed = true;
        }

        var reportedStimulation = confirmedDrivePairStimulation ?? bestStimulation;
        var reportedSupport = supportCounts[reportedStimulation];
        var reportedMedian = activeScores[reportedStimulation].Count == 0
            ? 0.0
            : MedianSorted(activeScores[reportedStimulation].Order().ToArray());
        return new DrivePairConsensusState(
            HasCandidate: reportedSupport > 0,
            Confirmed: confirmedDrivePairStimulation is not null,
            JustConfirmed: justConfirmed,
            Stimulation: reportedStimulation,
            SupportCount: reportedSupport,
            ObservationCount: observationCount,
            ActiveMedianScore: reportedMedian,
            ClearUpdates: confirmedDrivePairClearUpdates);
    }

    private RollingEndpointConfirmationState UpdateRollingEndpointConfirmation(
        DrivePairConsensusState drivePairConsensus,
        IReadOnlyList<double> endpointSpecificScores)
    {
        if (endpointSpecificScores.Count != ElectrodeCount)
        {
            throw new ArgumentException(
                $"Expected {ElectrodeCount} endpoint-specific scores.",
                nameof(endpointSpecificScores));
        }

        endpointSpecificScoreHistory.Enqueue(endpointSpecificScores.ToArray());
        while (endpointSpecificScoreHistory.Count > options.DrivePairConsensusWindowUpdates)
        {
            endpointSpecificScoreHistory.Dequeue();
        }

        var confirmed = new bool[ElectrodeCount];
        if (!drivePairConsensus.Confirmed ||
            endpointSpecificScoreHistory.Count < options.DrivePairConsensusWindowUpdates)
        {
            return new RollingEndpointConfirmationState(
                confirmed,
                LeftSupportCount: 0,
                RightSupportCount: 0,
                ObservationCount: endpointSpecificScoreHistory.Count);
        }

        var leftElectrode = drivePairConsensus.Stimulation;
        var rightElectrode = Mod(drivePairConsensus.Stimulation + 1);
        var leftSupportCount = endpointSpecificScoreHistory.Count(observation =>
            observation[leftElectrode] >= options.Relative48CandidateThreshold);
        var rightSupportCount = endpointSpecificScoreHistory.Count(observation =>
            observation[rightElectrode] >= options.Relative48CandidateThreshold);
        var requiredSupport = (int)Math.Ceiling(
            options.DrivePairConsensusWindowUpdates *
            options.DrivePairEndpointConfirmationMinimumSupportFraction);
        confirmed[leftElectrode] = leftSupportCount >= requiredSupport;
        confirmed[rightElectrode] = rightSupportCount >= requiredSupport;
        return new RollingEndpointConfirmationState(
            confirmed,
            leftSupportCount,
            rightSupportCount,
            endpointSpecificScoreHistory.Count);
    }

    private void ApplyPersistentTopologyCandidates(
        EcdCwrEvidenceDResult evidenceD,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var rows = evidenceD.WindowScores
            .GroupBy(window => Mod(window.ExpectedReferenceChannel))
            .Select(group =>
            {
                var faults = group.Where(window => !window.HardFault && window.RejectReason is
                    DemodulatedWindowRejectReason.Top3NotContiguous or
                    DemodulatedWindowRejectReason.ExpectedReferenceNotInTop3 or
                    DemodulatedWindowRejectReason.WeakReference).ToArray();
                return new
                {
                    Stimulation = group.Key,
                    Total = group.Count(),
                    Faults = faults.Length,
                    Score = faults.Length == 0 ? 0.0 : faults.Max(window => window.Score)
                };
            })
            .Where(row => row.Total >= options.MinimumTopologyObservations &&
                row.Faults >= options.MinimumTopologyObservations &&
                row.Faults / (double)row.Total >= options.MinimumTopologyViolationFraction)
            .ToArray();

        foreach (var row in rows)
        {
            var score = Math.Max(options.Relative48CandidateThreshold, row.Score);
            var reason = $"pre-reference persistent topology {row.Faults}/{row.Total}";
            foreach (var electrode in new[] { row.Stimulation, Mod(row.Stimulation + 1) })
            {
                AddCandidate(
                    electrode,
                    score,
                    ElectrodeFaultType.UncertainStructured,
                    ElectrodeEvidenceKind.EvidenceD | ElectrodeEvidenceKind.PersistentTopology,
                    reason,
                    scores,
                    faultTypes,
                    evidenceKinds,
                    reasons);
            }
        }
    }

    private void ApplySaturationFaults(
        EcdCwrEvidenceDResult evidenceD,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        foreach (var window in evidenceD.WindowScores.Where(window => window.HardFault))
        {
            foreach (var electrode in new[]
                     {
                         Mod(window.ExpectedReferenceChannel),
                         Mod(window.ExpectedReferenceChannel + 1)
                     })
            {
                AddCandidate(
                    electrode,
                    options.HardFaultScore,
                    ElectrodeFaultType.ElectrodeContact,
                    ElectrodeEvidenceKind.EvidenceD | ElectrodeEvidenceKind.Saturation,
                    "pre-reference D saturation",
                    scores,
                    faultTypes,
                    evidenceKinds,
                    reasons);
            }
        }
    }

    private static void AddCandidate(
        int electrode,
        double score,
        ElectrodeFaultType faultType,
        ElectrodeEvidenceKind evidenceKind,
        string reason,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        evidenceKinds[electrode] |= evidenceKind;
        if (score < scores[electrode])
        {
            return;
        }

        scores[electrode] = score;
        faultTypes[electrode] = faultType;
        reasons[electrode] = reason;
    }

    private static double[] ReadColumn(double[,] values, int column)
    {
        var result = new double[ElectrodeCount];
        for (var row = 0; row < ElectrodeCount; row++)
        {
            result[row] = values[row, column];
        }

        return result;
    }

    private static double[] RobustAbsoluteScores(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).Order().ToArray();
        if (finite.Length == 0)
        {
            return new double[values.Count];
        }

        var median = MedianSorted(finite);
        var deviations = finite.Select(value => Math.Abs(value - median)).Order().ToArray();
        var mad = MedianSorted(deviations);
        var scale = Math.Max(1.0e-12, Math.Max(1.4826 * mad, Math.Abs(median) * 0.1));
        return values
            .Select(value => double.IsFinite(value) ? Math.Abs(value - median) / scale : 0.0)
            .ToArray();
    }

    private static double MedianSorted(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private bool TryFindHalfSplit(
        IReadOnlyList<double> values,
        out double splitRatio)
    {
        splitRatio = 0.0;
        if (values.Count != ElectrodeCount || values.Any(value => !double.IsFinite(value) || value < 0.0))
        {
            return false;
        }

        var ordered = values
            .Select((value, electrode) => (value, electrode))
            .OrderBy(item => item.value)
            .ToArray();
        var lowerBoundary = ordered[(ElectrodeCount / 2) - 1].value;
        var upperBoundary = ordered[ElectrodeCount / 2].value;
        if (lowerBoundary <= 1.0e-12)
        {
            return false;
        }

        splitRatio = upperBoundary / lowerBoundary;
        if (splitRatio < options.DirectAHalfSplitMinimumRatio)
        {
            return false;
        }

        return true;
    }

    private static string CreateSummary(IReadOnlyList<ElectrodeContactState> states, bool systemLevel)
    {
        static string List(IReadOnlyList<ElectrodeContactState> values, ElectrodeContactState state)
        {
            var electrodes = Enumerable.Range(0, values.Count)
                .Where(index => values[index] == state)
                .Select(index => (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return $"[{string.Join(',', electrodes)}]";
        }

        var prefix = systemLevel ? "启动诊断系统级报警" : "启动诊断";
        return $"{prefix}（无 qc_ref，仅诊断、未重构）：dark={List(states, ElectrodeContactState.DarkRed)} red={List(states, ElectrodeContactState.Red)} yellow={List(states, ElectrodeContactState.Yellow)}";
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }

    private readonly record struct DrivePairConsensusState(
        bool HasCandidate,
        bool Confirmed,
        bool JustConfirmed,
        int Stimulation,
        int SupportCount,
        int ObservationCount,
        double ActiveMedianScore,
        int ClearUpdates);

    private readonly record struct RollingEndpointConfirmationState(
        bool[] ConfirmedElectrodes,
        int LeftSupportCount,
        int RightSupportCount,
        int ObservationCount);

    private readonly record struct SevereUnilateralConfirmationState(
        bool[] ConfirmedElectrodes,
        int[] SupportCounts,
        int ObservationCount);
}
