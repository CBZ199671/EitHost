using System.Globalization;
using System.Text;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationScenarioPlan
{
    public const string BatchSchemaVersion = "ecd-cwr-simulation-batch-v1";
    public const string BackendRequestSchemaVersion = "ecd-cwr-cem-backend-request-v1";
    public const string BackendCommand = "ecd_cwr_simulate_cem";
    public const string OutputLayout = "row_major_16x16_256_complex";

    public static readonly double[] DefaultMultiFrequencyHz = [10_000.0, 5_000.0, 50_000.0];

    public static readonly double[] DefaultFrequencyContactImpedanceMultipliers = [1.0, 1.35, 0.55];

    public static readonly int[] TargetCounts = [0, 1, 2, 3, 4];

    public static readonly EcdCwrTargetPlacement[] TargetPlacements =
    [
        EcdCwrTargetPlacement.Center,
        EcdCwrTargetPlacement.Boundary,
        EcdCwrTargetPlacement.Random
    ];

    public static readonly EcdCwrConductivityPattern[] ConductivityPatterns =
    [
        EcdCwrConductivityPattern.High,
        EcdCwrConductivityPattern.Low,
        EcdCwrConductivityPattern.Mixed
    ];

    public static readonly EcdCwrContactImpedanceCase[] ContactImpedanceCases =
    [
        new("zc_x1", 1.0),
        new("zc_x5", 5.0),
        new("zc_x20", 20.0),
        new("zc_x50", 50.0),
        new("zc_open", double.PositiveInfinity)
    ];

    public static readonly EcdCwrNoiseCase[] NoiseCases =
    [
        new("noise_inf", double.PositiveInfinity),
        new("noise_40db", 40.0),
        new("noise_30db", 30.0),
        new("noise_20db", 20.0)
    ];

    public static readonly EcdCwrFaultMode[] FaultModes =
    [
        EcdCwrFaultMode.None,
        EcdCwrFaultMode.Single,
        EcdCwrFaultMode.AdjacentDual,
        EcdCwrFaultMode.RemoteDual,
        EcdCwrFaultMode.Triple,
        EcdCwrFaultMode.Global
    ];

    public IReadOnlyList<EcdCwrSimulationScenario> CreateFullFactorial()
    {
        var scenarios = new List<EcdCwrSimulationScenario>();
        var index = 0;
        foreach (var targetCount in TargetCounts)
            foreach (var placement in TargetPlacements)
                foreach (var conductivity in ConductivityPatterns)
                    foreach (var zc in ContactImpedanceCases)
                        foreach (var noise in NoiseCases)
                            foreach (var faultMode in FaultModes)
                            {
                                scenarios.Add(new EcdCwrSimulationScenario(
                                    $"ecd-cwr-sim-{index:000000}",
                                    "cem_per_electrode_zc",
                                    targetCount,
                                    placement,
                                    conductivity,
                                    zc,
                                    noise,
                                    faultMode,
                                    FaultElectrodesFor(faultMode)));
                                index++;
                            }

        return scenarios;
    }

    public string ToCsv(IReadOnlyList<EcdCwrSimulationScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var builder = new StringBuilder();
        builder.AppendLine("scenario_id,model_kind,target_count,target_placement,conductivity_pattern,zc_case,zc_multiplier,noise_case,noise_snr_db,fault_mode,fault_electrodes");
        foreach (var scenario in scenarios)
        {
            builder.Append(scenario.ScenarioId).Append(',')
                .Append(scenario.ModelKind).Append(',')
                .Append(scenario.TargetCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(scenario.TargetPlacement).Append(',')
                .Append(scenario.ConductivityPattern).Append(',')
                .Append(scenario.ContactImpedance.Label).Append(',')
                .Append(FormatNumber(scenario.ContactImpedance.Multiplier)).Append(',')
                .Append(scenario.Noise.Label).Append(',')
                .Append(FormatNumber(scenario.Noise.SnrDb)).Append(',')
                .Append(scenario.FaultMode).Append(',')
                .Append(string.Join(";", scenario.FaultElectrodes))
                .AppendLine();
        }

        return builder.ToString();
    }

    public EcdCwrSimulationBatchManifest CreateBatchManifest(
        IReadOnlyList<EcdCwrSimulationScenario> scenarios,
        string outputDirectory,
        string? scenarioCsvPath = null,
        DateTimeOffset? createdAt = null,
        bool emitContactJacobian = false,
        bool emitMultiFrequency = false)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        var requestDirectory = Path.Combine(fullOutputDirectory, "requests");
        var resultDirectory = Path.Combine(fullOutputDirectory, "results");
        var labelDirectory = Path.Combine(fullOutputDirectory, "labels");
        var workItems = scenarios
            .Select(scenario => new EcdCwrSimulationWorkItem(
                scenario.ScenarioId,
                BackendCommand,
                scenario,
                Path.Combine(requestDirectory, $"{scenario.ScenarioId}.request.json"),
                Path.Combine(resultDirectory, $"{scenario.ScenarioId}.h5"),
                Path.Combine(labelDirectory, $"{scenario.ScenarioId}.label.json"),
                OutputLayout))
            .ToArray();

        return new EcdCwrSimulationBatchManifest(
            BatchSchemaVersion,
            createdAt ?? DateTimeOffset.Now,
            "cem_per_electrode_zc",
            scenarios.Count,
            string.IsNullOrWhiteSpace(scenarioCsvPath) ? null : Path.GetFullPath(scenarioCsvPath),
            fullOutputDirectory,
            requestDirectory,
            resultDirectory,
            labelDirectory,
            workItems,
            emitContactJacobian,
            emitMultiFrequency);
    }

    public EcdCwrSimulationBackendRequest CreateBackendRequest(
        EcdCwrSimulationWorkItem workItem,
        bool emitContactJacobian = false,
        bool emitMultiFrequency = false)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        return new EcdCwrSimulationBackendRequest(
            BackendRequestSchemaVersion,
            workItem.Command,
            workItem.ScenarioId,
            new EcdCwrSimulationModelContract(
                "cem",
                "per_electrode_zc",
                16,
                "{ad}",
                "{ad}",
                RotateMeasurements: true,
                UseMeasurementCurrent: true,
                OutputLayout,
                EmitContactJacobian: emitContactJacobian,
                FrequenciesHz: emitMultiFrequency ? DefaultMultiFrequencyHz : null,
                FrequencyContactImpedanceMultipliers: emitMultiFrequency ? DefaultFrequencyContactImpedanceMultipliers : null),
            workItem.Scenario,
            new EcdCwrSimulationOutputContract(
                workItem.OutputHdf5Path,
                workItem.LabelJsonPath,
                OutputLayout,
                FullObservationCount: 256,
                RetainedObservationCount: 208));
    }

    private static int[] FaultElectrodesFor(EcdCwrFaultMode faultMode)
    {
        return faultMode switch
        {
            EcdCwrFaultMode.None => [],
            EcdCwrFaultMode.Single => [0],
            EcdCwrFaultMode.AdjacentDual => [0, 1],
            EcdCwrFaultMode.RemoteDual => [0, 8],
            EcdCwrFaultMode.Triple => [0, 5, 10],
            EcdCwrFaultMode.Global => Enumerable.Range(0, 16).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(faultMode), faultMode, "Unsupported ECD-CWR fault mode.")
        };
    }

    private static string FormatNumber(double value)
    {
        return double.IsPositiveInfinity(value)
            ? "inf"
            : value.ToString("G17", CultureInfo.InvariantCulture);
    }
}

