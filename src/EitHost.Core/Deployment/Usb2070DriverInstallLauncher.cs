using System.Diagnostics;

namespace EitHost.Core.Deployment;

public sealed class Usb2070DriverInstallLauncher
{
    public ProcessStartInfo CreateStartInfo(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var scriptPath = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "scripts",
            "start-usb2070-driver-install-admin.ps1"));
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("USB2070 driver admin launcher script was not found.", scriptPath);
        }

        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            Verb = "runas",
            UseShellExecute = true,
            WorkingDirectory = baseDirectory
        };
    }

    public void Launch(string baseDirectory)
    {
        using var process = Process.Start(CreateStartInfo(baseDirectory));
    }
}
