using System.Text;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrRong2026BaselineRunner
{
    public EcdCwrRong2026BaselineRunReport Run(
        EcdCwrSimulationBatchManifest manifest,
        EcdCwrRong2026BaselineRunOptions? options = null,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new EcdCwrRong2026BaselineRunOptions();
        var selected = Select(manifest.WorkItems, options).ToArray();
        var predictions = new List<EcdCwrSimulationPrediction>(selected.Length);
        var items = new List<EcdCwrRong2026BaselineRunItem>(selected.Length);
        var skippedMissing = 0;
        var failed = 0;
        var analyzer = new EcdCwrRong2026Baseline();
        for (var index = 0; index < selected.Length; index++)
        {
            var workItem = selected[index];
            if (!File.Exists(workItem.OutputHdf5Path))
            {
                skippedMissing++;
                items.Add(FailedItem(workItem, "missing simulation HDF5"));
                if (!options.SkipMissingResults)
                {
                    failed++;
                }

                progress?.Invoke(index + 1, selected.Length);
                continue;
            }

            try
            {
                var result = analyzer.Analyze(ReadInputFromHdf5(workItem.OutputHdf5Path), options.Algorithm);
                predictions.Add(ToPrediction(workItem.ScenarioId, result));
                items.Add(ToItem(workItem, result));
            }
            catch (Exception ex)
            {
                failed++;
                items.Add(FailedItem(workItem, ex.Message));
            }

            progress?.Invoke(index + 1, selected.Length);
        }

        var healthyItems = items.Where(item => item.Analyzed && !item.HasEffectiveContactFault).ToArray();
        var healthyBoundaryHigh = healthyItems
            .Where(item =>
                item.TargetPlacement == EcdCwrTargetPlacement.Boundary &&
                item.ConductivityPattern == EcdCwrConductivityPattern.High)
            .ToArray();
        var singleZc20 = items.Where(item =>
            item.Analyzed &&
            item.FaultMode == EcdCwrFaultMode.Single &&
            item.ContactImpedanceMultiplier >= 20.0).ToArray();
        var adjacentDual = items.Where(item =>
            item.Analyzed &&
            item.FaultMode == EcdCwrFaultMode.AdjacentDual &&
            item.HasEffectiveContactFault).ToArray();
        var fullCoverage = selected.Length == manifest.WorkItems.Count &&
            predictions.Select(item => item.ScenarioId).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
            manifest.WorkItems.Count &&
            skippedMissing == 0 &&
            failed == 0;
        var executionPassed = failed == 0;
        return new EcdCwrRong2026BaselineRunReport(
            EcdCwrRong2026Baseline.SchemaVersion,
            DateTimeOffset.Now,
            EcdCwrRong2026Baseline.SourceDoi,
            EcdCwrRong2026Baseline.CreatePolicyVersion(options.Algorithm),
            EcdCwrRong2026Baseline.Equation7Interpretation,
            EcdCwrRong2026Baseline.DescribeAssumptions(options.Algorithm),
            manifest.WorkItems.Count,
            selected.Length,
            predictions.Count,
            skippedMissing,
            failed,
            Rate(healthyItems.Count(item => item.DetectedElectrodes.Count > 0), healthyItems.Length),
            Rate(
                healthyBoundaryHigh.Count(item => item.DetectedElectrodes.Count > 0),
                healthyBoundaryHigh.Length),
            Rate(singleZc20.Count(item => item.Top1Correct), singleZc20.Length),
            Rate(adjacentDual.Count(item => item.AdjacentDualSeparated), adjacentDual.Length),
            fullCoverage,
            executionPassed,
            fullCoverage && executionPassed,
            predictions,
            items);
    }

    public static string ToMarkdown(EcdCwrRong2026BaselineRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine("# Rong 2026 Executable Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Generated at: {report.GeneratedAt:O}");
        builder.AppendLine($"- DOI: {report.SourceDoi}");
        builder.AppendLine($"- Policy: `{report.PolicyVersion}`");
        builder.AppendLine($"- Equation 7: {report.Equation7Interpretation}");
        builder.AppendLine($"- Coverage: {report.AnalyzedItems}/{report.ManifestWorkItemCount}");
        builder.AppendLine($"- Execution passed: {report.ExecutionPassed}");
        builder.AppendLine($"- Full coverage passed: {report.FullCoveragePassed}");
        builder.AppendLine($"- Healthy false positive: {FormatRate(report.HealthyFalsePositiveRate)}");
        builder.AppendLine($"- Healthy boundary-high false positive: {FormatRate(report.HealthyBoundaryHighFalsePositiveRate)}");
        builder.AppendLine($"- Single zc x20+ top-1: {FormatRate(report.SingleElectrodeTop1Accuracy)}");
        builder.AppendLine($"- Adjacent-dual separation: {FormatRate(report.AdjacentDualSeparationRate)}");
        builder.AppendLine();
        builder.AppendLine("## Operational Assumptions");
        builder.AppendLine();
        foreach (var assumption in report.OperationalAssumptions)
        {
            builder.AppendLine($"- {assumption}");
        }

        builder.AppendLine();
        builder.AppendLine("## Items With Issues");
        builder.AppendLine();
        builder.AppendLine("|scenario|truth|detected|top1|gap|threshold|issue|");
        builder.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var item in report.Items
            .Where(item => item.Issue is not null || item.FalsePositive ||
                item.HasEffectiveContactFault && !item.Top1Correct)
            .Take(300))
        {
            builder.AppendLine(
                $"|{item.ScenarioId}|{item.FaultMode}:{string.Join(',', item.ExpectedFaultElectrodes)}|{string.Join(',', item.DetectedElectrodes)}|{item.Top1Electrode}|{item.GapIndex}|{item.Threshold:G6}|{item.Issue}|");
        }

        return builder.ToString();
    }

    private static IEnumerable<EcdCwrSimulationWorkItem> Select(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        EcdCwrRong2026BaselineRunOptions options)
    {
        IEnumerable<EcdCwrSimulationWorkItem> query = workItems;
        if (options.ScenarioIds.Count > 0)
        {
            query = query.Where(item => options.ScenarioIds.Contains(item.ScenarioId));
        }

        if (options.StartIndex > 0)
        {
            query = query.Skip(options.StartIndex);
        }

        if (options.Limit is > 0)
        {
            query = query.Take(options.Limit.Value);
        }

        return query;
    }

    internal static EcdCwrRong2026Input ReadInputFromHdf5(string path)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(path);
        var reference = ReadComplexAwareVector(file, "/reference_retained_complex_208");
        var target = ReadComplexAwareVector(file, "/retained_complex_208");
        return new EcdCwrRong2026Input(
            reference.Real,
            reference.Amplitude,
            target.Real,
            target.Amplitude);
    }

    private static ComplexAwareVector ReadComplexAwareVector(IH5Group file, string path)
    {
        if (!file.LinkExists(path))
        {
            throw new InvalidDataException($"Missing dataset {path}.");
        }

        var dataset = file.Dataset(path);
        var dimensions = dataset.Space.Dimensions;
        double[] real;
        double[] amplitude;
        if (TryRead(() => dataset.Read<Hdf5Complex128[]>(memoryDims: dimensions), out var complex128))
        {
            real = complex128.Select(value => value.Real).ToArray();
            amplitude = complex128
                .Select(value => Math.Sqrt((value.Real * value.Real) + (value.Imaginary * value.Imaginary)))
                .ToArray();
        }
        else if (TryRead(() => dataset.Read<Hdf5Complex64[]>(memoryDims: dimensions), out var complex64))
        {
            real = complex64.Select(value => (double)value.Real).ToArray();
            amplitude = complex64
                .Select(value => Math.Sqrt(
                    ((double)value.Real * value.Real) +
                    ((double)value.Imaginary * value.Imaginary)))
                .ToArray();
        }
        else if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubles))
        {
            real = doubles;
            amplitude = doubles.Select(Math.Abs).ToArray();
        }
        else if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var singles))
        {
            real = singles.Select(value => (double)value).ToArray();
            amplitude = singles.Select(value => Math.Abs((double)value)).ToArray();
        }
        else
        {
            throw new InvalidDataException($"Unsupported vector dataset type at {path}.");
        }

        if (real.Length != EcdCwrRong2026Baseline.MeasurementCount ||
            amplitude.Length != EcdCwrRong2026Baseline.MeasurementCount ||
            real.Any(value => !double.IsFinite(value)) ||
            amplitude.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException($"{path} is not a finite 208-point vector.");
        }

        return new ComplexAwareVector(real, amplitude);
    }

    private static EcdCwrSimulationPrediction ToPrediction(
        string scenarioId,
        EcdCwrRong2026Result result)
    {
        var detected = result.DetectedElectrodes.ToHashSet();
        var states = Enumerable.Range(0, EcdCwrRong2026Baseline.ElectrodeCount)
            .Select(electrode => detected.Contains(electrode)
                ? ElectrodeContactState.Red
                : ElectrodeContactState.Green)
            .ToArray();
        var faultTypes = Enumerable.Range(0, EcdCwrRong2026Baseline.ElectrodeCount)
            .Select(electrode => detected.Contains(electrode)
                ? ElectrodeFaultType.ElectrodeContact
                : ElectrodeFaultType.None)
            .ToArray();
        var reasons = Enumerable.Range(0, EcdCwrRong2026Baseline.ElectrodeCount)
            .Select(electrode => detected.Contains(electrode)
                ? $"Rong2026 score={result.ElectrodeScores16[electrode]:G6} threshold={result.Threshold:G6}"
                : string.Empty)
            .ToArray();
        return new EcdCwrSimulationPrediction(
            scenarioId,
            states,
            faultTypes,
            result.ElectrodeScores16,
            DiagnosticPolicyVersion: result.PolicyVersion,
            CandidateScores: result.ElectrodeScores16,
            CandidateFaultTypes: faultTypes,
            CandidateReasons: reasons);
    }

    private static EcdCwrRong2026BaselineRunItem ToItem(
        EcdCwrSimulationWorkItem workItem,
        EcdCwrRong2026Result result)
    {
        var scenario = workItem.Scenario;
        var effective = HasEffectiveContactFault(scenario);
        var top = UniqueTop(result.ElectrodeScores16, 1);
        var top1 = top.Length == 1 ? top[0] : -1;
        var expected = scenario.FaultElectrodes.ToArray();
        var adjacentSeparated = effective &&
            scenario.FaultMode == EcdCwrFaultMode.AdjacentDual &&
            expected.All(result.DetectedElectrodes.Contains);
        return new EcdCwrRong2026BaselineRunItem(
            scenario.ScenarioId,
            true,
            scenario.FaultMode,
            scenario.TargetPlacement,
            scenario.ConductivityPattern,
            scenario.ContactImpedance.Multiplier,
            expected,
            effective,
            result.DetectedElectrodes,
            result.ElectrodeScores16,
            top1,
            effective && scenario.FaultMode == EcdCwrFaultMode.Single &&
                expected.Length == 1 && top1 == expected[0],
            adjacentSeparated,
            !effective && result.DetectedElectrodes.Count > 0,
            result.GapIndex,
            result.Threshold,
            result.L1Penalty,
            result.ResidualRms,
            result.ReciprocityError208.Max(),
            result.CurvatureError208.Max(),
            result.AffectedMeasurementCount,
            result.ValidTemplateRowCount,
            null);
    }

    private static EcdCwrRong2026BaselineRunItem FailedItem(
        EcdCwrSimulationWorkItem workItem,
        string issue)
    {
        var scenario = workItem.Scenario;
        return new EcdCwrRong2026BaselineRunItem(
            scenario.ScenarioId,
            false,
            scenario.FaultMode,
            scenario.TargetPlacement,
            scenario.ConductivityPattern,
            scenario.ContactImpedance.Multiplier,
            scenario.FaultElectrodes.ToArray(),
            HasEffectiveContactFault(scenario),
            [],
            [],
            -1,
            false,
            false,
            false,
            0,
            0.0,
            0.0,
            double.NaN,
            double.NaN,
            double.NaN,
            0,
            0,
            issue);
    }

    private static int[] UniqueTop(IReadOnlyList<double> scores, int count)
    {
        var ordered = scores
            .Select((score, electrode) => new { score, electrode })
            .Where(item => double.IsFinite(item.score) && item.score > 0.0)
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.electrode)
            .ToArray();
        if (ordered.Length < count)
        {
            return ordered.Select(item => item.electrode).ToArray();
        }

        if (ordered.Skip(count).Any(item => NearlyEqual(item.score, ordered[count - 1].score)))
        {
            return [];
        }

        return ordered.Take(count).Select(item => item.electrode).ToArray();
    }

    private static bool NearlyEqual(double left, double right)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1.0e-9 * scale;
    }

    private static bool HasEffectiveContactFault(EcdCwrSimulationScenario scenario)
    {
        return scenario.FaultMode != EcdCwrFaultMode.None &&
            (double.IsPositiveInfinity(scenario.ContactImpedance.Multiplier) ||
                scenario.ContactImpedance.Multiplier > 1.0);
    }

    private static double Rate(int numerator, int denominator)
    {
        return denominator == 0 ? double.NaN : (double)numerator / denominator;
    }

    private static string FormatRate(double value)
    {
        return double.IsFinite(value) ? value.ToString("P4") : "n/a";
    }

    private static bool TryRead<T>(Func<T> read, out T value)
    {
        try
        {
            value = read();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

#pragma warning disable CS0649
    private struct Hdf5Complex64
    {
        [H5Name("r")]
        public float Real;

        [H5Name("i")]
        public float Imaginary;
    }

    private struct Hdf5Complex128
    {
        [H5Name("r")]
        public double Real;

        [H5Name("i")]
        public double Imaginary;
    }
#pragma warning restore CS0649

    private sealed record ComplexAwareVector(double[] Real, double[] Amplitude);
}

public sealed record EcdCwrRong2026BaselineRunOptions
{
    public int StartIndex { get; init; }

    public int? Limit { get; init; }

    public IReadOnlySet<string> ScenarioIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool SkipMissingResults { get; init; }

    public EcdCwrRong2026Options Algorithm { get; init; } = new();
}

public sealed record EcdCwrRong2026BaselineRunReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string SourceDoi,
    string PolicyVersion,
    string Equation7Interpretation,
    IReadOnlyList<string> OperationalAssumptions,
    int ManifestWorkItemCount,
    int SelectedItems,
    int AnalyzedItems,
    int SkippedMissing,
    int FailedItems,
    double HealthyFalsePositiveRate,
    double HealthyBoundaryHighFalsePositiveRate,
    double SingleElectrodeTop1Accuracy,
    double AdjacentDualSeparationRate,
    bool FullCoverage,
    bool ExecutionPassed,
    bool FullCoveragePassed,
    IReadOnlyList<EcdCwrSimulationPrediction> Predictions,
    IReadOnlyList<EcdCwrRong2026BaselineRunItem> Items);

public sealed record EcdCwrRong2026BaselineRunItem(
    string ScenarioId,
    bool Analyzed,
    EcdCwrFaultMode FaultMode,
    EcdCwrTargetPlacement TargetPlacement,
    EcdCwrConductivityPattern ConductivityPattern,
    double ContactImpedanceMultiplier,
    IReadOnlyList<int> ExpectedFaultElectrodes,
    bool HasEffectiveContactFault,
    IReadOnlyList<int> DetectedElectrodes,
    IReadOnlyList<double> ElectrodeScores16,
    int Top1Electrode,
    bool Top1Correct,
    bool AdjacentDualSeparated,
    bool FalsePositive,
    int GapIndex,
    double Threshold,
    double L1Penalty,
    double ResidualRms,
    double MaxReciprocityError,
    double MaxCurvatureError,
    int AffectedMeasurementCount,
    int ValidTemplateRowCount,
    string? Issue);
