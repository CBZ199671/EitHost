using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrSimulationPredictionReplayRunner
{
    public EcdCwrSimulationPredictionReplayReport Replay(
        EcdCwrSimulationBatchManifest manifest,
        EcdCwrSimulationPredictionReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new EcdCwrSimulationPredictionReplayOptions();
        var diagnosticPolicyVersion = EcdCwrDiagnosticPolicy.ForProfile(options.DiagnosticProfile);
        var selected = SelectWorkItems(manifest.WorkItems, options).ToArray();
        var items = selected.Select(item => ReplayItem(item, options)).ToArray();
        return new EcdCwrSimulationPredictionReplayReport(
            DateTimeOffset.Now,
            options.DiagnosticProfile,
            manifest.WorkItems.Count,
            selected.Length,
            items.Count(item => item.Skipped),
            items.Count(item => !item.Passed && !item.Skipped),
            items.Where(item => item.Prediction is not null).Select(item => item.Prediction!).ToArray(),
            items,
            diagnosticPolicyVersion);
    }

    public EcdCwrFaultDictionaryPolicyBenchmarkReport BenchmarkFaultDictionaryPolicies(
        EcdCwrSimulationBatchManifest manifest,
        EcdCwrFaultDictionaryPolicyBenchmarkOptions? options = null,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        options ??= new EcdCwrFaultDictionaryPolicyBenchmarkOptions();
        var selected = SelectWorkItems(manifest.WorkItems, options).ToArray();
        var definitions = EcdCwrFaultDictionaryPolicies.All;
        var predictions = definitions.ToDictionary(
            definition => definition.Policy,
            _ => new List<EcdCwrSimulationPrediction>());
        var activeCounts = definitions.ToDictionary(
            definition => definition.Policy,
            _ => new List<double>());
        var residuals = definitions.ToDictionary(
            definition => definition.Policy,
            _ => new List<double>());
        var skippedMissing = 0;
        var failed = 0;

        for (var index = 0; index < selected.Length; index++)
        {
            var item = selected[index];
            if (!File.Exists(item.OutputHdf5Path))
            {
                skippedMissing++;
                if (!options.SkipMissingResults)
                {
                    failed++;
                }

                progress?.Invoke(index + 1, selected.Length);
                continue;
            }

            try
            {
                using var file = Hdf5FileAccess.OpenReadWithRetry(item.OutputHdf5Path);
                var raw = ReadComplexMatrix(file, "/raw_complex_256");
                var reference = ReadComplexMatrix(file, "/reference_complex_256");
                var baseline = ElectrodeContactBaseline.FromReference(reference.Real, reference.Imaginary);
                foreach (var definition in definitions)
                {
                    var monitor = new ElectrodeContactMonitor(
                        baseline,
                        new ElectrodeContactMonitorOptions
                        {
                            EwmaRise = 1.0,
                            EwmaFall = 1.0,
                            FaultDictionaryPolicy = definition.Policy
                        });
                    monitor.Update(raw.Real, raw.Imaginary);
                    var result = monitor.Update(raw.Real, raw.Imaginary);
                    var policyVersion = $"{EcdCwrDiagnosticPolicy.CurrentVersion}+{definition.Version}";
                    predictions[definition.Policy].Add(new EcdCwrSimulationPrediction(
                        item.ScenarioId,
                        result.States,
                        result.FaultTypes,
                        result.Scores,
                        result.SystemLevel,
                        ImageQualityScore: result.ImageQualityScore,
                        DiagnosticPolicyVersion: policyVersion,
                        CandidateScores: result.CandidateScores,
                        CandidateFaultTypes: result.CandidateFaultTypes,
                        CandidateEvidenceKinds: result.CandidateEvidenceKinds,
                        CandidateReasons: result.CandidateReasons,
                        PhysicalFieldGuardApplied: result.PhysicalFieldGuardApplied));
                    if (result.FaultDictionaryTrace is { } trace)
                    {
                        activeCounts[definition.Policy].Add(
                            trace.ActiveCoefficientCount(options.ActiveCoefficientThreshold));
                        if (double.IsFinite(trace.ResidualRms))
                        {
                            residuals[definition.Policy].Add(trace.ResidualRms);
                        }
                    }
                }
            }
            catch
            {
                failed++;
            }

            progress?.Invoke(index + 1, selected.Length);
        }

        var evaluator = new EcdCwrSimulationScoreEvaluator();
        var rows = definitions
            .Select(definition =>
            {
                var policyPredictions = predictions[definition.Policy];
                var score = evaluator.Evaluate(selected, policyPredictions);
                var policyActiveCounts = activeCounts[definition.Policy];
                var policyResiduals = residuals[definition.Policy];
                return new EcdCwrFaultDictionaryPolicyBenchmarkRow(
                    definition.Policy,
                    definition.Version,
                    definition.L1Penalty,
                    definition.GroupPenalty,
                    score.PredictionCount,
                    score.MissingPredictionCount,
                    score.HealthyFalseRedRate,
                    score.HealthyBoundaryHighFalseRedRate,
                    score.SingleElectrodeTop1Accuracy,
                    score.AdjacentDualSeparationRate,
                    score.FaultTypeAccuracy,
                    policyActiveCounts.Count == 0 ? double.NaN : policyActiveCounts.Average(),
                    policyResiduals.Count == 0 ? double.NaN : policyResiduals.Average(),
                    score.DiagnosticPolicyVersion ?? string.Empty);
            })
            .ToArray();
        var winner = rows
            .OrderBy(row => row.HealthyBoundaryHighFalseRedRate)
            .ThenBy(row => row.HealthyFalseRedRate)
            .ThenByDescending(row => row.FaultTypeAccuracy)
            .ThenByDescending(row => row.AdjacentDualSeparationRate)
            .ThenByDescending(row => row.SingleElectrodeTop1Accuracy)
            .ThenBy(row => row.MeanActiveCoefficientCount)
            .ThenBy(row => row.MeanResidualRms)
            .First();
        var persisted = EcdCwrFaultDictionaryPolicies.Selected;
        return new EcdCwrFaultDictionaryPolicyBenchmarkReport(
            DateTimeOffset.Now,
            manifest.WorkItems.Count,
            selected.Length,
            skippedMissing,
            failed,
            options.ActiveCoefficientThreshold,
            "raw/reference complex256; identical A normalization and absent B/C/D masks for all policies",
            winner.Policy,
            winner.PolicyVersion,
            persisted.Policy,
            persisted.Version,
            rows);
    }

    public static string ToMarkdown(EcdCwrSimulationPredictionReplayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            "# ECD-CWR Simulation Prediction Replay",
            "",
            $"- Replayed at: {report.ReplayedAt:O}",
            $"- Diagnostic profile: {report.DiagnosticProfile}",
            $"- Diagnostic policy: {report.DiagnosticPolicyVersion}",
            $"- Manifest work items: {report.ManifestWorkItemCount}",
            $"- Selected: {report.SelectedItems}",
            $"- Predictions: {report.Predictions.Count}",
            $"- Skipped missing: {report.SkippedMissing}",
            $"- Failed: {report.FailedItems}",
            "",
            "## Issues",
            "",
            "|scenario|status|issues|",
            "|---|---|---|"
        };
        foreach (var item in report.Items.Where(item => item.Issues.Count > 0 || item.Skipped))
        {
            var status = item.Skipped ? "skipped" : item.Passed ? "passed" : "failed";
            lines.Add($"|{item.ScenarioId}|{status}|{string.Join("<br>", item.Issues)}|");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static IEnumerable<EcdCwrSimulationWorkItem> SelectWorkItems(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        EcdCwrSimulationPredictionReplayOptions options)
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

    private static IEnumerable<EcdCwrSimulationWorkItem> SelectWorkItems(
        IReadOnlyList<EcdCwrSimulationWorkItem> workItems,
        EcdCwrFaultDictionaryPolicyBenchmarkOptions options)
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

    private static EcdCwrSimulationPredictionReplayItem ReplayItem(
        EcdCwrSimulationWorkItem item,
        EcdCwrSimulationPredictionReplayOptions options)
    {
        var issues = new List<string>();
        if (!File.Exists(item.OutputHdf5Path))
        {
            issues.Add("missing result HDF5");
            return new EcdCwrSimulationPredictionReplayItem(
                item.ScenarioId,
                item.OutputHdf5Path,
                options.SkipMissingResults,
                issues,
                null);
        }

        try
        {
            using var file = Hdf5FileAccess.OpenReadWithRetry(item.OutputHdf5Path);
            var raw = ReadComplexMatrix(file, "/raw_complex_256");
            var reference = ReadComplexMatrix(file, "/reference_complex_256");
            var contactSubspace = options.DiagnosticProfile == EcdCwrDiagnosticReplayProfile.EcdCwrCurrent
                ? AnalyzeContactSubspace(file, raw, reference)
                : null;
            var multiFrequency = options.DiagnosticProfile == EcdCwrDiagnosticReplayProfile.EcdCwrCurrent
                ? BuildMultiFrequencyEvidence(file)
                : EcdCwrSimulationMultiFrequencyReplayEvidence.Empty;
            var baseline = ElectrodeContactBaseline.FromReference(reference.Real, reference.Imaginary);
            var monitor = new ElectrodeContactMonitor(
                baseline,
                new ElectrodeContactMonitorOptions
                {
                    EwmaRise = 1.0,
                    EwmaFall = 1.0
                });
            monitor.Update(
                raw.Real,
                raw.Imaginary,
                primaryFrequencyHz: multiFrequency.PrimaryFrequencyHz,
                peerFrequencyEvidence: multiFrequency.PeerFrames);
            var result = monitor.Update(
                raw.Real,
                raw.Imaginary,
                primaryFrequencyHz: multiFrequency.PrimaryFrequencyHz,
                peerFrequencyEvidence: multiFrequency.PeerFrames);
            var contactSubspaceDiscriminantScore = CalculateContactSubspaceDiscriminantScore(
                contactSubspace?.ContactSubspaceScore,
                result.CandidateScores ?? result.Scores);
            var prediction = new EcdCwrSimulationPrediction(
                item.ScenarioId,
                result.States,
                result.FaultTypes,
                result.Scores,
                result.SystemLevel,
                contactSubspace?.ContactSubspaceScore,
                ContactSubspaceDiscriminantScore: contactSubspaceDiscriminantScore,
                ContactSubspaceProjectedNorm: contactSubspace?.ProjectedNorm,
                ContactSubspaceResidualNorm: contactSubspace?.ResidualNorm,
                ContactSubspaceCoefficients: contactSubspace?.ContactCoefficients,
                ImageQualityScore: result.ImageQualityScore,
                MultiFrequencyPrimaryHz: multiFrequency.PrimaryFrequencyHz,
                MultiFrequencyPeerFrameCount: multiFrequency.PeerFrames.Count,
                DiagnosticPolicyVersion: EcdCwrDiagnosticPolicy.ForProfile(options.DiagnosticProfile),
                CandidateScores: result.CandidateScores,
                CandidateFaultTypes: result.CandidateFaultTypes,
                CandidateEvidenceKinds: result.CandidateEvidenceKinds,
                CandidateReasons: result.CandidateReasons,
                PhysicalFieldGuardApplied: result.PhysicalFieldGuardApplied);
            return new EcdCwrSimulationPredictionReplayItem(
                item.ScenarioId,
                item.OutputHdf5Path,
                Skipped: false,
                issues,
                prediction);
        }
        catch (Exception ex)
        {
            issues.Add($"replay failed: {ex.Message}");
            return new EcdCwrSimulationPredictionReplayItem(
                item.ScenarioId,
                item.OutputHdf5Path,
                Skipped: false,
                issues,
                null);
        }
    }

    private static double? CalculateContactSubspaceDiscriminantScore(
        double? projectionRatio,
        IReadOnlyList<double>? electrodeScores)
    {
        if (projectionRatio is not { } ratio || !double.IsFinite(ratio))
        {
            return null;
        }

        var maxScore = electrodeScores?
            .Where(score => double.IsFinite(score))
            .Select(score => Math.Max(0.0, score))
            .DefaultIfEmpty(0.0)
            .Max() ?? 0.0;
        var structuredEvidence = maxScore <= 0.0 ? 0.0 : maxScore / (maxScore + 1.0);
        return Math.Clamp(ratio, 0.0, 1.0) * structuredEvidence;
    }

    private static EcdCwrSimulationMultiFrequencyReplayEvidence BuildMultiFrequencyEvidence(IH5Group file)
    {
        if (!file.LinkExists("/frequency_hz"))
        {
            return EcdCwrSimulationMultiFrequencyReplayEvidence.Empty;
        }

        var frequencies = ReadDoubleVector(file, "/frequency_hz");
        if (frequencies.Length < 2)
        {
            return EcdCwrSimulationMultiFrequencyReplayEvidence.Empty;
        }

        var rawFrames = ReadComplexMatrixStack(file, "/frequency_raw_complex_256", frequencies.Length, 16, 16);
        var referenceFrames = ReadComplexMatrixStack(file, "/frequency_reference_complex_256", frequencies.Length, 16, 16);
        var evidenceFrames = new List<EcdCwrFrequencyEvidenceFrame>(frequencies.Length);
        for (var index = 0; index < frequencies.Length; index++)
        {
            var baseline = ElectrodeContactBaseline.FromReference(
                referenceFrames[index].Real,
                referenceFrames[index].Imaginary);
            var monitor = new ElectrodeContactMonitor(
                baseline,
                new ElectrodeContactMonitorOptions
                {
                    EwmaRise = 1.0,
                    EwmaFall = 1.0
                });
            var result = monitor.Update(rawFrames[index].Real, rawFrames[index].Imaginary);
            evidenceFrames.Add(new EcdCwrFrequencyEvidenceFrame(frequencies[index], result.Scores));
        }

        return new EcdCwrSimulationMultiFrequencyReplayEvidence(
            frequencies[0],
            evidenceFrames.Skip(1).ToArray());
    }

    private static EcdCwrContactSubspaceResult? AnalyzeContactSubspace(
        IH5Group file,
        ComplexMatrix raw,
        ComplexMatrix reference)
    {
        const string contactJacobianPath = "/contact_jacobian_208x16";
        if (!file.LinkExists(contactJacobianPath))
        {
            return null;
        }

        var contactJacobian = ReadComplexMatrix(file, contactJacobianPath, 208, 16);
        var target208 = ReadRetainedVectorOrBuild(file, "/retained_complex_208", raw);
        var reference208 = ReadRetainedVectorOrBuild(file, "/reference_retained_complex_208", reference);
        var delta = StackComplexDelta(target208, reference208);
        var stackedJacobian = StackComplexMatrix(contactJacobian);
        return new EcdCwrContactSubspaceAnalyzer().Analyze(delta, stackedJacobian);
    }

    private static ComplexVector ReadRetainedVectorOrBuild(
        IH5Group file,
        string datasetPath,
        ComplexMatrix matrix)
    {
        return file.LinkExists(datasetPath)
            ? ReadComplexVector(file, datasetPath, 208)
            : BuildRetainedVector(matrix);
    }

    private static double[] StackComplexDelta(
        ComplexVector target,
        ComplexVector reference)
    {
        var output = new double[416];
        for (var index = 0; index < 208; index++)
        {
            output[index] = target.Real[index] - reference.Real[index];
            output[index + 208] = target.Imaginary[index] - reference.Imaginary[index];
        }

        return output;
    }

    private static double[,] StackComplexMatrix(ComplexMatrix matrix)
    {
        var output = new double[416, 16];
        for (var row = 0; row < 208; row++)
        {
            for (var column = 0; column < 16; column++)
            {
                output[row, column] = matrix.Real[row, column];
                output[row + 208, column] = matrix.Imaginary[row, column];
            }
        }

        return output;
    }

    private static ComplexVector BuildRetainedVector(ComplexMatrix matrix)
    {
        var real = new double[208];
        var imaginary = new double[208];
        var index = 0;
        for (var stimulation = 0; stimulation < 16; stimulation++)
        {
            for (var relative = 2; relative <= 14; relative++)
            {
                var measurement = Mod(stimulation + relative);
                real[index] = matrix.Real[stimulation, measurement];
                imaginary[index] = matrix.Imaginary[stimulation, measurement];
                index++;
            }
        }

        return new ComplexVector(real, imaginary);
    }

    private static ComplexMatrix ReadComplexMatrix(
        IH5Group file,
        string datasetPath,
        int expectedRows = 16,
        int expectedColumns = 16)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (!dimensions.SequenceEqual([(ulong)expectedRows, (ulong)expectedColumns]))
        {
            throw new InvalidDataException($"{datasetPath} shape must be ({expectedRows},{expectedColumns}).");
        }

        if (dataset.Type.Class == H5DataTypeClass.Compound &&
            TryRead(() => dataset.Read<Hdf5Complex64[,]>(memoryDims: dimensions), out var complex64))
        {
            return ConvertComplex64(complex64, expectedRows, expectedColumns);
        }

        if (TryRead(() => dataset.Read<double[,]>(memoryDims: dimensions), out var doubleReal))
        {
            return new ComplexMatrix(doubleReal, new double[expectedRows, expectedColumns]);
        }

        if (TryRead(() => dataset.Read<float[,]>(memoryDims: dimensions), out var floatReal))
        {
            return new ComplexMatrix(ConvertReal(floatReal, expectedRows, expectedColumns), new double[expectedRows, expectedColumns]);
        }

        throw new InvalidDataException($"Unsupported complex dataset type at {datasetPath}.");
    }

    private static ComplexMatrix[] ReadComplexMatrixStack(
        IH5Group file,
        string datasetPath,
        int expectedDepth,
        int expectedRows,
        int expectedColumns)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (!dimensions.SequenceEqual([(ulong)expectedDepth, (ulong)expectedRows, (ulong)expectedColumns]))
        {
            throw new InvalidDataException(
                $"{datasetPath} shape must be ({expectedDepth},{expectedRows},{expectedColumns}).");
        }

        if (dataset.Type.Class == H5DataTypeClass.Compound &&
            TryRead(() => dataset.Read<Hdf5Complex64[,,]>(memoryDims: dimensions), out var complex64))
        {
            return ConvertComplex64(complex64, expectedDepth, expectedRows, expectedColumns);
        }

        if (TryRead(() => dataset.Read<double[,,]>(memoryDims: dimensions), out var doubleReal))
        {
            return ConvertReal(doubleReal, expectedDepth, expectedRows, expectedColumns);
        }

        if (TryRead(() => dataset.Read<float[,,]>(memoryDims: dimensions), out var floatReal))
        {
            return ConvertReal(floatReal, expectedDepth, expectedRows, expectedColumns);
        }

        throw new InvalidDataException($"Unsupported complex matrix stack type at {datasetPath}.");
    }

    private static ComplexVector ReadComplexVector(
        IH5Group file,
        string datasetPath,
        int expectedLength)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (!dimensions.SequenceEqual([(ulong)expectedLength]))
        {
            throw new InvalidDataException($"{datasetPath} shape must be ({expectedLength}).");
        }

        if (dataset.Type.Class == H5DataTypeClass.Compound &&
            TryRead(() => dataset.Read<Hdf5Complex64[]>(memoryDims: dimensions), out var complex64))
        {
            return ConvertComplex64(complex64, expectedLength);
        }

        if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubleReal))
        {
            return new ComplexVector(doubleReal, new double[expectedLength]);
        }

        if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var floatReal))
        {
            return new ComplexVector(ConvertReal(floatReal, expectedLength), new double[expectedLength]);
        }

        throw new InvalidDataException($"Unsupported complex vector dataset type at {datasetPath}.");
    }

    private static double[] ReadDoubleVector(IH5Group file, string datasetPath)
    {
        if (!file.LinkExists(datasetPath))
        {
            throw new InvalidDataException($"Missing dataset {datasetPath}.");
        }

        var dataset = file.Dataset(datasetPath);
        var dimensions = dataset.Space.Dimensions;
        if (dimensions.Length != 1 || dimensions[0] == 0)
        {
            throw new InvalidDataException($"{datasetPath} must be a non-empty vector.");
        }

        if (TryRead(() => dataset.Read<double[]>(memoryDims: dimensions), out var doubleValues))
        {
            return doubleValues;
        }

        if (TryRead(() => dataset.Read<float[]>(memoryDims: dimensions), out var floatValues))
        {
            return floatValues.Select(value => (double)value).ToArray();
        }

        throw new InvalidDataException($"Unsupported numeric vector type at {datasetPath}.");
    }

    private static ComplexMatrix ConvertComplex64(
        Hdf5Complex64[,] values,
        int rows,
        int columns)
    {
        var real = new double[rows, columns];
        var imaginary = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                real[row, column] = values[row, column].Real;
                imaginary[row, column] = values[row, column].Imaginary;
            }
        }

        return new ComplexMatrix(real, imaginary);
    }

    private static ComplexVector ConvertComplex64(
        Hdf5Complex64[] values,
        int length)
    {
        var real = new double[length];
        var imaginary = new double[length];
        for (var index = 0; index < length; index++)
        {
            real[index] = values[index].Real;
            imaginary[index] = values[index].Imaginary;
        }

        return new ComplexVector(real, imaginary);
    }

    private static ComplexMatrix[] ConvertComplex64(
        Hdf5Complex64[,,] values,
        int depth,
        int rows,
        int columns)
    {
        var output = new ComplexMatrix[depth];
        for (var frame = 0; frame < depth; frame++)
        {
            var real = new double[rows, columns];
            var imaginary = new double[rows, columns];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    real[row, column] = values[frame, row, column].Real;
                    imaginary[row, column] = values[frame, row, column].Imaginary;
                }
            }

            output[frame] = new ComplexMatrix(real, imaginary);
        }

        return output;
    }

    private static double[,] ConvertReal(
        float[,] values,
        int rows,
        int columns)
    {
        var real = new double[rows, columns];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                real[row, column] = values[row, column];
            }
        }

        return real;
    }

    private static double[] ConvertReal(
        float[] values,
        int length)
    {
        var real = new double[length];
        for (var index = 0; index < length; index++)
        {
            real[index] = values[index];
        }

        return real;
    }

    private static ComplexMatrix[] ConvertReal(
        double[,,] values,
        int depth,
        int rows,
        int columns)
    {
        var output = new ComplexMatrix[depth];
        for (var frame = 0; frame < depth; frame++)
        {
            var real = new double[rows, columns];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    real[row, column] = values[frame, row, column];
                }
            }

            output[frame] = new ComplexMatrix(real, new double[rows, columns]);
        }

        return output;
    }

    private static ComplexMatrix[] ConvertReal(
        float[,,] values,
        int depth,
        int rows,
        int columns)
    {
        var output = new ComplexMatrix[depth];
        for (var frame = 0; frame < depth; frame++)
        {
            var real = new double[rows, columns];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    real[row, column] = values[frame, row, column];
                }
            }

            output[frame] = new ComplexMatrix(real, new double[rows, columns]);
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

    private readonly record struct ComplexMatrix(double[,] Real, double[,] Imaginary);

    private readonly record struct ComplexVector(double[] Real, double[] Imaginary);

    private sealed record EcdCwrSimulationMultiFrequencyReplayEvidence(
        double? PrimaryFrequencyHz,
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame> PeerFrames)
    {
        public static EcdCwrSimulationMultiFrequencyReplayEvidence Empty { get; } = new(null, []);
    }

