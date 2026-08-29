using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.Core.Diagnostics.Baseline;

public enum EitBaselineIntegrityClassification
{
    NoiseFloor,
    CommonScaleAmbiguous,
    StructuredChange,
    MixedChange,
    DemodStateChanged
}

public sealed record EitDemodulationFingerprint(
    double EstimatedWindowSamples,
    int UniformOffsetSamples,
    int RotationStartChannelOneBased,
    int RotationDirection)
{
    public static EitDemodulationFingerprint Stable(
        double estimatedWindowSamples,
        int uniformOffsetSamples,
        int rotationStartChannelOneBased,
        int rotationDirection)
    {
        return new EitDemodulationFingerprint(
            estimatedWindowSamples,
            uniformOffsetSamples,
            rotationStartChannelOneBased,
            rotationDirection);
    }
}

public sealed record EitBaselineIntegrityResult(
    EitBaselineIntegrityClassification Classification,
    bool DemodStateChanged,
    double GlobalNoiseScore,
    double GlobalNoiseThreshold,
    double CommonScale,
    double ShapeResidualRelative,
    double ComplexScaleMagnitude,
    double ComplexPhaseDegrees,
    double ComplexShapeResidualRelative,
    double CommonModeEnergyFraction,
    double NearDriveScale,
    double RemoteScale)
{
    public string StorageClassification => Classification switch
    {
        EitBaselineIntegrityClassification.NoiseFloor => "noise_floor",
        EitBaselineIntegrityClassification.CommonScaleAmbiguous => "common_scale_ambiguous",
        EitBaselineIntegrityClassification.StructuredChange => "structured_change",
        EitBaselineIntegrityClassification.MixedChange => "mixed_change",
        EitBaselineIntegrityClassification.DemodStateChanged => "demod_state_changed",
        _ => "mixed_change"
    };

    public string ToChineseSummary(int referenceEpoch)
    {
        var label = Classification switch
        {
            EitBaselineIntegrityClassification.NoiseFloor => "噪声范围内",
            EitBaselineIntegrityClassification.CommonScaleAmbiguous => "共同比例变化（来源待判）",
            EitBaselineIntegrityClassification.StructuredChange => "结构性变化",
            EitBaselineIntegrityClassification.MixedChange => "混合变化",
            EitBaselineIntegrityClassification.DemodStateChanged => "解调状态改变",
            _ => "待判"
        };
        return FormattableString.Invariant(
            $"基线诊断 e{referenceEpoch}：{label} · α={CommonScale:F6} ({(CommonScale - 1.0) * 100.0:+0.0000;-0.0000;0.0000}%) · 形状={ShapeResidualRelative * 100.0:F4}% · |β|={ComplexScaleMagnitude:F6} · β相位={ComplexPhaseDegrees:+0.000;-0.000;0.000}° · 复残={ComplexShapeResidualRelative * 100.0:F4}% · 噪声={GlobalNoiseScore:F2}/{GlobalNoiseThreshold:F2} · 共模能量={CommonModeEnergyFraction * 100.0:F1}% · 近/远={NearDriveScale:F6}/{RemoteScale:F6}");
    }
}

/// <summary>
/// Computes read-only evidence about movement away from a locked reference.
/// The analyzer never changes either input vector and does not return a
/// compensated reconstruction vector.
/// </summary>
public sealed class EitBaselineIntegrityAnalyzer
{
    private const double CommonDominanceThreshold = 0.90;
    private const double StructuredDominanceThreshold = 0.10;

