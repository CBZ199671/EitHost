using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrAdaptiveContactProfileSchema
{
    public const string Version = "ecd-cwr-adaptive-contact-profile-v1";
}

public sealed record EcdCwrOperatingFingerprint(
    string DeviceLabel,
    string FirmwareVersion,
    string FirmwareBuildId,
    double ExcitationFrequencyHz,
    double DacGain,
    int DacPhaseDegrees,
    int PgaGain,
    int SampleRateHz,
    double ChannelCycles,
    double DiscardLeadingCycles,
    double DiscardTrailingCycles,
    string SubjectProfile,
    string AlgorithmVersion)
{
    public bool HasVerifiableFirmwareBuild =>
        !string.Equals(FirmwareBuildId.Trim(), "unreported", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(FirmwareBuildId.Trim(), "unknown", StringComparison.OrdinalIgnoreCase);

    public string CanonicalKey
    {
        get
        {
            Validate();
            return string.Join(
                '|',
                Normalize(DeviceLabel),
                Normalize(FirmwareVersion),
                Normalize(FirmwareBuildId),
                ExcitationFrequencyHz.ToString("R", CultureInfo.InvariantCulture),
                DacGain.ToString("R", CultureInfo.InvariantCulture),
                DacPhaseDegrees.ToString(CultureInfo.InvariantCulture),
                PgaGain.ToString(CultureInfo.InvariantCulture),
                SampleRateHz.ToString(CultureInfo.InvariantCulture),
                ChannelCycles.ToString("R", CultureInfo.InvariantCulture),
                DiscardLeadingCycles.ToString("R", CultureInfo.InvariantCulture),
                DiscardTrailingCycles.ToString("R", CultureInfo.InvariantCulture),
                Normalize(SubjectProfile),
                Normalize(AlgorithmVersion));
        }
    }

    public string FingerprintId
    {
        get
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalKey));
            return Convert.ToHexStringLower(digest)[..16];
        }
    }

    public bool IsExactMatch(EcdCwrOperatingFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(CanonicalKey, other.CanonicalKey, StringComparison.Ordinal);
    }

    public IReadOnlyList<string> DescribeDifferences(EcdCwrOperatingFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<string>();
        AddStringDifference(differences, "device", DeviceLabel, other.DeviceLabel);
        AddStringDifference(differences, "firmware-version", FirmwareVersion, other.FirmwareVersion);
        AddStringDifference(differences, "firmware-build", FirmwareBuildId, other.FirmwareBuildId);
        AddDoubleDifference(differences, "excitation-hz", ExcitationFrequencyHz, other.ExcitationFrequencyHz);
        AddDoubleDifference(differences, "dac-gain", DacGain, other.DacGain);
        AddValueDifference(differences, "dac-phase", DacPhaseDegrees, other.DacPhaseDegrees);
        AddValueDifference(differences, "pga", PgaGain, other.PgaGain);
        AddValueDifference(differences, "sample-rate", SampleRateHz, other.SampleRateHz);
        AddDoubleDifference(differences, "channel-cycles", ChannelCycles, other.ChannelCycles);
        AddDoubleDifference(differences, "discard-leading", DiscardLeadingCycles, other.DiscardLeadingCycles);
        AddDoubleDifference(differences, "discard-trailing", DiscardTrailingCycles, other.DiscardTrailingCycles);
        AddStringDifference(differences, "subject", SubjectProfile, other.SubjectProfile);
        AddStringDifference(differences, "algorithm", AlgorithmVersion, other.AlgorithmVersion);
        return differences;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(FirmwareVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(FirmwareBuildId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SubjectProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(AlgorithmVersion);
        if (!double.IsFinite(ExcitationFrequencyHz) || ExcitationFrequencyHz <= 0.0 ||
            !double.IsFinite(DacGain) || DacGain <= 0.0 ||
            DacPhaseDegrees is < 0 or >= 360 ||
            PgaGain <= 0 ||
            SampleRateHz <= 0 ||
            !double.IsFinite(ChannelCycles) || ChannelCycles <= 0.0 ||
            !double.IsFinite(DiscardLeadingCycles) || DiscardLeadingCycles < 0.0 ||
            !double.IsFinite(DiscardTrailingCycles) || DiscardTrailingCycles < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(EcdCwrOperatingFingerprint));
        }
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToUpperInvariant();
    }

    private static void AddStringDifference(
        ICollection<string> differences,
        string name,
        string left,
        string right)
    {
        if (!string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal))
        {
            differences.Add(name);
        }
    }

    private static void AddDoubleDifference(
        ICollection<string> differences,
        string name,
        double left,
        double right)
    {
        if (BitConverter.DoubleToInt64Bits(left) != BitConverter.DoubleToInt64Bits(right))
        {
            differences.Add(name);
        }
    }

    private static void AddValueDifference<T>(
        ICollection<string> differences,
        string name,
        T left,
        T right)
        where T : IEquatable<T>
    {
        if (!left.Equals(right))
        {
            differences.Add(name);
        }
    }
}

