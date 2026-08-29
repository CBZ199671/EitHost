using System.Text.Json;
using System.Text.Json.Serialization;
using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrHealthCalibrationSchema
{
    public const string Version = "ecd-cwr-health-calibration-v1";
}

public sealed record EcdCwrHealthCalibrationMetadata(
    string DeviceLabel,
    double FrequencyHz,
    DateTimeOffset CreatedAt,
    string? SourceLabel = null);

public sealed record EcdCwrHealthCalibration(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    string DeviceLabel,
    string? SourceLabel,
    double FrequencyHz,
    int FrameCount,
    IReadOnlyList<EcdCwrComplexStatistic> Contact48,
    IReadOnlyList<EcdCwrReciprocalStatistic> ReciprocalPairs,
    IReadOnlyList<EcdCwrWaveformTemplate> WaveformTemplates,
    EcdCwrCalibrationQuality Quality);

public sealed record EcdCwrComplexStatistic(
    int StimulationIndex,
    int RelativeChannelIndex,
    int SampleCount,
    double MeanReal,
    double MeanImaginary,
    double MagnitudeMean,
    double MagnitudeSigma,
    double MagnitudeMad);

public sealed record EcdCwrReciprocalStatistic(
    int StimulationIndex,
    int RelativeChannelIndex,
    int ReciprocalStimulationIndex,
    int ReciprocalRelativeChannelIndex,
    int Sign,
    int SampleCount,
    double MeanReal,
    double MeanImaginary,
    double MagnitudeMean,
    double MagnitudeSigma,
    double MagnitudeMad,
    double ComplexGainReal = 1.0,
    double ComplexGainImaginary = 0.0);

public sealed record EcdCwrWaveformTemplate(
    int StimulationIndex,
    IReadOnlyList<int> RelativeChannelIndices,
    IReadOnlyList<double> NormalizedMedianAmplitudes);

public sealed record EcdCwrCalibrationQuality(
    double Contact48WhitenedResidualP99,
    int UsableFrameCount,
    int RejectedFrameCount,
    bool Passed);

public sealed record EcdCwrHealthCalibrationOptions(
    double ResidualNoiseFloor = 1e-9,
    double QualityP99Threshold = 3.0,
    int MinimumFrameCount = 100,
    double Contact48FrameOutlierZThreshold = 3.0,
    double Contact48FrameRmsThreshold = 2.5,
    double Contact48FrameMaximumExceedanceFraction = 0.15,
    double Contact48FrameHardOutlierZThreshold = 12.0,
    int Contact48FrameOutlierFilterPasses = 2,
    int Contact48FrameOutlierMinimumFrameCount = 100);

public sealed class EcdCwrHealthCalibrationBuilder
{
    public EcdCwrHealthCalibration Create(
        OfflineDemodulationResult demodulation,
        EcdCwrHealthCalibrationMetadata metadata,
        EcdCwrHealthCalibrationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(demodulation);
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new EcdCwrHealthCalibrationOptions();
        if (options.MinimumFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum frame count must be positive.");
        }

        if (!double.IsFinite(options.Contact48FrameOutlierZThreshold) ||
            options.Contact48FrameOutlierZThreshold <= 0.0 ||
            !double.IsFinite(options.Contact48FrameRmsThreshold) ||
            options.Contact48FrameRmsThreshold <= 0.0 ||
            !double.IsFinite(options.Contact48FrameMaximumExceedanceFraction) ||
            options.Contact48FrameMaximumExceedanceFraction is < 0.0 or > 1.0 ||
            !double.IsFinite(options.Contact48FrameHardOutlierZThreshold) ||
            options.Contact48FrameHardOutlierZThreshold <= options.Contact48FrameOutlierZThreshold ||
            options.Contact48FrameOutlierFilterPasses < 0 ||
            options.Contact48FrameOutlierMinimumFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Contact48 frame outlier options are invalid.");
        }

        var usableFrames = demodulation.Frames
            .Where(IsUsableGreenFrame)
            .ToArray();
        if (usableFrames.Length < options.MinimumFrameCount)
        {
            throw new InvalidOperationException(
                $"Health calibration requires at least {options.MinimumFrameCount} all-green demodulated frames.");
        }

        var stableFrames = FilterContact48StableFrames(usableFrames, options);
        if (stableFrames.Length < options.MinimumFrameCount)
        {
            throw new InvalidOperationException(
                $"Health calibration retained {stableFrames.Length}/{usableFrames.Length} stable Contact48 frames; " +
                $"at least {options.MinimumFrameCount} are required.");
        }

        var contact48 = BuildContact48(stableFrames);
        var reciprocalPairs = BuildReciprocalPairs(stableFrames);
        var waveformTemplates = BuildWaveformTemplates(stableFrames);
        var residualP99 = CalculateContact48WhitenedResidualP99(stableFrames, contact48, options);

        return new EcdCwrHealthCalibration(
            EcdCwrHealthCalibrationSchema.Version,
            metadata.CreatedAt,
            metadata.DeviceLabel,
            metadata.SourceLabel,
            metadata.FrequencyHz,
            stableFrames.Length,
            contact48,
            reciprocalPairs,
            waveformTemplates,
            new EcdCwrCalibrationQuality(
                residualP99,
                stableFrames.Length,
                demodulation.Frames.Count - stableFrames.Length,
                residualP99 < options.QualityP99Threshold));
    }

