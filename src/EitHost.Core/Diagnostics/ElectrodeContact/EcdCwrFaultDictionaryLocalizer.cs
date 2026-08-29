namespace EitHost.Core.Diagnostics.ElectrodeContact;

public static class EcdCwrFaultDictionary
{
    public const int ElectrodeCount = 16;
    public const int ColumnCount = ElectrodeCount * 4;

    public static int DriveColumn(int electrode) => ValidateElectrode(electrode);

    public static int MeasureColumn(int electrode) => ElectrodeCount + ValidateElectrode(electrode);

    public static int PairLinkColumn(int stimulation) => (2 * ElectrodeCount) + ValidateElectrode(stimulation);

    public static int MeasurementChannelColumn(int channel) => (3 * ElectrodeCount) + ValidateElectrode(channel);

    private static int ValidateElectrode(int index)
    {
        if (index < 0 || index >= ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Fault dictionary index must be within 0..15.");
        }

        return index;
    }
}

public sealed class EcdCwrFaultDictionaryBuilder
{
    private const int ElectrodeCount = EcdCwrFaultDictionary.ElectrodeCount;

    public IReadOnlyList<EcdCwrFaultDictionaryObservation> Build(EcdCwrFaultDictionaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var observations = new List<EcdCwrFaultDictionaryObservation>();
        if (input.EvidenceA is not null)
        {
            foreach (var point in input.EvidenceA.PointScores.Where(point => !point.Saturated))
            {
                observations.Add(CreateObservation(
                    EcdCwrFaultDictionaryObservationKind.Contact48,
                    point.StimulationIndex,
                    point.RelativeChannelIndex,
                    point.Score,
                    input.Contact48Weight));
            }
        }

        if (input.ReciprocityScores208 is { } reciprocity)
        {
            Validate208(reciprocity, nameof(input.ReciprocityScores208));
            Add208Observations(observations, EcdCwrFaultDictionaryObservationKind.Reciprocity208, reciprocity, input.ReciprocityWeight);
        }

        if (input.ShapeScores208 is { } shape)
        {
            Validate208(shape, nameof(input.ShapeScores208));
            Add208Observations(observations, EcdCwrFaultDictionaryObservationKind.Shape208, shape, input.ShapeWeight);
        }

        if (input.TopologyScores16 is { } topology)
        {
            Validate16(topology, nameof(input.TopologyScores16));
            for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
            {
                var value = Sanitize(topology[stimulation]);
                if (value <= 0.0)
                {
                    continue;
                }

                observations.Add(new EcdCwrFaultDictionaryObservation(
                    EcdCwrFaultDictionaryObservationKind.Topology16,
                    stimulation,
                    null,
                    value,
                    input.TopologyWeight,
                    [
                        EcdCwrFaultDictionary.DriveColumn(stimulation),
                        EcdCwrFaultDictionary.DriveColumn(Mod(stimulation + 1)),
                        EcdCwrFaultDictionary.PairLinkColumn(stimulation)
                    ]));
            }
        }

        return observations;
    }

