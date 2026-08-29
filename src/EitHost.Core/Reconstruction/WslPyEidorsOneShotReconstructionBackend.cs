using System.Diagnostics;

namespace EitHost.Core.Reconstruction;

public sealed class WslPyEidorsOneShotReconstructionBackend : IRealtimeReconstructionBackend
{
    private const string DefaultNixExecutable = "/nix/var/nix/profiles/default/bin/nix";

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
                WslPyEidorsReconstructionBackend.BuildRequestJson(request),
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
                throw new InvalidOperationException(
                    "PyEIDORS one-shot reconstruction failed."
                    + Environment.NewLine
                    + $"command: {DescribeStartInfo(startInfo)}"
                    + Environment.NewLine
                    + $"exit: {process.ExitCode}"
                    + Environment.NewLine
                    + $"stdout: {TrimForMessage(stdout)}"
                    + Environment.NewLine
                    + $"stderr: {TrimForMessage(stderr)}");
            }

            if (!File.Exists(outputPath))
            {
                throw new FileNotFoundException(
                    "PyEIDORS one-shot reconstruction did not produce result HDF5."
                    + Environment.NewLine
                    + $"command: {DescribeStartInfo(startInfo)}"
                    + Environment.NewLine
                    + $"stdout: {TrimForMessage(stdout)}"
                    + Environment.NewLine
                    + $"stderr: {TrimForMessage(stderr)}",
                    outputPath);
            }

            return resultReader.Read(
                outputPath,
                request.BlockNumber,
                stopwatch.Elapsed,
                request.PersistResultFiles) with
            {
                ReconstructionScaleStatus = request.ReconstructionScaleStatus,
                ReconstructionScaleProvenance = request.ReconstructionScaleProvenance
            };
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
        value = value.Trim();
        return value.Length <= 4000 ? value : value[^4000..];
    }

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