    private static DemodulatedFrame[] FilterContact48StableFrames(
        IReadOnlyList<DemodulatedFrame> frames,
        EcdCwrHealthCalibrationOptions options)
    {
        var current = frames.ToArray();
        if (current.Length < options.Contact48FrameOutlierMinimumFrameCount ||
            options.Contact48FrameOutlierFilterPasses == 0)
        {
            return current;
        }

        for (var pass = 0; pass < options.Contact48FrameOutlierFilterPasses; pass++)
        {
            var gates = BuildContact48RobustGates(current, options.ResidualNoiseFloor);
            var filtered = current
                .Where(frame => IsWithinContact48Gates(
                    frame,
                    gates,
                    options))
                .ToArray();
            if (filtered.Length == current.Length)
            {
                break;
            }

            current = filtered;
            if (current.Length < options.MinimumFrameCount)
            {
                break;
            }
        }

        return current;
    }

    private static IReadOnlyList<Contact48RobustGate> BuildContact48RobustGates(
        IReadOnlyList<DemodulatedFrame> frames,
        double noiseFloor)
    {
        var gates = new List<Contact48RobustGate>(48);
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            foreach (var relativeChannel in AdjacentAmplitudeFrameLayout.ExcludedKIndices)
            {
                var samples = frames
                    .Select(frame => ReadComplex(frame, stimulation, relativeChannel))
                    .Where(sample => double.IsFinite(sample.Real) && double.IsFinite(sample.Imaginary))
                    .ToArray();
                var centerReal = Median(samples.Select(sample => sample.Real).ToArray());
                var centerImaginary = Median(samples.Select(sample => sample.Imaginary).ToArray());
                var radialResiduals = samples
                    .Select(sample => Math.Sqrt(
                        Math.Pow(sample.Real - centerReal, 2.0) +
                        Math.Pow(sample.Imaginary - centerImaginary, 2.0)))
                    .ToArray();
                var radialMedian = Median(radialResiduals);
                var radialMad = MedianAbsoluteDeviation(radialResiduals);
                var scale = Math.Max(
                    noiseFloor,
                    Math.Max(1.4826 * radialMedian, 1.4826 * radialMad));
                gates.Add(new Contact48RobustGate(
                    stimulation,
                    relativeChannel,
                    centerReal,
                    centerImaginary,
                    scale));
            }
        }

