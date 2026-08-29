using System.Globalization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrRong2026Baseline
{
    public const string SchemaVersion = "ecd-cwr-rong2026-reproduction-v1";
    public const string SourceDoi = "10.1088/1361-6501/ae6d1d";
    public const string Equation7Interpretation =
        "symmetric-mean correction of printed Eq.7 magnitude-difference denominator";
    public const int ElectrodeCount = 16;
    public const int MeasurementsPerStimulation = 13;
    public const int MeasurementCount = ElectrodeCount * MeasurementsPerStimulation;

    private const int RetainedRelativeChannelMin = 2;
    private static readonly int[][] CouplingRows = BuildCouplingRows();

    public EcdCwrRong2026Result Analyze(
        IReadOnlyList<double> reference208,
        IReadOnlyList<double> target208,
        EcdCwrRong2026Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(reference208);
        ArgumentNullException.ThrowIfNull(target208);
        return Analyze(
            new EcdCwrRong2026Input(
                reference208,
                reference208.Select(Math.Abs).ToArray(),
                target208,
                target208.Select(Math.Abs).ToArray()),
            options);
    }

    public EcdCwrRong2026Result Analyze(
        EcdCwrRong2026Input input,
        EcdCwrRong2026Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new EcdCwrRong2026Options();
        ValidateOptions(options);
        var referenceReal = ValidateVector(input.ReferenceReal208, nameof(input.ReferenceReal208));
        var referenceAmplitude = ValidateAmplitudeVector(
            input.ReferenceAmplitude208,
            nameof(input.ReferenceAmplitude208));
        var targetReal = ValidateVector(input.TargetReal208, nameof(input.TargetReal208));
        var targetAmplitude = ValidateAmplitudeVector(
            input.TargetAmplitude208,
            nameof(input.TargetAmplitude208));

        var reciprocity = CalculateReciprocityError(targetAmplitude, options.DenominatorFloor);
        var curvature = CalculateCurvatureError(referenceAmplitude, targetAmplitude);
        var combined = reciprocity
            .Zip(curvature, (reciprocal, shape) =>
                Math.Max(0.0, reciprocal + (options.CurvatureWeight * shape)))
            .ToArray();
        var (scores, penalty, residualRms) = SolveNonNegativeL1(combined, options);
        var (threshold, gapIndex, detected) = ApplyGapThreshold(scores, options);
        var compensation = BuildTemplateCompensation(
            referenceReal,
            referenceAmplitude,
            targetReal,
            targetAmplitude,
            detected,
            options);
        return new EcdCwrRong2026Result(
            SchemaVersion,
            CreatePolicyVersion(options),
            SourceDoi,
            Equation7Interpretation,
            DescribeAssumptions(options),
            reciprocity,
            curvature,
            combined,
            scores,
            penalty,
            residualRms,
            gapIndex,
            threshold,
            detected,
            compensation.Template13,
            compensation.CompensatedReal208,
            compensation.AffectedMeasurementCount,
            compensation.ValidTemplateRowCount,
            compensation.SevereRowCount);
    }

    public static string CreatePolicyVersion(EcdCwrRong2026Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"rong2026-reproduction-v1:eq7=symmetric-mean:alpha={options.CurvatureWeight:G6}:lambda-rel={options.LambdaFraction:G6}:tnoise={options.NoiseFloor:G6}:tau={options.AmplitudeTolerance:G6}:sg=5x2:valley={options.MildValleyElasticity:G6}:k={options.MaxGapFaultCount}");
    }

    public static IReadOnlyList<string> DescribeAssumptions(EcdCwrRong2026Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            "Eq.7 uses abs(V-Vrecip)/((abs(V)+abs(Vrecip))/2); the printed magnitude-difference denominator is singular and is treated as a publication typo.",
            $"The unspecified L1 penalty is {options.LambdaFraction.ToString("G6", CultureInfo.InvariantCulture)} times the non-negative lambda-max scale.",
            $"The unspecified T_noise is fixed at {options.NoiseFloor.ToString("G6", CultureInfo.InvariantCulture)} in electrode-score units.",
            "The ideal U profile is the matching 13-point healthy reference row for each stimulation.",
            "The unspecified Savitzky-Golay configuration is window 5, polynomial order 2, with reflected boundaries.",
            $"Severe drive rows use the global median valley; other affected rows use valley elasticity {options.MildValleyElasticity.ToString("G6", CultureInfo.InvariantCulture)}.",
            "The L1 solve is constrained non-negative because X is an electrode fault-energy score.",
            "Template synthesis operates on amplitude and restores the original real-value sign for reconstruction."
        ];
    }

    public static int ReciprocalRetainedIndex(int retainedIndex)
    {
        if (retainedIndex is < 0 or >= MeasurementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedIndex));
        }

        var stimulation = retainedIndex / MeasurementsPerStimulation;
        var relative = (retainedIndex % MeasurementsPerStimulation) + RetainedRelativeChannelMin;
        var reciprocalStimulation = Mod(stimulation + relative);
        var reciprocalRelative = ElectrodeCount - relative;
        return (reciprocalStimulation * MeasurementsPerStimulation) +
            (reciprocalRelative - RetainedRelativeChannelMin);
    }

    public static IReadOnlyList<int> InvolvedElectrodes(int retainedIndex)
    {
        if (retainedIndex is < 0 or >= MeasurementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedIndex));
        }

        return CouplingRows[retainedIndex];
    }

    private static double[] CalculateReciprocityError(
        IReadOnlyList<double> amplitude,
        double denominatorFloor)
    {
        var output = new double[MeasurementCount];
        for (var index = 0; index < MeasurementCount; index++)
        {
            var reciprocalIndex = ReciprocalRetainedIndex(index);
            var left = amplitude[index];
            var right = amplitude[reciprocalIndex];
            var denominator = Math.Max(denominatorFloor, 0.5 * (left + right));
            output[index] = Math.Abs(left - right) / denominator;
        }

        return output;
    }

    private static double[] CalculateCurvatureError(
        IReadOnlyList<double> referenceAmplitude,
        IReadOnlyList<double> targetAmplitude)
    {
        var output = new double[MeasurementCount];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            var start = stimulation * MeasurementsPerStimulation;
            var reference = referenceAmplitude.Skip(start).Take(MeasurementsPerStimulation).ToArray();
            var target = targetAmplitude.Skip(start).Take(MeasurementsPerStimulation).ToArray();
            var error = 1.0 - PearsonCorrelation(target, reference);
            for (var column = 0; column < MeasurementsPerStimulation; column++)
            {
                output[start + column] = error;
            }
        }

        return output;
    }

    private static (double[] Scores, double Penalty, double ResidualRms) SolveNonNegativeL1(
        IReadOnlyList<double> error208,
        EcdCwrRong2026Options options)
    {
        var lambdaMax = 0.0;
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var projection = 0.0;
            for (var row = 0; row < MeasurementCount; row++)
            {
                if (Array.IndexOf(CouplingRows[row], electrode) >= 0)
                {
                    projection += error208[row];
                }
            }

            lambdaMax = Math.Max(lambdaMax, 2.0 * projection);
        }

        var penalty = options.LambdaFraction * lambdaMax;
        var lipschitz = EstimateLipschitz();
        var step = 1.0 / Math.Max(lipschitz, options.DenominatorFloor);
        var x = new double[ElectrodeCount];
        var y = new double[ElectrodeCount];
        var t = 1.0;
        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            var gradient = CalculateGradient(error208, y);
            var next = new double[ElectrodeCount];
            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                next[electrode] = Math.Max(
                    0.0,
                    y[electrode] - (step * gradient[electrode]) - (step * penalty));
            }

            var maxChange = next.Zip(x, (left, right) => Math.Abs(left - right)).Max();
            if (maxChange <= options.ConvergenceTolerance)
            {
                x = next;
                break;
            }

            var previous = x;
            x = next;
            var nextT = 0.5 * (1.0 + Math.Sqrt(1.0 + (4.0 * t * t)));
            var momentum = (t - 1.0) / nextT;
            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                y[electrode] = x[electrode] + (momentum * (x[electrode] - previous[electrode]));
            }

            t = nextT;
        }

        var residualSquares = 0.0;
        for (var row = 0; row < MeasurementCount; row++)
        {
            var residual = PredictRow(CouplingRows[row], x) - error208[row];
            residualSquares += residual * residual;
        }

        return (x, penalty, Math.Sqrt(residualSquares / MeasurementCount));
    }

    private static double EstimateLipschitz()
    {
        var vector = Enumerable.Repeat(1.0 / Math.Sqrt(ElectrodeCount), ElectrodeCount).ToArray();
        var eigenvalue = 1.0;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var next = ApplyTwiceNormalMatrix(vector);
            var norm = Math.Sqrt(next.Sum(value => value * value));
            if (norm <= double.Epsilon)
            {
                return 1.0;
            }

            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                vector[electrode] = next[electrode] / norm;
            }

            eigenvalue = vector.Zip(ApplyTwiceNormalMatrix(vector), (left, right) => left * right).Sum();
        }

        return Math.Max(1.0, eigenvalue);
    }

    private static double[] ApplyTwiceNormalMatrix(IReadOnlyList<double> vector)
    {
        var output = new double[ElectrodeCount];
        foreach (var row in CouplingRows)
        {
            var prediction = PredictRow(row, vector);
            foreach (var electrode in row)
            {
                output[electrode] += 2.0 * prediction;
            }
        }

        return output;
    }

    private static double[] CalculateGradient(
        IReadOnlyList<double> error208,
        IReadOnlyList<double> coefficients)
    {
        var gradient = new double[ElectrodeCount];
        for (var rowIndex = 0; rowIndex < MeasurementCount; rowIndex++)
        {
            var row = CouplingRows[rowIndex];
            var residual = PredictRow(row, coefficients) - error208[rowIndex];
            foreach (var electrode in row)
            {
                gradient[electrode] += 2.0 * residual;
            }
        }

        return gradient;
    }

    private static double PredictRow(
        IReadOnlyList<int> row,
        IReadOnlyList<double> coefficients)
    {
        var sum = 0.0;
        foreach (var electrode in row)
        {
            sum += coefficients[electrode];
        }

        return sum;
    }

    private static (double Threshold, int GapIndex, int[] Detected) ApplyGapThreshold(
        IReadOnlyList<double> scores,
        EcdCwrRong2026Options options)
    {
        var ordered = scores
            .Select((score, electrode) => new ElectrodeScore(electrode, score))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Electrode)
            .ToArray();
        var maximumSplit = Math.Min(options.MaxGapFaultCount, ElectrodeCount - 1);
        var gapIndex = 0;
        var maximumGap = 0.0;
        for (var split = 1; split <= maximumSplit; split++)
        {
            var gap = Math.Abs(ordered[split - 1].Score - ordered[split].Score);
            if (gap > maximumGap)
            {
                maximumGap = gap;
                gapIndex = split;
            }
        }

        if (gapIndex == 0)
        {
            return (options.NoiseFloor, 0, []);
        }

        var adaptive = 0.5 * (ordered[gapIndex - 1].Score + ordered[gapIndex].Score);
        var threshold = Math.Max(options.NoiseFloor, adaptive);
        var detected = ordered
            .Where(item => item.Score > threshold)
            .Take(options.MaxGapFaultCount)
            .Select(item => item.Electrode)
            .Order()
            .ToArray();
        return (threshold, gapIndex, detected);
    }

    private static TemplateCompensation BuildTemplateCompensation(
        IReadOnlyList<double> referenceReal,
        IReadOnlyList<double> referenceAmplitude,
        IReadOnlyList<double> targetReal,
        IReadOnlyList<double> targetAmplitude,
        IReadOnlyList<int> detectedElectrodes,
        EcdCwrRong2026Options options)
    {
        var detected = detectedElectrodes.ToHashSet();
        var validRows = Enumerable.Range(0, ElectrodeCount)
            .Where(stimulation =>
                !detected.Contains(stimulation) &&
                !detected.Contains(Mod(stimulation + 1)))
            .ToArray();
        var normalizedRows = BuildNormalizedRows(targetAmplitude, validRows, options.DenominatorFloor);
        var templateSource = targetAmplitude;
        if (normalizedRows.Count == 0)
        {
            validRows = Enumerable.Range(0, ElectrodeCount).ToArray();
            normalizedRows = BuildNormalizedRows(
                referenceAmplitude,
                validRows,
                options.DenominatorFloor);
            templateSource = referenceAmplitude;
        }

        var template = SmoothAndNormalize(PointwiseMedian(normalizedRows));
        var peakToPeaks = validRows
            .Select(row => RowPeakToPeak(templateSource, row))
            .Where(value => value > options.DenominatorFloor)
            .ToArray();
        var valleys = validRows
            .Select(row => RowMinimum(templateSource, row))
            .ToArray();
        var targetPeakToPeak = Median(peakToPeaks);
        var targetValley = Median(valleys);
        var output = targetReal.ToArray();
        var affectedCount = 0;
        var severeRows = 0;
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            var severe = detected.Contains(stimulation) || detected.Contains(Mod(stimulation + 1));
            if (severe)
            {
                severeRows++;
            }

            var rowPeakToPeak = RowPeakToPeak(targetAmplitude, stimulation);
            var rowValley = RowMinimum(targetAmplitude, stimulation);
            var lowerPeak = targetPeakToPeak * (1.0 - options.AmplitudeTolerance);
            var upperPeak = targetPeakToPeak * (1.0 + options.AmplitudeTolerance);
            var adjustedPeak = targetPeakToPeak <= options.DenominatorFloor
                ? rowPeakToPeak
                : Math.Clamp(rowPeakToPeak, lowerPeak, upperPeak);
            var adjustedValley = severe
                ? targetValley
                : rowValley + (options.MildValleyElasticity * (targetValley - rowValley));
            for (var column = 0; column < MeasurementsPerStimulation; column++)
            {
                var index = (stimulation * MeasurementsPerStimulation) + column;
                if (!CouplingRows[index].Any(detected.Contains))
                {
                    continue;
                }

                var synthesizedAmplitude = Math.Max(
                    0.0,
                    adjustedValley + (adjustedPeak * template[column]));
                var signSource = Math.Abs(targetReal[index]) > options.DenominatorFloor
                    ? targetReal[index]
                    : referenceReal[index];
                output[index] = signSource < 0.0 ? -synthesizedAmplitude : synthesizedAmplitude;
                affectedCount++;
            }
        }

        return new TemplateCompensation(
            template,
            output,
            affectedCount,
            validRows.Length,
            severeRows);
    }

    private static List<double[]> BuildNormalizedRows(
        IReadOnlyList<double> values,
        IReadOnlyList<int> rows,
        double floor)
    {
        var output = new List<double[]>(rows.Count);
        foreach (var row in rows)
        {
            var minimum = RowMinimum(values, row);
            var peakToPeak = RowPeakToPeak(values, row);
            if (peakToPeak <= floor)
            {
                continue;
            }

            var start = row * MeasurementsPerStimulation;
            output.Add(Enumerable.Range(0, MeasurementsPerStimulation)
                .Select(column => (values[start + column] - minimum) / (peakToPeak + floor))
                .ToArray());
        }

        return output;
    }

    private static double[] PointwiseMedian(IReadOnlyList<double[]> rows)
    {
        if (rows.Count == 0)
        {
            return new double[MeasurementsPerStimulation];
        }

        return Enumerable.Range(0, MeasurementsPerStimulation)
            .Select(column => Median(rows.Select(row => row[column]).ToArray()))
            .ToArray();
    }

    private static double[] SmoothAndNormalize(IReadOnlyList<double> values)
    {
        ReadOnlySpan<double> coefficients = [-3.0 / 35.0, 12.0 / 35.0, 17.0 / 35.0, 12.0 / 35.0, -3.0 / 35.0];
        var smoothed = new double[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var sum = 0.0;
            for (var offset = -2; offset <= 2; offset++)
            {
                sum += coefficients[offset + 2] * values[ReflectIndex(index + offset, values.Count)];
            }

            smoothed[index] = sum;
        }

        var minimum = smoothed.Min();
        var range = smoothed.Max() - minimum;
        return range <= double.Epsilon
            ? new double[values.Count]
            : smoothed.Select(value => Math.Clamp((value - minimum) / range, 0.0, 1.0)).ToArray();
    }

    private static int ReflectIndex(int index, int count)
    {
        while (index < 0 || index >= count)
        {
            index = index < 0 ? -index : (2 * count) - index - 2;
        }

        return index;
    }

    private static double RowMinimum(IReadOnlyList<double> values, int row)
    {
        return values.Skip(row * MeasurementsPerStimulation).Take(MeasurementsPerStimulation).Min();
    }

    private static double RowPeakToPeak(IReadOnlyList<double> values, int row)
    {
        var segment = values.Skip(row * MeasurementsPerStimulation).Take(MeasurementsPerStimulation).ToArray();
        return segment.Max() - segment.Min();
    }

    private static double PearsonCorrelation(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return 1.0;
        }

        var leftMean = left.Average();
        var rightMean = right.Average();
        var numerator = 0.0;
        var leftSquares = 0.0;
        var rightSquares = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftCentered = left[index] - leftMean;
            var rightCentered = right[index] - rightMean;
            numerator += leftCentered * rightCentered;
            leftSquares += leftCentered * leftCentered;
            rightSquares += rightCentered * rightCentered;
        }

        var denominator = Math.Sqrt(leftSquares * rightSquares);
        if (denominator <= double.Epsilon)
        {
            return left.SequenceEqual(right) ? 1.0 : 0.0;
        }

        return Math.Clamp(numerator / denominator, -1.0, 1.0);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var ordered = values.Where(double.IsFinite).Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0.0;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : 0.5 * (ordered[middle - 1] + ordered[middle]);
    }

    private static double[] ValidateVector(IReadOnlyList<double> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count != MeasurementCount)
        {
            throw new ArgumentException($"{name} length {values.Count} != {MeasurementCount}.", name);
        }

        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException($"{name} contains non-finite values.", name);
        }

        return values.ToArray();
    }

    private static double[] ValidateAmplitudeVector(IReadOnlyList<double> values, string name)
    {
        var output = ValidateVector(values, name);
        if (output.Any(value => value < 0.0))
        {
            throw new ArgumentException($"{name} contains negative amplitudes.", name);
        }

        return output;
    }

    private static void ValidateOptions(EcdCwrRong2026Options options)
    {
        if (!double.IsFinite(options.CurvatureWeight) || options.CurvatureWeight < 0.0 ||
            !double.IsFinite(options.LambdaFraction) || options.LambdaFraction is < 0.0 or > 1.0 ||
            !double.IsFinite(options.NoiseFloor) || options.NoiseFloor < 0.0 ||
            !double.IsFinite(options.AmplitudeTolerance) || options.AmplitudeTolerance is < 0.0 or >= 1.0 ||
            !double.IsFinite(options.MildValleyElasticity) || options.MildValleyElasticity is < 0.0 or > 1.0 ||
            !double.IsFinite(options.ConvergenceTolerance) || options.ConvergenceTolerance <= 0.0 ||
            !double.IsFinite(options.DenominatorFloor) || options.DenominatorFloor <= 0.0 ||
            options.MaxGapFaultCount is < 1 or > ElectrodeCount / 2 ||
            options.MaxIterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Rong 2026 options are outside supported bounds.");
        }
    }

    private static int[][] BuildCouplingRows()
    {
        var rows = new int[MeasurementCount][];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var column = 0; column < MeasurementsPerStimulation; column++)
            {
                var relative = column + RetainedRelativeChannelMin;
                rows[(stimulation * MeasurementsPerStimulation) + column] =
                [
                    stimulation,
                    Mod(stimulation + 1),
                    Mod(stimulation + relative),
                    Mod(stimulation + relative + 1)
                ];
            }
        }

        return rows;
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }

    private sealed record ElectrodeScore(int Electrode, double Score);

    private sealed record TemplateCompensation(
        double[] Template13,
        double[] CompensatedReal208,
        int AffectedMeasurementCount,
        int ValidTemplateRowCount,
        int SevereRowCount);
}

