namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrTraceabilityReplayVerifier
{
    public static string ToMarkdown(EcdCwrTraceabilityReplayVerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Traceability Replay Verification",
            "",
            $"- Verified at: {report.VerifiedAt:O}",
            $"- Expected predictions: {report.ExpectedPredictionCount}",
            $"- Replayed predictions: {report.ReplayedPredictionCount}",
            $"- Expected diagnostic policy: {report.ExpectedDiagnosticPolicyVersion ?? "missing"}",
            $"- Replayed diagnostic policy: {report.ReplayedDiagnosticPolicyVersion ?? "missing"}",
            $"- Passed items: {report.PassedItems}",
            $"- Failed items: {report.FailedItems}",
            $"- Score tolerance: {report.ScoreTolerance:G}",
            $"- Image quality tolerance: {report.ImageQualityTolerance:G}",
            $"- Passed: {report.Passed}",
            "",
            "## Issues",
            "",
            "|scenario|status|issues|",
            "|---|---|---|"
        };
        foreach (var item in report.Items.Where(item => !item.Passed))
        {
            lines.Add($"|{item.ScenarioId}|failed|{string.Join("<br>", item.Issues)}|");
        }

        if (report.Items.All(item => item.Passed))
        {
            lines.Add("|all|passed|none|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public EcdCwrTraceabilityReplayVerificationReport Verify(
        IReadOnlyList<EcdCwrSimulationPrediction> expectedPredictions,
        IReadOnlyList<EcdCwrSimulationPrediction> replayedPredictions,
        EcdCwrTraceabilityReplayVerificationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expectedPredictions);
        ArgumentNullException.ThrowIfNull(replayedPredictions);
        options ??= new EcdCwrTraceabilityReplayVerificationOptions();
        var expectedByScenario = expectedPredictions
            .GroupBy(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var replayedByScenario = replayedPredictions
            .GroupBy(item => item.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var scenarioIds = expectedByScenario.Keys
            .Union(replayedByScenario.Keys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var items = scenarioIds
            .Select(id => VerifyItem(
                id,
                expectedByScenario.GetValueOrDefault(id),
                replayedByScenario.GetValueOrDefault(id),
                options))
            .ToArray();
        return new EcdCwrTraceabilityReplayVerificationReport(
            DateTimeOffset.Now,
            expectedByScenario.Count,
            replayedByScenario.Count,
            items.Count(item => item.Passed),
            items.Count(item => !item.Passed),
            items,
            options.ScoreTolerance,
            options.ImageQualityTolerance,
            SinglePolicyVersion(expectedByScenario.Values),
            SinglePolicyVersion(replayedByScenario.Values));
    }

    private static string? SinglePolicyVersion(IEnumerable<EcdCwrSimulationPrediction> predictions)
    {
        var versions = predictions
            .Select(prediction => prediction.DiagnosticPolicyVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }

    private static EcdCwrTraceabilityReplayVerificationItem VerifyItem(
        string scenarioId,
        EcdCwrSimulationPrediction? expected,
        EcdCwrSimulationPrediction? replayed,
        EcdCwrTraceabilityReplayVerificationOptions options)
    {
        var issues = new List<string>();
        if (expected is null)
        {
            issues.Add("missing expected prediction");
        }

        if (replayed is null)
        {
            issues.Add("missing replayed prediction");
        }

        if (expected is not null && replayed is not null)
        {
            if (string.IsNullOrWhiteSpace(expected.DiagnosticPolicyVersion))
            {
                issues.Add("diagnostic_policy_version missing in expected");
                return new EcdCwrTraceabilityReplayVerificationItem(
                    scenarioId,
                    Passed: false,
                    issues);
            }

            if (!string.Equals(
                expected.DiagnosticPolicyVersion,
                replayed.DiagnosticPolicyVersion,
                StringComparison.Ordinal))
            {
                issues.Add(
                    $"diagnostic_policy_version mismatch: expected={expected.DiagnosticPolicyVersion}, replayed={replayed.DiagnosticPolicyVersion ?? "missing"}");
                return new EcdCwrTraceabilityReplayVerificationItem(
                    scenarioId,
                    Passed: false,
                    issues);
            }

            CompareSequence("states", expected.States, replayed.States, issues);
            CompareSequence("fault_types", expected.FaultTypes, replayed.FaultTypes, issues);
            CompareDoubleSequence("scores", expected.Scores, replayed.Scores, options.ScoreTolerance, issues);
            CompareSequence("candidate_fault_types", expected.CandidateFaultTypes, replayed.CandidateFaultTypes, issues);
            CompareSequence("candidate_evidence_kinds", expected.CandidateEvidenceKinds, replayed.CandidateEvidenceKinds, issues);
            CompareSequence("candidate_reasons", expected.CandidateReasons, replayed.CandidateReasons, issues);
            CompareDoubleSequence(
                "candidate_scores",
                expected.CandidateScores,
                replayed.CandidateScores,
                options.ScoreTolerance,
                issues);
            CompareNullableDouble(
                "image_quality_score",
                expected.ImageQualityScore,
                replayed.ImageQualityScore,
                options.ImageQualityTolerance,
                issues);
            CompareNullableDouble(
                "contact_subspace_score",
                expected.ContactSubspaceScore,
                replayed.ContactSubspaceScore,
                options.ScoreTolerance,
                issues);
            CompareNullableDouble(
                "contact_subspace_discriminant_score",
                expected.ContactSubspaceDiscriminantScore,
                replayed.ContactSubspaceDiscriminantScore,
                options.ScoreTolerance,
                issues);
            if (expected.SystemLevel != replayed.SystemLevel)
            {
                issues.Add("system_level mismatch");
            }

            if (expected.PhysicalFieldGuardApplied != replayed.PhysicalFieldGuardApplied)
            {
                issues.Add("physical_field_guard_applied mismatch");
            }

            if (expected.MultiFrequencyPrimaryHz != replayed.MultiFrequencyPrimaryHz)
            {
                issues.Add("multi_frequency_primary_hz mismatch");
            }

            if (expected.MultiFrequencyPeerFrameCount != replayed.MultiFrequencyPeerFrameCount)
            {
                issues.Add("multi_frequency_peer_frame_count mismatch");
            }
        }

        return new EcdCwrTraceabilityReplayVerificationItem(
            scenarioId,
            issues.Count == 0,
            issues);
    }

    private static void CompareSequence<T>(
        string field,
        IReadOnlyList<T>? expected,
        IReadOnlyList<T>? actual,
        ICollection<string> issues)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                issues.Add($"{field} null mismatch");
            }

            return;
        }

        if (expected.Count != actual.Count)
        {
            issues.Add($"{field} length mismatch");
            return;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(expected[index], actual[index]))
            {
                issues.Add($"{field}[{index}] mismatch");
                return;
            }
        }
    }

    private static void CompareDoubleSequence(
        string field,
        IReadOnlyList<double>? expected,
        IReadOnlyList<double>? actual,
        double tolerance,
        ICollection<string> issues)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                issues.Add($"{field} null mismatch");
            }

            return;
        }

        if (expected.Count != actual.Count)
        {
            issues.Add($"{field} length mismatch");
            return;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            if (!NearlyEqual(expected[index], actual[index], tolerance))
            {
                issues.Add($"{field}[{index}] mismatch");
                return;
            }
        }
    }

    private static void CompareNullableDouble(
        string field,
        double? expected,
        double? actual,
        double tolerance,
        ICollection<string> issues)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                issues.Add($"{field} null mismatch");
            }

            return;
        }

        if (!NearlyEqual(expected.Value, actual.Value, tolerance))
        {
            issues.Add($"{field} mismatch");
        }
    }

    private static bool NearlyEqual(double expected, double actual, double tolerance)
    {
        if (double.IsNaN(expected) && double.IsNaN(actual))
        {
            return true;
        }

        if (!double.IsFinite(expected) || !double.IsFinite(actual))
        {
            return expected.Equals(actual);
        }

        return Math.Abs(expected - actual) <= tolerance;
    }
}

