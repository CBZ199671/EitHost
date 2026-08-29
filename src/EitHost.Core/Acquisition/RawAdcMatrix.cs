using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Acquisition;

public static class RawAdcMatrix
{
    public static ushort[,] FromInterleaved(
        IReadOnlyList<ushort> values,
        int valueCount,
        int channelCount = Usb2070Constants.RequiredMeasurementChannelCount)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCount);

        if (valueCount > values.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(valueCount), "Value count cannot exceed source buffer length.");
        }

        if (valueCount % channelCount != 0)
        {
            throw new ArgumentException("Raw USB2070 value count must divide evenly by channel count.", nameof(valueCount));
        }

        var rowCount = valueCount / channelCount;
        if (rowCount == 0)
        {
            throw new ArgumentException("Raw USB2070 data must contain at least one sample row.", nameof(valueCount));
        }

        var matrix = new ushort[rowCount, channelCount];
        for (var row = 0; row < rowCount; row++)
        {
            for (var channel = 0; channel < channelCount; channel++)
            {
                matrix[row, channel] = values[(row * channelCount) + channel];
            }
        }

        return matrix;
    }
}