public sealed record EcdCwrRong2026Options(
    double CurvatureWeight = 2.0,
    double LambdaFraction = 0.05,
    double NoiseFloor = 0.02,
    double AmplitudeTolerance = 0.05,
    double MildValleyElasticity = 0.25,
    int MaxGapFaultCount = 8,
    int MaxIterations = 2000,
    double ConvergenceTolerance = 1.0e-10,
    double DenominatorFloor = 1.0e-12);

public sealed record EcdCwrRong2026Input(
    IReadOnlyList<double> ReferenceReal208,
    IReadOnlyList<double> ReferenceAmplitude208,
    IReadOnlyList<double> TargetReal208,
    IReadOnlyList<double> TargetAmplitude208);

public sealed record EcdCwrRong2026Result(
    string SchemaVersion,
    string PolicyVersion,
    string SourceDoi,
    string Equation7Interpretation,
    IReadOnlyList<string> OperationalAssumptions,
    IReadOnlyList<double> ReciprocityError208,
    IReadOnlyList<double> CurvatureError208,
    IReadOnlyList<double> ComprehensiveError208,
    IReadOnlyList<double> ElectrodeScores16,
    double L1Penalty,
    double ResidualRms,
    int GapIndex,
    double Threshold,
    IReadOnlyList<int> DetectedElectrodes,
    IReadOnlyList<double> Template13,
    IReadOnlyList<double> CompensatedReal208,
    int AffectedMeasurementCount,
    int ValidTemplateRowCount,
    int SevereRowCount);
