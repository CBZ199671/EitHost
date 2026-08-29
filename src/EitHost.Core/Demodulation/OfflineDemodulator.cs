namespace EitHost.Core.Demodulation;

public sealed class OfflineDemodulator
{
    private const int ChannelCount = 16;
    private const double Pi = Math.PI;
    private const double UniformTopologyEquivalentScoreTolerance = 1.0;
    private const double UniformIntegrationNoiseFloorAllowanceLsb = 4.0;
    public const double MaximumUniformIntegrationInstability = 0.01;

    public OfflineDemodulationResult Demodulate(ushort[,] rawAdcCounts, OfflineDemodulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        ArgumentNullException.ThrowIfNull(settings);

        if (rawAdcCounts.GetLength(1) != ChannelCount)
        {
            throw new ArgumentException("Offline demodulation expects raw data shaped [sample, 16].", nameof(rawAdcCounts));
        }

        if (settings.DetectionChannelIndex >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Detection channel must be within 0..15.");
        }

        var data = ToChannelMajor(rawAdcCounts);
        var waveform = ReferenceWaveform.Create(data[0].Length, settings);
        var usedUniformCadence = false;
        var uniformOffsetSamples = 0;
        var uniformIntegrationStable = true;
        var uniformIntegrationInstability = 0.0;
        var estimatedWindowSamples = settings.SampleRateHz / settings.ExcitationFrequencyHz * settings.ChannelCycles;
        var peakLocationsOverride = settings.PeakLocationsOverride?.Select(value => checked((int)value)).ToArray();
        int[] peaks = settings.ForceUniformCadence
            ? []
            : peakLocationsOverride
            ?? DetectRedpoints(
                HilbertEnvelope(data[settings.DetectionChannelIndex]),
                settings.MinRegionWidth,
                settings.PeakRatio);
        if (peakLocationsOverride is not null && peaks.Length >= 2)
        {
            uniformOffsetSamples = peaks[0];
            estimatedWindowSamples = Median(peaks.Zip(peaks.Skip(1), (left, right) => (double)(right - left)).ToArray()) /
                settings.WindowsPerFrame;
        }

        if (settings.ForceUniformCadence ||
            (peakLocationsOverride is null && !DetectedPeaksMatchExpectedCadence(peaks, rawAdcCounts.GetLength(0), settings)))
        {
            var cadence = GenerateUniformFrameCadence(data, waveform, settings);
            peaks = cadence.PeakLocations;
            usedUniformCadence = peaks.Length >= 2;
            uniformOffsetSamples = cadence.OffsetSamples;
            estimatedWindowSamples = cadence.WindowSamples;
            uniformIntegrationStable = cadence.IntegrationStable;
            uniformIntegrationInstability = cadence.IntegrationInstability;
        }

        var frames = DemodulateFrames(data, waveform, peaks, settings);
        return new OfflineDemodulationResult(
            peaks,
            frames,
            AverageValidFrames(frames, settings.IncludeCorrectedFramesInAverage),
            usedUniformCadence,
            uniformOffsetSamples,
            estimatedWindowSamples,
            AggregateTrustedPartialObservations(frames),
            AggregateDiagnosticObservations(frames),
            uniformIntegrationStable,
            uniformIntegrationInstability);
    }

    public OfflineDemodulationResult CombineRealtimeBlocks(
        IReadOnlyList<RealtimeDemodulatedBlock> blocks,
        bool includeCorrectedFramesInAverage = true)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var frames = new List<DemodulatedFrame>();
        foreach (var block in blocks.OrderBy(block => block.BlockNumber))
        {
            foreach (var frame in block.Frames)
            {
                frames.Add(frame with
                {
                    FrameNumber = frames.Count + 1,
                    StartSample = checked((int)(block.StartSampleIndex + frame.StartSample)),
                    EndSample = checked((int)(block.StartSampleIndex + frame.EndSample))
                });
            }
        }

