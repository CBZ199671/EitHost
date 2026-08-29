using System.Windows;
using System.Windows.Media;
using EitHost.Core.Analysis;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics;
using EitHost.Core.Diagnostics.ElectrodeContact;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels;

internal static class RealtimeVisualizationProjection
{
    private const string SignalViewModeReference = "reference";
    private const string SignalViewModeTarget = "target";
    private const string DemodDisplayModePolar = "polar";
    private const double CanvasWidth = VisualizationGeometry.DefaultPlotCanvasWidth;
    private const double CanvasHeight = 220.0;
    private const double CanvasPadding = 14.0;

    internal static RealtimeDemodPreviewSnapshot CreateRealtimeSignalPreviewSnapshot(
        RealtimeSignalPreviewSource source,
        string viewMode,
        string demodDisplayMode)
    {
        return VisualizationRenderer.NormalizeRealtimeSignalViewMode(viewMode) switch
        {
            SignalViewModeReference => CreateRealtimeReferencePreviewSnapshot(source),
            SignalViewModeTarget => CreateRealtimeTargetPreviewSnapshot(source),
            _ when VisualizationRenderer.NormalizeRealtimeDemodDisplayMode(demodDisplayMode) == DemodDisplayModePolar =>
                CreateRealtimePolarPreviewSnapshot(source),
            _ => CreateRealtimeRectangularPreviewSnapshot(source)
        };
    }

    internal static RealtimeBoundaryFitPreviewSnapshot CreateRealtimeBoundaryFitPreviewSnapshot(
        RealtimeReconstructionResult result,
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        string differenceOrientation,
        EcdCwrWaveformTemplateDisplayPackage? templateDisplayPackage = null)
    {
        var measured = result.MeasuredVoltageFit208 is { Length: > 0 } backendMeasured
            ? backendMeasured
            : CreateBoundaryDifferenceVector(reference, target, differenceOrientation);
        var simulated = result.SimulatedVoltageFit208 is { Length: > 0 } backendSimulated &&
                        backendSimulated.Length == measured.Length
            ? backendSimulated
            : null;
        var templateExpected = CreateTemplateExpectedDeltaVector208(
            templateDisplayPackage,
            reference,
            differenceOrientation);

        var rangeInputs = simulated is null
            ? new[] { measured }
            : new[] { measured, simulated };
        if (templateExpected is { Length: > 0 })
        {
            rangeInputs = [.. rangeInputs, templateExpected];
        }

        var range = FindFiniteRange(rangeInputs);
        var axis = FormatVoltageAxisLabels(range.Min, range.Max);
        var stats = simulated is null
            ? $"raw meas ΔV {measured.Length} · 等待正演拟合"
            : $"raw meas/fit ΔV {measured.Length} · mean |err| {MeanAbsoluteDifference(measured, simulated):G3}";
        if (templateExpected is not null)
        {
            stats += " · 模板display-only";
        }

        return new RealtimeBoundaryFitPreviewSnapshot(
            CreateSeriesGeometry(measured, range.Min, range.Max),
            simulated is null ? null : CreateSeriesGeometry(simulated, range.Min, range.Max),
            templateExpected is null ? null : CreateSeriesGeometry(templateExpected, range.Min, range.Max),
            stats,
            axis.Top,
            axis.Middle,
            axis.Bottom);
    }

