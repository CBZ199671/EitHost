using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HDF.PInvoke;
using PureHDF;

namespace EitHost.Core.Storage.Hdf5;

internal static class Hdf5IncrementalStageAppender
{
    private const long CompressionThresholdBytes = 4 * 1024;
    private const int TargetChunkBytes = 128 * 1024;

    private static readonly IReadOnlyDictionary<string, string[]> StageOwnedPaths =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["demod"] = ["/demod", "/quality"],
            ["diagnostics"] = ["/diagnostics", "/replay_demod_override"],
            ["reference_candidates"] = ["/candidates"],
            ["reconstruction"] =
            [
                "/reconstruction",
                "/input",
                "/metadata/reconstruction_json"
            ]
        };

    private static readonly string[] CommonRunUpdates =
    [
        "experiment_run_id",
        "block_number",
        "source_start_sample_index",
        "source_end_sample_index",
        "acquired_at_utc",
        "processed_at_utc",
        "stage",
        "artifact_format"
    ];

    private static readonly string[] ReconstructionRunUpdates =
    [
        "processing_mode",
        "weight_policy_version",
        "reference_epoch"
    ];

    public static void Create(
        string filePath,
        H5File content,
        string stage,
        string blockRoot = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = OpenFileWithStrongClose(
            access => H5F.create(filePath, H5F.ACC_TRUNC, H5P.DEFAULT, access),
            $"create derived artifact '{filePath}'",
            leaseProbePath: filePath,
            leaseProbeAccess: FileAccess.ReadWrite);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        Exception? primaryException = null;
        try
        {
            var normalizedRoot = NormalizeRoot(blockRoot);
            EnsureGroupTree(file, normalizedRoot);
            WriteGroupChildren(file, normalizedRoot.Length == 0 ? "/" : normalizedRoot, content);
            PublishCompletionMarker(file, stage, normalizedRoot);
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            CloseFileChecked(file, $"close derived artifact '{filePath}'", primaryException);
        }
    }

    public static void CreateMigrated(string filePath, H5File content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = OpenFileWithStrongClose(
            access => H5F.create(filePath, H5F.ACC_TRUNC, H5P.DEFAULT, access),
            $"create migrated derived artifact '{filePath}'",
            leaseProbePath: filePath,
            leaseProbeAccess: FileAccess.ReadWrite);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        Exception? primaryException = null;
        try
        {
            WriteGroupChildren(file, "/", content);
            EnsureSuccess(
                H5F.flush(file, H5F.scope_t.GLOBAL),
                "flush migrated derived artifact");
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            CloseFileChecked(file, $"close migrated derived artifact '{filePath}'", primaryException);
        }
    }

    public static void Append(
        string destinationPath,
        H5File stageContent,
        string stage,
        string blockRoot = "")
    {
        ArgumentNullException.ThrowIfNull(stageContent);
        if (!StageOwnedPaths.TryGetValue(stage, out var ownedPaths))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown derived stage.");
        }

        var destination = OpenFileWithStrongClose(
            access => H5F.open(destinationPath, H5F.ACC_RDWR, access),
            $"open derived artifact '{destinationPath}' read-write",
            leaseProbePath: destinationPath,
            leaseProbeAccess: FileAccess.ReadWrite);
        using var nativeCall = Hdf5NativeCallGate.Enter();
        Exception? primaryException = null;
        try
        {
            var normalizedRoot = NormalizeRoot(blockRoot);
            EnsureGroupTree(destination, normalizedRoot);
            EnsureGroup(destination, Prefix(normalizedRoot, "/metadata"));
            EnsureGroup(destination, Prefix(normalizedRoot, "/metadata/run"));
            EnsureGroup(destination, Prefix(normalizedRoot, "/metadata/stages"));

            DeleteIfPresent(destination, Prefix(normalizedRoot, $"/metadata/stages/{stage}"));
            foreach (var path in ownedPaths)
            {
                DeleteIfPresent(destination, Prefix(normalizedRoot, path));
            }

            foreach (var path in ownedPaths.Where(path => !path.StartsWith("/metadata/", StringComparison.Ordinal)))
            {
                var name = path[1..];
                if (stageContent.TryGetValue(name, out var value) && value is H5Group group)
                {
                    var targetGroupPath = Prefix(normalizedRoot, path);
                    EnsureGroup(destination, targetGroupPath);
                    WriteGroupChildren(destination, targetGroupPath, group);
                }
            }

            var metadata = RequireGroup(stageContent, "metadata");
            var run = RequireGroup(metadata, "run");
            foreach (var name in CommonRunUpdates)
            {
                ReplaceValueIfPresent(
                    destination,
                    Prefix(normalizedRoot, "/metadata/run"),
                    run,
                    name);
            }

            if (string.Equals(stage, "reconstruction", StringComparison.Ordinal))
            {
                foreach (var name in ReconstructionRunUpdates)
                {
                    ReplaceValueIfPresent(
                        destination,
                        Prefix(normalizedRoot, "/metadata/run"),
                        run,
                        name);
                }

                if (metadata.TryGetValue("reconstruction_json", out var reconstructionJson))
                {
                    ReplaceValue(
                        destination,
                        Prefix(normalizedRoot, "/metadata/reconstruction_json"),
                        reconstructionJson);
                }
            }

            var stages = RequireGroup(metadata, "stages");
            var stageGroup = RequireGroup(stages, stage);
            var stagePath = Prefix(normalizedRoot, $"/metadata/stages/{stage}");
            EnsureGroup(destination, stagePath);
            WriteGroupChildren(destination, stagePath, stageGroup);
            PublishCompletionMarker(destination, stage, normalizedRoot);
        }
        catch (Exception ex)
        {
            primaryException = ex;
            throw;
        }
        finally
        {
            CloseFileChecked(
                destination,
                $"close derived artifact '{destinationPath}' after stage '{stage}'",
                primaryException);
        }
    }

    private static void PublishCompletionMarker(long file, string stage, string blockRoot = "")
    {
        EnsureSuccess(
            H5F.flush(file, H5F.scope_t.GLOBAL),
            "flush derived stage payload");
        WriteDataset(file, Prefix(blockRoot, $"/metadata/stages/{stage}/complete"), 1);
        EnsureSuccess(
            H5F.flush(file, H5F.scope_t.GLOBAL),
            "flush derived stage completion marker");
    }

    private static void WriteGroupChildren(long file, string path, H5Group group)
    {
        foreach (var (name, value) in group)
        {
            var childPath = path == "/" ? $"/{name}" : $"{path}/{name}";
            if (value is H5Group childGroup)
            {
                EnsureGroup(file, childPath);
                WriteGroupChildren(file, childPath, childGroup);
            }
            else
            {
                WriteDataset(file, childPath, value);
            }
        }
    }

    private static void ReplaceValueIfPresent(
        long file,
        string parentPath,
        H5Group source,
        string name)
    {
        if (source.TryGetValue(name, out var value))
        {
            ReplaceValue(file, $"{parentPath}/{name}", value);
        }
    }

    private static void ReplaceValue(long file, string path, object value)
    {
        DeleteIfPresent(file, path);
        WriteDataset(file, path, value);
    }

    private static void WriteDataset(long file, string path, object data)
    {
        switch (data)
        {
            case double[] values:
                WriteNumericArray(file, path, values, H5T.NATIVE_DOUBLE, sizeof(double));
                break;
            case int[] values:
                WriteNumericArray(file, path, values, H5T.NATIVE_INT32, sizeof(int));
                break;
            case long[,] values:
                WriteNumericMatrix(file, path, values, H5T.NATIVE_INT64, sizeof(long));
                break;
            case double[,] values:
                WriteNumericMatrix(file, path, values, H5T.NATIVE_DOUBLE, sizeof(double));
                break;
            case double value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_DOUBLE);
                break;
            case int value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_INT32);
                break;
            case uint value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_UINT32);
                break;
            case long value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_INT64);
                break;
            case ushort value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_UINT16);
                break;
            case byte value:
                WriteNumericScalar(file, path, [value], H5T.NATIVE_UCHAR);
                break;
            case bool value:
                WriteNumericScalar(file, path, [(byte)(value ? 1 : 0)], H5T.NATIVE_UCHAR);
                break;
            case string value:
                WriteString(file, path, value);
                break;
            case string[] values:
                WriteStringArray(file, path, values);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported native derived dataset type at '{path}': {data.GetType().FullName}.");
        }
    }

    private static void WriteNumericArray<T>(
        long file,
        string path,
        T[] values,
        long dataType,
        int elementSize)
        where T : unmanaged
    {
        var dimensions = new[] { checked((ulong)values.LongLength) };
        var dataspace = H5S.create_simple(1, dimensions, null);
        EnsureValid(dataspace, $"create dataspace for '{path}'");
        long creation = H5P.DEFAULT;
        try
        {
            if (values.LongLength * elementSize >= CompressionThresholdBytes)
            {
                creation = H5P.create(H5P.DATASET_CREATE);
                EnsureValid(creation, $"create dataset properties for '{path}'");
                var chunkLength = Math.Max(1, TargetChunkBytes / elementSize);
                EnsureSuccess(
                    H5P.set_chunk(
                        creation,
                        1,
                        [checked((ulong)Math.Min(values.LongLength, chunkLength))]),
                    $"set chunks for '{path}'");
                EnsureSuccess(H5P.set_shuffle(creation), $"set shuffle for '{path}'");
                EnsureSuccess(H5P.set_deflate(creation, 1), $"set deflate for '{path}'");
            }

            WritePinned(file, path, values, dataType, dataspace, creation);
        }
        finally
        {
            if (creation != H5P.DEFAULT)
            {
                H5P.close(creation);
            }

            H5S.close(dataspace);
        }
    }

    private static void WriteNumericScalar<T>(
        long file,
        string path,
        T[] value,
        long dataType)
        where T : unmanaged
    {
        var dataspace = H5S.create(H5S.class_t.SCALAR);
        EnsureValid(dataspace, $"create scalar dataspace for '{path}'");
        try
        {
            WritePinned(file, path, value, dataType, dataspace, H5P.DEFAULT);
        }
        finally
        {
            H5S.close(dataspace);
        }
    }

    private static void WriteNumericMatrix<T>(
        long file,
        string path,
        T[,] values,
        long dataType,
        int elementSize)
        where T : unmanaged
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var dimensions = new[] { checked((ulong)rows), checked((ulong)columns) };
        var dataspace = H5S.create_simple(2, dimensions, null);
        EnsureValid(dataspace, $"create matrix dataspace for '{path}'");
        long creation = H5P.DEFAULT;
        try
        {
            if (values.LongLength * elementSize >= CompressionThresholdBytes)
            {
                creation = H5P.create(H5P.DATASET_CREATE);
                EnsureValid(creation, $"create matrix dataset properties for '{path}'");
                var chunkRows = Math.Max(1, TargetChunkBytes / Math.Max(elementSize, columns * elementSize));
                EnsureSuccess(
                    H5P.set_chunk(
                        creation,
                        2,
                        [checked((ulong)Math.Min(rows, chunkRows)), checked((ulong)columns)]),
                    $"set matrix chunks for '{path}'");
                EnsureSuccess(H5P.set_shuffle(creation), $"set matrix shuffle for '{path}'");
                EnsureSuccess(H5P.set_deflate(creation, 1), $"set matrix deflate for '{path}'");
            }

            var dataset = H5D.create(
                file,
                path,
                dataType,
                dataspace,
                H5P.DEFAULT,
                creation,
                H5P.DEFAULT);
            EnsureValid(dataset, $"create matrix dataset '{path}'");
            var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
            try
            {
                EnsureSuccess(
                    H5D.write(
                        dataset,
                        dataType,
                        H5S.ALL,
                        H5S.ALL,
                        H5P.DEFAULT,
                        handle.AddrOfPinnedObject()),
                    $"write matrix dataset '{path}'");
            }
            finally
            {
                handle.Free();
                H5D.close(dataset);
            }
        }
        finally
        {
            if (creation != H5P.DEFAULT)
            {
                H5P.close(creation);
            }

            H5S.close(dataspace);
        }
    }

    private static void WritePinned<T>(
        long file,
        string path,
        T[] values,
        long dataType,
        long dataspace,
        long creation)
        where T : unmanaged
    {
        var dataset = H5D.create(
            file,
            path,
            dataType,
            dataspace,
            H5P.DEFAULT,
            creation,
            H5P.DEFAULT);
        EnsureValid(dataset, $"create dataset '{path}'");
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            EnsureSuccess(
                H5D.write(
                    dataset,
                    dataType,
                    H5S.ALL,
                    H5S.ALL,
                    H5P.DEFAULT,
                    handle.AddrOfPinnedObject()),
                $"write dataset '{path}'");
        }
        finally
        {
            handle.Free();
            H5D.close(dataset);
        }
    }

    private static void WriteString(long file, string path, string value)
    {
        var dataType = H5T.copy(H5T.C_S1);
        EnsureValid(dataType, $"copy string type for '{path}'");
        var dataspace = -1L;
        var dataset = -1L;
        var utf8 = IntPtr.Zero;
        var pointer = default(GCHandle);
        try
        {
            EnsureSuccess(H5T.set_size(dataType, H5T.VARIABLE), $"set string size for '{path}'");
            EnsureSuccess(H5T.set_cset(dataType, H5T.cset_t.UTF8), $"set UTF-8 for '{path}'");
            EnsureSuccess(H5T.set_strpad(dataType, H5T.str_t.NULLTERM), $"set string padding for '{path}'");
            dataspace = H5S.create(H5S.class_t.SCALAR);
            EnsureValid(dataspace, $"create string dataspace for '{path}'");
            dataset = H5D.create(
                file,
                path,
                dataType,
                dataspace,
                H5P.DEFAULT,
                H5P.DEFAULT,
                H5P.DEFAULT);
            EnsureValid(dataset, $"create string dataset '{path}'");

            var bytes = Encoding.UTF8.GetBytes(value + '\0');
            utf8 = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, utf8, bytes.Length);
            var addresses = new[] { utf8 };
            pointer = GCHandle.Alloc(addresses, GCHandleType.Pinned);
            EnsureSuccess(
                H5D.write(
                    dataset,
                    dataType,
                    H5S.ALL,
                    H5S.ALL,
                    H5P.DEFAULT,
                    pointer.AddrOfPinnedObject()),
                $"write string dataset '{path}'");
        }
        finally
        {
            if (pointer.IsAllocated)
            {
                pointer.Free();
            }

            if (utf8 != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(utf8);
            }

            if (dataset >= 0)
            {
                H5D.close(dataset);
            }

            if (dataspace >= 0)
            {
                H5S.close(dataspace);
            }

            H5T.close(dataType);
        }
    }

    private static void WriteStringArray(long file, string path, string[] values)
    {
        var dataType = H5T.copy(H5T.C_S1);
        EnsureValid(dataType, $"copy string-array type for '{path}'");
        var dataspace = -1L;
        var dataset = -1L;
        var pointers = new IntPtr[values.Length];
        var pointerHandle = default(GCHandle);
        try
        {
            EnsureSuccess(H5T.set_size(dataType, H5T.VARIABLE), $"set string-array size for '{path}'");
            EnsureSuccess(H5T.set_cset(dataType, H5T.cset_t.UTF8), $"set string-array UTF-8 for '{path}'");
            EnsureSuccess(H5T.set_strpad(dataType, H5T.str_t.NULLTERM), $"set string-array padding for '{path}'");
            dataspace = H5S.create_simple(1, [checked((ulong)values.LongLength)], null);
            EnsureValid(dataspace, $"create string-array dataspace for '{path}'");
            dataset = H5D.create(
                file,
                path,
                dataType,
                dataspace,
                H5P.DEFAULT,
                H5P.DEFAULT,
                H5P.DEFAULT);
            EnsureValid(dataset, $"create string-array dataset '{path}'");

            for (var index = 0; index < values.Length; index++)
            {
                var bytes = Encoding.UTF8.GetBytes(values[index] + '\0');
                pointers[index] = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, pointers[index], bytes.Length);
            }

            pointerHandle = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            EnsureSuccess(
                H5D.write(
                    dataset,
                    dataType,
                    H5S.ALL,
                    H5S.ALL,
                    H5P.DEFAULT,
                    pointerHandle.AddrOfPinnedObject()),
                $"write string-array dataset '{path}'");
        }
        finally
        {
            if (pointerHandle.IsAllocated)
            {
                pointerHandle.Free();
            }

            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }

            if (dataset >= 0)
            {
                H5D.close(dataset);
            }

            if (dataspace >= 0)
            {
                H5S.close(dataspace);
            }

            H5T.close(dataType);
        }
    }

    internal static void WriteContent(long file, string path, H5Group group)
    {
        var normalized = NormalizeRoot(path);
        EnsureGroupTree(file, normalized);
        WriteGroupChildren(file, normalized.Length == 0 ? "/" : normalized, group);
    }

    internal static void ReplaceContentValue(long file, string path, object value) =>
        ReplaceValue(file, path, value);

    private static H5Group RequireGroup(H5Group parent, string name)
    {
        if (!parent.TryGetValue(name, out var value) || value is not H5Group group)
        {
            throw new InvalidDataException($"Derived stage is missing group '{name}'.");
        }

        return group;
    }

    private static void EnsureGroup(long file, string path)
    {
        if (H5L.exists(file, path, H5P.DEFAULT) > 0)
        {
            return;
        }

        var group = H5G.create(file, path, H5P.DEFAULT, H5P.DEFAULT, H5P.DEFAULT);
        EnsureValid(group, $"create group '{path}'");
        EnsureSuccess(H5G.close(group), $"close group '{path}'");
    }

    internal static void EnsureGroupTree(long file, string path)
    {
        var normalized = NormalizeRoot(path);
        if (normalized.Length == 0)
        {
            return;
        }

        var current = string.Empty;
        foreach (var part in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += $"/{part}";
            EnsureGroup(file, current);
        }
    }

    private static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path.Trim(), "/", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return $"/{path.Trim().Trim('/')}";
    }

    private static string Prefix(string root, string path)
    {
        var normalizedRoot = NormalizeRoot(root);
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        return $"{normalizedRoot}{normalizedPath}";
    }

    private static void DeleteIfPresent(long file, string path)
    {
        if (H5L.exists(file, path, H5P.DEFAULT) > 0)
        {
            EnsureSuccess(H5L.delete(file, path, H5P.DEFAULT), $"delete '{path}'");
        }
    }

    private static void SuppressNativeDiagnostics()
    {
        H5E.set_auto(H5E.DEFAULT, null!, IntPtr.Zero);
    }

    private static void EnsureValid(long identifier, string operation)
    {
        if (identifier < 0)
        {
            throw CreateNativeFailure(operation);
        }
    }

    private static void EnsureSuccess(int result, string operation)
    {
        if (result < 0)
        {
            throw CreateNativeFailure(operation);
        }
    }

    internal static long OpenFileWithStrongClose(
        Func<long, long> open,
        string operation,
        Action<TimeSpan>? delay = null,
        string? leaseProbePath = null,
        FileAccess leaseProbeAccess = FileAccess.Read)
    {
        ArgumentNullException.ThrowIfNull(open);
        if (Hdf5NativeCallGate.IsEnteredByCurrentThread)
        {
            throw new InvalidOperationException(
                $"Native HDF5 file open must start outside the process gate so lease backoff can release it: {operation}.");
        }

        var failedDuringOpen = false;
        Func<Exception, bool>? shouldRetry = leaseProbePath is null
            ? null
            : ex => AtomicFileCommitter.IsTransientLeaseFailure(ex) ||
                (failedDuringOpen && AtomicFileCommitter.IsFileBlockedByTransientLease(
                    leaseProbePath,
                    leaseProbeAccess));
        return AtomicFileCommitter.ExecuteWithTransientLeaseRetry(
            operation,
            () =>
            {
                failedDuringOpen = false;
                return OpenFileAttempt(
                    open,
                    operation,
                    () => failedDuringOpen = true);
            },
            delay,
            shouldRetry);
    }

    private static long OpenFileAttempt(
        Func<long, long> open,
        string operation,
        Action markOpenFailure)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        SuppressNativeDiagnostics();
        var access = H5P.create(H5P.FILE_ACCESS);
        EnsureValid(access, $"create strong-close file access for {operation}");
        long file = -1;
        Exception? primaryException = null;
        try
        {
            EnsureSuccess(
                H5P.set_fclose_degree(access, H5F.close_degree_t.STRONG),
                $"set strong-close file access for {operation}");
            try
            {
                file = open(access);
                EnsureValid(file, operation);
            }
            catch
            {
                markOpenFailure();
                throw;
            }

            return file;
        }
        catch (Exception ex)
        {
            primaryException = ex;
            if (file >= 0)
            {
                CloseFileChecked(file, $"close file after failed {operation}", ex);
            }

            throw;
        }
        finally
        {
            if (H5P.close(access) < 0)
            {
                var closeFailure = CreateNativeFailure(
                    $"close strong-close file access for {operation}");
                if (primaryException is null)
                {
                    if (file >= 0)
                    {
                        CloseFileChecked(file, $"close file after access-property failure for {operation}", closeFailure);
                    }

                    throw closeFailure;
                }

                try
                {
                    primaryException.Data["HDF5 access-property close failure"] = closeFailure.ToString();
                    Trace.TraceError(
                        $"HDF5 access-property close failed while preserving primary exception: {closeFailure}");
                }
                catch (Exception diagnosticError) when (diagnosticError is not (
                    OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // Diagnostics must not replace the primary HDF5 operation failure.
                }
            }
        }
    }

    internal static void CloseFileChecked(
        long file,
        string operation,
        Exception? primaryException,
        Func<long, int>? close = null,
        Func<long, long>? getOpenObjectCount = null)
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        var usesNativeClose = close is null;
        // H5Fget_obj_count reports application-visible HDF5 identifiers, not metadata-cache entries.
        // A count above the file identifier itself therefore proves a child handle escaped its owner.
        var openObjectCount = getOpenObjectCount?.Invoke(file)
            ?? (usesNativeClose
                ? H5F.get_obj_count(file, H5F.OBJ_ALL | H5F.OBJ_LOCAL)
                : 1);
        close ??= H5F.close;
        var closeResult = close(file);
        if (closeResult >= 0 && openObjectCount == 1)
        {
            return;
        }

        var closeFailure = closeResult < 0
            ? CreateNativeFailure(operation)
            : new IOException(
                openObjectCount < 0
                    ? $"HDF5 object-count query failed before successful close: {operation}."
                    : $"HDF5 file closed with {openObjectCount} open local objects instead of only its file handle: {operation}.");
        if (primaryException is null)
        {
            throw closeFailure;
        }

        try
        {
            primaryException.Data["HDF5 close failure"] = closeFailure.ToString();
            Trace.TraceError($"HDF5 close failed while preserving primary exception: {closeFailure}");
        }
        catch (Exception diagnosticError) when (diagnosticError is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Close diagnostics must never replace the primary HDF5 operation failure.
        }
    }

    private static IOException CreateNativeFailure(string operation)
    {
        var nativeStack = CaptureNativeErrorStack();
        var message = $"HDF5 operation failed: {operation}.";
        if (!string.IsNullOrWhiteSpace(nativeStack))
        {
            message += $"{Environment.NewLine}Native HDF5 error stack:{Environment.NewLine}{nativeStack}";
        }

        return new IOException(message);
    }

    internal static string CaptureNativeErrorStack()
    {
        using var nativeCall = Hdf5NativeCallGate.Enter();
        const int maximumEntries = 16;
        const int maximumFieldLength = 512;
        var stack = H5E.get_current_stack();
        if (stack < 0)
        {
            return string.Empty;
        }

        try
        {
            var entries = new List<string>();
            H5E.walk_t walker = (uint index, ref H5E.error_t error, IntPtr _) =>
            {
                if (entries.Count >= maximumEntries)
                {
                    return 0;
                }

                var function = BoundNativeField(error.func_name, maximumFieldLength, "unknown");
                var file = BoundNativeField(error.file_name, maximumFieldLength, "unknown");
                var description = BoundNativeField(error.desc, maximumFieldLength, "no description");
                entries.Add($"#{index} {function} ({file}:{error.line}): {description}");
                return 0;
            };
            var walkResult = H5E.walk(
                stack,
                H5E.direction_t.H5E_WALK_DOWNWARD,
                walker,
                IntPtr.Zero);
            GC.KeepAlive(walker);
            return walkResult < 0
                ? string.Empty
                : string.Join(Environment.NewLine, entries);
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            H5E.close_stack(stack);
        }
    }

    private static string BoundNativeField(string? value, int maximumLength, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }
}
