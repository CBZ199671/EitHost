namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrContactSubspaceAnalyzer
{
    public EcdCwrContactSubspaceResult Analyze(
        IReadOnlyList<double> deltaVoltage,
        double[,] contactJacobian,
        EcdCwrContactSubspaceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(deltaVoltage);
        ArgumentNullException.ThrowIfNull(contactJacobian);
        options ??= new EcdCwrContactSubspaceOptions();
        var measurementCount = contactJacobian.GetLength(0);
        var electrodeCount = contactJacobian.GetLength(1);
        if (electrodeCount != 16)
        {
            throw new ArgumentException("Contact subspace analyzer expects J_z with 16 electrode columns.", nameof(contactJacobian));
        }

        if (deltaVoltage.Count != measurementCount)
        {
            throw new ArgumentException("Delta voltage length must match J_z measurement rows.", nameof(deltaVoltage));
        }

        if (measurementCount == 0)
        {
            return new EcdCwrContactSubspaceResult(0.0, 0.0, 0.0, new double[16], []);
        }

        var eta = ResolveEta(contactJacobian, options);
        var normal = new double[electrodeCount, electrodeCount];
        var rhs = new double[electrodeCount];
        for (var row = 0; row < measurementCount; row++)
        {
            var value = Sanitize(deltaVoltage[row]);
            for (var left = 0; left < electrodeCount; left++)
            {
                var jLeft = contactJacobian[row, left];
                rhs[left] += jLeft * value;
                for (var right = 0; right < electrodeCount; right++)
                {
                    normal[left, right] += jLeft * contactJacobian[row, right];
                }
            }
        }

        for (var index = 0; index < electrodeCount; index++)
        {
            normal[index, index] += eta;
        }

        var coefficients = SolveSmallLinearSystem(normal, rhs);
        var projected = new double[measurementCount];
        for (var row = 0; row < measurementCount; row++)
        {
            var sum = 0.0;
            for (var column = 0; column < electrodeCount; column++)
            {
                sum += contactJacobian[row, column] * coefficients[column];
            }

            projected[row] = sum;
        }

        var deltaNorm = Norm(deltaVoltage);
        var projectedNorm = Norm(projected);
        var residualNorm = ResidualNorm(deltaVoltage, projected);
        var score = deltaNorm <= options.Epsilon ? 0.0 : projectedNorm / (deltaNorm + options.Epsilon);
        return new EcdCwrContactSubspaceResult(
            Math.Clamp(score, 0.0, 1.0),
            projectedNorm,
            residualNorm,
            coefficients,
            projected);
    }

    private static double ResolveEta(double[,] contactJacobian, EcdCwrContactSubspaceOptions options)
    {
        if (options.Eta is { } explicitEta)
        {
            return Math.Max(0.0, explicitEta);
        }

        var electrodeCount = contactJacobian.GetLength(1);
        var trace = 0.0;
        for (var row = 0; row < contactJacobian.GetLength(0); row++)
        {
            for (var column = 0; column < electrodeCount; column++)
            {
                trace += contactJacobian[row, column] * contactJacobian[row, column];
            }
        }

        return Math.Max(0.0, options.EtaTraceScale) * trace / electrodeCount;
    }

    private static double Norm(IEnumerable<double> values)
    {
        return Math.Sqrt(values.Sum(value => Sanitize(value) * Sanitize(value)));
    }

    private static double ResidualNorm(IReadOnlyList<double> values, IReadOnlyList<double> projected)
    {
        var sum = 0.0;
        for (var index = 0; index < values.Count; index++)
        {
            var residual = Sanitize(values[index]) - Sanitize(projected[index]);
            sum += residual * residual;
        }

        return Math.Sqrt(sum);
    }

    private static double Sanitize(double value)
    {
        return double.IsFinite(value) ? value : 0.0;
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
}

public sealed record EcdCwrContactSubspaceOptions(
    double? Eta = null,
    double EtaTraceScale = 1.0e-3,
    double Epsilon = 1.0e-12);

public sealed record EcdCwrContactSubspaceResult(
    double ContactSubspaceScore,
    double ProjectedNorm,
    double ResidualNorm,
    double[] ContactCoefficients,
    double[] ProjectedDeltaVoltage);
