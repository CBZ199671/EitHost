using PureHDF;
using PureHDF.Filters;
using PureHDF.VOL.Native;

namespace EitHost.Core.Storage.Hdf5;

internal static class Hdf5StoragePolicy
{
    private const long CompressionThresholdBytes = 4 * 1024;
    private const int TargetChunkBytes = 128 * 1024;

    internal static IReadOnlyList<ushort> NumericFilterIds { get; } =
        [ShuffleFilter.Id, DeflateFilter.Id];

    public static object Numeric(ushort[,] values) =>
        values.LongLength * sizeof(ushort) >= CompressionThresholdBytes
            ? CreateChunked(
                values,
                MatrixChunks(values.GetLength(0), values.GetLength(1), sizeof(ushort)))
            : values;

    public static object Numeric(ushort[] values, int rows, int columns)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        if (values.Length != checked(rows * columns))
        {
            throw new ArgumentException("Flat numeric payload length must match matrix dimensions.", nameof(values));
        }

        var fileDimensions = new[] { checked((ulong)rows), checked((ulong)columns) };
        return values.LongLength * sizeof(ushort) >= CompressionThresholdBytes
            ? CreateChunked(
                values,
                MatrixChunks(rows, columns, sizeof(ushort)),
                fileDimensions)
            : new H5Dataset(values, fileDims: fileDimensions);
    }

    public static object Numeric(double[] values) =>
        values.LongLength * sizeof(double) >= CompressionThresholdBytes
            ? CreateChunked(values, VectorChunks(values.Length, sizeof(double)))
            : values;

    public static object Numeric(double[,] values) =>
        values.LongLength * sizeof(double) >= CompressionThresholdBytes
            ? CreateChunked(
                values,
                MatrixChunks(values.GetLength(0), values.GetLength(1), sizeof(double)))
            : values;

    public static object Numeric(int[,] values) =>
        values.LongLength * sizeof(int) >= CompressionThresholdBytes
            ? CreateChunked(
                values,
                MatrixChunks(values.GetLength(0), values.GetLength(1), sizeof(int)))
            : values;

    private static H5Dataset CreateChunked(object values, uint[] chunks, ulong[]? fileDimensions = null)
    {
        return new H5Dataset(
            values,
            chunks: chunks,
            fileDims: fileDimensions,
            datasetCreation: new H5DatasetCreation(
                Filters:
                [
                    new H5Filter(
                        ShuffleFilter.Id,
                        new Dictionary<string, object>()),
                    new H5Filter(
                        DeflateFilter.Id,
                        new Dictionary<string, object>
                        {
                            [DeflateFilter.COMPRESSION_LEVEL] = 1
                        })
                ]));
    }

    private static uint[] VectorChunks(int length, int elementSize)
    {
        var elementsPerChunk = Math.Max(1, TargetChunkBytes / elementSize);
        return [checked((uint)Math.Min(length, elementsPerChunk))];
    }

    private static uint[] MatrixChunks(int rows, int columns, int elementSize)
    {
        var bytesPerRow = checked(columns * elementSize);
        var rowsPerChunk = Math.Max(1, TargetChunkBytes / bytesPerRow);
        return
        [
            checked((uint)Math.Min(rows, rowsPerChunk)),
            checked((uint)columns)
        ];
    }
}
