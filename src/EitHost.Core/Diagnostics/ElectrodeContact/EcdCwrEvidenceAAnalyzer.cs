namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrEvidenceAAnalyzer
{
    private const int ElectrodeCount = 16;

    public EcdCwrEvidenceAResult Analyze(
        double[,] fullReal,
        double[,] fullImaginary,
        EcdCwrHealthCalibration calibration,
        int[,]? sat256 = null,
        EcdCwrEvidenceAOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fullReal);
        ArgumentNullException.ThrowIfNull(fullImaginary);
        ArgumentNullException.ThrowIfNull(calibration);
        options ??= new EcdCwrEvidenceAOptions();

        ValidateFullMatrix(fullReal, nameof(fullReal));
        ValidateFullMatrix(fullImaginary, nameof(fullImaginary));
        if (sat256 is not null)
        {
            ValidateFullMatrix(sat256, nameof(sat256));
        }

        var stats = calibration.Contact48.ToDictionary(
            stat => (stat.StimulationIndex, stat.RelativeChannelIndex),
            stat => stat);
        var drive = new double[ElectrodeCount];
        var left = new double[ElectrodeCount];
        var right = new double[ElectrodeCount];
        var pointScores = new List<EcdCwrEvidenceAPoint>(48);
        var saturatedPoints = new List<EcdCwrEvidenceAPoint>(48);

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            foreach (var relativeChannel in new[] { 0, 1, 15 })
            {
                if (!stats.TryGetValue((stimulation, relativeChannel), out var stat))
                {
                    throw new InvalidOperationException(
                        $"Calibration is missing contact48 statistic for s={stimulation}, k={relativeChannel}.");
                }

                var saturated = sat256 is not null && sat256[stimulation, relativeChannel] > 0;
                var score = saturated
                    ? 0.0
                    : CalculateWhitenedResidual(
                        fullReal[stimulation, relativeChannel],
                        fullImaginary[stimulation, relativeChannel],
                        stat,
                        options);
                var point = new EcdCwrEvidenceAPoint(stimulation, relativeChannel, score, saturated);
                pointScores.Add(point);
                if (saturated)
                {
                    saturatedPoints.Add(point);
                }

                if (relativeChannel == 0)
                {
                    drive[stimulation] = score;
                }
                else if (relativeChannel == 1)
                {
                    right[stimulation] = score;
                }
                else
                {
                    left[stimulation] = score;
                }
            }
        }

        var candidates = BuildCandidates(drive, left, right, options);
        return new EcdCwrEvidenceAResult(
            drive,
            left,
            right,
            pointScores,
            saturatedPoints,
            candidates,
            candidates.Count > 0);
    }

    private static IReadOnlyList<EcdCwrEvidenceACandidate> BuildCandidates(
        IReadOnlyList<double> drive,
        IReadOnlyList<double> left,
        IReadOnlyList<double> right,
        EcdCwrEvidenceAOptions options)
    {
        var candidates = new List<EcdCwrEvidenceACandidate>();
        var driveRowsClaimed = new bool[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var previousDrive = Mod(electrode - 1);
            var driveScore = Math.Min(drive[previousDrive], drive[electrode]);
            var sharedScore = Math.Min(left[electrode], right[previousDrive]);
            var score = Math.Max(driveScore, sharedScore);
            if (score < options.CandidateZThreshold)
            {
                continue;
            }

            candidates.Add(new EcdCwrEvidenceACandidate(
                EcdCwrEvidenceACandidateType.ElectrodeContact,
                electrode,
                null,
                score,
                score >= options.SevereZThreshold,
                $"electrode {electrode} shared evidence drive={driveScore:G3} shared={sharedScore:G3}"));
            if (driveScore >= options.CandidateZThreshold)
            {
                driveRowsClaimed[previousDrive] = true;
                driveRowsClaimed[electrode] = true;
            }
        }

        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            if (driveRowsClaimed[stimulation] || drive[stimulation] < options.CandidateZThreshold)
            {
                continue;
            }

            candidates.Add(new EcdCwrEvidenceACandidate(
                EcdCwrEvidenceACandidateType.DrivePairLink,
                null,
                stimulation,
                drive[stimulation],
                drive[stimulation] >= options.SevereZThreshold,
                $"drive pair {stimulation} isolated evidence drive={drive[stimulation]:G3}"));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
    }

    private static double CalculateWhitenedResidual(
        double real,
        double imaginary,
        EcdCwrComplexStatistic stat,
        EcdCwrEvidenceAOptions options)
    {
        var residual = Math.Sqrt(
            Math.Pow(real - stat.MeanReal, 2.0) +
            Math.Pow(imaginary - stat.MeanImaginary, 2.0));
        var scale = Math.Max(
            options.ResidualNoiseFloor,
            Math.Max(stat.MagnitudeSigma, 1.4826 * stat.MagnitudeMad));
        return residual / scale;
    }

    private static void ValidateFullMatrix(double[,] matrix, string name)
    {
        if (matrix.GetLength(0) != ElectrodeCount || matrix.GetLength(1) != ElectrodeCount)
        {
            throw new ArgumentException("Evidence A expects a [16,16] full-observation matrix.", name);
        }
    }

    private static void ValidateFullMatrix(int[,] matrix, string name)
    {
        if (matrix.GetLength(0) != ElectrodeCount || matrix.GetLength(1) != ElectrodeCount)
        {
            throw new ArgumentException("Evidence A expects a [16,16] full-observation matrix.", name);
        }
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrEvidenceAOptions(
    double CandidateZThreshold = 3.0,
    double SevereZThreshold = 15.0,
    double ResidualNoiseFloor = 1e-9);

public enum EcdCwrEvidenceACandidateType
{
    ElectrodeContact = 0,
    DrivePairLink = 1
}

public sealed record EcdCwrEvidenceAPoint(
    int StimulationIndex,
    int RelativeChannelIndex,
    double Score,
    bool Saturated);

public sealed record EcdCwrEvidenceACandidate(
    EcdCwrEvidenceACandidateType Type,
    int? ElectrodeIndex,
    int? DrivePairIndex,
    double Score,
    bool Severe,
    string Reason);

public sealed record EcdCwrEvidenceAResult(
    IReadOnlyList<double> DriveScores,
    IReadOnlyList<double> LeftSharedScores,
    IReadOnlyList<double> RightSharedScores,
    IReadOnlyList<EcdCwrEvidenceAPoint> PointScores,
    IReadOnlyList<EcdCwrEvidenceAPoint> SaturatedPoints,
    IReadOnlyList<EcdCwrEvidenceACandidate> Candidates,
    bool HasCandidate);
