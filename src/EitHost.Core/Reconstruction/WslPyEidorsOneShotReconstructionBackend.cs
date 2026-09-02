using System.Diagnostics;
using System.Text.Json;

namespace EitHost.Core.Reconstruction;

public sealed class WslPyEidorsOneShotReconstructionBackend : IRealtimeReconstructionBackend
{
    private const string DefaultNixExecutable = "/nix/var/nix/profiles/default/bin/nix";
    internal const string StructuredBackendErrorPrefix = "[backend-worker] backend-error-json ";
    private const string LegacyBackendErrorPrefix = "[backend-worker] backend error [";

    private readonly WslPyEidorsReconstructionOptions options;
    private readonly Hdf5ReconstructionResultReader resultReader;
    private int requestCounter;
    private bool disposed;

    public WslPyEidorsOneShotReconstructionBackend(
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
        try
        {
            var exchangeDirectory = options.ResolveExchangeDirectory();
            Directory.CreateDirectory(exchangeDirectory);
            var requestId = CreateRequestId(request);
            inputPath = Path.Combine(exchangeDirectory, $"{requestId}.request.json");
            outputPath = Path.Combine(exchangeDirectory, $"{requestId}.result.h5");
            await File.WriteAllTextAsync(
                inputPath,
                WslPyEidorsReconstructionBackend.BuildProfileRequestJson(request, options),
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var stopwatch = Stopwatch.StartNew();
            var startInfo = CreateStartInfo(inputPath, outputPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start wsl.exe PyEIDORS one-shot backend.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillBestEffort(process);
                throw;
            }

            stopwatch.Stop();
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var diagnostics =
                    $"command: {DescribeStartInfo(startInfo)}"
                    + Environment.NewLine
                    + $"exit: {process.ExitCode}"
                    + Environment.NewLine
                    + $"stdout: {TrimForMessage(stdout)}"
                    + Environment.NewLine
                    + $"stderr: {TrimForMessage(stderr)}";
                throw CreateBackendFailureException(
                    stderr,
                    stdout,
                    process.ExitCode,
                    diagnostics);
            }

            if (!File.Exists(outputPath))
            {
                throw PyEidorsReconstructionException.FromBackend(
                    "BackendOutputContractError",
                    "one-shot process exited successfully but did not produce the reconstruction result HDF5",
                    diagnostics:
                        $"command: {DescribeStartInfo(startInfo)}"
                        + Environment.NewLine
                        + $"output: {outputPath}"
                        + Environment.NewLine
                        + $"stdout: {TrimForMessage(stdout)}"
                        + Environment.NewLine
                        + $"stderr: {TrimForMessage(stderr)}");
            }

            try
            {
                return resultReader.Read(
                    outputPath,
                    request.BlockNumber,
                    stopwatch.Elapsed,
                    request.PersistResultFiles,
                    options.BackendRequiresCanonicalMeshIndex) with
                {
                    ReconstructionScaleStatus = request.ReconstructionScaleStatus,
                    ReconstructionScaleProvenance = request.ReconstructionScaleProvenance
                };
            }
            catch (Exception ex) when (ex is not PyEidorsReconstructionException)
            {
                throw PyEidorsReconstructionException.FromFrontendResult(outputPath, ex);
            }
        }
        catch (OperationCanceledException)
        {
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
            WslPyEidorsReconstructionBackend.DeleteTransientExchangeFiles(
                inputPath,
                outputPath,
                request.PersistResultFiles);
        }
    }

    public ProcessStartInfo CreateStartInfo(string inputPath, string outputPath)
    {
        var backendRepositoryPath = options.ResolveBackendRepositoryPath();
        var launchCommand = ResolveOneShotLaunchCommand(options);
        var wslInputPath = WslPathMapper.ToWslPath(inputPath);
        var wslOutputPath = WslPathMapper.ToWslPath(outputPath);
        if (TryCreateDirectNixStartInfo(
            backendRepositoryPath,
            launchCommand,
            wslInputPath,
            wslOutputPath,
            out var directStartInfo))
        {
            return directStartInfo;
        }

        var command = BuildCommand(
            backendRepositoryPath,
            launchCommand,
            wslInputPath,
            wslOutputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
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
        return startInfo;
    }

    public void Dispose()
    {
        disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal static string BuildCommand(
        string backendRepositoryPath,
        string launchCommand,
        string inputPath,
        string outputPath)
    {
        return string.Join(
            " ",
            "set -e;",
            "cd",
            WslPyEidorsReconstructionBackend.ShellQuote(backendRepositoryPath),
            "&&",
            WslPyEidorsReconstructionBackend.BuildWorkerEnvironmentPrefix(),
            launchCommand,
            "reconstruct",
            "--input",
            WslPyEidorsReconstructionBackend.ShellQuote(inputPath),
            "--output",
            WslPyEidorsReconstructionBackend.ShellQuote(outputPath));
    }

    internal static string ResolveOneShotLaunchCommand(WslPyEidorsReconstructionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkerLaunchCommand)
            && !options.UseNixDevelop
            && !string.Equals(options.BackendProfile, WslPyEidorsBackendManifest.CustomProfile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "尚未选择 PyEIDORS 后端路线。请先选择后端目录和路线。");
        }

        var command = !string.IsNullOrWhiteSpace(options.WorkerLaunchCommand)
            ? options.WorkerLaunchCommand.Trim()
            : BuildLaunchCommandFromWorkerParts(options);
        return StripTrailingServe(command);
    }

