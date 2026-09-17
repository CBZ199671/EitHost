using EitHost.Core.Acquisition;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Reconstruction;

public static class Pseudo3dTimeDivisionContract
{
    public static void ValidateSource(LayeredPseudo3dSource source, int expectedSlot)
    {
        ValidateStamp(source.Result.TimeDivision, source.AcquiredAt, expectedSlot);
        if (source.Result.TimeDivision!.Profile != Pseudo3dAcquisitionProfile.Version)
            throw new InvalidDataException("实时伪三维需要当前分时方案的数据。");
        if (!source.Result.Succeeded || source.Result.BlockNumber != source.Result.TimeDivision!.Round)
            throw new InvalidDataException("分时重构结果与采集轮次不匹配。");
        source.Result.GetMeshIndexMetadata().ValidateForResult(source.Result.NodeCoords,
            source.Result.CellConnectivity, source.Result.Conductivity.Length, requireCanonical: true);
    }

    private static void ValidateStamp(Pseudo3dAcquisitionStamp? stamp, DateTimeOffset acquiredAt, int expectedSlot)
    {
        if (stamp is null || stamp.SessionId == Guid.Empty || stamp.Round < 1 || stamp.Slot != expectedSlot ||
            !((stamp.Profile == Pseudo3dAcquisitionProfile.Version && stamp.FrequencyTuningWord == Pseudo3dAcquisitionProfile.FrequencyTuningWord) ||
              (stamp.Profile == "plant-dual-tdm-3125-20-8-4-v1" && stamp.FrequencyTuningWord == 1747)) ||
            !stamp.Qualified || stamp.SampleMidpoint != acquiredAt ||
            stamp.CaptureStartedAt >= stamp.SampleMidpoint || !double.IsFinite(stamp.TimingUncertaintyMilliseconds) ||
            stamp.TimingUncertaintyMilliseconds is < 0 or > 50)
            throw new InvalidDataException("伪三维需要固定方案的高质量同频分时数据；独立连续二维数据不可配对。");
    }

    public static bool IsSameRound(Pseudo3dAcquisitionStamp? lower, Pseudo3dAcquisitionStamp? upper) =>
        lower is not null && upper is not null && lower.SessionId == upper.SessionId &&
        lower.Round == upper.Round && lower.Slot == 0 && upper.Slot == 1;

    public static void ValidatePair(LayeredPseudo3dSource lower, LayeredPseudo3dSource upper)
    {
        ValidateSource(lower, 0);
        ValidateSource(upper, 1);
        ValidateAcquisitionPair(lower.Result.TimeDivision, lower.AcquiredAt, upper.Result.TimeDivision, upper.AcquiredAt);
        var a = lower.Result;
        var b = upper.Result;
        if (a.GetMeshIndexMetadata() != b.GetMeshIndexMetadata() ||
            ReconstructionMeshFingerprint.Compute(a.NodeCoords, a.CellConnectivity) !=
            ReconstructionMeshFingerprint.Compute(b.NodeCoords, b.CellConnectivity))
            throw new InvalidDataException("两套网格的坐标、连接索引或参数顺序不完全一致，禁止伪三维插值。");
    }

    public static void ValidateAcquisitionPair(Pseudo3dAcquisitionStamp? lower, DateTimeOffset lowerAt,
        Pseudo3dAcquisitionStamp? upper, DateTimeOffset upperAt)
    {
        ValidateStamp(lower, lowerAt, 0);
        ValidateStamp(upper, upperAt, 1);
        if (!IsSameRound(lower, upper) || lower!.Profile != upper!.Profile || lower.FrequencyTuningWord != upper.FrequencyTuningWord)
            throw new InvalidDataException("上下层必须来自同一分时会话、同一轮次且各使用一次。");
        var skew = upperAt - lowerAt;
        if (skew <= TimeSpan.Zero || skew.TotalMilliseconds + lower!.TimingUncertaintyMilliseconds +
            upper!.TimingUncertaintyMilliseconds > Pseudo3dAcquisitionProfile.MaximumPairSkew.TotalMilliseconds)
            throw new InvalidDataException("分时两层的采样时差及时间不确定度超过固定配对范围。");
    }
}