    public static EcdCwrFaultDictionaryObservation CreateObservation(
        EcdCwrFaultDictionaryObservationKind kind,
        int stimulation,
        int relativeChannel,
        double value,
        double weight = 1.0)
    {
        if (relativeChannel < 0 || relativeChannel >= ElectrodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeChannel), "Relative channel index must be within 0..15.");
        }

        var measurementChannel = Mod(stimulation + relativeChannel);
        var measureLeft = measurementChannel;
        var measureRight = Mod(measurementChannel + 1);
        return new EcdCwrFaultDictionaryObservation(
            kind,
            stimulation,
            relativeChannel,
            Sanitize(value),
            Sanitize(weight),
            [
                EcdCwrFaultDictionary.DriveColumn(stimulation),
                EcdCwrFaultDictionary.DriveColumn(Mod(stimulation + 1)),
                EcdCwrFaultDictionary.MeasureColumn(measureLeft),
                EcdCwrFaultDictionary.MeasureColumn(measureRight),
                EcdCwrFaultDictionary.PairLinkColumn(stimulation),
                EcdCwrFaultDictionary.MeasurementChannelColumn(measurementChannel)
            ]);
    }

    private static void Add208Observations(
        ICollection<EcdCwrFaultDictionaryObservation> observations,
        EcdCwrFaultDictionaryObservationKind kind,
        IReadOnlyList<double> scores,
        double weight)
    {
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            for (var column = 0; column < 13; column++)
            {
                var value = Sanitize(scores[(stimulation * 13) + column]);
                if (value <= 0.0)
                {
                    continue;
                }

                observations.Add(CreateObservation(kind, stimulation, column + 2, value, weight));
            }
        }
    }

    private static void Validate208(IReadOnlyList<double> values, string name)
    {
        if (values.Count != 208)
        {
            throw new ArgumentException("Fault dictionary 208-point evidence must contain 208 values.", name);
        }
    }

    private static void Validate16(IReadOnlyList<double> values, string name)
    {
        if (values.Count != ElectrodeCount)
        {
            throw new ArgumentException("Fault dictionary topology evidence must contain 16 values.", name);
        }
    }

    private static double Sanitize(double value)
    {
        return double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed class EcdCwrFaultDictionaryLocalizer
{
    private const int ElectrodeCount = EcdCwrFaultDictionary.ElectrodeCount;
    private const int ColumnCount = EcdCwrFaultDictionary.ColumnCount;

    public EcdCwrFaultLocalizationResult Localize(
        EcdCwrFaultDictionaryInput input,
        EcdCwrFaultDictionaryLocalizerOptions? options = null)
    {
        return Localize(new EcdCwrFaultDictionaryBuilder().Build(input), options);
    }

    public EcdCwrFaultLocalizationResult Localize(
        IReadOnlyList<EcdCwrFaultDictionaryObservation> observations,
        EcdCwrFaultDictionaryLocalizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        options ??= new EcdCwrFaultDictionaryLocalizerOptions();
        var coefficients = SolveSparseGroupLasso(observations, options);
        var drive = Slice(coefficients, 0);
        var measure = Slice(coefficients, ElectrodeCount);
        var pairLink = Slice(coefficients, 2 * ElectrodeCount);
        var measurementChannel = Slice(coefficients, 3 * ElectrodeCount);
        var electrodeScores = Enumerable.Range(0, ElectrodeCount)
            .Select(index => Math.Max(drive[index], measure[index]))
            .ToArray();
        var faultTypes = InterpretElectrodeFaultTypes(drive, measure, options);
        var confidence = CalculateConfidence(electrodeScores, options);
        var reasons = BuildReasons(drive, measure, faultTypes);

        return new EcdCwrFaultLocalizationResult(
            drive,
            measure,
            pairLink,
            measurementChannel,
            electrodeScores,
            confidence,
            faultTypes,
            reasons,
            InterpretPairLinks(pairLink, drive, measure, options),
            InterpretMeasurementChannels(measurementChannel, measure, options),
            CalculateResidualRms(observations, coefficients),
            observations.Count);
    }

    private static double[] SolveSparseGroupLasso(
        IReadOnlyList<EcdCwrFaultDictionaryObservation> observations,
        EcdCwrFaultDictionaryLocalizerOptions options)
    {
        var x = new double[ColumnCount];
        if (observations.Count == 0)
        {
            return x;
        }

        var lipschitz = EstimateLipschitz(observations);
        var step = 1.0 / Math.Max(lipschitz, 1e-9);
        var y = new double[ColumnCount];
        var previous = new double[ColumnCount];
        var t = 1.0;
        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            var gradient = CalculateGradient(observations, y);
            var next = new double[ColumnCount];
            for (var column = 0; column < ColumnCount; column++)
            {
                next[column] = Math.Max(0.0, y[column] - (step * gradient[column]) - (step * options.L1Penalty));
            }

            ApplyDriveMeasureGroupShrink(next, step * options.GroupPenalty);
            var maxChange = next.Zip(x, (left, right) => Math.Abs(left - right)).Max();
            if (maxChange <= options.ConvergenceTolerance)
            {
                return next;
            }

            Array.Copy(x, previous, ColumnCount);
            Array.Copy(next, x, ColumnCount);
            var nextT = 0.5 * (1.0 + Math.Sqrt(1.0 + (4.0 * t * t)));
            var momentum = (t - 1.0) / nextT;
            for (var column = 0; column < ColumnCount; column++)
            {
                y[column] = x[column] + (momentum * (x[column] - previous[column]));
            }

            t = nextT;
        }

        return x;
    }

    private static double EstimateLipschitz(IReadOnlyList<EcdCwrFaultDictionaryObservation> observations)
    {
        var columnSums = new double[ColumnCount];
        foreach (var observation in observations)
        {
            var weightSquare = observation.Weight * observation.Weight;
            foreach (var column in observation.ColumnIndices)
            {
                columnSums[column] += weightSquare * observation.ColumnIndices.Length;
            }
        }

        return Math.Max(1.0, columnSums.Max());
    }

    private static double[] CalculateGradient(
        IReadOnlyList<EcdCwrFaultDictionaryObservation> observations,
        IReadOnlyList<double> coefficients)
    {
        var gradient = new double[ColumnCount];
        foreach (var observation in observations)
        {
            var residual = Predict(observation, coefficients) - observation.Value;
            var weightedResidual = observation.Weight * observation.Weight * residual;
            foreach (var column in observation.ColumnIndices)
            {
                gradient[column] += weightedResidual;
            }
        }

        return gradient;
    }

    private static void ApplyDriveMeasureGroupShrink(double[] coefficients, double threshold)
    {
        if (threshold <= 0.0)
        {
            return;
        }

        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var driveColumn = EcdCwrFaultDictionary.DriveColumn(electrode);
            var measureColumn = EcdCwrFaultDictionary.MeasureColumn(electrode);
            var norm = Math.Sqrt(
                (coefficients[driveColumn] * coefficients[driveColumn]) +
                (coefficients[measureColumn] * coefficients[measureColumn]));
            if (norm <= threshold)
            {
                coefficients[driveColumn] = 0.0;
                coefficients[measureColumn] = 0.0;
                continue;
            }

            var scale = 1.0 - (threshold / norm);
            coefficients[driveColumn] *= scale;
            coefficients[measureColumn] *= scale;
        }
    }

    private static double Predict(
        EcdCwrFaultDictionaryObservation observation,
        IReadOnlyList<double> coefficients)
    {
        var sum = 0.0;
        foreach (var column in observation.ColumnIndices)
        {
            sum += coefficients[column];
        }

        return sum;
    }

    private static double[] Slice(IReadOnlyList<double> values, int start)
    {
        return values.Skip(start).Take(ElectrodeCount).ToArray();
    }

    private static ElectrodeFaultType[] InterpretElectrodeFaultTypes(
        IReadOnlyList<double> drive,
        IReadOnlyList<double> measure,
        EcdCwrFaultDictionaryLocalizerOptions options)
    {
        var types = Enumerable.Repeat(ElectrodeFaultType.None, ElectrodeCount).ToArray();
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var max = Math.Max(drive[electrode], measure[electrode]);
            var min = Math.Min(drive[electrode], measure[electrode]);
            if (max < options.FaultThreshold)
            {
                continue;
            }

            types[electrode] = min >= options.JointElectrodeThresholdRatio * max
                ? ElectrodeFaultType.ElectrodeContact
                : ElectrodeFaultType.UncertainStructured;
        }

        return types;
    }

    private static double[] CalculateConfidence(
        IReadOnlyList<double> electrodeScores,
        EcdCwrFaultDictionaryLocalizerOptions options)
    {
        return electrodeScores
            .Select(score => Math.Clamp(score / Math.Max(options.FaultThreshold, 1e-9), 0.0, 1.0))
            .ToArray();
    }

    private static string[] BuildReasons(
        IReadOnlyList<double> drive,
        IReadOnlyList<double> measure,
        IReadOnlyList<ElectrodeFaultType> faultTypes)
    {
        return Enumerable.Range(0, ElectrodeCount)
            .Select(electrode => faultTypes[electrode] switch
            {
                ElectrodeFaultType.ElectrodeContact =>
                    $"dictionary drive+measure drive={drive[electrode]:G3} measure={measure[electrode]:G3}",
                ElectrodeFaultType.UncertainStructured =>
                    $"dictionary single-role drive={drive[electrode]:G3} measure={measure[electrode]:G3}",
                _ => string.Empty
            })
            .ToArray();
    }

    private static EcdCwrLinkFault[] InterpretPairLinks(
        IReadOnlyList<double> pairLink,
        IReadOnlyList<double> drive,
        IReadOnlyList<double> measure,
        EcdCwrFaultDictionaryLocalizerOptions options)
    {
        var faults = new List<EcdCwrLinkFault>();
        for (var index = 0; index < ElectrodeCount; index++)
        {
            var score = pairLink[index];
            var adjacentElectrodeScore = new[]
            {
                drive[index],
                drive[Mod(index + 1)],
                measure[index],
                measure[Mod(index + 1)]
            }.Max();
            var dominates = score >= adjacentElectrodeScore * options.LinkDominanceRatio;
            if (score >= options.LinkFaultThreshold && dominates)
            {
                faults.Add(new EcdCwrLinkFault(
                    index,
                    score,
                    Active: true,
                    $"dictionary pairlink={index} score={score:G3}"));
            }
        }

        return faults.ToArray();
    }

    private static EcdCwrLinkFault[] InterpretMeasurementChannels(
        IReadOnlyList<double> measurementChannel,
        IReadOnlyList<double> measure,
        EcdCwrFaultDictionaryLocalizerOptions options)
    {
        var faults = new List<EcdCwrLinkFault>();
        for (var index = 0; index < ElectrodeCount; index++)
        {
            var score = measurementChannel[index];
            var adjacentElectrodeScore = Math.Max(measure[index], measure[Mod(index + 1)]);
            var dominates = score >= adjacentElectrodeScore * options.LinkDominanceRatio;
            if (score >= options.LinkFaultThreshold && dominates)
            {
                faults.Add(new EcdCwrLinkFault(
                    index,
                    score,
                    Active: true,
                    $"dictionary measchannel={index} score={score:G3}"));
            }
        }

        return faults.ToArray();
    }

    private static double CalculateResidualRms(
        IReadOnlyList<EcdCwrFaultDictionaryObservation> observations,
        IReadOnlyList<double> coefficients)
    {
        if (observations.Count == 0)
        {
            return 0.0;
        }

        var sumSquares = observations.Sum(observation =>
        {
            var residual = observation.Value - Predict(observation, coefficients);
            return observation.Weight * observation.Weight * residual * residual;
        });
        return Math.Sqrt(sumSquares / observations.Count);
    }

    private static int Mod(int value)
    {
        var result = value % ElectrodeCount;
        return result < 0 ? result + ElectrodeCount : result;
    }
}

