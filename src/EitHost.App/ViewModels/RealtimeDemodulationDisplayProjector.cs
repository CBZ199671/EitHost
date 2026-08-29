namespace EitHost.App.ViewModels;

public sealed record RealtimeDemodulationAxisTick(
    double Value,
    string Label,
    double Top,
    double LabelTop,
    bool IsZero);

internal sealed record RealtimeDemodulationAxisScale(
    double Minimum,
    double Maximum,
    IReadOnlyList<RealtimeDemodulationAxisTick> Ticks);

internal static class RealtimeDemodulationAxisFormatter
{
    private const int TargetTickCount = 6;
    private const double TickLabelHalfHeight = 7.0;

    public static RealtimeDemodulationAxisScale CreateVoltageScale(
        double min,
        double max,
        double top,
        double bottom)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || bottom <= top)
        {
            return new RealtimeDemodulationAxisScale(0.0, 1.0, []);
        }

        var dataMinimum = Math.Min(0.0, Math.Min(min, max));
        var dataMaximum = Math.Max(0.0, Math.Max(min, max));
        if (Math.Abs(dataMaximum - dataMinimum) < 1.0e-15)
        {
            var padding = Math.Max(1.0e-12, Math.Abs(dataMinimum) * 0.1);
            dataMinimum -= padding;
            dataMaximum += padding;
        }

        var step = SelectNiceStep(dataMinimum, dataMaximum);
        var axisMinimum = Math.Floor(dataMinimum / step) * step;
        var axisMaximum = Math.Ceiling(dataMaximum / step) * step;
        axisMinimum = NormalizeZero(axisMinimum);
        axisMaximum = NormalizeZero(axisMaximum);
        if (Math.Abs(axisMaximum - axisMinimum) < 1.0e-15)
        {
            axisMaximum = axisMinimum + step;
        }

        var maximumAbsoluteVoltage = Math.Max(Math.Abs(axisMinimum), Math.Abs(axisMaximum));
        var (scale, unit) = maximumAbsoluteVoltage switch
        {
            >= 1.0 => (1.0, "V"),
            >= 1.0e-3 => (1.0e3, "mV"),
            >= 1.0e-6 => (1.0e6, "µV"),
            >= 1.0e-9 => (1.0e9, "nV"),
            _ => (1.0, "V")
        };

        var intervalCount = checked((int)Math.Round((axisMaximum - axisMinimum) / step));
        var ticks = new List<RealtimeDemodulationAxisTick>(intervalCount + 1);
        for (var index = intervalCount; index >= 0; index--)
        {
            var value = NormalizeZero(axisMinimum + (index * step));
            var y = MapValueToY(value, axisMinimum, axisMaximum, top, bottom);
            ticks.Add(new RealtimeDemodulationAxisTick(
                value,
                value == 0.0 ? $"0 {unit}" : FormatScaled(value * scale, unit),
                y,
                Math.Max(0.0, y - TickLabelHalfHeight),
                value == 0.0));
        }

        return new RealtimeDemodulationAxisScale(axisMinimum, axisMaximum, ticks);
    }

    public static RealtimeDemodulationAxisScale CreatePhaseScale(double top, double bottom)
    {
        double[] values = [180.0, 90.0, 0.0, -90.0, -180.0];
        var ticks = values
            .Select(value =>
            {
                var y = MapValueToY(value, -180.0, 180.0, top, bottom);
                var label = value switch
                {
                    > 0.0 => $"+{value:0}°",
                    < 0.0 => $"−{Math.Abs(value):0}°",
                    _ => "0°"
                };
                return new RealtimeDemodulationAxisTick(
                    value,
                    label,
                    y,
                    Math.Max(0.0, y - TickLabelHalfHeight),
                    value == 0.0);
            })
            .ToArray();
        return new RealtimeDemodulationAxisScale(-180.0, 180.0, ticks);
    }

    private static double SelectNiceStep(double minimum, double maximum)
    {
        var rawStep = (maximum - minimum) / (TargetTickCount - 1);
        var exponent = Math.Floor(Math.Log10(rawStep));
        var candidates = new List<double>();
        double[] multipliers = [1.0, 2.0, 2.5, 5.0, 10.0];
        for (var exponentOffset = -1; exponentOffset <= 1; exponentOffset++)
        {
            var power = Math.Pow(10.0, exponent + exponentOffset);
            candidates.AddRange(multipliers.Select(multiplier => multiplier * power));
        }

        var evaluated = candidates
            .Where(candidate => double.IsFinite(candidate) && candidate > 0.0)
            .Distinct()
            .Select(candidate =>
            {
                var axisMinimum = Math.Floor(minimum / candidate) * candidate;
                var axisMaximum = Math.Ceiling(maximum / candidate) * candidate;
                var count = checked((int)Math.Round((axisMaximum - axisMinimum) / candidate)) + 1;
                var expansion = ((axisMaximum - axisMinimum) - (maximum - minimum)) / (maximum - minimum);
                return new { Step = candidate, Count = count, Expansion = expansion };
            })
            .ToArray();
        var preferred = evaluated
            .Where(candidate => candidate.Count is >= 5 and <= 7)
            .OrderBy(candidate => Math.Abs(candidate.Count - TargetTickCount))
            .ThenBy(candidate => candidate.Expansion)
            .FirstOrDefault();
        return (preferred ?? evaluated.OrderBy(candidate => Math.Abs(candidate.Count - TargetTickCount)).First()).Step;
    }

    private static double MapValueToY(double value, double minimum, double maximum, double top, double bottom)
    {
        return top + (((maximum - value) / (maximum - minimum)) * (bottom - top));
    }

    private static double NormalizeZero(double value)
    {
        return Math.Abs(value) < 1.0e-14 ? 0.0 : value;
    }

    private static string FormatScaled(double value, string unit)
    {
        var absoluteValue = Math.Abs(value);
        var format = absoluteValue switch
        {
            >= 100.0 => "F0",
            >= 10.0 => "F1",
            >= 1.0 => "F2",
            _ => "F3"
        };
        return $"{value.ToString(format, System.Globalization.CultureInfo.InvariantCulture)} {unit}";
    }
}

