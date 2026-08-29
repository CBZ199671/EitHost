using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationReconstructionRunner
{
    private const int RetainedCount = RealtimeReconstructionRequest.BoundaryVoltageCount;

    public async Task<EcdCwrSimulationReconstructionRunReport> RunAsync(
        EcdCwrSimulationBatchManifest manifest,
        IReadOnlyList<EcdCwrSimulationPrediction> predictions,
        EcdCwrSimulationReconstructionRunOptions options,
        IRealtimeReconstructionBackend backend,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        Directory.CreateDirectory(options.OutputDirectory);
        var predictionByScenario = predictions
            .GroupBy(prediction => prediction.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var selected = SelectWorkItems(manifest.WorkItems, options).ToArray();
        var items = new List<EcdCwrSimulationReconstructionRunItem>();
        var references = new List<EcdCwrReconstructionResultReference>();
        for (var scenarioIndex = 0; scenarioIndex < selected.Length; scenarioIndex++)
        {
            var workItem = selected[scenarioIndex];
            predictionByScenario.TryGetValue(workItem.ScenarioId, out var prediction);
            var reuseCache = new List<CachedMethodTransform>();
            foreach (var method in NormalizeMethods(options.Methods))
            {
                var item = await RunMethodAsync(
                    workItem,
                    prediction,
                    method,
                    options,
                    backend,
                    reuseCache,
                    cancellationToken).ConfigureAwait(false);
                items.Add(item);
                if (item.Reference is not null)
                {
                    references.Add(item.Reference);
                }

                if (!item.Passed && !options.ContinueOnError)
                {
                    return BuildReport(manifest, selected.Length, items, references);
                }
            }

            progress?.Invoke(scenarioIndex + 1, selected.Length);
        }

        return BuildReport(manifest, selected.Length, items, references);
    }

    public static string ToMarkdown(EcdCwrSimulationReconstructionRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Simulation Reconstruction Run",
            "",
            $"- Completed at: {report.CompletedAt:O}",
            $"- Manifest work items: {report.ManifestWorkItemCount}",
            $"- Selected scenarios: {report.SelectedScenarioCount}",
            $"- Attempts: {report.AttemptedItems}",
            $"- Succeeded: {report.SucceededItems}",
            $"- Skipped existing: {report.SkippedExisting}",
            $"- Frame drops: {report.FrameDropItems}",
            $"- Failed: {report.FailedItems}",
            $"- Result references: {report.ResultReferences.Count}",
            "",
            "## Items",
            "",
            "|scenario|method|status|result|issues|",
            "|---|---|---|---|---|"
        };
        foreach (var item in report.Items.Take(300))
        {
            lines.Add(
                $"|{item.ScenarioId}|{item.Method}|{item.Status}|{item.ResultHdf5Path}|{string.Join("<br>", item.Issues)}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static EcdCwrSimulationReconstructionRunReport BuildReport(
        EcdCwrSimulationBatchManifest manifest,
        int selectedCount,
        IReadOnlyList<EcdCwrSimulationReconstructionRunItem> items,
        IReadOnlyList<EcdCwrReconstructionResultReference> references)
    {
        return new EcdCwrSimulationReconstructionRunReport(
            DateTimeOffset.Now,
            manifest.WorkItems.Count,
            selectedCount,
            items.Count,
            items.Count(item => item.Passed),
            items.Count(item => item.Status == EcdCwrSimulationReconstructionRunStatus.SkippedExisting),
            items.Count(item => item.Status == EcdCwrSimulationReconstructionRunStatus.FrameDropped),
            items.Count(item => !item.Passed),
            references.ToArray(),
            items.ToArray());
    }

    private static IEnumerable<EcdCwrSimulationWorkItem> SelectWorkItems(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        EcdCwrSimulationReconstructionRunOptions options)
    {
        IEnumerable<EcdCwrSimulationWorkItem> query = workItems;
        if (options.ScenarioIds.Count > 0)
        {
            query = query.Where(item => options.ScenarioIds.Contains(item.ScenarioId));
        }

        if (options.FiniteContactOnly)
        {
            query = query.Where(IsFiniteContactReconstructionScenario);
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

    private static bool IsFiniteContactReconstructionScenario(EcdCwrSimulationWorkItem item)
    {
        return item.Scenario.TargetCount > 0 &&
            (item.Scenario.FaultMode is EcdCwrFaultMode.Single
                or EcdCwrFaultMode.AdjacentDual
                or EcdCwrFaultMode.RemoteDual
                or EcdCwrFaultMode.Triple) &&
            double.IsFinite(item.Scenario.ContactImpedance.Multiplier) &&
            item.Scenario.ContactImpedance.Multiplier > 1.0;
    }

    private static IReadOnlyList<string> NormalizeMethods(IReadOnlyList<string> methods)
    {
        var selected = methods.Count == 0
            ? EcdCwrReconstructionMethods.All
            : methods;
        return selected
            .Select(EcdCwrReconstructionMethods.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<EcdCwrSimulationReconstructionRunItem> RunMethodAsync(
        EcdCwrSimulationWorkItem workItem,
        EcdCwrSimulationPrediction? prediction,
        string method,
        EcdCwrSimulationReconstructionRunOptions options,
        IRealtimeReconstructionBackend backend,
        List<CachedMethodTransform> reuseCache,
        CancellationToken cancellationToken)
    {
        var diagnosticPolicyVersion = prediction?.DiagnosticPolicyVersion;
        var outputPath = ResultPath(
            options.OutputDirectory,
            diagnosticPolicyVersion,
            method,
            workItem.ScenarioId);
        if (options.SkipExisting && File.Exists(outputPath))
        {
            if (File.Exists(workItem.OutputHdf5Path))
            {
                try
                {
                    var cachedData = ReadSimulationData(workItem.OutputHdf5Path);
                    var cachedTransform = TransformForMethod(cachedData, prediction, method);
                    reuseCache.Add(new CachedMethodTransform(
                        cachedTransform.Target208.ToArray(),
                        cachedTransform.MeasurementWeight208.ToArray(),
                        outputPath));
                }
                catch (Exception)
                {
                    // Existing result remains usable; cache seeding is optional.
                }
            }

            return Success(
                workItem,
                method,
                outputPath,
                EcdCwrSimulationReconstructionRunStatus.SkippedExisting,
                [],
                diagnosticPolicyVersion,
                DefaultMethodPolicyVersion(method));
        }

        if (!File.Exists(workItem.OutputHdf5Path))
        {
            return Failed(workItem, method, outputPath, "missing simulation HDF5");
        }

        try
        {
            var data = ReadSimulationData(workItem.OutputHdf5Path);
            var transformed = TransformForMethod(data, prediction, method);
            if (method == EcdCwrReconstructionMethods.FrameDrop && transformed.FrameDropped)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                WriteBackgroundReconstruction(outputPath, data);
                return Success(
                    workItem,
                    method,
                    outputPath,
                    EcdCwrSimulationReconstructionRunStatus.FrameDropped,
                    ["frame dropped to background reference"],
                    diagnosticPolicyVersion,
                    transformed.WeightPolicyVersion);
            }

            var reusable = FindReusableResult(reuseCache, transformed);
            if (reusable is not null && File.Exists(reusable.ResultHdf5Path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                if (!string.Equals(reusable.ResultHdf5Path, outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(reusable.ResultHdf5Path, outputPath, overwrite: true);
                }

                return Success(
                    workItem,
                    method,
                    outputPath,
                    EcdCwrSimulationReconstructionRunStatus.Succeeded,
                    [$"reused reconstruction from identical input: {Path.GetFileName(reusable.ResultHdf5Path)}"],
                    diagnosticPolicyVersion,
                    transformed.WeightPolicyVersion);
            }

            var request = new RealtimeReconstructionRequest(
                "ECD-CWR-SIM",
                blockNumber: Math.Max(1, ExtractScenarioIndex(workItem.ScenarioId) + 1),
                DateTimeOffset.Now,
                data.Reference208,
                transformed.Target208,
                options.ExcitationFrequencyHz,
                options.ExcitationChannelCycles,
                options.MeshSize,
                options.DifferenceLambda,
                persistResultFiles: true,
                options.ReconstructionRoute,
                customLambdaEnabled: true,
                differenceOrientation: options.DifferenceOrientation,
                transformed.MeasurementWeight208,
                transformed.WeightPolicyVersion);
            var result = await backend.ReconstructAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || !File.Exists(result.OutputHdf5Path))
            {
                return Failed(
                    workItem,
                    method,
                    outputPath,
                    result.ErrorMessage ?? "reconstruction backend did not produce a result HDF5");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Copy(result.OutputHdf5Path, outputPath, overwrite: true);
            DeleteCopiedExchangeResult(result.OutputHdf5Path, outputPath, options.OutputDirectory);
            reuseCache.Add(new CachedMethodTransform(
                transformed.Target208.ToArray(),
                transformed.MeasurementWeight208.ToArray(),
                outputPath));
            return Success(
                workItem,
                method,
                outputPath,
                EcdCwrSimulationReconstructionRunStatus.Succeeded,
                [],
                diagnosticPolicyVersion,
                transformed.WeightPolicyVersion);
        }
        catch (Exception ex)
        {
            return Failed(workItem, method, outputPath, ex.Message);
        }
    }

    private static void DeleteCopiedExchangeResult(
        string backendResultPath,
        string copiedResultPath,
        string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(backendResultPath) ||
            !File.Exists(backendResultPath) ||
            string.Equals(
                Path.GetFullPath(backendResultPath),
                Path.GetFullPath(copiedResultPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var exchangeDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "_exchange"));
        var fullBackendPath = Path.GetFullPath(backendResultPath);
        if (!fullBackendPath.StartsWith(
            exchangeDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(fullBackendPath);
        }
        catch
        {
            // Best-effort cleanup. Reconstruction artifacts were already copied.
        }
    }

    private static CachedMethodTransform? FindReusableResult(
        IReadOnlyList<CachedMethodTransform> cache,
        MethodTransform transform)
    {
        foreach (var candidate in cache)
        {
            if (candidate.Target208.SequenceEqual(transform.Target208) &&
                candidate.MeasurementWeight208.SequenceEqual(transform.MeasurementWeight208))
            {
                return candidate;
            }
        }

        return null;
    }

    private static MethodTransform TransformForMethod(
        SimulationData data,
        EcdCwrSimulationPrediction? prediction,
        string method)
    {
        var scores = NormalizeScores(prediction);
        var continuousWeights = new EcdCwrContinuousWeightMapper().Map(scores);
        var binaryOptions = new EcdCwrBinaryWeightMapperOptions();
        var badMask = continuousWeights.Select(weight => weight < 0.99).ToArray();
        return method switch
        {
            EcdCwrReconstructionMethods.Weighted => new MethodTransform(
                data.Target208,
                continuousWeights,
                EcdCwrContinuousWeightMapper.CreatePolicyVersion(new EcdCwrContinuousWeightMapperOptions()),
                FrameDropped: false),
            EcdCwrReconstructionMethods.ContaminationAwareWeighted => new MethodTransform(
                data.Target208,
                new EcdCwrContaminationAwareWeightMapper().Map(
                    scores,
                    prediction?.CandidateEvidenceKinds,
                    prediction?.FaultTypes),
                EcdCwrContaminationAwareWeightMapper.CreatePolicyVersion(
                    new EcdCwrContinuousWeightMapperOptions()),
                FrameDropped: false),
            EcdCwrReconstructionMethods.BinaryWeighted => new MethodTransform(
                data.Target208,
                new EcdCwrBinaryWeightMapper().Map(prediction?.States, binaryOptions),
                EcdCwrBinaryWeightMapper.CreatePolicyVersion(binaryOptions),
                FrameDropped: false),
            EcdCwrReconstructionMethods.AllOne => new MethodTransform(
                data.Target208,
                Ones(),
                "ecd-cwr-all-one-v1",
                FrameDropped: false),
            EcdCwrReconstructionMethods.StaticReplacement => new MethodTransform(
                ReplaceBadMeasurements(data.Target208, data.Reference208, badMask),
                Ones(),
                "sr-static-replacement-v1",
                FrameDropped: false),
            EcdCwrReconstructionMethods.DirectReciprocity => new MethodTransform(
                ReplaceBadMeasurementsWithReciprocal(data.Target208, data.Reference208, badMask),
                Ones(),
                "drm-direct-reciprocity-v1",
                FrameDropped: false),
            EcdCwrReconstructionMethods.Rong2026TemplateReplacement => BuildRong2026Transform(data),
            EcdCwrReconstructionMethods.FrameDrop => new MethodTransform(
                data.Target208,
                Ones(),
                "cd-frame-drop-v1",
                FrameDropped: HasRedLike(prediction)),
            _ => throw new ArgumentException($"Unsupported ECD-CWR reconstruction method '{method}'.")
        };
    }

    private static MethodTransform BuildRong2026Transform(SimulationData data)
    {
        var result = new EcdCwrRong2026Baseline().Analyze(data.RongInput);
        return new MethodTransform(
            result.CompensatedReal208.ToArray(),
            Ones(),
            result.PolicyVersion,
            FrameDropped: false);
    }

    private static double[] NormalizeScores(EcdCwrSimulationPrediction? prediction)
    {
        if (prediction?.Scores is { Count: 16 } scores)
        {
            return scores.Select(value => double.IsFinite(value) ? Math.Max(0.0, value) : 0.0).ToArray();
        }

        if (prediction?.States is { Count: 16 } states)
        {
            return states.Select(static state => state switch
            {
                ElectrodeContactState.SystemLevel => 8.0,
                ElectrodeContactState.DarkRed => 8.0,
                ElectrodeContactState.Red => 6.0,
                ElectrodeContactState.Yellow => 2.0,
                _ => 0.0
            }).ToArray();
        }

        return new double[16];
    }

    private static bool HasRedLike(EcdCwrSimulationPrediction? prediction)
    {
        return prediction is not null &&
            (prediction.SystemLevel ||
                prediction.States?.Any(state =>
                    state is ElectrodeContactState.Red
                        or ElectrodeContactState.DarkRed
                        or ElectrodeContactState.SystemLevel) == true);
    }

    private static double[] ReplaceBadMeasurements(
        IReadOnlyList<double> target,
        IReadOnlyList<double> replacement,
        IReadOnlyList<bool> badMask)
    {
        var output = target.ToArray();
        for (var index = 0; index < output.Length; index++)
        {
            if (badMask[index])
            {
                output[index] = replacement[index];
            }
        }

        return output;
    }

    private static double[] ReplaceBadMeasurementsWithReciprocal(
        IReadOnlyList<double> target,
        IReadOnlyList<double> reference,
        IReadOnlyList<bool> badMask)
    {
        var output = target.ToArray();
        for (var index = 0; index < output.Length; index++)
        {
            if (!badMask[index])
            {
                continue;
            }

            var reciprocal = ReciprocalRetainedIndex(index);
            output[index] = reciprocal >= 0 ? target[reciprocal] : reference[index];
        }

        return output;
    }

    private static int ReciprocalRetainedIndex(int retainedIndex)
    {
        var stimulation = retainedIndex / 13;
        var relative = (retainedIndex % 13) + 2;
        var reciprocalStim = Mod(stimulation + relative);
        var reciprocalRelative = 16 - relative;
        return (reciprocalStim * 13) + (reciprocalRelative - 2);
    }

    private static SimulationData ReadSimulationData(string hdf5Path)
    {
        using var file = Hdf5FileAccess.OpenReadWithRetry(hdf5Path);
        return new SimulationData(
            ReadRealVector(file, "/reference_retained_complex_208", RetainedCount, preferComplex: true),
            ReadRealVector(file, "/retained_complex_208", RetainedCount, preferComplex: true),
            ReadRealVector(file, "/ground_truth_conductivity"),
            ReadDoubleMatrix(file, "/node_coords"),
            ReadIntMatrix(file, "/cell_connectivity"),
            EcdCwrRong2026BaselineRunner.ReadInputFromHdf5(hdf5Path));
    }

    private static double[] ReadRealVector(
        IH5Group file,
        string datasetPath,
        int? expectedLength = null,
        bool preferComplex = false)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        double[] values;
        if (preferComplex &&
            TryRead(() => dataset.Read<Hdf5Complex128[]>(memoryDims: dimensions), out var preferredComplex128Vector))
        {
            values = preferredComplex128Vector.Select(value => value.Real).ToArray();
        }
        else if (preferComplex &&
            TryRead(() => dataset.Read<Hdf5Complex64[]>(memoryDims: dimensions), out var preferredComplex64Vector))
        {
            values = preferredComplex64Vector.Select(value => (double)value.Real).ToArray();
        }
        else if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubleVector))
        {
            values = doubleVector;
        }
        else if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var floatVector))
        {
            values = floatVector.Select(value => (double)value).ToArray();
        }
        else if (TryRead(() => dataset.Read<Hdf5Complex64[]>(memoryDims: dimensions), out var complex64Vector))
        {
            values = complex64Vector.Select(value => (double)value.Real).ToArray();
        }
        else if (TryRead(() => dataset.Read<Hdf5Complex128[]>(memoryDims: dimensions), out var complex128Vector))
        {
            values = complex128Vector.Select(value => value.Real).ToArray();
        }
        else
        {
            throw new InvalidDataException($"Unsupported vector dataset type at {datasetPath}.");
        }

        if (expectedLength is not null && values.Length != expectedLength.Value)
        {
            throw new InvalidDataException($"{datasetPath} length {values.Length} != {expectedLength.Value}.");
        }

        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidDataException($"{datasetPath} contains non-finite values.");
        }

        return values;
    }

    private static double[,] ReadDoubleMatrix(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubleMatrix))
        {
            return doubleMatrix;
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floatMatrix))
        {
            return ConvertToDouble(floatMatrix);
        }

        throw new InvalidDataException($"Unsupported matrix dataset type at {datasetPath}.");
    }

    private static int[,] ReadIntMatrix(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (TryRead(() => dataset.Read<int[,]>(memoryDims: dimensions), out var intMatrix))
        {
            return intMatrix;
        }

        if (TryRead(() => dataset.Read<long[,]>(memoryDims: dimensions), out var longMatrix))
        {
            return ConvertToInt(longMatrix);
        }

        if (TryRead(() => dataset.Read<uint[,]>(memoryDims: dimensions), out var uintMatrix))
        {
            return ConvertToInt(uintMatrix);
        }

        throw new InvalidDataException($"Unsupported integer matrix dataset type at {datasetPath}.");
    }

    private static void WriteBackgroundReconstruction(string path, SimulationData data)
    {
        new H5File
        {
            ["conductivity"] = Enumerable.Repeat(1.0, data.GroundTruthConductivity.Length).ToArray(),
            ["node_coords"] = data.NodeCoords,
            ["cell_connectivity"] = data.CellConnectivity
        }.Write(path);
    }

    private static EcdCwrSimulationReconstructionRunItem Success(
        EcdCwrSimulationWorkItem workItem,
        string method,
        string outputPath,
        EcdCwrSimulationReconstructionRunStatus status,
        IReadOnlyList<string> issues,
        string? diagnosticPolicyVersion,
        string? methodPolicyVersion)
    {
        return new EcdCwrSimulationReconstructionRunItem(
            workItem.ScenarioId,
            method,
            status,
            outputPath,
            issues,
            new EcdCwrReconstructionResultReference(
                workItem.ScenarioId,
                method,
                outputPath,
                diagnosticPolicyVersion,
                methodPolicyVersion));
    }

    private static string? DefaultMethodPolicyVersion(string method)
    {
        return method == EcdCwrReconstructionMethods.Rong2026TemplateReplacement
            ? EcdCwrRong2026Baseline.CreatePolicyVersion(new EcdCwrRong2026Options())
            : null;
    }

    private static EcdCwrSimulationReconstructionRunItem Failed(
        EcdCwrSimulationWorkItem workItem,
        string method,
        string outputPath,
        string issue)
    {
        return new EcdCwrSimulationReconstructionRunItem(
            workItem.ScenarioId,
            method,
            EcdCwrSimulationReconstructionRunStatus.Failed,
            outputPath,
            [issue],
            null);
    }

    private static string ResultPath(
        string outputDirectory,
        string? diagnosticPolicyVersion,
        string method,
        string scenarioId)
    {
        return Path.Combine(
            Path.GetFullPath(outputDirectory),
            EcdCwrDiagnosticPolicy.ToPathSegment(diagnosticPolicyVersion),
            method,
            $"{scenarioId}.h5");
    }

    private static int ExtractScenarioIndex(string scenarioId)
    {
        var suffix = new string(scenarioId.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(suffix, out var parsed) ? parsed : 0;
    }

    private static double[] Ones()
    {
        return Enumerable.Repeat(1.0, RetainedCount).ToArray();
    }

    private static double[,] ConvertToDouble(float[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = values[row, column];
            }
        }

        return output;
    }

    private static int[,] ConvertToInt(long[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = checked((int)values[row, column]);
            }
        }

        return output;
    }

    private static int[,] ConvertToInt(uint[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var output = new int[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                output[row, column] = checked((int)values[row, column]);
            }
        }

        return output;
    }

    private static int Mod(int value)
    {
        var result = value % 16;
        return result < 0 ? result + 16 : result;
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

    private sealed record SimulationData(
        double[] Reference208,
        double[] Target208,
        double[] GroundTruthConductivity,
        double[,] NodeCoords,
        int[,] CellConnectivity,
        EcdCwrRong2026Input RongInput);

    private sealed record MethodTransform(
        double[] Target208,
        double[] MeasurementWeight208,
        string WeightPolicyVersion,
        bool FrameDropped);

    private sealed record CachedMethodTransform(
        double[] Target208,
        double[] MeasurementWeight208,
        string ResultHdf5Path);
}