        return gates;
    }

    private static bool IsWithinContact48Gates(
        DemodulatedFrame frame,
        IReadOnlyList<Contact48RobustGate> gates,
        EcdCwrHealthCalibrationOptions options)
    {
        var squaredSum = 0.0;
        var exceedanceCount = 0;
        var maximumAllowedExceedances = (int)Math.Ceiling(
            options.Contact48FrameMaximumExceedanceFraction * gates.Count);
        foreach (var gate in gates)
        {
            var sample = ReadComplex(frame, gate.StimulationIndex, gate.RelativeChannelIndex);
            var residual = Math.Sqrt(
                Math.Pow(sample.Real - gate.CenterReal, 2.0) +
                Math.Pow(sample.Imaginary - gate.CenterImaginary, 2.0));
            var normalized = residual / gate.Scale;
            if (!double.IsFinite(normalized) ||
                normalized > options.Contact48FrameHardOutlierZThreshold)
            {
                return false;
            }

            squaredSum += normalized * normalized;
            if (normalized > options.Contact48FrameOutlierZThreshold)
            {
                exceedanceCount++;
            }
        }

        var normalizedRms = Math.Sqrt(squaredSum / gates.Count);
        return normalizedRms <= options.Contact48FrameRmsThreshold &&
            exceedanceCount <= maximumAllowedExceedances;
    }

    private static bool IsUsableGreenFrame(DemodulatedFrame frame)
    {
        return frame.FullRealComponents is not null &&
            frame.FullImaginaryComponents is not null &&
            frame.FullAmplitudes is not null &&
            frame.WindowQualities.Count == DemodulatedFrame.StimulationCount &&
            frame.WindowQualities.All(quality =>
                quality.State == DemodulatedWindowQualityState.Valid &&
                !quality.Rejected &&
                quality.Top3Contiguous &&
                quality.Top1IsTripletCenter &&
                quality.TripletCenterChannel == quality.ExpectedReferenceChannel);
    }

    private static IReadOnlyList<EcdCwrComplexStatistic> BuildContact48(IReadOnlyList<DemodulatedFrame> frames)
    {
        var stats = new List<EcdCwrComplexStatistic>(48);
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            foreach (var relativeChannel in AdjacentAmplitudeFrameLayout.ExcludedKIndices)
            {
                var samples = frames
                    .Select(frame => ReadComplex(frame, stimulation, relativeChannel))
                    .ToArray();
                var statistic = CreateComplexStatistic(samples);
                stats.Add(new EcdCwrComplexStatistic(
                    stimulation,
                    relativeChannel,
                    statistic.SampleCount,
                    statistic.MeanReal,
                    statistic.MeanImaginary,
                    statistic.MagnitudeMean,
                    statistic.MagnitudeSigma,
                    statistic.MagnitudeMad));
            }
        }

        return stats;
    }

    private static IReadOnlyList<EcdCwrReciprocalStatistic> BuildReciprocalPairs(IReadOnlyList<DemodulatedFrame> frames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stats = new List<EcdCwrReciprocalStatistic>(104);
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                var reciprocal = AdjacentReciprocalTiming.MapReciprocal(stimulation, relativeChannel);
                var key = CreateReciprocalKey(stimulation, relativeChannel, reciprocal);
                if (!seen.Add(key))
                {
                    continue;
                }

                var pairSamples = frames
                    .Select(frame => new ReciprocalPairSample(
                        ReadComplex(frame, stimulation, relativeChannel),
                        ReadComplex(frame, reciprocal.StimulationIndex, reciprocal.RelativeChannelIndex)))
                    .ToArray();
                var gain = EstimateComplexGain(pairSamples);
                var samples = pairSamples
                    .Select(sample => Subtract(sample.Left, Multiply(gain, sample.Right)))
                    .ToArray();
                var statistic = CreateComplexStatistic(samples);
                stats.Add(new EcdCwrReciprocalStatistic(
                    stimulation,
                    relativeChannel,
                    reciprocal.StimulationIndex,
                    reciprocal.RelativeChannelIndex,
                    Sign: 1,
                    statistic.SampleCount,
                    statistic.MeanReal,
                    statistic.MeanImaginary,
                    statistic.MagnitudeMean,
                    statistic.MagnitudeSigma,
                    statistic.MagnitudeMad,
                    gain.Real,
                    gain.Imaginary));
            }
        }

        return stats;
    }

    private static IReadOnlyList<EcdCwrWaveformTemplate> BuildWaveformTemplates(IReadOnlyList<DemodulatedFrame> frames)
    {
        var templates = new List<EcdCwrWaveformTemplate>(DemodulatedFrame.StimulationCount);
        var relativeChannels = Enumerable.Range(2, DemodulatedFrame.MeasurementsPerStimulation)
            .ToArray();

        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            var columnValues = Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
                .Select(_ => new List<double>())
                .ToArray();
            foreach (var frame in frames)
            {
                var row = Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
                    .Select(column => frame.Amplitudes[stimulation, column])
                    .Where(value => double.IsFinite(value) && value > 0.0)
                    .ToArray();
                var normalization = Median(row);
                if (!double.IsFinite(normalization) || normalization <= double.Epsilon)
                {
                    continue;
                }

                for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
                {
                    var value = frame.Amplitudes[stimulation, column];
                    if (double.IsFinite(value))
                    {
                        columnValues[column].Add(value / normalization);
                    }
                }
            }

            templates.Add(new EcdCwrWaveformTemplate(
                stimulation,
                relativeChannels,
                columnValues.Select(values => Median(values)).ToArray()));
        }

        return templates;
    }

    private static double CalculateContact48WhitenedResidualP99(
        IReadOnlyList<DemodulatedFrame> frames,
        IReadOnlyList<EcdCwrComplexStatistic> contact48,
        EcdCwrHealthCalibrationOptions options)
    {
        var residuals = new List<double>(frames.Count * contact48.Count);
        foreach (var frame in frames)
        {
            foreach (var stat in contact48)
            {
                var sample = ReadComplex(frame, stat.StimulationIndex, stat.RelativeChannelIndex);
                var residual = Math.Sqrt(
                    Math.Pow(sample.Real - stat.MeanReal, 2.0) +
                    Math.Pow(sample.Imaginary - stat.MeanImaginary, 2.0));
                var scale = Math.Max(
                    options.ResidualNoiseFloor,
                    Math.Max(stat.MagnitudeSigma, 1.4826 * stat.MagnitudeMad));
                residuals.Add(residual / scale);
            }
        }

        return Percentile(residuals, 0.99);
    }

    private static ComplexStatistic CreateComplexStatistic(IReadOnlyList<ComplexSample> samples)
    {
        var finite = samples
            .Where(sample => double.IsFinite(sample.Real) && double.IsFinite(sample.Imaginary))
            .ToArray();
        if (finite.Length == 0)
        {
            return new ComplexStatistic(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        }

        var meanReal = finite.Average(sample => sample.Real);
        var meanImaginary = finite.Average(sample => sample.Imaginary);
        var magnitudes = finite.Select(sample => sample.Magnitude).ToArray();
        var residualMagnitudes = finite
            .Select(sample => Math.Sqrt(
                Math.Pow(sample.Real - meanReal, 2.0) +
                Math.Pow(sample.Imaginary - meanImaginary, 2.0)))
            .ToArray();
        var complexResidualRms = Math.Sqrt(
            residualMagnitudes.Select(value => value * value).Average());
        return new ComplexStatistic(
            finite.Length,
            meanReal,
            meanImaginary,
            magnitudes.Average(),
            complexResidualRms,
            MedianAbsoluteDeviation(residualMagnitudes));
    }

    private static ComplexSample EstimateComplexGain(IReadOnlyList<ReciprocalPairSample> samples)
    {
        var numeratorReal = 0.0;
        var numeratorImaginary = 0.0;
        var denominator = 0.0;
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.Left.Real) ||
                !double.IsFinite(sample.Left.Imaginary) ||
                !double.IsFinite(sample.Right.Real) ||
                !double.IsFinite(sample.Right.Imaginary))
            {
                continue;
            }

            // least-squares complex gain: g = sum(left * conj(right)) / sum(|right|^2)
            numeratorReal += (sample.Left.Real * sample.Right.Real) +
                (sample.Left.Imaginary * sample.Right.Imaginary);
            numeratorImaginary += (sample.Left.Imaginary * sample.Right.Real) -
                (sample.Left.Real * sample.Right.Imaginary);
            denominator += (sample.Right.Real * sample.Right.Real) +
                (sample.Right.Imaginary * sample.Right.Imaginary);
        }

        if (denominator <= double.Epsilon || !double.IsFinite(denominator))
        {
            return new ComplexSample(1.0, 0.0);
        }

        return new ComplexSample(numeratorReal / denominator, numeratorImaginary / denominator);
    }

    private static ComplexSample Multiply(ComplexSample left, ComplexSample right)
    {
        return new ComplexSample(
            (left.Real * right.Real) - (left.Imaginary * right.Imaginary),
            (left.Real * right.Imaginary) + (left.Imaginary * right.Real));
    }

    private static ComplexSample Subtract(ComplexSample left, ComplexSample right)
    {
        return new ComplexSample(left.Real - right.Real, left.Imaginary - right.Imaginary);
    }

    private static ComplexSample ReadComplex(DemodulatedFrame frame, int stimulation, int relativeChannel)
    {
        if (frame.FullRealComponents is null || frame.FullImaginaryComponents is null)
        {
            throw new InvalidOperationException("Demodulated frame does not contain full 16x16 complex data.");
        }

        return new ComplexSample(
            frame.FullRealComponents[stimulation, relativeChannel],
            frame.FullImaginaryComponents[stimulation, relativeChannel]);
    }

    private static string CreateReciprocalKey(
        int stimulation,
        int relativeChannel,
        ReciprocalObservation reciprocal)
    {
        var left = (stimulation * DemodulatedFrame.FullMeasurementsPerStimulation) + relativeChannel;
        var right = (reciprocal.StimulationIndex * DemodulatedFrame.FullMeasurementsPerStimulation) +
            reciprocal.RelativeChannelIndex;
        return left <= right ? $"{left}:{right}" : $"{right}:{left}";
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0.0;
        }

        var mean = values.Average();
        var variance = values.Sum(value => Math.Pow(value - mean, 2.0)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    private static double MedianAbsoluteDeviation(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var median = Median(values);
        return Median(values.Select(value => Math.Abs(value - median)).ToArray());
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return double.NaN;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 1
            ? finite[middle]
            : (finite[middle - 1] + finite[middle]) / 2.0;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return double.NaN;
        }

        var position = percentile * (finite.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return finite[lower];
        }

        var fraction = position - lower;
        return finite[lower] + ((finite[upper] - finite[lower]) * fraction);
    }

    private readonly record struct ComplexSample(double Real, double Imaginary)
    {
        public double Magnitude => Math.Sqrt((Real * Real) + (Imaginary * Imaginary));
    }

    private readonly record struct ReciprocalPairSample(ComplexSample Left, ComplexSample Right);

    private sealed record Contact48RobustGate(
        int StimulationIndex,
        int RelativeChannelIndex,
        double CenterReal,
        double CenterImaginary,
        double Scale);

    private sealed record ComplexStatistic(
        int SampleCount,
        double MeanReal,
        double MeanImaginary,
        double MagnitudeMean,
        double MagnitudeSigma,
        double MagnitudeMad);
}

public sealed class EcdCwrHealthCalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Save(string path, EcdCwrHealthCalibration calibration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(calibration);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(calibration, JsonOptions));
    }

    public EcdCwrHealthCalibration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) &&
            string.Equals(
                schemaElement.GetString(),
                EcdCwrSessionCalibrationSchema.Version,
                StringComparison.Ordinal))
        {
            return new EcdCwrSessionCalibrationStore().Load(path).HealthCalibration;
        }

        var calibration = JsonSerializer.Deserialize<EcdCwrHealthCalibration>(
            File.ReadAllText(path),
            JsonOptions) ?? throw new InvalidOperationException("Calibration file is empty or invalid.");
        if (!string.Equals(calibration.SchemaVersion, EcdCwrHealthCalibrationSchema.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported ECD-CWR calibration schema '{calibration.SchemaVersion}'. Expected '{EcdCwrHealthCalibrationSchema.Version}'.");
        }

        return calibration;
    }
}
