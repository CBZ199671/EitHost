using System.Globalization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrReferenceOperatingPoint(
    double ActualFrequencyHz,
    double DacGain,
    int DacChannel,
    int PhaseDegrees,
    int PgaGain,
    int SampleRateHz,
    string AcquisitionRange,
    string ExcitationMode,
    double ChannelCycles,
    int ScanTimes,
    double DiscardLeadingCycles,
    double DiscardTrailingCycles,
    int FramesPerBlock,
    int MinimumAcceptedFrames,
    string PairingMapSummary,
    string DifferenceOrientation,
    bool UseFrequencyDivisionLockIn,
    IReadOnlyList<double> InterferenceFrequencyHz)
{
    public string Fingerprint
    {
        get
        {
            var interference = string.Join(
                ',',
                InterferenceFrequencyHz.Select(frequency =>
                    frequency.ToString("G17", CultureInfo.InvariantCulture)));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"f={ActualFrequencyHz:G17};gain={DacGain:G17};dac={DacChannel};phase={PhaseDegrees};pga={PgaGain};" +
                $"sr={SampleRateHz};range={Uri.EscapeDataString(AcquisitionRange)};" +
                $"excitation={Uri.EscapeDataString(ExcitationMode)}:{ChannelCycles:G17}:{ScanTimes};" +
                $"trim={DiscardLeadingCycles:G17}/{DiscardTrailingCycles:G17};" +
                $"block={FramesPerBlock}/{MinimumAcceptedFrames};map={Uri.EscapeDataString(PairingMapSummary)};" +
                $"difference={Uri.EscapeDataString(DifferenceOrientation)};fdm={UseFrequencyDivisionLockIn};if={interference}");
        }
    }
}

public sealed record EcdCwrReferenceCandidate(
    long Sequence,
    string SourceId,
    DateTimeOffset CapturedAt,
    int BlockNumber,
    int FrameNumber,
    long StartSampleIndex,
    long EndSampleIndex,
    string Fingerprint,
    int GapBeforeSamples,
    int SaturationCount,
    string ContactEvidence,
    EcdCwrRobustReferenceObservation Observation);

public sealed record EcdCwrReferenceWindow(
    string WindowId,
    string Fingerprint,
    IReadOnlyList<string> SourceCandidateIds,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    DateTimeOffset EffectiveReferenceAt,
    int FrameCount,
    double DriftPerMinute,
    int GapCount,
    int SaturationCount,
    string ContactEvidence,
    bool UsesPersistedCandidates);

/// <summary>
/// Bounded in-memory candidate history with optional persisted candidates supplied
/// by the caller when selectable windows are built or resolved.
/// </summary>
public sealed class EcdCwrReferenceCandidateHistory
{
    private readonly int memoryCapacity;
    private readonly List<EcdCwrReferenceCandidate> memory = [];