public sealed record EcdCwrFaultDictionaryInput(
    EcdCwrEvidenceAResult? EvidenceA = null,
    IReadOnlyList<double>? ReciprocityScores208 = null,
    IReadOnlyList<double>? ShapeScores208 = null,
    IReadOnlyList<double>? TopologyScores16 = null,
    double Contact48Weight = 1.0,
    double ReciprocityWeight = 1.0,
    double ShapeWeight = 0.35,
    double TopologyWeight = 1.0);

public sealed record EcdCwrFaultDictionaryObservation(
    EcdCwrFaultDictionaryObservationKind Kind,
    int StimulationIndex,
    int? RelativeChannelIndex,
    double Value,
    double Weight,
    int[] ColumnIndices);

public enum EcdCwrFaultDictionaryObservationKind
{
    Contact48 = 0,
    Reciprocity208 = 1,
    Shape208 = 2,
    Topology16 = 3
}

public sealed record EcdCwrFaultDictionaryLocalizerOptions(
    double L1Penalty = 0.05,
    double GroupPenalty = 0.02,
    int MaxIterations = 1000,
    double ConvergenceTolerance = 1e-7,
    double FaultThreshold = 1.0,
    double LinkFaultThreshold = 1.0,
    double JointElectrodeThresholdRatio = 0.5,
    double LinkDominanceRatio = 0.8);