    public EitBaselineIntegrityResult Analyze(
        IReadOnlyList<double> referenceAmplitude208,
        IReadOnlyList<double> referenceReal208,
        IReadOnlyList<double> referenceImaginary208,
        IReadOnlyList<double> targetAmplitude208,
        IReadOnlyList<double> targetReal208,
        IReadOnlyList<double> targetImaginary208,
        EcdCwrBoundaryNoiseModel noiseModel,
        EitDemodulationFingerprint referenceDemodulation,
        EitDemodulationFingerprint targetDemodulation)
    {
        ValidateVector(referenceAmplitude208, nameof(referenceAmplitude208));
        ValidateVector(referenceReal208, nameof(referenceReal208));
        ValidateVector(referenceImaginary208, nameof(referenceImaginary208));
        ValidateVector(targetAmplitude208, nameof(targetAmplitude208));
        ValidateVector(targetReal208, nameof(targetReal208));
        ValidateVector(targetImaginary208, nameof(targetImaginary208));
        ArgumentNullException.ThrowIfNull(noiseModel);
        ArgumentNullException.ThrowIfNull(referenceDemodulation);
        ArgumentNullException.ThrowIfNull(targetDemodulation);

        var commonScale = LeastSquaresScale(referenceAmplitude208, targetAmplitude208);
        var shapeResidual = RelativeResidual(referenceAmplitude208, targetAmplitude208, commonScale);
        var commonFraction = CalculateCommonModeEnergyFraction(
            referenceAmplitude208,
            targetAmplitude208,
            commonScale);
        var nearScale = GroupScale(referenceAmplitude208, targetAmplitude208, IsNearDriveMeasurement);
        var remoteScale = GroupScale(referenceAmplitude208, targetAmplitude208, IsRemoteMeasurement);
        var (complexMagnitude, complexPhase, complexResidual) = ComplexLeastSquares(
            referenceReal208,
            referenceImaginary208,
            targetReal208,
            targetImaginary208);
        var globalScore = noiseModel.CalculateGlobalScore(targetAmplitude208);
        var demodChanged = HasDemodulationStateChanged(referenceDemodulation, targetDemodulation);
        var classification = Classify(
            demodChanged,
            globalScore,
            noiseModel.GlobalScoreThreshold,
            commonFraction);

        return new EitBaselineIntegrityResult(
            classification,
            demodChanged,
            globalScore,
            noiseModel.GlobalScoreThreshold,
            commonScale,
            shapeResidual,
            complexMagnitude,
            complexPhase,
            complexResidual,
            commonFraction,
            nearScale,
            remoteScale);
    }

    public static double[] SelectRetainedMeasurements(IReadOnlyList<double> fullVector256)
    {
        ArgumentNullException.ThrowIfNull(fullVector256);
        if (fullVector256.Count != DemodulatedFrame.FlattenedFullMeasurementCount ||
            fullVector256.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Full demodulation vector must contain 256 finite values.", nameof(fullVector256));
        }

        var retained = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var output = 0;
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            var row = stimulation * DemodulatedFrame.FullMeasurementsPerStimulation;
            for (var relativeChannel = 2; relativeChannel <= 14; relativeChannel++)
            {
                retained[output++] = fullVector256[row + relativeChannel];
            }
        }

