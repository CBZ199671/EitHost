using System.Text.Json;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationDatasetValidator
{
    public EcdCwrSimulationDatasetValidationReport Validate(EcdCwrSimulationBatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var items = manifest.WorkItems
            .Select(item => ValidateWorkItem(item, manifest.EmitContactJacobian, manifest.EmitMultiFrequency))
            .ToArray();
        return new EcdCwrSimulationDatasetValidationReport(
            EcdCwrSimulationScenarioPlan.BatchSchemaVersion,
            DateTimeOffset.Now,
            manifest.ScenarioCount,
            manifest.WorkItems.Count,
            items.Count(item => item.Passed),
            items.Count(item => !File.Exists(item.OutputHdf5Path)),
            items.Count(item => !File.Exists(item.LabelJsonPath)),
            items);
    }

    public static string ToMarkdown(EcdCwrSimulationDatasetValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR CEM Dataset Validation",
            "",
            $"- Validated at: {report.ValidatedAt:O}",
            $"- Manifest scenario count: {report.ManifestScenarioCount}",
            $"- Work items: {report.WorkItemCount}",
            $"- Passed: {report.PassedItems}",
            $"- Missing HDF5: {report.MissingHdf5}",
            $"- Missing label: {report.MissingLabel}",
            $"- Failed: {report.FailedItems}",
            "",
            "## Failed Items",
            "",
            "|scenario|issues|",
            "|---|---|"
        };
        foreach (var item in report.Items.Where(item => !item.Passed))
        {
            lines.Add($"|{item.ScenarioId}|{string.Join("<br>", item.Issues)}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static EcdCwrSimulationDatasetValidationItem ValidateWorkItem(
        EcdCwrSimulationWorkItem item,
        bool requireContactJacobian,
        bool requireMultiFrequency = false)
    {
        var issues = new List<string>();
        if (!File.Exists(item.OutputHdf5Path))
        {
            issues.Add("missing result HDF5");
        }
        else
        {
            ValidateHdf5(item.OutputHdf5Path, requireContactJacobian, requireMultiFrequency, issues);
        }

        if (!File.Exists(item.LabelJsonPath))
        {
            issues.Add("missing label JSON");
        }
        else
        {
            ValidateLabel(item.LabelJsonPath, item, issues);
        }

        return new EcdCwrSimulationDatasetValidationItem(
            item.ScenarioId,
            item.OutputHdf5Path,
            item.LabelJsonPath,
            issues);
    }

    private static void ValidateHdf5(
        string path,
        bool requireContactJacobian,
        bool requireMultiFrequency,
        List<string> issues)
    {
        try
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(path);
            ExpectDimensions(file, "/raw_complex_256", [16UL, 16UL], issues);
            ExpectDimensions(file, "/reference_complex_256", [16UL, 16UL], issues);
            ExpectDimensions(file, "/retained_complex_208", [208UL], issues);
            ExpectDimensions(file, "/reference_retained_complex_208", [208UL], issues);
            ExpectDimensions(file, "/retained_indices_208", [208UL], issues);
            ExpectDimensions(file, "/contact_impedance", [16UL], issues);
            if (file.LinkExists("/contact_jacobian_208x16"))
            {
                ExpectDimensions(file, "/contact_jacobian_208x16", [208UL, 16UL], issues);
            }
            else if (requireContactJacobian)
            {
                issues.Add("missing dataset /contact_jacobian_208x16");
            }

            ValidateMultiFrequencyDatasets(file, requireMultiFrequency, issues);

            ExpectRank(file, "/ground_truth_conductivity", 1, issues, requireNonEmpty: true);
            ExpectRank(file, "/node_coords", 2, issues, requireNonEmpty: true);
            ExpectRank(file, "/cell_connectivity", 2, issues, requireNonEmpty: true);
        }
        catch (Exception ex)
        {
            issues.Add($"invalid result HDF5: {ex.Message}");
        }
    }

    private static void ValidateMultiFrequencyDatasets(
        IH5Group file,
        bool requireMultiFrequency,
        List<string> issues)
    {
        if (!file.LinkExists("/frequency_hz"))
        {
            if (requireMultiFrequency)
            {
                issues.Add("missing dataset /frequency_hz");
            }

            return;
        }

        var frequencyShape = file.Dataset("/frequency_hz").Space.Dimensions.ToArray();
        if (frequencyShape.Length != 1 || frequencyShape[0] == 0)
        {
            issues.Add($"/frequency_hz shape {FormatShape(frequencyShape)} must be non-empty rank-1");
            return;
        }

        var frequencyCount = frequencyShape[0];
        ExpectDimensions(file, "/frequency_raw_complex_256", [frequencyCount, 16UL, 16UL], issues);
        ExpectDimensions(file, "/frequency_reference_complex_256", [frequencyCount, 16UL, 16UL], issues);
        ExpectDimensions(file, "/frequency_retained_complex_208", [frequencyCount, 208UL], issues);
        ExpectDimensions(file, "/frequency_reference_retained_complex_208", [frequencyCount, 208UL], issues);
        ExpectDimensions(file, "/frequency_contact_impedance_multipliers", [frequencyCount], issues);
        ExpectDimensions(file, "/frequency_contact_impedance_16", [frequencyCount, 16UL], issues);
        ExpectDimensions(file, "/frequency_reference_contact_impedance_16", [frequencyCount, 16UL], issues);
    }

    private static void ExpectDimensions(
        IH5Group file,
        string datasetPath,
        IReadOnlyList<ulong> expected,
        List<string> issues)
    {
        if (!file.LinkExists(datasetPath))
        {
            issues.Add($"missing dataset {datasetPath}");
            return;
        }

        var actual = file.Dataset(datasetPath).Space.Dimensions.ToArray();
        if (!actual.SequenceEqual(expected))
        {
            issues.Add($"{datasetPath} shape {FormatShape(actual)} != {FormatShape(expected)}");
        }
    }

    private static void ExpectRank(
        IH5Group file,
        string datasetPath,
        int expectedRank,
        List<string> issues,
        bool requireNonEmpty = false)
    {
        if (!file.LinkExists(datasetPath))
        {
            issues.Add($"missing dataset {datasetPath}");
            return;
        }

        var actual = file.Dataset(datasetPath).Space.Dimensions.ToArray();
        if (actual.Length != expectedRank)
        {
            issues.Add($"{datasetPath} rank {actual.Length} != {expectedRank}");
        }

        if (requireNonEmpty && actual.Any(dimension => dimension == 0))
        {
            issues.Add($"{datasetPath} must be non-empty");
        }
    }

    private static void ValidateLabel(
        string path,
        EcdCwrSimulationWorkItem item,
        List<string> issues)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema_version", out var schema)
                || !string.Equals(schema.GetString(), "ecd-cwr-cem-simulation-label-v1", StringComparison.Ordinal))
            {
                issues.Add("label schema_version mismatch");
            }

            if (!root.TryGetProperty("scenario_id", out var scenarioId)
                || !string.Equals(scenarioId.GetString(), item.ScenarioId, StringComparison.Ordinal))
            {
                issues.Add("label scenario_id mismatch");
            }

            if (!root.TryGetProperty("fault_electrodes", out var faultElectrodes)
                || faultElectrodes.ValueKind != JsonValueKind.Array)
            {
                issues.Add("label fault_electrodes missing");
            }

            if (!root.TryGetProperty("contact_impedance", out var contactImpedance)
                || contactImpedance.ValueKind != JsonValueKind.Array
                || contactImpedance.GetArrayLength() != 16)
            {
                issues.Add("label contact_impedance length mismatch");
            }
        }
        catch (Exception ex)
        {
            issues.Add($"invalid label JSON: {ex.Message}");
        }
    }

    private static string FormatShape(IReadOnlyList<ulong> shape)
    {
        return "(" + string.Join(",", shape) + ")";
    }
}

public sealed record EcdCwrSimulationDatasetValidationReport(
    string SchemaVersion,
    DateTimeOffset ValidatedAt,
    int ManifestScenarioCount,
    int WorkItemCount,
    int PassedItems,
    int MissingHdf5,
    int MissingLabel,
    IReadOnlyList<EcdCwrSimulationDatasetValidationItem> Items)
{
    public int FailedItems => WorkItemCount - PassedItems;

    public bool Passed => FailedItems == 0 && WorkItemCount == ManifestScenarioCount;
}

public sealed record EcdCwrSimulationDatasetValidationItem(
    string ScenarioId,
    string OutputHdf5Path,
    string LabelJsonPath,
    IReadOnlyList<string> Issues)
{
    public bool Passed => Issues.Count == 0;
}