public sealed record EcdCwrSimulationReconstructionRunOptions
{
    public string OutputDirectory { get; init; } =
        Path.Combine(Environment.CurrentDirectory, "artifacts", "ecd-cwr-reconstruction");

    public IReadOnlyList<string> Methods { get; init; } = EcdCwrReconstructionMethods.All;

    public int StartIndex { get; init; }

    public int? Limit { get; init; }

    public IReadOnlySet<string> ScenarioIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool SkipExisting { get; init; }

    public bool ContinueOnError { get; init; }

    public bool FiniteContactOnly { get; init; }

    public double MeshSize { get; init; } = 0.12;

    public double DifferenceLambda { get; init; } = 1.0e-2;

    public string ReconstructionRoute { get; init; } = RealtimeReconstructionRequest.DefaultReconstructionRoute;

    public string DifferenceOrientation { get; init; } = RealtimeReconstructionRequest.DefaultDifferenceOrientation;

    public int ExcitationFrequencyHz { get; init; } = 10_000;

    public double ExcitationChannelCycles { get; init; } = 10.0;
}

public enum EcdCwrSimulationReconstructionRunStatus
{
    Succeeded = 0,
    SkippedExisting = 1,
    FrameDropped = 2,
    Failed = 3
}

public sealed record EcdCwrSimulationReconstructionRunReport(
    DateTimeOffset CompletedAt,
    int ManifestWorkItemCount,
    int SelectedScenarioCount,
    int AttemptedItems,
    int SucceededItems,
    int SkippedExisting,
    int FrameDropItems,
    int FailedItems,
    IReadOnlyList<EcdCwrReconstructionResultReference> ResultReferences,
    IReadOnlyList<EcdCwrSimulationReconstructionRunItem> Items)
{
    public bool Passed => FailedItems == 0;
}

public sealed record EcdCwrSimulationReconstructionRunItem(
    string ScenarioId,
    string Method,
    EcdCwrSimulationReconstructionRunStatus Status,
    string ResultHdf5Path,
    IReadOnlyList<string> Issues,
    EcdCwrReconstructionResultReference? Reference)
{
    public bool Passed => Status != EcdCwrSimulationReconstructionRunStatus.Failed && Reference is not null;
}
