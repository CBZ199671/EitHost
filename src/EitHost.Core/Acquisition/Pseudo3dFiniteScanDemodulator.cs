using EitHost.Core.Demodulation;

namespace EitHost.Core.Acquisition;

public sealed record Pseudo3dFiniteScanWindow(int MinimumStartRow, int MaximumStartRow);
public sealed record Pseudo3dFiniteScanResult(RealtimeDemodulatedBlock Block, int AnalysisStartRow, int AnalysisEndRow);

/// <summary>Analyzes one finite burst; all searches and quality checks use only this burst.</summary>
public static class Pseudo3dFiniteScanDemodulator
{
    public static Pseudo3dFiniteScanResult Demodulate(
        ushort[,] raw, RealtimeDemodulationSettings settings, Pseudo3dFiniteScanWindow window)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(window);
        if (raw.GetLength(1) != 16 || window.MinimumStartRow < 16 ||
            window.MaximumStartRow < window.MinimumStartRow || window.MaximumStartRow >= raw.GetLength(0))
            throw new ArgumentException("Finite scan needs a measured start bracket and a quiet ADC prefix.");
        var onset = FindStartRow(raw, settings, window);
        if (onset < window.MinimumStartRow || onset > window.MaximumStartRow)
            throw new InvalidDataException("本槽启动范围内没有连续激励波形，不能生成三帧块。");

        // The envelope only narrows the search. It never qualifies a frame: the
        // complete topology, saturation and 1% integration gates still apply.
        var halfDwell = (int)Math.Ceiling(settings.NominalWindowSamples / 2);
        var first = Math.Max(window.MinimumStartRow, onset - halfDwell);
        var maximumOffset = Math.Min(window.MaximumStartRow, onset + halfDwell) - first;
        var samples = new ushort[raw.GetLength(0) - first, 16];
        Buffer.BlockCopy(raw, first * 32, samples, 0, samples.Length * 2);
        var demodulator = new OfflineDemodulator();
        var result = demodulator.DemodulateFiniteScan(samples,
            settings.ToOfflineSettingsWithLockedWindowSamples(settings.NominalWindowSamples), maximumOffset);
        // Stable within-window integration does not justify a cadence refit just
        // because electrode topology failed. Preserve that rejection evidence.
        if (!result.UniformIntegrationStable)
        {
            var candidate = demodulator.DemodulateFiniteScan(samples, settings.ToOfflineSettings(), maximumOffset);
            if (candidate.Average.AcceptedFrameCount >= result.Average.AcceptedFrameCount &&
                candidate.UniformIntegrationInstability < result.UniformIntegrationInstability)
                result = candidate;
        }
        if (result.Frames.Count != settings.FramesPerBlock || result.PeakLocations.Count != settings.FramesPerBlock + 1)
            throw new InvalidDataException("有限扫描没有覆盖完整的三帧，不能发布当前轮。");
        var accepted = result.Average.AcceptedFrameCount;
        var high = result.UniformIntegrationStable && accepted == settings.FramesPerBlock;
        var qualities = result.Frames[0].WindowQualities;
        var direction = qualities.Count < 2 ? 0 : (qualities[1].ExpectedReferenceChannel - qualities[0].ExpectedReferenceChannel + 16) % 16;
        var block = new RealtimeDemodulatedBlock(1, first, first + result.PeakLocations[^1], result.PeakLocations[^1],
            result.EstimatedWindowSamples, result.UniformOffsetSamples, qualities[0].ExpectedReferenceChannel + 1,
            direction == 1 ? 1 : direction == 15 ? -1 : 0, accepted, result.Average.RejectedFrameCount,
            result.UniformIntegrationStable ? (double)accepted / settings.FramesPerBlock : 0, high,
            result.Average, result.Frames, result.PeakLocations, result.TrustedPartialAverage, result.DiagnosticAverage,
            result.UniformIntegrationStable, result.UniformIntegrationInstability);
        return new(block, first, checked((int)block.EndSampleIndex));
    }

    public static int FindStartRow(ushort[,] raw, RealtimeDemodulationSettings settings, Pseudo3dFiniteScanWindow window)
    {
        var cycle = Math.Max(4, (int)Math.Ceiling(settings.SampleRateHz / settings.ExcitationFrequencyHz));
        if (window.MinimumStartRow < cycle) throw new ArgumentException("A quiet prefix of at least one cycle is required.");
        var baseline = new List<double>();
        for (var row = 0; row + cycle <= window.MinimumStartRow; row += cycle)
            baseline.Add(MaximumRms(raw, row, cycle));
        baseline.Sort();
        var threshold = Math.Max(16, baseline[baseline.Count / 2] * 8);
        var onset = -1;
        var consecutive = 0;
        for (var row = window.MinimumStartRow; row + cycle <= raw.GetLength(0) && row <= window.MaximumStartRow + 2 * cycle; row += cycle)
        {
            consecutive = MaximumRms(raw, row, cycle) >= threshold ? consecutive + 1 : 0;
            if (consecutive >= 3) { onset = row - 2 * cycle; break; }
        }
        return onset;
    }

    private static double MaximumRms(ushort[,] raw, int first, int count)
    {
        var maximum = 0.0;
        for (var channel = 0; channel < 16; channel++)
        {
            double sum = 0, squares = 0;
            for (var row = first; row < first + count; row++)
            {
                double value = raw[row, channel];
                sum += value; squares += value * value;
            }
            maximum = Math.Max(maximum, Math.Sqrt(Math.Max(0, squares / count - sum * sum / count / count)));
        }
        return maximum;
    }
}