public enum EcdCwrFaultDictionaryPolicy
{
    PureL1 = 0,
    PureGroup = 1,
    SparseGroup = 2
}

public sealed record EcdCwrFaultDictionaryPolicyDefinition(
    EcdCwrFaultDictionaryPolicy Policy,
    string Version,
    double L1Penalty,
    double GroupPenalty);

public static class EcdCwrFaultDictionaryPolicies
{
    public const EcdCwrFaultDictionaryPolicy SelectedPolicy =
        EcdCwrFaultDictionaryPolicy.SparseGroup;

    public static IReadOnlyList<EcdCwrFaultDictionaryPolicyDefinition> All { get; } =
    [
        new(EcdCwrFaultDictionaryPolicy.PureL1, "ecd-cwr-dict-pure-l1-v1", 0.05, 0.0),
        new(EcdCwrFaultDictionaryPolicy.PureGroup, "ecd-cwr-dict-pure-group-v1", 0.0, 0.02),
        new(EcdCwrFaultDictionaryPolicy.SparseGroup, "ecd-cwr-dict-sparse-group-v1", 0.05, 0.02)
    ];

    public static EcdCwrFaultDictionaryPolicyDefinition Selected => Get(SelectedPolicy);

    public static EcdCwrFaultDictionaryPolicyDefinition Get(EcdCwrFaultDictionaryPolicy policy)
    {
        return All.Single(definition => definition.Policy == policy);
    }
}

public sealed record EcdCwrFaultLocalizationResult(
    double[] DriveScores,
    double[] MeasureScores,
    double[] PairLinkScores,
    double[] MeasurementChannelScores,
    double[] ElectrodeScores,
    double[] FaultConfidence,
    ElectrodeFaultType[] FaultTypes,
    string[] UpgradeGateReasons,
    IReadOnlyList<EcdCwrLinkFault> PairLinkFaults,
    IReadOnlyList<EcdCwrLinkFault> MeasurementChannelFaults,
    double ResidualRms,
    int ObservationCount);

public sealed record EcdCwrLinkFault(
    int Index,
    double Score,
    bool Active,
    string Reason);