    private static RealtimeDemodPreviewSnapshot CreateRealtimeRectangularPreviewSnapshot(RealtimeSignalPreviewSource source)
    {
        var range = FindFiniteRange(source.RealVoltage208, source.ImaginaryVoltage208);
        var axis = RealtimeDemodulationAxisFormatter.CreateVoltageScale(
            range.Min,
            range.Max,
            CanvasPadding,
            CanvasHeight - CanvasPadding);
        var rangeSummary = $"Re/Im {range.Min:G4}~{range.Max:G4} V";
        var stats = source.DiagnosticMode
            ? $"诊断解调（低置信） · {rangeSummary} · block {source.BlockNumber} · strict acc {source.AcceptedFrameCount}/{source.FramesPerBlock} · 健康 {source.TrustedMeasurementCount}/208 · 诊断 {source.DiagnosticMeasurementCount}/208 · {source.RejectSummary}"
            : $"复数解调 · {rangeSummary} · block {source.BlockNumber} · acc {source.AcceptedFrameCount}/{source.FramesPerBlock} · q {source.QualityWeight:P0}" +
              FormatDemodulationStability(source.StepStability);
        return new RealtimeDemodPreviewSnapshot(
            CreateSeriesGeometry(source.RealVoltage208, axis.Minimum, axis.Maximum),
            CreateSeriesGeometry(source.ImaginaryVoltage208, axis.Minimum, axis.Maximum),
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: false),
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: true),
            stats,
            axis.Ticks);
    }

    private static RealtimeDemodPreviewSnapshot CreateRealtimePolarPreviewSnapshot(RealtimeSignalPreviewSource source)
    {
        var polar = RealtimeDemodulationDisplayProjector.ToPolar(source.RealVoltage208, source.ImaginaryVoltage208);
        var magnitudeRange = FindFiniteRange(polar.Magnitude);
        var magnitudeAxis = RealtimeDemodulationAxisFormatter.CreateVoltageScale(
            magnitudeRange.Min,
            magnitudeRange.Max,
            CanvasPadding,
            (CanvasHeight / 2.0) - CanvasPadding);
        var phaseAxis = RealtimeDemodulationAxisFormatter.CreatePhaseScale(
            (CanvasHeight / 2.0) + CanvasPadding,
            CanvasHeight - CanvasPadding);
        IReadOnlyList<RealtimeDemodulationAxisTick> ticks = [.. magnitudeAxis.Ticks, .. phaseAxis.Ticks];
        var rangeSummary = $"|V| {magnitudeRange.Min:G4}~{magnitudeRange.Max:G4} V · φ -180~180°";
        var stats = source.DiagnosticMode
            ? $"诊断极坐标（低置信） · {rangeSummary} · block {source.BlockNumber} · strict acc {source.AcceptedFrameCount}/{source.FramesPerBlock} · 相位屏蔽 {polar.MaskedPhaseCount}/208"
            : $"专家极坐标 · {rangeSummary} · block {source.BlockNumber} · acc {source.AcceptedFrameCount}/{source.FramesPerBlock} · q {source.QualityWeight:P0} · 相位屏蔽 {polar.MaskedPhaseCount}/208" +
              FormatDemodulationStability(source.StepStability);
        return new RealtimeDemodPreviewSnapshot(
            CreateSeriesGeometryInBand(
                polar.Magnitude,
                magnitudeAxis.Minimum,
                magnitudeAxis.Maximum,
                CanvasPadding,
                (CanvasHeight / 2.0) - CanvasPadding),
            CreateSeriesGeometryInBand(
                polar.PhaseDegrees,
                -180.0,
                180.0,
                (CanvasHeight / 2.0) + CanvasPadding,
                CanvasHeight - CanvasPadding,
                discontinuityThreshold: 180.0),
            CreateHorizontalTickGeometry(ticks, zeroLines: false),
            CreateHorizontalTickGeometry(ticks, zeroLines: true),
            stats,
            ticks);
    }

    private static RealtimeDemodPreviewSnapshot CreateRealtimeReferencePreviewSnapshot(RealtimeSignalPreviewSource source)
    {
        if (source.ReferenceVoltage208 is not { Length: > 0 } reference)
        {
            return new RealtimeDemodPreviewSnapshot(null, null, null, null, "参考帧 · 未锁定", []);
        }

        var range = FindFiniteRange(reference);
        var axis = RealtimeDemodulationAxisFormatter.CreateVoltageScale(
            range.Min,
            range.Max,
            CanvasPadding,
            CanvasHeight - CanvasPadding);
        return new RealtimeDemodPreviewSnapshot(
            CreateSeriesGeometry(reference, axis.Minimum, axis.Maximum),
            null,
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: false),
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: true),
            source.ReferenceIsProvisional
                ? $"参考帧 {reference.Length} pts · 快速预览（低置信，正式稳定性后台验证中）"
                : $"参考帧 {reference.Length} pts · 正式锁定",
            axis.Ticks);
    }

    private static RealtimeDemodPreviewSnapshot CreateRealtimeTargetPreviewSnapshot(RealtimeSignalPreviewSource source)
    {
        var range = FindFiniteRange(source.AmplitudeVoltage208);
        var axis = RealtimeDemodulationAxisFormatter.CreateVoltageScale(
            range.Min,
            range.Max,
            CanvasPadding,
            CanvasHeight - CanvasPadding);
        return new RealtimeDemodPreviewSnapshot(
            CreateSeriesGeometry(source.AmplitudeVoltage208, axis.Minimum, axis.Maximum),
            null,
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: false),
            CreateHorizontalTickGeometry(axis.Ticks, zeroLines: true),
            $"目标帧 {source.AmplitudeVoltage208.Length} pts · block {source.BlockNumber}",
            axis.Ticks);
    }

    private static string FormatDemodulationStability(RealtimeDemodulationStepStability? stability)
    {
        return stability is null
            ? " · 相邻块稳定度：建立连续基线"
            : $" · 相邻块同相 I Δα {stability.RealCommonScaleDeltaPercent:+0.00000;-0.00000;0.00000}%" +
              $" · 相位 Δ {stability.ComplexPhaseDeltaDegrees:+0.00000;-0.00000;0.00000}°" +
              $" · 去尺度形状 {stability.RealShapeResidualPercent:0.00000}%（仅诊断）";
    }

    private static (string Top, string Middle, string Bottom) FormatVoltageAxisLabels(double min, double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        var middle = (min + max) / 2.0;
        return (FormatVoltageAxisLabel(max), FormatVoltageAxisLabel(middle), FormatVoltageAxisLabel(min));
    }

    internal static string FormatVoltageAxisLabel(double value)
    {
        if (!double.IsFinite(value))
        {
            return string.Empty;
        }

        var abs = Math.Abs(value);
        if (abs >= 10.0)
        {
            return $"{value:F1} V";
        }

        if (abs >= 1.0)
        {
            return $"{value:F2} V";
        }

        if (abs >= 0.01)
        {
            return $"{value:F3} V";
        }

        return $"{value:G3} V";
    }

    internal static Geometry? CreateSeriesGeometry(IReadOnlyList<double> values)
    {
        var range = FindFiniteRange(values);
        return CreateSeriesGeometry(values, range.Min, range.Max);
    }

    internal static Geometry? CreateSeriesGeometry(IReadOnlyList<double> values, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(values);
        var finiteValues = values.Where(double.IsFinite).ToArray();
        if (finiteValues.Length == 0)
        {
            return null;
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            (min, max) = FindFiniteRange(finiteValues);
        }

        if (Math.Abs(max - min) < double.Epsilon)
        {
            min -= 1.0;
            max += 1.0;
        }

        var points = new List<Point>(finiteValues.Length);
        var xScale = finiteValues.Length == 1
            ? 0.0
            : (CanvasWidth - (2.0 * CanvasPadding)) / (finiteValues.Length - 1);
        var yScale = (CanvasHeight - (2.0 * CanvasPadding)) / (max - min);
        for (var index = 0; index < finiteValues.Length; index++)
        {
            var x = CanvasPadding + (index * xScale);
            var y = CanvasHeight - CanvasPadding - ((finiteValues[index] - min) * yScale);
            points.Add(new Point(x, Math.Clamp(y, CanvasPadding, CanvasHeight - CanvasPadding)));
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            if (points.Count > 1)
            {
                context.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    internal static Geometry? CreateSeriesGeometryInBand(
        IReadOnlyList<double> values,
        double min,
        double max,
        double bandTop,
        double bandBottom,
        double? discontinuityThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || bandBottom <= bandTop)
        {
            return null;
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            (min, max) = FindFiniteRange(values);
        }

        if (Math.Abs(max - min) < double.Epsilon)
        {
            min -= 1.0;
            max += 1.0;
        }

        var xScale = values.Count == 1
            ? 0.0
            : (CanvasWidth - (2.0 * CanvasPadding)) / (values.Count - 1);
        var yScale = (bandBottom - bandTop) / (max - min);
        var geometry = new StreamGeometry();
        var hasPoint = false;
        var hasAnyPoint = false;
        var previousValue = double.NaN;
        using (var context = geometry.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var value = values[index];
                if (!double.IsFinite(value))
                {
                    hasPoint = false;
                    previousValue = double.NaN;
                    continue;
                }

                if (discontinuityThreshold is { } threshold &&
                    double.IsFinite(previousValue) &&
                    Math.Abs(value - previousValue) > threshold)
                {
                    hasPoint = false;
                }

                var x = CanvasPadding + (index * xScale);
                var y = bandBottom - ((value - min) * yScale);
                var point = new Point(x, Math.Clamp(y, bandTop, bandBottom));
                if (!hasPoint)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                    hasPoint = true;
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }

                hasAnyPoint = true;
                previousValue = value;
            }
        }

        if (!hasAnyPoint)
        {
            return null;
        }

        geometry.Freeze();
        return geometry;
    }

    internal static Geometry? CreateHorizontalTickGeometry(
        IReadOnlyList<RealtimeDemodulationAxisTick> ticks,
        bool zeroLines)
    {
        var selectedTicks = ticks.Where(tick => tick.IsZero == zeroLines).ToArray();
        if (selectedTicks.Length == 0)
        {
            return null;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var tick in selectedTicks)
            {
                context.BeginFigure(new Point(0.0, tick.Top), isFilled: false, isClosed: false);
                context.LineTo(new Point(CanvasWidth, tick.Top), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    internal static (double Min, double Max) FindFiniteRange(params IReadOnlyList<double>[] series)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var values in series)
        {
            foreach (var value in values)
            {
                if (!double.IsFinite(value))
                {
                    continue;
                }

                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return (0.0, 1.0);
        }

        if (Math.Abs(max - min) < 1.0e-12)
        {
            var pad = Math.Max(1.0, Math.Abs(min) * 0.05);
            return (min - pad, max + pad);
        }

        var margin = (max - min) * 0.08;
        return (min - margin, max + margin);
    }

    internal static double MeanAbsoluteDifference(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var count = Math.Min(left.Count, right.Count);
        if (count == 0)
        {
            return double.NaN;
        }

        var sum = 0.0;
        var finiteCount = 0;
        for (var index = 0; index < count; index++)
        {
            if (!double.IsFinite(left[index]) || !double.IsFinite(right[index]))
            {
                continue;
            }

            sum += Math.Abs(right[index] - left[index]);
            finiteCount++;
        }

        return finiteCount == 0 ? double.NaN : sum / finiteCount;
    }

    internal static double[]? CreateTemplateExpectedDeltaVector208(
        EcdCwrWaveformTemplateDisplayPackage? package,
        IReadOnlyList<double> reference,
        string differenceOrientation)
    {
        var expectedTarget = CreateTemplateExpectedTargetVector208(package);
        return expectedTarget is null || expectedTarget.Length != reference.Count
            ? null
            : CreateBoundaryDifferenceVector(reference, expectedTarget, differenceOrientation);
    }

    internal static double[]? CreateTemplateExpectedTargetVector208(EcdCwrWaveformTemplateDisplayPackage? package)
    {
        if (package is null || !package.DisplayOnly || package.Windows.Count == 0)
        {
            return null;
        }

        var expected = Enumerable.Repeat(double.NaN, DemodulatedFrame.FlattenedMeasurementCount).ToArray();
        var filled = new bool[DemodulatedFrame.FlattenedMeasurementCount];
        foreach (var window in package.Windows)
        {
            if (window.StimulationIndex < 0 ||
                window.StimulationIndex >= DemodulatedFrame.StimulationCount ||
                window.ExpectedDisplayAmplitudes.Count != DemodulatedFrame.MeasurementsPerStimulation)
            {
                return null;
            }

            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                var index = checked((window.StimulationIndex * DemodulatedFrame.MeasurementsPerStimulation) + column);
                expected[index] = window.ExpectedDisplayAmplitudes[column];
                filled[index] = true;
            }
        }

        return filled.All(value => value) ? expected : null;
    }

    internal static double[] CreateBoundaryDifferenceVector(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        string differenceOrientation)
    {
        var count = Math.Min(reference.Count, target.Count);
        var values = new double[count];
        var referenceMinusTarget = string.Equals(
            differenceOrientation,
            "reference_minus_target",
            StringComparison.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            values[index] = referenceMinusTarget
                ? reference[index] - target[index]
                : target[index] - reference[index];
        }

        return values;
    }
}