public sealed record EcdCwrTraceabilityReplayVerificationOptions(
    double ScoreTolerance = 1.0e-9,
    double ImageQualityTolerance = 1.0e-9);

public sealed record EcdCwrTraceabilityReplayVerificationReport(
    DateTimeOffset VerifiedAt,
    int ExpectedPredictionCount,
    int ReplayedPredictionCount,
    int PassedItems,
    int FailedItems,
    IReadOnlyList<EcdCwrTraceabilityReplayVerificationItem> Items,
    double ScoreTolerance,
    double ImageQualityTolerance,
    string? ExpectedDiagnosticPolicyVersion = null,
    string? ReplayedDiagnosticPolicyVersion = null)
{
    public bool Passed => FailedItems == 0 &&
        ExpectedPredictionCount > 0 &&
        ReplayedPredictionCount >= ExpectedPredictionCount &&
        !string.IsNullOrWhiteSpace(ExpectedDiagnosticPolicyVersion) &&
        string.Equals(
            ExpectedDiagnosticPolicyVersion,
            ReplayedDiagnosticPolicyVersion,
            StringComparison.Ordinal);
}

public sealed record EcdCwrTraceabilityReplayVerificationItem(
    string ScenarioId,
    bool Passed,
    IReadOnlyList<string> Issues);
