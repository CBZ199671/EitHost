using EitHost.Core.Demodulation;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Acquisition;

public static class Pseudo3dAcquisitionProfile
{
    public const string Version = "plant-dual-tdm-10000-20-8-4-3frames-v2";
    public const int FrequencyHz = 10000;
    public const double CurrentUa = 10;
    public const double ChannelCycles = 20;
    public const int SampleRateHz = 200000;
    public const int FramesPerBlock = 3;
    public const int ScanFrames = 3;
    public const double TargetVolumesPerSecond = 3;
    public const double DiscardLeadingCycles = 8;
    public const double DiscardTrailingCycles = 4;
    public static TimeSpan HandoffGuard => TimeSpan.FromMilliseconds(20);
    public static TimeSpan SlotTimeout => TimeSpan.FromSeconds(3);
    public static TimeSpan MaximumPairSkew => TimeSpan.FromMilliseconds(1500);
    public static uint FrequencyTuningWord => DdsFrequencyPlan.CalculateTuningWord(FrequencyHz);
    public static double ActualFrequencyHz => DdsFrequencyPlan.CalculateActualFrequencyHz(FrequencyTuningWord);
    public static DdsDacSettings DacSettings => new(1, FrequencyHz, CurrentUa / 100, 0);
    public static DdsExcitationSettings ExcitationSettings => new(DdsExcitationMode.Adjacent, FrequencyHz, ChannelCycles, ScanFrames);
    public static Usb2070AcquisitionSettings AcquisitionSettings => new(
        SampleRateHz, Usb2070AdRange.Bipolar5V, Usb2070TriggerMode.Continue,
        Usb2070TriggerSource.Software, 0, 1024, 2048);

    public static RealtimeDemodulationSettings CreateDemodulationSettings(DdsExecutionReceipt receipt) => new(
        SampleRateHz, ActualFrequencyHz, receipt.CalculateEffectiveChannelCycles(ActualFrequencyHz),
        framesPerBlock: FramesPerBlock, minimumAcceptedFrames: FramesPerBlock,
        discardLeadingCycles: DiscardLeadingCycles, discardTrailingCycles: DiscardTrailingCycles,
        adRange: Usb2070AdRange.Bipolar5V);
}

public sealed record Pseudo3dAcquisitionStamp(
    Guid SessionId,
    long Round,
    int Slot,
    string Profile,
    uint FrequencyTuningWord,
    DateTimeOffset CaptureStartedAt,
    DateTimeOffset SampleMidpoint,
    double TimingUncertaintyMilliseconds,
    bool Qualified)
{
    public bool TrustedZeroDifference { get; init; }
}

public sealed record Pseudo3dSlotCapture(
    string SetLabel,
    ushort[,] Raw,
    int AnalysisStartRow,
    int AnalysisEndRow,
    DateTimeOffset CaptureStartedAt,
    DdsFirmwareCapabilities Capabilities,
    DdsExecutionReceipt Execution,
    DdsScanStatus CompletedStatus,
    RealtimeDemodulatedBlock Block)
{
    public Pseudo3dFiniteScanWindow? FiniteScan { get; init; }
}
