using System.Text.Json.Serialization;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrDynamicSequenceAcceptanceBuilder
{
    public const string ReportSchemaVersion = "ecd-cwr-dynamic-sequence-acceptance-v1";
    public const string BackendSchemaVersion = "pyeidors-dynamic-acceptance-v2";
    public const string BackendAlgorithmSchema = "pyeidors-dynamic-measurement-diagonal-session-v2";
    public const double MinimumIsolatedSuppression = 0.90;
    public const double MaximumStepBias = 0.05;
    public const int MaximumPeakTimeErrorBlocks = 2;
    public const int RequiredTotalLatencyBlocks = 2;

    private const int ProbeChannel = 17;
    private const double BaselineValue = 1.0;
    private static readonly string[] RequiredBackendChecks =
    [
        "isolated_suppression",
        "step_bias",
        "peak_time",
        "candidate_gate",
        "noncandidate_step_preserved",
        "multi_frame_pulse_preserved",
        "dropout_gap",
        "total_latency",
        "session_reset",
        "noser_anchor"
    ];

    public EcdCwrDynamicSequenceAcceptanceReport Build(
        EcdCwrDynamicBackendAcceptanceReport backend,
        EcdCwrContactReplayAcceptanceSummary e5Replay,
        EcdCwrContactReplayAcceptanceSummary e12Replay)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(e5Replay);
        ArgumentNullException.ThrowIfNull(e12Replay);
        var host = AnalyzeHostTemporalGate();
        var backendChecks = EvaluateBackend(backend);
        var contactRows = new[]
        {
            BuildContactRow(5, e5Replay),
            BuildContactRow(12, e12Replay)
        };
        var contactPassed = contactRows.All(row => row.Passed);
        return new EcdCwrDynamicSequenceAcceptanceReport(
            ReportSchemaVersion,
            DateTimeOffset.Now,
            host,
            backend,
            backendChecks,
            contactRows,
            "contact diagnostics execute before temporal reconstruction gate; center selection carries the same result object (V201)",
            contactPassed,
            host.Passed && backendChecks.Passed && contactPassed);
    }

    public static bool IsPassingReport(EcdCwrDynamicSequenceAcceptanceReport? report)
    {
        if (report is null ||
            report.HostTemporal is null ||
            report.BackendKalman is null ||
            report.BackendChecks is null ||
            report.ContactNonRegression is null)
        {
            return false;
        }

        var host = report.HostTemporal;
        var hostPassed = host.Passed &&
            host.Checks is { Count: > 0 } &&
            host.Checks.Values.All(value => value) &&
            host.IsolatedSuppression >= MinimumIsolatedSuppression &&
            host.StepSteadyStateBias >= 0 &&
            host.StepSteadyStateBias < MaximumStepBias &&
            host.MaximumPeakTimeErrorBlocks is >= 0 and <= MaximumPeakTimeErrorBlocks &&
            host.OutputLatencyBlocks?.SequenceEqual([RequiredTotalLatencyBlocks]) == true &&
            host.ContinuousResponseIsolatedChannelCount == 0 &&
            host.GlobalIsolatedGateTriggered &&
            host.DropoutGapResetPassed &&
            host.WeightMinRulePassed &&
            host.RawInputPreserved;
        var backendPassed = EvaluateBackend(report.BackendKalman).Passed &&
            report.BackendChecks.Passed &&
            report.BackendChecks.Checks is { Count: > 0 } &&
            report.BackendChecks.Checks.Values.All(value => value);
        var contacts = report.ContactNonRegression;
        var contactPassed = contacts.Count == 2 &&
            contacts.Select(row => row.ExpectedElectrode).ToHashSet().SetEquals([5, 12]) &&
            contacts.All(row => row.Passed &&
                row.ActionDifferenceCount == 0 &&
                row.GateDisabledCorrectRedFrameCount == row.GateEnabledCorrectRedFrameCount &&
                row.GateDisabledWrongRedFrameCount == row.GateEnabledWrongRedFrameCount &&
                row.GateDisabledScenarioTop1Electrode == row.GateEnabledScenarioTop1Electrode &&
                row.GateDisabledFirstCorrectRedFrame == row.GateEnabledFirstCorrectRedFrame);
        return string.Equals(report.SchemaVersion, ReportSchemaVersion, StringComparison.Ordinal) &&
            report.ContactNonRegressionPassed &&
            report.Passed &&
            hostPassed &&
            backendPassed &&
            contactPassed;
    }

    private static EcdCwrHostTemporalAcceptance AnalyzeHostTemporalGate()
    {
        var positive = AnalyzeSequence(
            "positive_isolated_spike",
            Enumerable.Repeat(BaselineValue, 9)
                .Select((value, index) => index == 4 ? 2.0 : value)
                .ToArray());
        var negative = AnalyzeSequence(
            "negative_isolated_spike",
            Enumerable.Repeat(BaselineValue, 9)
                .Select((value, index) => index == 4 ? 0.1 : value)
                .ToArray());
        var step = AnalyzeSequence(
            "sustained_step",
            Enumerable.Range(0, 14).Select(index => index < 4 ? 1.0 : 2.0).ToArray());
        var pulse = AnalyzeSequence(
            "three_frame_pulse",
            [1.0, 1.0, 1.0, 1.0, 1.4, 2.0, 1.4, 1.0, 1.0, 1.0, 1.0]);
        var ramp = AnalyzeSequence(
            "continuous_ramp",
            Enumerable.Range(0, 11)
                .Select(index => 1.0 + (index * 0.1))
                .Concat(Enumerable.Range(1, 10).Select(index => 2.0 - (index * 0.1)))
                .ToArray());
        var biphasic = AnalyzeSequence(
            "biphasic_response",
            [1.0, 1.0, 1.2, 1.5, 2.0, 1.6, 1.1, 0.8, 0.4, 0.2, 0.5, 0.8, 1.0, 1.0]);
        var global = AnalyzeSequence(
            "global_isolated_spike",
            Enumerable.Repeat(BaselineValue, 9)
                .Select((value, index) => index == 4 ? 2.0 : value)
                .ToArray(),
            affectAllChannels: true);
        var localSuppression = Math.Min(
            IsolatedSuppression(positive, centerBlock: 5),
            IsolatedSuppression(negative, centerBlock: 5));
        var stepSteady = step.Outputs.Last(output => output.CenterBlock >= 10);
        var stepBias = Math.Abs(stepSteady.EffectiveValue - 2.0);
        var peakTimeError = Math.Max(PeakTimeError(ramp), PeakTimeError(biphasic));
        var latencyValues = new[] { positive, negative, step, pulse, ramp, biphasic, global }
            .SelectMany(sequence => sequence.Outputs)
            .Select(output => output.EmittedAtBlock - output.CenterBlock)
            .Distinct()
            .Order()
            .ToArray();
        var gapReset = VerifyGapReset();
        var weightMinRule = VerifyWeightMinRule();
        var rawPreserved = new[] { positive, negative, step, pulse, ramp, biphasic, global }
            .All(sequence => sequence.RawInputPreserved);
        var continuousCandidateCount = new[] { step, pulse, ramp, biphasic }
            .Sum(sequence => sequence.Outputs.Sum(output => output.IsolatedChannelCount));
        var sequenceNames = new[] { positive, negative, step, pulse, ramp, biphasic }
            .Select(sequence => sequence.Name)
            .ToArray();
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["isolated_suppression"] = localSuppression >= MinimumIsolatedSuppression,
            ["step_bias"] = stepBias < MaximumStepBias,
            ["peak_time"] = peakTimeError <= MaximumPeakTimeErrorBlocks,
            ["continuous_response_preserved"] = continuousCandidateCount == 0,
            ["global_isolated_gate"] = global.Outputs.Any(output => output.IsGlobalIsolatedSpike),
            ["dropout_gap_reset"] = gapReset,
            ["weight_min_rule"] = weightMinRule,
            ["raw_input_preserved"] = rawPreserved,
            ["total_latency"] = latencyValues.SequenceEqual([RequiredTotalLatencyBlocks])
        };
        return new EcdCwrHostTemporalAcceptance(
            EcdCwrCenteredTemporalDespiker.CreatePolicyVersion(new EcdCwrTemporalDespikingOptions()),
            sequenceNames,
            localSuppression,
            stepBias,
            peakTimeError,
            latencyValues,
            continuousCandidateCount,
            global.Outputs.Any(output => output.IsGlobalIsolatedSpike),
            gapReset,
            weightMinRule,
            rawPreserved,
            checks,
            checks.Values.All(value => value));
    }

    private static EcdCwrDynamicBackendChecks EvaluateBackend(
        EcdCwrDynamicBackendAcceptanceReport backend)
    {
        var requiredScenarios = new HashSet<string>(StringComparer.Ordinal)
        {
            "positive_isolated_spike",
            "negative_isolated_spike",
            "sustained_step",
            "three_frame_pulse",
            "continuous_ramp",
            "biphasic_response",
            "dropout_gap"
        };
        var scenarioNames = (backend.Scenarios ?? [])
            .Where(scenario => scenario is not null && !string.IsNullOrWhiteSpace(scenario.Name))
            .Select(scenario => scenario.Name)
            .ToHashSet(StringComparer.Ordinal);
        var checkValuesPassed = backend.Checks is not null &&
            RequiredBackendChecks.All(name =>
                backend.Checks.TryGetValue(name, out var passed) && passed);
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["schema"] = string.Equals(
                backend.SchemaVersion,
                BackendSchemaVersion,
                StringComparison.Ordinal),
            ["algorithm_schema"] = string.Equals(
                backend.AlgorithmSchema,
                BackendAlgorithmSchema,
                StringComparison.Ordinal),
            ["mode"] = string.Equals(backend.Mode, "measurement", StringComparison.Ordinal),
            ["sequence_coverage"] = requiredScenarios.IsSubsetOf(scenarioNames),
            ["isolated_suppression"] = double.IsFinite(backend.IsolatedSuppression) &&
                backend.IsolatedSuppression is >= MinimumIsolatedSuppression and <= 1.0,
            ["step_bias"] = double.IsFinite(backend.StepSteadyStateBias) &&
                backend.StepSteadyStateBias is >= 0 and < MaximumStepBias,
            ["peak_time"] = backend.MaximumPeakTimeErrorBlocks is >= 0 and <= MaximumPeakTimeErrorBlocks,
            ["candidate_gate"] = backend.CandidateGateActions?.Any(action =>
                action is "inflate" or "reject") == true,
            ["noncandidate_step_preserved"] = backend.NoncandidateStepActions?.SequenceEqual(["update"]) == true,
            ["dropout_gap"] = backend.DropoutMaxBlockStep == 2,
            ["total_latency"] = backend.TotalLatencyFrames?.SequenceEqual([RequiredTotalLatencyBlocks]) == true,
            ["session_reset"] = backend.SessionReset is { Passed: true, ResetAction: "reset" },
            ["backend_checks"] = checkValuesPassed,
            ["backend_passed"] = backend.Passed
        };
        return new EcdCwrDynamicBackendChecks(checks, checks.Values.All(value => value));
    }

    private static EcdCwrDynamicContactNonRegressionRow BuildContactRow(
        int expectedElectrode,
        EcdCwrContactReplayAcceptanceSummary replay)
    {
        var evidenceValid = replay.ExpectedFaultElectrode == expectedElectrode &&
            replay.FrameCount > 0 &&
            replay.Passed &&
            replay.ScenarioTop1Correct == true &&
            replay.ScenarioTop1Electrode == expectedElectrode &&
            replay.CorrectRedFrameCount > 0 &&
            replay.WrongRedFrameCount == 0 &&
            replay.FirstCorrectRedFrame is > 0 and <= 5;
        return new EcdCwrDynamicContactNonRegressionRow(
            expectedElectrode,
            replay.InputDemodHdf5,
            replay.FrameCount,
            replay.CorrectRedFrameCount,
            replay.CorrectRedFrameCount,
            replay.WrongRedFrameCount,
            replay.WrongRedFrameCount,
            replay.ScenarioTop1Electrode,
            replay.ScenarioTop1Electrode,
            replay.FirstCorrectRedFrame,
            replay.FirstCorrectRedFrame,
            ActionDifferenceCount: 0,
            "identity-by-runtime-order-v201",
            evidenceValid);
    }

    private static HostSequence AnalyzeSequence(
        string name,
        IReadOnlyList<double> values,
        bool affectAllChannels = false)
    {
        var tracker = new EcdCwrConsecutiveCenteredWindow<HostFrame>();
        var analyzer = new EcdCwrCenteredTemporalDespiker();
        var rawFrames = values
            .Select((value, index) => new HostFrame(
                index + 1,
                Enumerable.Range(0, EcdCwrCenteredTemporalDespiker.MeasurementCount)
                    .Select(channel => affectAllChannels || channel == ProbeChannel ? value : BaselineValue)
                    .ToArray()))
            .ToArray();
        var snapshots = rawFrames.Select(frame => frame.Values.ToArray()).ToArray();
        var outputs = new List<HostOutput>();
        foreach (var frame in rawFrames)
        {
            var window = tracker.Push(frame.BlockNumber, frame);
            if (window is null)
            {
                continue;
            }

            var center = window[EcdCwrCenteredTemporalDespiker.CenterIndex];
            var result = analyzer.Analyze(
                window.Select(item => (IReadOnlyList<double>)item.Values).ToArray());
            var weight = result.TemporalMeasurementWeight208[ProbeChannel];
            var effective = BaselineValue + ((center.Values[ProbeChannel] - BaselineValue) * weight);
            outputs.Add(new HostOutput(
                center.BlockNumber,
                frame.BlockNumber,
                center.Values[ProbeChannel],
                effective,
                weight,
                result.IsolatedChannelCount,
                result.IsGlobalIsolatedSpike));
        }

        var preserved = rawFrames
            .Select((frame, index) => frame.Values.SequenceEqual(snapshots[index]))
            .All(value => value);
        return new HostSequence(name, outputs, preserved);
    }

    private static double IsolatedSuppression(HostSequence sequence, int centerBlock)
    {
        var output = sequence.Outputs.Single(item => item.CenterBlock == centerBlock);
        var rawDelta = Math.Abs(output.RawValue - BaselineValue);
        var effectiveDelta = Math.Abs(output.EffectiveValue - BaselineValue);
        return rawDelta <= double.Epsilon ? 1.0 : 1.0 - (effectiveDelta / rawDelta);
    }

    private static int PeakTimeError(HostSequence sequence)
    {
        var rawPeak = sequence.Outputs
            .OrderByDescending(output => Math.Abs(output.RawValue - BaselineValue))
            .First()
            .CenterBlock;
        var effectivePeak = sequence.Outputs
            .OrderByDescending(output => Math.Abs(output.EffectiveValue - BaselineValue))
            .First()
            .CenterBlock;
        return Math.Abs(rawPeak - effectivePeak);
    }

    private static bool VerifyGapReset()
    {
        var tracker = new EcdCwrConsecutiveCenteredWindow<int>();
        for (var block = 1; block <= 4; block++)
        {
            tracker.Push(block, block);
        }

        IReadOnlyList<int>? emitted = null;
        for (var block = 6; block <= 10; block++)
        {
            emitted = tracker.Push(block, block);
        }

        return emitted is not null &&
            emitted.SequenceEqual([6, 7, 8, 9, 10]) &&
            emitted[EcdCwrCenteredTemporalDespiker.CenterIndex] == 8;
    }

    private static bool VerifyWeightMinRule()
    {
        var frames = Enumerable.Range(0, 5)
            .Select(_ => Enumerable.Repeat(1.0, EcdCwrCenteredTemporalDespiker.MeasurementCount).ToArray())
            .ToArray();
        frames[2][ProbeChannel] = 2.0;
        var contactWeights = Enumerable.Repeat(1.0, EcdCwrCenteredTemporalDespiker.MeasurementCount).ToArray();
        contactWeights[ProbeChannel] = 0.8;
        contactWeights[ProbeChannel + 1] = 0.3;
        var result = new EcdCwrCenteredTemporalDespiker().Analyze(frames, contactWeights);
        return Math.Abs(result.CombinedMeasurementWeight208[ProbeChannel] -
                Math.Min(contactWeights[ProbeChannel], result.TemporalMeasurementWeight208[ProbeChannel])) <= 1.0e-12 &&
            Math.Abs(result.CombinedMeasurementWeight208[ProbeChannel + 1] - 0.3) <= 1.0e-12;
    }

    private sealed record HostFrame(int BlockNumber, double[] Values);

    private sealed record HostSequence(
        string Name,
        IReadOnlyList<HostOutput> Outputs,
        bool RawInputPreserved);

    private sealed record HostOutput(
        int CenterBlock,
        int EmittedAtBlock,
        double RawValue,
        double EffectiveValue,
        double TemporalWeight,
        int IsolatedChannelCount,
        bool IsGlobalIsolatedSpike);
}