#pragma warning disable CS0649
    private struct Hdf5Complex64
    {
        [H5Name("r")]
        public float Real;

        [H5Name("i")]
        public float Imaginary;
    }
#pragma warning restore CS0649
}

public sealed record EcdCwrSimulationPredictionReplayOptions
{
    public int StartIndex { get; init; }

    public int? Limit { get; init; }

    public bool SkipMissingResults { get; init; }

    public EcdCwrDiagnosticReplayProfile DiagnosticProfile { get; init; } =
        EcdCwrDiagnosticReplayProfile.EcdCwrCurrent;

    public IReadOnlySet<string> ScenarioIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public enum EcdCwrDiagnosticReplayProfile
{
    EcdCwrCurrent = 0,
    P2Baseline = 1
}

public sealed record EcdCwrSimulationPredictionReplayReport(
    DateTimeOffset ReplayedAt,
    EcdCwrDiagnosticReplayProfile DiagnosticProfile,
    int ManifestWorkItemCount,
    int SelectedItems,
    int SkippedMissing,
    int FailedItems,
    IReadOnlyList<EcdCwrSimulationPrediction> Predictions,
    IReadOnlyList<EcdCwrSimulationPredictionReplayItem> Items,
    string? DiagnosticPolicyVersion = null)
{
    public bool Passed => FailedItems == 0;
}

public sealed record EcdCwrSimulationPredictionReplayItem(
    string ScenarioId,
    string OutputHdf5Path,
    bool Skipped,
    IReadOnlyList<string> Issues,
    EcdCwrSimulationPrediction? Prediction)
{
    public bool Passed => !Skipped && Issues.Count == 0 && Prediction is not null;
}
