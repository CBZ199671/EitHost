using EitHost.Core.Demodulation;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrWaveformTemplateDisplayBuilder
{
    public const string DisplayPolicyVersion = "display-only-template-v1";

    public EcdCwrWaveformTemplateDisplayPackage Build(
        DemodulatedFrame frame,
        EcdCwrHealthCalibration calibration,
        EcdCwrWaveformTemplateDisplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibration);
        options ??= new EcdCwrWaveformTemplateDisplayOptions();
        ValidateRetainedAmplitudeFrame(frame);

        var templates = calibration.WaveformTemplates.ToDictionary(
            template => template.StimulationIndex,
            template => template);
        var windows = new List<EcdCwrWaveformTemplateDisplayWindow>(DemodulatedFrame.StimulationCount);
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            if (!templates.TryGetValue(stimulation, out var template) ||
                template.NormalizedMedianAmplitudes.Count != DemodulatedFrame.MeasurementsPerStimulation)
            {
                continue;
            }

            var observed = Enumerable.Range(0, DemodulatedFrame.MeasurementsPerStimulation)
                .Select(column => Sanitize(frame.Amplitudes[stimulation, column]))
                .ToArray();
            var scale = Math.Max(options.NormalizationFloor, Median(observed.Where(value => value > 0.0).ToArray()));
            var expectedNormalized = template.NormalizedMedianAmplitudes
                .Select(Sanitize)
                .ToArray();
            var observedNormalized = observed
                .Select(value => value / scale)
                .ToArray();
            var expected = expectedNormalized
                .Select(value => value * scale)
                .ToArray();
            var residual = observed
                .Zip(expected, (left, right) => left - right)
                .ToArray();

            windows.Add(new EcdCwrWaveformTemplateDisplayWindow(
                stimulation,
                template.RelativeChannelIndices.ToArray(),
                scale,
                observed,
                expected,
                residual,
                observedNormalized,
                expectedNormalized,
                DisplayOnly: true));
        }

        return new EcdCwrWaveformTemplateDisplayPackage(
            DisplayPolicyVersion,
            DisplayOnly: true,
            windows);
    }

    private static void ValidateRetainedAmplitudeFrame(DemodulatedFrame frame)
    {
        if (frame.Amplitudes.GetLength(0) != DemodulatedFrame.StimulationCount ||
            frame.Amplitudes.GetLength(1) != DemodulatedFrame.MeasurementsPerStimulation)
        {
            throw new ArgumentException("Waveform template display requires a [16,13] retained amplitude frame.", nameof(frame));
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var finite = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
        if (finite.Length == 0)
        {
            return 0.0;
        }

        var middle = finite.Length / 2;
        return finite.Length % 2 == 1
            ? finite[middle]
            : (finite[middle - 1] + finite[middle]) / 2.0;
    }

    private static double Sanitize(double value)
    {
        return double.IsFinite(value) ? value : 0.0;
    }
}

public sealed record EcdCwrWaveformTemplateDisplayOptions(
    double NormalizationFloor = 1.0e-12);

public sealed record EcdCwrWaveformTemplateDisplayPackage(
    string PolicyVersion,
    bool DisplayOnly,
    IReadOnlyList<EcdCwrWaveformTemplateDisplayWindow> Windows);

public sealed record EcdCwrWaveformTemplateDisplayWindow(
    int StimulationIndex,
    IReadOnlyList<int> RelativeChannelIndices,
    double Scale,
    IReadOnlyList<double> ObservedAmplitudes,
    IReadOnlyList<double> ExpectedDisplayAmplitudes,
    IReadOnlyList<double> ResidualAmplitudes,
    IReadOnlyList<double> ObservedNormalizedAmplitudes,
    IReadOnlyList<double> ExpectedNormalizedAmplitudes,
    bool DisplayOnly);