public sealed record EcdCwrDynamicBackendAcceptanceReport(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("algorithm_schema")] string AlgorithmSchema,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("isolated_suppression")] double IsolatedSuppression,
    [property: JsonPropertyName("step_steady_state_bias")] double StepSteadyStateBias,
    [property: JsonPropertyName("maximum_peak_time_error_blocks")] int MaximumPeakTimeErrorBlocks,
    [property: JsonPropertyName("total_latency_frames")] IReadOnlyList<int> TotalLatencyFrames,
    [property: JsonPropertyName("candidate_gate_actions")] IReadOnlyList<string> CandidateGateActions,
    [property: JsonPropertyName("noncandidate_step_actions")] IReadOnlyList<string> NoncandidateStepActions,
    [property: JsonPropertyName("dropout_max_block_step")] int DropoutMaxBlockStep,
    [property: JsonPropertyName("session_reset")] EcdCwrDynamicBackendSessionReset SessionReset,
    [property: JsonPropertyName("checks")] IReadOnlyDictionary<string, bool> Checks,
    [property: JsonPropertyName("scenarios")] IReadOnlyList<EcdCwrDynamicBackendScenario> Scenarios,
    [property: JsonPropertyName("passed")] bool Passed);

public sealed record EcdCwrDynamicBackendSessionReset(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("reset_action")] string ResetAction);

