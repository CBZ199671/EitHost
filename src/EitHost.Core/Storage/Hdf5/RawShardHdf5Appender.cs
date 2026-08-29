using System.Runtime.InteropServices;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using HDF.PInvoke;

namespace EitHost.Core.Storage.Hdf5;

internal sealed record RawShardAppendContext(
    Guid ExperimentRunId,
    Guid SessionId,
    int SegmentSequence,
    long StartSampleIndex,
    long EndSampleIndex,
    DateTimeOffset CapturedAt,
    DeviceRunMetadata Device,
    Hdf5ExcitationMetadata Excitation,
    Usb2070AcquisitionMetadata Acquisition,
    RawSegmentDemodulationMetadata Demodulation,
    IReadOnlyList<RawAcquisitionDiscontinuityEvent> Discontinuities,
    long FrameRows,
    long CapacityRows);

internal sealed class RawShardHdf5Appender
{
    private const int TargetChunkBytes = 128 * 1024;

    public void Create(
        string filePath,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        Validate(context, values, valueOffset, valueCount);
        var fullPath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.partial";
        try
        {
            CreateCore(temporaryPath, context, values, valueOffset, valueCount);
            AtomicFileCommitter.MoveWithRetry(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            AtomicFileCommitter.DeleteBestEffort(temporaryPath);
        }
    }

    public long Append(
        string filePath,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        using var session = Open(filePath);
        var newRows = Append(session, context, values, valueOffset, valueCount);
        Checkpoint(session, context, newRows);
        return newRows;
    }

    public RawShardHdf5WriteSession Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var file = Hdf5IncrementalStageAppender.OpenFileWithStrongClose(
            access => H5F.open(fullPath, H5F.ACC_RDWR, access),
            $"open raw shard '{fullPath}' read-write",
            leaseProbePath: fullPath,
            leaseProbeAccess: FileAccess.ReadWrite);
        return new RawShardHdf5WriteSession(fullPath, file);
    }

    public long Append(
        RawShardHdf5WriteSession session,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfClosed();
        Validate(context, values, valueOffset, valueCount);
        return AppendValues(session.FileIdentifier, context, values, valueOffset, valueCount);
    }

