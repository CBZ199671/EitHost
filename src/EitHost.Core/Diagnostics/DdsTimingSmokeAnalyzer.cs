using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;

namespace EitHost.Core.Diagnostics;

public sealed record DdsTimingSmokeAnalysis(
    DdsTimingValidationResult Timing,
    double MeasuredCarrierHz,
    double CarrierErrorPercent,
    IReadOnlyList<int> ObservedStepOrder,
    bool StepOrderMatched,
    int StrictAcceptedFrames,
    int RejectedFrames,
    int ValidTop3Windows,
    int TotalWindows);

public static class DdsTimingSmokeAnalyzer
{
    public static DdsTimingSmokeAnalysis Analyze(
        ushort[,] raw,
        int sampleRateHz,
        double expectedCarrierHz,
        DdsExecutionReceipt execution,
        OfflineDemodulationResult demodulation)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(demodulation);
        if (raw.GetLength(0) < 3 || raw.GetLength(1) == 0)
        {
            throw new ArgumentException("DDS timing analysis requires at least three rows and one channel.", nameof(raw));
        }

        var timing = DdsTimingValidator.Validate(execution, sampleRateHz, demodulation.EstimatedWindowSamples);
        var carrierHz = EstimateCarrierFrequency(raw, sampleRateHz, demodulation);
        var carrierErrorPercent = Math.Abs(carrierHz - expectedCarrierHz) / expectedCarrierHz * 100.0;
        var acceptedFrameNumbers = demodulation.Average.AcceptedFrameNumbers.ToHashSet();
        var representative = demodulation.Frames.FirstOrDefault(frame => acceptedFrameNumbers.Contains(frame.FrameNumber))
            ?? demodulation.Frames
                .OrderBy(frame => frame.WindowQualities.Count(quality => quality.Rejected))
                .FirstOrDefault();
        var observedOrder = representative?.WindowQualities
            .OrderBy(quality => quality.WindowIndex)
            .Select(quality => quality.DetectedTop1Channel + 1)
            .ToArray() ?? [];
        var qualities = demodulation.Frames.SelectMany(frame => frame.WindowQualities).ToArray();
        return new DdsTimingSmokeAnalysis(
            timing,
            carrierHz,
            carrierErrorPercent,
            observedOrder,
            IsAscendingCyclicStepOrder(observedOrder),
            demodulation.Average.AcceptedFrameCount,
            demodulation.Average.RejectedFrameCount,
            qualities.Count(quality =>
                !quality.Rejected &&
                quality.Top3Contiguous &&
                quality.Top1IsTripletCenter),
            qualities.Length);
    }

    public static bool IsAscendingCyclicStepOrder(IReadOnlyList<int> observedOrder)
    {
        ArgumentNullException.ThrowIfNull(observedOrder);
        if (observedOrder.Count != DemodulatedFrame.StimulationCount ||
            observedOrder.Any(channel => channel is < 1 or > DemodulatedFrame.StimulationCount))
        {
            return false;
        }

        for (var index = 1; index < observedOrder.Count; index++)
        {
            if (observedOrder[index] != (observedOrder[index - 1] % DemodulatedFrame.StimulationCount) + 1)
            {
                return false;
            }
        }

        return true;
    }

    public static double EstimateCarrierFrequency(
        ushort[,] raw,
        int sampleRateHz,
        OfflineDemodulationResult? demodulation = null)
    {
        if (demodulation is not null &&
            EstimateStableWindowCarrierFrequency(raw, sampleRateHz, demodulation) is { } stableFrequency)
        {
            return stableFrequency;
        }

        var rows = raw.GetLength(0);
        var channels = raw.GetLength(1);
        var selectedChannel = 0;
        long bestRange = -1;
        for (var channel = 0; channel < channels; channel++)
        {
            ushort minimum = ushort.MaxValue;
            ushort maximum = ushort.MinValue;
            for (var row = 0; row < rows; row++)
            {
                minimum = Math.Min(minimum, raw[row, channel]);
                maximum = Math.Max(maximum, raw[row, channel]);
            }

            var range = (long)maximum - minimum;
            if (range > bestRange)
            {
                bestRange = range;
                selectedChannel = channel;
            }
        }

        var mean = 0.0;
        for (var row = 0; row < rows; row++)
        {
            mean += raw[row, selectedChannel];
        }

        mean /= rows;
        var risingCrossings = 0;
        var previous = raw[0, selectedChannel] - mean;
        for (var row = 1; row < rows; row++)
        {
            var current = raw[row, selectedChannel] - mean;
            if (previous <= 0.0 && current > 0.0)
            {
                risingCrossings++;
            }

            previous = current;
        }

        if (risingCrossings < 2)
        {
            return 0.0;
        }

        return risingCrossings * (double)sampleRateHz / (rows - 1);
    }

    private static double? EstimateStableWindowCarrierFrequency(
        ushort[,] raw,
        int sampleRateHz,
        OfflineDemodulationResult demodulation)
    {
        var rowCount = raw.GetLength(0);
        var channelCount = raw.GetLength(1);
        var risingPeriods = new List<double>();
        foreach (var frame in demodulation.Frames)
        {
            var windowSamples = (frame.EndSample - frame.StartSample) /
                (double)DemodulatedFrame.StimulationCount;
            if (windowSamples < 8.0)
            {
                continue;
            }

            for (var window = 0; window < DemodulatedFrame.StimulationCount; window++)
            {
                var left = Math.Clamp(
                    (int)Math.Round(frame.StartSample + ((window + 0.25) * windowSamples)),
                    0,
                    rowCount - 1);
                var right = Math.Clamp(
                    (int)Math.Round(frame.StartSample + ((window + 0.75) * windowSamples)),
                    left + 1,
                    rowCount);
                if (right - left < 3)
                {
                    continue;
                }

                var selectedChannel = SelectStrongestChannel(raw, left, right, channelCount);
                var mean = 0.0;
                for (var row = left; row < right; row++)
                {
                    mean += raw[row, selectedChannel];
                }

                mean /= right - left;
                var crossings = new List<double>();
                var previous = raw[left, selectedChannel] - mean;
                for (var row = left + 1; row < right; row++)
                {
                    var current = raw[row, selectedChannel] - mean;
                    if (previous <= 0.0 && current > 0.0)
                    {
                        var denominator = current - previous;
                        var fraction = denominator <= double.Epsilon ? 0.0 : -previous / denominator;
                        crossings.Add((row - 1) + fraction);
                    }

                    previous = current;
                }

                for (var index = 1; index < crossings.Count; index++)
                {
                    var period = crossings[index] - crossings[index - 1];
                    if (period > 1.0)
                    {
                        risingPeriods.Add(period);
                    }
                }
            }
        }

        if (risingPeriods.Count == 0)
        {
            return null;
        }

        risingPeriods.Sort();
        var middle = risingPeriods.Count / 2;
        var medianPeriod = risingPeriods.Count % 2 == 0
            ? (risingPeriods[middle - 1] + risingPeriods[middle]) / 2.0
            : risingPeriods[middle];
        return medianPeriod <= double.Epsilon ? null : sampleRateHz / medianPeriod;
    }

    private static int SelectStrongestChannel(ushort[,] raw, int left, int right, int channelCount)
    {
        var selectedChannel = 0;
        var bestRange = -1;
        for (var channel = 0; channel < channelCount; channel++)
        {
            var minimum = ushort.MaxValue;
            var maximum = ushort.MinValue;
            for (var row = left; row < right; row++)
            {
                minimum = Math.Min(minimum, raw[row, channel]);
                maximum = Math.Max(maximum, raw[row, channel]);
            }

            var range = maximum - minimum;
            if (range > bestRange)
            {
                bestRange = range;
                selectedChannel = channel;
            }
        }

        return selectedChannel;
    }
}
