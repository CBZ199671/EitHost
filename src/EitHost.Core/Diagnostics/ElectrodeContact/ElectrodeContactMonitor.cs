using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public enum ElectrodeContactState
{
    Green = 0,
    Yellow = 1,
    Red = 2,
    DarkRed = 3,
    SystemLevel = 4
}

public enum ElectrodeFaultType
{
    None = 0,
    ElectrodeContact = 1,
    DrivePairLink = 2,
    AcquisitionChannel = 3,
    SystemLevel = 4,
    UncertainStructured = 5,
    NoiseCandidate = 6
}

[Flags]
public enum ElectrodeEvidenceKind
{
    None = 0,
    EvidenceA = 1,
    EvidenceD = 2,
    Saturation = 4,
    SystemSentinel = 8,
    EvidenceE = 16,
    EvidenceB = 32,
    EvidenceC = 64,
    EvidenceF = 128,
    PersistentTopology = 256,
    MultiFaultConsensus = 512,
    MultiFaultNeighbor = 1024,
    PreReferenceRelative48 = 2048,
    PreReferenceConsensus = 4096,
    PreReferenceShared48 = 8192,
    PreReferenceDrivePair48 = 16384,
    PreReferenceBilateralShared48 = 32768,
    PreReferenceHalfSplit = 65536
}

public sealed record ElectrodeContactMonitorOptions
{
    public double RelativeNoiseFloor { get; init; } = 0.05;

    public double AbsoluteNoiseFloor { get; init; } = 1.0e-9;

    public double CandidateZThreshold { get; init; } = 3.0;

    public double SevereZThreshold { get; init; } = 15.0;

    public double AOnlyRedConfirmationScore { get; init; } = 2.0;

    public double AOnlyConfirmationFallPerFrame { get; init; } = 0.2;

    public double DominantGapThreshold { get; init; } = 4.0;

    public double DominantRedConfirmationScore { get; init; } = 2.0;

    public double DominantConfirmationFallPerFrame { get; init; } = 0.1;

    public int MaxSparseDirectACandidates { get; init; } = 4;

    public double PhysicalFieldMedianRelativeThreshold { get; init; } = 0.03;

    public double PhysicalFieldP90RelativeUpperThreshold { get; init; } = 0.80;

    public double PhysicalFieldP90MedianSpreadThreshold { get; init; } = 0.05;

    public int RecoveryConfirmationFrames { get; init; } = 2;

    public int IntermittentRecoveryConfirmationFrames { get; init; } = 3;

    public double RecoveryDirectADropRatio { get; init; } = 0.5;

    public double SystemMedianZThreshold { get; init; } = 8.0;

    public double EwmaRise { get; init; } = 0.5;

    public double EwmaFall { get; init; } = 0.05;

    public double YellowThreshold { get; init; } = 2.0;

    public double RedThreshold { get; init; } = 5.0;

    public int MaxElectrodeCandidatesBeforeSystemLevel { get; init; } = 8;

    public double MultiFaultBackgroundRatio { get; init; } = 1.5;

    public double MultiFaultMinimumTopologySupportFraction { get; init; } = 0.5;

    public double MultiFaultConfirmationScore { get; init; } = 2.0;

    public double MultiFaultConfirmationFallPerFrame { get; init; } = 0.5;

    public double TopologyDistanceWeight { get; init; } = 1.0;

    public double ArgmaxDistanceWeight { get; init; } = 1.0;

    public double WeakSignalPenaltyWeight { get; init; } = 1.0;

    public double YellowMeasurementWeight { get; init; } = 0.5;

    public string WeightPolicyVersion { get; init; } = "ecd-cwr-p1-binary-v1";

    public bool UseFaultDictionaryLocalization { get; init; } = true;

    public EcdCwrFaultDictionaryPolicy FaultDictionaryPolicy { get; init; } =
        EcdCwrFaultDictionaryPolicies.SelectedPolicy;

    public bool UseContinuousMeasurementWeights { get; init; } = true;

    public double ContinuousWeightQ0 { get; init; } = 2.0;

    public double ContinuousWeightPower { get; init; } = 2.0;

    public double ContinuousMinimumWeight { get; init; } = 0.02;

    public EcdCwrContactDriftBasis ContactDriftBasis { get; init; } =
        EcdCwrContactDriftBasis.ConstantAndFirstHarmonic;

    public double ContactDriftL1Penalty { get; init; } = 0.05;

    public double ContactDriftRidge { get; init; } = 1.0e-3;

    public double ReciprocityWeight { get; init; } = 1.0;

    public double DynamicReciprocityWeight { get; init; } = 0.25;

    public double ReciprocityDynamicFrameDeltaRmsThreshold { get; init; } = double.PositiveInfinity;

    public double ReciprocityDynamicThresholdGain { get; init; } = 1.0;

    public double ShapeWeight { get; init; } = 0.35;

    public double ContactSubspaceCandidateThreshold { get; init; } = 0.5;

    public double ContactSubspaceCandidateWeight { get; init; } = 1.0;

    public double ContactSubspaceCoefficientRelativeThreshold { get; init; } = 0.25;
}

public sealed record ElectrodeContactBaseline(double[,] Real256, double[,] Imaginary256)
{
    public const int ElectrodeCount = 16;
    public const int FullObservationCount = 256;
    public const int RetainedObservationCount = 208;

    public static ElectrodeContactBaseline FromReference(double[,] real256, double[,] imaginary256)
    {
        ValidateFullMatrix(real256, nameof(real256));
        ValidateFullMatrix(imaginary256, nameof(imaginary256));
        return new ElectrodeContactBaseline(CloneMatrix(real256), CloneMatrix(imaginary256));
    }

    internal static void ValidateFullMatrix(double[,] values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.GetLength(0) != ElectrodeCount || values.GetLength(1) != ElectrodeCount)
        {
            throw new ArgumentException("Electrode contact diagnostics require full 16x16 observations.", name);
        }
    }

    internal static double[,] CloneMatrix(double[,] values)
    {
        var clone = new double[values.GetLength(0), values.GetLength(1)];
        Array.Copy(values, clone, values.Length);
        return clone;
    }
}

public sealed record ElectrodeContactDiagnosticResult(
    ElectrodeContactState[] States,
    ElectrodeFaultType[] FaultTypes,
    double[] Scores,
    double[] FaultConfidence,
    string[] UpgradeGateReasons,
    double[] MeasurementWeight208,
    double ImageQualityScore,
    string WeightPolicyVersion,
    string Summary,
    bool SystemLevel,
    bool ReferenceInvalidated = false,
    EcdCwrMultiFrequencyScoreFusionResult? MultiFrequencyFusion = null,
    double[]? DirectEvidenceAScores = null,
    bool PhysicalFieldGuardApplied = false,
    double RetainedMedianRelativeChange = 0.0,
    double RetainedP90RelativeChange = 0.0,
    double[]? CandidateScores = null,
    ElectrodeFaultType[]? CandidateFaultTypes = null,
    ElectrodeEvidenceKind[]? CandidateEvidenceKinds = null,
    string[]? CandidateReasons = null,
    EcdCwrSupplementalEvidenceSummary? SupplementalEvidence = null,
    EcdCwrRuntimeEvidenceSummary? RuntimeEvidence = null,
    EcdCwrFaultDictionaryTrace? FaultDictionaryTrace = null,
    EcdCwrContactSubspaceEvidenceSummary? ContactSubspaceEvidence = null,
    EcdCwrMultiFaultDirectAConsensusResult? MultiFaultConsensus = null,
    bool PreReferenceOnly = false,
    EcdCwrPreReferenceConsensusResult? PreReferenceConsensus = null)
{
    public int RedLikeElectrodeCount => States.Count(state => state is ElectrodeContactState.Red or ElectrodeContactState.DarkRed);
}

public sealed record EcdCwrContactSubspaceEvidenceInput(
    double[,]? ContactJacobian,
    string MeasurementSpace,
    string Source,
    string Status)
{
    public const string Amplitude208 = "amplitude208";
    public const string ComplexStacked416 = "complex-stacked416";

    public static EcdCwrContactSubspaceEvidenceInput Unavailable(string reason)
    {
        return new EcdCwrContactSubspaceEvidenceInput(
            null,
            string.Empty,
            "realtime-backend",
            string.IsNullOrWhiteSpace(reason) ? "unavailable: J_z not supplied" : reason.Trim());
    }
}

public sealed record EcdCwrContactSubspaceEvidenceSummary(
    bool EvidenceFAvailable,
    bool CandidateApplied,
    string Status,
    string Source,
    string MeasurementSpace,
    double ContactSubspaceScore,
    double ProjectedNorm,
    double ResidualNorm,
    double[] ContactCoefficients)
{
    public static EcdCwrContactSubspaceEvidenceSummary Unavailable(
        string reason,
        string source = "realtime-backend",
        string measurementSpace = "")
    {
        return new EcdCwrContactSubspaceEvidenceSummary(
            false,
            false,
            string.IsNullOrWhiteSpace(reason) ? "unavailable: J_z not supplied" : reason.Trim(),
            string.IsNullOrWhiteSpace(source) ? "realtime-backend" : source.Trim(),
            measurementSpace?.Trim() ?? string.Empty,
            0.0,
            0.0,
            0.0,
            new double[ElectrodeContactBaseline.ElectrodeCount]);
    }

    public static EcdCwrContactSubspaceEvidenceSummary NotApplicable(string reason)
    {
        return Unavailable($"not-applicable: {reason}");
    }
}

public sealed record EcdCwrSupplementalEvidenceSummary(
    bool EvidenceBAvailable,
    bool EvidenceCAvailable,
    bool ReciprocityDynamicTooFast,
    int ReciprocityViolationCount,
    double ReciprocityMaxWhitenedScore,
    double ShapeMaxScore,
    string ReciprocityStatus,
    string ShapeStatus);

public sealed record EcdCwrRuntimeEvidenceSummary(
    bool EvidenceDAvailable,
    int EvidenceDSoftViolationCount,
    int EvidenceDHardFaultCount,
    double EvidenceDMaxScore,
    bool RawGlobalSentinelTriggered,
    double RawContact48MedianZ,
    double RawDriveMedianZ,
    double SaturationRatio,
    string SystemSentinelReason,
    string FaultDictionaryPolicyVersion);

public sealed record EcdCwrFaultDictionaryTrace(
    string PolicyVersion,
    double[] DriveScores,
    double[] MeasureScores,
    double[] PairLinkScores,
    double[] MeasurementChannelScores,
    double ResidualRms,
    int ObservationCount)
{
    public int ActiveCoefficientCount(double threshold = 1.0e-9)
    {
        return DriveScores
            .Concat(MeasureScores)
            .Concat(PairLinkScores)
            .Concat(MeasurementChannelScores)
            .Count(score => double.IsFinite(score) && score > threshold);
    }
}

public sealed class ElectrodeContactMonitor
{
    private const int ElectrodeCount = ElectrodeContactBaseline.ElectrodeCount;