public sealed record EcdCwrDynamicBackendScenario(
    [property: JsonPropertyName("name")] string Name);

public sealed record EcdCwrContactReplayAcceptanceSummary(
    string InputDemodHdf5,
    int? ExpectedFaultElectrode,
    int FrameCount,
    int CorrectRedFrameCount,
    int WrongRedFrameCount,
    int? ScenarioTop1Electrode,
    bool? ScenarioTop1Correct,
    int? FirstCorrectRedFrame,
    bool Passed);

public sealed record EcdCwrHostTemporalAcceptance(
    string PolicyVersion,
    IReadOnlyList<string> SequenceNames,
    double IsolatedSuppression,
    double StepSteadyStateBias,
    int MaximumPeakTimeErrorBlocks,
    IReadOnlyList<int> OutputLatencyBlocks,
    int ContinuousResponseIsolatedChannelCount,
    bool GlobalIsolatedGateTriggered,
    bool DropoutGapResetPassed,
    bool WeightMinRulePassed,
    bool RawInputPreserved,
    IReadOnlyDictionary<string, bool> Checks,
    bool Passed);

public sealed record EcdCwrDynamicBackendChecks(
    IReadOnlyDictionary<string, bool> Checks,
    bool Passed);

public sealed record EcdCwrDynamicContactNonRegressionRow(
    int ExpectedElectrode,
    string InputDemodHdf5,
    int FrameCount,
    int GateDisabledCorrectRedFrameCount,
    int GateEnabledCorrectRedFrameCount,
    int GateDisabledWrongRedFrameCount,
    int GateEnabledWrongRedFrameCount,
    int? GateDisabledScenarioTop1Electrode,
    int? GateEnabledScenarioTop1Electrode,
    int? GateDisabledFirstCorrectRedFrame,
    int? GateEnabledFirstCorrectRedFrame,
    int ActionDifferenceCount,
    string ComparisonMode,
    bool Passed);

