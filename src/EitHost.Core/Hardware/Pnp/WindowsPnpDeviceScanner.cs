using System.Management;
using System.Runtime.Versioning;

namespace EitHost.Core.Hardware.Pnp;

[SupportedOSPlatform("windows")]
public sealed class WindowsPnpDeviceScanner : IPnpDeviceScanner
{
    public Task<PnpDeviceSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows PnP scanning requires Windows.");
        }

        return Task.Run(() => Capture(cancellationToken), cancellationToken);
    }

    private static PnpDeviceSnapshot Capture(CancellationToken cancellationToken)
    {
        var candidates = new List<PnpDeviceCandidate>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name,PNPDeviceID,PNPClass,Service,Status,ConfigManagerErrorCode FROM Win32_PnPEntity");
        using var results = searcher.Get();

        foreach (ManagementObject device in results)
        {
            using (device)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var problemCode = ReadInt(device, "ConfigManagerErrorCode");

                var candidate = PnpDeviceCandidateFactory.TryCreate(
                    ReadString(device, "PNPDeviceID"),
                    ReadString(device, "Name"),
                    ReadString(device, "PNPClass"),
                    ReadString(device, "Service"),
                    ReadString(device, "Status"),
                    problemCode,
                    DescribeProblemCode(problemCode));

                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return new PnpDeviceSnapshot(DateTimeOffset.UtcNow, candidates);
    }

    private static string? ReadString(ManagementBaseObject device, string propertyName)
    {
        return device.Properties[propertyName]?.Value as string;
    }

    private static int? ReadInt(ManagementBaseObject device, string propertyName)
    {
        var value = device.Properties[propertyName]?.Value;
        return value is null ? null : Convert.ToInt32(value);
    }

    private static string? DescribeProblemCode(int? problemCode)
    {
        return problemCode switch
        {
            null or 0 => null,
            28 => "The drivers for this device are not installed.",
            _ => $"PnP problem code {problemCode.Value}."
        };
    }
}