public sealed record EcdCwrHealthyContactScoreObservation(
    double[] ElectrodeSpecificScores,
    double[] DrivePairScores,
    bool HighQuality,
    bool KnownAllConnected);

public sealed record EcdCwrHealthyScoreStatistic(
    int ElectrodeIndex,
    double ElectrodeMedian,
    double ElectrodeMad,
    double ElectrodeP99,
    double ElectrodeP999,
    double DrivePairMedian,
    double DrivePairMad,
    double DrivePairP99,
    double DrivePairP999);

public sealed record EcdCwrAdaptiveContactThresholds(
    double YellowEntry,
    double RedEntry,
    double RedRelease,
    double DirectAConfirmation,
    double DrivePairActiveMedian)
{
    public void Validate()
    {
        if (!double.IsFinite(YellowEntry) || YellowEntry <= 0.0 ||
            !double.IsFinite(RedEntry) || RedEntry <= YellowEntry ||
            !double.IsFinite(RedRelease) || RedRelease < YellowEntry || RedRelease >= RedEntry ||
            !double.IsFinite(DirectAConfirmation) || DirectAConfirmation < RedEntry ||
            !double.IsFinite(DrivePairActiveMedian) || DrivePairActiveMedian < YellowEntry)
        {
            throw new InvalidOperationException("Adaptive contact thresholds are invalid.");
        }
    }

    public EcdCwrPreReferenceContactOptions ApplyTo(EcdCwrPreReferenceContactOptions? baseline = null)
    {
        Validate();
        var source = baseline ?? new EcdCwrPreReferenceContactOptions();
        return source with
        {
            Relative48CandidateThreshold = YellowEntry,
            ConfirmedRelative48Threshold = RedEntry,
            ConfirmedRelative48ReleaseThreshold = RedRelease,
            DirectAOnlyMinimumScore = DirectAConfirmation,
            DrivePairConsensusMinimumActiveMedianScore = DrivePairActiveMedian,
            SevereUnilateralConfirmationMinimumScore =
                CalculateSevereUnilateralConfirmationMinimumScore(source.HardFaultScore)
        };
    }

    public double CalculateSevereUnilateralConfirmationMinimumScore(double hardFaultScore = 15.0)
    {
        Validate();
        if (!double.IsFinite(hardFaultScore) || hardFaultScore <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardFaultScore));
        }

        return Math.Min(hardFaultScore * 0.6, DirectAConfirmation * 2.0);
    }
}

public sealed record EcdCwrAdaptiveContactProfile(
    string SchemaVersion,
    string ProfileId,
    DateTimeOffset CreatedAt,
    EcdCwrOperatingFingerprint Fingerprint,
    int HealthyFrameCount,
    IReadOnlyList<EcdCwrHealthyScoreStatistic> HealthyStatistics,
    EcdCwrAdaptiveContactThresholds Thresholds,
    string? SourceLabel = null)
{
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, EcdCwrAdaptiveContactProfileSchema.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported adaptive contact profile schema '{SchemaVersion}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ProfileId);
        Fingerprint.Validate();
        Thresholds.Validate();
        if (HealthyFrameCount < 100 || HealthyStatistics.Count != ElectrodeContactBaseline.ElectrodeCount)
        {
            throw new InvalidOperationException("Adaptive contact profile requires >=100 healthy frames and 16 electrode statistics.");
        }
    }
}

public sealed record EcdCwrAdaptiveContactProfileBuilderOptions(
    int MinimumHealthyFrameCount = 100,
    double YellowQuantile = 0.99,
    double RedQuantile = 0.999,
    double YellowMargin = 0.5,
    double RedMargin = 1.0,
    double MinimumYellowThreshold = 2.5,
    double MinimumRedSeparation = 1.0,
    double ReleaseHysteresis = 0.75,
    double DirectAExtra = 0.5,
    double MaximumYellowThreshold = 8.0,
    double MaximumRedThreshold = 12.0);

