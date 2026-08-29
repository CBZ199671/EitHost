using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels;

internal sealed record DeviceRunParameterProfile(
    int DdsDacChannel,
    int DdsFrequencyHz,
    double DdsGain,
    int DdsPhaseDegrees,
    int DdsPgaGain,
    DdsExcitationMode ExcitationMode,
    double ExcitationChannelCycles,
    double DemodDiscardLeadingCycles,
    double DemodDiscardTrailingCycles,
    int ExcitationScanTimes,
    int ExcitationOverheadUs,
    int AcquisitionSampleRateHz,
    Usb2070AdRange AcquisitionRange,
    Usb2070TriggerMode AcquisitionTriggerMode,
    Usb2070TriggerSource AcquisitionTriggerSource,
    int AcquisitionTriggerDelay,
    int AcquisitionTriggerLength,
    int AcquisitionTriggerLevel,
    int AcquisitionReadSampleRows,
    int RealtimeFramesPerBlock,
    int RealtimeMinimumAcceptedFrames,
    double RealtimeMeshSize,
    double RealtimeDifferenceLambda,
    string RealtimeStorageMode,
    bool RealtimeSaveReconstructionResults,
    bool RealtimeEnableOutlierDetection,
    bool RealtimeEnableOutlierCompensation,
    bool RealtimeEnableTemporalDespiking,
    bool RealtimeEnableDynamicKalman,
    string RealtimeDynamicKalmanMode,
    string RealtimeReconstructionRoute,
    bool RealtimeUseCustomLambda,
    bool RealtimeUseFrequencyDivisionLockIn,
    string RealtimeDifferenceOrientation,
    string RealtimeReferenceScalePolicy)
{
    internal Usb2070AcquisitionSettings CreateAcquisitionSettings() =>
        new(
            AcquisitionSampleRateHz,
            AcquisitionRange,
            AcquisitionTriggerMode,
            AcquisitionTriggerSource,
            AcquisitionTriggerDelay,
            AcquisitionTriggerLength,
            AcquisitionTriggerLevel);

    internal Hdf5ExcitationMetadata CreateExcitationMetadata() =>
        new(
            new DdsDacSettings(
                checked((byte)DdsDacChannel),
                DdsFrequencyHz,
                DdsGain,
                DdsPhaseDegrees),
            new DdsExcitationSettings(
                ExcitationMode,
                DdsFrequencyHz,
                ExcitationChannelCycles,
                ExcitationScanTimes),
            checked((byte)DdsPgaGain));

    internal bool TryValidateDemodDiscardCycles(out string? message)
    {
        if (DdsFrequencyHz <= 0)
        {
            message = "DDS 频率必须大于 0 Hz。";
            return false;
        }

        if (ExcitationChannelCycles <= 0)
        {
            message = "激励周期数必须大于 0。";
            return false;
        }

        if (!DdsExcitationSettings.TryCalculateTimeUs(
                DdsFrequencyHz,
                ExcitationChannelCycles,
                out _))
        {
            var requestedDwellUs = ExcitationChannelCycles * 1_000_000.0 / DdsFrequencyHz;
            message =
                $"激励驻留 {requestedDwellUs:0.###} us 超出固件范围 {DdsProtocolConstants.MinimumExcitationTimeUs}-{DdsProtocolConstants.MaximumExcitationTimeUs} us。";
            return false;
        }

        if (ExcitationScanTimes < 0)
        {
            message = "扫描圈数不能为负数；0 表示连续扫描。";
            return false;
        }

        if (DemodDiscardLeadingCycles < 0 || DemodDiscardTrailingCycles < 0)
        {
            message = "解调丢弃周期不能为负数。";
            return false;
        }

        if (DemodDiscardLeadingCycles + DemodDiscardTrailingCycles >= ExcitationChannelCycles)
        {
            message =
                $"解调丢弃周期设置无效：前丢弃 {DemodDiscardLeadingCycles:g} + 后丢弃 {DemodDiscardTrailingCycles:g} 必须小于总周期 {ExcitationChannelCycles:g}。";
            return false;
        }

        message = null;
        return true;
    }
}