        var estimatedWindowSamples = blocks.Count == 0
            ? 0.0
            : Median(blocks.Select(block => block.EstimatedWindowSamples).ToArray());
        var integrationInstability = blocks.Count == 0
            ? double.PositiveInfinity
            : blocks.Max(block => block.UniformIntegrationInstability);
        return new OfflineDemodulationResult(
            [],
            frames,
            AverageValidFrames(frames, includeCorrectedFramesInAverage),
            UsedUniformCadence: false,
            UniformOffsetSamples: frames.FirstOrDefault()?.StartSample ?? 0,
            EstimatedWindowSamples: estimatedWindowSamples,
            AggregateTrustedPartialObservations(frames),
            AggregateDiagnosticObservations(frames),
            UniformIntegrationStable: blocks.Count > 0 && blocks.All(block => block.UniformIntegrationStable),
            UniformIntegrationInstability: integrationInstability,
            BoundaryProvenance: "production_realtime_block_relock");
    }

    private static double[][] ToChannelMajor(ushort[,] rawAdcCounts)
    {
        var rows = rawAdcCounts.GetLength(0);
        var data = new double[ChannelCount][];
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            data[channel] = new double[rows];
        }

        for (var row = 0; row < rows; row++)
        {
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                data[channel][row] = rawAdcCounts[row, channel];
            }
        }

        return data;
    }

    private static UniformFrameCadence GenerateUniformFrameCadence(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings)
    {
        var sampleCount = data[0].Length;
        var nominalWindowSamples = settings.SampleRateHz / settings.ExcitationFrequencyHz * settings.ChannelCycles;
        var nominalWindowSamplesRounded = (int)Math.Round(nominalWindowSamples);
        var nominalFrameSamples = (int)Math.Round(nominalWindowSamples * settings.WindowsPerFrame);
        if (nominalWindowSamplesRounded <= 1 || nominalFrameSamples <= 1 || sampleCount < nominalFrameSamples)
        {
            return new UniformFrameCadence([], 0, nominalWindowSamples, false, double.PositiveInfinity);
        }

        if (settings.UniformWindowSamplesOverride is { } lockedWindowSamples)
        {
            var lockedSelection = FindBestUniformFrameOffset(
                data,
                waveform,
                settings,
                lockedWindowSamples,
                includeFrameCountBonus: true);
            var lockedPeaks = BuildUniformPeakLocations(
                sampleCount,
                settings,
                lockedSelection.OffsetSamples,
                lockedWindowSamples);
            return new UniformFrameCadence(
                lockedPeaks,
                lockedSelection.OffsetSamples,
                lockedWindowSamples,
                lockedSelection.IntegrationStable,
                lockedSelection.IntegrationInstability);
        }

        var selection = FindBestUniformFrameOffset(data, waveform, settings, nominalWindowSamples, includeFrameCountBonus: true);
        var offset = selection.OffsetSamples;
        var windowSamples = EstimateUniformWindowSamples(data, waveform, settings, offset, nominalWindowSamples);
        selection = FindBestUniformFrameOffset(data, waveform, settings, windowSamples, includeFrameCountBonus: true);
        offset = selection.OffsetSamples;
        windowSamples = EstimateUniformWindowSamples(data, waveform, settings, offset, nominalWindowSamples);
        var peaks = BuildUniformPeakLocations(sampleCount, settings, offset, windowSamples);
        return new UniformFrameCadence(
            peaks,
            offset,
            windowSamples,
            selection.IntegrationStable,
            selection.IntegrationInstability);
    }

    private static bool DetectedPeaksMatchExpectedCadence(
        IReadOnlyList<int> peaks,
        int sampleCount,
        OfflineDemodulationSettings settings)
    {
        if (peaks.Count < 2)
        {
            return false;
        }

        var expectedWindowSamples = settings.SampleRateHz / settings.ExcitationFrequencyHz * settings.ChannelCycles;
        var expectedFrameSamples = expectedWindowSamples * settings.WindowsPerFrame;
        if (expectedFrameSamples <= 1.0)
        {
            return false;
        }

        var expectedFrames = (int)Math.Floor(sampleCount / expectedFrameSamples);
        if (expectedFrames >= 2 && peaks.Count < 3)
        {
            return false;
        }

        var gaps = peaks.Zip(peaks.Skip(1), (left, right) => (double)(right - left)).ToArray();
        if (gaps.Length == 0 || gaps.Any(gap => gap <= 0))
        {
            return false;
        }

        var medianGap = Median(gaps);
        return medianGap >= 0.50 * expectedFrameSamples && medianGap <= 1.75 * expectedFrameSamples;
    }

    private static int[] BuildUniformPeakLocations(
        int sampleCount,
        OfflineDemodulationSettings settings,
        int offset,
        double windowSamples)
    {
        var samplesPerFrame = windowSamples * settings.WindowsPerFrame;
        if (windowSamples <= 1.0 || samplesPerFrame <= 1.0)
        {
            return [];
        }

        var usableSamples = sampleCount - offset;
        if (usableSamples < samplesPerFrame)
        {
            return [];
        }

        var frameCount = Math.Min(settings.MaxFrames, (int)Math.Floor(usableSamples / samplesPerFrame));
        var peaks = new int[frameCount + 1];
        for (var index = 0; index <= frameCount; index++)
        {
            peaks[index] = Math.Min(sampleCount, (int)Math.Round(offset + (index * samplesPerFrame)));
            if (index > 0 && peaks[index] <= peaks[index - 1])
            {
                peaks[index] = Math.Min(sampleCount, peaks[index - 1] + 1);
            }
        }

        return peaks;
    }

    private static double EstimateUniformWindowSamples(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings,
        int offset,
        double nominalWindowSamples)
    {
        var referenceFrameCount = CountUniformFrames(data[0].Length, settings, offset, nominalWindowSamples);
        var span = settings.MaxFrames <= 5
            ? Math.Max(1.0, nominalWindowSamples * 0.01)
            : settings.MaxFrames <= 10
            ? Math.Max(4.0, nominalWindowSamples * 0.02)
            : Math.Max(4.0, nominalWindowSamples * 0.08);
        var minWindowSamples = Math.Max(2.0, nominalWindowSamples - span);
        var maxWindowSamples = nominalWindowSamples + span;
        var coarseStep = nominalWindowSamples < 256.0 ? 0.25 : 0.5;
        var bestWindowSamples = SearchUniformWindowSamples(
            data,
            waveform,
            settings,
            offset,
            minWindowSamples,
            maxWindowSamples,
            coarseStep,
            referenceFrameCount);
        var refinedWindowSamples = SearchUniformWindowSamples(
            data,
            waveform,
            settings,
            offset,
            Math.Max(2.0, bestWindowSamples - coarseStep),
            bestWindowSamples + coarseStep,
            coarseStep / 5.0,
            referenceFrameCount);
        return Math.Abs(refinedWindowSamples - nominalWindowSamples) <= 0.20
            ? nominalWindowSamples
            : refinedWindowSamples;
    }

    private static double SearchUniformWindowSamples(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings,
        int offset,
        double minWindowSamples,
        double maxWindowSamples,
        double step,
        int referenceFrameCount)
    {
        var bestWindowSamples = minWindowSamples;
        var bestScore = double.NegativeInfinity;
        for (var windowSamples = minWindowSamples; windowSamples <= maxWindowSamples + (0.5 * step); windowSamples += step)
        {
            var score = ScoreUniformCadence(
                data,
                waveform,
                settings,
                offset,
                windowSamples,
                maxFramesToScore: 12,
                includeFrameCountBonus: false);
            var frameCount = CountUniformFrames(data[0].Length, settings, offset, windowSamples);
            if (frameCount < referenceFrameCount)
            {
                score -= 5000.0 * (referenceFrameCount - frameCount);
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestWindowSamples = windowSamples;
            }
        }

        return bestWindowSamples;
    }

    private static int CountUniformFrames(
        int sampleCount,
        OfflineDemodulationSettings settings,
        int offset,
        double windowSamples)
    {
        var samplesPerFrame = windowSamples * settings.WindowsPerFrame;
        return samplesPerFrame <= 1.0 || sampleCount <= offset
            ? 0
            : Math.Min(settings.MaxFrames, (int)Math.Floor((sampleCount - offset) / samplesPerFrame));
    }

    private static UniformOffsetSelection FindBestUniformFrameOffset(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings,
        double windowSamples,
        bool includeFrameCountBonus)
    {
        var sampleCount = data[0].Length;
        var samplesPerFrame = windowSamples * settings.WindowsPerFrame;
        var maxOffset = Math.Min((int)Math.Round(windowSamples) - 1, (int)Math.Floor(sampleCount - samplesPerFrame));
        if (maxOffset <= 0)
        {
            var instability = ScoreUniformIntegrationInstability(data, waveform, settings, 0, windowSamples);
            return new UniformOffsetSelection(
                0,
                instability <= MaximumUniformIntegrationInstability,
                instability);
        }

        var stride = settings.MaxFrames <= 5
            ? Math.Max(1, (int)Math.Round(windowSamples / 25.0))
            : settings.MaxFrames <= 10
            ? Math.Max(1, (int)Math.Round(windowSamples / 50.0))
            : windowSamples <= 512 ? 1 : Math.Max(1, (int)Math.Round(windowSamples / 256.0));
        var candidates = new List<UniformOffsetCandidate>();
        for (var offset = 0; offset <= maxOffset; offset += stride)
        {
            candidates.Add(new UniformOffsetCandidate(
                offset,
                ScoreUniformCadence(
                    data,
                    waveform,
                    settings,
                    offset,
                    windowSamples,
                    maxFramesToScore: 8,
                    includeFrameCountBonus)));
        }

        if (maxOffset % stride != 0)
        {
            candidates.Add(new UniformOffsetCandidate(
                maxOffset,
                ScoreUniformCadence(
                    data,
                    waveform,
                    settings,
                    maxOffset,
                    windowSamples,
                    maxFramesToScore: 8,
                    includeFrameCountBonus)));
        }

        var bestTopologyScore = candidates.Max(candidate => candidate.TopologyScore);
        var equivalentCandidates = candidates
            .Where(candidate =>
                candidate.TopologyScore >= bestTopologyScore - UniformTopologyEquivalentScoreTolerance)
            .Select(candidate => candidate with
            {
                IntegrationInstability = ScoreUniformIntegrationInstability(
                    data,
                    waveform,
                    settings,
                    candidate.OffsetSamples,
                    windowSamples)
            })
            .ToArray();
        var bestCandidate = equivalentCandidates
            .OrderBy(candidate => candidate.IntegrationInstability)
            .ThenByDescending(candidate => candidate.TopologyScore)
            .ThenBy(candidate => candidate.OffsetSamples)
            .First();
        return new UniformOffsetSelection(
            bestCandidate.OffsetSamples,
            bestCandidate.IntegrationInstability <= MaximumUniformIntegrationInstability,
            bestCandidate.IntegrationInstability);
    }

    private static double ScoreUniformIntegrationInstability(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings,
        int offset,
        double windowSamples)
    {
        var samplesPerCycle = windowSamples / settings.ChannelCycles;
        var segmentLeft = offset;
        var segmentRight = ((int)Math.Round(offset + windowSamples)) - 1;
        var discards = ResolveWindowDiscards(settings, windowSamples, segmentRight - segmentLeft + 1);
        var innerLeft = segmentLeft + discards.LeadingSamples;
        var innerRight = segmentRight - discards.TrailingSamples;
        if (innerRight >= data[0].Length ||
            !TryResolveIntegrationComparisonRange(
                innerLeft,
                innerRight,
                samplesPerCycle,
                out _,
                out _,
                out _,
                out _))
        {
            return 0.0;
        }

        var availableFrameCount = Math.Min(
            settings.MaxFrames,
            (int)Math.Floor((data[0].Length - offset) / (windowSamples * settings.WindowsPerFrame)));
        if (availableFrameCount <= 0)
        {
            return double.PositiveInfinity;
        }

        var windowInstabilities = new List<double>(
            availableFrameCount * settings.WindowsPerFrame);
        for (var window = 0; window < availableFrameCount * settings.WindowsPerFrame; window++)
        {
            segmentLeft = (int)Math.Round(offset + (window * windowSamples));
            segmentRight = ((int)Math.Round(offset + ((window + 1) * windowSamples))) - 1;
            discards = ResolveWindowDiscards(settings, windowSamples, segmentRight - segmentLeft + 1);
            innerLeft = segmentLeft + discards.LeadingSamples;
            innerRight = segmentRight - discards.TrailingSamples;
            if (innerRight >= data[0].Length)
            {
                return double.PositiveInfinity;
            }

            if (!TryResolveIntegrationComparisonRange(
                    innerLeft,
                    innerRight,
                    samplesPerCycle,
                    out var firstLeft,
                    out var firstRight,
                    out var secondLeft,
                    out var secondRight))
            {
                return 0.0;
            }

            var firstHalf = ProjectWindowSingleFrequency(
                data,
                waveform,
                firstLeft,
                firstRight,
                settings.AdcLsbVolts);
            var secondHalf = ProjectWindowSingleFrequency(
                data,
                waveform,
                secondLeft,
                secondRight,
                settings.AdcLsbVolts);
            var channelDifferences = new double[ChannelCount];
            for (var channel = 0; channel < ChannelCount; channel++)
            {
                var first = firstHalf.Projections[channel];
                var second = secondHalf.Projections[channel];
                // At low excitation current, a few physical ADC counts can exceed
                // the relative 1% gate even though both half-window projections are
                // inside the converter noise/quantization floor. Preserve the 1%
                // settling test for resolved signals while giving unresolved signals
                // an explicit four-LSB absolute allowance in the configured AD range.
                var physicalNoiseFloorDenominator =
                    UniformIntegrationNoiseFloorAllowanceLsb * settings.AdcLsbVolts /
                    MaximumUniformIntegrationInstability;
                var denominator = Math.Max(
                    physicalNoiseFloorDenominator,
                    0.5 * (first.Magnitude() + second.Magnitude()));
                channelDifferences[channel] = (first - second).Magnitude() / denominator;
            }

            windowInstabilities.Add(Median(channelDifferences));
        }

        return Percentile(windowInstabilities, 90.0);
    }

    private static bool TryResolveIntegrationComparisonRange(
        int innerLeft,
        int innerRight,
        double samplesPerCycle,
        out int firstLeft,
        out int firstRight,
        out int secondLeft,
        out int secondRight)
    {
        firstLeft = 0;
        firstRight = -1;
        secondLeft = 0;
        secondRight = -1;
        var usableSamples = innerRight - innerLeft + 1;
        if (usableSamples <= 0 || !double.IsFinite(samplesPerCycle) || samplesPerCycle <= 0.0)
        {
            return false;
        }

        var completeCycles = (int)Math.Floor((usableSamples / samplesPerCycle) + 1e-9);
        var cyclesPerHalf = completeCycles / 2;
        if (cyclesPerHalf < 2)
        {
            return false;
        }

        var halfSamples = Math.Min(
            usableSamples / 2,
            (int)Math.Round(cyclesPerHalf * samplesPerCycle));
        if (halfSamples < 2)
        {
            return false;
        }

        firstLeft = innerLeft;
        firstRight = firstLeft + halfSamples - 1;
        secondRight = innerRight;
        secondLeft = secondRight - halfSamples + 1;
        return firstRight < secondLeft;
    }

    private static double ScoreUniformCadence(
        double[][] data,
        ReferenceWaveform waveform,
        OfflineDemodulationSettings settings,
        int offset,
        double windowSamples,
        int maxFramesToScore,
        bool includeFrameCountBonus)
    {
        var sampleCount = data[0].Length;
        var samplesPerFrame = windowSamples * settings.WindowsPerFrame;
        var frameCount = Math.Min(
            Math.Min(settings.MaxFrames, maxFramesToScore),
            (int)Math.Floor((sampleCount - offset) / samplesPerFrame));
        if (frameCount <= 0)
        {
            return double.NegativeInfinity;
        }

        var score = 0.0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var firstWindow = frame * settings.WindowsPerFrame;
            var analyses = new WindowAmplitudeAnalysis[settings.WindowsPerFrame];
            for (var window = 0; window < settings.WindowsPerFrame; window++)
            {
                var absoluteWindow = firstWindow + window;
                var segmentLeft = (int)Math.Round(offset + (absoluteWindow * windowSamples));
                var segmentRight = ((int)Math.Round(offset + ((absoluteWindow + 1) * windowSamples))) - 1;
                var scoreDiscard = ResolveWindowDiscards(settings, windowSamples, segmentRight - segmentLeft + 1);
                var innerLeft = segmentLeft + scoreDiscard.LeadingSamples;
                var innerRight = segmentRight - scoreDiscard.TrailingSamples;
                analyses[window] = AnalyzeAmplitudes(ProjectWindow(data, waveform, innerLeft, innerRight, settings).Projections);
            }

            var sequence = ChooseReferenceSequence(analyses);
            for (var window = 0; window < analyses.Length; window++)
            {
                var analysis = analyses[window];
                var expected = sequence.ExpectedChannel(window);
                var top3Contiguous = analysis.TripletCenterChannel >= 0;
                var expectedInTop3 = analysis.Top3Channels.Contains(expected);

                score += top3Contiguous ? 100.0 : -250.0;
                if (analysis.TripletCenterChannel == expected)
                {
                    score += 100.0;
                }
                else if (expectedInTop3)
                {
                    score += 45.0;
                }
                else
                {
                    score -= 100.0;
                }

                if (analysis.Top1IsTripletCenter)
                {
                    score += 25.0;
                }

                if (analysis.PeakToBackgroundRatio > 1.0)
                {
                    score += Math.Min(25.0, 8.0 * Math.Log(analysis.PeakToBackgroundRatio, 2.0));
                }

                if (analysis.Top1Channel >= 0)
                {
                    score += Math.Min(200.0, 1000.0 * analysis.Amplitudes[analysis.Top1Channel]);
                }

                var totalAmplitude = analysis.Amplitudes.Sum();
                if (totalAmplitude > double.Epsilon)
                {
                    var top3Amplitude = analysis.Top3Channels.Sum(channel => analysis.Amplitudes[channel]);
                    score += 150.0 * top3Amplitude / totalAmplitude;
                }

                var orderedAmplitudes = analysis.Amplitudes.OrderDescending().ToArray();
                if (orderedAmplitudes.Length >= 4 && orderedAmplitudes[3] > double.Epsilon)
                {
                    score += Math.Min(120.0, 30.0 * orderedAmplitudes[2] / orderedAmplitudes[3]);
                }
            }
        }

        return (score / frameCount) + (includeFrameCountBonus ? 10000.0 * frameCount : 0.0);
    }

    private static IReadOnlyList<DemodulatedFrame> DemodulateFrames(
        double[][] data,
        ReferenceWaveform waveform,
        IReadOnlyList<int> peakLocations,
        OfflineDemodulationSettings settings)
    {
        if (peakLocations.Count < 2)
        {
            return [];
        }

        var sampleCount = data[0].Length;
        var frameCount = Math.Min(settings.MaxFrames, peakLocations.Count - 1);
        var frames = new DemodulatedFrame?[frameCount];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = settings.MaxDegreeOfParallelism
        };
        Parallel.For(0, frameCount, parallelOptions, frameIndex =>
        {
            var frameStart = peakLocations[frameIndex];
            var frameEnd = peakLocations[frameIndex + 1];
            if (frameEnd <= frameStart)
            {
                return;
            }

            var frameLength = frameEnd - frameStart;
            var segmentLength = (double)frameLength / settings.WindowsPerFrame;
            var windows = new WindowProjection[settings.WindowsPerFrame];
            var analyses = new WindowAmplitudeAnalysis[settings.WindowsPerFrame];

            for (var window = 0; window < settings.WindowsPerFrame; window++)
            {
                var segmentLeft = (int)Math.Round(frameStart + (window * segmentLength));
                var segmentRight = ((int)Math.Round(frameStart + ((window + 1) * segmentLength))) - 1;
                segmentLeft = Math.Max(0, segmentLeft);
                segmentRight = Math.Min(sampleCount - 1, segmentRight);

                var discardSamples = ResolveWindowDiscards(settings, segmentLength, segmentRight - segmentLeft + 1);
                var innerLeft = segmentLeft + discardSamples.LeadingSamples;
                var innerRight = segmentRight - discardSamples.TrailingSamples;
                var projection = ProjectWindow(data, waveform, innerLeft, innerRight, settings);
                windows[window] = projection;
                analyses[window] = AnalyzeAmplitudes(projection.Projections);
            }

            var sequence = ChooseReferenceSequence(analyses);
            frames[frameIndex] = BuildFrame(frameIndex + 1, frameStart, frameEnd, windows, analyses, sequence, settings);
        });

        return frames
            .Where(frame => frame is not null)
            .Cast<DemodulatedFrame>()
            .ToArray();
    }

    private static DemodulatedFrame BuildFrame(
        int frameNumber,
        int frameStart,
        int frameEnd,
        IReadOnlyList<WindowProjection> windows,
        IReadOnlyList<WindowAmplitudeAnalysis> analyses,
        ReferenceSequence sequence,
        OfflineDemodulationSettings settings)
    {
        var amplitudeSums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var realSums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var imaginarySums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var fullAmplitudeSums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var fullRealSums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var fullImaginarySums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var diagnosticAmplitudeSums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var diagnosticRealSums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var diagnosticImaginarySums = new double[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var diagnosticFullAmplitudeSums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var diagnosticFullRealSums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var diagnosticFullImaginarySums = new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var fullSaturationCounts = new int[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var acceptedCounts = new int[ChannelCount];
        var diagnosticCounts = new int[ChannelCount];
        var frequencyTemplate = windows.FirstOrDefault(window => window.FrequencyProjections is { Length: > 1 });
        var frequencyHz = frequencyTemplate?.FrequenciesHz ?? [];
        var frequencyAmplitudeSums = CreateFrequencyMatrices(frequencyHz.Count);
        var frequencyRealSums = CreateFrequencyMatrices(frequencyHz.Count);
        var frequencyImaginarySums = CreateFrequencyMatrices(frequencyHz.Count);
        var frequencyCounts = new int[frequencyHz.Count, ChannelCount];
        var stimulationCounts = new int[ChannelCount, 3];
        var qualities = new List<DemodulatedWindowQuality>(windows.Count);

        for (var window = 0; window < windows.Count; window++)
        {
            var expectedReference = sequence.ExpectedChannel(window);
            var quality = ClassifyWindow(window, expectedReference, analyses[window], windows[window], settings);
            qualities.Add(quality);
            stimulationCounts[expectedReference, (int)quality.State]++;

            for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
            {
                var measurementChannel = Mod(expectedReference + relativeChannel, ChannelCount);
                fullSaturationCounts[expectedReference, relativeChannel] =
                    windows[window].ChannelSaturationCounts[measurementChannel];
            }

            var reference = ResolveEidorsReference(
                windows[window].Projections,
                expectedReference);
            var referenceMagnitude = reference.Magnitude();
            if (referenceMagnitude <= double.Epsilon)
            {
                continue;
            }

            var rotator = reference.Conjugate() / referenceMagnitude;

            for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
            {
                var measurementChannel = Mod(expectedReference + relativeChannel, ChannelCount);
                var demodulated = windows[window].Projections[measurementChannel] * rotator;
                diagnosticFullRealSums[expectedReference, relativeChannel] += demodulated.Real;
                diagnosticFullImaginarySums[expectedReference, relativeChannel] += demodulated.Imaginary;
                diagnosticFullAmplitudeSums[expectedReference, relativeChannel] += demodulated.Magnitude();
                if (!quality.Rejected)
                {
                    fullRealSums[expectedReference, relativeChannel] += demodulated.Real;
                    fullImaginarySums[expectedReference, relativeChannel] += demodulated.Imaginary;
                    fullAmplitudeSums[expectedReference, relativeChannel] += demodulated.Magnitude();
                }
            }

            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var measurementChannel = Mod(expectedReference + relativeChannel, ChannelCount);
                var column = relativeChannel - 2;
                var demodulated = windows[window].Projections[measurementChannel] * rotator;
                diagnosticRealSums[expectedReference, column] += demodulated.Real;
                diagnosticImaginarySums[expectedReference, column] += demodulated.Imaginary;
                diagnosticAmplitudeSums[expectedReference, column] += demodulated.Magnitude();
                if (!quality.Rejected)
                {
                    realSums[expectedReference, column] += demodulated.Real;
                    imaginarySums[expectedReference, column] += demodulated.Imaginary;
                    amplitudeSums[expectedReference, column] += demodulated.Magnitude();
                }
            }

            diagnosticCounts[expectedReference]++;
            if (quality.Rejected)
            {
                continue;
            }

            acceptedCounts[expectedReference]++;
            AccumulateFrequencyFrames(
                windows[window],
                frequencyHz.Count,
                expectedReference,
                frequencyAmplitudeSums,
                frequencyRealSums,
                frequencyImaginarySums,
                frequencyCounts);
        }

        var amplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var real = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var imaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var fullAmplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullReal = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullImaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var diagnosticAmplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var diagnosticReal = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var diagnosticImaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var diagnosticSampleCounts = new int[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var diagnosticFullAmplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var diagnosticFullReal = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var diagnosticFullImaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var diagnosticFullSampleCounts = new int[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var frequencyFrames = CreateDemodulatedFrequencyFrames(
            frequencyHz,
            frequencyAmplitudeSums,
            frequencyRealSums,
            frequencyImaginarySums,
            frequencyCounts);

        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            var diagnosticCount = diagnosticCounts[stimulation];
            if (diagnosticCount > 0)
            {
                for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
                {
                    diagnosticAmplitudes[stimulation, column] = diagnosticAmplitudeSums[stimulation, column] / diagnosticCount;
                    diagnosticReal[stimulation, column] = diagnosticRealSums[stimulation, column] / diagnosticCount;
                    diagnosticImaginary[stimulation, column] = diagnosticImaginarySums[stimulation, column] / diagnosticCount;
                    diagnosticSampleCounts[stimulation, column] = diagnosticCount;
                }

                for (var column = 0; column < DemodulatedFrame.FullMeasurementsPerStimulation; column++)
                {
                    diagnosticFullAmplitudes[stimulation, column] = diagnosticFullAmplitudeSums[stimulation, column] / diagnosticCount;
                    diagnosticFullReal[stimulation, column] = diagnosticFullRealSums[stimulation, column] / diagnosticCount;
                    diagnosticFullImaginary[stimulation, column] = diagnosticFullImaginarySums[stimulation, column] / diagnosticCount;
                    diagnosticFullSampleCounts[stimulation, column] = diagnosticCount;
                }
            }

            var count = acceptedCounts[stimulation];
            if (count == 0)
            {
                continue;
            }

            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                amplitudes[stimulation, column] = amplitudeSums[stimulation, column] / count;
                real[stimulation, column] = realSums[stimulation, column] / count;
                imaginary[stimulation, column] = imaginarySums[stimulation, column] / count;
            }

            for (var column = 0; column < DemodulatedFrame.FullMeasurementsPerStimulation; column++)
            {
                fullAmplitudes[stimulation, column] = fullAmplitudeSums[stimulation, column] / count;
                fullReal[stimulation, column] = fullRealSums[stimulation, column] / count;
                fullImaginary[stimulation, column] = fullImaginarySums[stimulation, column] / count;
            }
        }

        var diagnosticObservation = new DemodulatedObservationAggregate(
            diagnosticAmplitudes,
            diagnosticReal,
            diagnosticImaginary,
            diagnosticSampleCounts,
            diagnosticFullAmplitudes,
            diagnosticFullReal,
            diagnosticFullImaginary,
            diagnosticFullSampleCounts,
            contributingFrameCount: diagnosticCounts.Any(count => count > 0) ? 1 : 0,
            contributingWindowCount: diagnosticCounts.Sum(),
            totalWindowCount: windows.Count,
            includesRejectedWindows: true);

        return new DemodulatedFrame(
            frameNumber,
            frameStart,
            frameEnd,
            amplitudes,
            real,
            imaginary,
            qualities,
            stimulationCounts,
            fullAmplitudes,
            fullReal,
            fullImaginary,
            fullSaturationCounts,
            frequencyFrames,
            diagnosticObservation);
    }

    private static DemodulatedFrameAverage AverageValidFrames(
        IReadOnlyList<DemodulatedFrame> frames,
        bool includeCorrectedFrames)
    {
        var accepted = frames.Where(frame => IsAverageUsableFrame(frame, includeCorrectedFrames)).ToArray();
        var rejected = frames.Where(frame => !accepted.Contains(frame)).Select(frame => frame.FrameNumber).ToArray();
        var amplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var real = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var imaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var counts = new int[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var fullAmplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullReal = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullImaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullCounts = new int[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];

        if (accepted.Length == 0)
        {
            return new DemodulatedFrameAverage([], rejected, amplitudes, real, imaginary, counts, fullAmplitudes, fullReal, fullImaginary, fullCounts, []);
        }

        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                var ampSum = 0.0;
                var realSum = 0.0;
                var imaginarySum = 0.0;
                var count = 0;
                foreach (var frame in accepted)
                {
                    if (!double.IsFinite(frame.Amplitudes[stimulation, column]) ||
                        !double.IsFinite(frame.RealComponents[stimulation, column]) ||
                        !double.IsFinite(frame.ImaginaryComponents[stimulation, column]))
                    {
                        continue;
                    }

                    ampSum += frame.Amplitudes[stimulation, column];
                    realSum += frame.RealComponents[stimulation, column];
                    imaginarySum += frame.ImaginaryComponents[stimulation, column];
                    count++;
                }

                if (count == 0)
                {
                    continue;
                }

                amplitudes[stimulation, column] = ampSum / count;
                real[stimulation, column] = realSum / count;
                imaginary[stimulation, column] = imaginarySum / count;
                counts[stimulation, column] = count;
            }

            for (var column = 0; column < DemodulatedFrame.FullMeasurementsPerStimulation; column++)
            {
                var ampSum = 0.0;
                var realSum = 0.0;
                var imaginarySum = 0.0;
                var count = 0;
                foreach (var frame in accepted)
                {
                    if (frame.FullAmplitudes is null ||
                        frame.FullRealComponents is null ||
                        frame.FullImaginaryComponents is null ||
                        !double.IsFinite(frame.FullAmplitudes[stimulation, column]) ||
                        !double.IsFinite(frame.FullRealComponents[stimulation, column]) ||
                        !double.IsFinite(frame.FullImaginaryComponents[stimulation, column]))
                    {
                        continue;
                    }

                    ampSum += frame.FullAmplitudes[stimulation, column];
                    realSum += frame.FullRealComponents[stimulation, column];
                    imaginarySum += frame.FullImaginaryComponents[stimulation, column];
                    count++;
                }

                if (count == 0)
                {
                    continue;
                }

                fullAmplitudes[stimulation, column] = ampSum / count;
                fullReal[stimulation, column] = realSum / count;
                fullImaginary[stimulation, column] = imaginarySum / count;
                fullCounts[stimulation, column] = count;
            }
        }

        return new DemodulatedFrameAverage(
            accepted.Select(frame => frame.FrameNumber).ToArray(),
            rejected,
            amplitudes,
            real,
            imaginary,
            counts,
            fullAmplitudes,
            fullReal,
            fullImaginary,
            fullCounts,
            AverageFrequencyFrames(accepted));
    }

    private static DemodulatedObservationAggregate AggregateTrustedPartialObservations(
        IReadOnlyList<DemodulatedFrame> frames)
    {
        return AggregateObservations(
            frames.Select(CreateTrustedPartialObservation).ToArray(),
            includesRejectedWindows: false);
    }

    private static DemodulatedObservationAggregate AggregateDiagnosticObservations(
        IReadOnlyList<DemodulatedFrame> frames)
    {
        return AggregateObservations(
            frames
                .Select(frame => frame.DiagnosticObservation)
                .Where(observation => observation is not null)
                .Cast<DemodulatedObservationAggregate>()
                .ToArray(),
            includesRejectedWindows: true);
    }

    private static DemodulatedObservationAggregate CreateTrustedPartialObservation(DemodulatedFrame frame)
    {
        var counts = new int[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var fullCounts = new int[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];
        var contributingWindows = 0;
        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            var count = frame.WindowQualities.Count(quality =>
                quality.ExpectedReferenceChannel == stimulation && !quality.Rejected);
            contributingWindows += count;
            if (count == 0)
            {
                continue;
            }

            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                if (double.IsFinite(frame.Amplitudes[stimulation, column]))
                {
                    counts[stimulation, column] = count;
                }
            }

            for (var column = 0; column < DemodulatedFrame.FullMeasurementsPerStimulation; column++)
            {
                if (frame.FullAmplitudes is not null && double.IsFinite(frame.FullAmplitudes[stimulation, column]))
                {
                    fullCounts[stimulation, column] = count;
                }
            }
        }

        return new DemodulatedObservationAggregate(
            frame.Amplitudes,
            frame.RealComponents,
            frame.ImaginaryComponents,
            counts,
            frame.FullAmplitudes ?? CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation),
            frame.FullRealComponents ?? CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation),
            frame.FullImaginaryComponents ?? CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation),
            fullCounts,
            contributingFrameCount: contributingWindows > 0 ? 1 : 0,
            contributingWindowCount: contributingWindows,
            totalWindowCount: frame.WindowQualities.Count,
            includesRejectedWindows: false);
    }

    private static DemodulatedObservationAggregate AggregateObservations(
        IReadOnlyList<DemodulatedObservationAggregate> observations,
        bool includesRejectedWindows)
    {
        var amplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var real = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var imaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.MeasurementsPerStimulation);
        var counts = new int[ChannelCount, DemodulatedFrame.MeasurementsPerStimulation];
        var fullAmplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullReal = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullImaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
        var fullCounts = new int[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation];

        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                AggregateCell(
                    observations,
                    observation => observation.Amplitudes[stimulation, column],
                    observation => observation.RealComponents[stimulation, column],
                    observation => observation.ImaginaryComponents[stimulation, column],
                    observation => observation.SampleCounts[stimulation, column],
                    out amplitudes[stimulation, column],
                    out real[stimulation, column],
                    out imaginary[stimulation, column],
                    out counts[stimulation, column]);
            }

            for (var column = 0; column < DemodulatedFrame.FullMeasurementsPerStimulation; column++)
            {
                AggregateCell(
                    observations,
                    observation => observation.FullAmplitudes[stimulation, column],
                    observation => observation.FullRealComponents[stimulation, column],
                    observation => observation.FullImaginaryComponents[stimulation, column],
                    observation => observation.FullSampleCounts[stimulation, column],
                    out fullAmplitudes[stimulation, column],
                    out fullReal[stimulation, column],
                    out fullImaginary[stimulation, column],
                    out fullCounts[stimulation, column]);
            }
        }

        return new DemodulatedObservationAggregate(
            amplitudes,
            real,
            imaginary,
            counts,
            fullAmplitudes,
            fullReal,
            fullImaginary,
            fullCounts,
            contributingFrameCount: observations.Count(observation => observation.ContributingFrameCount > 0),
            contributingWindowCount: observations.Sum(observation => observation.ContributingWindowCount),
            totalWindowCount: observations.Sum(observation => observation.TotalWindowCount),
            includesRejectedWindows: includesRejectedWindows);
    }

    private static void AggregateCell(
        IReadOnlyList<DemodulatedObservationAggregate> observations,
        Func<DemodulatedObservationAggregate, double> amplitudeSelector,
        Func<DemodulatedObservationAggregate, double> realSelector,
        Func<DemodulatedObservationAggregate, double> imaginarySelector,
        Func<DemodulatedObservationAggregate, int> countSelector,
        out double amplitude,
        out double real,
        out double imaginary,
        out int count)
    {
        var contributing = observations
            .Where(observation =>
                countSelector(observation) > 0 &&
                double.IsFinite(amplitudeSelector(observation)) &&
                double.IsFinite(realSelector(observation)) &&
                double.IsFinite(imaginarySelector(observation)))
            .ToArray();
        if (contributing.Length == 0)
        {
            amplitude = double.NaN;
            real = double.NaN;
            imaginary = double.NaN;
            count = 0;
            return;
        }

        amplitude = Median(contributing.Select(amplitudeSelector).ToArray());
        real = Median(contributing.Select(realSelector).ToArray());
        imaginary = Median(contributing.Select(imaginarySelector).ToArray());
        count = contributing.Sum(countSelector);
    }

    private static double[][,] CreateFrequencyMatrices(int frequencyCount)
    {
        return Enumerable.Range(0, frequencyCount)
            .Select(_ => new double[ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation])
            .ToArray();
    }

    private static void AccumulateFrequencyFrames(
        WindowProjection window,
        int frequencyCount,
        int expectedReference,
        IReadOnlyList<double[,]> amplitudeSums,
        IReadOnlyList<double[,]> realSums,
        IReadOnlyList<double[,]> imaginarySums,
        int[,] counts)
    {
        if (frequencyCount == 0 || window.FrequencyProjections is null)
        {
            return;
        }

        for (var frequencyIndex = 0; frequencyIndex < frequencyCount; frequencyIndex++)
        {
            var projections = window.FrequencyProjections[frequencyIndex];
            var reference = ResolveEidorsReference(projections, expectedReference);
            var referenceMagnitude = reference.Magnitude();
            if (referenceMagnitude <= double.Epsilon)
            {
                continue;
            }

            var rotator = reference.Conjugate() / referenceMagnitude;
            for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
            {
                var measurementChannel = Mod(expectedReference + relativeChannel, ChannelCount);
                var demodulated = projections[measurementChannel] * rotator;
                realSums[frequencyIndex][expectedReference, relativeChannel] += demodulated.Real;
                imaginarySums[frequencyIndex][expectedReference, relativeChannel] += demodulated.Imaginary;
                amplitudeSums[frequencyIndex][expectedReference, relativeChannel] += demodulated.Magnitude();
            }

            counts[frequencyIndex, expectedReference]++;
        }
    }

    private static IReadOnlyList<DemodulatedFrequencyFrame> CreateDemodulatedFrequencyFrames(
        IReadOnlyList<double> frequencyHz,
        IReadOnlyList<double[,]> amplitudeSums,
        IReadOnlyList<double[,]> realSums,
        IReadOnlyList<double[,]> imaginarySums,
        int[,] counts)
    {
        if (frequencyHz.Count == 0)
        {
            return [];
        }

        var frames = new List<DemodulatedFrequencyFrame>(frequencyHz.Count);
        for (var frequencyIndex = 0; frequencyIndex < frequencyHz.Count; frequencyIndex++)
        {
            var amplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            var real = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            var imaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
            {
                var count = counts[frequencyIndex, stimulation];
                if (count == 0)
                {
                    continue;
                }

                for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
                {
                    amplitudes[stimulation, relativeChannel] = amplitudeSums[frequencyIndex][stimulation, relativeChannel] / count;
                    real[stimulation, relativeChannel] = realSums[frequencyIndex][stimulation, relativeChannel] / count;
                    imaginary[stimulation, relativeChannel] = imaginarySums[frequencyIndex][stimulation, relativeChannel] / count;
                }
            }

            frames.Add(new DemodulatedFrequencyFrame(frequencyHz[frequencyIndex], amplitudes, real, imaginary));
        }

        return frames;
    }

    private static ComplexValue ResolveEidorsReference(
        IReadOnlyList<ComplexValue> projections,
        int firstElectrode)
    {
        // PyEIDORS/EIDORS {ad}: first=-I, next=+I.  Use the acquired
        // positive-current endpoint voltage as phase origin. Each projection
        // is already the acquired adjacent differential V(first)-V(next), not
        // a single-electrode potential, so reverse that same channel's
        // endpoints here. No display/output sign patch is applied downstream.
        var acquiredFirstMinusNext = projections[firstElectrode];
        return new ComplexValue(0.0, 0.0) - acquiredFirstMinusNext;
    }

    private static IReadOnlyList<DemodulatedFrequencyFrame> AverageFrequencyFrames(
        IReadOnlyList<DemodulatedFrame> acceptedFrames)
    {
        var frequencyHz = acceptedFrames
            .SelectMany(frame => frame.FrequencyFrames ?? [])
            .Select(frame => frame.FrequencyHz)
            .Distinct()
            .Order()
            .ToArray();
        if (frequencyHz.Length == 0)
        {
            return [];
        }

        var averages = new List<DemodulatedFrequencyFrame>(frequencyHz.Length);
        foreach (var frequency in frequencyHz)
        {
            var amplitudes = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            var real = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            var imaginary = CreateNanMatrix(ChannelCount, DemodulatedFrame.FullMeasurementsPerStimulation);
            for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
            {
                for (var relativeChannel = 0; relativeChannel < DemodulatedFrame.FullMeasurementsPerStimulation; relativeChannel++)
                {
                    var ampSum = 0.0;
                    var realSum = 0.0;
                    var imaginarySum = 0.0;
                    var count = 0;
                    foreach (var frame in acceptedFrames)
                    {
                        var frequencyFrame = frame.FrequencyFrames?
                            .FirstOrDefault(item => Math.Abs(item.FrequencyHz - frequency) <= 1.0e-9);
                        if (frequencyFrame is null ||
                            !double.IsFinite(frequencyFrame.FullAmplitudes[stimulation, relativeChannel]) ||
                            !double.IsFinite(frequencyFrame.FullRealComponents[stimulation, relativeChannel]) ||
                            !double.IsFinite(frequencyFrame.FullImaginaryComponents[stimulation, relativeChannel]))
                        {
                            continue;
                        }

                        ampSum += frequencyFrame.FullAmplitudes[stimulation, relativeChannel];
                        realSum += frequencyFrame.FullRealComponents[stimulation, relativeChannel];
                        imaginarySum += frequencyFrame.FullImaginaryComponents[stimulation, relativeChannel];
                        count++;
                    }

                    if (count == 0)
                    {
                        continue;
                    }

                    amplitudes[stimulation, relativeChannel] = ampSum / count;
                    real[stimulation, relativeChannel] = realSum / count;
                    imaginary[stimulation, relativeChannel] = imaginarySum / count;
                }
            }

            averages.Add(new DemodulatedFrequencyFrame(frequency, amplitudes, real, imaginary));
        }

        return averages;
    }

    private static bool IsAverageUsableFrame(DemodulatedFrame frame, bool includeCorrectedFrames)
    {
        if (frame.WindowQualities.Count != DemodulatedFrame.StimulationCount)
        {
            return false;
        }

        if (includeCorrectedFrames)
        {
            return frame.WindowQualities.All(quality => !quality.Rejected);
        }

        return frame.WindowQualities.All(quality =>
            quality.State == DemodulatedWindowQualityState.Valid &&
            quality.Top3Contiguous &&
            quality.Top1IsTripletCenter &&
            quality.TripletCenterChannel == quality.ExpectedReferenceChannel &&
            !quality.Rejected);
    }

    private static WindowProjection ProjectWindow(
        double[][] data,
        ReferenceWaveform waveform,
        int innerLeft,
        int innerRight,
        OfflineDemodulationSettings settings)
    {
        if (settings.InterferenceFrequencyHz.Count > 0)
        {
            return ProjectWindowMultiFrequency(data, innerLeft, innerRight, settings);
        }

        return ProjectWindowSingleFrequency(data, waveform, innerLeft, innerRight, settings.AdcLsbVolts);
    }

    private static WindowProjection ProjectWindowSingleFrequency(
        double[][] data,
        ReferenceWaveform waveform,
        int innerLeft,
        int innerRight,
        double countsToVolts)
    {
        if (innerRight <= innerLeft)
        {
            return new WindowProjection(
                Enumerable.Repeat(new ComplexValue(0, 0), ChannelCount).ToArray(),
                0,
                new int[ChannelCount],
                [],
                null);
        }

        var length = innerRight - innerLeft + 1;
        var norm = 2.0 / length * countsToVolts;
        var projections = new ComplexValue[ChannelCount];
        var saturationCount = 0;
        var channelSaturationCounts = new int[ChannelCount];

        for (var channel = 0; channel < ChannelCount; channel++)
        {
            var mean = 0.0;
            for (var sample = 0; sample < length; sample++)
            {
                var value = data[channel][innerLeft + sample];
                mean += value;
                if (value <= 0 || value >= ushort.MaxValue)
                {
                    saturationCount++;
                    channelSaturationCounts[channel]++;
                }
            }

            mean /= length;

            var real = 0.0;
            var imaginary = 0.0;
            for (var sample = 0; sample < length; sample++)
            {
                var absoluteSample = innerLeft + sample;
                var centered = data[channel][absoluteSample] - mean;
                real += centered * waveform.Cos[absoluteSample];
                imaginary -= centered * waveform.Sin[absoluteSample];
            }

            projections[channel] = new ComplexValue(norm * real, norm * imaginary);
        }

        return new WindowProjection(projections, saturationCount, channelSaturationCounts, [], null);
    }

    private static WindowProjection ProjectWindowMultiFrequency(
        double[][] data,
        int innerLeft,
        int innerRight,
        OfflineDemodulationSettings settings)
    {
        if (innerRight <= innerLeft)
        {
            return new WindowProjection(
                Enumerable.Repeat(new ComplexValue(0, 0), ChannelCount).ToArray(),
                0,
                new int[ChannelCount],
                [],
                null);
        }

        var length = innerRight - innerLeft + 1;
        var frequencies = CreateProjectionFrequencies(settings);
        var basisCount = 1 + (2 * frequencies.Length);
        if (length <= basisCount)
        {
            return ProjectWindowSingleFrequency(
                data,
                ReferenceWaveform.Create(data[0].Length, settings),
                innerLeft,
                innerRight,
                settings.AdcLsbVolts);
        }

        var gram = new double[basisCount, basisCount];
        var basisRows = new double[length][];
        for (var sample = 0; sample < length; sample++)
        {
            var absoluteSample = innerLeft + sample;
            var basis = new double[basisCount];
            basis[0] = 1.0;
            for (var frequencyIndex = 0; frequencyIndex < frequencies.Length; frequencyIndex++)
            {
                var phase = 2.0 * Pi * frequencies[frequencyIndex] * absoluteSample / settings.SampleRateHz;
                basis[1 + (2 * frequencyIndex)] = Math.Cos(phase);
                basis[2 + (2 * frequencyIndex)] = Math.Sin(phase);
            }

            basisRows[sample] = basis;
            for (var row = 0; row < basisCount; row++)
            {
                for (var column = 0; column <= row; column++)
                {
                    gram[row, column] += basis[row] * basis[column];
                }
            }
        }

        for (var row = 0; row < basisCount; row++)
        {
            for (var column = row + 1; column < basisCount; column++)
            {
                gram[row, column] = gram[column, row];
            }
        }

        var projections = new ComplexValue[ChannelCount];
        var frequencyProjections = frequencies
            .Select(_ => new ComplexValue[ChannelCount])
            .ToArray();
        var saturationCount = 0;
        var channelSaturationCounts = new int[ChannelCount];
        for (var channel = 0; channel < ChannelCount; channel++)
        {
            var rhs = new double[basisCount];
            for (var sample = 0; sample < length; sample++)
            {
                var value = data[channel][innerLeft + sample];
                if (value <= 0 || value >= ushort.MaxValue)
                {
                    saturationCount++;
                    channelSaturationCounts[channel]++;
                }

                var volts = value * settings.AdcLsbVolts;
                var basis = basisRows[sample];
                for (var index = 0; index < basisCount; index++)
                {
                    rhs[index] += volts * basis[index];
                }
            }

            if (TrySolveLinearSystem(gram, rhs, out var coefficients))
            {
                for (var frequencyIndex = 0; frequencyIndex < frequencies.Length; frequencyIndex++)
                {
                    frequencyProjections[frequencyIndex][channel] = new ComplexValue(
                        coefficients[1 + (2 * frequencyIndex)],
                        -coefficients[2 + (2 * frequencyIndex)]);
                }

                projections[channel] = frequencyProjections[0][channel];
            }
            else
            {
                projections[channel] = new ComplexValue(0, 0);
            }
        }

        return new WindowProjection(projections, saturationCount, channelSaturationCounts, frequencies, frequencyProjections);
    }

    private static double[] CreateProjectionFrequencies(OfflineDemodulationSettings settings)
    {
        return new[] { settings.ExcitationFrequencyHz }
            .Concat(settings.InterferenceFrequencyHz)
            .Where(frequency => double.IsFinite(frequency) && frequency > 0.0)
            .Distinct()
            .ToArray();
    }

    private static bool TrySolveLinearSystem(double[,] matrix, double[] rhs, out double[] solution)
    {
        var size = rhs.Length;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, size] = rhs[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var pivotRow = pivot;
            var pivotMagnitude = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var magnitude = Math.Abs(augmented[row, pivot]);
                if (magnitude > pivotMagnitude)
                {
                    pivotMagnitude = magnitude;
                    pivotRow = row;
                }
            }

            if (pivotMagnitude <= 1e-10)
            {
                solution = [];
                return false;
            }

            if (pivotRow != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (augmented[pivot, column], augmented[pivotRow, column]) =
                        (augmented[pivotRow, column], augmented[pivot, column]);
                }
            }

            var divisor = augmented[pivot, pivot];
            for (var column = pivot; column <= size; column++)
            {
                augmented[pivot, column] /= divisor;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                if (Math.Abs(factor) <= double.Epsilon)
                {
                    continue;
                }

                for (var column = pivot; column <= size; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        solution = new double[size];
        for (var row = 0; row < size; row++)
        {
            solution[row] = augmented[row, size];
        }

        return solution.All(double.IsFinite);
    }

    private static DemodulationWindowDiscard ResolveWindowDiscards(
        OfflineDemodulationSettings settings,
        double windowSamples,
        int segmentLength)
    {
        return settings.ResolveWindowDiscard(windowSamples, segmentLength);
    }

    private static WindowAmplitudeAnalysis AnalyzeAmplitudes(IReadOnlyList<ComplexValue> projections)
    {
        var amplitudes = projections.Select(projection => projection.Magnitude()).ToArray();
        var topChannels = Enumerable.Range(0, ChannelCount)
            .OrderByDescending(channel => amplitudes[channel])
            .Take(3)
            .ToArray();
        var topSet = topChannels.ToHashSet();
        var tripletCenter = FindTripletCenter(topSet);
        var background = Enumerable.Range(0, ChannelCount)
            .Where(channel => !topSet.Contains(channel))
            .Select(channel => amplitudes[channel])
            .ToArray();
        var backgroundLevel = Median(background);
        var peakToBackgroundRatio = backgroundLevel <= double.Epsilon
            ? amplitudes[topChannels[0]] > double.Epsilon ? double.PositiveInfinity : 0.0
            : amplitudes[topChannels[0]] / backgroundLevel;

        return new WindowAmplitudeAnalysis(
            amplitudes,
            topChannels,
            tripletCenter,
            tripletCenter >= 0 && topChannels[0] == tripletCenter,
            peakToBackgroundRatio);
    }

    private static ReferenceSequence ChooseReferenceSequence(IReadOnlyList<WindowAmplitudeAnalysis> analyses)
    {
        var bestScore = int.MinValue;
        var bestCenterMatches = -1;
        var bestTop3Matches = -1;
        var bestOffset = 0;
        var bestDirection = 1;

        foreach (var direction in new[] { 1, -1 })
        {
            for (var offset = 0; offset < ChannelCount; offset++)
            {
                var score = 0;
                var centerMatches = 0;
                var top3Matches = 0;
                for (var window = 0; window < analyses.Count; window++)
                {
                    var expected = Mod(offset + (direction * window), ChannelCount);
                    if (analyses[window].Top3Channels.Contains(expected))
                    {
                        score += 2;
                        top3Matches++;
                    }

                    if (analyses[window].TripletCenterChannel == expected)
                    {
                        score += 4;
                        centerMatches++;
                    }

                    if (analyses[window].Top1Channel == expected)
                    {
                        score++;
                    }
                }

                if (score > bestScore ||
                    (score == bestScore && centerMatches > bestCenterMatches) ||
                    (score == bestScore && centerMatches == bestCenterMatches && top3Matches > bestTop3Matches))
                {
                    bestScore = score;
                    bestCenterMatches = centerMatches;
                    bestTop3Matches = top3Matches;
                    bestOffset = offset;
                    bestDirection = direction;
                }
            }
        }

        return new ReferenceSequence(bestOffset, bestDirection);
    }

    private static DemodulatedWindowQuality ClassifyWindow(
        int windowIndex,
        int expectedReferenceChannel,
        WindowAmplitudeAnalysis analysis,
        WindowProjection projection,
        OfflineDemodulationSettings settings)
    {
        var top3Contiguous = analysis.TripletCenterChannel >= 0;
        var expectedInTop3 = analysis.Top3Channels.Contains(expectedReferenceChannel);
        var top1IsTripletCenter = top3Contiguous && analysis.Top1IsTripletCenter;
        var referenceAmplitude = projection.Projections[expectedReferenceChannel].Magnitude();
        var state = DemodulatedWindowQualityState.Valid;
        var reason = DemodulatedWindowRejectReason.None;

        if (!top3Contiguous)
        {
            state = DemodulatedWindowQualityState.Rejected;
            reason = DemodulatedWindowRejectReason.Top3NotContiguous;
        }
        else if (!expectedInTop3)
        {
            state = DemodulatedWindowQualityState.Rejected;
            reason = DemodulatedWindowRejectReason.ExpectedReferenceNotInTop3;
        }
        else if (projection.AdcSaturationCount > 0)
        {
            state = DemodulatedWindowQualityState.Rejected;
            reason = DemodulatedWindowRejectReason.AdcSaturation;
        }
        else if (referenceAmplitude <= double.Epsilon)
        {
            state = DemodulatedWindowQualityState.Rejected;
            reason = DemodulatedWindowRejectReason.WeakReference;
        }
        else if (analysis.PeakToBackgroundRatio < settings.MinPeakToBackgroundRatio)
        {
            state = DemodulatedWindowQualityState.Corrected;
            reason = DemodulatedWindowRejectReason.WeakPeakToBackground;
        }
        else if (!top1IsTripletCenter || analysis.TripletCenterChannel != expectedReferenceChannel)
        {
            state = DemodulatedWindowQualityState.Corrected;
        }

        return new DemodulatedWindowQuality(
            windowIndex,
            expectedReferenceChannel,
            analysis.Top1Channel,
            analysis.TripletCenterChannel,
            analysis.Top3Channels,
            top3Contiguous,
            top1IsTripletCenter,
            state,
            reason,
            analysis.PeakToBackgroundRatio,
            projection.AdcSaturationCount);
    }

    private static int FindTripletCenter(IReadOnlySet<int> channels)
    {
        for (var center = 0; center < ChannelCount; center++)
        {
            if (channels.Contains(Mod(center - 1, ChannelCount)) &&
                channels.Contains(center) &&
                channels.Contains(Mod(center + 1, ChannelCount)))
            {
                return center;
            }
        }

        return -1;
    }

    private static double[,] CreateNanMatrix(int rows, int columns)
    {
        var matrix = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                matrix[row, column] = double.NaN;
            }
        }

        return matrix;
    }

    private static int Mod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static int[] DetectRedpoints(IReadOnlyList<double> envelope, int minRegionWidth, double peakRatio)
    {
        if (envelope.Count == 0)
        {
            return [];
        }

        var clean = envelope.Where(value => !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();
        if (clean.Length == 0)
        {
            return [];
        }

        var median = Median(clean);
        var p90 = Percentile(clean, 90);
        var p99 = Percentile(clean, 99);
        var threshold = Math.Max(median + (0.35 * (p90 - median)), 0.10 * p99);
        var regions = FindRegions(envelope, threshold, minRegionWidth);
        var candidates = new List<(int Location, double Value)>();

        foreach (var region in regions)
        {
            var max = Enumerable.Range(region.Start, region.End - region.Start + 1).Max(index => envelope[index]);
            if (max <= 0)
            {
                continue;
            }

            var highThreshold = peakRatio * max;
            var location = region.Start;
            var found = false;
            for (var index = region.Start; index <= region.End; index++)
            {
                if (envelope[index] >= highThreshold)
                {
                    location = index;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                location = Enumerable.Range(region.Start, region.End - region.Start + 1)
                    .OrderByDescending(index => envelope[index])
                    .First();
            }

            candidates.Add((location, envelope[location]));
        }

        if (candidates.Count <= 1)
        {
            return candidates.Select(candidate => candidate.Location).ToArray();
        }

        var diffs = candidates.Zip(candidates.Skip(1), (left, right) => (double)(right.Location - left.Location)).ToArray();
        var minGap = 0.5 * Median(diffs);
        var keep = new bool[candidates.Count];
        var i = 0;
        while (i < candidates.Count)
        {
            var j = i;
            while (j + 1 < candidates.Count && candidates[j + 1].Location - candidates[j].Location < minGap)
            {
                j++;
            }

            var best = i;
            for (var k = i + 1; k <= j; k++)
            {
                if (candidates[k].Value > candidates[best].Value)
                {
                    best = k;
                }
            }

            keep[best] = true;
            i = j + 1;
        }

        return candidates.Where((_, index) => keep[index]).Select(candidate => candidate.Location).ToArray();
    }

    private static List<(int Start, int End)> FindRegions(IReadOnlyList<double> envelope, double threshold, int minRegionWidth)
    {
        var regions = new List<(int Start, int End)>();
        var inRegion = false;
        var start = 0;
        for (var index = 0; index < envelope.Count; index++)
        {
            var above = envelope[index] > threshold;
            if (above && !inRegion)
            {
                inRegion = true;
                start = index;
            }
            else if ((!above || index == envelope.Count - 1) && inRegion)
            {
                var end = above ? index : index - 1;
                if (end - start + 1 >= minRegionWidth)
                {
                    regions.Add((start, end));
                }

                inRegion = false;
            }
        }

        return regions;
    }

    private static double[] HilbertEnvelope(IReadOnlyList<double> samples)
    {
        var originalLength = samples.Count;
        if (originalLength == 0)
        {
            return [];
        }

        var fftLength = 1;
        while (fftLength < originalLength)
        {
            fftLength <<= 1;
        }

        var spectrum = new ComplexValue[fftLength];
        for (var index = 0; index < originalLength; index++)
        {
            spectrum[index] = new ComplexValue(samples[index], 0);
        }

        for (var index = originalLength; index < fftLength; index++)
        {
            var reflectedIndex = (2 * originalLength) - index - 2;
            spectrum[index] = new ComplexValue(samples[reflectedIndex], 0);
        }

        Fft(spectrum, inverse: false);
        var half = fftLength / 2;
        for (var index = 1; index < half; index++)
        {
            spectrum[index] = spectrum[index] * 2.0;
        }

        for (var index = half + 1; index < fftLength; index++)
        {
            spectrum[index] = new ComplexValue(0, 0);
        }

        Fft(spectrum, inverse: true);
        return spectrum.Take(originalLength).Select(value => Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary))).ToArray();
    }

    private static void Fft(ComplexValue[] values, bool inverse)
    {
        var n = values.Length;
        var j = 0;
        for (var i = 1; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (values[i], values[j]) = (values[j], values[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2.0 * Pi / length * (inverse ? -1.0 : 1.0);
            var wlen = new ComplexValue(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = new ComplexValue(1, 0);
                for (var k = 0; k < length / 2; k++)
                {
                    var u = values[i + k];
                    var v = values[i + k + (length / 2)] * w;
                    values[i + k] = u + v;
                    values[i + k + (length / 2)] = u - v;
                    w *= wlen;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                values[i] = values[i] / n;
            }
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : 0.5 * (ordered[middle] + ordered[middle - 1]);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var ordered = values.Order().ToArray();
        if (percentile <= 0)
        {
            return ordered[0];
        }

        if (percentile >= 100)
        {
            return ordered[^1];
        }

        var position = percentile / 100.0 * (ordered.Length - 1);
        var index = (int)position;
        var fraction = position - index;
        return index + 1 < ordered.Length
            ? (ordered[index] * (1.0 - fraction)) + (ordered[index + 1] * fraction)
            : ordered[index];
    }

    private readonly record struct ComplexValue(double Real, double Imaginary)
    {
        public double Magnitude()
        {
            return Math.Sqrt((Real * Real) + (Imaginary * Imaginary));
        }

        public ComplexValue Conjugate()
        {
            return new ComplexValue(Real, -Imaginary);
        }

        public static ComplexValue operator +(ComplexValue left, ComplexValue right)
        {
            return new ComplexValue(left.Real + right.Real, left.Imaginary + right.Imaginary);
        }

        public static ComplexValue operator -(ComplexValue left, ComplexValue right)
        {
            return new ComplexValue(left.Real - right.Real, left.Imaginary - right.Imaginary);
        }

        public static ComplexValue operator *(ComplexValue left, ComplexValue right)
        {
            return new ComplexValue(
                (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
                (left.Real * right.Imaginary) + (left.Imaginary * right.Real));
        }

        public static ComplexValue operator *(ComplexValue left, double scalar)
        {
            return new ComplexValue(left.Real * scalar, left.Imaginary * scalar);
        }

        public static ComplexValue operator /(ComplexValue left, double scalar)
        {
            return new ComplexValue(left.Real / scalar, left.Imaginary / scalar);
        }
    }

    private sealed record WindowProjection(
        ComplexValue[] Projections,
        int AdcSaturationCount,
        int[] ChannelSaturationCounts,
        IReadOnlyList<double> FrequenciesHz,
        ComplexValue[][]? FrequencyProjections);

    private sealed record ReferenceWaveform(double[] Cos, double[] Sin)
    {
        public static ReferenceWaveform Create(int sampleCount, OfflineDemodulationSettings settings)
        {
            var cos = new double[sampleCount];
            var sin = new double[sampleCount];
            var phaseStep = 2.0 * Pi * settings.ExcitationFrequencyHz / settings.SampleRateHz;
            for (var sample = 0; sample < sampleCount; sample++)
            {
                var phase = phaseStep * sample;
                cos[sample] = Math.Cos(phase);
                sin[sample] = Math.Sin(phase);
            }

            return new ReferenceWaveform(cos, sin);
        }
    }

    private sealed record WindowAmplitudeAnalysis(
        double[] Amplitudes,
        int[] Top3Channels,
        int TripletCenterChannel,
        bool Top1IsTripletCenter,
        double PeakToBackgroundRatio)
    {
        public int Top1Channel => Top3Channels.Length > 0 ? Top3Channels[0] : -1;
    }

    private readonly record struct ReferenceSequence(int Offset, int Direction)
    {
        public int ExpectedChannel(int windowIndex)
        {
            return Mod(Offset + (Direction * windowIndex), ChannelCount);
        }
    }

    private sealed record UniformFrameCadence(
        int[] PeakLocations,
        int OffsetSamples,
        double WindowSamples,
        bool IntegrationStable,
        double IntegrationInstability);

    private sealed record UniformOffsetCandidate(
        int OffsetSamples,
        double TopologyScore,
        double IntegrationInstability = double.PositiveInfinity);

    private readonly record struct UniformOffsetSelection(
        int OffsetSamples,
        bool IntegrationStable,
        double IntegrationInstability);
}
