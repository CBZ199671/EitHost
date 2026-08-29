using System.Diagnostics;
using EitHost.Core.Acquisition;
using EitHost.Core.Demodulation;
using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;
using PureHDF;

namespace EitHost.Core.Application.Realtime;

public sealed record RealtimeRawPersistenceMetadata(
    Guid ExperimentRunId,
    Guid SessionId,
    DateTimeOffset RunStartedAt,
    DeviceRunMetadata Device,
    Hdf5ExcitationMetadata Excitation,
    Usb2070AcquisitionMetadata Acquisition,
    RawSegmentDemodulationMetadata Demodulation);

public sealed record RealtimeRawPersistenceResult(
    string Hdf5Path,
    int SampleRows,
    int ChannelCount,
    TimeSpan WriteElapsed,
    TimeSpan DurabilityCheckpointElapsed,
    bool DurabilityCheckpointed,
    bool CatalogCheckpointed,
    ExperimentRunCatalogSummary? CanonicalSummary);

public sealed record RealtimeRawRecoveryResult(
    int RecoveredShardCount,
    IReadOnlyList<string> Warnings);

public sealed class RealtimeRawPersistenceService : IDisposable
{
    public const int FramesPerRawShard = 10_000;
    public static readonly TimeSpan DurabilityCheckpointInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CatalogCheckpointInterval = TimeSpan.FromSeconds(5);
    private const long MetadataAllowanceBytes = 1024L * 1024L;
    private const int MaximumReusableValueBufferCount = 2;
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly IDataRootStorageService storageService;
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? operatorDiagnostic;
    private readonly RawShardHdf5Appender shardAppender = new();
    private readonly Dictionary<Guid, RawShardState> activeShards = [];
    private readonly Dictionary<Guid, int> nextShardSequences = [];
    private readonly Dictionary<int, ushort[]> reusableValueBuffers = [];
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly object disposalSync = new();
    private int admittedOperations;
    private bool disposed;
    private bool persistenceGateDisposed;

    public RealtimeRawPersistenceService(
        DataRootLayout layout,
        ExperimentCatalog catalog,
        IDataRootStorageService storageService,
        TimeProvider? timeProvider = null,
        Action<string>? operatorDiagnostic = null)
    {
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.operatorDiagnostic = operatorDiagnostic;
    }

    public async Task<RealtimeRawPersistenceResult> PersistAsync<TContext>(
        RealtimeRawBatch<TContext> batch,
        RealtimeRawPersistenceMetadata metadata,
        CancellationToken cancellationToken = default)
        where TContext : notnull
    {
        AdmitOperation();

        var enteredGate = false;
        try
        {
            ArgumentNullException.ThrowIfNull(batch);
            ArgumentNullException.ThrowIfNull(metadata);
            ValidateIdentity(batch, metadata);
            await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            return await Task.Run(
                    () => Persist(batch, metadata),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (enteredGate)
            {
                persistenceGate.Release();
            }

            CompleteOperation();
            batch?.Dispose();
        }
    }

    public async Task<ExperimentRunCatalogSummary?> CompleteRunAsync(
        Guid experimentRunId,
        bool publishReady = true,
        CancellationToken cancellationToken = default)
    {
        if (experimentRunId == Guid.Empty)
        {
            throw new ArgumentException("Experiment run identity cannot be empty.", nameof(experimentRunId));
        }

        AdmitOperation();
        var enteredGate = false;
        try
        {
            await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            return await Task.Run(
                    () => CompleteRun(experimentRunId, publishReady),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (enteredGate)
            {
                persistenceGate.Release();
            }

            CompleteOperation();
        }
    }

    public RealtimeRawRecoveryResult ReconcileIncompleteCatalogShards()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var recovered = 0;
        var warnings = new List<string>();
        foreach (var run in catalog.ListRuns())
        {
            foreach (var segment in catalog.ListRawSegments(run.ExperimentRunId)
                         .Where(item => !string.Equals(item.Status, "ready", StringComparison.Ordinal)))
            {
                try
                {
                    var path = layout.ResolveArtifactPath(segment.ArtifactPath);
                    using var file = Hdf5FileAccess.OpenReadWithRetry(path);
                    var dataset = file.Dataset("/raw/adc_counts");
                    var dimensions = dataset.Space.Dimensions;
                    if (dimensions.Length != 2 ||
                        dimensions[0] == 0 ||
                        dimensions[0] > long.MaxValue ||
                        dimensions[1] != (ulong)segment.ChannelCount)
                    {
                        throw new InvalidDataException("Raw shard extent is not a valid non-empty channel matrix.");
                    }

                    var embeddedRunId = file.Dataset("/metadata/run/run_id").Read<string>();
                    if (!Guid.TryParse(embeddedRunId, out var parsedRunId) ||
                        parsedRunId != segment.ExperimentRunId)
                    {
                        throw new InvalidDataException("Raw shard embedded run identity does not match catalog.");
                    }

                    var embeddedSequence = file.Dataset("/metadata/run/segment_sequence").Read<int>();
                    var embeddedStartSampleIndex =
                        file.Dataset("/metadata/run/start_sample_index").Read<long>();
                    if (embeddedSequence != segment.SegmentSequence ||
                        embeddedStartSampleIndex != segment.StartSampleIndex)
                    {
                        throw new InvalidDataException("Raw shard embedded segment identity does not match catalog.");
                    }

                    var sampleRows = checked((long)dimensions[0]);
                    var hasDiscontinuity = file.LinkExists("/metadata/acquisition/has_discontinuity")
                        ? file.Dataset("/metadata/acquisition/has_discontinuity").Read<bool>()
                        : segment.HasDiscontinuity;
                    catalog.RegisterRawSegment(segment with
                    {
                        EndSampleIndex = checked(segment.StartSampleIndex + sampleRows),
                        SampleRows = sampleRows,
                        Status = "ready",
                        HasDiscontinuity = hasDiscontinuity
                    });
                    recovered++;
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"raw shard recovery failed run={segment.ExperimentRunId:D} " +
                        $"segment={segment.SegmentSequence}: {ex.Message}");
                }
            }
        }

        return new RealtimeRawRecoveryResult(recovered, warnings);
    }

