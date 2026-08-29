namespace EitHost.Core.Hardware.Dds;

public static class DdsFrequencyPlan
{
    public static uint CalculateTuningWord(double requestedFrequencyHz)
    {
        if (!double.IsFinite(requestedFrequencyHz) || requestedFrequencyHz <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedFrequencyHz));
        }

        var tuningWord = Math.Truncate(
            requestedFrequencyHz * DdsProtocolConstants.DdsPhaseAccumulatorScale /
            DdsProtocolConstants.DdsSystemClockHz);
        return checked((uint)tuningWord);
    }

    public static double CalculateActualFrequencyHz(uint frequencyTuningWord) =>
        frequencyTuningWord * DdsProtocolConstants.DdsSystemClockHz /
        DdsProtocolConstants.DdsPhaseAccumulatorScale;
}
