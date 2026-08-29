using EitHost.Core.Analysis;

namespace EitHost.App.ViewModels;

internal sealed class RealtimePreviewStateStore
{
    private readonly Dictionary<string, RealtimeDevicePreviewCache> previewBySet =
        new(StringComparer.OrdinalIgnoreCase);
    private RealtimeRawPreviewSnapshot? pendingRaw;
    private RealtimeDemodPreviewSnapshot? pendingDemod;
    private RealtimeBoundaryFitPreviewSnapshot? pendingBoundaryFit;
    private RealtimeImagePreviewSnapshot? pendingImage;
    private RealtimeRoiPreviewSnapshot? pendingRoi;
    private RealtimeSignalPreviewSource? latestSignalSource;
    private string? pendingSummary;
    private string? pendingImageStats;
    private string? pendingReconstructionActivity;
    private string? pendingReferenceSummary;
    private string? pendingBaselineIntegritySummary;
    private string? pendingContactSummary;
    private string? pendingMultiFrequencySummary;
    private string? pendingDataQualityStatus;
    private string? pendingReferenceModeStatus;
    private string? pendingReconstructionQualityStatus;
    private string? pendingRoiReadinessStatus;
    private bool? pendingReferenceInvalidated;
    private readonly List<string> pendingLogLines = [];

    internal object Gate { get; } = new();

    internal IReadOnlyDictionary<string, RealtimeDevicePreviewCache> PreviewBySetUnsafe => previewBySet;

    internal RealtimeRoiPreviewSnapshot? PendingRoiUnsafe
    {
        get => pendingRoi;
        set => pendingRoi = value;
    }

    internal Dictionary<string, List<RoiCurvePoint>> RoiSeriesBySet { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, List<FixedRoiTemporalSample>> FixedRoiSamplesBySet { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, HashSet<int>> FixedRoiPinnedFramesBySet { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal RealtimeDevicePreviewCache GetOrCreateUnsafe(string setLabel)
    {
        if (!previewBySet.TryGetValue(setLabel, out var cache))
        {
            cache = new RealtimeDevicePreviewCache();
            previewBySet[setLabel] = cache;
        }

        return cache;
    }

    internal void Clear(string? setLabel, bool clearsDisplayedSet)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(setLabel))
            {
                previewBySet.Remove(setLabel);
                RoiSeriesBySet.Remove(setLabel);
                FixedRoiSamplesBySet.Remove(setLabel);
                FixedRoiPinnedFramesBySet.Remove(setLabel);
            }
            else
            {
                RoiSeriesBySet.Clear();
                FixedRoiSamplesBySet.Clear();
                FixedRoiPinnedFramesBySet.Clear();
            }

            if (string.IsNullOrWhiteSpace(setLabel) || clearsDisplayedSet)
            {
                ClearPendingUnsafe(clearLogs: true);
                latestSignalSource = null;
            }
        }
    }