public sealed class EcdCwrAdaptiveContactProfileBuilder
{
    public EcdCwrAdaptiveContactProfile Create(
        EcdCwrOperatingFingerprint fingerprint,
        IReadOnlyList<EcdCwrHealthyContactScoreObservation> observations,
        DateTimeOffset createdAt,
        string? sourceLabel = null,
        EcdCwrAdaptiveContactProfileBuilderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(observations);
        fingerprint.Validate();
        if (!fingerprint.HasVerifiableFirmwareBuild)
        {
            throw new InvalidOperationException("Adaptive contact profile requires a verifiable firmware build id.");
        }

        options ??= new EcdCwrAdaptiveContactProfileBuilderOptions();
        ValidateOptions(options);

        var retained = observations
            .Where(observation => observation.HighQuality && observation.KnownAllConnected)
            .ToArray();
        if (retained.Length < options.MinimumHealthyFrameCount)
        {
            throw new InvalidOperationException(
                $"Adaptive contact profile requires at least {options.MinimumHealthyFrameCount} known-all-connected high-quality frames.");
        }

        foreach (var observation in retained)
        {
            ValidateObservation(observation);
        }

        var statistics = Enumerable.Range(0, ElectrodeContactBaseline.ElectrodeCount)
            .Select(electrode => CreateStatistic(retained, electrode))
            .ToArray();
        var familyWiseMaximums = retained
            .Select(observation => Math.Max(
                observation.ElectrodeSpecificScores.Max(),
                observation.DrivePairScores.Max()))
            .ToArray();
        var yellow = Math.Clamp(
            Percentile(familyWiseMaximums, options.YellowQuantile) + options.YellowMargin,
            options.MinimumYellowThreshold,
            options.MaximumYellowThreshold);
        var red = Math.Clamp(
            Math.Max(
                yellow + options.MinimumRedSeparation,
                Percentile(familyWiseMaximums, options.RedQuantile) + options.RedMargin),
            yellow + options.MinimumRedSeparation,
            options.MaximumRedThreshold);
        var release = Math.Max(yellow, red - options.ReleaseHysteresis);
        if (release >= red)
        {
            release = yellow;
        }

        var thresholds = new EcdCwrAdaptiveContactThresholds(
            YellowEntry: yellow,
            RedEntry: red,
            RedRelease: release,
            DirectAConfirmation: Math.Min(options.MaximumRedThreshold, red + options.DirectAExtra),
            DrivePairActiveMedian: yellow);
        thresholds.Validate();
        var profile = new EcdCwrAdaptiveContactProfile(
            EcdCwrAdaptiveContactProfileSchema.Version,
            $"contact-{fingerprint.FingerprintId}-{createdAt:yyyyMMddHHmmss}",
            createdAt,
            fingerprint,
            retained.Length,
            statistics,
            thresholds,
            sourceLabel);
        profile.Validate();
        return profile;
    }

    private static EcdCwrHealthyScoreStatistic CreateStatistic(
        IReadOnlyList<EcdCwrHealthyContactScoreObservation> observations,
        int electrode)
    {
        var electrodeScores = observations.Select(observation => observation.ElectrodeSpecificScores[electrode]).ToArray();
        var drivePairScores = observations.Select(observation => observation.DrivePairScores[electrode]).ToArray();
        return new EcdCwrHealthyScoreStatistic(
            electrode,
            Median(electrodeScores),
            MedianAbsoluteDeviation(electrodeScores),
            Percentile(electrodeScores, 0.99),
            Percentile(electrodeScores, 0.999),
            Median(drivePairScores),
            MedianAbsoluteDeviation(drivePairScores),
            Percentile(drivePairScores, 0.99),
            Percentile(drivePairScores, 0.999));
    }

    private static void ValidateObservation(EcdCwrHealthyContactScoreObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.ElectrodeSpecificScores.Length != ElectrodeContactBaseline.ElectrodeCount ||
            observation.DrivePairScores.Length != ElectrodeContactBaseline.ElectrodeCount ||
            observation.ElectrodeSpecificScores.Any(score => !double.IsFinite(score) || score < 0.0) ||
            observation.DrivePairScores.Any(score => !double.IsFinite(score) || score < 0.0))
        {
            throw new ArgumentException("Healthy contact score observations require two finite nonnegative 16-value vectors.");
        }
    }

    private static void ValidateOptions(EcdCwrAdaptiveContactProfileBuilderOptions options)
    {
        if (options.MinimumHealthyFrameCount < 100 ||
            options.YellowQuantile is <= 0.0 or >= 1.0 ||
            options.RedQuantile <= options.YellowQuantile || options.RedQuantile > 1.0 ||
            options.YellowMargin < 0.0 || options.RedMargin < 0.0 ||
            options.MinimumYellowThreshold <= 0.0 ||
            options.MinimumRedSeparation <= 0.0 ||
            options.ReleaseHysteresis <= 0.0 ||
            options.DirectAExtra < 0.0 ||
            options.MaximumYellowThreshold <= options.MinimumYellowThreshold ||
            options.MaximumRedThreshold <= options.MaximumYellowThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static double MedianAbsoluteDeviation(IReadOnlyList<double> values)
    {
        var median = Median(values);
        return Median(values.Select(value => Math.Abs(value - median)).ToArray());
    }

    private static double Median(IReadOnlyList<double> values)
    {
        return Percentile(values, 0.5);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var position = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return ordered[lower];
        }

        return ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }
}