    public void Checkpoint(
        RawShardHdf5WriteSession session,
        RawShardAppendContext context,
        long sampleRows)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        ArgumentNullException.ThrowIfNull(session);
        session.ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(context);
        if (sampleRows <= 0 || sampleRows > context.CapacityRows)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRows));
        }

        UpdateMutableMetadata(session.FileIdentifier, context, sampleRows);
        EnsureSuccess(
            H5F.flush(session.FileIdentifier, H5F.scope_t.GLOBAL),
            "checkpoint active raw shard");
    }

    private static void CreateCore(
        string filePath,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        var file = Hdf5IncrementalStageAppender.OpenFileWithStrongClose(
            access => H5F.create(filePath, H5F.ACC_TRUNC, H5P.DEFAULT, access),
            $"create raw shard '{filePath}'",
            leaseProbePath: filePath,
            leaseProbeAccess: FileAccess.ReadWrite);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        Exception? primaryException = null;
        try
        {
            Hdf5IncrementalStageAppender.EnsureGroupTree(file, "/raw");
            CreateAdcDataset(file, context, values, valueOffset, valueCount);
            var rows = valueCount / context.Device.MeasurementChannelCount;
            var metadata = new RawShardMetadataView(context, rows);
            Hdf5IncrementalStageAppender.WriteContent(
                file,
                "/metadata",
                RawSegmentHdf5Writer.CreateMetadataGroup(metadata));
            UpdateStorageMetadata(file, context, rows);
            EnsureSuccess(H5F.flush(file, H5F.scope_t.GLOBAL), "flush new raw shard");
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            Hdf5IncrementalStageAppender.CloseFileChecked(
                file,
                $"close new raw shard '{filePath}'",
                primaryException);
        }
    }

    private static void CreateAdcDataset(
        long file,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        var channelCount = context.Device.MeasurementChannelCount;
        var rows = valueCount / channelCount;
        var dimensions = new[] { checked((ulong)rows), checked((ulong)channelCount) };
        var maximum = new[] { checked((ulong)context.CapacityRows), checked((ulong)channelCount) };
        var dataspace = H5S.create_simple(2, dimensions, maximum);
        EnsureValid(dataspace, "create raw shard dataspace");
        var creation = H5P.create(H5P.DATASET_CREATE);
        EnsureValid(creation, "create raw shard dataset properties");
        var dataset = -1L;
        try
        {
            var rowBytes = checked(channelCount * sizeof(ushort));
            var chunkRows = Math.Max(1, TargetChunkBytes / rowBytes);
            EnsureSuccess(
                H5P.set_chunk(
                    creation,
                    2,
                    [checked((ulong)Math.Min(context.CapacityRows, chunkRows)), checked((ulong)channelCount)]),
                "set raw shard chunks");
            EnsureSuccess(H5P.set_shuffle(creation), "set raw shard shuffle");
            EnsureSuccess(H5P.set_deflate(creation, 1), "set raw shard deflate");
            dataset = H5D.create(
                file,
                "/raw/adc_counts",
                H5T.NATIVE_UINT16,
                dataspace,
                H5P.DEFAULT,
                creation,
                H5P.DEFAULT);
            EnsureValid(dataset, "create raw shard ADC dataset");
            WritePinned(dataset, dataspace, values, valueOffset);
        }
        finally
        {
            if (dataset >= 0)
            {
                H5D.close(dataset);
            }

            H5P.close(creation);
            H5S.close(dataspace);
        }
    }

    private static long AppendValues(
        long file,
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        var channelCount = context.Device.MeasurementChannelCount;
        var appendRows = valueCount / channelCount;
        var dataset = H5D.open(file, "/raw/adc_counts");
        EnsureValid(dataset, "open raw shard ADC dataset");
        var oldSpace = -1L;
        var fileSpace = -1L;
        var memorySpace = -1L;
        try
        {
            oldSpace = H5D.get_space(dataset);
            EnsureValid(oldSpace, "get raw shard current dataspace");
            var dimensions = new ulong[2];
            EnsureSuccess(H5S.get_simple_extent_dims(oldSpace, dimensions, null), "read raw shard dimensions");
            if (dimensions[1] != (ulong)channelCount)
            {
                throw new InvalidDataException("Raw shard channel count changed during append.");
            }

            var oldRows = checked((long)dimensions[0]);
            var newRows = checked(oldRows + appendRows);
            if (newRows > context.CapacityRows)
            {
                throw new InvalidDataException("Raw shard append exceeds its 10000-frame capacity.");
            }

            EnsureSuccess(
                H5D.set_extent(dataset, [checked((ulong)newRows), checked((ulong)channelCount)]),
                "extend raw shard ADC dataset");
            fileSpace = H5D.get_space(dataset);
            EnsureValid(fileSpace, "get extended raw shard dataspace");
            EnsureSuccess(
                H5S.select_hyperslab(
                    fileSpace,
                    H5S.seloper_t.SET,
                    [checked((ulong)oldRows), 0],
                    null,
                    [checked((ulong)appendRows), checked((ulong)channelCount)],
                    null),
                "select raw shard append hyperslab");
            memorySpace = H5S.create_simple(
                2,
                [checked((ulong)appendRows), checked((ulong)channelCount)],
                null);
            EnsureValid(memorySpace, "create raw shard append memory space");
            WritePinned(dataset, memorySpace, fileSpace, values, valueOffset);
            return newRows;
        }
        finally
        {
            if (memorySpace >= 0)
            {
                H5S.close(memorySpace);
            }

            if (fileSpace >= 0)
            {
                H5S.close(fileSpace);
            }

            if (oldSpace >= 0)
            {
                H5S.close(oldSpace);
            }

            H5D.close(dataset);
        }
    }

    private static void UpdateMutableMetadata(
        long file,
        RawShardAppendContext context,
        long sampleRows)
    {
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/run/end_sample_index",
            context.EndSampleIndex);
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/run/sample_rows",
            sampleRows);
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/acquisition/has_discontinuity",
            context.Discontinuities.Count > 0);
        if (context.Discontinuities.Count > 0)
        {
            var ranges = new long[context.Discontinuities.Count, 2];
            var detectedAt = new string[context.Discontinuities.Count];
            var reasons = new string[context.Discontinuities.Count];
            for (var index = 0; index < context.Discontinuities.Count; index++)
            {
                var item = context.Discontinuities[index];
                ranges[index, 0] = item.StartSampleIndex;
                ranges[index, 1] = item.EndSampleIndex;
                detectedAt[index] = item.DetectedAt.ToUniversalTime().ToString("O");
                reasons[index] = item.Reason;
            }

            Hdf5IncrementalStageAppender.ReplaceContentValue(
                file,
                "/metadata/acquisition/overflow_events",
                ranges);
            Hdf5IncrementalStageAppender.ReplaceContentValue(
                file,
                "/metadata/acquisition/overflow_event_detected_at_utc",
                detectedAt);
            Hdf5IncrementalStageAppender.ReplaceContentValue(
                file,
                "/metadata/acquisition/overflow_event_reason",
                reasons);
        }

        UpdateStorageMetadata(file, context, sampleRows);
    }

    private static void UpdateStorageMetadata(
        long file,
        RawShardAppendContext context,
        long sampleRows)
    {
        Hdf5IncrementalStageAppender.EnsureGroupTree(file, "/metadata/storage");
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/storage/contract",
            "raw_shard_v1");
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/storage/frame_rows",
            context.FrameRows);
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/storage/frame_capacity",
            10_000L);
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/storage/capacity_rows",
            context.CapacityRows);
        Hdf5IncrementalStageAppender.ReplaceContentValue(
            file,
            "/metadata/storage/stored_complete_frames",
            sampleRows / context.FrameRows);
    }

    private static void WritePinned(
        long dataset,
        long dataspace,
        ushort[] values,
        int valueOffset) =>
        WritePinned(dataset, dataspace, H5S.ALL, values, valueOffset);

    private static void WritePinned(
        long dataset,
        long memorySpace,
        long fileSpace,
        ushort[] values,
        int valueOffset)
    {
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            var pointer = IntPtr.Add(handle.AddrOfPinnedObject(), checked(valueOffset * sizeof(ushort)));
            EnsureSuccess(
                H5D.write(
                    dataset,
                    H5T.NATIVE_UINT16,
                    memorySpace,
                    fileSpace,
                    H5P.DEFAULT,
                    pointer),
                "write raw shard ADC rows");
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Validate(
        RawShardAppendContext context,
        ushort[] values,
        int valueOffset,
        int valueCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegative(valueOffset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(valueCount);
        var channelCount = context.Device.MeasurementChannelCount;
        if (valueOffset > values.Length - valueCount || valueCount % channelCount != 0)
        {
            throw new ArgumentException("Raw shard append must contain complete in-range channel rows.");
        }

        if (context.FrameRows <= 0 || context.CapacityRows != checked(context.FrameRows * 10_000L))
        {
            throw new ArgumentException("Raw shard frame capacity contract is invalid.", nameof(context));
        }
    }

    private static void EnsureValid(long identifier, string operation)
    {
        if (identifier < 0)
        {
            throw CreateFailure(operation);
        }
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
        var nativeStack = Hdf5IncrementalStageAppender.CaptureNativeErrorStack();
        return new IOException(
            string.IsNullOrWhiteSpace(nativeStack)
                ? $"HDF5 operation failed: {operation}."
                : $"HDF5 operation failed: {operation}.{Environment.NewLine}{nativeStack}");
    }

    private sealed class RawShardMetadataView(
        RawShardAppendContext context,
        int sampleRows) : IRawSegmentWriteData
    {
        public Guid ExperimentRunId => context.ExperimentRunId;
        public Guid SessionId => context.SessionId;
        public int SegmentSequence => context.SegmentSequence;
        public long StartSampleIndex => context.StartSampleIndex;
        public long EndSampleIndex => context.EndSampleIndex;
        public DateTimeOffset CapturedAt => context.CapturedAt;
        public DeviceRunMetadata Device => context.Device;
        public Hdf5ExcitationMetadata Excitation => context.Excitation;
        public Usb2070AcquisitionMetadata Acquisition => context.Acquisition;
        public RawSegmentDemodulationMetadata Demodulation => context.Demodulation;
        public IReadOnlyList<RawAcquisitionDiscontinuityEvent> Discontinuities => context.Discontinuities;
        public int SampleRows => sampleRows;
        public int ChannelCount => context.Device.MeasurementChannelCount;
        public object CreateAdcDataset() => throw new NotSupportedException();
    }
}

internal sealed class RawShardHdf5WriteSession : IDisposable
{
    private int closed;

    internal RawShardHdf5WriteSession(string filePath, long fileIdentifier)
    {
        FilePath = filePath;
        FileIdentifier = fileIdentifier;
    }

    internal string FilePath { get; }

    internal long FileIdentifier { get; }

    internal bool IsClosed => Volatile.Read(ref closed) != 0;

    internal void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        using var nativeCall = Hdf5NativeCallGate.Enter();
        Hdf5IncrementalStageAppender.CloseFileChecked(
            FileIdentifier,
            $"close active raw shard '{FilePath}'",
            primaryException: null);
    }
}