internal sealed record RealtimePolarDemodulationSeries(
    double[] Magnitude,
    double[] PhaseDegrees,
    double MagnitudeFloor,
    int MaskedPhaseCount);

internal static class RealtimeDemodulationDisplayProjector
{
    private const double RelativeMagnitudeFloor = 0.02;

    public static RealtimePolarDemodulationSeries ToPolar(
        IReadOnlyList<double> real,
        IReadOnlyList<double> imaginary)
    {
        ArgumentNullException.ThrowIfNull(real);
        ArgumentNullException.ThrowIfNull(imaginary);
        if (real.Count != imaginary.Count)
        {
            throw new ArgumentException("Real and imaginary vectors must have the same length.", nameof(imaginary));
        }

        var magnitude = new double[real.Count];
        var phaseDegrees = new double[real.Count];
        for (var index = 0; index < real.Count; index++)
        {
            if (!double.IsFinite(real[index]) || !double.IsFinite(imaginary[index]))
            {
                magnitude[index] = double.NaN;
                phaseDegrees[index] = double.NaN;
                continue;
            }

            magnitude[index] = Math.Sqrt((real[index] * real[index]) + (imaginary[index] * imaginary[index]));
        }

        var finiteMagnitude = magnitude
            .Where(value => double.IsFinite(value) && value > 0.0)
            .OrderBy(value => value)
            .ToArray();
        var medianMagnitude = finiteMagnitude.Length == 0
            ? 0.0
            : finiteMagnitude.Length % 2 == 0
                ? 0.5 * (finiteMagnitude[(finiteMagnitude.Length / 2) - 1] + finiteMagnitude[finiteMagnitude.Length / 2])
                : finiteMagnitude[finiteMagnitude.Length / 2];
        var magnitudeFloor = Math.Max(1.0e-15, medianMagnitude * RelativeMagnitudeFloor);
        var maskedPhaseCount = 0;
        for (var index = 0; index < phaseDegrees.Length; index++)
        {
            if (!double.IsFinite(magnitude[index]) || magnitude[index] < magnitudeFloor)
            {
                phaseDegrees[index] = double.NaN;
                maskedPhaseCount++;
                continue;
            }

            var phase = 180.0 * Math.Atan2(imaginary[index], real[index]) / Math.PI;
            phaseDegrees[index] = phase >= 180.0 ? phase - 360.0 : phase;
        }

        return new RealtimePolarDemodulationSeries(
            magnitude,
            phaseDegrees,
            magnitudeFloor,
            maskedPhaseCount);
    }
}