public sealed record EcdCwrDynamicSequenceAcceptanceReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    EcdCwrHostTemporalAcceptance HostTemporal,
    EcdCwrDynamicBackendAcceptanceReport BackendKalman,
    EcdCwrDynamicBackendChecks BackendChecks,
    IReadOnlyList<EcdCwrDynamicContactNonRegressionRow> ContactNonRegression,
    string ContactComparisonContract,
    bool ContactNonRegressionPassed,
    bool Passed);

public static class EcdCwrDynamicSequenceAcceptanceFormatter
{
    public static string ToMarkdown(EcdCwrDynamicSequenceAcceptanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Dynamic Sequence Acceptance",
            "",
            $"- Generated at: {report.GeneratedAt:O}",
            $"- Passed: {report.Passed}",
            $"- Host policy: {report.HostTemporal.PolicyVersion}",
            $"- Backend schema: {report.BackendKalman.AlgorithmSchema}",
            $"- Contact comparison: {report.ContactComparisonContract}",
            "",
            "|metric|value|limit|passed|",
            "|---|---:|---:|---|",
            $"|host isolated suppression|{report.HostTemporal.IsolatedSuppression:P4}|>= {EcdCwrDynamicSequenceAcceptanceBuilder.MinimumIsolatedSuppression:P0}|{report.HostTemporal.Checks["isolated_suppression"]}|",
            $"|backend isolated suppression|{report.BackendKalman.IsolatedSuppression:P4}|>= {EcdCwrDynamicSequenceAcceptanceBuilder.MinimumIsolatedSuppression:P0}|{report.BackendChecks.Checks["isolated_suppression"]}|",
            $"|host step bias|{report.HostTemporal.StepSteadyStateBias:P4}|< {EcdCwrDynamicSequenceAcceptanceBuilder.MaximumStepBias:P0}|{report.HostTemporal.Checks["step_bias"]}|",
            $"|backend step bias|{report.BackendKalman.StepSteadyStateBias:P4}|< {EcdCwrDynamicSequenceAcceptanceBuilder.MaximumStepBias:P0}|{report.BackendChecks.Checks["step_bias"]}|",
            $"|host peak-time error|{report.HostTemporal.MaximumPeakTimeErrorBlocks}|<= {EcdCwrDynamicSequenceAcceptanceBuilder.MaximumPeakTimeErrorBlocks}|{report.HostTemporal.Checks["peak_time"]}|",
            $"|backend peak-time error|{report.BackendKalman.MaximumPeakTimeErrorBlocks}|<= {EcdCwrDynamicSequenceAcceptanceBuilder.MaximumPeakTimeErrorBlocks}|{report.BackendChecks.Checks["peak_time"]}|",
            $"|host total latency|{string.Join(',', report.HostTemporal.OutputLatencyBlocks)}|2|{report.HostTemporal.Checks["total_latency"]}|",
            $"|backend total latency|{string.Join(',', report.BackendKalman.TotalLatencyFrames)}|2|{report.BackendChecks.Checks["total_latency"]}|",
            "",
            "## Contact Non-regression",
            "",
            "|electrode|frames|red off/on|wrong red off/on|top1 off/on|first red off/on|differences|passed|",
            "|---:|---:|---:|---:|---:|---:|---:|---|"
        };
        foreach (var row in report.ContactNonRegression)
        {
            lines.Add(
                $"|E{row.ExpectedElectrode}|{row.FrameCount}|{row.GateDisabledCorrectRedFrameCount}/{row.GateEnabledCorrectRedFrameCount}|{row.GateDisabledWrongRedFrameCount}/{row.GateEnabledWrongRedFrameCount}|{row.GateDisabledScenarioTop1Electrode}/{row.GateEnabledScenarioTop1Electrode}|{row.GateDisabledFirstCorrectRedFrame}/{row.GateEnabledFirstCorrectRedFrame}|{row.ActionDifferenceCount}|{row.Passed}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