    public void Dispose()
    {
        var disposeGate = false;
        lock (disposalSync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (admittedOperations == 0)
            {
                persistenceGateDisposed = true;
                disposeGate = true;
            }
        }

        if (disposeGate)
        {
            DisposeResources();
            persistenceGate.Dispose();
        }
    }

    private void AdmitOperation()
    {
        lock (disposalSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            admittedOperations++;
        }
    }

    private void CompleteOperation()
    {
        var disposeGate = false;
        lock (disposalSync)
        {
            admittedOperations--;
            if (disposed && admittedOperations == 0 && !persistenceGateDisposed)
            {
                persistenceGateDisposed = true;
                disposeGate = true;
            }
        }

        if (disposeGate)
        {
            DisposeResources();
            persistenceGate.Dispose();
        }
    }

    private RealtimeRawPersistenceResult Persist<TContext>(
        RealtimeRawBatch<TContext> batch,
        RealtimeRawPersistenceMetadata metadata)
        where TContext : notnull
    {
        var stopwatch = Stopwatch.StartNew();
        var durabilityStopwatch = new Stopwatch();
        storageService.EnsureWriteCapacity(EstimateArtifactBytes(
            checked((long)batch.ValueCount * sizeof(ushort))));
        var channelCount = metadata.Device.MeasurementChannelCount;
        var sampleRows = batch.ValueCount / channelCount;
        var frameRows = CalculateFrameRows(metadata);
        var capacityRows = checked(frameRows * FramesPerRawShard);
        var values = RentValueBuffer(batch.ValueCount);
        try
        {
            batch.CopyValuesTo(values);
            var rawDirectory = layout.EnsureRawDirectory(metadata.ExperimentRunId, metadata.RunStartedAt);
            var consumedRows = 0;
            string? lastPath = null;
            var durabilityCheckpointed = false;
            var catalogCheckpointed = false;
            while (consumedRows < sampleRows)
            {
                var checkpointAt = timeProvider.GetUtcNow();
                var absoluteStart = checked(batch.StartSampleIndex + consumedRows);
                if (activeShards.TryGetValue(metadata.ExperimentRunId, out var currentShard) &&
                    (currentShard.EndSampleIndex != absoluteStart ||
                     currentShard.SampleRows >= currentShard.CapacityRows))
                {
                    CheckpointAndCloseShard(currentShard, durabilityStopwatch);
                    RegisterShard(currentShard, metadata.ExperimentRunId, channelCount, "ready");
                    durabilityCheckpointed = true;
                    catalogCheckpointed = true;
                    activeShards.Remove(metadata.ExperimentRunId);
                }

                if (!activeShards.TryGetValue(metadata.ExperimentRunId, out var shard))
                {
                    shard = CreateShardState(
                        metadata,
                        rawDirectory,
                        absoluteStart,
                        batch.CapturedAt + TimeSpan.FromSeconds(
                            consumedRows / (double)metadata.Acquisition.SampleRateHz),
                        frameRows,
                        capacityRows);
                    activeShards[metadata.ExperimentRunId] = shard;
                }

                var rowsToWrite = checked((int)Math.Min(
                    sampleRows - consumedRows,
                    shard.CapacityRows - shard.SampleRows));
                var chunkEnd = checked(absoluteStart + rowsToWrite);
                var chunkDiscontinuities = batch.Discontinuities
                    .Where(item => item.EndSampleIndex > absoluteStart &&
                                   item.StartSampleIndex < chunkEnd)
                    .Select(item => item with
                    {
                        StartSampleIndex = Math.Max(item.StartSampleIndex, absoluteStart),
                        EndSampleIndex = Math.Min(item.EndSampleIndex, chunkEnd)
                    })
                    .ToArray();
                shard.Discontinuities.AddRange(chunkDiscontinuities);
                var context = new RawShardAppendContext(
                    metadata.ExperimentRunId,
                    metadata.SessionId,
                    shard.SegmentSequence,
                    shard.StartSampleIndex,
                    chunkEnd,
                    shard.CapturedAt,
                    metadata.Device,
                    metadata.Excitation,
                    metadata.Acquisition,
                    metadata.Demodulation,
                    shard.Discontinuities.ToArray(),
                    shard.FrameRows,
                    shard.CapacityRows);
                var valueOffset = checked(consumedRows * channelCount);
                var valueCount = checked(rowsToWrite * channelCount);
                if (shard.SampleRows == 0)
                {
                    durabilityStopwatch.Start();
                    shardAppender.Create(
                        shard.Hdf5Path,
                        context,
                        values,
                        valueOffset,
                        valueCount);
                    durabilityStopwatch.Stop();
                    shard.Session = shardAppender.Open(shard.Hdf5Path);
                }
                else
                {
                    shardAppender.Append(
                        shard.Session ?? throw new InvalidOperationException("Active raw shard session is missing."),
                        context,
                        values,
                        valueOffset,
                        valueCount);
                }

                shard.SampleRows = checked(shard.SampleRows + rowsToWrite);
                shard.EndSampleIndex = chunkEnd;
                if (shard.CheckpointedRows == 0)
                {
                    shard.CheckpointedRows = shard.SampleRows;
                    shard.LastDurabilityCheckpointAt = checkpointAt;
                    durabilityCheckpointed = true;
                }

                var forceCheckpoint = chunkDiscontinuities.Length > 0 ||
                                      shard.SampleRows == shard.CapacityRows;
                if (forceCheckpoint ||
                    checkpointAt - shard.LastDurabilityCheckpointAt >= DurabilityCheckpointInterval)
                {
                    CheckpointShard(shard, durabilityStopwatch, checkpointAt);
                    durabilityCheckpointed = true;
                }

                if (shard.LastCatalogCheckpointAt == default ||
                    checkpointAt - shard.LastCatalogCheckpointAt >= CatalogCheckpointInterval ||
                    shard.SampleRows == shard.CapacityRows)
                {
                    if (shard.CheckpointedRows != shard.SampleRows)
                    {
                        CheckpointShard(shard, durabilityStopwatch, checkpointAt);
                        durabilityCheckpointed = true;
                    }

                    RegisterShard(
                        shard,
                        metadata.ExperimentRunId,
                        channelCount,
                        shard.SampleRows == shard.CapacityRows ? "ready" : "writing");
                    shard.LastCatalogCheckpointAt = checkpointAt;
                    catalogCheckpointed = true;
                }

                lastPath = shard.Hdf5Path;
                consumedRows += rowsToWrite;
                if (shard.SampleRows == shard.CapacityRows)
                {
                    shard.Session?.Dispose();
                    shard.Session = null;
                    activeShards.Remove(metadata.ExperimentRunId);
                }
            }

            var summary = catalogCheckpointed
                ? catalog.GetRunSummary(metadata.ExperimentRunId)
                : null;
            stopwatch.Stop();
            return new RealtimeRawPersistenceResult(
                lastPath ?? throw new InvalidOperationException("Raw shard persistence produced no artifact."),
                sampleRows,
                channelCount,
                stopwatch.Elapsed,
                durabilityStopwatch.Elapsed,
                durabilityCheckpointed,
                catalogCheckpointed,
                summary);
        }
        finally
        {
            ReturnValueBuffer(values);
        }
    }