    public EcdCwrReferenceCandidateHistory(int memoryCapacity = 500)
    {
        if (memoryCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryCapacity));
        }

        this.memoryCapacity = memoryCapacity;
    }

    public int MemoryCount => memory.Count;

    public int LatestContiguousCount
    {
        get
        {
            if (memory.Count == 0)
            {
                return 0;
            }

            var count = 1;
            for (var index = memory.Count - 1; index > 0; index--)
            {
                var current = memory[index];
                var previous = memory[index - 1];
                if (current.Sequence != previous.Sequence + 1 ||
                    current.GapBeforeSamples > 0 ||
                    !string.Equals(current.Fingerprint, previous.Fingerprint, StringComparison.Ordinal))
                {
                    break;
                }

                count++;
            }

            return count;
        }
    }

    public IReadOnlyList<EcdCwrReferenceCandidate> MemoryCandidates => memory.ToArray();

    public void Add(EcdCwrReferenceCandidate candidate)
    {
        ValidateCandidate(candidate);
        var duplicateIndex = memory.FindIndex(item =>
            string.Equals(item.SourceId, candidate.SourceId, StringComparison.Ordinal));
        if (duplicateIndex >= 0)
        {
            memory[duplicateIndex] = candidate;
        }
        else
        {
            memory.Add(candidate);
        }

        memory.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        if (memory.Count > memoryCapacity)
        {
            memory.RemoveRange(0, memory.Count - memoryCapacity);
        }
    }

    public void Clear()
    {
        memory.Clear();
    }

    public IReadOnlyList<EcdCwrReferenceWindow> BuildWindows(
        int requiredFrameCount,
        IReadOnlyList<EcdCwrReferenceCandidate>? persistedCandidates = null,
        int maximumWindowCount = 40)
    {
        if (requiredFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFrameCount));
        }

        if (maximumWindowCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWindowCount));
        }

        var merged = Merge(persistedCandidates);
        var memoryIds = memory.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
        var windows = new List<EcdCwrReferenceWindow>();
        var segmentStart = 0;
        for (var index = 1; index <= merged.Length; index++)
        {
            var boundary = index == merged.Length ||
                IsBoundary(merged[index - 1], merged[index]);
            if (!boundary)
            {
                continue;
            }

            var segmentLength = index - segmentStart;
            for (var offset = 0; offset + requiredFrameCount <= segmentLength; offset++)
            {
                var selected = merged
                    .Skip(segmentStart + offset)
                    .Take(requiredFrameCount)
                    .ToArray();
                windows.Add(CreateWindow(selected, memoryIds));
            }

            segmentStart = index;
        }

        if (windows.Count <= maximumWindowCount)
        {
            return windows;
        }

        if (maximumWindowCount == 1)
        {
            return [windows[^1]];
        }

        // Keep the full time span selectable instead of silently discarding old
        // persisted windows. The UI receives evenly spaced representatives plus
        // the exact first/last window.
        var sampled = new List<EcdCwrReferenceWindow>(maximumWindowCount);
        for (var index = 0; index < maximumWindowCount; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (windows.Count - 1.0) / (maximumWindowCount - 1.0),
                MidpointRounding.AwayFromZero);
            if (sampled.Count == 0 ||
                !string.Equals(sampled[^1].WindowId, windows[sourceIndex].WindowId, StringComparison.Ordinal))
            {
                sampled.Add(windows[sourceIndex]);
            }
        }

        return sampled;
    }

    public EcdCwrReferenceWindow? BuildAutomaticWindow(
        DateTimeOffset cutoff,
        int requiredFrameCount,
        IReadOnlyList<EcdCwrReferenceCandidate>? persistedCandidates = null)
    {
        if (requiredFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFrameCount));
        }

        var eligible = Merge(persistedCandidates)
            .Where(candidate => candidate.CapturedAt <= cutoff)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var segmentStart = eligible.Length - 1;
        while (segmentStart > 0 &&
            !IsBoundary(eligible[segmentStart - 1], eligible[segmentStart]))
        {
            segmentStart--;
        }

        var segmentLength = eligible.Length - segmentStart;
        if (segmentLength < requiredFrameCount)
        {
            return null;
        }

        var memoryIds = memory.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
        return CreateWindow(eligible[segmentStart..], memoryIds);
    }

    public IReadOnlyList<EcdCwrReferenceWindow> BuildRepresentativeWindows(
        int requiredFrameCount,
        IReadOnlyList<EcdCwrReferenceCandidate>? persistedCandidates = null,
        int maximumWindowCount = 8)
    {
        if (requiredFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredFrameCount));
        }

        if (maximumWindowCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWindowCount));
        }

        var merged = Merge(persistedCandidates);
        var memoryIds = memory.Select(candidate => candidate.SourceId).ToHashSet(StringComparer.Ordinal);
        var windows = new List<EcdCwrReferenceWindow>();
        var segmentStart = 0;
        for (var index = 1; index <= merged.Length; index++)
        {
            var boundary = index == merged.Length ||
                IsBoundary(merged[index - 1], merged[index]);
            if (!boundary)
            {
                continue;
            }

            var segmentWindows = new List<EcdCwrReferenceWindow>();
            for (var end = index; end - requiredFrameCount >= segmentStart; end -= requiredFrameCount)
            {
                segmentWindows.Add(CreateWindow(
                    merged[(end - requiredFrameCount)..end],
                    memoryIds));
            }

            segmentWindows.Reverse();
            windows.AddRange(segmentWindows);
            segmentStart = index;
        }

        if (windows.Count <= maximumWindowCount)
        {
            return windows;
        }

        if (maximumWindowCount == 1)
        {
            return [windows[^1]];
        }

        var sampled = new List<EcdCwrReferenceWindow>(maximumWindowCount);
        for (var index = 0; index < maximumWindowCount; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (windows.Count - 1.0) / (maximumWindowCount - 1.0),
                MidpointRounding.AwayFromZero);
            sampled.Add(windows[sourceIndex]);
        }

        return sampled;
    }

    public IReadOnlyList<EcdCwrRobustReferenceObservation> ResolveObservations(
        EcdCwrReferenceWindow window,
        IReadOnlyList<EcdCwrReferenceCandidate>? persistedCandidates = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        var byId = Merge(persistedCandidates).ToDictionary(candidate => candidate.SourceId, StringComparer.Ordinal);
        var observations = new List<EcdCwrRobustReferenceObservation>(window.SourceCandidateIds.Count);
        foreach (var sourceId in window.SourceCandidateIds)
        {
            if (!byId.TryGetValue(sourceId, out var candidate))
            {
                throw new InvalidOperationException($"Reference candidate '{sourceId}' is no longer available.");
            }

            observations.Add(candidate.Observation);
        }

        return observations;
    }

    private EcdCwrReferenceCandidate[] Merge(
        IReadOnlyList<EcdCwrReferenceCandidate>? persistedCandidates)
    {
        var merged = new Dictionary<string, EcdCwrReferenceCandidate>(StringComparer.Ordinal);
        if (persistedCandidates is not null)
        {
            foreach (var candidate in persistedCandidates)
            {
                ValidateCandidate(candidate);
                merged[candidate.SourceId] = candidate;
            }
        }

        foreach (var candidate in memory)
        {
            merged[candidate.SourceId] = candidate;
        }

        return merged.Values.OrderBy(candidate => candidate.Sequence).ToArray();
    }

    private static EcdCwrReferenceWindow CreateWindow(
        IReadOnlyList<EcdCwrReferenceCandidate> candidates,
        IReadOnlySet<string> memoryIds)
    {
        var first = candidates[0];
        var last = candidates[^1];
        var duration = last.CapturedAt - first.CapturedAt;
        var durationMinutes = Math.Max(duration.TotalMinutes, 1.0 / 60.0);
        var drift = RelativeRootMeanSquareDifference(
            first.Observation.Voltage208,
            last.Observation.Voltage208) / durationMinutes;
        var contactEvidence = candidates
            .Select(candidate => candidate.ContactEvidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new EcdCwrReferenceWindow(
            $"{first.SourceId}..{last.SourceId}",
            first.Fingerprint,
            candidates.Select(candidate => candidate.SourceId).ToArray(),
            first.CapturedAt,
            last.CapturedAt,
            first.CapturedAt + TimeSpan.FromTicks(duration.Ticks / 2),
            candidates.Count,
            drift,
            candidates.Skip(1).Count(candidate => candidate.GapBeforeSamples > 0),
            candidates.Sum(candidate => candidate.SaturationCount),
            string.Join(" / ", contactEvidence),
            candidates.Any(candidate => !memoryIds.Contains(candidate.SourceId)));
    }

    private static double RelativeRootMeanSquareDifference(
        IReadOnlyList<double> first,
        IReadOnlyList<double> last)
    {
        var squaredDelta = 0.0;
        var squaredBase = 0.0;
        for (var index = 0; index < first.Count; index++)
        {
            var delta = last[index] - first[index];
            squaredDelta += delta * delta;
            squaredBase += first[index] * first[index];
        }

        return Math.Sqrt(squaredDelta / Math.Max(squaredBase, 1.0e-24));
    }

    private static bool IsBoundary(
        EcdCwrReferenceCandidate previous,
        EcdCwrReferenceCandidate current)
    {
        return current.Sequence != previous.Sequence + 1 ||
            current.GapBeforeSamples > 0 ||
            !string.Equals(current.Fingerprint, previous.Fingerprint, StringComparison.Ordinal);
    }

    private static void ValidateCandidate(EcdCwrReferenceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(candidate.SourceId) ||
            candidate.BlockNumber <= 0 ||
            candidate.FrameNumber < 0 ||
            candidate.EndSampleIndex < candidate.StartSampleIndex ||
            string.IsNullOrWhiteSpace(candidate.Fingerprint) ||
            candidate.GapBeforeSamples < 0 ||
            candidate.SaturationCount < 0 ||
            string.IsNullOrWhiteSpace(candidate.ContactEvidence) ||
            !EcdCwrRobustReferenceBuilder.IsFiniteObservation(candidate.Observation))
        {
            throw new ArgumentException("Reference candidate contains invalid provenance or observation data.", nameof(candidate));
        }
    }
}
