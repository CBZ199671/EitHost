namespace EitHost.Core.Reconstruction;

public sealed record RealtimeImageColorScaleOptions(
    double RobustQuantile = 0.995,
    double MaximumExpansionFactor = 1.5,
    double ContractionRate = 0.02,
    double CenterTrackingRate = 0.02,
    double RelativeRangeFloor = 1.0e-6,
    double AbsoluteRangeFloor = 1.0e-9);

public sealed record RealtimeImageColorScaleSnapshot(
    double Center,
    double Range,
    double CandidateRange,
    bool Initialized);

public sealed class RealtimeImageColorScaleTracker
{
    private readonly RealtimeImageColorScaleOptions options;
    private bool initialized;
    private double center;
    private double range;

    public RealtimeImageColorScaleTracker(RealtimeImageColorScaleOptions? options = null)
    {
        this.options = options ?? new RealtimeImageColorScaleOptions();
        ValidateOptions(this.options);
    }

    public RealtimeImageColorScaleSnapshot Update(IReadOnlyList<double> conductivity)
    {
        ArgumentNullException.ThrowIfNull(conductivity);
        if (conductivity.Count == 0 || conductivity.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Color scale conductivity must contain finite values.", nameof(conductivity));
        }

        var candidateCenter = Quantile(conductivity, 0.5);
        var deviations = conductivity.Select(value => Math.Abs(value - candidateCenter)).ToArray();
        var floor = Math.Max(
            options.AbsoluteRangeFloor,
            Math.Abs(candidateCenter) * options.RelativeRangeFloor);
        var candidateRange = Math.Max(floor, Quantile(deviations, options.RobustQuantile));
        if (!initialized)
        {
            initialized = true;
            center = candidateCenter;
            range = candidateRange;
        }
        else
        {
            center += options.CenterTrackingRate * (candidateCenter - center);
            range = candidateRange > range
                ? Math.Min(candidateRange, range * options.MaximumExpansionFactor)
                : range + (options.ContractionRate * (candidateRange - range));
            range = Math.Max(floor, range);
        }

        return new RealtimeImageColorScaleSnapshot(center, range, candidateRange, initialized);
    }

    public void Reset()
    {
        initialized = false;
        center = 0.0;
        range = 0.0;
    }

    private static double Quantile(IReadOnlyList<double> values, double probability)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = probability * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static void ValidateOptions(RealtimeImageColorScaleOptions options)
    {
        if (!double.IsFinite(options.RobustQuantile) || options.RobustQuantile is <= 0.5 or > 1.0 ||
            !double.IsFinite(options.MaximumExpansionFactor) || options.MaximumExpansionFactor < 1.0 ||
            !double.IsFinite(options.ContractionRate) || options.ContractionRate is <= 0.0 or > 1.0 ||
            !double.IsFinite(options.CenterTrackingRate) || options.CenterTrackingRate is <= 0.0 or > 1.0 ||
            !double.IsFinite(options.RelativeRangeFloor) || options.RelativeRangeFloor <= 0.0 ||
            !double.IsFinite(options.AbsoluteRangeFloor) || options.AbsoluteRangeFloor <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }
}
