namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrMultiFaultDirectAConsensusOptions(
    double SevereThreshold = 15.0,
    int MinimumCandidateCount = 1,
    int MaximumCandidateCount = 8,
    double BackgroundGap = 4.0,
    double BackgroundRatio = 1.5,
    double MinimumTopologySupportFraction = 0.5,
    double ConfirmationScore = 2.0,
    double ReleaseFallPerUpdate = 0.5);

public sealed record EcdCwrMultiFaultDirectAConsensusResult(
    bool[] Candidates,
    bool[] Confirmed,
    double[] ConfirmationLevels,
    double BackgroundMaximum,
    double WeakestCandidateScore,
    bool SystemLevelTriggered,
    string Status,
    int TopologySupportedCandidateCount = 0,
    double TopologySupportFraction = 0.0);

public sealed class EcdCwrMultiFaultDirectAConsensusTracker
{
    private const int ElectrodeCount = ElectrodeContactBaseline.ElectrodeCount;

    private readonly EcdCwrMultiFaultDirectAConsensusOptions options;
    private readonly double[] confirmationLevels = new double[ElectrodeCount];
    private readonly bool[] confirmed = new bool[ElectrodeCount];

    public EcdCwrMultiFaultDirectAConsensusTracker(
        EcdCwrMultiFaultDirectAConsensusOptions? options = null)
    {
        this.options = options ?? new EcdCwrMultiFaultDirectAConsensusOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.SevereThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.MinimumCandidateCount);
        if (this.options.MaximumCandidateCount < this.options.MinimumCandidateCount ||
            this.options.MaximumCandidateCount > ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Multi-fault maximum candidate count must be within the electrode count and not below the minimum.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(this.options.BackgroundGap);
        if (!double.IsFinite(this.options.BackgroundRatio) || this.options.BackgroundRatio < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Background ratio must be finite and at least 1.");
        }

        if (!double.IsFinite(this.options.MinimumTopologySupportFraction) ||
            this.options.MinimumTopologySupportFraction is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Minimum topology support fraction must be finite and within 0..1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.ConfirmationScore);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.ReleaseFallPerUpdate);
    }

    public EcdCwrMultiFaultDirectAConsensusResult Update(
        IReadOnlyList<double> directEvidenceAScores,
        IReadOnlyList<bool> persistentTopologySupport)
    {
        ArgumentNullException.ThrowIfNull(directEvidenceAScores);
        ArgumentNullException.ThrowIfNull(persistentTopologySupport);
        if (directEvidenceAScores.Count != ElectrodeCount || persistentTopologySupport.Count != ElectrodeCount)
        {
            throw new ArgumentException("Multi-fault consensus requires 16 direct-A scores and topology flags.");
        }

        var safeScores = directEvidenceAScores
            .Select(score => double.IsFinite(score) ? Math.Max(0.0, score) : 0.0)
            .ToArray();
        var severeCandidates = Enumerable.Range(0, ElectrodeCount)
            .Where(electrode => safeScores[electrode] >= options.SevereThreshold)
            .ToArray();
        var severeSet = severeCandidates.ToHashSet();
        var backgroundMaximum = Enumerable.Range(0, ElectrodeCount)
            .Where(electrode => !severeSet.Contains(electrode))
            .Select(electrode => safeScores[electrode])
            .DefaultIfEmpty(0.0)
            .Max();
        var weakestCandidate = severeCandidates.Length == 0
            ? 0.0
            : severeCandidates.Min(electrode => safeScores[electrode]);
        var topologySupportedCandidateCount = severeCandidates.Count(electrode =>
            persistentTopologySupport[electrode]);
        var topologySupportFraction = severeCandidates.Length == 0
            ? 0.0
            : topologySupportedCandidateCount / (double)severeCandidates.Length;
        var topologyCorroborated = topologySupportedCandidateCount > 0 &&
            topologySupportFraction >= options.MinimumTopologySupportFraction;
        var countSupported = severeCandidates.Length >= options.MinimumCandidateCount &&
            severeCandidates.Length <= options.MaximumCandidateCount;
        var separated = countSupported &&
            weakestCandidate >= backgroundMaximum + options.BackgroundGap &&
            weakestCandidate >= backgroundMaximum * options.BackgroundRatio;
        var candidates = new bool[ElectrodeCount];
        if (separated && topologyCorroborated)
        {
            foreach (var electrode in severeCandidates)
            {
                candidates[electrode] = true;
            }
        }

        var confirmationCeiling = options.ConfirmationScore + 1.0;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            confirmationLevels[electrode] = candidates[electrode]
                ? Math.Min(confirmationCeiling, confirmationLevels[electrode] + 1.0)
                : Math.Max(0.0, confirmationLevels[electrode] - options.ReleaseFallPerUpdate);
            if (confirmationLevels[electrode] >= options.ConfirmationScore)
            {
                confirmed[electrode] = true;
            }
            else if (confirmationLevels[electrode] <= 0.0)
            {
                confirmed[electrode] = false;
            }
        }

        var systemLevelTriggered = severeCandidates.Length >= options.MaximumCandidateCount;
        var exceedsLocalizationLimit = severeCandidates.Length > options.MaximumCandidateCount;
        var reachesLocalizationLimit = severeCandidates.Length == options.MaximumCandidateCount;
        string status;
        if (exceedsLocalizationLimit)
        {
            status = $"system-level candidate-count={severeCandidates.Length} exceeds {options.MaximumCandidateCount}";
        }
        else if (reachesLocalizationLimit)
        {
            status = !separated
                ? $"system-level sparse-limit separation-insufficient weakest={weakestCandidate:G3} background={backgroundMaximum:G3}"
                : !topologyCorroborated
                    ? $"system-level sparse-limit topology-insufficient supported={topologySupportedCandidateCount}/{severeCandidates.Length}"
                    : $"system-level sparse-limit tracking candidates=[{FormatElectrodes(candidates)}] confirmed=[{FormatElectrodes(confirmed)}] background={backgroundMaximum:G3} topology={topologySupportedCandidateCount}/{severeCandidates.Length}";
        }
        else if (!countSupported)
        {
            status = $"candidate-count={severeCandidates.Length} outside {options.MinimumCandidateCount}..{options.MaximumCandidateCount}";
        }
        else
        {
            status = !separated
                ? $"separation-insufficient weakest={weakestCandidate:G3} background={backgroundMaximum:G3}"
                : !topologyCorroborated
                    ? $"topology-insufficient supported={topologySupportedCandidateCount}/{severeCandidates.Length}"
                    : $"tracking candidates=[{FormatElectrodes(candidates)}] confirmed=[{FormatElectrodes(confirmed)}] background={backgroundMaximum:G3} topology={topologySupportedCandidateCount}/{severeCandidates.Length}";
        }
        return new EcdCwrMultiFaultDirectAConsensusResult(
            candidates,
            confirmed.ToArray(),
            confirmationLevels.ToArray(),
            backgroundMaximum,
            weakestCandidate,
            systemLevelTriggered,
            status,
            topologySupportedCandidateCount,
            topologySupportFraction);
    }

    public void ResetElectrode(int electrode)
    {
        if (electrode < 0 || electrode >= ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(electrode));
        }

        confirmationLevels[electrode] = 0.0;
        confirmed[electrode] = false;
    }

    private static string FormatElectrodes(IReadOnlyList<bool> selected)
    {
        return string.Join(",", selected
            .Select((value, electrode) => (value, electrode))
            .Where(item => item.value)
            .Select(item => (item.electrode + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
