namespace EitHost.Core.Demodulation;

public sealed class RealtimeBlockDemodulator
{
    private const int ChannelCount = DemodulatedFrame.StimulationCount;

    private readonly RealtimeDemodulationSettings settings;
    private readonly OfflineDemodulator offlineDemodulator;
    private readonly List<ushort[]> bufferedRows = [];
    private readonly ushort[,] cadenceHistoryRows;
    private long firstBufferedSampleIndex;
    private int nextBlockNumber = 1;
    private double? lockedWindowSamples;
    private int blocksSinceLastRelock;
    private int consecutiveLowQualityBlocks;
    private int cadenceHistoryHead;
    private int cadenceHistoryCount;
    private int cadenceRefreshGeneration;
    private bool forceRelockRequested;
    private Task<CadenceRefreshResult>? cadenceRefreshTask;

    public RealtimeBlockDemodulator(
        RealtimeDemodulationSettings settings,
        OfflineDemodulator? offlineDemodulator = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.offlineDemodulator = offlineDemodulator ?? new OfflineDemodulator();
        cadenceHistoryRows = new ushort[settings.RequiredBufferedSamples, ChannelCount];
    }

    public int BufferedSampleCount => bufferedRows.Count;

    public long FirstBufferedSampleIndex => firstBufferedSampleIndex;

    public int BlockingRelockCount { get; private set; }

    public int CadenceRefreshScheduledCount { get; private set; }

    public int CadenceRefreshAppliedCount { get; private set; }

    public int CadenceRefreshFailedCount { get; private set; }

    public int CadenceRefreshRejectedCount { get; private set; }

    public double? LastRejectedCadenceRefreshSamples { get; private set; }

    public double? LockedWindowSamples => lockedWindowSamples;

