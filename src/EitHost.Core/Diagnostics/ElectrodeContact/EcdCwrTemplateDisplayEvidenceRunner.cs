using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EitHost.Core.Demodulation;
using EitHost.Core.Storage.Frames;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrTemplateDisplayEvidenceRunner
{
    public const string SchemaVersion = "ecd-cwr-template-display-evidence-v1";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public EcdCwrTemplateDisplayEvidenceReport Run(
        string realtimeReportPath,
        string calibrationPath,
        string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realtimeReportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        realtimeReportPath = Path.GetFullPath(realtimeReportPath);
        calibrationPath = Path.GetFullPath(calibrationPath);
        sqlitePath = Path.GetFullPath(sqlitePath);
        if (!File.Exists(realtimeReportPath))
        {
            throw new FileNotFoundException("Realtime demodulation evidence report not found.", realtimeReportPath);
        }

        if (!File.Exists(calibrationPath))
        {
            throw new FileNotFoundException("ECD-CWR health calibration not found.", calibrationPath);
        }

        var source = ReadSourceFrame(realtimeReportPath);
        var calibration = new EcdCwrHealthCalibrationStore().Load(calibrationPath);
        var frame = CreateFrame(source);
        var amplitudeBefore = source.MeanAmplitude208.ToArray();
        var realBefore = source.MeanReal208.ToArray();
        var imaginaryBefore = source.MeanImaginary208.ToArray();
        var package = new EcdCwrWaveformTemplateDisplayBuilder().Build(frame, calibration);
        var payload = JsonSerializer.Serialize(package, PayloadJsonOptions);
        var vectorsUnchanged = amplitudeBefore.SequenceEqual(source.MeanAmplitude208) &&
            realBefore.SequenceEqual(source.MeanReal208) &&
            imaginaryBefore.SequenceEqual(source.MeanImaginary208);
        var finite = package.Windows.Count == DemodulatedFrame.StimulationCount &&
            package.Windows.All(window =>
                window.DisplayOnly &&
                window.RelativeChannelIndices.Count == DemodulatedFrame.MeasurementsPerStimulation &&
                window.ObservedAmplitudes.Count == DemodulatedFrame.MeasurementsPerStimulation &&
                window.ExpectedDisplayAmplitudes.Count == DemodulatedFrame.MeasurementsPerStimulation &&
                window.ResidualAmplitudes.Count == DemodulatedFrame.MeasurementsPerStimulation &&
                window.ObservedAmplitudes
                    .Concat(window.ExpectedDisplayAmplitudes)
                    .Concat(window.ResidualAmplitudes)
                    .All(double.IsFinite));
        var residuals = package.Windows.SelectMany(window => window.ResidualAmplitudes).ToArray();
        var residualRms = residuals.Length == 0
            ? double.NaN
            : Math.Sqrt(residuals.Select(value => value * value).Average());
        var maxAbsoluteResidual = residuals.Length == 0
            ? double.NaN
            : residuals.Max(Math.Abs);

        var runId = Guid.NewGuid();
        var store = new EitFrameStore(sqlitePath);
        store.Initialize();
        using (var connection = store.OpenWriteConnection())
        {
            store.BeginRun(
                connection,
                new ImagingRunConfigRecord(
                    runId,
                    Guid.NewGuid(),
                    source.SetLabel,
                    source.CapturedAt,
                    "template-display-evidence-only",
                    0.0,
                    false,
                    0.0,
                    source.FrequencyHz,
                    source.ChannelCycles,
                    source.SampleRateHz,
                    "target-reference",
                    RealtimeStoragePolicy.ImagingValue));
            store.AppendFrame(
                connection,
                new ImagingFrameRecord(
                    runId,
                    source.BlockNumber,
                    source.CapturedAt,
                    1.0,
                    source.AcceptedFrameCount,
                    source.RejectedFrameCount,
                    source.MeanAmplitude208,
                    source.MeanReal208,
                    source.MeanImaginary208,
                    MeasurementWeight208: Enumerable.Repeat(1.0, EitFrameStore.BoundaryVectorLength).ToArray(),
                    WeightPolicyVersion: "all-one-v1",
                    DisplayCompensationPolicy: package.PolicyVersion,
                    DisplayCompensationOnly: package.DisplayOnly,
                    DisplayCompensationPayloadJson: payload,
                    ReferenceStatus: "real-water-template-display-evidence"));
        }

        var stored = store.GetFrame(runId, source.BlockNumber);
        var storageRoundtrip = stored is not null &&
            string.Equals(stored.DisplayCompensationPolicy, package.PolicyVersion, StringComparison.Ordinal) &&
            stored.DisplayCompensationOnly &&
            string.Equals(stored.DisplayCompensationPayloadJson, payload, StringComparison.Ordinal) &&
            stored.MeanAmplitude208.SequenceEqual(amplitudeBefore) &&
            stored.MeanReal208.SequenceEqual(realBefore) &&
            stored.MeanImaginary208.SequenceEqual(imaginaryBefore) &&
            stored.MeasurementWeight208?.All(weight => Math.Abs(weight - 1.0) <= 1.0e-12) == true;
        var frequencyMatches = Math.Abs(calibration.FrequencyHz - source.FrequencyHz) <= 1.0e-9;
        var passed = calibration.Quality.Passed &&
            frequencyMatches &&
            package.DisplayOnly &&
            string.Equals(
                package.PolicyVersion,
                EcdCwrWaveformTemplateDisplayBuilder.DisplayPolicyVersion,
                StringComparison.Ordinal) &&
            finite &&
            vectorsUnchanged &&
            storageRoundtrip;
        return new EcdCwrTemplateDisplayEvidenceReport(
            SchemaVersion,
            DateTimeOffset.Now,
            realtimeReportPath,
            calibrationPath,
            sqlitePath,
            runId,
            source.SetLabel,
            source.BlockNumber,
            source.FrequencyHz,
            calibration.FrameCount,
            calibration.Quality.Contact48WhitenedResidualP99,
            calibration.Quality.RejectedFrameCount,
            frequencyMatches,
            package.PolicyVersion,
            package.DisplayOnly,
            package.Windows.Count,
            package.Windows.Sum(window => window.ObservedAmplitudes.Count),
            finite,
            vectorsUnchanged,
            storageRoundtrip,
            residualRms,
            maxAbsoluteResidual,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            passed);
    }

    public static string ToMarkdown(EcdCwrTemplateDisplayEvidenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# ECD-CWR 真实水桶模板显示证据");
        builder.AppendLine();
        builder.AppendLine($"- 生成时间：{report.GeneratedAt:O}");
        builder.AppendLine($"- 结论：{(report.Passed ? "PASS" : "FAIL")}");
        builder.AppendLine($"- 数据源：`{report.RealtimeReportPath}`");
        builder.AppendLine($"- 健康标定：`{report.CalibrationPath}`");
        builder.AppendLine($"- SQLite：`{report.SqlitePath}`");
        builder.AppendLine($"- 标定帧：{report.CalibrationFrameCount}，P99={report.CalibrationContact48P99:F6}，剔除={report.CalibrationRejectedFrameCount}");
        builder.AppendLine($"- 显示策略：`{report.DisplayPolicyVersion}`，display-only={report.DisplayOnly}");
        builder.AppendLine($"- 模板窗口/点数：{report.WindowCount}/{report.PointCount}");
        builder.AppendLine($"- 数值有限：{report.AllFinite}");
        builder.AppendLine($"- 原始重构向量未变：{report.ReconstructionVectorsUnchanged}");
        builder.AppendLine($"- SQLite 写读回环：{report.StorageRoundtripPassed}");
        builder.AppendLine($"- residual RMS/max：{report.ResidualRms:G8}/{report.MaxAbsoluteResidual:G8}");
        builder.AppendLine($"- payload SHA256：`{report.PayloadSha256}`");
        return builder.ToString();
    }

    private static RealWaterSourceFrame ReadSourceFrame(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var options = Property(root, "Options", "options");
        var setLabel = StringProperty(options, "SetLabel", "set_label") ?? "EIT-01";
        var frequencyHz = DoubleProperty(options, "ExcitationFrequencyHz", "excitation_frequency_hz");
        var sampleRateHz = DoubleProperty(options, "SampleRateHz", "sample_rate_hz");
        var channelCycles = DoubleProperty(options, "ChannelCycles", "channel_cycles");
        var capturedAtText = StringProperty(root, "EndedAt", "ended_at");
        var capturedAt = DateTimeOffset.TryParse(capturedAtText, out var parsed)
            ? parsed
            : DateTimeOffset.Now;
        JsonElement? selected = null;
        foreach (var block in Property(root, "Blocks", "blocks").EnumerateArray())
        {
            if (BoolProperty(block, "IsHighQuality", "is_high_quality"))
            {
                selected = block.Clone();
            }
        }

        if (selected is not { } source)
        {
            throw new InvalidDataException("Realtime report contains no high-quality block.");
        }

        return new RealWaterSourceFrame(
            setLabel,
            IntProperty(source, "BlockNumber", "block_number"),
            capturedAt,
            frequencyHz,
            sampleRateHz,
            channelCycles,
            IntProperty(source, "AcceptedFrameCount", "accepted_frame_count"),
            IntProperty(source, "RejectedFrameCount", "rejected_frame_count"),
            DoubleArray(source, "MeanAmplitude208", "mean_amplitude208"),
            DoubleArray(source, "MeanReal208", "mean_real208"),
            DoubleArray(source, "MeanImaginary208", "mean_imaginary208"));
    }

    private static DemodulatedFrame CreateFrame(RealWaterSourceFrame source)
    {
        return new DemodulatedFrame(
            1,
            0,
            0,
            Matrix208(source.MeanAmplitude208),
            Matrix208(source.MeanReal208),
            Matrix208(source.MeanImaginary208),
            [],
            new int[DemodulatedFrame.StimulationCount, 3]);
    }

    private static double[,] Matrix208(IReadOnlyList<double> values)
    {
        if (values.Count != DemodulatedFrame.FlattenedMeasurementCount || values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException("Realtime report must contain a finite 208-point vector.");
        }

        var matrix = new double[DemodulatedFrame.StimulationCount, DemodulatedFrame.MeasurementsPerStimulation];
        for (var index = 0; index < values.Count; index++)
        {
            matrix[index / DemodulatedFrame.MeasurementsPerStimulation,
                index % DemodulatedFrame.MeasurementsPerStimulation] = values[index];
        }

        return matrix;
    }

    private static JsonElement Property(JsonElement element, string pascalName, string snakeName)
    {
        if (element.TryGetProperty(pascalName, out var value) || element.TryGetProperty(snakeName, out value))
        {
            return value;
        }

        throw new InvalidDataException($"Realtime report is missing {pascalName}.");
    }

    private static string? StringProperty(JsonElement element, string pascalName, string snakeName)
    {
        var value = Property(element, pascalName, snakeName);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static int IntProperty(JsonElement element, string pascalName, string snakeName)
    {
        return Property(element, pascalName, snakeName).GetInt32();
    }

    private static double DoubleProperty(JsonElement element, string pascalName, string snakeName)
    {
        return Property(element, pascalName, snakeName).GetDouble();
    }

    private static bool BoolProperty(JsonElement element, string pascalName, string snakeName)
    {
        return Property(element, pascalName, snakeName).GetBoolean();
    }

    private static double[] DoubleArray(JsonElement element, string pascalName, string snakeName)
    {
        var values = Property(element, pascalName, snakeName)
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        if (values.Length != EitFrameStore.BoundaryVectorLength || values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException($"Realtime report {pascalName} is not a finite 208-point vector.");
        }

        return values;
    }

    private sealed record RealWaterSourceFrame(
        string SetLabel,
        int BlockNumber,
        DateTimeOffset CapturedAt,
        double FrequencyHz,
        double SampleRateHz,
        double ChannelCycles,
        int AcceptedFrameCount,
        int RejectedFrameCount,
        double[] MeanAmplitude208,
        double[] MeanReal208,
        double[] MeanImaginary208);
}

public sealed record EcdCwrTemplateDisplayEvidenceReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string RealtimeReportPath,
    string CalibrationPath,
    string SqlitePath,
    Guid ImagingRunId,
    string SetLabel,
    int BlockNumber,
    double FrequencyHz,
    int CalibrationFrameCount,
    double CalibrationContact48P99,
    int CalibrationRejectedFrameCount,
    bool FrequencyMatches,
    string DisplayPolicyVersion,
    bool DisplayOnly,
    int WindowCount,
    int PointCount,
    bool AllFinite,
    bool ReconstructionVectorsUnchanged,
    bool StorageRoundtripPassed,
    double ResidualRms,
    double MaxAbsoluteResidual,
    string PayloadSha256,
    bool Passed);
