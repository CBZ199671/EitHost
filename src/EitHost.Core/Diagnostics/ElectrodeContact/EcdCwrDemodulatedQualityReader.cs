using EitHost.Core.Demodulation;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrDemodulatedQualityReader
{
    private const int QualityColumnCount = 13;
    private const int QualityMetricColumnCount = 3;

    public IReadOnlyList<IReadOnlyList<DemodulatedWindowQuality>> Read(string demodulatedHdf5Path, int frameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demodulatedHdf5Path);
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        using var file = Hdf5FileAccess.OpenReadWithRetry(demodulatedHdf5Path);
        var frames = new List<IReadOnlyList<DemodulatedWindowQuality>>(frameCount);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameNumber = frameIndex + 1;
            var quality = file.Dataset($"/demod/frame_{frameNumber:000}_quality").Read<int[,]>();
            var metrics = file.Dataset($"/demod/frame_{frameNumber:000}_quality_metrics").Read<double[,]>();
            frames.Add(ParseFrame(quality, metrics, frameNumber));
        }

        return frames;
    }

    private static IReadOnlyList<DemodulatedWindowQuality> ParseFrame(
        int[,] quality,
        double[,] metrics,
        int frameNumber)
    {
        if (quality.GetLength(0) != DemodulatedFrame.StimulationCount ||
            quality.GetLength(1) != QualityColumnCount ||
            metrics.GetLength(0) != DemodulatedFrame.StimulationCount ||
            metrics.GetLength(1) != QualityMetricColumnCount)
        {
            throw new InvalidDataException(
                $"Demodulated quality frame {frameNumber} must contain quality[16,13] and metrics[16,3].");
        }

        var values = new DemodulatedWindowQuality[DemodulatedFrame.StimulationCount];
        for (var row = 0; row < values.Length; row++)
        {
            var top3 = Enumerable.Range(4, 3)
                .Select(column => quality[row, column])
                .Where(value => value > 0)
                .Select(value => value - 1)
                .ToArray();
            values[row] = new DemodulatedWindowQuality(
                WindowIndex: ToZeroBased(quality[row, 0]),
                ExpectedReferenceChannel: ToZeroBased(quality[row, 1]),
                DetectedTop1Channel: ToOptionalZeroBased(quality[row, 2]),
                TripletCenterChannel: ToOptionalZeroBased(quality[row, 3]),
                Top3Channels: top3,
                Top3Contiguous: quality[row, 7] != 0,
                Top1IsTripletCenter: quality[row, 8] != 0,
                State: (DemodulatedWindowQualityState)quality[row, 9],
                RejectReason: (DemodulatedWindowRejectReason)quality[row, 12],
                PeakToBackgroundRatio: metrics[row, 1],
                AdcSaturationCount: checked((int)Math.Round(metrics[row, 2])));
        }

        return values;
    }

    private static int ToZeroBased(int oneBased)
    {
        if (oneBased <= 0)
        {
            throw new InvalidDataException("Required demodulated quality index must be one-based and positive.");
        }

        return oneBased - 1;
    }

    private static int ToOptionalZeroBased(int oneBased)
    {
        return oneBased > 0 ? oneBased - 1 : -1;
    }
}
