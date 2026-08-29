namespace EitHost.Core.Demodulation;

public enum DemodulatedWindowQualityState
{
    Valid = 0,
    Corrected = 1,
    Rejected = 2
}

public enum DemodulatedWindowRejectReason
{
    None = 0,
    Top3NotContiguous = 1,
    ExpectedReferenceNotInTop3 = 2,
    WeakPeakToBackground = 3,
    AdcSaturation = 4,
    WeakReference = 5
}

public sealed record DemodulatedWindowQuality(
    int WindowIndex,
    int ExpectedReferenceChannel,
    int DetectedTop1Channel,
    int TripletCenterChannel,
    int[] Top3Channels,
    bool Top3Contiguous,
    bool Top1IsTripletCenter,
    DemodulatedWindowQualityState State,
    DemodulatedWindowRejectReason RejectReason,
    double PeakToBackgroundRatio,
    int AdcSaturationCount)
{
    public bool Corrected => State == DemodulatedWindowQualityState.Corrected;

    public bool Rejected => State == DemodulatedWindowQualityState.Rejected;
}