public sealed record EcdCwrSimulationScenario(
    string ScenarioId,
    string ModelKind,
    int TargetCount,
    EcdCwrTargetPlacement TargetPlacement,
    EcdCwrConductivityPattern ConductivityPattern,
    EcdCwrContactImpedanceCase ContactImpedance,
    EcdCwrNoiseCase Noise,
    EcdCwrFaultMode FaultMode,
    IReadOnlyList<int> FaultElectrodes);

public sealed record EcdCwrSimulationBatchManifest(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    string ModelKind,
    int ScenarioCount,
    string? ScenarioCsvPath,
    string OutputDirectory,
    string RequestDirectory,
    string ResultDirectory,
    string LabelDirectory,
    IReadOnlyList<EcdCwrSimulationWorkItem> WorkItems,
    bool EmitContactJacobian = false,
    bool EmitMultiFrequency = false);

public sealed record EcdCwrSimulationWorkItem(
    string ScenarioId,
    string Command,
    EcdCwrSimulationScenario Scenario,
    string RequestJsonPath,
    string OutputHdf5Path,
    string LabelJsonPath,
    string OutputLayout);

public sealed record EcdCwrSimulationBackendRequest(
    string SchemaVersion,
    string Command,
    string ScenarioId,
    EcdCwrSimulationModelContract Model,
    EcdCwrSimulationScenario Scenario,
    EcdCwrSimulationOutputContract Output);

public sealed record EcdCwrSimulationModelContract(
    string ElectrodeModel,
    string ContactImpedanceMode,
    int ElectrodeCount,
    string StimPattern,
    string MeasurementPattern,
    bool RotateMeasurements,
    bool UseMeasurementCurrent,
    string OutputLayout,
    bool EmitContactJacobian = false,
    IReadOnlyList<double>? FrequenciesHz = null,
    IReadOnlyList<double>? FrequencyContactImpedanceMultipliers = null);

public sealed record EcdCwrSimulationOutputContract(
    string Hdf5Path,
    string LabelJsonPath,
    string Layout,
    int FullObservationCount,
    int RetainedObservationCount);

public sealed record EcdCwrContactImpedanceCase(string Label, double Multiplier);

public sealed record EcdCwrNoiseCase(string Label, double SnrDb);

public enum EcdCwrTargetPlacement
{
    Center = 0,
    Boundary = 1,
    Random = 2
}

public enum EcdCwrConductivityPattern
{
    High = 0,
    Low = 1,
    Mixed = 2
}

public enum EcdCwrFaultMode
{
    None = 0,
    Single = 1,
    AdjacentDual = 2,
    RemoteDual = 3,
    Triple = 4,
    Global = 5
}
