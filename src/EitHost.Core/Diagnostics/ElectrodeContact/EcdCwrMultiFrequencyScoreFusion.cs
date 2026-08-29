namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrMultiFrequencyScoreFusion
{
    private const int ElectrodeCount = 16;

    public EcdCwrMultiFrequencyScoreFusionResult Fuse(
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame> frames,
        EcdCwrMultiFrequencyScoreFusionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= new EcdCwrMultiFrequencyScoreFusionOptions();
        if (frames.Count < 2)
        {
            throw new ArgumentException("Multi-frequency score fusion requires at least two frequency frames.", nameof(frames));
        }

        var ordered = frames.OrderBy(frame => frame.FrequencyHz).ToArray();
        foreach (var frame in ordered)
        {
            if (frame.ElectrodeMagnitudeScores.Count != ElectrodeCount)
            {
                throw new ArgumentException("Each frequency frame must contain 16 electrode scores.", nameof(frames));
            }
        }

        var consistency = new EcdCwrMultiFrequencyConsistencyAnalyzer().Analyze(
            ordered,
            new EcdCwrMultiFrequencyConsistencyOptions(
                options.ActiveMagnitudeThreshold,
                options.MinimumActiveFraction,
                options.MaxContactLikelihoodBoost,
                options.Epsilon));
        var primaryIndex = ResolvePrimaryFrequencyIndex(ordered, options.PrimaryFrequencyHz);
        var baseScores = new double[ElectrodeCount];
        var fusedScores = new double[ElectrodeCount];
        var dominantFrequencyIndex = new int[ElectrodeCount];
        for (var electrode = 0; electrode < ElectrodeCount; electrode++)
        {
            var bestScore = 0.0;
            var bestIndex = 0;
            for (var frequencyIndex = 0; frequencyIndex < ordered.Length; frequencyIndex++)
            {
                var score = SanitizeScore(ordered[frequencyIndex].ElectrodeMagnitudeScores[electrode]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = frequencyIndex;
                }
            }

            baseScores[electrode] = primaryIndex is { } primary
                ? SanitizeScore(ordered[primary].ElectrodeMagnitudeScores[electrode])
                : bestScore;
            dominantFrequencyIndex[electrode] = bestIndex;
            fusedScores[electrode] = Math.Min(
                Math.Max(0.0, options.MaxFusedScore),
                baseScores[electrode] * consistency.ContactLikelihoodMultiplier[electrode]);
        }

        return new EcdCwrMultiFrequencyScoreFusionResult(
            consistency,
            baseScores,
            fusedScores,
            dominantFrequencyIndex);
    }

    private static double SanitizeScore(double value)
    {
        return double.IsFinite(value) ? Math.Max(0.0, value) : 0.0;
    }

    private static int? ResolvePrimaryFrequencyIndex(
        IReadOnlyList<EcdCwrFrequencyEvidenceFrame> ordered,
        double? primaryFrequencyHz)
    {
        if (primaryFrequencyHz is not { } frequency || !double.IsFinite(frequency) || frequency <= 0.0)
        {
            return null;
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            if (Math.Abs(ordered[index].FrequencyHz - frequency) <= 1.0e-9)
            {
                return index;
            }
        }

        return null;
    }
}

public sealed record EcdCwrMultiFrequencyScoreFusionOptions(
    double ActiveMagnitudeThreshold = 2.0,
    double MinimumActiveFraction = 0.5,
    double MaxContactLikelihoodBoost = 1.0,
    double MaxFusedScore = double.PositiveInfinity,
    double Epsilon = 1.0e-9,
    double? PrimaryFrequencyHz = null);

public sealed record EcdCwrMultiFrequencyScoreFusionResult(
    EcdCwrMultiFrequencyConsistencyResult Consistency,
    double[] BaseScores,
    double[] FusedScores,
    int[] DominantFrequencyIndex);
