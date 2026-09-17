using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Reconstruction;

public sealed class WslPyEidorsReconstructionBackend : IRealtimeReconstructionBackend, IPseudo3dKrigingBackend
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly WslPyEidorsReconstructionOptions options;
    private readonly Hdf5ReconstructionResultReader resultReader;
    private readonly Hdf5Pseudo3dKrigingProtocol pseudo3dProtocol;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object processGate = new();
    private readonly Queue<string> recentStderr = new();
    private WorkerSession? currentSession;
    private int requestCounter;
    private bool disposed;

    public WslPyEidorsReconstructionBackend(
        WslPyEidorsReconstructionOptions? options = null,
        Hdf5ReconstructionResultReader? resultReader = null,
        Hdf5Pseudo3dKrigingProtocol? pseudo3dProtocol = null)
    {
        this.options = WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(
            options ?? new WslPyEidorsReconstructionOptions());
        this.resultReader = resultReader ?? new Hdf5ReconstructionResultReader();
        this.pseudo3dProtocol = pseudo3dProtocol ?? new Hdf5Pseudo3dKrigingProtocol();
    }

    public async Task<RealtimeReconstructionResult> ReconstructAsync(
        RealtimeReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        string? inputPath = null;
        string? outputPath = null;
        WorkerSession? requestSession = null;
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestId = CreateRequestId(request);
            requestSession = EnsureWorkerStarted();
            var memory = !request.PersistResultFiles
                && await SupportsMemoryTransportAsync(requestSession, cancellationToken).ConfigureAwait(false);
            WorkerDoneMessage done;
            if (memory)
            {
                var payload = Encoding.UTF8.GetBytes(BuildProfileRequestJson(request, options));
                done = await SendWorkerRequestCoreAsync(
                    requestSession, requestId, "reconstruct", "", "", cancellationToken,
                    payload, "json").ConfigureAwait(false);
            }
            else
            {
                var exchangeDirectory = options.ResolveExchangeDirectory();
                Directory.CreateDirectory(exchangeDirectory);
                inputPath = Path.Combine(exchangeDirectory, $"{requestId}.request.json");
                outputPath = Path.Combine(exchangeDirectory, $"{requestId}.result.h5");
                await File.WriteAllTextAsync(
                    inputPath, BuildProfileRequestJson(request, options), cancellationToken).ConfigureAwait(false);
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                done = await SendWorkerRequestAsync(
                    requestSession, requestId, "reconstruct",
                    WslPathMapper.ToWslPath(inputPath), WslPathMapper.ToWslPath(outputPath),
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.Equals(done.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var details = string.IsNullOrWhiteSpace(done.Error)
                    ? GetRecentStderrText(requestSession)
                    : done.Error;
                if (!string.IsNullOrWhiteSpace(done.ErrorOrigin)
                    && !string.Equals(done.ErrorOrigin, "backend", StringComparison.OrdinalIgnoreCase))
                {
                    throw PyEidorsReconstructionException.FromBackend(
                        "WorkerProtocolError",
                        $"worker declared unsupported error_origin '{done.ErrorOrigin}': {details}",
                        done.Traceback,
                        GetRecentStderrText(requestSession));
                }

                throw PyEidorsReconstructionException.FromBackend(
                    done.ErrorType,
                    details,
                    done.Traceback,
                    GetRecentStderrText(requestSession));
            }

            if (!memory && !File.Exists(outputPath))
            {
                throw PyEidorsReconstructionException.FromBackend(
                    "BackendOutputContractError",
                    "worker reported success but did not produce the reconstruction result HDF5",
                    diagnostics: outputPath);
            }

            var stopwatchElapsed = done.Elapsed;
            try
            {
                RealtimeReconstructionResult result;
                if (memory)
                {
                    using var data = new MemoryStream(
                        done.Payload ?? throw new InvalidDataException("Worker omitted memory reconstruction result."),
                        writable: false);
                    result = resultReader.Read(data, request.BlockNumber, stopwatchElapsed,
                        options.BackendRequiresCanonicalMeshIndex);
                }
                else
                {
                    result = resultReader.Read(outputPath!, request.BlockNumber, stopwatchElapsed,
                        request.PersistResultFiles, options.BackendRequiresCanonicalMeshIndex);
                }
                return result with
                {
                    ReconstructionScaleStatus = request.ReconstructionScaleStatus,
                    ReconstructionScaleProvenance = request.ReconstructionScaleProvenance
                };
            }
            catch (Exception ex) when (ex is not PyEidorsReconstructionException)
            {
                throw PyEidorsReconstructionException.FromFrontendResult(outputPath ?? "memory HDF5", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestSession is not null)
            {
                RestartWorkerAfterCanceledRequest(requestSession);
            }

            throw;
        }
        catch (PyEidorsReconstructionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw PyEidorsReconstructionException.FromFrontendProcessing(
                "后端启动/传输桥接",
                ex);
        }
        finally
        {
            DeleteTransientExchangeFiles(inputPath, outputPath, request.PersistResultFiles);
            requestGate.Release();
        }
    }

    public async Task<Pseudo3dKrigingResult> InterpolatePseudo3dAsync(
        Pseudo3dKrigingRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        string? inputPath = null;
        string? outputPath = null;
        WorkerSession? requestSession = null;
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestId = CreatePseudo3dRequestId();
            requestSession = EnsureWorkerStarted();
            var memory = await SupportsMemoryTransportAsync(requestSession, cancellationToken).ConfigureAwait(false);
            WorkerDoneMessage done;
            if (memory)
            {
                using var requestData = new WorkerPayloadBuffer();
                pseudo3dProtocol.WriteRequest(requestData, request);
                WorkerBinaryProtocol.ValidatePayloadSize(checked((int)requestData.Length));
                done = await SendWorkerRequestCoreAsync(
                    requestSession, requestId, "pseudo3d_kriging", "", "", cancellationToken,
                    requestData.GetBuffer().AsMemory(0, checked((int)requestData.Length)),
                    "hdf5").ConfigureAwait(false);
            }
            else
            {
                var exchangeDirectory = options.ResolveExchangeDirectory();
                Directory.CreateDirectory(exchangeDirectory);
                inputPath = Path.Combine(exchangeDirectory, $"{requestId}.request.h5");
                outputPath = Path.Combine(exchangeDirectory, $"{requestId}.result.h5");
                pseudo3dProtocol.WriteRequest(inputPath, request);
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
                done = await SendWorkerRequestAsync(
                    requestSession, requestId, "pseudo3d_kriging",
                    WslPathMapper.ToWslPath(inputPath), WslPathMapper.ToWslPath(outputPath),
                    cancellationToken).ConfigureAwait(false);
            }
            if (!string.Equals(done.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var details = string.IsNullOrWhiteSpace(done.Error)
                    ? GetRecentStderrText(requestSession)
                    : done.Error;
                throw PyEidorsReconstructionException.FromBackend(
                    done.ErrorType,
                    details,
                    done.Traceback,
                    GetRecentStderrText(requestSession));
            }

            if (!memory && !File.Exists(outputPath))
            {
                throw PyEidorsReconstructionException.FromBackend(
                    "BackendOutputContractError",
                    "worker reported success but did not produce the pseudo-3D Kriging result HDF5",
                    diagnostics: outputPath);
            }

            try
            {
                if (memory)
                {
                    using var data = new MemoryStream(
                        done.Payload ?? throw new InvalidDataException("Worker omitted memory Kriging result."),
                        writable: false);
                    return pseudo3dProtocol.ReadResult(data, done.Elapsed);
                }
                return pseudo3dProtocol.ReadResult(outputPath!, done.Elapsed);
            }
            catch (Exception ex) when (ex is not PyEidorsReconstructionException)
            {
                throw PyEidorsReconstructionException.FromFrontendResult(outputPath ?? "memory HDF5", ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (requestSession is not null)
            {
                RestartWorkerAfterCanceledRequest(requestSession);
            }

            throw;
        }
        catch (PyEidorsReconstructionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw PyEidorsReconstructionException.FromFrontendProcessing(
                "伪三维 Kriging 后端启动/传输桥接",
                ex);
        }
        finally
        {
            DeleteTransientExchangeFiles(inputPath, outputPath, persistResultFiles: false);
            requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorkerSession? session;
        lock (processGate)
        {
            session = currentSession;
        }

        Dispose();
        if (session?.StdoutTask is not null)
        {
            try
            {
                await session.StdoutTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                // best-effort shutdown
            }
        }

        if (session?.ReaperTask is not null)
        {
            try
            {
                await session.ReaperTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                ObserveLateFault(session.ReaperTask);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        requestGate.Dispose();
        StopWorker(sendShutdown: true, wait: TimeSpan.FromMilliseconds(500));
    }

    private WorkerSession EnsureWorkerStarted()
    {
        lock (processGate)
        {
            if (currentSession is { } running && IsReusableWorkerSession(running))
            {
                return running;
            }

            if (currentSession is { IsTerminating: true } terminating
                && IsProcessRunning(terminating.Process))
            {
                throw PyEidorsReconstructionException.FromBackend(
                    "WorkerTerminationPending",
                    $"previous worker process {TryGetProcessId(terminating.Process)} is still terminating",
                    diagnostics: GetRecentStderrText(terminating));
            }

            var command = BuildWorkerCommand();
            var startInfo = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(options.DistroName);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("bash");
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);

            var started = Hdf5ChildProcess.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start wsl.exe PyEIDORS backend worker.");
            var session = new WorkerSession(started);
            currentSession = session;
            AddRecentStderr(session, "worker command: " + command);
            session.StderrTask = Task.Run(() => ReadStderrAsync(session));
            session.StdoutTask = Task.Run(() => ReadStdoutAsync(session));
            return session;
        }
    }

    private async Task<bool> SupportsMemoryTransportAsync(WorkerSession session, CancellationToken cancellationToken)
    {
        if (session.MemoryTransportSupported is { } cached)
            return cached;
        var requestId = $"capabilities_{Interlocked.Increment(ref requestCounter)}";
        var done = await SendWorkerRequestAsync(
            session, requestId, "capabilities", "", "", cancellationToken).ConfigureAwait(false);
        session.MemoryTransportSupported = done.Status == "ok"
            && done.Metadata is { ValueKind: JsonValueKind.Object } metadata
            && metadata.TryGetProperty("transports", out var transports)
            && transports.ValueKind == JsonValueKind.Array
            && transports.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && item.GetString() == WorkerBinaryProtocol.Transport);
        return session.MemoryTransportSupported.Value;
    }

    private Task<WorkerDoneMessage> SendWorkerRequestAsync(
        WorkerSession session, string requestId, string command,
        string inputPath, string outputPath, CancellationToken cancellationToken) =>
        SendWorkerRequestCoreAsync(session, requestId, command, inputPath, outputPath, cancellationToken);

    private async Task<WorkerDoneMessage> SendWorkerRequestCoreAsync(
        WorkerSession session,
        string requestId,
        string command,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? payload = null,
        string? payloadFormat = null)
    {
        var worker = session.Process;
        var pendingRequest = new TaskCompletionSource<WorkerDoneMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.Pending.TryAdd(requestId, pendingRequest))
        {
            throw new InvalidOperationException($"Duplicate PyEIDORS backend request id: {requestId}");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            Exception? unavailable = null;
            lock (processGate)
            {
                if (!ReferenceEquals(currentSession, session) || session.IsTerminating || !IsProcessRunning(worker))
                {
                    unavailable = session.TerminalFailure ?? PyEidorsReconstructionException.FromBackend(
                        "WorkerProcessExit",
                        WorkerExitSummary(worker),
                        diagnostics: GetRecentStderrText(session));
                }
            }

            if (unavailable is not null)
            {
                throw unavailable;
            }

            if (payload is { } binary)
            {
                WorkerBinaryProtocol.ValidatePayloadSize(binary.Length);
                await worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    id = requestId,
                    command,
                    transport = WorkerBinaryProtocol.Transport,
                    payload_bytes = binary.Length,
                    payload_format = payloadFormat,
                    rm_cache_persistence = "memory"
                }, CompactJson) + "\n");
                var input = worker.StandardInput.BaseStream;
                await input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await input.WriteAsync(binary, cancellationToken).ConfigureAwait(false);
                await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = requestId,
                    command,
                    input = inputPath,
                    output = outputPath
                }, CompactJson)).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<WorkerDoneMessage>)state!).TrySetCanceled(),
                pendingRequest);
            var done = await pendingRequest.Task.ConfigureAwait(false);
            stopwatch.Stop();
            return done with { Elapsed = stopwatch.Elapsed };
        }
        catch
        {
            session.Pending.TryRemove(requestId, out _);
            if (payload is not null)
                StopWorker(session, PyEidorsReconstructionException.FromBackend(
                    "WorkerProtocolError", "Memory transfer interrupted; this request was not replayed."),
                    sendShutdown: false, wait: TimeSpan.FromMilliseconds(250));
            throw;
        }
    }

    private async Task ReadStdoutAsync(WorkerSession session)
    {
        var worker = session.Process;

        void FailProtocol(string detail, Exception? cause = null)
        {
            AddRecentStderr(session, $"stdout(protocol-error): {detail}");
            var diagnostics = cause is null
                ? GetRecentStderrText(session)
                : cause + Environment.NewLine + GetRecentStderrText(session);
            var failure = PyEidorsReconstructionException.FromBackend(
                "WorkerProtocolError",
                detail,
                diagnostics: diagnostics);
            StopWorker(
                session,
                failure,
                sendShutdown: false,
                wait: TimeSpan.FromMilliseconds(250));
        }

        Exception? readerFailure = null;
        try
        {
            var protocol = new WorkerBinaryProtocol(worker.StandardOutput.BaseStream);
            while (await protocol.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (IsGmshInformationalStdoutLine(line))
                {
                    AddRecentStderr(session, $"stdout(native-info): {line}");
                    continue;
                }

                WorkerProtocolMessage message;
                try
                {
                    message = ParseWorkerProtocolMessage(line);
                }
                catch (Exception ex)
                {
                    FailProtocol($"worker emitted invalid JSON-lines protocol output: {line}", ex);
                    return;
                }

                var payload = message.PayloadBytes > 0
                    ? await protocol.ReadPayloadAsync(message.PayloadBytes).ConfigureAwait(false)
                    : null;
                if (string.Equals(message.Type, "done", StringComparison.OrdinalIgnoreCase)
                    && session.Pending.TryRemove(message.Id, out var completion))
                {
                    completion.TrySetResult(new WorkerDoneMessage(
                        message.Status!,
                        message.Error ?? string.Empty,
                        message.ErrorOrigin ?? string.Empty,
                        message.ErrorType ?? string.Empty,
                        message.Traceback ?? string.Empty,
                        TimeSpan.Zero) { Payload = payload, Metadata = message.Metadata });
                }
                else if (string.Equals(message.Type, "done", StringComparison.OrdinalIgnoreCase))
                {
                    if (!session.Pending.IsEmpty)
                    {
                        FailProtocol($"worker returned unmatched done request id '{message.Id}'.");
                        return;
                    }

                    AddRecentStderr(session, $"stdout(done unmatched id={message.Id}, no pending request)");
                }
                else if (string.Equals(message.Type, "progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (session.Pending.ContainsKey(message.Id))
                    {
                        continue;
                    }

                    if (!session.Pending.IsEmpty)
                    {
                        FailProtocol($"worker returned unmatched progress request id '{message.Id}'.");
                        return;
                    }

                    AddRecentStderr(session, $"stdout(progress unmatched id={message.Id}, no pending request)");
                }
            }
        }
        catch (Exception ex)
        {
            readerFailure = ex;
            AddRecentStderr(session, "stdout reader failed: " + ex.Message);
        }
        finally
        {
            var summary = WorkerExitSummary(worker);
            if (readerFailure is not null)
            {
                summary += "; stdout reader failed: " + readerFailure.Message;
            }

            var preliminaryFailure = PyEidorsReconstructionException.FromBackend(
                "WorkerProcessExit",
                summary,
                diagnostics: GetRecentStderrText(session));
            if (session.TryBeginTermination(preliminaryFailure))
            {
                AddRecentStderr(session, summary);
                var terminated = StopWorkerProcess(
                    session,
                    sendShutdown: false,
                    wait: TimeSpan.FromMilliseconds(250));
                await WaitForStderrDrainAsync(session, TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                var finalFailure = PyEidorsReconstructionException.FromBackend(
                    "WorkerProcessExit",
                    summary,
                    diagnostics: GetRecentStderrText(session));
                session.SetTerminalFailure(finalFailure);
                FailPendingRequests(session, finalFailure);
                CompleteWorkerTermination(session, terminated);
            }
        }
    }

    private static async Task WaitForStderrDrainAsync(WorkerSession session, TimeSpan timeout)
    {
        var task = session.StderrTask;
        if (task is null || task.IsCompleted)
        {
            return;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort diagnostics only; never let stderr drain hide the real worker EOF.
        }
    }

    private static string WorkerExitSummary(Process worker)
    {
        try
        {
            return worker.HasExited
                ? $"backend worker process exited with code {worker.ExitCode}"
                : "backend worker stdout closed while process was still running";
        }
        catch (Exception ex)
        {
            return "backend worker exit status unavailable: " + ex.Message;
        }
    }

    private async Task ReadStderrAsync(WorkerSession session)
    {
        var worker = session.Process;
        try
        {
            while (await worker.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    AddRecentStderr(session, line);
                }
            }
        }
        catch (Exception ex)
        {
            AddRecentStderr(session, "stderr reader failed: " + ex.Message);
        }
    }

    private string BuildWorkerCommand()
    {
        return $"cd {ShellQuote(options.ResolveBackendRepositoryPath())} && {BuildWorkerLaunchCommand()}";
    }

    private string BuildWorkerLaunchCommand()
    {
        if (!string.IsNullOrWhiteSpace(options.WorkerLaunchCommand))
        {
            return $"{BuildWorkerEnvironmentPrefix()} exec {options.WorkerLaunchCommand.Trim()}";
        }

        if (!options.UseNixDevelop
            && !string.Equals(options.BackendProfile, WslPyEidorsBackendManifest.CustomProfile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "尚未选择 PyEIDORS 后端路线。请先在实时成像设置中选择后端目录和路线。");
        }

        var workerExecutable = string.IsNullOrWhiteSpace(options.WorkerExecutable)
            ? throw new InvalidOperationException("PyEIDORS backend worker executable is empty.")
            : options.WorkerExecutable.Trim();
        var workerArguments = string.IsNullOrWhiteSpace(options.WorkerArguments)
            ? string.Empty
            : " " + options.WorkerArguments.Trim();
        var workerCommand = $"{workerExecutable}{workerArguments}";
        if (!options.UseNixDevelop)
        {
            return $"{BuildWorkerEnvironmentPrefix()} exec {workerCommand}";
        }

        var profile = string.IsNullOrWhiteSpace(options.NixDevelopProfile)
            ? string.Empty
            : " " + ShellQuote(options.NixDevelopProfile.Trim());
        return $"{BuildWorkerEnvironmentPrefix()} exec nix develop{profile} -c {workerCommand}";
    }

    internal static string BuildWorkerEnvironmentPrefix()
    {
        return "EIT_APP_BACKEND_WORKER_HDF5_COMPRESSION=off EIT_APP_BACKEND_WORKER_HDF5_SHUFFLE=off";
    }

    internal static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private void RestartWorkerAfterCanceledRequest(WorkerSession session)
    {
        AddRecentStderr(session, "request cancelled or timed out; restarting PyEIDORS worker");
        StopWorker(
            session,
            new OperationCanceledException("PyEIDORS request was cancelled; worker generation stopped."),
            sendShutdown: false,
            wait: TimeSpan.FromMilliseconds(250));
    }

    private void StopWorker(bool sendShutdown, TimeSpan wait)
    {
        WorkerSession? session;
        lock (processGate)
        {
            session = currentSession;
        }

        if (session is null)
        {
            return;
        }

        StopWorker(
            session,
            new OperationCanceledException("PyEIDORS worker stopped."),
            sendShutdown,
            wait);
    }

    private void StopWorker(
        WorkerSession session,
        Exception failure,
        bool sendShutdown,
        TimeSpan wait)
    {
        if (!session.TryBeginTermination(failure))
        {
            return;
        }

        FailPendingRequests(session, failure);
        var terminated = StopWorkerProcess(session, sendShutdown, wait);
        CompleteWorkerTermination(session, terminated);
    }

    private bool StopWorkerProcess(WorkerSession session, bool sendShutdown, TimeSpan wait)
    {
        var worker = session.Process;
        if (sendShutdown && IsProcessRunning(worker))
        {
            try
            {
                var id = "shutdown-" + Interlocked.Increment(ref requestCounter).ToString(CultureInfo.InvariantCulture);
                worker.StandardInput.WriteLine(JsonSerializer.Serialize(new
                {
                    id,
                    command = "shutdown"
                }, CompactJson));
                worker.StandardInput.Flush();
            }
            catch
            {
                // The process may already be exiting.
            }
        }

        if (WaitForExit(worker, wait))
        {
            return true;
        }

        TryKill(worker, entireProcessTree: true);
        if (WaitForExit(worker, TimeSpan.FromMilliseconds(250)))
        {
            return true;
        }

        TryKill(worker, entireProcessTree: false);
        return WaitForExit(worker, TimeSpan.FromMilliseconds(250));
    }

    private void CompleteWorkerTermination(WorkerSession session, bool terminated)
    {
        if (terminated)
        {
            ClearCurrentWorker(session);
            DisposeWorkerProcess(session);
            return;
        }

        AddRecentStderr(
            session,
            $"worker process {TryGetProcessId(session.Process)} did not terminate promptly; background reaper scheduled");
        session.ReaperTask = Task.Run(async () =>
        {
            try
            {
                var retryDelay = TimeSpan.FromMilliseconds(250);
                while (IsProcessRunning(session.Process))
                {
                    TryKill(session.Process, entireProcessTree: true);
                    if (WaitForExit(session.Process, TimeSpan.FromMilliseconds(250)))
                    {
                        break;
                    }

                    TryKill(session.Process, entireProcessTree: false);
                    if (WaitForExit(session.Process, TimeSpan.FromMilliseconds(250)))
                    {
                        break;
                    }

                    await Task.Delay(retryDelay).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromMilliseconds(Math.Min(5_000, retryDelay.TotalMilliseconds * 2));
                }
            }
            finally
            {
                if (IsProcessRunning(session.Process))
                {
                    AddRecentStderr(
                        session,
                        $"worker process {TryGetProcessId(session.Process)} reaper stopped before process exit; backend remains unavailable");
                }
                else
                {
                    AddRecentStderr(session, $"worker process {TryGetProcessId(session.Process)} reaper completed");
                    ClearCurrentWorker(session);
                    DisposeWorkerProcess(session);
                }
            }
        });
        ObserveLateFault(session.ReaperTask);
    }

    private static void ObserveLateFault(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }

        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static string TryGetProcessId(Process process)
    {
        try
        {
            return process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            return "<unknown>";
        }
    }

    private static bool WaitForExit(Process worker, TimeSpan wait)
    {
        try
        {
            var milliseconds = (int)Math.Clamp(wait.TotalMilliseconds, 0, int.MaxValue);
            return !IsProcessRunning(worker)
                || worker.WaitForExit(milliseconds);
        }
        catch
        {
            return !IsProcessRunning(worker);
        }
    }

    private static void TryKill(Process worker, bool entireProcessTree)
    {
        try
        {
            if (IsProcessRunning(worker))
            {
                if (entireProcessTree)
                {
                    worker.Kill(entireProcessTree: true);
                }
                else
                {
                    worker.Kill();
                }
            }
        }
        catch
        {
            // The second plain-process attempt remains available if tree termination fails.
        }
    }

    private static void DisposeWorkerProcess(WorkerSession session)
    {
        try
        {
            session.Process.Dispose();
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private bool ClearCurrentWorker(WorkerSession session)
    {
        lock (processGate)
        {
            if (!ReferenceEquals(currentSession, session))
            {
                return false;
            }

            currentSession = null;
            return true;
        }
    }

    private static void FailPendingRequests(WorkerSession session, Exception exception)
    {
        foreach (var item in session.Pending.ToArray())
        {
            if (session.Pending.TryRemove(item.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private static bool IsProcessRunning(Process worker)
    {
        try
        {
            return !worker.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReusableWorkerSession(WorkerSession session)
    {
        return !session.IsTerminating && IsProcessRunning(session.Process);
    }

    internal static void DeleteTransientExchangeFiles(string? inputPath, string? outputPath, bool persistResultFiles)
    {
        DeleteFileBestEffort(inputPath);
        if (!persistResultFiles)
        {
            DeleteFileBestEffort(outputPath);
        }
    }

    private static void DeleteFileBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup must not break the realtime display path.
        }
    }

    private string CreateRequestId(RealtimeReconstructionRequest request)
    {
        var sequence = Interlocked.Increment(ref requestCounter);
        var label = new string(request.SetLabel
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{label}_block{request.BlockNumber:000000}_{sequence:000000}";
    }

    private string CreatePseudo3dRequestId()
    {
        var sequence = Interlocked.Increment(ref requestCounter);
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_pseudo3d_kriging_{sequence:000000}";
    }

    internal static string BuildRequestJson(RealtimeReconstructionRequest request)
    {
        return BuildProfileRequestJson(request, null);
    }

    internal static string BuildProfileRequestJson(
        RealtimeReconstructionRequest request,
        WslPyEidorsReconstructionOptions? options)
    {
        var zeros = new double[RealtimeReconstructionRequest.BoundaryVoltageCount];
        var metadata = CreateMetadata(request, options);
        var payload = new
        {
            reference_frame = new
            {
                real = request.ReferenceVoltage208,
                imag = zeros,
                timestamp = request.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
                frame_index = Math.Max(0, request.BlockNumber - 1),
                metadata
            },
            target_frame = new
            {
                real = request.TargetVoltage208,
                imag = zeros,
                timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds() / 1000.0,
                frame_index = request.BlockNumber,
                metadata
            },
            use_part = "real",
            measurement_weight = request.MeasurementWeight208,
            method = "gn-difference",
            regularization_alpha = request.DifferenceLambda,
            max_iterations = 1,
            mesh_dimension = 2,
            mesh_refinement = request.MeshSize,
            metadata
        };
        return JsonSerializer.Serialize(payload, CompactJson);
    }

    internal static Dictionary<string, object?> CreateMetadata(
        RealtimeReconstructionRequest request,
        WslPyEidorsReconstructionOptions? options)
    {
        var route = RealtimeReconstructionRequest.NormalizeReconstructionRoute(request.ReconstructionRoute);
        var useGpu = options?.BackendRequiresGpu == true;
        var requiresAmgx = options?.BackendRequiresAmgx == true;
        var regularization = route switch
        {
            "laplace_rm" => "laplace",
            "curvature_rm" => "curvature",
            _ => "noser"
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["request_source"] = "EitHost realtime imaging",
            ["backend_profile"] = options?.BackendProfile ?? string.Empty,
            ["backend_profile_requires_gpu"] = useGpu,
            ["backend_profile_requires_amgx"] = requiresAmgx,
            ["backend_profile_requires_canonical_mesh_index"] = options?.BackendRequiresCanonicalMeshIndex == true,
            ["set_label"] = request.SetLabel,
            ["block_number"] = request.BlockNumber,
            ["persist_result_files"] = request.PersistResultFiles,
            ["n_elec"] = 16,
            ["n_rings"] = 1,
            ["mesh_dimension"] = 2,
            ["mesh_size"] = request.MeshSize,
            ["radius"] = 1.0,
            ["geometry_scale_to_m"] = 1.0,
            ["stim_pattern"] = "{ad}",
            ["meas_pattern"] = "{ad}",
            ["measurement_protocol"] = "eidors_full_3d",
            ["rotate_meas"] = true,
            ["use_meas_current"] = false,
            ["use_meas_current_next"] = 0,
            ["stim_direction"] = "ccw",
            ["meas_direction"] = "ccw",
            ["stim_first_positive"] = false,
            ["drive_mode"] = "normalized",
            ["drive_value"] = 1.0,
            ["reconstruction_scale_status"] = request.ReconstructionScaleStatus,
            ["reconstruction_scale_provenance"] = request.ReconstructionScaleProvenance,
            ["reconstruction_output_unit"] = request.ReconstructionScaleStatus == ReconstructionScale.PhysicalCalibrated
                ? "S/m"
                : "model_relative",
            ["frequency_hz"] = request.ExcitationFrequencyHz,
            ["excitation_channel_cycles"] = request.ExcitationChannelCycles,
            ["eit_value_mode"] = "amplitude_only",
            ["input_mode"] = "adjacent_pair_voltage",
            ["input_layout"] = "row_major_16x13_208",
            ["measurement_weights"] = request.MeasurementWeight208,
            ["measurement_weight_policy"] = request.WeightPolicyVersion,
            ["measurement_weight_count"] = request.MeasurementWeight208.Count,
            ["measurement_weight_min"] = request.MeasurementWeight208.Count == 0 ? 0.0 : request.MeasurementWeight208.Min(),
            ["measurement_weight_max"] = request.MeasurementWeight208.Count == 0 ? 0.0 : request.MeasurementWeight208.Max(),
            ["dynamic_kalman_enabled"] = request.DynamicKalman is not null,
            ["dynamic_kalman_session_id"] = request.DynamicKalman?.SessionId,
            ["dynamic_kalman_fingerprint"] = request.DynamicKalman?.Fingerprint,
            ["dynamic_kalman_reset"] = request.DynamicKalman?.ResetSession ?? false,
            ["dynamic_kalman_innovation_candidate"] = request.DynamicKalman?.InnovationCandidate ?? false,
            ["dynamic_kalman_upstream_latency_frames"] = request.DynamicKalman?.UpstreamLatencyFrames ?? 2,
            ["dynamic_kalman_process_noise_relative_std"] = request.DynamicKalman?.ProcessNoiseRelativeStd,
            ["dynamic_kalman_measurement_noise_relative_std"] = request.DynamicKalman?.MeasurementNoiseRelativeStd,
            ["dynamic_kalman_initial_relative_std"] = request.DynamicKalman?.InitialRelativeStd,
            ["dynamic_kalman_transition_decay_per_block"] = request.DynamicKalman?.TransitionDecayPerBlock,
            ["dynamic_kalman_innovation_gate"] = request.DynamicKalman?.InnovationGate,
            ["dynamic_kalman_nis_threshold_per_dof"] = request.DynamicKalman?.NisThresholdPerDof,
            ["dynamic_kalman_max_variance_inflation"] = request.DynamicKalman?.MaxVarianceInflation,
            ["dynamic_kalman_mode"] = request.DynamicKalman?.Mode,
            ["dynamic_kalman_max_measurement_state_product"] = request.DynamicKalman?.MaxMeasurementStateProduct,
            ["dynamic_kalman_static_noser_anchor_relative_std"] = request.DynamicKalman?.StaticNoserAnchorRelativeStd,
            ["dynamic_kalman_static_noser_anchor_minimum_gain"] = request.DynamicKalman?.StaticNoserAnchorMinimumGain,
            ["dynamic_kalman_static_guard_rms_ratio"] = request.DynamicKalman?.StaticGuardRmsRatio,
            ["dynamic_kalman_static_guard_robust_ratio"] = request.DynamicKalman?.StaticGuardRobustRatio,
            ["dynamic_kalman_static_guard_minimum_deviation_relative"] = request.DynamicKalman?.StaticGuardMinimumDeviationRelative,
            ["reconstruction_runtime"] = "single_step_cached",
            ["simulation_inverse_route"] = route,
            ["simulation_inverse_route_kind"] = "rm",
            ["simulation_inverse_debug_route"] = false,
            ["rm_route_requires_artifact"] = true,
            ["rm_auto_build"] = true,
            ["rm_regularization"] = regularization,
            ["rm_form"] = "measurement",
            ["rm_output_display_mode"] = "absolute_sigma",
            ["rm_artifact_dir"] = ".pyeidors_cache/eithost_realtime_rm",
            ["online_hot_path"] = "rm_matmul",
            ["difference_mode"] = "raw",
            ["difference_orientation"] = request.DifferenceOrientation,
            ["difference_preset"] = route,
            ["absolute_preset"] = "eidors_abs_gn",
            ["difference_lambda"] = request.DifferenceLambda,
            ["lambda_eff"] = request.DifferenceLambda,
            ["lambda_eff_custom_enabled"] = request.CustomLambdaEnabled,
            ["device"] = "cpu",
            ["rm_device"] = useGpu ? "cuda" : "cpu",
            ["petsc_device"] = "cpu",
            ["forward_backend"] = "dolfinx",
            ["forward_solver_preset"] = "auto",
            ["forward_mat_solve"] = "off",
            ["acceleration_profile"] = "default"
        };
    }

    private void AddRecentStderr(string line)
    {
        lock (recentStderr)
        {
            recentStderr.Enqueue(line);
            while (recentStderr.Count > 40)
            {
                recentStderr.Dequeue();
            }
        }
    }

    private void AddRecentStderr(WorkerSession session, string line)
    {
        session.AddDiagnostic(line);
        AddRecentStderr(line);
    }

    private string GetRecentStderrText()
    {
        lock (recentStderr)
        {
            return recentStderr.Count == 0
                ? "no backend stderr captured"
                : string.Join(Environment.NewLine, recentStderr);
        }
    }

    private static string GetRecentStderrText(WorkerSession session)
    {
        return session.GetDiagnosticText();
    }

    private static WorkerProtocolMessage ParseWorkerProtocolMessage(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var id = GetString(root, "id");
        var type = GetString(root, "type");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidDataException("worker protocol message omitted request id");
        }

        if (!string.Equals(type, "done", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(type, "progress", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"worker protocol message has unsupported type '{type}'");
        }

        var status = GetString(root, "status");
        if (string.Equals(type, "done", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"worker done message has unsupported status '{status ?? "<missing>"}'");
        }

        var payloadBytes = 0;
        if (root.TryGetProperty("payload_bytes", out var length))
        {
            if (length.ValueKind != JsonValueKind.Number || !length.TryGetInt32(out payloadBytes))
                throw new InvalidDataException("worker payload_bytes must be an integer");
            WorkerBinaryProtocol.ValidatePayloadSize(payloadBytes);
            if (GetString(root, "transport") != WorkerBinaryProtocol.Transport
                || GetString(root, "payload_format") != "hdf5" || type != "done" || status != "ok")
                throw new InvalidDataException("worker binary result has invalid transport, format or status");
        }
        else if (root.TryGetProperty("transport", out _))
        {
            throw new InvalidDataException("worker binary result omitted payload_bytes");
        }

        return new WorkerProtocolMessage(
            id!,
            type!,
            status,
            GetString(root, "error"),
            GetString(root, "error_origin"),
            GetString(root, "error_type"),
            GetString(root, "traceback"))
        {
            PayloadBytes = payloadBytes,
            Metadata = root.TryGetProperty("metadata", out var metadata) ? metadata.Clone() : null
        };
    }

    private static bool IsGmshInformationalStdoutLine(string line)
    {
        var remainder = line.AsSpan().TrimStart();
        if (!remainder.StartsWith("Info", StringComparison.Ordinal))
        {
            return false;
        }

        remainder = remainder["Info".Length..].TrimStart();
        return remainder.StartsWith(":", StringComparison.Ordinal);
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"worker protocol field '{propertyName}' must be a string or null");
        }

        return property.GetString();
    }

    private sealed class WorkerSession
    {
        private readonly object terminationGate = new();
        private readonly Queue<string> recentDiagnostics = new();
        private Exception? terminalFailure;
        private int terminating;

        public WorkerSession(Process process)
        {
            Process = process;
        }

        public Process Process { get; }
        public bool? MemoryTransportSupported { get; set; }

        public ConcurrentDictionary<string, TaskCompletionSource<WorkerDoneMessage>> Pending { get; } =
            new(StringComparer.Ordinal);

        public Task? StdoutTask { get; set; }

        public Task? StderrTask { get; set; }

        public Task? ReaperTask { get; set; }

        public bool IsTerminating => Volatile.Read(ref terminating) != 0;

        public Exception? TerminalFailure => Volatile.Read(ref terminalFailure);

        public bool TryBeginTermination(Exception failure)
        {
            lock (terminationGate)
            {
                if (terminating != 0)
                {
                    return false;
                }

                terminalFailure = failure;
                Volatile.Write(ref terminating, 1);
                return true;
            }
        }

        public void SetTerminalFailure(Exception failure)
        {
            Volatile.Write(ref terminalFailure, failure);
        }

        public void AddDiagnostic(string line)
        {
            lock (recentDiagnostics)
            {
                recentDiagnostics.Enqueue(line);
                while (recentDiagnostics.Count > 40)
                {
                    recentDiagnostics.Dequeue();
                }
            }
        }

        public string GetDiagnosticText()
        {
            lock (recentDiagnostics)
            {
                return recentDiagnostics.Count == 0
                    ? "no backend stderr captured"
                    : string.Join(Environment.NewLine, recentDiagnostics);
            }
        }
    }

    private sealed record WorkerProtocolMessage(
        string Id,
        string Type,
        string? Status,
        string? Error,
        string? ErrorOrigin,
        string? ErrorType,
        string? Traceback)
    {
        public int PayloadBytes { get; init; }
        public JsonElement? Metadata { get; init; }
    }

    private sealed record WorkerDoneMessage(
        string Status,
        string Error,
        string ErrorOrigin,
        string ErrorType,
        string Traceback,
        TimeSpan Elapsed)
    {
        public byte[]? Payload { get; init; }
        public JsonElement? Metadata { get; init; }
    }
}