    internal void PublishImageStats(string setLabel, bool displayed, string stats)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).ImageStats = stats;
            if (displayed)
            {
                pendingImageStats = stats;
            }
        }
    }

    internal void PublishReconstructionActivity(string setLabel, bool displayed, string activity)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).ReconstructionActivity = activity;
            if (displayed)
            {
                pendingReconstructionActivity = activity;
            }
        }
    }

    internal void PublishReferenceInvalidated(string setLabel, bool displayed, bool invalidated)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).ReferenceInvalidated = invalidated;
            if (displayed)
            {
                pendingReferenceInvalidated = invalidated;
            }
        }
    }

    internal void PublishSummary(string setLabel, bool displayed, string summary)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).Summary = summary;
            if (displayed)
            {
                pendingSummary = summary;
            }
        }
    }

    internal bool ClearLowConfidenceImage(string setLabel, bool displayed)
    {
        lock (Gate)
        {
            var cache = GetOrCreateUnsafe(setLabel);
            if (cache.Image is not { LowConfidence: true } image)
            {
                return false;
            }

            var updated = image with { LowConfidence = false };
            cache.Image = updated;
            if (displayed)
            {
                pendingImage = updated;
            }

            return true;
        }
    }

    internal void PublishReferenceSummary(string setLabel, bool displayed, string summary)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).ReferenceSummary = summary;
            if (displayed)
            {
                pendingReferenceSummary = summary;
            }
        }
    }

    internal void PublishBaselineIntegritySummary(string setLabel, bool displayed, string summary)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).BaselineIntegritySummary = summary;
            if (displayed)
            {
                pendingBaselineIntegritySummary = summary;
            }
        }
    }

    internal void PublishContactSummary(string setLabel, bool displayed, string summary)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).ContactSummary = summary;
            if (displayed)
            {
                pendingContactSummary = summary;
            }
        }
    }

    internal void PublishMultiFrequencySummary(string setLabel, bool displayed, string summary)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).MultiFrequencySummary = summary;
            if (displayed)
            {
                pendingMultiFrequencySummary = summary;
            }
        }
    }

    internal void PublishQualityAxes(
        string setLabel,
        bool displayed,
        string? dataQuality,
        string? referenceMode,
        string? reconstructionQuality,
        string? roiReadiness)
    {
        lock (Gate)
        {
            var cache = GetOrCreateUnsafe(setLabel);
            cache.DataQualityStatus = dataQuality ?? cache.DataQualityStatus;
            cache.ReferenceModeStatus = referenceMode ?? cache.ReferenceModeStatus;
            cache.ReconstructionQualityStatus = reconstructionQuality ?? cache.ReconstructionQualityStatus;
            cache.RoiReadinessStatus = roiReadiness ?? cache.RoiReadinessStatus;
            if (displayed)
            {
                pendingDataQualityStatus = dataQuality;
                pendingReferenceModeStatus = referenceMode;
                pendingReconstructionQualityStatus = reconstructionQuality;
                pendingRoiReadinessStatus = roiReadiness;
            }
        }
    }

    internal void QueueLog(string line, int limit)
    {
        lock (Gate)
        {
            pendingLogLines.Add(line);
            while (pendingLogLines.Count > limit)
            {
                pendingLogLines.RemoveAt(0);
            }
        }
    }

    internal void PublishRaw(string setLabel, bool displayed, RealtimeRawPreviewSnapshot snapshot)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).Raw = snapshot;
            if (displayed)
            {
                pendingRaw = snapshot;
            }
        }
    }

    internal void PublishSignal(
        string setLabel,
        bool displayed,
        RealtimeSignalPreviewSource source,
        string summary,
        string viewMode,
        string demodDisplayMode)
    {
        lock (Gate)
        {
            var cache = GetOrCreateUnsafe(setLabel);
            cache.SignalSource = source;
            cache.Summary = summary;
            if (displayed)
            {
                latestSignalSource = source;
                pendingDemod = RealtimeVisualizationProjection.CreateRealtimeSignalPreviewSnapshot(
                    source,
                    viewMode,
                    demodDisplayMode);
                pendingSummary = summary;
            }
        }
    }

    internal void PublishBoundary(string setLabel, bool displayed, RealtimeBoundaryFitPreviewSnapshot snapshot)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).BoundaryFit = snapshot;
            if (displayed)
            {
                pendingBoundaryFit = snapshot;
            }
        }
    }

    internal void PublishImage(
        string setLabel,
        bool displayed,
        RealtimeImagePreviewSnapshot snapshot,
        string? summary)
    {
        lock (Gate)
        {
            var cache = GetOrCreateUnsafe(setLabel);
            cache.Image = snapshot;
            if (summary is not null)
            {
                cache.Summary = summary;
            }

            if (displayed)
            {
                pendingImage = snapshot;
                if (summary is not null)
                {
                    pendingSummary = summary;
                }
            }
        }
    }

    internal void PublishRoi(string setLabel, bool displayed, RealtimeRoiPreviewSnapshot snapshot)
    {
        lock (Gate)
        {
            GetOrCreateUnsafe(setLabel).Roi = snapshot;
            if (displayed)
            {
                pendingRoi = snapshot;
            }
        }
    }

    internal RealtimeDevicePreviewCache? SelectDisplay(string? setLabel)
    {
        lock (Gate)
        {
            var cache = !string.IsNullOrWhiteSpace(setLabel) && previewBySet.TryGetValue(setLabel, out var existing)
                ? existing.Clone()
                : null;
            latestSignalSource = cache?.SignalSource;
            ClearPendingUnsafe(clearLogs: false);
            return cache;
        }
    }

    internal RealtimeSignalPreviewSource? GetSignalSource(string? setLabel)
    {
        lock (Gate)
        {
            return setLabel is not null && previewBySet.TryGetValue(setLabel, out var cache)
                ? cache.SignalSource
                : latestSignalSource;
        }
    }

    internal RealtimePreviewPendingBatch TakePending()
    {
        lock (Gate)
        {
            var batch = new RealtimePreviewPendingBatch(
                pendingRaw,
                pendingDemod,
                pendingBoundaryFit,
                pendingImage,
                pendingRoi,
                pendingSummary,
                pendingImageStats,
                pendingReconstructionActivity,
                pendingReferenceSummary,
                pendingBaselineIntegritySummary,
                pendingContactSummary,
                pendingMultiFrequencySummary,
                pendingDataQualityStatus,
                pendingReferenceModeStatus,
                pendingReconstructionQualityStatus,
                pendingRoiReadinessStatus,
                pendingReferenceInvalidated,
                pendingLogLines.Count == 0 ? [] : [.. pendingLogLines]);
            ClearPendingUnsafe(clearLogs: true);
            return batch;
        }
    }

    internal void ClearAllRoi()
    {
        lock (Gate)
        {
            RoiSeriesBySet.Clear();
            FixedRoiSamplesBySet.Clear();
            FixedRoiPinnedFramesBySet.Clear();
            foreach (var cache in previewBySet.Values)
            {
                cache.Roi = null;
            }

            pendingRoi = null;
        }
    }

    private void ClearPendingUnsafe(bool clearLogs)
    {
        pendingRaw = null;
        pendingDemod = null;
        pendingBoundaryFit = null;
        pendingImage = null;
        pendingRoi = null;
        pendingSummary = null;
        pendingImageStats = null;
        pendingReconstructionActivity = null;
        pendingReferenceSummary = null;
        pendingBaselineIntegritySummary = null;
        pendingContactSummary = null;
        pendingMultiFrequencySummary = null;
        pendingDataQualityStatus = null;
        pendingReferenceModeStatus = null;
        pendingReconstructionQualityStatus = null;
        pendingRoiReadinessStatus = null;
        pendingReferenceInvalidated = null;
        if (clearLogs)
        {
            pendingLogLines.Clear();
        }
    }
}

internal sealed record RealtimePreviewPendingBatch(
    RealtimeRawPreviewSnapshot? Raw,
    RealtimeDemodPreviewSnapshot? Demod,
    RealtimeBoundaryFitPreviewSnapshot? BoundaryFit,
    RealtimeImagePreviewSnapshot? Image,
    RealtimeRoiPreviewSnapshot? Roi,
    string? Summary,
    string? ImageStats,
    string? ReconstructionActivity,
    string? ReferenceSummary,
    string? BaselineIntegritySummary,
    string? ContactSummary,
    string? MultiFrequencySummary,
    string? DataQualityStatus,
    string? ReferenceModeStatus,
    string? ReconstructionQualityStatus,
    string? RoiReadinessStatus,
    bool? ReferenceInvalidated,
    IReadOnlyList<string> LogLines);