        return retained;
    }

    private static EitBaselineIntegrityClassification Classify(
        bool demodChanged,
        double globalScore,
        double noiseThreshold,
        double commonFraction)
    {
        if (demodChanged)
        {
            return EitBaselineIntegrityClassification.DemodStateChanged;
        }

        if (globalScore <= noiseThreshold)
        {
            return EitBaselineIntegrityClassification.NoiseFloor;
        }

        if (commonFraction >= CommonDominanceThreshold)
        {
            return EitBaselineIntegrityClassification.CommonScaleAmbiguous;
        }

        return commonFraction <= StructuredDominanceThreshold
            ? EitBaselineIntegrityClassification.StructuredChange
            : EitBaselineIntegrityClassification.MixedChange;
    }

    private static bool HasDemodulationStateChanged(
        EitDemodulationFingerprint reference,
        EitDemodulationFingerprint target)
    {
        var windowTolerance = Math.Max(0.5, Math.Abs(reference.EstimatedWindowSamples) * 0.005);
        return Math.Abs(target.EstimatedWindowSamples - reference.EstimatedWindowSamples) > windowTolerance ||
            target.RotationDirection != reference.RotationDirection;
    }

    private static double LeastSquaresScale(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target)
    {
        var numerator = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < reference.Count; index++)
        {
            numerator += reference[index] * target[index];
            denominator += reference[index] * reference[index];
        }

        return denominator > double.Epsilon ? numerator / denominator : 1.0;
    }

    private static double GroupScale(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        Func<int, bool> includeMeasurement)
    {
        var numerator = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < reference.Count; index++)
        {
            var relativeMeasurement = index % DemodulatedFrame.MeasurementsPerStimulation;
            if (!includeMeasurement(relativeMeasurement))
            {
                continue;
            }

            numerator += reference[index] * target[index];
            denominator += reference[index] * reference[index];
        }

        return denominator > double.Epsilon ? numerator / denominator : 1.0;
    }

    private static bool IsNearDriveMeasurement(int measurementIndex)
    {
        return measurementIndex is 0 or 1 or 11 or 12;
    }

    private static bool IsRemoteMeasurement(int measurementIndex)
    {
        return measurementIndex is >= 4 and <= 8;
    }

    private static double RelativeResidual(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        double scale)
    {
        var residualEnergy = 0.0;
        var referenceEnergy = 0.0;
        for (var index = 0; index < reference.Count; index++)
        {
            var residual = target[index] - (scale * reference[index]);
            residualEnergy += residual * residual;
            referenceEnergy += reference[index] * reference[index];
        }

        return referenceEnergy > double.Epsilon
            ? Math.Sqrt(residualEnergy / referenceEnergy)
            : 0.0;
    }

    private static double CalculateCommonModeEnergyFraction(
        IReadOnlyList<double> reference,
        IReadOnlyList<double> target,
        double scale)
    {
        var commonEnergy = 0.0;
        var shapeEnergy = 0.0;
        for (var index = 0; index < reference.Count; index++)
        {
            var common = (scale - 1.0) * reference[index];
            var shape = target[index] - (scale * reference[index]);
            commonEnergy += common * common;
            shapeEnergy += shape * shape;
        }

        var total = commonEnergy + shapeEnergy;
        return total > double.Epsilon ? commonEnergy / total : 0.0;
    }

    private static (double Magnitude, double PhaseDegrees, double RelativeResidual) ComplexLeastSquares(
        IReadOnlyList<double> referenceReal,
        IReadOnlyList<double> referenceImaginary,
        IReadOnlyList<double> targetReal,
        IReadOnlyList<double> targetImaginary)
    {
        var numeratorReal = 0.0;
        var numeratorImaginary = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < referenceReal.Count; index++)
        {
            var rr = referenceReal[index];
            var ri = referenceImaginary[index];
            var tr = targetReal[index];
            var ti = targetImaginary[index];
            numeratorReal += (rr * tr) + (ri * ti);
            numeratorImaginary += (rr * ti) - (ri * tr);
            denominator += (rr * rr) + (ri * ri);
        }

        if (denominator <= double.Epsilon)
        {
            return (1.0, 0.0, 0.0);
        }

        var betaReal = numeratorReal / denominator;
        var betaImaginary = numeratorImaginary / denominator;
        var residualEnergy = 0.0;
        for (var index = 0; index < referenceReal.Count; index++)
        {
            var fittedReal = (betaReal * referenceReal[index]) - (betaImaginary * referenceImaginary[index]);
            var fittedImaginary = (betaReal * referenceImaginary[index]) + (betaImaginary * referenceReal[index]);
            var residualReal = targetReal[index] - fittedReal;
            var residualImaginary = targetImaginary[index] - fittedImaginary;
            residualEnergy += (residualReal * residualReal) + (residualImaginary * residualImaginary);
        }

        return (
            Math.Sqrt((betaReal * betaReal) + (betaImaginary * betaImaginary)),
            Math.Atan2(betaImaginary, betaReal) * 180.0 / Math.PI,
            Math.Sqrt(residualEnergy / denominator));
    }

    private static void ValidateVector(IReadOnlyList<double> vector, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            vector.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Baseline integrity requires 208 finite values.", parameterName);
        }
    }
}
