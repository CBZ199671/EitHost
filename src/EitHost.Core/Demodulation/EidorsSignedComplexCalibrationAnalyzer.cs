using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Demodulation;

public sealed class EidorsSignedComplexCalibrationAnalyzer
{
    public const int RequiredRetainedObservationCount = 208;
    public const int MinimumRealFrameCount = 30;
    public const double MinimumAbsoluteMedianShapeCorrelation = 0.98;
    public const double MinimumChosenVsOppositeCorrelationGap = 0.5;

    public EidorsSignedComplexCalibrationReport Analyze(
        string demodulatedHdf5Path,
        string eidorsReferenceHdf5Path)
    {
        var demodPath = Path.GetFullPath(demodulatedHdf5Path);
        var referencePath = Path.GetFullPath(eidorsReferenceHdf5Path);
        var hardwareReal = ReadDoubleMatrix(demodPath, "/demod/mean_real_16x16");
        var hardwareImaginary = ReadDoubleMatrix(demodPath, "/demod/mean_imag_16x16");
        var framesReal = ReadDoubleStack(demodPath, "/demod/frames_real_256");
        var modelReal = ReadEidorsReferenceRealMatrix(referencePath);
        ValidateMatrix(hardwareReal, "/demod/mean_real_16x16");
        ValidateMatrix(hardwareImaginary, "/demod/mean_imag_16x16");
        ValidateMatrix(modelReal, "/reference_complex_256");
        ValidateStack(framesReal, "/demod/frames_real_256");

        var hardwareShape = MedianRetainedShape(hardwareReal);
        var modelShape = MedianRetainedShape(modelReal);
        var before = PearsonCorrelation(hardwareShape, modelShape);
        var reverseReferenceEndpointOrder = before < 0.0;
        var after = reverseReferenceEndpointOrder ? -before : before;
        var frameCorrelationsBefore = Enumerable.Range(0, framesReal.GetLength(0))
            .Select(frame => PearsonCorrelation(MedianRetainedShape(framesReal, frame), modelShape))
            .ToArray();
        var allFramesChooseSameOrder = frameCorrelationsBefore.All(value =>
            double.IsFinite(value) && (value < 0.0) == reverseReferenceEndpointOrder);
        var frameCorrelationsAfter = frameCorrelationsBefore
            .Select(value => reverseReferenceEndpointOrder ? -value : value)
            .ToArray();
        var gap = after - (-after);
        var failures = new List<string>();
        if (framesReal.GetLength(0) < MinimumRealFrameCount)
        {
            failures.Add($"真实帧不足：{framesReal.GetLength(0)} < {MinimumRealFrameCount}。");
        }

        if (!double.IsFinite(after) || after < MinimumAbsoluteMedianShapeCorrelation)
        {
            failures.Add(
                $"中位形状相关不足：{after.ToString("F6", CultureInfo.InvariantCulture)} < {MinimumAbsoluteMedianShapeCorrelation:F2}。");
        }

        if (!double.IsFinite(gap) || gap < MinimumChosenVsOppositeCorrelationGap)
        {
            failures.Add(
                $"所选端点顺序与相反顺序差距不足：{gap.ToString("F6", CultureInfo.InvariantCulture)} < {MinimumChosenVsOppositeCorrelationGap:F1}。");
        }

        if (!allFramesChooseSameOrder)
        {
            failures.Add("真实帧未一致选择同一 EIDORS 端点顺序。");
        }

        return new EidorsSignedComplexCalibrationReport(
            DateTimeOffset.Now,
            demodPath,
            Sha256(demodPath),
            referencePath,
            Sha256(referencePath),
            RequiredRetainedObservationCount,
            framesReal.GetLength(0),
            reverseReferenceEndpointOrder
                ? "V(next_electrode)-V(first_electrode)"
                : "already_eidors_aligned_V(first_electrode)-V(next_electrode)",
            reverseReferenceEndpointOrder,
            before,
            after,
            -after,
            gap,
            Median(frameCorrelationsBefore),
            Median(frameCorrelationsAfter),
            frameCorrelationsAfter.Min(),
            frameCorrelationsAfter.Max(),
            allFramesChooseSameOrder,
            MedianRetainedValue(hardwareReal),
            reverseReferenceEndpointOrder
                ? -MedianRetainedValue(hardwareReal)
                : MedianRetainedValue(hardwareReal),
            MedianRetainedValue(hardwareImaginary),
            reverseReferenceEndpointOrder
                ? -MedianRetainedValue(hardwareImaginary)
                : MedianRetainedValue(hardwareImaginary),
            failures.Count == 0,
            failures);
    }