    private ExperimentRunCatalogSummary? CompleteRun(Guid experimentRunId, bool publishReady)
    {
        if (activeShards.Remove(experimentRunId, out var shard))
        {
            if (publishReady)
            {
                var stopwatch = new Stopwatch();
                CheckpointAndCloseShard(shard, stopwatch);
                RegisterShard(
                    shard,
                    experimentRunId,
                    shard.Metadata.Device.MeasurementChannelCount,
                    "ready");
            }
            else
            {
                shard.Session?.Dispose();
                shard.Session = null;
            }
        }

        return publishReady ? catalog.GetRunSummary(experimentRunId) : null;
    }

    private void CheckpointShard(
        RawShardState shard,
        Stopwatch checkpointStopwatch,
        DateTimeOffset checkpointAt)
    {
        if (shard.SampleRows <= 0 || shard.CheckpointedRows == shard.SampleRows)
        {
            return;
        }

        checkpointStopwatch.Start();
        try
        {
            shardAppender.Checkpoint(
                shard.Session ?? throw new InvalidOperationException("Active raw shard session is missing."),
                CreateAppendContext(shard),
                shard.SampleRows);
        }
        finally
        {
            checkpointStopwatch.Stop();
        }

        shard.CheckpointedRows = shard.SampleRows;
        shard.LastDurabilityCheckpointAt = checkpointAt;
    }

