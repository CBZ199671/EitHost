using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrStartupDegradedReferenceOptions(
    int MinimumFrameCount = 100,
    int MaximumFrameCount = 300,
    int MaximumLocalizedFaultCount = 7,
    double MinimumEffectiveWeight = 0.05,
    double MaximumImageQualityCap = 0.55);

public sealed record EcdCwrStartupDegradedReference(
    EcdCwrRobustReference RobustReference,
    double[] MeasurementWeight208,
    bool[] FaultSet,
    int[] FaultElectrodes,
    double ImageQualityCap,
    string WeightPolicyVersion);

public sealed record EcdCwrStartupDegradedReferenceUpdate(
    bool Eligible,
    bool Locked,
    int UsableFrameCount,
    int[] FaultElectrodes,
    EcdCwrStartupDegradedReference? Reference,
    string Status,
    int AggregateFrameEquivalentCount = 0);

public sealed class EcdCwrStartupDegradedReferenceAccumulator
{
    public const string PolicyVersion = "ecd-cwr-startup-degraded-reference-v1";

    private readonly EcdCwrStartupDegradedReferenceOptions options;
    private readonly List<CandidateObservation> candidateObservations = [];
    private bool[]? trackedFaultSet;

    public EcdCwrStartupDegradedReferenceAccumulator(
        EcdCwrStartupDegradedReferenceOptions? options = null)
    {
        this.options = options ?? new EcdCwrStartupDegradedReferenceOptions();
        ValidateOptions(this.options);
    }

    public EcdCwrStartupDegradedReferenceUpdate Update(
        IReadOnlyList<DemodulatedFrame> frames,
        ElectrodeContactDiagnosticResult? diagnostic,
        DemodulatedObservationAggregate? diagnosticAggregate = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (!TryGetEligibleFaultSet(diagnostic, out var faultSet, out var reason))
        {
            Reset();
            return new EcdCwrStartupDegradedReferenceUpdate(
                Eligible: false,
                Locked: false,
                UsableFrameCount: 0,
                FaultElectrodes: [],
                Reference: null,
                Status: reason);
        }

        if (trackedFaultSet is null || !trackedFaultSet.SequenceEqual(faultSet))
        {
            candidateObservations.Clear();
            trackedFaultSet = faultSet.ToArray();
        }

        var finiteFrames = frames
            .Where(EcdCwrRobustReferenceBuilder.IsFiniteDiagnosticFrame)
            .ToArray();
        candidateObservations.AddRange(finiteFrames.Select(frame => new CandidateObservation(
            new EcdCwrRobustReferenceObservation(
                frame.FlattenAmplitudesRowMajor(),
                frame.FlattenFullRealRowMajor(),
                frame.FlattenFullImaginaryRowMajor()),
            AggregateFallback: false)));

        if (TryCreateAggregateObservation(diagnosticAggregate, out var aggregateObservation))
        {
            var missingFrameSupport = Math.Max(
                0,
                Math.Min(diagnosticAggregate!.ContributingFrameCount, options.MaximumFrameCount) - finiteFrames.Length);
            for (var index = 0; index < missingFrameSupport; index++)
            {
                candidateObservations.Add(new CandidateObservation(
                    aggregateObservation,
                    AggregateFallback: true));
            }
        }

        if (candidateObservations.Count > options.MaximumFrameCount)
        {
            candidateObservations.RemoveRange(
                0,
                candidateObservations.Count - options.MaximumFrameCount);
        }

        var usableFrameCount = candidateObservations.Count;
        var aggregateFrameEquivalentCount = candidateObservations.Count(candidate => candidate.AggregateFallback);
        var faultElectrodes = ToElectrodeNumbers(faultSet);
        if (usableFrameCount < options.MinimumFrameCount)
        {
            return new EcdCwrStartupDegradedReferenceUpdate(
                Eligible: true,
                Locked: false,
                UsableFrameCount: usableFrameCount,
                FaultElectrodes: faultElectrodes,
                Reference: null,
                Status: $"degraded-reference-warmup faults={string.Join(',', faultElectrodes)} frames={usableFrameCount}/{options.MinimumFrameCount} aggregate-equivalent={aggregateFrameEquivalentCount}",
                AggregateFrameEquivalentCount: aggregateFrameEquivalentCount);
        }

        var measurementWeights = diagnostic!.MeasurementWeight208.ToArray();
        try
        {
            var robustReference = new EcdCwrRobustReferenceBuilder().CreateFromObservations(
                candidateObservations.Select(candidate => candidate.Observation).ToArray(),
                new EcdCwrRobustReferenceOptions(MinimumFrameCount: options.MinimumFrameCount),
                measurementWeights);
            var effectiveCount = measurementWeights.Count(weight => weight >= options.MinimumEffectiveWeight);
            var imageQualityCap = Math.Min(
                options.MaximumImageQualityCap,
                0.65 * effectiveCount / DemodulatedFrame.FlattenedMeasurementCount);
            var policy = $"{PolicyVersion}:faults={string.Join(',', faultElectrodes)}:{diagnostic.WeightPolicyVersion}";
            var reference = new EcdCwrStartupDegradedReference(
                robustReference,
                measurementWeights,
                faultSet.ToArray(),
                faultElectrodes,
                imageQualityCap,
                policy);
            return new EcdCwrStartupDegradedReferenceUpdate(
                Eligible: true,
                Locked: true,
                UsableFrameCount: usableFrameCount,
                FaultElectrodes: faultElectrodes,
                Reference: reference,
                Status: $"degraded-reference-locked faults={string.Join(',', faultElectrodes)} frames={robustReference.FrameCount} effective={effectiveCount}/208 aggregate-equivalent={aggregateFrameEquivalentCount}",
                AggregateFrameEquivalentCount: aggregateFrameEquivalentCount);
        }
        catch (InvalidOperationException ex)
        {
            return new EcdCwrStartupDegradedReferenceUpdate(
                Eligible: true,
                Locked: false,
                UsableFrameCount: usableFrameCount,
                FaultElectrodes: faultElectrodes,
                Reference: null,
                Status: $"degraded-reference-filtering faults={string.Join(',', faultElectrodes)} frames={usableFrameCount} aggregate-equivalent={aggregateFrameEquivalentCount}: {ex.Message}",
                AggregateFrameEquivalentCount: aggregateFrameEquivalentCount);
        }
    }