    private readonly ElectrodeContactBaseline baseline;
    private readonly ElectrodeContactMonitorOptions options;
    private EcdCwrHealthCalibration? healthCalibration;
    private DemodulatedFrame? previousSupplementalFrame;
    private double[,]? contactNoiseScale256;
    private readonly double[] ewmaScores = new double[ElectrodeCount];
    private readonly double[] aOnlyRedConfirmation = new double[ElectrodeCount];
    private readonly double[] dominantRedConfirmation = new double[ElectrodeCount];
    private readonly bool[] dominantConfirmationHadGap = new bool[ElectrodeCount];
    private readonly bool[] intermittentContactLatched = new bool[ElectrodeCount];
    private readonly int[] directARecoveryFrames = new int[ElectrodeCount];
    private readonly double[] criticalDirectAPeak = new double[ElectrodeCount];
    private readonly bool[] criticalSinceReference = new bool[ElectrodeCount];
    private readonly EcdCwrMultiFaultDirectAConsensusTracker multiFaultDirectATracker;

    public ElectrodeContactMonitor(
        ElectrodeContactBaseline baseline,
        ElectrodeContactMonitorOptions? options = null,
        EcdCwrHealthCalibration? healthCalibration = null)
    {
        this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        this.options = options ?? new ElectrodeContactMonitorOptions();
        multiFaultDirectATracker = new EcdCwrMultiFaultDirectAConsensusTracker(
            new EcdCwrMultiFaultDirectAConsensusOptions(
                SevereThreshold: this.options.SevereZThreshold,
                MinimumCandidateCount: 1,
                MaximumCandidateCount: this.options.MaxElectrodeCandidatesBeforeSystemLevel,
                BackgroundGap: this.options.DominantGapThreshold,
                BackgroundRatio: this.options.MultiFaultBackgroundRatio,
                MinimumTopologySupportFraction: this.options.MultiFaultMinimumTopologySupportFraction,
                ConfirmationScore: this.options.MultiFaultConfirmationScore,
                ReleaseFallPerUpdate: this.options.MultiFaultConfirmationFallPerFrame));
        if (healthCalibration is not null)
        {
            SetHealthCalibration(healthCalibration);
        }
    }

    public void SetHealthCalibration(EcdCwrHealthCalibration healthCalibration)
    {
        ArgumentNullException.ThrowIfNull(healthCalibration);
        var firstCalibration = this.healthCalibration is null;
        var scales = new double[ElectrodeCount, ElectrodeCount];
        var populated = new bool[ElectrodeCount, ElectrodeCount];
        foreach (var statistic in healthCalibration.Contact48)
        {
            var stimulation = statistic.StimulationIndex;
            var relativeChannel = statistic.RelativeChannelIndex;
            if (stimulation is < 0 or >= ElectrodeCount ||
                relativeChannel is < 0 or >= ElectrodeCount ||
                relativeChannel != 0 && relativeChannel != 1 && relativeChannel != ElectrodeCount - 1)
            {
                throw new InvalidOperationException(
                    $"Health calibration contains invalid contact48 index s={stimulation}, k={relativeChannel}.");
            }

            if (populated[stimulation, relativeChannel])
            {
                throw new InvalidOperationException(
                    $"Health calibration contains duplicate contact48 statistic s={stimulation}, k={relativeChannel}.");
            }

            var robustScale = Math.Max(statistic.MagnitudeSigma, 1.4826 * statistic.MagnitudeMad);
            scales[stimulation, relativeChannel] = double.IsFinite(robustScale) && robustScale > 0.0
                ? Math.Max(options.AbsoluteNoiseFloor, robustScale)
                : options.AbsoluteNoiseFloor;
            populated[stimulation, relativeChannel] = true;
        }

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            foreach (var relativeChannel in new[] { 0, 1, ElectrodeCount - 1 })
            {
                if (!populated[stimulation, relativeChannel])
                {
                    throw new InvalidOperationException(
                        $"Health calibration is missing contact48 statistic s={stimulation}, k={relativeChannel}.");
                }
            }
        }