    private void CheckpointAndCloseShard(RawShardState shard, Stopwatch checkpointStopwatch)
    {
        if (shard.SampleRows > 0 && shard.CheckpointedRows != shard.SampleRows)
        {
            CheckpointShard(shard, checkpointStopwatch, timeProvider.GetUtcNow());
        }

        shard.Session?.Dispose();
        shard.Session = null;
    }

    private static RawShardAppendContext CreateAppendContext(RawShardState shard) =>
        new(
            shard.Metadata.ExperimentRunId,
            shard.Metadata.SessionId,
            shard.SegmentSequence,
            shard.StartSampleIndex,
            shard.EndSampleIndex,
            shard.CapturedAt,
            shard.Metadata.Device,
            shard.Metadata.Excitation,
            shard.Metadata.Acquisition,
            shard.Metadata.Demodulation,
            shard.Discontinuities.ToArray(),
            shard.FrameRows,
            shard.CapacityRows);

    internal static long CalculateFrameRows(RealtimeRawPersistenceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var effectiveDwellUs = metadata.Excitation.Execution?.EffectiveTimeUs ??
                               metadata.Excitation.Excitation.CalculateTimeUs();
        var frameRows = Math.Round(
            metadata.Acquisition.SampleRateHz * effectiveDwellUs * EitSet.MeasurementChannelCount /
            1_000_000.0,
            MidpointRounding.AwayFromZero);
        if (!double.IsFinite(frameRows) || frameRows < 1 || frameRows > long.MaxValue)
        {
            throw new InvalidDataException("Actual DDS cadence cannot be represented as raw EIT frame rows.");
        }

        return checked((long)frameRows);
    }

    private RawShardState CreateShardState(
        RealtimeRawPersistenceMetadata metadata,
        string rawDirectory,
        long startSampleIndex,
        DateTimeOffset capturedAt,
        long frameRows,
        long capacityRows)
    {
        if (!nextShardSequences.TryGetValue(metadata.ExperimentRunId, out var sequence))
        {
            var existing = catalog.ListRawSegments(metadata.ExperimentRunId);
            sequence = existing.Count == 0 ? 0 : existing.Max(item => item.SegmentSequence) + 1;
        }

        nextShardSequences[metadata.ExperimentRunId] = checked(sequence + 1);
        return new RawShardState(
            metadata,
            sequence,
            Path.Combine(rawDirectory, $"raw_{sequence:D6}.h5"),
            startSampleIndex,
            capturedAt,
            frameRows,
            capacityRows);
    }

