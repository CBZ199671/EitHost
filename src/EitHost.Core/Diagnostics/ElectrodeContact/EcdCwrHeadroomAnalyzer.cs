using EitHost.Core.Demodulation;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrHeadroomAnalyzer
{
    private const int ChannelCount = AdjacentAmplitudeFrameLayout.ElectrodeCount;
    private const int AdcMidscale = 32768;
    private const int AdcMaxMagnitude = 32768;

    private readonly OfflineDemodulator demodulator;

    public EcdCwrHeadroomAnalyzer(OfflineDemodulator? demodulator = null)
    {
        this.demodulator = demodulator ?? new OfflineDemodulator();
    }

    public EcdCwrHeadroomReport AnalyzeHdf5(
        string inputHdf5Path,
        OfflineDemodulationSettings? settings = null,
        EcdCwrHeadroomAnalyzerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputHdf5Path);

        using var inputFile = Hdf5FileAccess.OpenReadWithRetry(inputHdf5Path);
        var raw = inputFile.Dataset("/raw/adc_counts").Read<ushort[,]>();
        var frequencyHz = inputFile.Dataset("/metadata/excitation/frequency_hz").Read<int>();
        settings ??= new OfflineDemodulationSettings(
            inputFile.Dataset("/metadata/acquisition/sample_rate_hz").Read<int>(),
            frequencyHz,
            channelCycles: inputFile.Dataset("/metadata/excitation/channel_cycles").Read<double>());

        return Analyze(raw, settings, options, sourceLabel: Path.GetFullPath(inputHdf5Path));
    }

    public EcdCwrHeadroomReport Analyze(
        ushort[,] rawAdcCounts,
        OfflineDemodulationSettings settings,
        EcdCwrHeadroomAnalyzerOptions? options = null,
        string? sourceLabel = null)
    {
        ArgumentNullException.ThrowIfNull(rawAdcCounts);
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new EcdCwrHeadroomAnalyzerOptions();

        if (rawAdcCounts.GetLength(1) != ChannelCount)
        {
            throw new ArgumentException("Headroom analysis expects raw data shaped [sample, 16].", nameof(rawAdcCounts));
        }

        var demodulation = demodulator.Demodulate(rawAdcCounts, settings);
        var accumulators = CreateAccumulators();
        var guardMagnitude = checked((int)Math.Ceiling(AdcMaxMagnitude * options.SaturationGuardFraction));
        var saturationThreshold = AdcMaxMagnitude - guardMagnitude;

        foreach (var frame in demodulation.Frames)
        {
            if (frame.WindowQualities.Count == 0)
            {
                continue;
            }

            var frameLength = frame.EndSample - frame.StartSample;
            if (frameLength <= 0)
            {
                continue;
            }

            var segmentLength = (double)frameLength / frame.WindowQualities.Count;
            for (var window = 0; window < frame.WindowQualities.Count; window++)
            {
                var quality = frame.WindowQualities[window];
                var stimulation = quality.ExpectedReferenceChannel;
                var segmentLeft = Math.Max(0, (int)Math.Round(frame.StartSample + (window * segmentLength)));
                var segmentRight = Math.Min(
                    rawAdcCounts.GetLength(0) - 1,
                    ((int)Math.Round(frame.StartSample + ((window + 1) * segmentLength))) - 1);
                if (segmentRight < segmentLeft)
                {
                    continue;
                }

                for (var relativeChannel = 0; relativeChannel < ChannelCount; relativeChannel++)
                {
                    var measurementChannel = (stimulation + relativeChannel) % ChannelCount;
                    var accumulator = accumulators[stimulation, relativeChannel];
                    var pointSaturated = false;
                    for (var sample = segmentLeft; sample <= segmentRight; sample++)
                    {
                        var magnitude = Math.Abs((int)rawAdcCounts[sample, measurementChannel] - AdcMidscale);
                        accumulator.Magnitudes.Add(magnitude);
                        if (magnitude >= saturationThreshold)
                        {
                            pointSaturated = true;
                            accumulator.SaturatedSampleCount++;
                        }
                    }

                    accumulator.WindowCount++;
                    if (pointSaturated)
                    {
                        accumulator.SaturatedWindowCount++;
                    }
                }
            }
        }

        var cells = BuildCells(accumulators, saturationThreshold);
        var cells48 = cells.Where(cell => cell.IsExcitationRelated48).ToArray();
        var cells208 = cells.Where(cell => !cell.IsExcitationRelated48).ToArray();
        var saturationRate48 = CalculateWindowSaturationRate(cells48);
        var saturationRate208 = CalculateWindowSaturationRate(cells208);
        var minHeadroom48 = cells48.Length == 0 ? double.NaN : cells48.Min(cell => cell.HeadroomFraction);
        var minHeadroom208 = cells208.Length == 0 ? double.NaN : cells208.Min(cell => cell.HeadroomFraction);
        var conclusion = Classify(cells48, saturationRate48, options);

        return new EcdCwrHeadroomReport(
            sourceLabel ?? "raw-adc",
            settings.ExcitationFrequencyHz,
            settings.ChannelCycles,
            demodulation.Frames.Count,
            guardMagnitude,
            saturationThreshold,
            saturationRate48,
            saturationRate208,
            minHeadroom48,
            minHeadroom208,
            conclusion,
            CreateSummary(conclusion, saturationRate48, minHeadroom48),
            cells48,
            cells208);
    }

    private static HeadroomAccumulator[,] CreateAccumulators()
    {
        var accumulators = new HeadroomAccumulator[ChannelCount, ChannelCount];
        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            for (var relativeChannel = 0; relativeChannel < ChannelCount; relativeChannel++)
            {
                accumulators[stimulation, relativeChannel] = new HeadroomAccumulator();
            }
        }

        return accumulators;
    }

    private static EcdCwrHeadroomCell[] BuildCells(HeadroomAccumulator[,] accumulators, int saturationThreshold)
    {
        var cells = new List<EcdCwrHeadroomCell>(ChannelCount * ChannelCount);
        for (var stimulation = 0; stimulation < ChannelCount; stimulation++)
        {
            for (var relativeChannel = 0; relativeChannel < ChannelCount; relativeChannel++)
            {
                var accumulator = accumulators[stimulation, relativeChannel];
                var p99 = Percentile(accumulator.Magnitudes, 0.99);
                var headroom = double.IsNaN(p99)
                    ? double.NaN
                    : Math.Max(0.0, (AdcMaxMagnitude - p99) / AdcMaxMagnitude);
                var sampleSaturationRate = accumulator.Magnitudes.Count == 0
                    ? 0.0
                    : (double)accumulator.SaturatedSampleCount / accumulator.Magnitudes.Count;
                var windowSaturationRate = accumulator.WindowCount == 0
                    ? 0.0
                    : (double)accumulator.SaturatedWindowCount / accumulator.WindowCount;

                cells.Add(new EcdCwrHeadroomCell(
                    stimulation,
                    relativeChannel,
                    AdjacentAmplitudeFrameLayout.ExcludedKIndices.Contains(relativeChannel),
                    accumulator.WindowCount,
                    accumulator.Magnitudes.Count,
                    accumulator.SaturatedWindowCount,
                    accumulator.SaturatedSampleCount,
                    windowSaturationRate,
                    sampleSaturationRate,
                    p99,
                    headroom,
                    saturationThreshold));
            }
        }

        return cells.ToArray();
    }

    private static double CalculateWindowSaturationRate(IReadOnlyList<EcdCwrHeadroomCell> cells)
    {
        var windowCount = cells.Sum(cell => cell.WindowCount);
        if (windowCount == 0)
        {
            return 0.0;
        }

        return (double)cells.Sum(cell => cell.SaturatedWindowCount) / windowCount;
    }

    private static EcdCwrHeadroomConclusion Classify(
        IReadOnlyList<EcdCwrHeadroomCell> cells48,
        double saturationRate48,
        EcdCwrHeadroomAnalyzerOptions options)
    {
        if (saturationRate48 >= options.BinarySaturationRateThreshold)
        {
            return EcdCwrHeadroomConclusion.BinaryHardEvidence;
        }

        if (saturationRate48 > 0.0 ||
            cells48.Any(cell => double.IsNaN(cell.HeadroomFraction) ||
                cell.HeadroomFraction <= options.ContinuousHeadroomThreshold))
        {
            return EcdCwrHeadroomConclusion.ContinuousWithSaturationMask;
        }

        return EcdCwrHeadroomConclusion.ContinuousEvidence;
    }

    private static string CreateSummary(
        EcdCwrHeadroomConclusion conclusion,
        double saturationRate48,
        double minHeadroom48)
    {
        return conclusion switch
        {
            EcdCwrHeadroomConclusion.ContinuousEvidence =>
                $"48-point headroom is healthy: min headroom={minHeadroom48:P1}, saturation={saturationRate48:P3}.",
            EcdCwrHeadroomConclusion.ContinuousWithSaturationMask =>
                $"48-point evidence can remain continuous with masks: min headroom={minHeadroom48:P1}, saturation={saturationRate48:P3}.",
            _ =>
                $"48-point evidence should degrade to binary hard evidence: min headroom={minHeadroom48:P1}, saturation={saturationRate48:P3}."
        };
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var ordered = values.Order().ToArray();
        var position = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        var fraction = position - lower;
        return ordered[lower] + ((ordered[upper] - ordered[lower]) * fraction);
    }

    private sealed class HeadroomAccumulator
    {
        public List<double> Magnitudes { get; } = [];

        public int WindowCount { get; set; }

        public int SaturatedWindowCount { get; set; }

        public int SaturatedSampleCount { get; set; }
    }
}

