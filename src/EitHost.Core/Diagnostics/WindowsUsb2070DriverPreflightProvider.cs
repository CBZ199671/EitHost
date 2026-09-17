using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.Core.Diagnostics;

[SupportedOSPlatform("windows")]
public static class WindowsUsb2070DriverPreflightProvider
{
    public static Usb2070DriverPreflight Capture(string? repoRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(repoRoot) ? FindRepoRoot() : repoRoot;
        var infPath = FindBestUsb2070InfPath(root);

        return new Usb2070DriverPreflight(
            IsAdministrator(),
            infPath,
            File.Exists(infPath),
            EnumerateDriverStoreMatches());
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string FindBestUsb2070InfPath(string repoRoot)
    {
        return EnumerateUsb2070InfCandidates(repoRoot)
            .OrderBy(candidate => candidate.SdkVersionRank)
            .ThenBy(candidate => candidate.WindowsRank)
            .ThenBy(candidate => candidate.CopyRank)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault()
            ?? Path.GetFullPath(Path.Combine(
                repoRoot,
                "..",
                "USB2070 SDK光盘23.1",
                "USB2070 SDK光盘23.1",
                "Driver x64 WIN10",
                "USB2070.inf"));
    }

    private static IEnumerable<Usb2070InfCandidate> EnumerateUsb2070InfCandidates(string repoRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExistingRoot(roots, repoRoot);
        var parent = Directory.GetParent(Path.GetFullPath(repoRoot));
        if (parent is not null)
        {
            AddExistingRoot(roots, parent.FullName);
        }

        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "USB2070.inf", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var fullPath = Path.GetFullPath(file);
                if (!fullPath.Contains("Driver x64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new Usb2070InfCandidate(
                    fullPath,
                    CalculateSdkVersionRank(fullPath),
                    CalculateWindowsRank(fullPath),
                    fullPath.Contains(" - ", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            }
        }
    }

    private static void AddExistingRoot(HashSet<string> roots, string root)
    {
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            roots.Add(Path.GetFullPath(root));
        }
    }

    private static int CalculateSdkVersionRank(string path)
    {
        var match = Regex.Match(path, @"USB2070 SDK[^\\]*(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        return match.Success
            ? -((int.Parse(match.Groups[1].Value) * 100) + int.Parse(match.Groups[2].Value))
            : 9999;
    }

    private static int CalculateWindowsRank(string path)
    {
        if (path.Contains("WIN10", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (path.Contains("WIN7-10", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (path.Contains("WIN7-8", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            new DirectoryInfo(Directory.GetCurrentDirectory()),
            new DirectoryInfo(AppContext.BaseDirectory)
        };

        foreach (var candidate in candidates)
        {
            for (var directory = candidate; directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "EitHost.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static IReadOnlyList<string> EnumerateDriverStoreMatches()
    {
        try
        {
            var result = RunProcessAsync(new ProcessStartInfo
            {
                FileName = "pnputil",
                ArgumentList = { "/enum-drivers" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            if (result.TimedOut || result.ExitCode != 0)
            {
                return [];
            }

            return result.StandardOutput
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line =>
                    line.Contains("USB2070", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("FCCTEC", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("FCUSB2Card", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("VID_1088", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal static async Task<ProcessCaptureResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException("Both stdout and stderr must be redirected.", nameof(startInfo));
        }

        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        using var process = new Process { StartInfo = startInfo };
        if (!Hdf5ChildProcess.Start(process))
        {
            throw new InvalidOperationException($"Unable to start process '{startInfo.FileName}'.");
        }

        var processId = process.Id;
        using var drainCancellation = new CancellationTokenSource();
        Task<string> standardOutputTask = Task.FromResult(string.Empty);
        Task<string> standardErrorTask = Task.FromResult(string.Empty);
        Task exitTask = Task.CompletedTask;
        Task captureTask = Task.CompletedTask;
        var captureInitialized = false;
        var timedOut = false;
        var terminated = true;
        try
        {
            standardOutputTask = process.StandardOutput.ReadToEndAsync(drainCancellation.Token);
            standardErrorTask = process.StandardError.ReadToEndAsync(drainCancellation.Token);
            exitTask = process.WaitForExitAsync(CancellationToken.None);
            captureTask = Task.WhenAll(exitTask, standardOutputTask, standardErrorTask);
            captureInitialized = true;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await captureTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await captureTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            timedOut = true;
            terminated = await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            await FinishPipeDrainsAsync(
                captureInitialized
                    ? captureTask
                    : Task.WhenAll(exitTask, standardOutputTask, standardErrorTask),
                drainCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            await FinishPipeDrainsAsync(
                captureInitialized
                    ? captureTask
                    : Task.WhenAll(exitTask, standardOutputTask, standardErrorTask),
                drainCancellation).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            await TerminateProcessTreeAsync(process).ConfigureAwait(false);
            await FinishPipeDrainsAsync(
                captureInitialized
                    ? captureTask
                    : Task.WhenAll(exitTask, standardOutputTask, standardErrorTask),
                drainCancellation).ConfigureAwait(false);
            throw;
        }

        return new ProcessCaptureResult(
            processId,
            timedOut,
            terminated,
            TryGetExitCode(process),
            GetCompletedOutput(standardOutputTask),
            GetCompletedOutput(standardErrorTask));
    }

    private static async Task<bool> TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (IsProcessAlive(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            try
            {
                if (IsProcessAlive(process))
                {
                    process.Kill();
                }
            }
            catch (Exception fallbackException) when (!IsFatal(fallbackException))
            {
                // The result reports whether termination ultimately succeeded.
            }
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            try
            {
                if (IsProcessAlive(process))
                {
                    process.Kill();
                }
            }
            catch (Exception fallbackException) when (!IsFatal(fallbackException))
            {
                // The result reports whether termination ultimately succeeded.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (Exception finalException) when (!IsFatal(finalException))
            {
                // Do not replace the timeout/cancellation outcome with cleanup failure.
            }
        }

        return !IsProcessAlive(process);
    }

    private static async Task FinishPipeDrainsAsync(
        Task captureTask,
        CancellationTokenSource drainCancellation)
    {
        try
        {
            await captureTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // A descendant may still own a pipe. Cancel both drains and observe them below.
        }

        drainCancellation.Cancel();
        try
        {
            await captureTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Bounded best effort: all started tasks have been awaited/observed here.
            ObserveLateFault(captureTask);
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

    private static string GetCompletedOutput(Task<string> task) =>
        task.IsCompletedSuccessfully ? task.Result : string.Empty;

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private sealed record Usb2070InfCandidate(string Path, int SdkVersionRank, int WindowsRank, int CopyRank);

    internal sealed record ProcessCaptureResult(
        int ProcessId,
        bool TimedOut,
        bool Terminated,
        int? ExitCode,
        string StandardOutput,
        string StandardError);
}