    private void RegisterShard(
        RawShardState shard,
        Guid experimentRunId,
        int channelCount,
        string status)
    {
        catalog.RegisterRawSegment(new RawSegmentCatalogRecord(
            experimentRunId,
            shard.SegmentSequence,
            layout.ToRelativeArtifactPath(shard.Hdf5Path),
            "/raw/adc_counts",
            shard.StartSampleIndex,
            shard.EndSampleIndex,
            shard.SampleRows,
            channelCount,
            shard.CapturedAt,
            status,
            shard.Discontinuities.Count > 0));
    }

    private void DisposeResources()
    {
        foreach (var shard in activeShards.Values)
        {
            try
            {
                shard.Session?.Dispose();
            }
            catch (Exception ex)
            {
                var message =
                    $"实时原始分片关闭失败：'{shard.Hdf5Path}'。文件可能仍被占用；" +
                    $"请停止后续归档/删除并检查诊断日志。{ex.Message}";
                Trace.TraceError($"{message} {ex}");
                PublishOperatorDiagnostic(message);
            }
        }

        activeShards.Clear();
        nextShardSequences.Clear();
        reusableValueBuffers.Clear();
    }

    internal void PublishOperatorDiagnostic(string message)
    {
        try
        {
            operatorDiagnostic?.Invoke(message);
        }
        catch (Exception diagnosticError) when (diagnosticError is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Trace.TraceError(
                $"Failed to publish raw persistence operator diagnostic: {diagnosticError}");
        }
    }

    private ushort[] RentValueBuffer(int length)
    {
        if (reusableValueBuffers.Remove(length, out var buffer))
        {
            return buffer;
        }

        return new ushort[length];
    }

    private void ReturnValueBuffer(ushort[] buffer)
    {
        if (!disposed &&
            reusableValueBuffers.Count < MaximumReusableValueBufferCount &&
            !reusableValueBuffers.ContainsKey(buffer.Length))
        {
            reusableValueBuffers.Add(buffer.Length, buffer);
        }
    }

    private static void ValidateIdentity<TContext>(
        RealtimeRawBatch<TContext> batch,
        RealtimeRawPersistenceMetadata metadata)
        where TContext : notnull
    {
        if (metadata.ExperimentRunId == Guid.Empty || metadata.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Realtime raw persistence requires non-empty run and session identities.");
        }

        if (!string.Equals(metadata.Device.SetLabel?.Trim(), metadata.Device.SetLabel, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(metadata.Device.SetLabel))
        {
            throw new ArgumentException("Realtime raw persistence requires a normalized set label.");
        }

        var expectedRows = batch.ValueCount / metadata.Device.MeasurementChannelCount;
        if (batch.ValueCount % metadata.Device.MeasurementChannelCount != 0 ||
            batch.EndSampleIndex - batch.StartSampleIndex != expectedRows)
        {
            throw new ArgumentException("Realtime raw batch sample range does not match its channel-row payload.");
        }
    }

    private static long EstimateArtifactBytes(long payloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBytes);
        return payloadBytes > long.MaxValue - MetadataAllowanceBytes
            ? long.MaxValue
            : payloadBytes + MetadataAllowanceBytes;
    }

    private sealed class RawShardState(
        RealtimeRawPersistenceMetadata metadata,
        int segmentSequence,
        string hdf5Path,
        long startSampleIndex,
        DateTimeOffset capturedAt,
        long frameRows,
        long capacityRows)
    {
        public RealtimeRawPersistenceMetadata Metadata { get; } = metadata;
        public int SegmentSequence { get; } = segmentSequence;
        public string Hdf5Path { get; } = hdf5Path;
        public long StartSampleIndex { get; } = startSampleIndex;
        public DateTimeOffset CapturedAt { get; } = capturedAt;
        public long FrameRows { get; } = frameRows;
        public long CapacityRows { get; } = capacityRows;
        public List<RawAcquisitionDiscontinuityEvent> Discontinuities { get; } = [];
        public RawShardHdf5WriteSession? Session { get; set; }
        public long EndSampleIndex { get; set; } = startSampleIndex;
        public long SampleRows { get; set; }
        public long CheckpointedRows { get; set; }
        public DateTimeOffset LastDurabilityCheckpointAt { get; set; }
        public DateTimeOffset LastCatalogCheckpointAt { get; set; }
    }
}
