namespace EitHost.Core.Hardware.Dds;

public sealed record DdsTimingValidationResult(
    bool IsMatch,
    double ExpectedWindowSamples,
    double ObservedWindowSamples,
    double ToleranceSamples,
    string? IssueCode)
{
    public const string ExcitationTimingMismatch = nameof(ExcitationTimingMismatch);
}

public static class DdsTimingValidator
{
    public static DdsTimingValidationResult Validate(
        DdsExecutionReceipt execution,
        int sampleRateHz,
        double observedWindowSamples)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (sampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        }

        var expectedWindowSamples = execution.EffectiveTimeNs * sampleRateHz / 1_000_000_000.0;
        var toleranceSamples = Math.Max(1.0, expectedWindowSamples * 0.005);
        var isMatch = double.IsFinite(observedWindowSamples) &&
            observedWindowSamples > 0.0 &&
            Math.Abs(observedWindowSamples - expectedWindowSamples) <= toleranceSamples;
        return new DdsTimingValidationResult(
            isMatch,
            expectedWindowSamples,
            observedWindowSamples,
            toleranceSamples,
            isMatch ? null : DdsTimingValidationResult.ExcitationTimingMismatch);
    }
}
