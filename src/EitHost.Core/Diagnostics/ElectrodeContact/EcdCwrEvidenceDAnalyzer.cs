using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrEvidenceDAnalyzer
{
    private const int ElectrodeCount = 16;

    public EcdCwrEvidenceDResult Analyze(
        IReadOnlyList<DemodulatedWindowQuality> qualities,
        EcdCwrEvidenceDOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(qualities);
        options ??= new EcdCwrEvidenceDOptions();

        var scores = qualities
            .Select(quality => ScoreWindow(quality, options))
            .ToArray();
        return new EcdCwrEvidenceDResult(
            scores,
            scores.Any(score => score.HardFault),
            scores.Length == 0 ? 0.0 : scores.Max(score => score.Score));
    }

    private static EcdCwrEvidenceDWindowScore ScoreWindow(
        DemodulatedWindowQuality quality,
        EcdCwrEvidenceDOptions options)
    {
        var top3Distance = CalculateTop3SetDistance(quality);
        var argmaxDistance = RingDistance(quality.DetectedTop1Channel, quality.ExpectedReferenceChannel);
        var peakToBackgroundPenalty = CalculatePeakToBackgroundPenalty(quality, options);
        var weakReferencePenalty = quality.RejectReason == DemodulatedWindowRejectReason.WeakReference
            ? 1.0
            : 0.0;
        var hardFault = quality.RejectReason == DemodulatedWindowRejectReason.AdcSaturation ||
            quality.AdcSaturationCount > 0;
        var score = hardFault
            ? options.HardFaultScore
            : (options.Top3SetWeight * top3Distance) +
                (options.ArgmaxDistanceWeight * argmaxDistance) +
                (options.PeakToBackgroundWeight * peakToBackgroundPenalty) +
                (options.WeakReferenceWeight * weakReferencePenalty);

        return new EcdCwrEvidenceDWindowScore(
            quality.WindowIndex,
            quality.ExpectedReferenceChannel,
            score,
            top3Distance,
            argmaxDistance,
            peakToBackgroundPenalty,
            weakReferencePenalty,
            hardFault,
            quality.RejectReason);
    }

    private static double CalculateTop3SetDistance(DemodulatedWindowQuality quality)
    {
        var expected = new[]
        {
            Mod(quality.ExpectedReferenceChannel - 1),
            quality.ExpectedReferenceChannel,
            Mod(quality.ExpectedReferenceChannel + 1)
        };
        if (quality.Top3Channels.Length == 0)
        {
            return 3.0;
        }

        return quality.Top3Channels
            .Take(3)
            .Sum(actual => expected.Min(candidate => RingDistance(actual, candidate)));
    }

    private static double CalculatePeakToBackgroundPenalty(
        DemodulatedWindowQuality quality,
        EcdCwrEvidenceDOptions options)
    {
        if (!double.IsFinite(quality.PeakToBackgroundRatio))
        {
            return 0.0;
        }

        if (quality.PeakToBackgroundRatio >= options.PeakToBackgroundHealthyFloor)
        {
            return 0.0;
        }

        return (options.PeakToBackgroundHealthyFloor - quality.PeakToBackgroundRatio) /
            Math.Max(options.PeakToBackgroundScale, double.Epsilon);
    }

    private static int RingDistance(int left, int right)
    {
        if (left < 0 || right < 0)
        {
            return ElectrodeCount / 2;
        }

        var raw = Math.Abs(Mod(left) - Mod(right));
        return Math.Min(raw, ElectrodeCount - raw);
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrEvidenceDOptions(
    double Top3SetWeight = 1.0,
    double ArgmaxDistanceWeight = 1.0,
    double PeakToBackgroundWeight = 1.0,
    double WeakReferenceWeight = 1.0,
    double PeakToBackgroundHealthyFloor = 2.0,
    double PeakToBackgroundScale = 1.0,
    double HardFaultScore = 100.0);

public sealed record EcdCwrEvidenceDWindowScore(
    int WindowIndex,
    int ExpectedReferenceChannel,
    double Score,
    double Top3SetDistance,
    double ArgmaxDistance,
    double PeakToBackgroundPenalty,
    double WeakReferencePenalty,
    bool HardFault,
    DemodulatedWindowRejectReason RejectReason);

public sealed record EcdCwrEvidenceDResult(
    IReadOnlyList<EcdCwrEvidenceDWindowScore> WindowScores,
    bool HasHardFault,
    double MaxScore);