public enum EcdCwrAdaptiveContactProfileMatchMode
{
    Exact,
    Uncalibrated,
    Mismatch
}

public sealed record EcdCwrAdaptiveContactProfileMatch(
    EcdCwrAdaptiveContactProfileMatchMode Mode,
    EcdCwrAdaptiveContactProfile? Profile,
    string Reason)
{
    public bool Calibrated => Mode == EcdCwrAdaptiveContactProfileMatchMode.Exact && Profile is not null;
}

public sealed class EcdCwrAdaptiveContactProfileMatcher
{
    public EcdCwrAdaptiveContactProfileMatch Select(
        EcdCwrOperatingFingerprint fingerprint,
        IEnumerable<EcdCwrAdaptiveContactProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(profiles);
        fingerprint.Validate();
        if (!fingerprint.HasVerifiableFirmwareBuild)
        {
            return new EcdCwrAdaptiveContactProfileMatch(
                EcdCwrAdaptiveContactProfileMatchMode.Uncalibrated,
                null,
                "firmware build id is unreported; adaptive profile disabled");
        }

        var candidates = profiles.ToArray();
        foreach (var profile in candidates)
        {
            profile.Validate();
        }

        var exact = candidates
            .Where(profile => profile.Fingerprint.IsExactMatch(fingerprint))
            .OrderByDescending(profile => profile.CreatedAt)
            .FirstOrDefault();
        if (exact is not null)
        {
            return new EcdCwrAdaptiveContactProfileMatch(
                EcdCwrAdaptiveContactProfileMatchMode.Exact,
                exact,
                $"exact profile={exact.ProfileId}");
        }

        var sameDevice = candidates
            .Where(profile => string.Equals(
                profile.Fingerprint.DeviceLabel.Trim(),
                fingerprint.DeviceLabel.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(profile => profile.CreatedAt)
            .FirstOrDefault();
        if (sameDevice is not null)
        {
            return new EcdCwrAdaptiveContactProfileMatch(
                EcdCwrAdaptiveContactProfileMatchMode.Mismatch,
                null,
                $"profile mismatch: {string.Join(',', sameDevice.Fingerprint.DescribeDifferences(fingerprint))}");
        }

        return new EcdCwrAdaptiveContactProfileMatch(
            EcdCwrAdaptiveContactProfileMatchMode.Uncalibrated,
            null,
            "no adaptive contact profile for device");
    }
}

public sealed class EcdCwrAdaptiveContactProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public void Save(string path, EcdCwrAdaptiveContactProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public EcdCwrAdaptiveContactProfile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var profile = JsonSerializer.Deserialize<EcdCwrAdaptiveContactProfile>(
            File.ReadAllText(path),
            JsonOptions) ?? throw new InvalidOperationException("Adaptive contact profile file is empty or invalid.");
        profile.Validate();
        return profile;
    }
}

public sealed class EcdCwrPreReferenceContactScoreExtractor
{
    private const int ElectrodeCount = ElectrodeContactBaseline.ElectrodeCount;

    public EcdCwrHealthyContactScoreObservation Extract(
        double[,] fullAmplitudes256,
        bool highQuality,
        bool knownAllConnected)
    {
        ElectrodeContactBaseline.ValidateFullMatrix(fullAmplitudes256, nameof(fullAmplitudes256));
        var driveScores = RobustAbsoluteScores(ReadColumn(fullAmplitudes256, 0));
        var rightScores = RobustAbsoluteScores(ReadColumn(fullAmplitudes256, 1));
        var leftScores = RobustAbsoluteScores(ReadColumn(fullAmplitudes256, ElectrodeCount - 1));
        var electrodeScores = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            electrodeScores[electrode] = Math.Max(leftScores[electrode], rightScores[Mod(electrode - 1)]);
        }

        return new EcdCwrHealthyContactScoreObservation(
            electrodeScores,
            driveScores,
            highQuality,
            knownAllConnected);
    }

    private static double[] ReadColumn(double[,] values, int column)
    {
        var result = new double[ElectrodeCount];
        for (var row = 0; row < ElectrodeCount; row++)
        {
            result[row] = values[row, column];
        }

        return result;
    }

    private static double[] RobustAbsoluteScores(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).Order().ToArray();
        if (finite.Length == 0)
        {
            return new double[values.Count];
        }

        var median = MedianSorted(finite);
        var deviations = finite.Select(value => Math.Abs(value - median)).Order().ToArray();
        var mad = MedianSorted(deviations);
        var scale = Math.Max(1.0e-12, Math.Max(1.4826 * mad, Math.Abs(median) * 0.1));
        return values
            .Select(value => double.IsFinite(value) ? Math.Abs(value - median) / scale : 0.0)
            .ToArray();
    }

    private static double MedianSorted(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}
