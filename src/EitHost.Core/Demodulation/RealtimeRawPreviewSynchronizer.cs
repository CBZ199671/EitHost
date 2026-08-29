namespace EitHost.Core.Demodulation;

public sealed class RealtimeRawChannelBuffer
{
    private readonly object gate = new();
    private readonly ushort[] samples;
    private int head;
    private int count;
    private long firstSampleIndex;
    private long nextSampleIndex;

    public RealtimeRawChannelBuffer(int capacitySamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacitySamples);
        samples = new ushort[capacitySamples];
    }

    public int Capacity => samples.Length;

    public void Append(ushort[,] rawAdcCounts, long startSampleIndex, int channelIndex)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        if (channelIndex < 0 || channelIndex >= rawAdcCounts.GetLength(1))
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex));
        }

        lock (gate)
        {
            if (count == 0 || startSampleIndex != nextSampleIndex)
            {
                ResetUnsafe(startSampleIndex);
            }

            for (var row = 0; row < rawAdcCounts.GetLength(0); row++)
            {
                if (count < samples.Length)
                {
                    samples[(head + count) % samples.Length] = rawAdcCounts[row, channelIndex];
                    count++;
                }
                else
                {
                    samples[head] = rawAdcCounts[row, channelIndex];
                    head = (head + 1) % samples.Length;
                    firstSampleIndex++;
                }

                nextSampleIndex++;
            }
        }
    }

    public bool TryRead(long startSampleIndex, int sampleCount, out ushort[] result)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSampleIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        lock (gate)
        {
            if (startSampleIndex < firstSampleIndex ||
                startSampleIndex > nextSampleIndex ||
                sampleCount > nextSampleIndex - startSampleIndex)
            {
                result = [];
                return false;
            }

            result = new ushort[sampleCount];
            var offset = checked((int)(startSampleIndex - firstSampleIndex));
            for (var index = 0; index < sampleCount; index++)
            {
                result[index] = samples[(head + offset + index) % samples.Length];
            }

            return true;
        }
    }

    public void Reset(long nextIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextIndex);
        lock (gate)
        {
            ResetUnsafe(nextIndex);
        }
    }

    private void ResetUnsafe(long nextIndex)
    {
        head = 0;
        count = 0;
        firstSampleIndex = nextIndex;
        nextSampleIndex = nextIndex;
    }
}

public sealed record RealtimeRawPreviewWindow(
    long StartSampleIndex,
    int SampleCount,
    int NominalSampleCount,
    int LeadingDiscardSamples,
    int TrailingDiscardSamples,
    int StimulationChannelOneBased,
    int FrameNumber,
    bool IsDiagnosticOnly = false);

public static class RealtimeRawPreviewSelector
{
    public static RealtimeRawPreviewWindow? Select(
        RealtimeDemodulatedBlock block,
        double sampleRateHz,
        double excitationFrequencyHz,
        double channelCycles,
        double discardLeadingCycles,
        double discardTrailingCycles,
        int preferredStimulationChannelOneBased = 1)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(excitationFrequencyHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(discardLeadingCycles);
        ArgumentOutOfRangeException.ThrowIfNegative(discardTrailingCycles);
        if (discardLeadingCycles + discardTrailingCycles >= channelCycles)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discardLeadingCycles),
                "Discarded cycles must leave at least one preview cycle.");
        }

        if (preferredStimulationChannelOneBased is < 1 or > DemodulatedFrame.StimulationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredStimulationChannelOneBased));
        }

        if (block.Frames.Count == 0)
        {
            return null;
        }

        var acceptedFrames = block.Average.AcceptedFrameNumbers.ToHashSet();
        for (var frameIndex = block.Frames.Count - 1; frameIndex >= 0; frameIndex--)
        {
            var frame = block.Frames[frameIndex];
            if (frame.WindowQualities.Count != DemodulatedFrame.StimulationCount ||
                frame.EndSample <= frame.StartSample)
            {
                continue;
            }

            var quality = frame.WindowQualities.FirstOrDefault(candidate =>
                candidate.ExpectedReferenceChannel == preferredStimulationChannelOneBased - 1);
            if (quality is null ||
                quality.WindowIndex < 0 ||
                quality.WindowIndex >= frame.WindowQualities.Count)
            {
                continue;
            }

            var segmentLength = (double)(frame.EndSample - frame.StartSample) /
                frame.WindowQualities.Count;
            var segmentLeft = (int)Math.Round(frame.StartSample + (quality.WindowIndex * segmentLength));
            var segmentRightExclusive = (int)Math.Round(
                frame.StartSample + ((quality.WindowIndex + 1) * segmentLength));
            segmentLeft = Math.Max(frame.StartSample, segmentLeft);
            segmentRightExclusive = Math.Min(frame.EndSample, segmentRightExclusive);
            var nominalSampleCount = segmentRightExclusive - segmentLeft;
            if (nominalSampleCount < 3)
            {
                continue;
            }

            var autoTrim = (int)Math.Round(0.08 * segmentLength);
            var samplesPerCycle = segmentLength / channelCycles;
            var leading = Math.Max(autoTrim, (int)Math.Round(discardLeadingCycles * samplesPerCycle));
            var trailing = Math.Max(autoTrim, (int)Math.Round(discardTrailingCycles * samplesPerCycle));
            var maxDiscardTotal = nominalSampleCount - 2;
            if (leading + trailing > maxDiscardTotal)
            {
                var total = leading + trailing;
                leading = total <= 0
                    ? 0
                    : (int)Math.Floor((double)leading / total * maxDiscardTotal);
                trailing = maxDiscardTotal - leading;
            }

            var sampleCount = nominalSampleCount - leading - trailing;
            if (sampleCount <= 0)
            {
                continue;
            }

            var absoluteStart = block.StartSampleIndex + segmentLeft + leading;
            var absoluteEnd = absoluteStart + sampleCount;
            if (absoluteStart < block.StartSampleIndex || absoluteEnd > block.EndSampleIndex)
            {
                continue;
            }

            return new RealtimeRawPreviewWindow(
                absoluteStart,
                sampleCount,
                nominalSampleCount,
                leading,
                trailing,
                preferredStimulationChannelOneBased,
                frame.FrameNumber,
                IsDiagnosticOnly: !block.IsHighQuality ||
                    !acceptedFrames.Contains(frame.FrameNumber) ||
                    quality.Rejected);
        }

        return null;
    }
}