    private bool TryCreateDirectNixStartInfo(
        string backendRepositoryPath,
        string launchCommand,
        string inputPath,
        string outputPath,
        out ProcessStartInfo startInfo)
    {
        startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var tokens = SplitCommandLine(launchCommand);
        if (tokens.Count < 2 || !string.Equals(tokens[0], "nix", StringComparison.Ordinal) ||
            !IsSupportedDirectNixMode(tokens[1]))
        {
            return false;
        }

        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(options.DistroName);
        startInfo.ArgumentList.Add("--cd");
        startInfo.ArgumentList.Add(backendRepositoryPath);
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("env");
        startInfo.ArgumentList.Add("EIT_APP_BACKEND_WORKER_HDF5_COMPRESSION=off");
        startInfo.ArgumentList.Add("EIT_APP_BACKEND_WORKER_HDF5_SHUFFLE=off");
        startInfo.ArgumentList.Add(DefaultNixExecutable);
        for (var index = 1; index < tokens.Count; index++)
        {
            startInfo.ArgumentList.Add(tokens[index]);
        }

        if (string.Equals(tokens[1], "run", StringComparison.Ordinal) && !tokens.Contains("--", StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("--");
        }

        startInfo.ArgumentList.Add("reconstruct");
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        return true;
    }

    private static bool IsSupportedDirectNixMode(string token)
    {
        return string.Equals(token, "run", StringComparison.Ordinal) ||
            string.Equals(token, "develop", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SplitCommandLine(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        for (var index = 0; index < command.Length; index++)
        {
            var ch = command[index];
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                FlushToken(tokens, current);
                continue;
            }

            if ((ch == '\'' || ch == '"') && (quote == '\0' || quote == ch))
            {
                quote = quote == '\0' ? ch : '\0';
                continue;
            }

            current.Append(ch);
        }

        FlushToken(tokens, current);
        return tokens;
    }

    private static void FlushToken(List<string> tokens, System.Text.StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        tokens.Add(current.ToString());
        current.Clear();
    }

    private static string BuildLaunchCommandFromWorkerParts(WslPyEidorsReconstructionOptions options)
    {
        var workerExecutable = string.IsNullOrWhiteSpace(options.WorkerExecutable)
            ? throw new InvalidOperationException("PyEIDORS backend worker executable is empty.")
            : options.WorkerExecutable.Trim();
        var workerArguments = string.IsNullOrWhiteSpace(options.WorkerArguments)
            ? string.Empty
            : " " + options.WorkerArguments.Trim();
        var workerCommand = $"{workerExecutable}{workerArguments}";
        if (!options.UseNixDevelop)
        {
            return workerCommand;
        }

        var profile = string.IsNullOrWhiteSpace(options.NixDevelopProfile)
            ? string.Empty
            : " " + WslPyEidorsReconstructionBackend.ShellQuote(options.NixDevelopProfile.Trim());
        return $"nix develop{profile} -c {workerCommand}";
    }

    private static string StripTrailingServe(string command)
    {
        const string serveSuffix = " serve";
        return command.EndsWith(serveSuffix, StringComparison.Ordinal)
            ? command[..^serveSuffix.Length].TrimEnd()
            : command;
    }

    private string CreateRequestId(RealtimeReconstructionRequest request)
    {
        var sequence = Interlocked.Increment(ref requestCounter);
        var label = new string(request.SetLabel
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{label}_block{request.BlockNumber:000000}_{sequence:000000}";
    }

    private static void KillBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort cancellation
        }
    }

    private static string TrimForMessage(string value)
    {
        return value.Trim();
    }

    internal static PyEidorsReconstructionException CreateBackendFailureException(
        string stderr,
        string stdout,
        int exitCode,
        string diagnostics)
    {
        var failure = ParseBackendFailure(stderr, stdout, exitCode);
        return PyEidorsReconstructionException.FromBackend(
            failure.ErrorType,
            failure.Detail,
            failure.Traceback,
            diagnostics);
    }

    private static BackendFailurePayload ParseBackendFailure(
        string stderr,
        string stdout,
        int exitCode)
    {
        string? structuredPayloadError = null;
        foreach (var output in new[] { stderr, stdout })
        {
            if (TryParseStructuredBackendFailure(
                output,
                out var structuredFailure,
                out var parseError))
            {
                return structuredFailure;
            }

            structuredPayloadError ??= parseError;
        }

        var traceback = FirstNonEmpty(stderr, stdout);
        if (structuredPayloadError is not null)
        {
            return new BackendFailurePayload(
                "BackendErrorPayloadContractError",
                $"backend emitted malformed structured error payload: {structuredPayloadError}",
                traceback);
        }

        foreach (var output in new[] { stderr, stdout })
        {
            if (TryParseLegacyBackendFailure(output, out var legacyFailure))
            {
                return legacyFailure;
            }
        }

        BackendFailurePayload? wrapperFailure = null;
        foreach (var output in new[] { stderr, stdout })
        {
            foreach (var line in SplitOutputLines(output).Reverse())
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                var candidate = line[..separator].Trim();
                var simpleName = candidate.Split('.').LastOrDefault() ?? string.Empty;
                if (!IsExceptionType(candidate, simpleName))
                {
                    continue;
                }

                var detail = line[(separator + 1)..].Trim();
                var parsed = new BackendFailurePayload(
                    candidate,
                    string.IsNullOrWhiteSpace(detail) ? FirstNonEmpty(stderr, stdout) : detail,
                    FirstNonEmpty(output, traceback));
                if (string.Equals(simpleName, "ReconstructionExecutionError", StringComparison.Ordinal))
                {
                    wrapperFailure ??= parsed;
                    continue;
                }

                return parsed;
            }
        }

        return wrapperFailure ?? new BackendFailurePayload(
            $"ProcessExitCode{exitCode}",
            FirstNonEmpty(stderr, stdout, "backend process exited without an error message"),
            traceback);
    }

