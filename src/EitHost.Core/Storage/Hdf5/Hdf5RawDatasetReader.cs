using System.Runtime.InteropServices;
using HDF.PInvoke;

namespace EitHost.Core.Storage.Hdf5;

public sealed record Hdf5RawDatasetChunk(long RowOffset, ushort[,] Values);

public sealed class Hdf5RawDatasetReader
{
    public const int DefaultChunkRows = 65_536;
    internal const int MaximumChunksPerFileLease = 8;
    internal const long MaximumFileLeaseBytes = 8L * 1024L * 1024L;

    private readonly Action? fileLeaseOpened;

    public Hdf5RawDatasetReader()
    {
    }

    internal Hdf5RawDatasetReader(Action fileLeaseOpened)
    {
        this.fileLeaseOpened = fileLeaseOpened ?? throw new ArgumentNullException(nameof(fileLeaseOpened));
    }

    public IEnumerable<Hdf5RawDatasetChunk> ReadRange(
        string filePath,
        long rowOffset,
        long rowCount,
        int chunkRows = DefaultChunkRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkRows);
        if (rowCount == 0)
        {
            yield break;
        }

        var fullPath = Path.GetFullPath(filePath);
        var (totalRows, channelCount) = ReadShape(fullPath);
        if (rowOffset > totalRows - rowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rowCount),
                "Requested raw HDF5 range exceeds the dataset extent.");
        }

        var chunksPerLease = GetChunksPerFileLease(chunkRows, channelCount);
        long consumed = 0;
        while (consumed < rowCount)
        {
            var currentOffset = checked(rowOffset + consumed);
            var leaseRows = Math.Min(
                rowCount - consumed,
                checked((long)chunkRows * chunksPerLease));
            foreach (var chunk in ReadChunkLease(
                         fullPath,
                         currentOffset,
                         leaseRows,
                         chunkRows,
                         channelCount))
            {
                yield return chunk;
            }

            consumed += leaseRows;
        }
    }

    private (long TotalRows, int ChannelCount) ReadShape(string fullPath)
    {
        var file = Hdf5IncrementalStageAppender.OpenFileWithStrongClose(
            access => H5F.open(fullPath, H5F.ACC_RDONLY, access),
            $"open raw HDF5 '{fullPath}' for shape read",
            leaseProbePath: fullPath,
            leaseProbeAccess: FileAccess.Read);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        var dataset = -1L;
        var datasetSpace = -1L;
        Exception? primaryException = null;
        try
        {
            fileLeaseOpened?.Invoke();
            dataset = H5D.open(file, "/raw/adc_counts");
            EnsureValid(dataset, "open /raw/adc_counts");
            datasetSpace = H5D.get_space(dataset);
            EnsureValid(datasetSpace, "get /raw/adc_counts dataspace");
            var dimensions = new ulong[2];
            var rank = H5S.get_simple_extent_dims(datasetSpace, dimensions, null);
            EnsureSuccess(rank, "read /raw/adc_counts dimensions");
            if (rank != 2 || dimensions[1] == 0 || dimensions[1] > int.MaxValue)
            {
                throw new InvalidDataException("Raw ADC dataset must be a two-dimensional channel matrix.");
            }

            return (checked((long)dimensions[0]), checked((int)dimensions[1]));
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            CloseReadHandles(fullPath, file, dataset, datasetSpace, primaryException, "shape read");
        }
    }

    private IReadOnlyList<Hdf5RawDatasetChunk> ReadChunkLease(
        string fullPath,
        long rowOffset,
        long rowCount,
        int chunkRows,
        int channelCount)
    {
        var chunks = new List<Hdf5RawDatasetChunk>(MaximumChunksPerFileLease);
        var file = Hdf5IncrementalStageAppender.OpenFileWithStrongClose(
            access => H5F.open(fullPath, H5F.ACC_RDONLY, access),
            $"open raw HDF5 '{fullPath}' for chunk-lease read",
            leaseProbePath: fullPath,
            leaseProbeAccess: FileAccess.Read);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        var dataset = -1L;
        var fileSpace = -1L;
        var memorySpace = -1L;
        Exception? primaryException = null;
        try
        {
            fileLeaseOpened?.Invoke();
            dataset = H5D.open(file, "/raw/adc_counts");
            EnsureValid(dataset, "open /raw/adc_counts");
            fileSpace = H5D.get_space(dataset);
            EnsureValid(fileSpace, "get raw chunk file space");
            long consumed = 0;
            while (consumed < rowCount)
            {
                var currentRows = checked((int)Math.Min(chunkRows, rowCount - consumed));
                var currentOffset = checked(rowOffset + consumed);
                var values = new ushort[currentRows, channelCount];
                EnsureSuccess(
                    H5S.select_hyperslab(
                        fileSpace,
                        H5S.seloper_t.SET,
                        [checked((ulong)currentOffset), 0],
                        null,
                        [checked((ulong)currentRows), checked((ulong)channelCount)],
                        null),
                    "select raw read hyperslab");
                memorySpace = H5S.create_simple(
                    2,
                    [checked((ulong)currentRows), checked((ulong)channelCount)],
                    null);
                EnsureValid(memorySpace, "create raw read memory space");
                var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
                try
                {
                    EnsureSuccess(
                        H5D.read(
                            dataset,
                            H5T.NATIVE_UINT16,
                            memorySpace,
                            fileSpace,
                            H5P.DEFAULT,
                            handle.AddrOfPinnedObject()),
                        "read raw HDF5 hyperslab");
                }
                finally
                {
                    handle.Free();
                }

                EnsureSuccess(
                    H5S.close(memorySpace),
                    "close raw read memory space");
                memorySpace = -1;
                chunks.Add(new Hdf5RawDatasetChunk(currentOffset, values));
                consumed += currentRows;
            }

            return chunks;
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            if (memorySpace >= 0)
            {
                H5S.close(memorySpace);
            }

            CloseReadHandles(fullPath, file, dataset, fileSpace, primaryException, "chunk-lease read");
        }
    }

    private static void CloseReadHandles(
        string fullPath,
        long file,
        long dataset,
        long dataSpace,
        Exception? primaryException,
        string phase)
    {
        if (dataSpace >= 0)
        {
            H5S.close(dataSpace);
        }

        if (dataset >= 0)
        {
            H5D.close(dataset);
        }

        if (file >= 0)
        {
            Hdf5IncrementalStageAppender.CloseFileChecked(
                file,
                $"close raw HDF5 '{fullPath}' after {phase}",
                primaryException);
        }
    }

    private static void EnsureValid(long identifier, string operation)
    {
        if (identifier < 0)
        {
            throw CreateFailure(operation);
        }
    }

    private static int GetChunksPerFileLease(int chunkRows, int channelCount)
    {
        var bytesPerChunk = checked((long)chunkRows * channelCount * sizeof(ushort));
        var byteBoundedChunks = Math.Max(1L, MaximumFileLeaseBytes / bytesPerChunk);
        return checked((int)Math.Min(MaximumChunksPerFileLease, byteBoundedChunks));
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result < 0)
        {
            throw CreateFailure(operation);
        }
    }

    private static IOException CreateFailure(string operation)
    {
        var stack = Hdf5IncrementalStageAppender.CaptureNativeErrorStack();
        return new IOException(
            string.IsNullOrWhiteSpace(stack)
                ? $"HDF5 operation failed: {operation}."
                : $"HDF5 operation failed: {operation}.{Environment.NewLine}{stack}");
    }
}
