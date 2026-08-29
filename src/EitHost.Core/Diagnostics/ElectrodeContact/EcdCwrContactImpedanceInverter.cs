namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrContactImpedanceInverter
{
    private const int ElectrodeCount = 16;

    public EcdCwrContactImpedanceInversionResult Invert(
        EcdCwrEvidenceAResult evidenceA,
        EcdCwrContactImpedanceInverterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(evidenceA);
        var masks = BuildMasksFromSaturation(evidenceA.SaturatedPoints);
        return Invert(
            evidenceA.DriveScores,
            evidenceA.LeftSharedScores,
            evidenceA.RightSharedScores,
            masks.DriveMask,
            masks.LeftMask,
            masks.RightMask,
            options);
    }

    public EcdCwrContactImpedanceInversionResult Invert(
        IReadOnlyList<double> driveScores,
        IReadOnlyList<double> leftSharedScores,
        IReadOnlyList<double> rightSharedScores,
        IReadOnlyList<bool>? driveMask = null,
        IReadOnlyList<bool>? leftMask = null,
        IReadOnlyList<bool>? rightMask = null,
        EcdCwrContactImpedanceInverterOptions? options = null)
    {
        options ??= new EcdCwrContactImpedanceInverterOptions();
        ValidateVector(driveScores, nameof(driveScores));
        ValidateVector(leftSharedScores, nameof(leftSharedScores));
        ValidateVector(rightSharedScores, nameof(rightSharedScores));
        ValidateMask(driveMask, nameof(driveMask));
        ValidateMask(leftMask, nameof(leftMask));
        ValidateMask(rightMask, nameof(rightMask));

        var observations = BuildObservations(
            driveScores,
            leftSharedScores,
            rightSharedScores,
            driveMask,
            leftMask,
            rightMask);
        var (scores, driftScores, driftCoefficients) = options.DriftBasis == EcdCwrContactDriftBasis.None
            ? (SolveNonNegativeLasso(observations, options), new double[ElectrodeCount], [])
            : SolveNonNegativeLassoWithDrift(observations, options);
        var totalScores = Add(scores, driftScores);
        var predictedDrive = new double[ElectrodeCount];
        var predictedLeft = new double[ElectrodeCount];
        var predictedRight = new double[ElectrodeCount];
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            predictedDrive[stimulation] = totalScores[stimulation] + totalScores[Mod(stimulation + 1)];
            predictedLeft[stimulation] = totalScores[stimulation];
            predictedRight[stimulation] = totalScores[Mod(stimulation + 1)];
        }

        var residualNorm = CalculateResidualNorm(observations, totalScores);
        return new EcdCwrContactImpedanceInversionResult(
            scores,
            predictedDrive,
            predictedLeft,
            predictedRight,
            residualNorm,
            observations.Count,
            driftScores,
            driftCoefficients);
    }

    private static (bool[] DriveMask, bool[] LeftMask, bool[] RightMask) BuildMasksFromSaturation(
        IReadOnlyList<EcdCwrEvidenceAPoint> saturatedPoints)
    {
        var drive = Enumerable.Repeat(true, ElectrodeCount).ToArray();
        var left = Enumerable.Repeat(true, ElectrodeCount).ToArray();
        var right = Enumerable.Repeat(true, ElectrodeCount).ToArray();
        foreach (var point in saturatedPoints)
        {
            if (point.RelativeChannelIndex == 0)
            {
                drive[point.StimulationIndex] = false;
            }
            else if (point.RelativeChannelIndex == 15)
            {
                left[point.StimulationIndex] = false;
            }
            else if (point.RelativeChannelIndex == 1)
            {
                right[point.StimulationIndex] = false;
            }
        }

        return (drive, left, right);
    }

    private static IReadOnlyList<ContactObservation> BuildObservations(
        IReadOnlyList<double> driveScores,
        IReadOnlyList<double> leftSharedScores,
        IReadOnlyList<double> rightSharedScores,
        IReadOnlyList<bool>? driveMask,
        IReadOnlyList<bool>? leftMask,
        IReadOnlyList<bool>? rightMask)
    {
        var observations = new List<ContactObservation>(ElectrodeCount * 3);
        for (var stimulation = 0; stimulation < ElectrodeCount; stimulation++)
        {
            if (driveMask is null || driveMask[stimulation])
            {
                observations.Add(ContactObservation.Drive(stimulation, Sanitize(driveScores[stimulation])));
            }

            if (leftMask is null || leftMask[stimulation])
            {
                observations.Add(ContactObservation.LeftShared(stimulation, Sanitize(leftSharedScores[stimulation])));
            }

            if (rightMask is null || rightMask[stimulation])
            {
                observations.Add(ContactObservation.RightShared(stimulation, Sanitize(rightSharedScores[stimulation])));
            }
        }

        return observations;
    }

    private static double[] SolveNonNegativeLasso(
        IReadOnlyList<ContactObservation> observations,
        EcdCwrContactImpedanceInverterOptions options)
    {
        var scores = new double[ElectrodeCount];
        if (observations.Count == 0)
        {
            return scores;
        }

        var columnNorms = new double[ElectrodeCount];
        foreach (var observation in observations)
        {
            foreach (var electrode in observation.Electrodes)
            {
                columnNorms[electrode] += observation.Weight * observation.Weight;
            }
        }

        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            var maxChange = 0.0;
            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                if (columnNorms[electrode] <= double.Epsilon)
                {
                    continue;
                }

                var numerator = 0.0;
                foreach (var observation in observations)
                {
                    if (!observation.Contains(electrode))
                    {
                        continue;
                    }

                    var residualWithoutCurrent = observation.Value - Predict(observation, scores) + scores[electrode];
                    numerator += observation.Weight * observation.Weight * residualWithoutCurrent;
                }

                var next = Math.Max(0.0, (numerator - options.L1Penalty) / columnNorms[electrode]);
                maxChange = Math.Max(maxChange, Math.Abs(next - scores[electrode]));
                scores[electrode] = next;
            }

            if (maxChange <= options.ConvergenceTolerance)
            {
                break;
            }
        }

        return scores;
    }

    private static (double[] SparseScores, double[] DriftScores, double[] DriftCoefficients)
        SolveNonNegativeLassoWithDrift(
            IReadOnlyList<ContactObservation> observations,
            EcdCwrContactImpedanceInverterOptions options)
    {
        var sparseScores = new double[ElectrodeCount];
        var basis = BuildDriftBasis(options.DriftBasis);
        var driftCoefficients = new double[basis.GetLength(1)];
        if (observations.Count == 0 || driftCoefficients.Length == 0)
        {
            return (sparseScores, new double[ElectrodeCount], driftCoefficients);
        }

        var columnNorms = new double[ElectrodeCount];
        foreach (var observation in observations)
        {
            foreach (var electrode in observation.Electrodes)
            {
                columnNorms[electrode] += observation.Weight * observation.Weight;
            }
        }

        for (var iteration = 0; iteration < options.MaxIterations; iteration++)
        {
            var previousSparse = sparseScores.ToArray();
            var previousDrift = driftCoefficients.ToArray();
            driftCoefficients = FitDriftCoefficients(observations, sparseScores, basis, options.DriftRidge);

            for (var electrode = 0; electrode < ElectrodeCount; electrode++)
            {
                if (columnNorms[electrode] <= double.Epsilon)
                {
                    continue;
                }

                var numerator = 0.0;
                foreach (var observation in observations)
                {
                    if (!observation.Contains(electrode))
                    {
                        continue;
                    }

                    var residualWithoutCurrent =
                        observation.Value -
                        Predict(observation, sparseScores) -
                        PredictDrift(observation, basis, driftCoefficients) +
                        sparseScores[electrode];
                    numerator += observation.Weight * observation.Weight * residualWithoutCurrent;
                }

                sparseScores[electrode] = Math.Max(0.0, (numerator - options.L1Penalty) / columnNorms[electrode]);
            }

            var maxChange = MaxDelta(previousSparse, sparseScores);
            maxChange = Math.Max(maxChange, MaxDelta(previousDrift, driftCoefficients));
            if (maxChange <= options.ConvergenceTolerance)
            {
                break;
            }
        }

        return (sparseScores, EvaluateDriftScores(basis, driftCoefficients), driftCoefficients);
    }

    private static double[,] BuildDriftBasis(EcdCwrContactDriftBasis driftBasis)
    {
        var columnCount = driftBasis switch
        {
            EcdCwrContactDriftBasis.None => 0,
            EcdCwrContactDriftBasis.Constant => 1,
            EcdCwrContactDriftBasis.ConstantAndFirstHarmonic => 3,
            _ => throw new ArgumentException("Unsupported contact drift basis.", nameof(driftBasis))
        };
        var basis = new double[ElectrodeCount, columnCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            if (columnCount >= 1)
            {
                basis[electrode, 0] = 1.0;
            }

            if (columnCount == 3)
            {
                var angle = 2.0 * Math.PI * electrode / ElectrodeCount;
                basis[electrode, 1] = Math.Cos(angle);
                basis[electrode, 2] = Math.Sin(angle);
            }
        }

        return basis;
    }

    private static double[] FitDriftCoefficients(
        IReadOnlyList<ContactObservation> observations,
        IReadOnlyList<double> sparseScores,
        double[,] basis,
        double ridge)
    {
        var columnCount = basis.GetLength(1);
        var normal = new double[columnCount, columnCount];
        var rhs = new double[columnCount];
        foreach (var observation in observations)
        {
            var residual = observation.Value - Predict(observation, sparseScores);
            var features = DriftFeatures(observation, basis);
            var weightSquare = observation.Weight * observation.Weight;
            for (var row = 0; row < columnCount; row++)
            {
                rhs[row] += weightSquare * features[row] * residual;
                for (var column = 0; column < columnCount; column++)
                {
                    normal[row, column] += weightSquare * features[row] * features[column];
                }
            }
        }

        var ridgeScale = Math.Max(0.0, ridge);
        for (var index = 0; index < columnCount; index++)
        {
            normal[index, index] += ridgeScale;
        }

        return SolveSmallLinearSystem(normal, rhs);
    }

    private static double[] DriftFeatures(ContactObservation observation, double[,] basis)
    {
        var features = new double[basis.GetLength(1)];
        foreach (var electrode in observation.Electrodes)
        {
            for (var column = 0; column < features.Length; column++)
            {
                features[column] += basis[electrode, column];
            }
        }

        return features;
    }

    private static double PredictDrift(
        ContactObservation observation,
        double[,] basis,
        IReadOnlyList<double> coefficients)
    {
        var sum = 0.0;
        foreach (var electrode in observation.Electrodes)
        {
            for (var column = 0; column < coefficients.Count; column++)
            {
                sum += basis[electrode, column] * coefficients[column];
            }
        }

        return sum;
    }

    private static double[] EvaluateDriftScores(double[,] basis, IReadOnlyList<double> coefficients)
    {
        var scores = new double[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            for (var column = 0; column < coefficients.Count; column++)
            {
                scores[electrode] += basis[electrode, column] * coefficients[column];
            }
        }

        return scores;
    }

    private static double[] SolveSmallLinearSystem(double[,] matrix, IReadOnlyList<double> rhs)
    {
        var size = rhs.Count;
        var augmented = new double[size, size + 1];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                augmented[row, column] = matrix[row, column];
            }

            augmented[row, size] = rhs[row];
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var bestRow = pivot;
            var bestValue = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var value = Math.Abs(augmented[row, pivot]);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestRow = row;
                }
            }

            if (bestValue <= double.Epsilon)
            {
                continue;
            }

            if (bestRow != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (augmented[pivot, column], augmented[bestRow, column]) =
                        (augmented[bestRow, column], augmented[pivot, column]);
                }
            }

            var pivotValue = augmented[pivot, pivot];
            for (var column = pivot; column <= size; column++)
            {
                augmented[pivot, column] /= pivotValue;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = augmented[row, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    augmented[row, column] -= factor * augmented[pivot, column];
                }
            }
        }

        var solution = new double[size];
        for (var row = 0; row < size; row++)
        {
            solution[row] = augmented[row, size];
        }

        return solution;
    }

    private static double CalculateResidualNorm(IReadOnlyList<ContactObservation> observations, IReadOnlyList<double> scores)
    {
        if (observations.Count == 0)
        {
            return 0.0;
        }

        var sumSquares = observations.Sum(observation =>
        {
            var residual = observation.Value - Predict(observation, scores);
            return observation.Weight * observation.Weight * residual * residual;
        });
        return Math.Sqrt(sumSquares / observations.Count);
    }

    private static double Predict(ContactObservation observation, IReadOnlyList<double> scores)
    {
        var sum = 0.0;
        foreach (var electrode in observation.Electrodes)
        {
            sum += scores[electrode];
        }

        return sum;
    }

    private static void ValidateVector(IReadOnlyList<double> values, string name)
    {
        if (values.Count != ElectrodeCount)
        {
            throw new ArgumentException("Contact impedance inversion expects 16 values.", name);
        }
    }

    private static void ValidateMask(IReadOnlyList<bool>? values, string name)
    {
        if (values is not null && values.Count != ElectrodeCount)
        {
            throw new ArgumentException("Contact impedance inversion masks must contain 16 values.", name);
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

    private static double[] Add(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var output = new double[ElectrodeCount];
        for (var index = 0; index < ElectrodeCount; index++)
        {
            output[index] = left[index] + right[index];
        }

        return output;
    }

    private static double MaxDelta(IReadOnlyList<double> previous, IReadOnlyList<double> current)
    {
        var max = 0.0;
        for (var index = 0; index < previous.Count; index++)
        {
            max = Math.Max(max, Math.Abs(previous[index] - current[index]));
        }

        return max;
    }

    private sealed record ContactObservation(
        EcdCwrContactObservationKind Kind,
        int StimulationIndex,
        double Value,
        double Weight,
        int[] Electrodes)
    {
        public static ContactObservation Drive(int stimulationIndex, double value)
        {
            return new ContactObservation(
                EcdCwrContactObservationKind.Drive,
                stimulationIndex,
                value,
                1.0,
                [stimulationIndex, Mod(stimulationIndex + 1)]);
        }

        public static ContactObservation LeftShared(int stimulationIndex, double value)
        {
            return new ContactObservation(
                EcdCwrContactObservationKind.LeftShared,
                stimulationIndex,
                value,
                1.0,
                [stimulationIndex]);
        }

        public static ContactObservation RightShared(int stimulationIndex, double value)
        {
            return new ContactObservation(
                EcdCwrContactObservationKind.RightShared,
                stimulationIndex,
                value,
                1.0,
                [Mod(stimulationIndex + 1)]);
        }

        public bool Contains(int electrode)
        {
            return Electrodes.Contains(electrode);
        }
    }
}

public sealed record EcdCwrContactImpedanceInverterOptions(
    double L1Penalty = 0.05,
    int MaxIterations = 500,
    double ConvergenceTolerance = 1e-8,
    EcdCwrContactDriftBasis DriftBasis = EcdCwrContactDriftBasis.None,
    double DriftRidge = 1e-3);

public enum EcdCwrContactDriftBasis
{
    None = 0,
    Constant = 1,
    ConstantAndFirstHarmonic = 2
}

public enum EcdCwrContactObservationKind
{
    Drive = 0,
    LeftShared = 1,
    RightShared = 2
}

public sealed record EcdCwrContactImpedanceInversionResult(
    double[] ElectrodeScores,
    double[] PredictedDriveScores,
    double[] PredictedLeftSharedScores,
    double[] PredictedRightSharedScores,
    double ResidualRms,
    int ObservationCount,
    double[]? DriftElectrodeScores = null,
    double[]? DriftCoefficients = null);