    public void ResetForDiscontinuity(long nextSampleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextSampleIndex);
        bufferedRows.Clear();
        firstBufferedSampleIndex = nextSampleIndex;
        lockedWindowSamples = null;
        blocksSinceLastRelock = 0;
        consecutiveLowQualityBlocks = 0;
        cadenceHistoryHead = 0;
        cadenceHistoryCount = 0;
        cadenceRefreshGeneration++;
        forceRelockRequested = false;
    }

    public void AppendSamples(ushort[,] rawAdcCounts)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        if (rawAdcCounts.GetLength(1) != ChannelCount)
        {
            throw new ArgumentException("Realtime demodulation expects raw data shaped [sample, 16].", nameof(rawAdcCounts));
        }

        for (var row = 0; row < rawAdcCounts.GetLength(0); row++)
        {
            var copy = new ushort[ChannelCount];
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                copy[channel] = rawAdcCounts[row, channel];
            }

            bufferedRows.Add(copy);
        }
    }

    public IReadOnlyList<RealtimeDemodulatedBlock> ProcessAvailableBlocks()
    {
        var blocks = new List<RealtimeDemodulatedBlock>();
        while (TryProcessNextBlock(out var block))
        {
            blocks.Add(block);
        }

        return blocks;
    }

    public bool TryProcessNextBlock(out RealtimeDemodulatedBlock block)
    {
        block = null!;
        TryApplyCompletedCadenceRefresh();
        var shouldRelock = ShouldRelock();
        var requiredSamples = shouldRelock ? settings.RequiredBufferedSamples : GetFastPathRequiredSamples();
        if (bufferedRows.Count < requiredSamples)
        {
            return false;
        }

        var raw = MaterializeBufferedRows(requiredSamples);
        var result = offlineDemodulator.Demodulate(
            raw,
            shouldRelock
                ? settings.ToOfflineSettings()
                : settings.ToOfflineSettingsWithLockedWindowSamples(
                    lockedWindowSamples ?? settings.NominalWindowSamples));
        if (result.PeakLocations.Count < 2 || result.Frames.Count == 0)
        {
            ForceRelockNextBlock();
            return false;
        }

        var consumedSamples = result.PeakLocations[^1];
        if (consumedSamples <= 0 || consumedSamples > bufferedRows.Count)
        {
            consumedSamples = Math.Min(bufferedRows.Count, Math.Max(1, (int)Math.Round(settings.NominalFrameSamples * settings.FramesPerBlock)));
        }

        var startSample = firstBufferedSampleIndex;
        var endSample = firstBufferedSampleIndex + consumedSamples;
        var acceptedFrameCount = result.Average.AcceptedFrameCount;
        var rejectedFrameCount = result.Average.RejectedFrameCount;
        var qualityWeight = settings.FramesPerBlock <= 0 || !result.UniformIntegrationStable
            ? 0.0
            : Math.Clamp((double)acceptedFrameCount / settings.FramesPerBlock, 0.0, 1.0);
        var firstFrame = result.Frames.FirstOrDefault();
        var rotationStartChannel = firstFrame?.WindowQualities.FirstOrDefault()?.ExpectedReferenceChannel + 1 ?? 0;
        var rotationDirection = EstimateRotationDirection(firstFrame);
        var isHighQuality = result.UniformIntegrationStable &&
            acceptedFrameCount >= settings.MinimumAcceptedFrames;

        block = new RealtimeDemodulatedBlock(
            nextBlockNumber++,
            startSample,
            endSample,
            consumedSamples,
            result.EstimatedWindowSamples,
            result.UniformOffsetSamples,
            rotationStartChannel,
            rotationDirection,
            acceptedFrameCount,
            rejectedFrameCount,
            qualityWeight,
            isHighQuality,
            result.Average,
            result.Frames,
            result.PeakLocations,
            result.TrustedPartialAverage,
            result.DiagnosticAverage,
            result.UniformIntegrationStable,
            result.UniformIntegrationInstability);

        AppendCadenceHistory(bufferedRows, consumedSamples);
        bufferedRows.RemoveRange(0, consumedSamples);
        firstBufferedSampleIndex += consumedSamples;
        UpdateCadenceLock(result, shouldRelock, isHighQuality);
        return true;
    }

    private bool ShouldRelock()
    {
        return lockedWindowSamples is null || forceRelockRequested;
    }

    private int GetFastPathRequiredSamples()
    {
        var windowSamples = lockedWindowSamples ?? settings.NominalWindowSamples;
        var samplesPerBlock = windowSamples * settings.WindowsPerFrame * settings.FramesPerBlock;
        return Math.Max(1, (int)Math.Ceiling(samplesPerBlock + windowSamples));
    }

    private void UpdateCadenceLock(
        OfflineDemodulationResult result,
        bool relocked,
        bool highQuality)
    {
        if (relocked && result.EstimatedWindowSamples > 1.0)
        {
            if (!highQuality)
            {
                CadenceRefreshRejectedCount++;
                LastRejectedCadenceRefreshSamples = result.EstimatedWindowSamples;
            }

            lockedWindowSamples = settings.SelectRelockedWindowSamples(
                lockedWindowSamples,
                result.EstimatedWindowSamples,
                highQuality);
            blocksSinceLastRelock = 0;
            forceRelockRequested = false;
            BlockingRelockCount++;
        }
        else
        {
            blocksSinceLastRelock++;
        }

        if (!highQuality)
        {
            consecutiveLowQualityBlocks++;
            if (consecutiveLowQualityBlocks == 3)
            {
                ForceRelockNextBlock();
            }
        }
        else
        {
            consecutiveLowQualityBlocks = 0;
            TryScheduleCadenceRefresh();
        }
    }

    private void ForceRelockNextBlock()
    {
        forceRelockRequested = true;
    }

    private void TryScheduleCadenceRefresh()
    {
        if (settings.RelockIntervalBlocks <= 0 ||
            blocksSinceLastRelock < settings.RelockIntervalBlocks ||
            cadenceHistoryCount < cadenceHistoryRows.GetLength(0) ||
            cadenceRefreshTask is not null)
        {
            return;
        }

        var history = MaterializeCadenceHistory();
        var generation = cadenceRefreshGeneration;
        cadenceRefreshTask = Task.Run(() => AnalyzeCadenceHistory(history, settings, generation));
        blocksSinceLastRelock = 0;
        CadenceRefreshScheduledCount++;
    }

    private void TryApplyCompletedCadenceRefresh()
    {
        var task = cadenceRefreshTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        cadenceRefreshTask = null;
        var refresh = task.GetAwaiter().GetResult();
        if (refresh.Generation != cadenceRefreshGeneration || refresh.EstimatedWindowSamples is null)
        {
            CadenceRefreshFailedCount++;
            return;
        }

        var refreshWindowSamples = refresh.EstimatedWindowSamples.Value;
        if (lockedWindowSamples is { } currentLock &&
            !settings.CanApplyBackgroundCadenceRefresh(currentLock, refreshWindowSamples))
        {
            CadenceRefreshRejectedCount++;
            LastRejectedCadenceRefreshSamples = refreshWindowSamples;
            return;
        }

        lockedWindowSamples = settings.StabilizeLockedWindowSamples(refreshWindowSamples);
        CadenceRefreshAppliedCount++;
    }

    private void AppendCadenceHistory(IReadOnlyList<ushort[]> source, int rowCount)
    {
        var capacity = cadenceHistoryRows.GetLength(0);
        var count = Math.Min(rowCount, source.Count);
        for (var row = 0; row < count; row++)
        {
            int destinationRow;
            if (cadenceHistoryCount < capacity)
            {
                destinationRow = (cadenceHistoryHead + cadenceHistoryCount) % capacity;
                cadenceHistoryCount++;
            }
            else
            {
                destinationRow = cadenceHistoryHead;
                cadenceHistoryHead = (cadenceHistoryHead + 1) % capacity;
            }

            for (var channel = 0; channel < ChannelCount; channel++)
            {
                cadenceHistoryRows[destinationRow, channel] = source[row][channel];
            }
        }
    }

    private ushort[,] MaterializeCadenceHistory()
    {
        var raw = new ushort[cadenceHistoryCount, ChannelCount];
        var capacity = cadenceHistoryRows.GetLength(0);
        for (var row = 0; row < cadenceHistoryCount; row++)
        {
            var sourceRow = (cadenceHistoryHead + row) % capacity;
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                raw[row, channel] = cadenceHistoryRows[sourceRow, channel];
            }
        }

        return raw;
    }

    private static CadenceRefreshResult AnalyzeCadenceHistory(
        ushort[,] history,
        RealtimeDemodulationSettings settings,
        int generation)
    {
        try
        {
            var result = new OfflineDemodulator().Demodulate(history, settings.ToOfflineSettings());
            var usable = result.PeakLocations.Count >= 2 &&
                result.Frames.Count > 0 &&
                result.Average.AcceptedFrameCount >= settings.MinimumAcceptedFrames &&
                result.EstimatedWindowSamples > 1.0;
            return new CadenceRefreshResult(
                generation,
                usable ? result.EstimatedWindowSamples : null);
        }
        catch
        {
            return new CadenceRefreshResult(generation, null);
        }
    }

    private ushort[,] MaterializeBufferedRows(int rowCount)
    {
        var count = Math.Min(rowCount, bufferedRows.Count);
        var raw = new ushort[count, ChannelCount];
        for (var row = 0; row < count; row++)
        {
            var source = bufferedRows[row];
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                raw[row, channel] = source[channel];
            }
        }

        return raw;
    }

    private static int EstimateRotationDirection(DemodulatedFrame? frame)
    {
        if (frame is null || frame.WindowQualities.Count < 2)
        {
            return 0;
        }

        var first = frame.WindowQualities[0].ExpectedReferenceChannel;
        var second = frame.WindowQualities[1].ExpectedReferenceChannel;
        var delta = (second - first + ChannelCount) % ChannelCount;
        return delta == 1 ? 1 : delta == ChannelCount - 1 ? -1 : 0;
    }

    private readonly record struct CadenceRefreshResult(
        int Generation,
        double? EstimatedWindowSamples);
}
