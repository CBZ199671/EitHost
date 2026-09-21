using System.Diagnostics;
using System.Text;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Reconstruction;

internal static class WslTextFileReader
{
    internal const int MaximumUtf8ByteCount = 1024 * 1024;

    private const int ProbeByteCount = MaximumUtf8ByteCount + 1;
    private const int MaximumRetainedStderrByteCount = 4096;
    internal static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultDistroReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PipeDrainWait = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string ReadAllText(string distroName, string linuxAbsoluteFilePath)
    {
        var startInfo = CreateStartInfo(distroName, linuxAbsoluteFilePath);
        EnsureDistroReady(distroName);
        return ReadAllTextAsync(
                startInfo,
                distroName,
                linuxAbsoluteFilePath,
                DefaultReadTimeout,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    internal static bool FileExists(string distroName, string linuxAbsoluteFilePath)
    {
        var startInfo = CreateFileExistsStartInfo(distroName, linuxAbsoluteFilePath);
        EnsureDistroReady(distroName);
        return FileExistsAsync(
                startInfo,
                distroName,
                linuxAbsoluteFilePath,
                DefaultReadTimeout,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    internal static ProcessStartInfo CreateStartInfo(
        string distroName,
        string linuxAbsoluteFilePath)
    {
        ValidateLocation(distroName, linuxAbsoluteFilePath);

        var startInfo = CreateWslStartInfo(distroName);
        startInfo.ArgumentList.Add("/usr/bin/head");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(ProbeByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(linuxAbsoluteFilePath);
        return startInfo;
    }

    internal static ProcessStartInfo CreateDistroReadyStartInfo(string distroName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);

        var startInfo = CreateWslStartInfo(distroName);
        startInfo.ArgumentList.Add("/usr/bin/true");
        return startInfo;
    }

    internal static ProcessStartInfo CreateFileExistsStartInfo(
        string distroName,
        string linuxAbsoluteFilePath)
    {
        ValidateLocation(distroName, linuxAbsoluteFilePath);

        var startInfo = CreateWslStartInfo(distroName);
        startInfo.ArgumentList.Add("/usr/bin/test");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(linuxAbsoluteFilePath);
        return startInfo;
    }

    internal static void EnsureDistroReady(string distroName)
    {
        EnsureDistroReadyAsync(
                CreateDistroReadyStartInfo(distroName),
                distroName,
                DefaultDistroReadyTimeout,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    internal static async Task EnsureDistroReadyAsync(
        ProcessStartInfo startInfo,
        string distroName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        BoundedProcessCapture capture;
        try
        {
            capture = await RunBoundedCaptureAsync(
                startInfo,
                distroName,
                linuxAbsoluteFilePath: null,
                MaximumRetainedStderrByteCount,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new WslDistroNotReadyException(distroName, timeout, ex.Message, ex);
        }

        if (capture.ExitCode != 0)
        {
            throw new WslDistroNotReadyException(
                distroName,
                timeout,
                BuildMessage(
                    distroName,
                    linuxAbsoluteFilePath: null,
                    $"readiness probe exited with code {capture.ExitCode}; stderr={FormatStderr(capture.StandardError)}"));
        }
    }

    internal static async Task<string> ReadAllTextAsync(
        ProcessStartInfo startInfo,
        string distroName,
        string linuxAbsoluteFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var capture = await RunBoundedCaptureAsync(
            startInfo,
            distroName,
            linuxAbsoluteFilePath,
            ProbeByteCount,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"process exited with code {capture.ExitCode}; stderr={FormatStderr(capture.StandardError)}"));
        }

        var stdout = capture.StandardOutput;
        if (stdout.Truncated || stdout.Bytes.Length > MaximumUtf8ByteCount)
        {
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"UTF-8 text exceeds the {MaximumUtf8ByteCount}-byte limit"));
        }

        try
        {
            var bytes = stdout.Bytes.AsSpan();
            if (bytes.StartsWith(Encoding.UTF8.Preamble))
            {
                bytes = bytes[Encoding.UTF8.Preamble.Length..];
            }

            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                "file is not valid UTF-8 text"), ex);
        }
    }

    internal static async Task<bool> FileExistsAsync(
        ProcessStartInfo startInfo,
        string distroName,
        string linuxAbsoluteFilePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var capture = await RunBoundedCaptureAsync(
            startInfo,
            distroName,
            linuxAbsoluteFilePath,
            MaximumRetainedStderrByteCount,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return capture.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"file probe exited with code {capture.ExitCode}; stderr={FormatStderr(capture.StandardError)}"))
        };
    }

    private static ProcessStartInfo CreateWslStartInfo(string distroName)
    {
        var startInfo = new ProcessStartInfo("wsl.exe")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--distribution");
        startInfo.ArgumentList.Add(distroName);
        startInfo.ArgumentList.Add("--exec");
        return startInfo;
    }

    private static async Task<BoundedProcessCapture> RunBoundedCaptureAsync(
        ProcessStartInfo startInfo,
        string distroName,
        string? linuxAbsoluteFilePath,
        int maximumRetainedStdoutByteCount,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ValidateLocation(distroName, linuxAbsoluteFilePath);
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException("Both stdout and stderr must be redirected.", nameof(startInfo));
        }

        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException("Shell execution is not permitted.", nameof(startInfo));
        }

        if (maximumRetainedStdoutByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRetainedStdoutByteCount));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!Hdf5ChildProcess.Start(process))
            {
                throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");
            }
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"could not start '{startInfo.FileName}': {ex.Message}"), ex);
        }

        using var drainCancellation = new CancellationTokenSource();
        Task<StreamCapture> stdoutTask = Task.FromResult(StreamCapture.Empty);
        Task<StreamCapture> stderrTask = Task.FromResult(StreamCapture.Empty);
        Task exitTask = Task.CompletedTask;
        Task captureTask = Task.CompletedTask;
        try
        {
            stdoutTask = DrainStreamAsync(
                process.StandardOutput.BaseStream,
                maximumRetainedStdoutByteCount,
                drainCancellation.Token);
            stderrTask = DrainStreamAsync(
                process.StandardError.BaseStream,
                MaximumRetainedStderrByteCount,
                drainCancellation.Token);
            exitTask = process.WaitForExitAsync(CancellationToken.None);
            captureTask = Task.WhenAll(exitTask, stdoutTask, stderrTask);

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await captureTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await captureTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException ex)
        {
            await TerminateAndObserveAsync(
                process,
                captureTask,
                drainCancellation).ConfigureAwait(false);
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"timed out after {timeout.TotalSeconds:0.###} seconds"), ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAndObserveAsync(
                process,
                captureTask,
                drainCancellation).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            await TerminateAndObserveAsync(
                process,
                captureTask,
                drainCancellation).ConfigureAwait(false);
            throw new IOException(BuildMessage(
                distroName,
                linuxAbsoluteFilePath,
                $"process execution failed: {ex.Message}"), ex);
        }

        return new BoundedProcessCapture(
            process.ExitCode,
            stdoutTask.Result,
            stderrTask.Result);
    }

    private static async Task<StreamCapture> DrainStreamAsync(
        Stream stream,
        int maximumRetainedByteCount,
        CancellationToken cancellationToken)
    {
        var retained = new MemoryStream(Math.Min(maximumRetainedByteCount, 8192));
        var buffer = new byte[8192];
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maximumRetainedByteCount - checked((int)retained.Length);
            var retainCount = Math.Min(Math.Max(remaining, 0), read);
            if (retainCount > 0)
            {
                retained.Write(buffer, 0, retainCount);
            }

            truncated |= retainCount != read;
        }

        return new StreamCapture(retained.ToArray(), truncated);
    }

    private static async Task TerminateAndObserveAsync(
        Process process,
        Task captureTask,
        CancellationTokenSource drainCancellation)
    {
        TryKill(process, entireProcessTree: true);
        await WaitForExitBoundedAsync(process).ConfigureAwait(false);
        if (IsProcessAlive(process))
        {
            TryKill(process, entireProcessTree: false);
            await WaitForExitBoundedAsync(process).ConfigureAwait(false);
        }

        drainCancellation.Cancel();
        try
        {
            await captureTask.WaitAsync(PipeDrainWait).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            ObserveLateFault(captureTask);
        }
    }

    private static async Task WaitForExitBoundedAsync(Process process)
    {
        if (!IsProcessAlive(process))
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(ProcessExitWait)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // The next fallback kill remains bounded and preserves the original failure.
        }
    }

    private static void TryKill(Process process, bool entireProcessTree)
    {
        try
        {
            if (IsProcessAlive(process))
            {
                process.Kill(entireProcessTree);
            }
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Cleanup is best effort; the caller's original failure remains primary.
        }
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

    private static bool IsProcessAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return false;
        }
    }

    private static void ValidateLocation(string distroName, string? linuxAbsoluteFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        if (linuxAbsoluteFilePath is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(linuxAbsoluteFilePath);
        if (linuxAbsoluteFilePath[0] != '/')
        {
            throw new ArgumentException(
                "The WSL file path must be an absolute Linux path.",
                nameof(linuxAbsoluteFilePath));
        }
    }

    private static string FormatStderr(StreamCapture capture)
    {
        var value = Encoding.UTF8.GetString(capture.Bytes).Trim();
        if (value.Length == 0)
        {
            value = "<empty>";
        }

        return capture.Truncated ? $"{value} [truncated]" : value;
    }

    private static string BuildMessage(
        string distroName,
        string? linuxAbsoluteFilePath,
        string failure) =>
        linuxAbsoluteFilePath is null
            ? $"WSL distro '{distroName}' did not become ready; {failure}."
            : $"Unable to read WSL text file; distro='{distroName}'; path='{linuxAbsoluteFilePath}'; {failure}.";

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private sealed record BoundedProcessCapture(
        int ExitCode,
        StreamCapture StandardOutput,
        StreamCapture StandardError);

    private sealed record StreamCapture(byte[] Bytes, bool Truncated)
    {
        internal static StreamCapture Empty { get; } = new([], false);
    }
}
