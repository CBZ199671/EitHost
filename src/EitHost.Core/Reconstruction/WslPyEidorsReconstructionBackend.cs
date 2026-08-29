using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace EitHost.Core.Reconstruction;

public sealed class WslPyEidorsReconstructionBackend : IRealtimeReconstructionBackend
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly WslPyEidorsReconstructionOptions options;
    private readonly Hdf5ReconstructionResultReader resultReader;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object processGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerDoneMessage>> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> recentStderr = new();
    private Process? process;
    private Task? stdoutTask;
    private Task? stderrTask;
    private int requestCounter;
    private bool disposed;

    public WslPyEidorsReconstructionBackend(
        WslPyEidorsReconstructionOptions? options = null,
        Hdf5ReconstructionResultReader? resultReader = null)
    {
        this.options = WslPyEidorsBackendManifest.ResolveConfiguredOrDefault(
            options ?? new WslPyEidorsReconstructionOptions());
        this.resultReader = resultReader ?? new Hdf5ReconstructionResultReader();
    }

    public async Task<RealtimeReconstructionResult> ReconstructAsync(
        RealtimeReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        string? inputPath = null;
        string? outputPath = null;
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exchangeDirectory = options.ResolveExchangeDirectory();
            Directory.CreateDirectory(exchangeDirectory);
            var requestId = CreateRequestId(request);
            inputPath = Path.Combine(exchangeDirectory, $"{requestId}.request.json");
            outputPath = Path.Combine(exchangeDirectory, $"{requestId}.result.h5");
            await File.WriteAllTextAsync(
                inputPath,
                BuildRequestJson(request),
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var worker = EnsureWorkerStarted();
            var done = await SendWorkerRequestAsync(
                worker,
                requestId,
                WslPathMapper.ToWslPath(inputPath),
                WslPathMapper.ToWslPath(outputPath),
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(done.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var details = string.IsNullOrWhiteSpace(done.Error)
                    ? GetRecentStderrText()
                    : done.Error;
                throw new InvalidOperationException($"PyEIDORS backend reconstruction failed: {details}");
            }

            var stopwatchElapsed = done.Elapsed;
            return resultReader.Read(
                outputPath,
                request.BlockNumber,
                stopwatchElapsed,
                request.PersistResultFiles) with
            {
                ReconstructionScaleStatus = request.ReconstructionScaleStatus,
                ReconstructionScaleProvenance = request.ReconstructionScaleProvenance
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestartWorkerAfterCanceledRequest();
            throw;
        }
        finally
        {
            DeleteTransientExchangeFiles(inputPath, outputPath, request.PersistResultFiles);
            requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (stdoutTask is not null)
        {
            try
            {
                await stdoutTask.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            }
            catch
            {
                // best-effort shutdown
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

    private Process EnsureWorkerStarted()
    {
        lock (processGate)
        {
            if (process is { HasExited: false } running)
            {
                return running;
            }

            var command = BuildWorkerCommand();
            AddRecentStderr("worker command: " + command);
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

            var started = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start wsl.exe PyEIDORS backend worker.");
            process = started;
            stderrTask = Task.Run(() => ReadStderrAsync(started));
            stdoutTask = Task.Run(() => ReadStdoutAsync(started));
            return started;
        }
    }

    private async Task<WorkerDoneMessage> SendWorkerRequestAsync(
        Process worker,
        string requestId,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (worker.HasExited)
        {
            throw new InvalidOperationException($"PyEIDORS backend worker exited early with code {worker.ExitCode}: {GetRecentStderrText()}");
        }

        var pendingRequest = new TaskCompletionSource<WorkerDoneMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(requestId, pendingRequest))
        {
            throw new InvalidOperationException($"Duplicate PyEIDORS backend request id: {requestId}");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = requestId,
                command = "reconstruct",
                input = inputPath,
                output = outputPath
            }, CompactJson)).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<WorkerDoneMessage>)state!).TrySetCanceled(),
                pendingRequest);
            var done = await pendingRequest.Task.ConfigureAwait(false);
            stopwatch.Stop();
            return done with { Elapsed = stopwatch.Elapsed };
        }
        catch
        {
            pending.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task ReadStdoutAsync(Process worker)
    {
        try
        {
            while (await worker.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WorkerProtocolMessage? message;
                try
                {
                    message = ParseWorkerProtocolMessage(line);
                }
                catch
                {
                    AddRecentStderr($"stdout(non-json): {line}");
                    continue;
                }

                if (message is null || string.IsNullOrWhiteSpace(message.Id))
                {
                    continue;
                }

                if (string.Equals(message.Type, "done", StringComparison.OrdinalIgnoreCase)
                    && pending.TryRemove(message.Id, out var completion))
                {
                    completion.TrySetResult(new WorkerDoneMessage(
                        message.Status ?? string.Empty,
                        message.Error ?? string.Empty,
                        TimeSpan.Zero));
                }
                else if (string.Equals(message.Type, "done", StringComparison.OrdinalIgnoreCase))
                {
                    AddRecentStderr($"stdout(done unmatched id={message.Id})");
                }
            }
        }
        catch (Exception ex)
        {
            AddRecentStderr("stdout reader failed: " + ex.Message);
        }
        finally
        {
            if (ClearCurrentWorker(worker))
            {
                AddRecentStderr(WorkerExitSummary(worker));
                await WaitForStderrDrainAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
                FailPendingRequests(new InvalidOperationException("PyEIDORS backend worker stdout closed: " + GetRecentStderrText()));
            }
        }
    }

    private async Task WaitForStderrDrainAsync(TimeSpan timeout)
    {
        var task = stderrTask;
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

    private async Task ReadStderrAsync(Process worker)
    {
        try
        {
            while (await worker.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    AddRecentStderr(line);
                }
            }
        }
        catch (Exception ex)
        {
            AddRecentStderr("stderr reader failed: " + ex.Message);
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

    private void RestartWorkerAfterCanceledRequest()
    {
        AddRecentStderr("request cancelled or timed out; restarting PyEIDORS worker");
        StopWorker(sendShutdown: false, wait: TimeSpan.FromMilliseconds(250));
    }

    private void StopWorker(bool sendShutdown, TimeSpan wait)
    {
        Process? worker;
        lock (processGate)
        {
            worker = process;
            process = null;
        }

        if (worker is null)
        {
            return;
        }

        try
        {
            FailPendingRequests(new OperationCanceledException("PyEIDORS worker stopped."));
            if (sendShutdown && !worker.HasExited)
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

            if (!worker.HasExited && !worker.WaitForExit((int)Math.Max(0, wait.TotalMilliseconds)))
            {
                worker.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort shutdown/restart
        }
        finally
        {
            worker.Dispose();
        }
    }

    private bool ClearCurrentWorker(Process worker)
    {
        lock (processGate)
        {
            if (!ReferenceEquals(process, worker))
            {
                return false;
            }

            process = null;
            return true;
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var item in pending.ToArray())
        {
            if (pending.TryRemove(item.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
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

    internal static string BuildRequestJson(RealtimeReconstructionRequest request)
    {
        var zeros = new double[RealtimeReconstructionRequest.BoundaryVoltageCount];
        var metadata = CreateMetadata(request);
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

    internal static Dictionary<string, object?> CreateMetadata(RealtimeReconstructionRequest request)
    {
        var route = RealtimeReconstructionRequest.NormalizeReconstructionRoute(request.ReconstructionRoute);
        var regularization = route switch
        {
            "laplace_rm" => "laplace",
            "curvature_rm" => "curvature",
            _ => "noser"
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["request_source"] = "EitHost realtime imaging",
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
            ["rm_device"] = "cpu",
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

    private string GetRecentStderrText()
    {
        lock (recentStderr)
        {
            return recentStderr.Count == 0
                ? "no backend stderr captured"
                : string.Join(Environment.NewLine, recentStderr);
        }
    }

    private static WorkerProtocolMessage? ParseWorkerProtocolMessage(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new WorkerProtocolMessage(
            GetString(root, "id"),
            GetString(root, "type"),
            GetString(root, "status"),
            GetString(root, "error"));
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : property.ToString()
            : null;
    }

    private sealed record WorkerProtocolMessage(
        string? Id,
        string? Type,
        string? Status,
        string? Error);

    private sealed record WorkerDoneMessage(
        string Status,
        string Error,
        TimeSpan Elapsed);
}
