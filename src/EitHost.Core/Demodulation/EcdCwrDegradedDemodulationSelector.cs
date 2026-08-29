namespace EitHost.Core.Demodulation;

public sealed record EcdCwrDegradedDemodulationSelection(
    bool CanReconstruct,
    double[] TargetVoltage208,
    double[] MeasurementWeight208,
    int TrustedMeasurementCount,
    int EffectiveMeasurementCount,
    int DiagnosticMeasurementCount,
    int TrustedStimulationCount,
    int MaximumMissingStimulationGap,
    double ImageQualityCap,
    string WeightPolicyVersion,
    string Status);

public sealed class EcdCwrDegradedDemodulationSelector
{
    public const string PolicyVersion = "ecd-cwr-degraded-window-mask-v1";
    public const int MinimumTrustedMeasurementCount = 104;
    public const int MinimumEffectiveMeasurementCount = 64;
    public const int MinimumTrustedStimulationCount = 8;
    public const int MaximumAllowedMissingStimulationGap = 4;
    public const double MinimumEffectiveWeight = 0.05;
    public const double MaximumImageQualityCap = 0.60;

    public EcdCwrDegradedDemodulationSelection Select(
        RealtimeDemodulatedBlock block,
        IReadOnlyList<double> referenceVoltage208,
        IReadOnlyList<double>? contactWeight208 = null)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(referenceVoltage208);
        if (referenceVoltage208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            referenceVoltage208.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException(
                "Degraded demodulation requires a finite 208-point reference.",
                nameof(referenceVoltage208));
        }

        contactWeight208 ??= Enumerable.Repeat(1.0, DemodulatedFrame.FlattenedMeasurementCount).ToArray();
        if (contactWeight208.Count != DemodulatedFrame.FlattenedMeasurementCount ||
            contactWeight208.Any(weight => !double.IsFinite(weight) || weight < 0.0 || weight > 1.0))
        {
            throw new ArgumentException(
                "Contact weights must contain 208 finite values in [0, 1].",
                nameof(contactWeight208));
        }

        var target = referenceVoltage208.ToArray();
        var weights = new double[DemodulatedFrame.FlattenedMeasurementCount];
        var partial = block.TrustedPartialAverage;
        if (partial is null)
        {
            return CreateUnavailableSelection(block, target, weights, "健康窗部分平均不可用");
        }

        var partialAmplitude = partial.FlattenAmplitudesRowMajor();
        var partialCounts = partial.FlattenSampleCountsRowMajor();
        var maximumSampleCount = Math.Max(1, partial.MaximumSampleCount);
        var trustedRows = new bool[DemodulatedFrame.StimulationCount];
        var trustedMeasurementCount = 0;
        for (var stimulation = 0; stimulation < DemodulatedFrame.StimulationCount; stimulation++)
        {
            var rowTrusted = true;
            for (var column = 0; column < DemodulatedFrame.MeasurementsPerStimulation; column++)
            {
                var index = (stimulation * DemodulatedFrame.MeasurementsPerStimulation) + column;
                var value = partialAmplitude[index];
                var sampleCount = partialCounts[index];
                if (sampleCount <= 0 || !double.IsFinite(value))
                {
                    rowTrusted = false;
                    target[index] = referenceVoltage208[index];
                    weights[index] = 0.0;
                    continue;
                }

                trustedMeasurementCount++;
                target[index] = value;
                var support = Math.Clamp(sampleCount / (double)maximumSampleCount, 0.0, 1.0);
                weights[index] = support * contactWeight208[index];
            }

            trustedRows[stimulation] = rowTrusted;
        }

        var trustedStimulationCount = trustedRows.Count(trusted => trusted);
        var maximumMissingGap = FindMaximumMissingRingGap(trustedRows);
        var effectiveMeasurementCount = weights.Count(weight => weight >= MinimumEffectiveWeight);
        var diagnosticMeasurementCount = block.DiagnosticAverage?.FiniteMeasurementCount
            ?? trustedMeasurementCount;
        var canReconstruct = trustedMeasurementCount >= MinimumTrustedMeasurementCount &&
            effectiveMeasurementCount >= MinimumEffectiveMeasurementCount &&
            trustedStimulationCount >= MinimumTrustedStimulationCount &&
            maximumMissingGap <= MaximumAllowedMissingStimulationGap;
        var imageQualityCap = Math.Min(
            MaximumImageQualityCap,
            0.65 * effectiveMeasurementCount / DemodulatedFrame.FlattenedMeasurementCount);
        var status = canReconstruct
            ? $"降级重构可用：健康 {trustedMeasurementCount}/208，有效 {effectiveMeasurementCount}/208，激励行 {trustedStimulationCount}/16"
            : $"降级重构覆盖不足：健康 {trustedMeasurementCount}/208，有效 {effectiveMeasurementCount}/208，激励行 {trustedStimulationCount}/16，最大缺口 {maximumMissingGap}";
        var policy = $"{PolicyVersion}:trusted={trustedMeasurementCount}:effective={effectiveMeasurementCount}:rows={trustedStimulationCount}:gap={maximumMissingGap}";
        return new EcdCwrDegradedDemodulationSelection(
            canReconstruct,
            target,
            weights,
            trustedMeasurementCount,
            effectiveMeasurementCount,
            diagnosticMeasurementCount,
            trustedStimulationCount,
            maximumMissingGap,
            imageQualityCap,
            policy,
            status);
    }

    private static EcdCwrDegradedDemodulationSelection CreateUnavailableSelection(
        RealtimeDemodulatedBlock block,
        double[] target,
        double[] weights,
        string reason)
    {
        return new EcdCwrDegradedDemodulationSelection(
            CanReconstruct: false,
            target,
            weights,
            TrustedMeasurementCount: 0,
            EffectiveMeasurementCount: 0,
            DiagnosticMeasurementCount: block.DiagnosticAverage?.FiniteMeasurementCount ?? 0,
            TrustedStimulationCount: 0,
            MaximumMissingStimulationGap: DemodulatedFrame.StimulationCount,
            ImageQualityCap: 0.0,
            WeightPolicyVersion: $"{PolicyVersion}:unavailable",
            Status: $"降级重构覆盖不足：{reason}");
    }

    private static int FindMaximumMissingRingGap(IReadOnlyList<bool> trustedRows)
    {
        if (trustedRows.All(trusted => trusted))
        {
            return 0;
        }

        if (trustedRows.All(trusted => !trusted))
        {
            return trustedRows.Count;
        }

        var maximum = 0;
        for (var start = 0; start < trustedRows.Count; start++)
        {
            if (trustedRows[start])
            {
                continue;
            }

            var length = 0;
            while (length < trustedRows.Count && !trustedRows[(start + length) % trustedRows.Count])
            {
                length++;
            }

            maximum = Math.Max(maximum, length);
        }

        return maximum;
    }
}
