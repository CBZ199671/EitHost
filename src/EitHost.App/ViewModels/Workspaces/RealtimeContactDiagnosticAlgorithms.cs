using System.Diagnostics;
using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels.Workspaces;

internal static class RealtimeContactDiagnosticAlgorithms
{
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromMilliseconds(500);

    internal static bool ShouldRun(
        RealtimeRunState state,
        IReadOnlyList<DemodulatedWindowQuality> qualities,
        bool forceModeTransition = false)
    {
        if (forceModeTransition)
        {
            Interlocked.Exchange(ref state.LastContactAnalysisTicks, Stopwatch.GetTimestamp());
            return true;
        }

        var saturationPresent = qualities.Any(quality =>
            quality.AdcSaturationCount > 0 ||
            quality.RejectReason == DemodulatedWindowRejectReason.AdcSaturation);
        var saturationAlreadyActive = state.LatestContactResult is { } latest &&
            (latest.SystemLevel || latest.States.Any(contactState =>
                contactState == ElectrodeContactState.DarkRed));
        if (saturationPresent && !saturationAlreadyActive)
        {
            Interlocked.Exchange(ref state.LastContactAnalysisTicks, Stopwatch.GetTimestamp());
            return true;
        }

        return ShouldUpdate(ref state.LastContactAnalysisTicks, AnalysisInterval);
    }

    internal static IReadOnlyList<EcdCwrFrequencyEvidenceFrame> BuildPeerFrequencyEvidence(
        RealtimeImagingRunConfig config,
        RealtimeDemodulatedBlock block)
    {
        if (!config.UseFrequencyDivisionLockIn ||
            config.InterferenceFrequencyHz.Count == 0 ||
            block.Average.FrequencyFrames is not { Count: > 0 } frequencyFrames)
        {
            return [];
        }

        return frequencyFrames
            .Where(frame => config.InterferenceFrequencyHz.Any(frequency =>
                Math.Abs(frequency - frame.FrequencyHz) <= 1.0e-6))
            .Select(CreateFrequencyEvidenceFrame)
            .ToArray();
    }

    private static EcdCwrFrequencyEvidenceFrame CreateFrequencyEvidenceFrame(
        DemodulatedFrequencyFrame frame)
    {
        var drive = new double[DemodulatedFrame.StimulationCount];
        var left = new double[DemodulatedFrame.StimulationCount];
        var right = new double[DemodulatedFrame.StimulationCount];
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            drive[stimulation] = ReadFinite(frame.FullAmplitudes, stimulation, 0);
            right[stimulation] = ReadFinite(frame.FullAmplitudes, stimulation, 1);
            left[stimulation] = ReadFinite(
                frame.FullAmplitudes,
                stimulation,
                DemodulatedFrame.FullMeasurementsPerStimulation - 1);
        }

        var driveScore = RobustPositiveScores(drive);
        var leftScore = RobustPositiveScores(left);
        var rightScore = RobustPositiveScores(right);
        var scores = new double[DemodulatedFrame.StimulationCount];
        var phaseReal = new double[DemodulatedFrame.StimulationCount];
        var phaseImaginary = new double[DemodulatedFrame.StimulationCount];
        for (var electrode = 0; electrode < DemodulatedFrame.StimulationCount; electrode++)
        {
            var previousStim = Mod(electrode - 1);
            var currentStim = electrode;
            scores[electrode] = Math.Max(
                Math.Max(driveScore[previousStim], driveScore[currentStim]),
                Math.Max(leftScore[electrode], rightScore[previousStim]));
            AddPhaseContribution(frame, currentStim, 0, phaseReal, phaseImaginary, electrode);
            AddPhaseContribution(frame, previousStim, 0, phaseReal, phaseImaginary, electrode);
            AddPhaseContribution(
                frame,
                electrode,
                DemodulatedFrame.FullMeasurementsPerStimulation - 1,
                phaseReal,
                phaseImaginary,
                electrode);
            AddPhaseContribution(frame, previousStim, 1, phaseReal, phaseImaginary, electrode);
        }

        var phaseScores = Enumerable.Range(0, DemodulatedFrame.StimulationCount)
            .Select(electrode => Math.Atan2(phaseImaginary[electrode], phaseReal[electrode]))
            .ToArray();
        return new EcdCwrFrequencyEvidenceFrame(frame.FrequencyHz, scores, phaseScores);
    }

    private static void AddPhaseContribution(
        DemodulatedFrequencyFrame frame,
        int stimulation,
        int relativeChannel,
        double[] phaseReal,
        double[] phaseImaginary,
        int electrode)
    {
        phaseReal[electrode] += ReadFinite(frame.FullRealComponents, Mod(stimulation), relativeChannel);
        phaseImaginary[electrode] += ReadFinite(frame.FullImaginaryComponents, Mod(stimulation), relativeChannel);
    }

    private static double ReadFinite(double[,] values, int row, int column)
    {
        var value = values[row, column];
        return double.IsFinite(value) ? value : 0.0;
    }

    private static double[] RobustPositiveScores(IReadOnlyList<double> values)
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
            .Select(value => double.IsFinite(value) ? Math.Max(0.0, (value - median) / scale) : 0.0)
            .ToArray();
    }

    private static double MedianSorted(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0.0;
        }

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static int Mod(int value)
    {
        var result = value % DemodulatedFrame.StimulationCount;
        return result < 0 ? result + DemodulatedFrame.StimulationCount : result;
    }

    private static bool ShouldUpdate(ref long lastTicks, TimeSpan interval)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref lastTicks);
        if (previous != 0 && Stopwatch.GetElapsedTime(previous, now) < interval)
        {
            return false;
        }

        Interlocked.Exchange(ref lastTicks, now);
        return true;
    }
}
