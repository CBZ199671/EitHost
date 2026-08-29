using System.Text.Json;
using System.Text.Json.Serialization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrDeviceCalibrationSchema
{
    public const string Version = "ecd-cwr-device-calibration-v1";
}

public static class EcdCwrSessionCalibrationSchema
{
    public const string Version = "ecd-cwr-session-calibration-v1";
}

public sealed record EcdCwrDeviceReciprocityCorrection(
    int StimulationIndex,
    int RelativeChannelIndex,
    int ReciprocalStimulationIndex,
    int ReciprocalRelativeChannelIndex,
    int Sign,
    double ComplexGainReal,
    double ComplexGainImaginary);

public sealed record EcdCwrDeviceCalibration(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    string DeviceLabel,
    double FrequencyHz,
    int SourceFrameCount,
    double SourceQualityP99,
    IReadOnlyList<EcdCwrDeviceReciprocityCorrection> ReciprocityCorrections);

public sealed record EcdCwrSessionCalibration(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    Guid ImagingRunId,
    int ReferenceGeneration,
    int ReferenceBlockNumber,
    string DeviceLabel,
    double FrequencyHz,
    int ReferenceFrameCount,
    int RejectedReferenceFrameCount,
    IReadOnlyList<double> ReferenceVoltage208,
    IReadOnlyList<double> ReferenceFullReal256,
    IReadOnlyList<double> ReferenceFullImaginary256,
    EcdCwrHealthCalibration HealthCalibration);

public sealed class EcdCwrDeviceCalibrationBuilder
{
    public EcdCwrDeviceCalibration Create(EcdCwrHealthCalibration healthCalibration)
    {
        ValidateHealthCalibration(healthCalibration);
        return new EcdCwrDeviceCalibration(
            EcdCwrDeviceCalibrationSchema.Version,
            DateTimeOffset.Now,
            healthCalibration.DeviceLabel,
            healthCalibration.FrequencyHz,
            healthCalibration.FrameCount,
            healthCalibration.Quality.Contact48WhitenedResidualP99,
            healthCalibration.ReciprocalPairs
                .Select(pair => new EcdCwrDeviceReciprocityCorrection(
                    pair.StimulationIndex,
                    pair.RelativeChannelIndex,
                    pair.ReciprocalStimulationIndex,
                    pair.ReciprocalRelativeChannelIndex,
                    pair.Sign,
                    pair.ComplexGainReal,
                    pair.ComplexGainImaginary))
                .ToArray());
    }

    internal static void ValidateHealthCalibration(EcdCwrHealthCalibration healthCalibration)
    {
        ArgumentNullException.ThrowIfNull(healthCalibration);
        if (!string.Equals(
                healthCalibration.SchemaVersion,
                EcdCwrHealthCalibrationSchema.Version,
                StringComparison.Ordinal) ||
            !healthCalibration.Quality.Passed ||
            healthCalibration.FrameCount < 100)
        {
            throw new ArgumentException("Calibration artifact requires a passed >=100-frame health calibration.", nameof(healthCalibration));
        }
    }
}

public sealed class EcdCwrSessionCalibrationBuilder
{
    public EcdCwrSessionCalibration Create(
        EcdCwrHealthCalibration healthCalibration,
        EcdCwrRobustReference robustReference,
        Guid imagingRunId,
        int referenceGeneration,
        int referenceBlockNumber)
    {
        EcdCwrDeviceCalibrationBuilder.ValidateHealthCalibration(healthCalibration);
        ArgumentNullException.ThrowIfNull(robustReference);
        if (robustReference.Voltage208.Length != 208 ||
            robustReference.FullReal256.Length != 256 ||
            robustReference.FullImaginary256.Length != 256 ||
            robustReference.FrameCount < 100 ||
            robustReference.Voltage208.Any(value => !double.IsFinite(value)) ||
            robustReference.FullReal256.Any(value => !double.IsFinite(value)) ||
            robustReference.FullImaginary256.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Session calibration requires finite 208/256 robust reference vectors from >=100 frames.", nameof(robustReference));
        }

        if (imagingRunId == Guid.Empty || referenceGeneration < 0 || referenceBlockNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceBlockNumber));
        }

        return new EcdCwrSessionCalibration(
            EcdCwrSessionCalibrationSchema.Version,
            DateTimeOffset.Now,
            imagingRunId,
            referenceGeneration,
            referenceBlockNumber,
            healthCalibration.DeviceLabel,
            healthCalibration.FrequencyHz,
            robustReference.FrameCount,
            robustReference.RejectedFrameCount,
            robustReference.Voltage208.ToArray(),
            robustReference.FullReal256.ToArray(),
            robustReference.FullImaginary256.ToArray(),
            healthCalibration);
    }
}

public sealed class EcdCwrDeviceCalibrationStore
{
    public void Save(string path, EcdCwrDeviceCalibration calibration)
    {
        CalibrationArtifactJson.Save(path, calibration);
    }

    public EcdCwrDeviceCalibration Load(string path)
    {
        var calibration = CalibrationArtifactJson.Load<EcdCwrDeviceCalibration>(path);
        if (!string.Equals(calibration.SchemaVersion, EcdCwrDeviceCalibrationSchema.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported device calibration schema '{calibration.SchemaVersion}'.");
        }

        return calibration;
    }
}

public sealed class EcdCwrSessionCalibrationStore
{
    public void Save(string path, EcdCwrSessionCalibration calibration)
    {
        CalibrationArtifactJson.Save(path, calibration);
    }

    public EcdCwrSessionCalibration Load(string path)
    {
        var calibration = CalibrationArtifactJson.Load<EcdCwrSessionCalibration>(path);
        if (!string.Equals(calibration.SchemaVersion, EcdCwrSessionCalibrationSchema.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported session calibration schema '{calibration.SchemaVersion}'.");
        }

        EcdCwrDeviceCalibrationBuilder.ValidateHealthCalibration(calibration.HealthCalibration);
        return calibration;
    }
}

internal static class CalibrationArtifactJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static void Save<T>(string path, T calibration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(calibration);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(calibration, Options));
    }

    internal static T Load<T>(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("Calibration file is empty or invalid.");
    }
}
