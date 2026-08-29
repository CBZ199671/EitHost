namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSystemSentinel
{
    public EcdCwrSystemSentinelResult Evaluate(
        EcdCwrEvidenceAResult evidenceA,
        double frameRsd,
        double satRatio,
        double medianReciprocalScore = 0.0,
        EcdCwrSystemSentinelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceA);
        options ??= new EcdCwrSystemSentinelOptions();

        var medianZ48 = Median(evidenceA.PointScores
            .Where(point => !point.Saturated && double.IsFinite(point.Score))
            .Select(point => Math.Abs(point.Score))
            .ToArray());
        var safeFrameRsd = double.IsFinite(frameRsd) ? Math.Max(0.0, frameRsd) : 0.0;
        var safeSatRatio = double.IsFinite(satRatio) ? Math.Clamp(satRatio, 0.0, 1.0) : 0.0;
        var safeReciprocal = double.IsFinite(medianReciprocalScore) ? Math.Max(0.0, medianReciprocalScore) : 0.0;
        var score =
            (options.MedianZ48Weight * medianZ48) +
            (options.MedianReciprocalWeight * safeReciprocal) +
            (options.FrameRsdWeight * safeFrameRsd) +
            (options.SaturationRatioWeight * safeSatRatio);
        var triggered = score >= options.ScoreThreshold ||
            medianZ48 >= options.MedianZ48HardThreshold ||
            safeSatRatio >= options.SaturationRatioHardThreshold;

        return new EcdCwrSystemSentinelResult(
            triggered,
            score,
            medianZ48,
            safeReciprocal,
            safeFrameRsd,
            safeSatRatio,
            triggered
                ? "system-level sentinel triggered; skip electrode-level decision for this frame"
                : "system-level sentinel clear");
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }
}

public sealed record EcdCwrSystemSentinelOptions(
    double MedianZ48Weight = 1.0,
    double MedianReciprocalWeight = 1.0,
    double FrameRsdWeight = 1.0,
    double SaturationRatioWeight = 10.0,
    double ScoreThreshold = 8.0,
    double MedianZ48HardThreshold = 8.0,
    double SaturationRatioHardThreshold = 0.10);

public sealed record EcdCwrSystemSentinelResult(
    bool Triggered,
    double Score,
    double MedianZ48,
    double MedianReciprocalScore,
    double FrameRsd,
    double SaturationRatio,
    string Reason);
