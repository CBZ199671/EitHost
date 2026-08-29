using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;

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
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil",
                ArgumentList = { "/enum-drivers" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            if (!process.HasExited || process.ExitCode != 0)
            {
                return string.IsNullOrWhiteSpace(error) ? [] : [$"pnputil: {error.Trim()}"];
            }

            return output
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

    private sealed record Usb2070InfCandidate(string Path, int SdkVersionRank, int WindowsRank, int CopyRank);
}