    public bool IsCompatible(ElectrodeContactDiagnosticResult? diagnostic)
    {
        return trackedFaultSet is not null &&
            TryGetEligibleFaultSet(diagnostic, out var faultSet, out _) &&
            trackedFaultSet.SequenceEqual(faultSet);
    }

    public void Reset()
    {
        candidateObservations.Clear();
        trackedFaultSet = null;
    }

    private static bool TryCreateAggregateObservation(
        DemodulatedObservationAggregate? aggregate,
        out EcdCwrRobustReferenceObservation observation)
    {
        observation = new EcdCwrRobustReferenceObservation([], [], []);
        if (aggregate is null ||
            aggregate.ContributingFrameCount <= 0 ||
            aggregate.FiniteMeasurementCount != DemodulatedFrame.FlattenedMeasurementCount ||
            aggregate.FiniteFullMeasurementCount != DemodulatedFrame.FlattenedFullMeasurementCount)
        {
            return false;
        }

        var real208 = aggregate.FlattenRealRowMajor();
        var imaginary208 = aggregate.FlattenImaginaryRowMajor();
        var candidate = new EcdCwrRobustReferenceObservation(
            aggregate.FlattenAmplitudesRowMajor(),
            aggregate.FlattenFullRealRowMajor(),
            aggregate.FlattenFullImaginaryRowMajor());
        if (real208.Any(value => !double.IsFinite(value)) ||
            imaginary208.Any(value => !double.IsFinite(value)) ||
            !EcdCwrRobustReferenceBuilder.IsFiniteObservation(candidate))
        {
            return false;
        }

        observation = candidate;
        return true;
    }

    private bool TryGetEligibleFaultSet(
        ElectrodeContactDiagnosticResult? diagnostic,
        out bool[] faultSet,
        out string reason)
    {
        faultSet = [];
        if (diagnostic is null || !diagnostic.PreReferenceOnly)
        {
            reason = "degraded-reference-ineligible: startup diagnosis unavailable";
            return false;
        }

        if (diagnostic.SystemLevel ||
            diagnostic.States.Any(state => state is ElectrodeContactState.DarkRed or ElectrodeContactState.SystemLevel))
        {
            reason = "degraded-reference-ineligible: hard/system fault";
            return false;
        }

        var consensus = diagnostic.PreReferenceConsensus;
        if (consensus is null || consensus.Candidates.Length != ElectrodeContactBaseline.ElectrodeCount ||
            consensus.Confirmed.Length != ElectrodeContactBaseline.ElectrodeCount ||
            consensus.SafetyMask is { Length: not ElectrodeContactBaseline.ElectrodeCount })
        {
            reason = "degraded-reference-ineligible: safety mask unavailable";
            return false;
        }

        var safetyMask = consensus.SafetyMask ?? consensus.Confirmed;
        var maskedCount = safetyMask.Count(selected => selected);
        if (maskedCount < 1 || maskedCount > options.MaximumLocalizedFaultCount ||
            !consensus.Candidates.SequenceEqual(safetyMask))
        {
            reason = $"degraded-reference-ineligible: safety-mask={maskedCount} candidates-exact={consensus.Candidates.SequenceEqual(safetyMask)}";
            return false;
        }

        if (diagnostic.MeasurementWeight208.Length != DemodulatedFrame.FlattenedMeasurementCount ||
            diagnostic.MeasurementWeight208.Any(weight => !double.IsFinite(weight) || weight < 0.0 || weight > 1.0) ||
            !diagnostic.MeasurementWeight208.Any(weight => weight < options.MinimumEffectiveWeight) ||
            !diagnostic.MeasurementWeight208.Any(weight => weight >= options.MinimumEffectiveWeight))
        {
            reason = "degraded-reference-ineligible: confirmed fault mask unavailable";
            return false;
        }

        faultSet = safetyMask.ToArray();
        reason = "degraded-reference-eligible";
        return true;
    }

    private static int[] ToElectrodeNumbers(IReadOnlyList<bool> faultSet)
    {
        return faultSet
            .Select((selected, electrode) => (selected, electrode))
            .Where(item => item.selected)
            .Select(item => item.electrode + 1)
            .ToArray();
    }

    private static void ValidateOptions(EcdCwrStartupDegradedReferenceOptions options)
    {
        if (options.MinimumFrameCount <= 0 ||
            options.MaximumFrameCount < options.MinimumFrameCount ||
            options.MaximumLocalizedFaultCount is < 1 or >= ElectrodeContactBaseline.ElectrodeCount ||
            !double.IsFinite(options.MinimumEffectiveWeight) || options.MinimumEffectiveWeight <= 0.0 || options.MinimumEffectiveWeight > 1.0 ||
            !double.IsFinite(options.MaximumImageQualityCap) || options.MaximumImageQualityCap <= 0.0 || options.MaximumImageQualityCap > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private sealed record CandidateObservation(
        EcdCwrRobustReferenceObservation Observation,
        bool AggregateFallback);
}