    private static bool TryParseStructuredBackendFailure(
        string output,
        out BackendFailurePayload failure,
        out string? parseError)
    {
        failure = null!;
        parseError = null;
        foreach (var line in SplitOutputLines(output).Reverse())
        {
            if (!line.StartsWith(StructuredBackendErrorPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var payloadJson = line[StructuredBackendErrorPrefix.Length..].Trim();
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                var origin = ReadPayloadString(root, "error_origin");
                var errorType = ReadPayloadString(root, "error_type");
                var detail = ReadPayloadString(root, "error", "detail", "error_msg");
                var traceback = ReadPayloadString(root, "traceback", "backend_traceback");
                if (!string.Equals(origin, "backend", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(errorType)
                    || string.IsNullOrWhiteSpace(detail)
                    || string.IsNullOrWhiteSpace(traceback))
                {
                    parseError ??=
                        "error_origin='backend', error_type, error and traceback are required strings";
                    continue;
                }

                failure = new BackendFailurePayload(
                    errorType,
                    detail,
                    traceback);
                return true;
            }
            catch (JsonException ex)
            {
                parseError ??= ex.Message;
            }
        }

        return false;
    }

    private static bool TryParseLegacyBackendFailure(
        string output,
        out BackendFailurePayload failure)
    {
        failure = null!;
        foreach (var line in SplitOutputLines(output).Reverse())
        {
            if (!line.StartsWith(LegacyBackendErrorPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var closingBracket = line.IndexOf(']', LegacyBackendErrorPrefix.Length);
            if (closingBracket <= LegacyBackendErrorPrefix.Length)
            {
                continue;
            }

            var errorType = line[LegacyBackendErrorPrefix.Length..closingBracket].Trim();
            if (!IsQualifiedExceptionType(errorType))
            {
                continue;
            }

            var detailStart = closingBracket + 1;
            if (detailStart < line.Length && line[detailStart] == ':')
            {
                detailStart++;
            }

            var detail = line[detailStart..].Trim();
            failure = new BackendFailurePayload(
                errorType,
                string.IsNullOrWhiteSpace(detail) ? FirstNonEmpty(output) : detail,
                FirstNonEmpty(output));
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string> SplitOutputLines(string output)
    {
        return output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsExceptionType(string candidate, string simpleName)
    {
        return IsQualifiedExceptionType(candidate)
            && (simpleName.EndsWith("Error", StringComparison.Ordinal)
                || simpleName.EndsWith("Exception", StringComparison.Ordinal));
    }

    private static bool IsQualifiedExceptionType(string value)
    {
        return value.Length > 0
            && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '.');
    }

    private static string? ReadPayloadString(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!.Trim();
            }
        }

        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private sealed record BackendFailurePayload(
        string ErrorType,
        string Detail,
        string? Traceback);

    private static string DescribeStartInfo(ProcessStartInfo startInfo)
    {
        return startInfo.ArgumentList.Count > 0
            ? startInfo.FileName + " " + string.Join(" ", startInfo.ArgumentList.Select(QuoteForDisplay))
            : startInfo.FileName + " " + startInfo.Arguments;
    }

    private static string QuoteForDisplay(string value)
    {
        return value.Any(char.IsWhiteSpace)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
