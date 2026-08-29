using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;
using PureHDF;
using System.Globalization;

namespace EitHost.Core.Storage.Catalog;

public sealed class ExperimentDemodCatchUpService
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly DerivedArtifactHdf5Writer writer;
    private readonly Hdf5RawDatasetReader rawReader;

    public ExperimentDemodCatchUpService(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        DerivedArtifactHdf5Writer? writer = null,
        Hdf5RawDatasetReader? rawReader = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.writer = writer ?? new DerivedArtifactHdf5Writer();
        this.rawReader = rawReader ?? new Hdf5RawDatasetReader();
    }

    /// <summary>
    /// Re-derives every demodulated block the live pipeline did not produce.
    ///
    /// Each block is committed independently, so cancelling between segments leaves the catalog
    /// consistent and the remaining work still marked pending for a later, idempotent retry.
    /// </summary>
    public ExperimentDemodCatchUpReport Run(
        Guid experimentRunId,
        IProgress<ExperimentCatchUpProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Observed before any catalog work so an already-cancelled request is honoured even when
        // the run has nothing to process.
        cancellationToken.ThrowIfCancellationRequested();
        var run = catalog.GetRun(experimentRunId) ?? throw new InvalidOperationException(
            $"Experiment run {experimentRunId:D} does not exist.");
        EnsureTerminalRun(run);
        var segments = catalog.ListRawSegments(experimentRunId)
            .Where(segment => string.Equals(segment.Status, "ready", StringComparison.Ordinal))
            .OrderBy(segment => segment.SegmentSequence)
            .ToArray();
        var existing = catalog.ListProcessingBlocks(experimentRunId).ToList();
        var readyRanges = existing
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .Select(block => (block.SourceStartSampleIndex, block.SourceEndSampleIndex))
            .ToList();
        var usedBlockNumbers = existing.Select(block => block.BlockNumber).ToHashSet();
        var nextBlockNumber = usedBlockNumbers.Count == 0 ? 1 : usedBlockNumbers.Max() + 1;
        RealtimeBlockDemodulator? demodulator = null;
        RealtimeDemodulationSettings? settings = null;
        long expectedNextSample = -1;
        var recoveredBlocks = 0;
        var skippedBlocks = 0;
        var failedBlocks = 0;
        var missingSegments = 0;
        long discardedRawRows = 0;
        long clockAnchorSampleIndex = 0;
        var clockAnchorAt = run.StartedAt;

        void PersistAvailableBlocks()
        {
            foreach (var replayedBlock in demodulator!.ProcessAvailableBlocks())
            {
                var range = (replayedBlock.StartSampleIndex, replayedBlock.EndSampleIndex);
                if (readyRanges.Any(ready => RangesOverlap(ready, range)))
                {
                    skippedBlocks++;
                    continue;
                }

                var blockNumber = SelectBlockNumber(
                    replayedBlock,
                    existing,
                    usedBlockNumbers,
                    ref nextBlockNumber);
                var block = replayedBlock with { BlockNumber = blockNumber };
                var acquiredAt = clockAnchorAt + TimeSpan.FromSeconds(
                    (block.StartSampleIndex - clockAnchorSampleIndex) / (double)settings!.SampleRateHz);
                var processedAt = DateTimeOffset.UtcNow;
                var outputPath = layout.GetDerivedBlockPath(run.RunDirectory, blockNumber);
                try
                {
                    writer.WriteDemodulatedBlock(
                        outputPath,
                        new DerivedDemodulatedBlockData(
                            experimentRunId,
                            acquiredAt,
                            processedAt,
                            block));
                    var record = new ProcessingBlockCatalogRecord(
                        experimentRunId,
                        blockNumber,
                        block.StartSampleIndex,
                        block.EndSampleIndex,
                        acquiredAt,
                        processedAt,
                        "ready",
                        QualityWeight: block.QualityWeight,
                        AcceptedFrameCount: block.AcceptedFrameCount,
                        RejectedFrameCount: block.RejectedFrameCount);
                    catalog.RecordDemodulatedBlock(
                        record,
                        new DerivedArtifactCatalogRecord(
                            experimentRunId,
                            blockNumber,
                            "demod",
                            layout.ToRelativeArtifactPath(outputPath),
                            DataRootLayout.GetDerivedDatasetPath(blockNumber, "/demod"),
                            processedAt));
                    existing.Add(record);
                    readyRanges.Add(range);
                    usedBlockNumbers.Add(blockNumber);
                    recoveredBlocks++;
                }
                catch (Exception ex)
                {
                    failedBlocks++;
                    catalog.RecordDemodulatedBlock(new ProcessingBlockCatalogRecord(
                        experimentRunId,
                        blockNumber,
                        block.StartSampleIndex,
                        block.EndSampleIndex,
                        acquiredAt,
                        processedAt,
                        "failed",
                        ex.Message));
                }
            }
        }

        var processedSegments = 0;
        progress?.Report(new ExperimentCatchUpProgress(
            experimentRunId,
            ExperimentCatchUpPhase.Demodulating,
            0,
            segments.Length));

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedSegments++;
            var path = layout.ResolveArtifactPath(segment.ArtifactPath);
            if (!File.Exists(path))
            {
                missingSegments++;
                expectedNextSample = -1;
                progress?.Report(new ExperimentCatchUpProgress(
                    experimentRunId,
                    ExperimentCatchUpPhase.Demodulating,
                    processedSegments,
                    segments.Length));
                continue;
            }

            try
            {
                using var file = Hdf5FileAccess.OpenReadWithRetry(path);
                ValidateSegmentIdentity(file, segment);
                var segmentSettings = ReadSettings(file);
                if (demodulator is null || settings is null || !SettingsMatch(settings, segmentSettings))
                {
                    settings = segmentSettings;
                    demodulator = new RealtimeBlockDemodulator(settings);
                    demodulator.ResetForDiscontinuity(segment.StartSampleIndex);
                    if (segment.StartSampleIndex != expectedNextSample && segment.StartSampleIndex > 0)
                    {
                        clockAnchorSampleIndex = segment.StartSampleIndex;
                        clockAnchorAt = segment.CapturedAt;
                    }
                }
                else if (expectedNextSample != segment.StartSampleIndex)
                {
                    demodulator.ResetForDiscontinuity(segment.StartSampleIndex);
                    clockAnchorSampleIndex = segment.StartSampleIndex;
                    clockAnchorAt = segment.CapturedAt;
                }

                var discontinuities = ReadDiscontinuities(file, segment);
                void AppendRange(long startSampleIndex, long endSampleIndex)
                {
                    if (endSampleIndex <= startSampleIndex)
                    {
                        return;
                    }

                    foreach (var chunk in rawReader.ReadRange(
                                 path,
                                 startSampleIndex - segment.StartSampleIndex,
                                 endSampleIndex - startSampleIndex))
                    {
                        demodulator.AppendSamples(chunk.Values);
                        PersistAvailableBlocks();
                    }
                }

                var cursor = segment.StartSampleIndex;
                foreach (var discontinuity in discontinuities)
                {
                    discardedRawRows = checked(
                        discardedRawRows +
                        discontinuity.EndSampleIndex - discontinuity.StartSampleIndex);
                    AppendRange(cursor, discontinuity.StartSampleIndex);
                    demodulator.ResetForDiscontinuity(discontinuity.EndSampleIndex);
                    clockAnchorSampleIndex = discontinuity.EndSampleIndex;
                    clockAnchorAt = discontinuity.DetectedAt;
                    cursor = discontinuity.EndSampleIndex;
                }

                AppendRange(cursor, segment.EndSampleIndex);
                expectedNextSample = segment.EndSampleIndex;
            }
            catch
            {
                failedBlocks++;
                expectedNextSample = -1;
            }

            progress?.Report(new ExperimentCatchUpProgress(
                experimentRunId,
                ExperimentCatchUpPhase.Demodulating,
                processedSegments,
                segments.Length));
        }

        var refreshedBlocks = catalog.ListProcessingBlocks(experimentRunId);
        var rawRows = segments.Sum(segment => segment.SampleRows);
        var coveredRows = CalculateCoveredRows(refreshedBlocks
            .Where(block => string.Equals(block.DemodStatus, "ready", StringComparison.Ordinal))
            .Select(block => (block.SourceStartSampleIndex, block.SourceEndSampleIndex)));
        var pendingRows = Math.Max(0, rawRows - discardedRawRows - coveredRows);
        var demodStatus = failedBlocks > 0 || missingSegments > 0
            ? "incomplete"
            : pendingRows > 0
                ? "partial"
                : "complete";
        var current = catalog.GetRun(experimentRunId)!;
        catalog.SetRunStageStatuses(
            experimentRunId,
            current.RawStatus,
            demodStatus,
            current.ReconstructionStatus);
        return new ExperimentDemodCatchUpReport(
            experimentRunId,
            rawRows,
            coveredRows,
            pendingRows,
            recoveredBlocks,
            skippedBlocks,
            failedBlocks,
            missingSegments,
            demodStatus,
            discardedRawRows);
    }

    private static void EnsureTerminalRun(ExperimentRunRecord run)
    {
        if (run.Status is not (
                ExperimentCatalog.CompletedStatus or
                ExperimentCatalog.InterruptedStatus or
                ExperimentCatalog.FailedStatus))
        {
            throw new InvalidOperationException(
                $"Catch-up requires a terminal experiment run; current status is '{run.Status}'.");
        }
    }

    private static void ValidateSegmentIdentity(IH5Group file, RawSegmentCatalogRecord segment)
    {
        var runId = Guid.Parse(file.Dataset("/metadata/run/experiment_run_id").Read<string>());
        var sequence = file.Dataset("/metadata/run/segment_sequence").Read<int>();
        var start = file.Dataset("/metadata/run/start_sample_index").Read<long>();
        var end = file.Dataset("/metadata/run/end_sample_index").Read<long>();
        if (runId != segment.ExperimentRunId ||
            sequence != segment.SegmentSequence ||
            start != segment.StartSampleIndex ||
            end != segment.EndSampleIndex)
        {
            throw new InvalidDataException("Raw segment HDF5 identity does not match catalog metadata.");
        }
    }

    private static IReadOnlyList<RawAcquisitionDiscontinuityEvent> ReadDiscontinuities(
        IH5Group file,
        RawSegmentCatalogRecord segment)
    {
        var embeddedHasDiscontinuity = file.LinkExists("/metadata/acquisition/has_discontinuity") &&
            file.Dataset("/metadata/acquisition/has_discontinuity").Read<bool>();
        if (embeddedHasDiscontinuity != segment.HasDiscontinuity)
        {
            throw new InvalidDataException(
                "Raw segment discontinuity flag does not match catalog metadata.");
        }

        if (!embeddedHasDiscontinuity)
        {
            return [];
        }

        if (!file.LinkExists("/metadata/acquisition/overflow_events") ||
            !file.LinkExists("/metadata/acquisition/overflow_event_detected_at_utc"))
        {
            throw new InvalidDataException("Raw segment marks a discontinuity but has no overflow event metadata.");
        }

        var ranges = file.Dataset("/metadata/acquisition/overflow_events").Read<long[,]>();
        var detectedAt = file.Dataset("/metadata/acquisition/overflow_event_detected_at_utc").Read<string[]>();
        var reasons = file.LinkExists("/metadata/acquisition/overflow_event_reason")
            ? file.Dataset("/metadata/acquisition/overflow_event_reason").Read<string[]>()
            : Enumerable.Repeat(
                RawAcquisitionDiscontinuityEvent.UsbBufferOverflowReason,
                ranges.GetLength(0)).ToArray();
        if (ranges.Rank != 2 ||
            ranges.GetLength(1) != 2 ||
            detectedAt.Length != ranges.GetLength(0) ||
            reasons.Length != ranges.GetLength(0))
        {
            throw new InvalidDataException("Raw segment overflow event metadata shape is invalid.");
        }

        var events = new List<RawAcquisitionDiscontinuityEvent>(ranges.GetLength(0));
        long previousEnd = segment.StartSampleIndex;
        for (var index = 0; index < ranges.GetLength(0); index++)
        {
            var start = ranges[index, 0];
            var end = ranges[index, 1];
            if (start < segment.StartSampleIndex ||
                end > segment.EndSampleIndex ||
                end <= start ||
                start < previousEnd)
            {
                throw new InvalidDataException(
                    "Raw segment overflow event range is outside the segment or overlaps another event.");
            }

            events.Add(new RawAcquisitionDiscontinuityEvent(
                start,
                end,
                DateTimeOffset.Parse(
                    detectedAt[index],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reasons[index]));
            previousEnd = end;
        }

        return events;
    }

    private static bool RangesOverlap(
        (long Start, long End) left,
        (long Start, long End) right)
    {
        return left.Start < right.End && right.Start < left.End;
    }

    private static RealtimeDemodulationSettings ReadSettings(IH5Group file)
    {
        var sampleRate = file.Dataset("/metadata/acquisition/sample_rate_hz").Read<int>();
        var frequency = file.LinkExists("/metadata/excitation/actual_frequency_hz")
            ? file.Dataset("/metadata/excitation/actual_frequency_hz").Read<double>()
            : file.Dataset("/metadata/excitation/frequency_hz").Read<int>();
        var channelCycles = file.LinkExists("/metadata/excitation/effective_channel_cycles")
            ? file.Dataset("/metadata/excitation/effective_channel_cycles").Read<double>()
            : file.Dataset("/metadata/excitation/channel_cycles").Read<double>();
        var adRange = (Usb2070AdRange)file.Dataset("/metadata/acquisition/ad_range_code").Read<int>();
        var framesPerBlock = file.LinkExists("/metadata/demodulation/frames_per_block")
            ? file.Dataset("/metadata/demodulation/frames_per_block").Read<int>()
            : 3;
        var minimumAcceptedFrames = file.LinkExists("/metadata/demodulation/minimum_accepted_frames")
            ? file.Dataset("/metadata/demodulation/minimum_accepted_frames").Read<int>()
            : framesPerBlock;
        var discardLeading = file.LinkExists("/metadata/demodulation/discard_leading_cycles")
            ? file.Dataset("/metadata/demodulation/discard_leading_cycles").Read<double>()
            : 0.0;
        var discardTrailing = file.LinkExists("/metadata/demodulation/discard_trailing_cycles")
            ? file.Dataset("/metadata/demodulation/discard_trailing_cycles").Read<double>()
            : 0.0;
        var interference = file.LinkExists("/metadata/demodulation/interference_frequency_hz")
            ? file.Dataset("/metadata/demodulation/interference_frequency_hz").Read<double[]>()
            : [];
        return new RealtimeDemodulationSettings(
            sampleRate,
            frequency,
            channelCycles,
            framesPerBlock: framesPerBlock,
            minimumAcceptedFrames: minimumAcceptedFrames,
            discardLeadingCycles: discardLeading,
            discardTrailingCycles: discardTrailing,
            interferenceFrequencyHz: interference,
            adRange: adRange);
    }

    private static bool SettingsMatch(
        RealtimeDemodulationSettings left,
        RealtimeDemodulationSettings right)
    {
        return left.SampleRateHz == right.SampleRateHz &&
            left.ExcitationFrequencyHz == right.ExcitationFrequencyHz &&
            left.ChannelCycles == right.ChannelCycles &&
            left.FramesPerBlock == right.FramesPerBlock &&
            left.MinimumAcceptedFrames == right.MinimumAcceptedFrames &&
            left.DiscardLeadingCycles == right.DiscardLeadingCycles &&
            left.DiscardTrailingCycles == right.DiscardTrailingCycles &&
            left.AdRange == right.AdRange &&
            left.InterferenceFrequencyHz.SequenceEqual(right.InterferenceFrequencyHz);
    }

    private static int SelectBlockNumber(
        RealtimeDemodulatedBlock block,
        IReadOnlyList<ProcessingBlockCatalogRecord> existing,
        HashSet<int> usedBlockNumbers,
        ref int nextBlockNumber)
    {
        var sameRange = existing.FirstOrDefault(candidate =>
            candidate.SourceStartSampleIndex == block.StartSampleIndex &&
            candidate.SourceEndSampleIndex == block.EndSampleIndex);
        if (sameRange is not null)
        {
            return sameRange.BlockNumber;
        }

        if (usedBlockNumbers.Add(block.BlockNumber))
        {
            return block.BlockNumber;
        }

        while (!usedBlockNumbers.Add(nextBlockNumber))
        {
            nextBlockNumber++;
        }

        return nextBlockNumber++;
    }

    private static long CalculateCoveredRows(IEnumerable<(long Start, long End)> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        long total = 0;
        var start = ordered[0].Start;
        var end = ordered[0].End;
        foreach (var range in ordered.Skip(1))
        {
            if (range.Start > end)
            {
                total = checked(total + end - start);
                start = range.Start;
                end = range.End;
            }
            else
            {
                end = Math.Max(end, range.End);
            }
        }

        return checked(total + end - start);
    }
}

public sealed record ExperimentDemodCatchUpReport(
    Guid ExperimentRunId,
    long RawSampleRows,
    long DemodCoveredRows,
    long PendingRawRows,
    int RecoveredBlockCount,
    int SkippedBlockCount,
    int FailedBlockCount,
    int MissingSegmentCount,
    string DemodStatus,
    long DiscardedRawRows = 0);