    public static string ToMarkdown(EidorsSignedComplexCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# EIDORS 有符号复电压标定");
        builder.AppendLine();
        builder.AppendLine($"- 结论：{(report.Passed ? "通过" : "未通过")}");
        builder.AppendLine($"- 实测 demod：`{report.DemodulatedHdf5Path}`");
        builder.AppendLine($"- 实测 SHA256：`{report.DemodHdf5Sha256}`");
        builder.AppendLine($"- PyEIDORS CEM 参考：`{report.EidorsReferenceHdf5Path}`");
        builder.AppendLine($"- 参考 SHA256：`{report.EidorsReferenceHdf5Sha256}`");
        builder.AppendLine($"- 保留测量：{report.RetainedObservationCount}");
        builder.AppendLine($"- 真实帧：{report.RealFrameCount}");
        builder.AppendLine($"- 推荐参考端点顺序：`{report.ReferenceEndpointOrder}`");
        builder.AppendLine($"- 需反转参考端点顺序：{report.ReferenceEndpointReversalRequired}");
        builder.AppendLine($"- 中位形状相关（输入）：{report.MedianShapeCorrelationBefore:F6}");
        builder.AppendLine($"- 中位形状相关（EIDORS 对齐）：{report.MedianShapeCorrelationAfter:F6}");
        builder.AppendLine($"- 相反端点顺序相关：{report.OppositeEndpointShapeCorrelation:F6}");
        builder.AppendLine($"- 相关差距：{report.ChosenVsOppositeCorrelationGap:F6}");
        builder.AppendLine($"- 对齐后逐帧相关中位/最小/最大：{report.FrameCorrelationMedianAfter:F6}/{report.FrameCorrelationMinimumAfter:F6}/{report.FrameCorrelationMaximumAfter:F6}");
        builder.AppendLine($"- 全帧同向：{report.AllFramesChooseEidorsEndpointOrder}");
        builder.AppendLine($"- 实部中位（输入→对齐）：{report.MedianRealBefore:G9} V → {report.MedianRealAfter:G9} V");
        builder.AppendLine($"- 虚部中位（输入→对齐）：{report.MedianImaginaryBefore:G9} V → {report.MedianImaginaryAfter:G9} V");
        if (report.Failures.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 未通过项");
            builder.AppendLine();
            foreach (var failure in report.Failures)
            {
                builder.AppendLine($"- {failure}");
            }
        }

        return builder.ToString();
    }