public sealed record EcdCwrHeadroomAnalyzerOptions(
    double SaturationGuardFraction = 0.01,
    double ContinuousHeadroomThreshold = 0.10,
    double BinarySaturationRateThreshold = 0.25);

public enum EcdCwrHeadroomConclusion
{
    ContinuousEvidence = 0,
    ContinuousWithSaturationMask = 1,
    BinaryHardEvidence = 2
}

public sealed record EcdCwrHeadroomReport(
    string SourceLabel,
    double FrequencyHz,
    double ChannelCycles,
    int FrameCount,
    int GuardMagnitudeCounts,
    int SaturationThresholdMagnitudeCounts,
    double SaturationRate48,
    double SaturationRate208,
    double MinHeadroom48,
    double MinHeadroom208,
    EcdCwrHeadroomConclusion Conclusion,
    string Summary,
    IReadOnlyList<EcdCwrHeadroomCell> Cells48,
    IReadOnlyList<EcdCwrHeadroomCell> Cells208);

public sealed record EcdCwrHeadroomCell(
    int StimulationIndex,
    int RelativeChannelIndex,
    bool IsExcitationRelated48,
    int WindowCount,
    int SampleCount,
    int SaturatedWindowCount,
    int SaturatedSampleCount,
    double WindowSaturationRate,
    double SampleSaturationRate,
    double P99MagnitudeCounts,
    double HeadroomFraction,
    int SaturationThresholdMagnitudeCounts);
