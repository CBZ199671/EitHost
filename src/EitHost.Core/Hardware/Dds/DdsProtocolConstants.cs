namespace EitHost.Core.Hardware.Dds;

public static class DdsProtocolConstants
{
    public const byte FrameHeader = 0xAA;
    public const byte ResponseFrameHeader = 0x55;
    public const byte ProtocolVersion = 0x02;
    public const int BaudRate = 115200;
    public const int DataBits = 8;
    public const int ResponseFrameOverhead = 6;
    public const int MaximumResponsePayloadLength = 32;
    public const uint TimerClockHz = 921_600;
    public const uint MinimumExcitationTimeUs = 2_000;
    public const uint MaximumExcitationTimeUs = 71_110;
    public const ushort RequiredFeatureFlags = 0x000F;
    public const ushort ScanStatusFeatureFlag = 0x0010;
    public const string SwitchGuardSemantics = "guaranteed_minimum";
    public const double DdsSystemClockHz = 30_000_000.0;
    public const double DdsPhaseAccumulatorScale = 16_777_216.0;
    public const double FrequencyTuningWordScale = DdsPhaseAccumulatorScale / DdsSystemClockHz;
    public const double DefaultExcitationChannelCycles = 20.0;
}