        contactNoiseScale256 = scales;
        this.healthCalibration = healthCalibration;
        if (firstCalibration)
        {
            previousSupplementalFrame = null;
        }
    }

    public ElectrodeContactDiagnosticResult Update(
        double[,] real256,
        double[,] imaginary256,
        IReadOnlyList<DemodulatedWindowQuality>? windowQualities = null,
        double? primaryFrequencyHz = null,
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame>? peerFrequencyEvidence = null,
        EcdCwrContactSubspaceEvidenceInput? contactSubspaceEvidence = null)
    {
        ElectrodeContactBaseline.ValidateFullMatrix(real256, nameof(real256));
        ElectrodeContactBaseline.ValidateFullMatrix(imaginary256, nameof(imaginary256));

        var supplementalEvidence = AnalyzeSupplementalEvidence(real256, imaginary256, windowQualities);
        var evidenceD = AnalyzeEvidenceD(windowQualities);
        var dictionaryPolicy = EcdCwrFaultDictionaryPolicies.Get(options.FaultDictionaryPolicy);
        var frameScores = new double[ElectrodeCount];
        var faultTypes = Enumerable.Repeat(ElectrodeFaultType.None, ElectrodeCount).ToArray();
        var evidenceKinds = new ElectrodeEvidenceKind[ElectrodeCount];
        var reasons = Enumerable.Repeat("green", ElectrodeCount).ToArray();
        var z48 = ComputeZ48(real256, imaginary256);
        var rawDriveMedianZ = Median(z48.Drive);
        var rawDriveSystemLevel = rawDriveMedianZ >= options.SystemMedianZThreshold &&
            z48.Drive.Count(score => score >= options.CandidateZThreshold) > ElectrodeCount / 2;
        var saturationRatio = evidenceD.WindowScores.Count == 0
            ? 0.0
            : evidenceD.WindowScores.Count(score => score.HardFault) / (double)evidenceD.WindowScores.Count;
        var globalSentinel = EvaluateRawGlobalSentinel(z48, saturationRatio);
        var rawGlobalSystemLevel = rawDriveSystemLevel || globalSentinel.Triggered;
        var runtimeEvidence = new EcdCwrRuntimeEvidenceSummary(
            EvidenceDAvailable: windowQualities is not null,
            EvidenceDSoftViolationCount: evidenceD.WindowScores.Count(score => !score.HardFault && score.Score > 0.0),
            EvidenceDHardFaultCount: evidenceD.WindowScores.Count(score => score.HardFault),
            EvidenceDMaxScore: evidenceD.MaxScore,
            RawGlobalSentinelTriggered: rawGlobalSystemLevel,
            RawContact48MedianZ: globalSentinel.MedianZ48,
            RawDriveMedianZ: rawDriveMedianZ,
            SaturationRatio: saturationRatio,
            SystemSentinelReason: rawDriveSystemLevel
                ? "raw drive-row global sentinel"
                : globalSentinel.Reason,
            FaultDictionaryPolicyVersion: dictionaryPolicy.Version);
        var sparseZ48 = SuppressContactDrift(z48);
        ApplyEvidenceA(sparseZ48, frameScores, faultTypes, evidenceKinds, reasons);
        ApplyEvidenceD(evidenceD, frameScores, faultTypes, evidenceKinds, reasons);
        ApplyPersistentTopologyEvidence(evidenceD, frameScores, faultTypes, evidenceKinds, reasons);
        var topologyScores = CreateTopologyScores(evidenceD);
        EcdCwrFaultLocalizationResult? faultLocalization = null;
        if (options.UseFaultDictionaryLocalization)
        {
            faultLocalization = ApplyFaultDictionaryLocalization(
                sparseZ48,
                topologyScores,
                supplementalEvidence,
                dictionaryPolicy,
                frameScores,
                faultTypes,
                evidenceKinds,
                reasons);
        }

        var multiFrequencyFusion = ApplyEvidenceE(
            primaryFrequencyHz,
            peerFrequencyEvidence,
            frameScores,
            faultTypes,
            evidenceKinds,
            reasons);
        var directElectrodeAScores = BuildDirectElectrodeAScores(sparseZ48);
        var persistentTopologySupport = evidenceKinds
            .Select(kind => (kind & ElectrodeEvidenceKind.PersistentTopology) != 0)
            .ToArray();
        var multiFaultConsensus = multiFaultDirectATracker.Update(
            directElectrodeAScores,
            persistentTopologySupport);
        var saturationCandidateCount = evidenceKinds.Count(kind =>
            (kind & ElectrodeEvidenceKind.Saturation) != 0);
        var systemLevel = saturationCandidateCount >= options.MaxElectrodeCandidatesBeforeSystemLevel ||
            multiFaultConsensus.SystemLevelTriggered ||
            rawGlobalSystemLevel;
        if (systemLevel)
        {
            var candidateScores = rawGlobalSystemLevel
                ? BuildDirectElectrodeAScores(z48)
                : frameScores;
            var systemLevelReason = rawGlobalSystemLevel
                ? runtimeEvidence.SystemSentinelReason
                : multiFaultConsensus.SystemLevelTriggered
                    ? multiFaultConsensus.Status
                    : "saturation sentinel";
            var localizedSparseLimit = !rawGlobalSystemLevel &&
                multiFaultConsensus.Candidates.Count(selected => selected) ==
                    options.MaxElectrodeCandidatesBeforeSystemLevel;
            if (localizedSparseLimit)
            {
                return BuildLocalizedSparseLimitSystemLevelResult(
                    frameScores,
                    faultTypes,
                    evidenceKinds,
                    reasons,
                    directElectrodeAScores,
                    multiFaultConsensus,
                    supplementalEvidence.Summary,
                    runtimeEvidence,
                    CreateFaultDictionaryTrace(dictionaryPolicy, faultLocalization),
                    systemLevelReason);
            }

            return BuildSystemLevelResult(
                candidateScores,
                systemLevelReason,
                supplementalEvidence.Summary,
                runtimeEvidence,
                CreateFaultDictionaryTrace(dictionaryPolicy, faultLocalization),
                EcdCwrContactSubspaceEvidenceSummary.NotApplicable(
                    systemLevelReason),
                directElectrodeAScores,
                multiFaultConsensus);
        }

        var contactSubspaceSummary = saturationCandidateCount > 0
            ? EcdCwrContactSubspaceEvidenceSummary.NotApplicable("saturation hard fault")
            : AnalyzeContactSubspaceEvidence(
                real256,
                imaginary256,
                contactSubspaceEvidence);
        var candidateScoresSnapshot = frameScores.ToArray();
        var candidateFaultTypesSnapshot = faultTypes.ToArray();
        var candidateEvidenceKindsSnapshot = evidenceKinds.ToArray();
        var candidateReasonsSnapshot = reasons.ToArray();
        contactSubspaceSummary = ApplyContactSubspaceCandidate(
            contactSubspaceSummary,
            candidateScoresSnapshot,
            candidateFaultTypesSnapshot,
            candidateEvidenceKindsSnapshot,
            candidateReasonsSnapshot);
        SuppressCandidateOnlyActions(
            candidateEvidenceKindsSnapshot,
            frameScores,
            faultTypes,
            evidenceKinds,
            reasons);

        var directACandidateCount = directElectrodeAScores.Count(score =>
            score >= options.CandidateZThreshold);
        var softQualityIssuePresent = windowQualities?.Any(quality =>
            quality.State != DemodulatedWindowQualityState.Valid &&
            quality.RejectReason != DemodulatedWindowRejectReason.AdcSaturation &&
            quality.AdcSaturationCount == 0) == true;
        var retainedFieldChange = AnalyzeRetainedFieldChange(real256, imaginary256);
        var physicalTargetLike = directACandidateCount > 0 &&
            retainedFieldChange.MedianRelative >= options.PhysicalFieldMedianRelativeThreshold &&
            retainedFieldChange.P90Relative <= options.PhysicalFieldP90RelativeUpperThreshold &&
            retainedFieldChange.P90Relative - retainedFieldChange.MedianRelative >=
                options.PhysicalFieldP90MedianSpreadThreshold;
        var physicalFieldGuardApplied = saturationCandidateCount == 0 &&
            (softQualityIssuePresent ||
             directACandidateCount > Math.Max(1, options.MaxSparseDirectACandidates) ||
             physicalTargetLike);
        if (physicalFieldGuardApplied)
        {
            SuppressWidespreadPhysicalFieldEvidence(frameScores, faultTypes, evidenceKinds, reasons);
        }

        ApplyMultiFaultDirectAConsensus(
            multiFaultConsensus,
            directElectrodeAScores,
            frameScores,
            faultTypes,
            evidenceKinds,
            reasons);
        ApplyMultiFaultNeighborWarnings(
            multiFaultConsensus,
            frameScores,
            faultTypes,
            evidenceKinds,
            reasons);

        UpdateDominantRedConfirmation(directElectrodeAScores, !physicalFieldGuardApplied);
        UpdateDirectARecoveryFrames(directElectrodeAScores, evidenceKinds);

        var dynamicRedThreshold = ComputeGapRedThreshold(frameScores);
        var states = new ElectrodeContactState[ElectrodeCount];
        var confidence = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var previous = ewmaScores[electrode];
            var current = frameScores[electrode];
            var lambda = current >= previous ? options.EwmaRise : options.EwmaFall;
            var score = (1.0 - lambda) * previous + lambda * current;
            ewmaScores[electrode] = score;
            confidence[electrode] = Math.Clamp(score / Math.Max(dynamicRedThreshold, 1.0e-12), 0.0, 1.0);

            UpdateAOnlyRedConfirmation(
                electrode,
                current,
                evidenceKinds[electrode],
                faultTypes[electrode]);
            var canRed = CanUpgradeToRed(
                electrode,
                current,
                evidenceKinds[electrode],
                faultTypes[electrode]);
            var dominantConfirmed = IsDominantRedConfirmed(electrode);
            var multiFaultConfirmed =
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.MultiFaultConsensus) != 0;
            if (dominantConfirmed && dominantConfirmationHadGap[electrode])
            {
                intermittentContactLatched[electrode] = true;
            }

            var electrodeRedThreshold = dominantConfirmed || multiFaultConfirmed
                ? options.RedThreshold
                : dynamicRedThreshold;
            if ((evidenceKinds[electrode] & ElectrodeEvidenceKind.Saturation) != 0)
            {
                states[electrode] = ElectrodeContactState.DarkRed;
                faultTypes[electrode] = NormalizeFaultType(faultTypes[electrode], canRed);
                reasons[electrode] = "D saturation";
            }
            else if (score >= electrodeRedThreshold && canRed)
            {
                states[electrode] = ElectrodeContactState.Red;
                faultTypes[electrode] = dominantConfirmed
                    ? ElectrodeFaultType.ElectrodeContact
                    : NormalizeFaultType(faultTypes[electrode], canRed);
                if (dominantConfirmed)
                {
                    reasons[electrode] = "A persistent dominant electrode";
                }
            }
            else if (score >= options.YellowThreshold)
            {
                states[electrode] = ElectrodeContactState.Yellow;
                if (faultTypes[electrode] == ElectrodeFaultType.ElectrodeContact && !canRed)
                {
                    faultTypes[electrode] = ElectrodeFaultType.UncertainStructured;
                    reasons[electrode] = evidenceKinds[electrode] == ElectrodeEvidenceKind.EvidenceA
                        ? "A-only target guard"
                        : "soft evidence upgrade gate";
                }
            }
            else
            {
                states[electrode] = ElectrodeContactState.Green;
                faultTypes[electrode] = ElectrodeFaultType.None;
                reasons[electrode] = "green";
            }

            var recoveryFramesRequired = intermittentContactLatched[electrode]
                ? Math.Max(options.RecoveryConfirmationFrames, options.IntermittentRecoveryConfirmationFrames)
                : options.RecoveryConfirmationFrames;
            if (criticalSinceReference[electrode] &&
                directARecoveryFrames[electrode] < recoveryFramesRequired &&
                states[electrode] != ElectrodeContactState.DarkRed)
            {
                states[electrode] = ElectrodeContactState.Red;
                faultTypes[electrode] = ElectrodeFaultType.ElectrodeContact;
                reasons[electrode] = physicalFieldGuardApplied
                    ? "confirmed contact held through physical-field guard"
                    : "confirmed contact held pending direct-A recovery";
                ewmaScores[electrode] = Math.Max(ewmaScores[electrode], options.RedThreshold);
                confidence[electrode] = Math.Max(confidence[electrode], 1.0);
            }

            if (criticalSinceReference[electrode] &&
                directARecoveryFrames[electrode] >= recoveryFramesRequired)
            {
                states[electrode] = ElectrodeContactState.Green;
                faultTypes[electrode] = ElectrodeFaultType.None;
                reasons[electrode] = "A recovery confirmed";
                ewmaScores[electrode] = 0.0;
                aOnlyRedConfirmation[electrode] = 0.0;
                dominantRedConfirmation[electrode] = 0.0;
                dominantConfirmationHadGap[electrode] = false;
                intermittentContactLatched[electrode] = false;
                criticalDirectAPeak[electrode] = 0.0;
                multiFaultDirectATracker.ResetElectrode(electrode);
            }

            if (states[electrode] is ElectrodeContactState.Red or ElectrodeContactState.DarkRed)
            {
                criticalSinceReference[electrode] = true;
                criticalDirectAPeak[electrode] = Math.Max(
                    criticalDirectAPeak[electrode],
                    directElectrodeAScores[electrode]);
            }
        }

        var referenceInvalidated = false;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (states[electrode] == ElectrodeContactState.Green && criticalSinceReference[electrode])
            {
                referenceInvalidated = true;
                criticalSinceReference[electrode] = false;
            }
        }

        var weights = BuildMeasurementWeights(states, ewmaScores, evidenceKinds, faultTypes);
        var quality = ComputeImageQuality(states, faultTypes, weights);
        return new ElectrodeContactDiagnosticResult(
            states,
            faultTypes,
            ewmaScores.ToArray(),
            confidence,
            reasons,
            weights,
            quality,
            CreateWeightPolicyVersion(),
            CreateSummary(states, faultTypes, quality, physicalFieldGuardApplied),
            SystemLevel: false,
            ReferenceInvalidated: referenceInvalidated,
            multiFrequencyFusion,
            directElectrodeAScores,
            physicalFieldGuardApplied,
            retainedFieldChange.MedianRelative,
            retainedFieldChange.P90Relative,
            candidateScoresSnapshot,
            candidateFaultTypesSnapshot,
            candidateEvidenceKindsSnapshot,
            candidateReasonsSnapshot,
            supplementalEvidence.Summary,
            runtimeEvidence,
            CreateFaultDictionaryTrace(dictionaryPolicy, faultLocalization),
            contactSubspaceSummary,
            multiFaultConsensus);
    }

    private EcdCwrContactSubspaceEvidenceSummary AnalyzeContactSubspaceEvidence(
        double[,] real256,
        double[,] imaginary256,
        EcdCwrContactSubspaceEvidenceInput? input)
    {
        if (input?.ContactJacobian is not { } contactJacobian)
        {
            return EcdCwrContactSubspaceEvidenceSummary.Unavailable(
                input?.Status ?? "unavailable: realtime backend has not supplied J_z",
                input?.Source ?? "realtime-backend",
                input?.MeasurementSpace ?? string.Empty);
        }

        var measurementSpace = input.MeasurementSpace?.Trim() ?? string.Empty;
        try
        {
            if (contactJacobian.GetLength(1) != ElectrodeCount)
            {
                throw new InvalidDataException(
                    $"J_z columns={contactJacobian.GetLength(1)}; expected {ElectrodeCount}.");
            }

            var deltaVoltage = measurementSpace switch
            {
                EcdCwrContactSubspaceEvidenceInput.Amplitude208 =>
                    BuildRetainedAmplitudeDelta(real256, imaginary256),
                EcdCwrContactSubspaceEvidenceInput.ComplexStacked416 =>
                    BuildRetainedComplexDelta(real256, imaginary256),
                _ => throw new InvalidDataException(
                    $"unsupported J_z measurement space '{measurementSpace}'.")
            };
            if (contactJacobian.GetLength(0) != deltaVoltage.Length)
            {
                throw new InvalidDataException(
                    $"J_z rows={contactJacobian.GetLength(0)}; expected {deltaVoltage.Length} for {measurementSpace}.");
            }

            for (var row = 0; row < contactJacobian.GetLength(0); row++)
            {
                for (var column = 0; column < contactJacobian.GetLength(1); column++)
                {
                    if (!double.IsFinite(contactJacobian[row, column]))
                    {
                        throw new InvalidDataException($"J_z contains non-finite value at [{row},{column}].");
                    }
                }
            }

            var result = new EcdCwrContactSubspaceAnalyzer().Analyze(deltaVoltage, contactJacobian);
            return new EcdCwrContactSubspaceEvidenceSummary(
                EvidenceFAvailable: true,
                CandidateApplied: false,
                Status: "available: candidate-only linear contact-subspace evidence",
                Source: string.IsNullOrWhiteSpace(input.Source) ? "realtime-backend" : input.Source.Trim(),
                MeasurementSpace: measurementSpace,
                result.ContactSubspaceScore,
                result.ProjectedNorm,
                result.ResidualNorm,
                result.ContactCoefficients.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or ArithmeticException)
        {
            return EcdCwrContactSubspaceEvidenceSummary.Unavailable(
                $"incompatible: {ex.Message}",
                input.Source,
                measurementSpace);
        }
    }

    private EcdCwrContactSubspaceEvidenceSummary ApplyContactSubspaceCandidate(
        EcdCwrContactSubspaceEvidenceSummary summary,
        double[] candidateScores,
        ElectrodeFaultType[] candidateFaultTypes,
        ElectrodeEvidenceKind[] candidateEvidenceKinds,
        string[] candidateReasons)
    {
        if (!summary.EvidenceFAvailable ||
            summary.ContactSubspaceScore < options.ContactSubspaceCandidateThreshold ||
            summary.ContactCoefficients.Length != ElectrodeCount)
        {
            return summary;
        }

        var maxCoefficient = summary.ContactCoefficients
            .Where(double.IsFinite)
            .Select(Math.Abs)
            .DefaultIfEmpty(0.0)
            .Max();
        if (maxCoefficient <= options.AbsoluteNoiseFloor)
        {
            return summary;
        }

        var applied = false;
        var threshold = Math.Clamp(options.ContactSubspaceCoefficientRelativeThreshold, 0.0, 1.0);
        var scoreScale = options.CandidateZThreshold *
            Math.Max(0.0, options.ContactSubspaceCandidateWeight) *
            summary.ContactSubspaceScore /
            Math.Max(options.ContactSubspaceCandidateThreshold, 1.0e-12);
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var relativeCoefficient = Math.Abs(summary.ContactCoefficients[electrode]) / maxCoefficient;
            if (!double.IsFinite(relativeCoefficient) || relativeCoefficient < threshold)
            {
                continue;
            }

            candidateScores[electrode] = Math.Max(candidateScores[electrode], scoreScale * relativeCoefficient);
            if (candidateFaultTypes[electrode] is ElectrodeFaultType.None or ElectrodeFaultType.NoiseCandidate)
            {
                candidateFaultTypes[electrode] = ElectrodeFaultType.ElectrodeContact;
            }

            candidateEvidenceKinds[electrode] |= ElectrodeEvidenceKind.EvidenceF;
            var label = FormattableString.Invariant(
                $"F contact-subspace s_z={summary.ContactSubspaceScore:F3} coeff={relativeCoefficient:F3}");
            candidateReasons[electrode] = candidateReasons[electrode] == "green"
                ? label
                : $"{candidateReasons[electrode]}; {label}";
            applied = true;
        }

        return applied ? summary with { CandidateApplied = true } : summary;
    }

    private double[] BuildRetainedAmplitudeDelta(double[,] real256, double[,] imaginary256)
    {
        var delta = new double[ElectrodeContactBaseline.RetainedObservationCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relative = 2; relative <= 14; relative++)
            {
                var currentReal = real256[stimulation, relative];
                var currentImaginary = imaginary256[stimulation, relative];
                var baselineReal = baseline.Real256[stimulation, relative];
                var baselineImaginary = baseline.Imaginary256[stimulation, relative];
                var currentMagnitude = Math.Sqrt(
                    (currentReal * currentReal) + (currentImaginary * currentImaginary));
                var baselineMagnitude = Math.Sqrt(
                    (baselineReal * baselineReal) + (baselineImaginary * baselineImaginary));
                delta[offset++] = currentMagnitude - baselineMagnitude;
            }
        }

        return delta;
    }

    private double[] BuildRetainedComplexDelta(double[,] real256, double[,] imaginary256)
    {
        var retainedCount = ElectrodeContactBaseline.RetainedObservationCount;
        var delta = new double[retainedCount * 2];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relative = 2; relative <= 14; relative++)
            {
                delta[offset] = real256[stimulation, relative] - baseline.Real256[stimulation, relative];
                delta[offset + retainedCount] =
                    imaginary256[stimulation, relative] - baseline.Imaginary256[stimulation, relative];
                offset++;
            }
        }

        return delta;
    }

    private SupplementalEvidence AnalyzeSupplementalEvidence(
        double[,] real256,
        double[,] imaginary256,
        IReadOnlyList<DemodulatedWindowQuality>? windowQualities)
    {
        if (healthCalibration is null)
        {
            return SupplementalEvidence.Unavailable("health calibration unavailable");
        }

        var frame = CreateSupplementalFrame(real256, imaginary256, windowQualities);
        double[]? reciprocityScores = null;
        double[]? shapeScores = null;
        var reciprocityAvailable = false;
        var shapeAvailable = false;
        var dynamicTooFast = false;
        var reciprocityViolationCount = 0;
        var reciprocityMax = 0.0;
        var shapeMax = 0.0;
        var reciprocityStatus = "calibration has no reciprocal pairs";
        var shapeStatus = "calibration has no waveform templates";

        if (healthCalibration.ReciprocalPairs.Count > 0)
        {
            try
            {
                var reciprocity = new EcdCwrReciprocityAnalyzer().Analyze(
                    frame,
                    healthCalibration,
                    previousSupplementalFrame,
                    new EcdCwrReciprocityAnalyzerOptions(
                        BaseViolationThreshold: options.CandidateZThreshold,
                        DynamicFrameDeltaRmsThreshold: options.ReciprocityDynamicFrameDeltaRmsThreshold,
                        DynamicThresholdGain: options.ReciprocityDynamicThresholdGain));
                reciprocityScores = BuildReciprocityCandidateScores(reciprocity);
                reciprocityAvailable = reciprocity.PairScores.Count > 0;
                dynamicTooFast = reciprocity.DynamicTooFast;
                reciprocityViolationCount = reciprocity.ViolationCount;
                reciprocityMax = reciprocity.MaxWhitenedScore;
                reciprocityStatus = reciprocityAvailable
                    ? "available"
                    : "calibration yielded no retained reciprocal pairs";
            }
            catch (ArgumentException ex)
            {
                reciprocityStatus = $"unavailable: {ex.Message}";
            }
            catch (InvalidOperationException ex)
            {
                reciprocityStatus = $"unavailable: {ex.Message}";
            }
        }

        if (healthCalibration.WaveformTemplates.Count > 0)
        {
            try
            {
                var shape = new EcdCwrWaveformShapeAnalyzer().Analyze(frame, healthCalibration);
                shapeScores = shape.MeasurementScores208;
                shapeAvailable = shape.WindowScores.Count > 0;
                shapeMax = shape.MaxScore;
                shapeStatus = shapeAvailable
                    ? "available"
                    : "calibration yielded no matching waveform templates";
            }
            catch (ArgumentException ex)
            {
                shapeStatus = $"unavailable: {ex.Message}";
            }
            catch (InvalidOperationException ex)
            {
                shapeStatus = $"unavailable: {ex.Message}";
            }
        }

        previousSupplementalFrame = frame;
        var reciprocityWeight = dynamicTooFast
            ? SanitizeWeight(options.DynamicReciprocityWeight)
            : SanitizeWeight(options.ReciprocityWeight);
        return new SupplementalEvidence(
            reciprocityScores,
            shapeScores,
            reciprocityWeight,
            SanitizeWeight(options.ShapeWeight),
            new EcdCwrSupplementalEvidenceSummary(
                reciprocityAvailable,
                shapeAvailable,
                dynamicTooFast,
                reciprocityViolationCount,
                reciprocityMax,
                shapeMax,
                reciprocityStatus,
                shapeStatus));
    }

    private double[] BuildReciprocityCandidateScores(EcdCwrReciprocityResult result)
    {
        var scores = new double[ElectrodeContactBaseline.RetainedObservationCount];
        foreach (var pair in result.PairScores.Where(pair => pair.Violated))
        {
            var normalized = options.CandidateZThreshold +
                Math.Max(0.0, pair.WhitenedScore - pair.DynamicThreshold);
            scores[pair.RetainedIndex] = Math.Max(scores[pair.RetainedIndex], normalized);
            scores[pair.ReciprocalRetainedIndex] = Math.Max(
                scores[pair.ReciprocalRetainedIndex],
                normalized);
        }

        return scores;
    }

    private static DemodulatedFrame CreateSupplementalFrame(
        double[,] real256,
        double[,] imaginary256,
        IReadOnlyList<DemodulatedWindowQuality>? windowQualities)
    {
        var amplitudes = new double[ElectrodeCount, DemodulatedFrame.MeasurementsPerStimulation];
        var retainedReal = new double[ElectrodeCount, DemodulatedFrame.MeasurementsPerStimulation];
        var retainedImaginary = new double[ElectrodeCount, DemodulatedFrame.MeasurementsPerStimulation];
        var fullAmplitudes = new double[ElectrodeCount, ElectrodeCount];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = 0; relativeChannel < ElectrodeCount; relativeChannel++)
            {
                var real = real256[stimulation, relativeChannel];
                var imaginary = imaginary256[stimulation, relativeChannel];
                fullAmplitudes[stimulation, relativeChannel] = Math.Sqrt((real * real) + (imaginary * imaginary));
                if (relativeChannel is < 2 or > 14)
                {
                    continue;
                }

                var retainedColumn = relativeChannel - 2;
                retainedReal[stimulation, retainedColumn] = real;
                retainedImaginary[stimulation, retainedColumn] = imaginary;
                amplitudes[stimulation, retainedColumn] = fullAmplitudes[stimulation, relativeChannel];
            }
        }

        return new DemodulatedFrame(
            FrameNumber: 0,
            StartSample: 0,
            EndSample: 0,
            amplitudes,
            retainedReal,
            retainedImaginary,
            windowQualities ?? [],
            new int[ElectrodeCount, DemodulatedFrame.MeasurementsPerStimulation],
            fullAmplitudes,
            ElectrodeContactBaseline.CloneMatrix(real256),
            ElectrodeContactBaseline.CloneMatrix(imaginary256));
    }

    private static double SanitizeWeight(double value)
    {
        return double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
    }

    private EcdCwrEvidenceDResult AnalyzeEvidenceD(
        IReadOnlyList<DemodulatedWindowQuality>? qualities)
    {
        return new EcdCwrEvidenceDAnalyzer().Analyze(
            qualities ?? [],
            new EcdCwrEvidenceDOptions(
                Top3SetWeight: options.TopologyDistanceWeight,
                ArgmaxDistanceWeight: options.ArgmaxDistanceWeight,
                PeakToBackgroundWeight: options.WeakSignalPenaltyWeight,
                WeakReferenceWeight: options.WeakSignalPenaltyWeight,
                HardFaultScore: options.SevereZThreshold));
    }

    private EcdCwrSystemSentinelResult EvaluateRawGlobalSentinel(Z48 z48, double saturationRatio)
    {
        return new EcdCwrSystemSentinel().Evaluate(
            BuildEvidenceAResult(z48),
            frameRsd: 0.0,
            satRatio: saturationRatio,
            medianReciprocalScore: 0.0,
            new EcdCwrSystemSentinelOptions(
                MedianZ48Weight: 0.0,
                MedianReciprocalWeight: 0.0,
                FrameRsdWeight: 0.0,
                SaturationRatioWeight: 0.0,
                ScoreThreshold: double.PositiveInfinity,
                MedianZ48HardThreshold: options.SystemMedianZThreshold,
                SaturationRatioHardThreshold: double.PositiveInfinity));
    }

    private Z48 ComputeZ48(double[,] real256, double[,] imaginary256)
    {
        var drive = new double[ElectrodeCount];
        var right = new double[ElectrodeCount];
        var left = new double[ElectrodeCount];
        for (var stim = 0; stim < ElectrodeCount; stim++)
        {
            drive[stim] = ComputeZ(real256, imaginary256, stim, 0);
            right[stim] = ComputeZ(real256, imaginary256, stim, 1);
            left[stim] = ComputeZ(real256, imaginary256, stim, ElectrodeCount - 1);
        }

        return new Z48(drive, left, right);
    }

    private Z48 SuppressContactDrift(Z48 z48)
    {
        if (options.ContactDriftBasis == EcdCwrContactDriftBasis.None)
        {
            return z48;
        }

        var inversion = new EcdCwrContactImpedanceInverter().Invert(
            z48.Drive,
            z48.Left,
            z48.Right,
            options: new EcdCwrContactImpedanceInverterOptions(
                options.ContactDriftL1Penalty,
                DriftBasis: options.ContactDriftBasis,
                DriftRidge: options.ContactDriftRidge));
        var drift = inversion.DriftElectrodeScores ?? new double[ElectrodeCount];
        var drive = new double[ElectrodeCount];
        var left = new double[ElectrodeCount];
        var right = new double[ElectrodeCount];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            drive[stimulation] = SubtractDrift(z48.Drive[stimulation], drift[stimulation] + drift[Mod(stimulation + 1)]);
            left[stimulation] = SubtractDrift(z48.Left[stimulation], drift[stimulation]);
            right[stimulation] = SubtractDrift(z48.Right[stimulation], drift[Mod(stimulation + 1)]);
        }

        return new Z48(drive, left, right);
    }

    private static double SubtractDrift(double value, double drift)
    {
        var safeValue = double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
        var safeDrift = double.IsFinite(drift) ? Math.Clamp(drift, 0.0, safeValue) : 0.0;
        return Math.Max(0.0, safeValue - safeDrift);
    }

    private double ComputeZ(double[,] real256, double[,] imaginary256, int stimulation, int relativeMeasurement)
    {
        var baseReal = baseline.Real256[stimulation, relativeMeasurement];
        var baseImag = baseline.Imaginary256[stimulation, relativeMeasurement];
        var diffReal = real256[stimulation, relativeMeasurement] - baseReal;
        var diffImag = imaginary256[stimulation, relativeMeasurement] - baseImag;
        var delta = Math.Sqrt((diffReal * diffReal) + (diffImag * diffImag));
        var baselineMagnitude = Math.Sqrt((baseReal * baseReal) + (baseImag * baseImag));
        var calibratedScale = contactNoiseScale256?[stimulation, relativeMeasurement];
        var physicalToleranceFloor = Math.Max(
            options.AbsoluteNoiseFloor,
            baselineMagnitude * options.RelativeNoiseFloor);
        var sigma = calibratedScale is > 0.0 && double.IsFinite(calibratedScale.Value)
            ? Math.Max(physicalToleranceFloor, calibratedScale.Value)
            : physicalToleranceFloor;
        return delta / sigma;
    }

    private void ApplyEvidenceA(
        Z48 z48,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var singleDriveRows = new bool[ElectrodeCount];
        for (var stim = 0; stim < ElectrodeCount; stim++)
        {
            singleDriveRows[stim] = z48.Drive[stim] >= options.CandidateZThreshold;
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var previousStim = Mod(electrode - 1);
            var currentStim = electrode;
            if (singleDriveRows[previousStim] && singleDriveRows[currentStim])
            {
                var score = Math.Max(z48.Drive[previousStim], z48.Drive[currentStim]);
                AddScore(electrode, score, ElectrodeFaultType.ElectrodeContact, ElectrodeEvidenceKind.EvidenceA, "A adjacent drive rows", scores, faultTypes, evidenceKinds, reasons);
            }

            var sharedScore = Math.Max(z48.Left[electrode], z48.Right[Mod(electrode - 1)]);
            if (sharedScore >= options.CandidateZThreshold)
            {
                AddScore(electrode, sharedScore, ElectrodeFaultType.ElectrodeContact, ElectrodeEvidenceKind.EvidenceA, "A shared 48", scores, faultTypes, evidenceKinds, reasons);
            }
        }

        for (var stim = 0; stim < ElectrodeCount; stim++)
        {
            if (!singleDriveRows[stim])
            {
                continue;
            }

            var leftElectrode = stim;
            var rightElectrode = Mod(stim + 1);
            var leftHasNeighbor = singleDriveRows[Mod(stim - 1)];
            var rightHasNeighbor = singleDriveRows[Mod(stim + 1)];
            if (!leftHasNeighbor && !rightHasNeighbor)
            {
                AddScore(leftElectrode, z48.Drive[stim], ElectrodeFaultType.DrivePairLink, ElectrodeEvidenceKind.EvidenceA, "A single drive row", scores, faultTypes, evidenceKinds, reasons);
                AddScore(rightElectrode, z48.Drive[stim], ElectrodeFaultType.DrivePairLink, ElectrodeEvidenceKind.EvidenceA, "A single drive row", scores, faultTypes, evidenceKinds, reasons);
            }
        }
    }

    private void ApplyEvidenceD(
        EcdCwrEvidenceDResult evidenceD,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        foreach (var window in evidenceD.WindowScores.Where(window => window.Score > 0.0))
        {
            var left = Mod(window.ExpectedReferenceChannel);
            var right = Mod(window.ExpectedReferenceChannel + 1);
            var kind = window.HardFault
                ? ElectrodeEvidenceKind.EvidenceD | ElectrodeEvidenceKind.Saturation
                : ElectrodeEvidenceKind.EvidenceD;
            var faultType = window.HardFault
                ? ElectrodeFaultType.ElectrodeContact
                : ElectrodeFaultType.UncertainStructured;
            var score = window.HardFault
                ? options.SevereZThreshold
                : options.CandidateZThreshold + window.Score;
            var reason = window.HardFault ? "D saturation" : "D topology telemetry";
            AddScore(left, score, faultType, kind, reason, scores, faultTypes, evidenceKinds, reasons);
            AddScore(right, score, faultType, kind, reason, scores, faultTypes, evidenceKinds, reasons);
        }
    }

    private void ApplyPersistentTopologyEvidence(
        EcdCwrEvidenceDResult evidenceD,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var persistentRows = evidenceD.WindowScores
            .GroupBy(window => Mod(window.ExpectedReferenceChannel))
            .Select(group =>
            {
                var repeatedFaults = group
                    .Where(window => window.RejectReason is
                        DemodulatedWindowRejectReason.Top3NotContiguous or
                        DemodulatedWindowRejectReason.ExpectedReferenceNotInTop3 or
                        DemodulatedWindowRejectReason.WeakReference)
                    .ToArray();
                return new
                {
                    Stimulation = group.Key,
                    Total = group.Count(),
                    Faults = repeatedFaults.Length,
                    Score = repeatedFaults.Length == 0 ? 0.0 : repeatedFaults.Max(window => window.Score)
                };
            })
            .Where(row => row.Total >= 2 && row.Faults >= 2 && row.Faults / (double)row.Total >= 2.0 / 3.0)
            .ToArray();

        foreach (var row in persistentRows)
        {
            var score = Math.Max(options.CandidateZThreshold + row.Score, options.YellowThreshold * 2.0);
            var kind = ElectrodeEvidenceKind.EvidenceD | ElectrodeEvidenceKind.PersistentTopology;
            var reason = $"D persistent topology {row.Faults}/{row.Total}";
            AddScore(
                row.Stimulation,
                score,
                ElectrodeFaultType.UncertainStructured,
                kind,
                reason,
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
            AddScore(
                Mod(row.Stimulation + 1),
                score,
                ElectrodeFaultType.UncertainStructured,
                kind,
                reason,
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }
    }

    private EcdCwrFaultLocalizationResult ApplyFaultDictionaryLocalization(
        Z48 z48,
        IReadOnlyList<double> topologyScores,
        SupplementalEvidence supplementalEvidence,
        EcdCwrFaultDictionaryPolicyDefinition dictionaryPolicy,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var localization = new EcdCwrFaultDictionaryLocalizer().Localize(
            new EcdCwrFaultDictionaryInput(
                EvidenceA: BuildEvidenceAResult(z48),
                ReciprocityScores208: supplementalEvidence.ReciprocityScores208,
                ShapeScores208: supplementalEvidence.ShapeScores208,
                TopologyScores16: topologyScores,
                Contact48Weight: 1.0,
                ReciprocityWeight: supplementalEvidence.ReciprocityWeight,
                ShapeWeight: supplementalEvidence.ShapeWeight,
                TopologyWeight: 1.0),
            new EcdCwrFaultDictionaryLocalizerOptions(
                L1Penalty: dictionaryPolicy.L1Penalty,
                GroupPenalty: dictionaryPolicy.GroupPenalty,
                FaultThreshold: options.CandidateZThreshold,
                LinkFaultThreshold: options.CandidateZThreshold,
                JointElectrodeThresholdRatio: 0.5));

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var score = localization.ElectrodeScores[electrode];
            if (score < options.CandidateZThreshold)
            {
                continue;
            }

            var faultType = localization.FaultTypes[electrode] == ElectrodeFaultType.ElectrodeContact
                ? ElectrodeFaultType.ElectrodeContact
                : ElectrodeFaultType.UncertainStructured;
            var evidenceKind = supplementalEvidence.HasCandidateSegments
                ? ResolveElectrodeLocalizationEvidenceKind(
                    z48,
                    topologyScores,
                    supplementalEvidence,
                    electrode)
                : HasElectrodeEvidenceA(z48, electrode)
                    ? ElectrodeEvidenceKind.EvidenceA
                    : ElectrodeEvidenceKind.EvidenceD;
            AddScore(
                electrode,
                score,
                faultType,
                evidenceKind,
                AppendEvidenceLabel(localization.UpgradeGateReasons[electrode], evidenceKind),
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }

        foreach (var fault in localization.PairLinkFaults)
        {
            AddLinkFaultScore(
                fault.Index,
                fault.Score,
                ElectrodeFaultType.DrivePairLink,
                supplementalEvidence.HasCandidateSegments
                    ? ResolvePairLinkLocalizationEvidenceKind(
                        z48,
                        topologyScores,
                        supplementalEvidence,
                        fault.Index)
                    : HasPairEvidenceA(z48, fault.Index)
                        ? ElectrodeEvidenceKind.EvidenceA
                        : ElectrodeEvidenceKind.EvidenceD,
                AppendEvidenceLabel(
                    fault.Reason,
                    supplementalEvidence.HasCandidateSegments
                        ? ResolvePairLinkLocalizationEvidenceKind(
                            z48,
                            topologyScores,
                            supplementalEvidence,
                            fault.Index)
                        : HasPairEvidenceA(z48, fault.Index)
                            ? ElectrodeEvidenceKind.EvidenceA
                            : ElectrodeEvidenceKind.EvidenceD),
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }

        foreach (var fault in localization.MeasurementChannelFaults)
        {
            AddLinkFaultScore(
                fault.Index,
                fault.Score,
                ElectrodeFaultType.AcquisitionChannel,
                supplementalEvidence.HasCandidateSegments
                    ? ResolveMeasurementChannelLocalizationEvidenceKind(
                        z48,
                        topologyScores,
                        supplementalEvidence,
                        fault.Index)
                    : HasPairEvidenceA(z48, fault.Index)
                        ? ElectrodeEvidenceKind.EvidenceA
                        : ElectrodeEvidenceKind.EvidenceD,
                AppendEvidenceLabel(
                    fault.Reason,
                    supplementalEvidence.HasCandidateSegments
                        ? ResolveMeasurementChannelLocalizationEvidenceKind(
                            z48,
                            topologyScores,
                            supplementalEvidence,
                            fault.Index)
                        : HasPairEvidenceA(z48, fault.Index)
                            ? ElectrodeEvidenceKind.EvidenceA
                            : ElectrodeEvidenceKind.EvidenceD),
                scores,
                faultTypes,
                evidenceKinds,
                reasons);
        }

        return localization;
    }

    private static EcdCwrFaultDictionaryTrace? CreateFaultDictionaryTrace(
        EcdCwrFaultDictionaryPolicyDefinition policy,
        EcdCwrFaultLocalizationResult? localization)
    {
        return localization is null
            ? null
            : new EcdCwrFaultDictionaryTrace(
                policy.Version,
                localization.DriveScores,
                localization.MeasureScores,
                localization.PairLinkScores,
                localization.MeasurementChannelScores,
                localization.ResidualRms,
                localization.ObservationCount);
    }

    private EcdCwrMultiFrequencyScoreFusionResult? ApplyEvidenceE(
        double? primaryFrequencyHz,
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame>? peerFrequencyEvidence,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        if (primaryFrequencyHz is not { } primary ||
            !double.IsFinite(primary) ||
            primary <= 0.0 ||
            peerFrequencyEvidence is not { Count: > 0 })
        {
            return null;
        }

        var frames = new List<EcdCwrFrequencyEvidenceFrame>(peerFrequencyEvidence.Count + 1)
        {
            new(primary, scores.ToArray())
        };
        frames.AddRange(peerFrequencyEvidence);

        EcdCwrMultiFrequencyScoreFusionResult fusion;
        try
        {
            fusion = new EcdCwrMultiFrequencyScoreFusion().Fuse(
                frames,
                new EcdCwrMultiFrequencyScoreFusionOptions(
                    ActiveMagnitudeThreshold: options.CandidateZThreshold,
                    MinimumActiveFraction: 0.5,
                    MaxContactLikelihoodBoost: 1.0,
                    MaxFusedScore: Math.Max(options.SevereZThreshold * 2.0, options.RedThreshold * 4.0),
                    PrimaryFrequencyHz: primary));
        }
        catch (ArgumentException)
        {
            return null;
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var fused = fusion.FusedScores[electrode];
            if (!double.IsFinite(fused) || fused <= scores[electrode])
            {
                continue;
            }

            scores[electrode] = fused;
            evidenceKinds[electrode] |= ElectrodeEvidenceKind.EvidenceE;
            if (faultTypes[electrode] == ElectrodeFaultType.None)
            {
                faultTypes[electrode] = ElectrodeFaultType.UncertainStructured;
            }

            reasons[electrode] = reasons[electrode] == "green"
                ? "E multi-frequency"
                : reasons[electrode] + " + E multi-frequency";
        }

        return fusion;
    }

    private void AddLinkFaultScore(
        int adjacentPairIndex,
        double score,
        ElectrodeFaultType faultType,
        ElectrodeEvidenceKind evidenceKind,
        string reason,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        AddScore(adjacentPairIndex, score, faultType, evidenceKind, reason, scores, faultTypes, evidenceKinds, reasons);
        AddScore(Mod(adjacentPairIndex + 1), score, faultType, evidenceKind, reason, scores, faultTypes, evidenceKinds, reasons);
    }

    private bool HasElectrodeEvidenceA(Z48 z48, int electrode)
    {
        return z48.Drive[Mod(electrode - 1)] >= options.CandidateZThreshold ||
            z48.Drive[electrode] >= options.CandidateZThreshold ||
            z48.Left[electrode] >= options.CandidateZThreshold ||
            z48.Right[Mod(electrode - 1)] >= options.CandidateZThreshold;
    }

    private bool HasPairEvidenceA(Z48 z48, int adjacentPairIndex)
    {
        return z48.Drive[adjacentPairIndex] >= options.CandidateZThreshold ||
            z48.Left[adjacentPairIndex] >= options.CandidateZThreshold ||
            z48.Right[adjacentPairIndex] >= options.CandidateZThreshold;
    }

    private ElectrodeEvidenceKind ResolveElectrodeLocalizationEvidenceKind(
        Z48 z48,
        IReadOnlyList<double> topologyScores,
        SupplementalEvidence supplementalEvidence,
        int electrode)
    {
        var kind = ElectrodeEvidenceKind.None;
        if (HasElectrodeEvidenceA(z48, electrode))
        {
            kind |= ElectrodeEvidenceKind.EvidenceA;
        }

        if (topologyScores[electrode] > 0.0 || topologyScores[Mod(electrode - 1)] > 0.0)
        {
            kind |= ElectrodeEvidenceKind.EvidenceD;
        }

        if (HasRetainedEvidenceForElectrode(supplementalEvidence.ReciprocityScores208, electrode))
        {
            kind |= ElectrodeEvidenceKind.EvidenceB;
        }

        if (HasRetainedEvidenceForElectrode(supplementalEvidence.ShapeScores208, electrode))
        {
            kind |= ElectrodeEvidenceKind.EvidenceC;
        }

        return kind;
    }

    private ElectrodeEvidenceKind ResolvePairLinkLocalizationEvidenceKind(
        Z48 z48,
        IReadOnlyList<double> topologyScores,
        SupplementalEvidence supplementalEvidence,
        int stimulation)
    {
        var kind = HasPairEvidenceA(z48, stimulation)
            ? ElectrodeEvidenceKind.EvidenceA
            : ElectrodeEvidenceKind.None;
        if (topologyScores[stimulation] > 0.0)
        {
            kind |= ElectrodeEvidenceKind.EvidenceD;
        }

        if (HasRetainedEvidenceForStimulation(supplementalEvidence.ReciprocityScores208, stimulation))
        {
            kind |= ElectrodeEvidenceKind.EvidenceB;
        }

        if (HasRetainedEvidenceForStimulation(supplementalEvidence.ShapeScores208, stimulation))
        {
            kind |= ElectrodeEvidenceKind.EvidenceC;
        }

        return kind;
    }

    private ElectrodeEvidenceKind ResolveMeasurementChannelLocalizationEvidenceKind(
        Z48 z48,
        IReadOnlyList<double> topologyScores,
        SupplementalEvidence supplementalEvidence,
        int measurementChannel)
    {
        var kind = HasPairEvidenceA(z48, measurementChannel)
            ? ElectrodeEvidenceKind.EvidenceA
            : ElectrodeEvidenceKind.None;
        if (topologyScores[measurementChannel] > 0.0)
        {
            kind |= ElectrodeEvidenceKind.EvidenceD;
        }

        if (HasRetainedEvidenceForMeasurementChannel(
                supplementalEvidence.ReciprocityScores208,
                measurementChannel))
        {
            kind |= ElectrodeEvidenceKind.EvidenceB;
        }

        if (HasRetainedEvidenceForMeasurementChannel(
                supplementalEvidence.ShapeScores208,
                measurementChannel))
        {
            kind |= ElectrodeEvidenceKind.EvidenceC;
        }

        return kind;
    }

    private static bool HasRetainedEvidenceForElectrode(IReadOnlyList<double>? scores, int electrode)
    {
        if (scores is not { Count: ElectrodeContactBaseline.RetainedObservationCount })
        {
            return false;
        }

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var value = scores[(stimulation * DemodulatedFrame.MeasurementsPerStimulation) + relativeChannel - 2];
                if (!double.IsFinite(value) || value <= 0.0)
                {
                    continue;
                }

                var measurement = Mod(stimulation + relativeChannel);
                if (electrode == stimulation ||
                    electrode == Mod(stimulation + 1) ||
                    electrode == measurement ||
                    electrode == Mod(measurement + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasRetainedEvidenceForStimulation(IReadOnlyList<double>? scores, int stimulation)
    {
        if (scores is not { Count: ElectrodeContactBaseline.RetainedObservationCount })
        {
            return false;
        }

        var offset = stimulation * DemodulatedFrame.MeasurementsPerStimulation;
        return Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
            .Any(index => double.IsFinite(scores[offset + index]) && scores[offset + index] > 0.0);
    }

    private static bool HasRetainedEvidenceForMeasurementChannel(
        IReadOnlyList<double>? scores,
        int measurementChannel)
    {
        if (scores is not { Count: ElectrodeContactBaseline.RetainedObservationCount })
        {
            return false;
        }

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                if (Mod(stimulation + relativeChannel) != measurementChannel)
                {
                    continue;
                }

                var value = scores[(stimulation * DemodulatedFrame.MeasurementsPerStimulation) + relativeChannel - 2];
                if (double.IsFinite(value) && value > 0.0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string AppendEvidenceLabel(string reason, ElectrodeEvidenceKind kind)
    {
        return kind == ElectrodeEvidenceKind.None
            ? reason
            : $"{reason} evidence={kind}";
    }

    private static bool IsActionSupportingEvidence(ElectrodeEvidenceKind kind)
    {
        return (kind & (ElectrodeEvidenceKind.EvidenceA |
            ElectrodeEvidenceKind.Saturation |
            ElectrodeEvidenceKind.PersistentTopology |
            ElectrodeEvidenceKind.MultiFaultConsensus)) != 0;
    }

    private static void SuppressCandidateOnlyActions(
        IReadOnlyList<ElectrodeEvidenceKind> candidateEvidenceKinds,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (IsActionSupportingEvidence(candidateEvidenceKinds[electrode]))
            {
                continue;
            }

            scores[electrode] = 0.0;
            faultTypes[electrode] = ElectrodeFaultType.None;
            evidenceKinds[electrode] = ElectrodeEvidenceKind.None;
            reasons[electrode] = "candidate-only soft evidence";
        }
    }

    private double[] CreateTopologyScores(EcdCwrEvidenceDResult evidenceD)
    {
        var scores = new double[ElectrodeCount];
        foreach (var window in evidenceD.WindowScores.Where(window => window.Score > 0.0))
        {
            var normalized = window.HardFault
                ? options.SevereZThreshold
                : options.CandidateZThreshold + window.Score;
            scores[Mod(window.ExpectedReferenceChannel)] = Math.Max(
                scores[Mod(window.ExpectedReferenceChannel)],
                normalized);
        }

        return scores;
    }

    private static EcdCwrEvidenceAResult BuildEvidenceAResult(Z48 z48)
    {
        var points = new List<EcdCwrEvidenceAPoint>(ElectrodeCount * 3);
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            points.Add(new EcdCwrEvidenceAPoint(stimulation, 0, z48.Drive[stimulation], Saturated: false));
            points.Add(new EcdCwrEvidenceAPoint(stimulation, 1, z48.Right[stimulation], Saturated: false));
            points.Add(new EcdCwrEvidenceAPoint(stimulation, 15, z48.Left[stimulation], Saturated: false));
        }

        return new EcdCwrEvidenceAResult(
            z48.Drive,
            z48.Left,
            z48.Right,
            points,
            SaturatedPoints: [],
            Candidates: [],
            HasCandidate: points.Any(point => point.Score > 0.0));
    }

    private void AddScore(
        int electrode,
        double score,
        ElectrodeFaultType faultType,
        ElectrodeEvidenceKind kind,
        string reason,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        if (score <= scores[electrode])
        {
            evidenceKinds[electrode] |= kind;
            return;
        }

        scores[electrode] = score;
        faultTypes[electrode] = PreferSpecificFaultType(faultTypes[electrode], faultType);
        evidenceKinds[electrode] |= kind;
        reasons[electrode] = reason;
    }

    private static ElectrodeFaultType PreferSpecificFaultType(
        ElectrodeFaultType existing,
        ElectrodeFaultType incoming)
    {
        var existingConcrete = existing is ElectrodeFaultType.ElectrodeContact
            or ElectrodeFaultType.DrivePairLink
            or ElectrodeFaultType.AcquisitionChannel
            or ElectrodeFaultType.SystemLevel;
        var incomingUncertain = incoming is ElectrodeFaultType.UncertainStructured
            or ElectrodeFaultType.NoiseCandidate
            or ElectrodeFaultType.None;
        return existingConcrete && incomingUncertain ? existing : incoming;
    }

    private void UpdateAOnlyRedConfirmation(
        int electrode,
        double currentScore,
        ElectrodeEvidenceKind kind,
        ElectrodeFaultType faultType)
    {
        var aOnlySevere = (kind & ElectrodeEvidenceKind.EvidenceA) != 0 &&
            faultType == ElectrodeFaultType.ElectrodeContact &&
            currentScore >= options.SevereZThreshold;
        aOnlyRedConfirmation[electrode] = aOnlySevere
            ? Math.Min(options.AOnlyRedConfirmationScore, aOnlyRedConfirmation[electrode] + 1.0)
            : Math.Max(0.0, aOnlyRedConfirmation[electrode] - options.AOnlyConfirmationFallPerFrame);
    }

    private void UpdateDominantRedConfirmation(
        IReadOnlyList<double> directElectrodeAScores,
        bool allowConfirmation)
    {
        var ranked = directElectrodeAScores
            .Select((score, electrode) => (Score: double.IsFinite(score) ? score : 0.0, Electrode: electrode))
            .OrderByDescending(item => item.Score)
            .ToArray();
        var dominantElectrode = ranked[0].Electrode;
        var dominant = allowConfirmation &&
            ranked[0].Score >= options.CandidateZThreshold &&
            ranked[0].Score - ranked[1].Score >= options.DominantGapThreshold;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (dominant && electrode == dominantElectrode)
            {
                var accumulated = dominantRedConfirmation[electrode] + 1.0;
                dominantRedConfirmation[electrode] = accumulated >= options.DominantRedConfirmationScore
                    ? options.DominantRedConfirmationScore + 1.0
                    : accumulated;
            }
            else
            {
                if (dominantRedConfirmation[electrode] > 0.0 &&
                    dominantRedConfirmation[electrode] < options.DominantRedConfirmationScore)
                {
                    dominantConfirmationHadGap[electrode] = true;
                }

                dominantRedConfirmation[electrode] = Math.Max(
                    0.0,
                    dominantRedConfirmation[electrode] - options.DominantConfirmationFallPerFrame);
                if (dominantRedConfirmation[electrode] <= 0.0)
                {
                    dominantConfirmationHadGap[electrode] = false;
                }
            }
        }
    }

    private void SuppressWidespreadPhysicalFieldEvidence(
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if ((evidenceKinds[electrode] & ElectrodeEvidenceKind.Saturation) != 0)
            {
                continue;
            }

            if ((evidenceKinds[electrode] & ElectrodeEvidenceKind.PersistentTopology) != 0)
            {
                scores[electrode] = Math.Max(options.YellowThreshold * 2.0, options.CandidateZThreshold);
                faultTypes[electrode] = ElectrodeFaultType.UncertainStructured;
                evidenceKinds[electrode] = ElectrodeEvidenceKind.EvidenceD |
                    ElectrodeEvidenceKind.PersistentTopology;
                reasons[electrode] = "D persistent topology candidate";
                continue;
            }

            scores[electrode] = 0.0;
            faultTypes[electrode] = ElectrodeFaultType.None;
            evidenceKinds[electrode] = ElectrodeEvidenceKind.None;
            reasons[electrode] = "widespread physical-field change guard";
        }
    }

    private void ApplyMultiFaultDirectAConsensus(
        EcdCwrMultiFaultDirectAConsensusResult consensus,
        IReadOnlyList<double> directElectrodeAScores,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!consensus.Confirmed[electrode])
            {
                continue;
            }

            scores[electrode] = Math.Max(options.SevereZThreshold, directElectrodeAScores[electrode]);
            faultTypes[electrode] = ElectrodeFaultType.ElectrodeContact;
            evidenceKinds[electrode] = ElectrodeEvidenceKind.EvidenceA |
                ElectrodeEvidenceKind.MultiFaultConsensus;
            reasons[electrode] = "A sparse multi-fault consensus";
        }
    }

    private void ApplyMultiFaultNeighborWarnings(
        EcdCwrMultiFaultDirectAConsensusResult consensus,
        double[] scores,
        ElectrodeFaultType[] faultTypes,
        ElectrodeEvidenceKind[] evidenceKinds,
        string[] reasons)
    {
        var neighbors = new bool[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!consensus.Confirmed[electrode])
            {
                continue;
            }

            neighbors[Mod(electrode - 1)] = true;
            neighbors[Mod(electrode + 1)] = true;
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!neighbors[electrode] ||
                consensus.Confirmed[electrode] ||
                (evidenceKinds[electrode] & ElectrodeEvidenceKind.Saturation) != 0)
            {
                continue;
            }

            scores[electrode] = Math.Max(
                scores[electrode],
                Math.Max(options.YellowThreshold * 2.0, options.CandidateZThreshold));
            faultTypes[electrode] = ElectrodeFaultType.UncertainStructured;
            evidenceKinds[electrode] |= ElectrodeEvidenceKind.MultiFaultNeighbor;
            reasons[electrode] = "A confirmed-set adjacent caution";
        }
    }

    private RetainedFieldChangeSummary AnalyzeRetainedFieldChange(
        double[,] real256,
        double[,] imaginary256)
    {
        var relativeChanges = new List<double>(ElectrodeContactBaseline.RetainedObservationCount);
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relativeMeasurement = 2; relativeMeasurement <= 14; relativeMeasurement++)
            {
                var baseReal = baseline.Real256[stimulation, relativeMeasurement];
                var baseImaginary = baseline.Imaginary256[stimulation, relativeMeasurement];
                var currentReal = real256[stimulation, relativeMeasurement];
                var currentImaginary = imaginary256[stimulation, relativeMeasurement];
                if (!double.IsFinite(baseReal) ||
                    !double.IsFinite(baseImaginary) ||
                    !double.IsFinite(currentReal) ||
                    !double.IsFinite(currentImaginary))
                {
                    continue;
                }

                var baselineMagnitude = Math.Sqrt((baseReal * baseReal) + (baseImaginary * baseImaginary));
                if (baselineMagnitude <= options.AbsoluteNoiseFloor)
                {
                    continue;
                }

                var diffReal = currentReal - baseReal;
                var diffImaginary = currentImaginary - baseImaginary;
                relativeChanges.Add(Math.Sqrt((diffReal * diffReal) + (diffImaginary * diffImaginary)) / baselineMagnitude);
            }
        }

        if (relativeChanges.Count == 0)
        {
            return new RetainedFieldChangeSummary(0.0, 0.0);
        }

        relativeChanges.Sort();
        return new RetainedFieldChangeSummary(
            PercentileSorted(relativeChanges, 0.50),
            PercentileSorted(relativeChanges, 0.90));
    }

    private static double PercentileSorted(IReadOnlyList<double> sorted, double probability)
    {
        if (sorted.Count == 0)
        {
            return 0.0;
        }

        var position = Math.Clamp(probability, 0.0, 1.0) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private double[] BuildDirectElectrodeAScores(Z48 z48)
    {
        var scores = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var previousDrive = z48.Drive[Mod(electrode - 1)];
            var currentDrive = z48.Drive[electrode];
            var adjacentDriveScore = previousDrive >= options.CandidateZThreshold &&
                currentDrive >= options.CandidateZThreshold
                ? Math.Max(previousDrive, currentDrive)
                : 0.0;
            var sharedScore = Math.Max(z48.Left[electrode], z48.Right[Mod(electrode - 1)]);
            scores[electrode] = Math.Max(adjacentDriveScore, sharedScore);
        }

        return scores;
    }

    private void UpdateDirectARecoveryFrames(
        IReadOnlyList<double> directElectrodeAScores,
        IReadOnlyList<ElectrodeEvidenceKind> evidenceKinds)
    {
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!criticalSinceReference[electrode])
            {
                directARecoveryFrames[electrode] = 0;
                continue;
            }

            var saturated = (evidenceKinds[electrode] & ElectrodeEvidenceKind.Saturation) != 0;
            var recoveryThreshold = Math.Max(
                options.CandidateZThreshold,
                criticalDirectAPeak[electrode] * options.RecoveryDirectADropRatio);
            var recoveryFramesRequired = intermittentContactLatched[electrode]
                ? Math.Max(options.RecoveryConfirmationFrames, options.IntermittentRecoveryConfirmationFrames)
                : options.RecoveryConfirmationFrames;
            directARecoveryFrames[electrode] = !saturated &&
                directElectrodeAScores[electrode] <= recoveryThreshold
                ? Math.Min(recoveryFramesRequired, directARecoveryFrames[electrode] + 1)
                : 0;
        }
    }

    private bool IsDominantRedConfirmed(int electrode)
    {
        return dominantRedConfirmation[electrode] >= options.DominantRedConfirmationScore;
    }

    private bool CanUpgradeToRed(
        int electrode,
        double currentScore,
        ElectrodeEvidenceKind kind,
        ElectrodeFaultType faultType)
    {
        if (IsDominantRedConfirmed(electrode))
        {
            return true;
        }

        if ((kind & ElectrodeEvidenceKind.MultiFaultConsensus) != 0)
        {
            return true;
        }

        if (faultType is ElectrodeFaultType.DrivePairLink
            or ElectrodeFaultType.AcquisitionChannel
            or ElectrodeFaultType.UncertainStructured
            or ElectrodeFaultType.NoiseCandidate)
        {
            return false;
        }

        if ((kind & ElectrodeEvidenceKind.Saturation) != 0)
        {
            return true;
        }

        return currentScore >= options.SevereZThreshold &&
            aOnlyRedConfirmation[electrode] >= options.AOnlyRedConfirmationScore;
    }

    private double ComputeGapRedThreshold(IReadOnlyList<double> frameScores)
    {
        var ordered = frameScores
            .Where(double.IsFinite)
            .OrderDescending()
            .ToArray();
        if (ordered.Length < 2 || ordered[0] < options.RedThreshold)
        {
            return options.RedThreshold;
        }

        var maxIndex = Math.Min(options.MaxElectrodeCandidatesBeforeSystemLevel, ordered.Length - 1);
        var bestGap = 0.0;
        var bestIndex = -1;
        for (var index = 0; index < maxIndex; index++)
        {
            var gap = ordered[index] - ordered[index + 1];
            if (gap > bestGap)
            {
                bestGap = gap;
                bestIndex = index;
            }
        }

        if (bestIndex < 0 || ordered[bestIndex] < options.RedThreshold)
        {
            return options.RedThreshold;
        }

        return Math.Max(options.RedThreshold, (ordered[bestIndex] + ordered[bestIndex + 1]) / 2.0);
    }

    private static ElectrodeFaultType NormalizeFaultType(ElectrodeFaultType faultType, bool canRed)
    {
        if (!canRed && faultType == ElectrodeFaultType.ElectrodeContact)
        {
            return ElectrodeFaultType.UncertainStructured;
        }

        return faultType == ElectrodeFaultType.None ? ElectrodeFaultType.ElectrodeContact : faultType;
    }

    private ElectrodeContactDiagnosticResult BuildSystemLevelResult(
        IReadOnlyList<double>? candidateScores = null,
        string reason = "system sentinel",
        EcdCwrSupplementalEvidenceSummary? supplementalEvidence = null,
        EcdCwrRuntimeEvidenceSummary? runtimeEvidence = null,
        EcdCwrFaultDictionaryTrace? faultDictionaryTrace = null,
        EcdCwrContactSubspaceEvidenceSummary? contactSubspaceEvidence = null,
        double[]? directEvidenceAScores = null,
        EcdCwrMultiFaultDirectAConsensusResult? multiFaultConsensus = null)
    {
        var states = Enumerable.Repeat(ElectrodeContactState.SystemLevel, ElectrodeCount).ToArray();
        var faultTypes = Enumerable.Repeat(ElectrodeFaultType.SystemLevel, ElectrodeCount).ToArray();
        var confidence = Enumerable.Repeat(1.0, ElectrodeCount).ToArray();
        var reasons = Enumerable.Repeat(reason, ElectrodeCount).ToArray();
        var weights = Enumerable.Repeat(0.0, ElectrodeContactBaseline.RetainedObservationCount).ToArray();
        var diagnosticScores = candidateScores?.Count == ElectrodeCount
            ? candidateScores.Select(score => double.IsFinite(score) ? Math.Max(0.0, score) : 0.0).ToArray()
            : Enumerable.Repeat(options.SevereZThreshold, ElectrodeCount).ToArray();
        var evidenceKinds = Enumerable.Repeat(ElectrodeEvidenceKind.SystemSentinel, ElectrodeCount).ToArray();
        return new ElectrodeContactDiagnosticResult(
            states,
            faultTypes,
            ewmaScores.ToArray(),
            confidence,
            reasons,
            weights,
            0.0,
            options.WeightPolicyVersion,
            "系统级异常：跳过逐电极判决",
            SystemLevel: true,
            ReferenceInvalidated: false,
            DirectEvidenceAScores: directEvidenceAScores,
            CandidateScores: diagnosticScores,
            CandidateFaultTypes: faultTypes.ToArray(),
            CandidateEvidenceKinds: evidenceKinds,
            CandidateReasons: reasons.ToArray(),
            SupplementalEvidence: supplementalEvidence,
            RuntimeEvidence: runtimeEvidence,
            FaultDictionaryTrace: faultDictionaryTrace,
            ContactSubspaceEvidence: contactSubspaceEvidence ??
                EcdCwrContactSubspaceEvidenceSummary.NotApplicable("system-level evidence"),
            MultiFaultConsensus: multiFaultConsensus);
    }

    private ElectrodeContactDiagnosticResult BuildLocalizedSparseLimitSystemLevelResult(
        IReadOnlyList<double> candidateScores,
        IReadOnlyList<ElectrodeFaultType> candidateFaultTypes,
        IReadOnlyList<ElectrodeEvidenceKind> candidateEvidenceKinds,
        IReadOnlyList<string> candidateReasons,
        double[] directEvidenceAScores,
        EcdCwrMultiFaultDirectAConsensusResult multiFaultConsensus,
        EcdCwrSupplementalEvidenceSummary supplementalEvidence,
        EcdCwrRuntimeEvidenceSummary runtimeEvidence,
        EcdCwrFaultDictionaryTrace? faultDictionaryTrace,
        string reason)
    {
        var states = Enumerable.Repeat(ElectrodeContactState.Green, ElectrodeCount).ToArray();
        var faultTypes = Enumerable.Repeat(ElectrodeFaultType.None, ElectrodeCount).ToArray();
        var scores = new double[ElectrodeCount];
        var confidence = new double[ElectrodeCount];
        var reasons = Enumerable.Repeat("system-level healthy remainder", ElectrodeCount).ToArray();
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (!multiFaultConsensus.Confirmed[electrode] && !multiFaultConsensus.Candidates[electrode])
            {
                continue;
            }

            criticalSinceReference[electrode] = true;
            criticalDirectAPeak[electrode] = Math.Max(
                criticalDirectAPeak[electrode],
                directEvidenceAScores[electrode]);
            if (multiFaultConsensus.Confirmed[electrode])
            {
                states[electrode] = ElectrodeContactState.Red;
                faultTypes[electrode] = ElectrodeFaultType.ElectrodeContact;
                scores[electrode] = Math.Max(options.SevereZThreshold, directEvidenceAScores[electrode]);
                confidence[electrode] = 1.0;
                reasons[electrode] = "A sparse-limit confirmed under system alarm";
            }
            else if (multiFaultConsensus.Candidates[electrode])
            {
                states[electrode] = ElectrodeContactState.Yellow;
                faultTypes[electrode] = ElectrodeFaultType.UncertainStructured;
                scores[electrode] = Math.Max(options.YellowThreshold, directEvidenceAScores[electrode]);
                confidence[electrode] = 0.5;
                reasons[electrode] = "A sparse-limit candidate under system alarm";
            }
        }

        var candidateCount = multiFaultConsensus.Candidates.Count(selected => selected);
        var confirmedCount = multiFaultConsensus.Confirmed.Count(selected => selected);
        var weights = Enumerable.Repeat(0.0, ElectrodeContactBaseline.RetainedObservationCount).ToArray();
        return new ElectrodeContactDiagnosticResult(
            states,
            faultTypes,
            scores,
            confidence,
            reasons,
            weights,
            0.0,
            CreateWeightPolicyVersion(),
            $"系统级最高警报：{candidateCount}/{ElectrodeCount} 电极严重异常，已确认 {confirmedCount}/{candidateCount}；停止可信重构",
            SystemLevel: true,
            ReferenceInvalidated: false,
            DirectEvidenceAScores: directEvidenceAScores,
            CandidateScores: candidateScores.ToArray(),
            CandidateFaultTypes: candidateFaultTypes.ToArray(),
            CandidateEvidenceKinds: candidateEvidenceKinds.ToArray(),
            CandidateReasons: candidateReasons.ToArray(),
            SupplementalEvidence: supplementalEvidence,
            RuntimeEvidence: runtimeEvidence,
            FaultDictionaryTrace: faultDictionaryTrace,
            ContactSubspaceEvidence: EcdCwrContactSubspaceEvidenceSummary.NotApplicable(reason),
            MultiFaultConsensus: multiFaultConsensus);
    }

    private double[] BuildMeasurementWeights(
        IReadOnlyList<ElectrodeContactState> states,
        IReadOnlyList<double> scores,
        IReadOnlyList<ElectrodeEvidenceKind> evidenceKinds,
        IReadOnlyList<ElectrodeFaultType> faultTypes)
    {
        if (options.UseContinuousMeasurementWeights)
        {
            return new EcdCwrContaminationAwareWeightMapper().Map(
                scores,
                evidenceKinds,
                faultTypes,
                new EcdCwrContinuousWeightMapperOptions(
                    options.ContinuousWeightQ0,
                    options.ContinuousWeightPower,
                    options.ContinuousMinimumWeight));
        }

        return BuildDiscreteMeasurementWeights(states);
    }

    private double[] BuildDiscreteMeasurementWeights(IReadOnlyList<ElectrodeContactState> states)
    {
        var weights = new double[ElectrodeContactBaseline.RetainedObservationCount];
        var offset = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var relative = 2; relative <= 14; relative++)
            {
                var measurementPair = Mod(stimulation + relative);
                var involved = new[]
                {
                    stimulation,
                    Mod(stimulation + 1),
                    measurementPair,
                    Mod(measurementPair + 1)
                };
                weights[offset++] = involved
                    .Select(electrode => WeightForState(states[electrode]))
                    .Min();
            }
        }

        return weights;
    }

    private string CreateWeightPolicyVersion()
    {
        if (!options.UseContinuousMeasurementWeights)
        {
            return options.WeightPolicyVersion;
        }

        return EcdCwrContaminationAwareWeightMapper.CreatePolicyVersion(
            new EcdCwrContinuousWeightMapperOptions(
                options.ContinuousWeightQ0,
                options.ContinuousWeightPower,
                options.ContinuousMinimumWeight));
    }

    private double WeightForState(ElectrodeContactState state)
    {
        return state switch
        {
            ElectrodeContactState.Red or ElectrodeContactState.DarkRed or ElectrodeContactState.SystemLevel => 0.0,
            ElectrodeContactState.Yellow => Math.Clamp(options.YellowMeasurementWeight, 0.0, 1.0),
            _ => 1.0
        };
    }

    private static double ComputeImageQuality(
        IReadOnlyList<ElectrodeContactState> states,
        IReadOnlyList<ElectrodeFaultType> faultTypes,
        IReadOnlyList<double> weights)
    {
        return new EcdCwrImageQualityEstimator().Estimate(new EcdCwrImageQualityInput(states, weights, faultTypes));
    }

    private static string CreateSummary(
        IReadOnlyList<ElectrodeContactState> states,
        IReadOnlyList<ElectrodeFaultType> faultTypes,
        double imageQuality,
        bool physicalFieldGuardApplied)
    {
        var red = states
            .Select((state, index) => (state, index))
            .Where(item => item.state is ElectrodeContactState.Red or ElectrodeContactState.DarkRed)
            .Select(item => (item.index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var yellow = states
            .Select((state, index) => (state, index))
            .Where(item => item.state == ElectrodeContactState.Yellow)
            .Select(item => (item.index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var uncertain = faultTypes.Count(type => type == ElectrodeFaultType.UncertainStructured);
        var guard = physicalFieldGuardApplied ? " physical-field-guard=on" : string.Empty;
        return $"接触诊断 Q={imageQuality:F2} red=[{string.Join(",", red)}] yellow=[{string.Join(",", yellow)}] uncertain={uncertain}{guard}";
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).Order().ToArray();
        if (finite.Length == 0)
        {
            return 0.0;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 0
            ? (finite[middle - 1] + finite[middle]) / 2.0
            : finite[middle];
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }

    private static int RingDistance(int left, int right)
    {
        var direct = Math.Abs(Mod(left) - Mod(right));
        return Math.Min(direct, ElectrodeCount - direct);
    }

    private sealed record Z48(double[] Drive, double[] Left, double[] Right);

    private sealed record RetainedFieldChangeSummary(double MedianRelative, double P90Relative);

    private sealed record SupplementalEvidence(
        double[]? ReciprocityScores208,
        double[]? ShapeScores208,
        double ReciprocityWeight,
        double ShapeWeight,
        EcdCwrSupplementalEvidenceSummary Summary)
    {
        public bool HasCandidateSegments => ReciprocityScores208 is not null || ShapeScores208 is not null;

        public static SupplementalEvidence Unavailable(string reason)
        {
            return new SupplementalEvidence(
                null,
                null,
                0.0,
                0.0,
                new EcdCwrSupplementalEvidenceSummary(
                    EvidenceBAvailable: false,
                    EvidenceCAvailable: false,
                    ReciprocityDynamicTooFast: false,
                    ReciprocityViolationCount: 0,
                    ReciprocityMaxWhitenedScore: 0.0,
                    ShapeMaxScore: 0.0,
                    ReciprocityStatus: reason,
                    ShapeStatus: reason));
        }
    }
}
