using System.Text.RegularExpressions;

namespace EitHost.Core.Reconstruction;

public static partial class WslPathMapper
{
    public static string ToWslPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(NormalizeExtendedWindowsPath(path));
        var wslMatch = WslUncRegex().Match(fullPath);
        if (wslMatch.Success)
        {
            var linuxPath = wslMatch.Groups["path"].Value.Replace('\\', '/');
            return "/" + linuxPath.TrimStart('/');
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            throw new ArgumentException($"Path is not a Windows drive or WSL UNC path: {path}", nameof(path));
        }

        var drive = char.ToLowerInvariant(root[0]);
        var relative = fullPath[root.Length..].Replace('\\', '/');
        return $"/mnt/{drive}/{relative}";
    }

    public static bool TryParseWslUncPath(string path, out string distroName, out string linuxPath)
    {
        distroName = string.Empty;
        linuxPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var wslMatch = WslUncRegex().Match(Path.GetFullPath(NormalizeExtendedWindowsPath(path)));
        if (!wslMatch.Success)
        {
            return false;
        }

        distroName = wslMatch.Groups["distro"].Value;
        linuxPath = "/" + wslMatch.Groups["path"].Value.Replace('\\', '/').TrimStart('/');
        return true;
    }

    public static string ToWslUncPath(string distroName, string linuxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        ArgumentException.ThrowIfNullOrWhiteSpace(linuxPath);
        var normalized = linuxPath.Trim().Replace('\\', '/').TrimStart('/');
        return $@"\\wsl.localhost\{distroName.Trim()}\{normalized.Replace('/', '\\')}";
    }

    private static string NormalizeExtendedWindowsPath(string path)
    {
        const string extendedUncPrefix = @"\\?\UNC\";
        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        const string extendedDrivePrefix = @"\\?\";
        return path.StartsWith(extendedDrivePrefix, StringComparison.OrdinalIgnoreCase)
            && path.Length >= extendedDrivePrefix.Length + 2
            && char.IsAsciiLetter(path[extendedDrivePrefix.Length])
            && path[extendedDrivePrefix.Length + 1] == ':'
                ? path[extendedDrivePrefix.Length..]
                : path;
    }

    [GeneratedRegex(@"^\\\\wsl(?:\.localhost|\$)?\\(?<distro>[^\\]+)(?:\\(?<path>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex WslUncRegex();
}