    private static double[,] ReadDoubleMatrix(string path, string datasetPath)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubles))
        {
            return doubles;
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floats))
        {
            return ConvertReal(floats);
        }

        throw new InvalidDataException($"Unsupported real matrix dataset at {datasetPath}.");
    }

    private static double[,,] ReadDoubleStack(string path, string datasetPath)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[,,]>(memoryDims: dimensions), out var doubles))
        {
            return doubles;
        }

        if (TryRead(() => dataset.Read<float[,,]>(memoryDims: dimensions), out var floats))
        {
            var result = new double[floats.GetLength(0), floats.GetLength(1), floats.GetLength(2)];
            for (var frame = 0; frame < floats.GetLength(0); frame++)
            {
                for (var row = 0; row < floats.GetLength(1); row++)
                {
                    for (var column = 0; column < floats.GetLength(2); column++)
                    {
                        result[frame, row, column] = floats[frame, row, column];
                    }
                }
            }

            return result;
        }

        throw new InvalidDataException($"Unsupported real stack dataset at {datasetPath}.");
    }

    private static double[,] ReadEidorsReferenceRealMatrix(string path)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        const string datasetPath = "/reference_complex_256";
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (dataset.Type.Class == H5DataTypeClass.Compound &&
            TryRead(() => dataset.Read<Hdf5Complex64[,]>(memoryDims: dimensions), out var complex))
        {
            var result = new double[complex.GetLength(0), complex.GetLength(1)];
            for (var row = 0; row < complex.GetLength(0); row++)
            {
                for (var column = 0; column < complex.GetLength(1); column++)
                {
                    result[row, column] = complex[row, column].Real;
                }
            }

            return result;
        }

        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubles))
        {
            return doubles;
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floats))
        {
            return ConvertReal(floats);
        }

        throw new InvalidDataException($"Unsupported complex dataset type at {datasetPath}.");
    }

    private static double[] MedianRetainedShape(double[,] matrix)
    {
        return Enumerable.Range(2, 13)
            .Select(column => Median(Enumerable.Range(0, 16).Select(row => matrix[row, column])))
            .ToArray();
    }

    private static double[] MedianRetainedShape(double[,,] stack, int frame)
    {
        return Enumerable.Range(2, 13)
            .Select(column => Median(Enumerable.Range(0, 16).Select(row => stack[frame, row, column])))
            .ToArray();
    }

    private static double MedianRetainedValue(double[,] matrix)
    {
        return Median(
            Enumerable.Range(0, 16)
                .SelectMany(row => Enumerable.Range(2, 13).Select(column => matrix[row, column])));
    }

    private static double PearsonCorrelation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return double.NaN;
        }

        var leftMean = left.Average();
        var rightMean = right.Average();
        var numerator = 0.0;
        var leftEnergy = 0.0;
        var rightEnergy = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftCentered = left[index] - leftMean;
            var rightCentered = right[index] - rightMean;
            numerator += leftCentered * rightCentered;
            leftEnergy += leftCentered * leftCentered;
            rightEnergy += rightCentered * rightCentered;
        }

        var denominator = Math.Sqrt(leftEnergy * rightEnergy);
        return denominator > double.Epsilon ? numerator / denominator : double.NaN;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(double.IsFinite).Order().ToArray();
        if (ordered.Length == 0)
        {
            return double.NaN;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? 0.5 * (ordered[middle - 1] + ordered[middle])
            : ordered[middle];
    }

    private static void ValidateMatrix(double[,] matrix, string datasetPath)
    {
        if (matrix.GetLength(0) != 16 || matrix.GetLength(1) != 16)
        {
            throw new InvalidDataException($"{datasetPath} shape must be (16,16).");
        }
    }

    private static void ValidateStack(double[,,] stack, string datasetPath)
    {
        if (stack.GetLength(1) != 16 || stack.GetLength(2) != 16)
        {
            throw new InvalidDataException($"{datasetPath} shape must be (frame,16,16).");
        }
    }

    private static double[,] ConvertReal(float[,] source)
    {
        var result = new double[source.GetLength(0), source.GetLength(1)];
        for (var row = 0; row < source.GetLength(0); row++)
        {
            for (var column = 0; column < source.GetLength(1); column++)
            {
                result[row, column] = source[row, column];
            }
        }

        return result;
    }

    private static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

#pragma warning disable CS0649
    private struct Hdf5Complex64
    {
        [H5Name("r")]
        public float Real;

        [H5Name("i")]
        public float Imaginary;
    }
#pragma warning restore CS0649
}

public sealed record EidorsSignedComplexCalibrationReport(
    DateTimeOffset GeneratedAt,
    string DemodulatedHdf5Path,
    string DemodHdf5Sha256,
    string EidorsReferenceHdf5Path,
    string EidorsReferenceHdf5Sha256,
    int RetainedObservationCount,
    int RealFrameCount,
    string ReferenceEndpointOrder,
    bool ReferenceEndpointReversalRequired,
    double MedianShapeCorrelationBefore,
    double MedianShapeCorrelationAfter,
    double OppositeEndpointShapeCorrelation,
    double ChosenVsOppositeCorrelationGap,
    double FrameCorrelationMedianBefore,
    double FrameCorrelationMedianAfter,
    double FrameCorrelationMinimumAfter,
    double FrameCorrelationMaximumAfter,
    bool AllFramesChooseEidorsEndpointOrder,
    double MedianRealBefore,
    double MedianRealAfter,
    double MedianImaginaryBefore,
    double MedianImaginaryAfter,
    bool Passed,
    IReadOnlyList<string> Failures);
