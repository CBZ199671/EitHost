using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed class HardwareSmokeReporter
{
    private readonly IPnpDeviceScanner pnpScanner;
    private readonly IUsb2070NativeApi usb2070NativeApi;
    private readonly Func<IReadOnlyList<string>> serialPortProvider;
    private readonly Func<Usb2070DriverPreflight> driverPreflightProvider;
    private readonly bool hasDriverPreflightProvider;

    public HardwareSmokeReporter(
        IPnpDeviceScanner pnpScanner,
        IUsb2070NativeApi usb2070NativeApi,
        Func<IReadOnlyList<string>> serialPortProvider,
        Func<Usb2070DriverPreflight>? driverPreflightProvider = null)
    {
        this.pnpScanner = pnpScanner ?? throw new ArgumentNullException(nameof(pnpScanner));
        this.usb2070NativeApi = usb2070NativeApi ?? throw new ArgumentNullException(nameof(usb2070NativeApi));
        this.serialPortProvider = serialPortProvider ?? throw new ArgumentNullException(nameof(serialPortProvider));
        this.driverPreflightProvider = driverPreflightProvider ?? (() => Usb2070DriverPreflight.Unknown);
        hasDriverPreflightProvider = driverPreflightProvider is not null;
    }

    public async Task<HardwareSmokeReport> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var snapshot = await pnpScanner.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var osSerialPorts = CaptureSerialPorts(warnings);
        var sdkDevices = CaptureUsb2070SdkDevices(warnings);
        var driverPreflight = CaptureDriverPreflight(warnings);

        if (snapshot.Usb2070Devices.Count == 0)
        {
            warnings.Add("PnP 未发现 USB2070。");
        }
        else
        {
            foreach (var device in snapshot.Usb2070Devices.Where(device =>
                         !string.IsNullOrWhiteSpace(device.Status)
                         && !string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase)
                         || device.ProblemCode is > 0))
            {
                warnings.Add($"USB2070 PnP 状态异常：{device.DisplayName} status={device.Status ?? "unknown"} problem={device.ProblemCode?.ToString() ?? "unknown"} {device.ProblemDescription}");
            }
        }

        if (snapshot.SerialDevices.Count == 0)
        {
            warnings.Add("PnP 未发现 DDS 串口候选。");
        }

        if (sdkDevices.Count == 0)
        {
            warnings.Add("USB2070 SDK 未发现可打开设备；检查驱动、USB2070.dll 架构和设备状态。");
        }

        var readiness = CreateReadiness(snapshot, osSerialPorts, sdkDevices);

        return new HardwareSmokeReport(
            DateTimeOffset.Now,
            snapshot.Usb2070Devices.Select(HardwareSmokeDeviceCandidate.FromPnp).ToArray(),
            snapshot.SerialDevices.Select(HardwareSmokeDeviceCandidate.FromPnp).ToArray(),
            osSerialPorts,
            sdkDevices,
            driverPreflight,
            readiness,
            warnings);
    }

    private static HardwareSmokeReadiness CreateReadiness(
        PnpDeviceSnapshot snapshot,
        IReadOnlyList<string> osSerialPorts,
        IReadOnlyList<HardwareSmokeUsb2070Device> sdkDevices)
    {
        var blockers = new List<string>();
        if (snapshot.Usb2070Devices.Count == 0)
        {
            blockers.Add("PnP 未发现 USB2070。");
        }
        else if (snapshot.Usb2070Devices.Any(device =>
                     !string.IsNullOrWhiteSpace(device.Status)
                     && !string.Equals(device.Status, "OK", StringComparison.OrdinalIgnoreCase)
                     || device.ProblemCode is > 0))
        {
            blockers.Add("USB2070 PnP 状态异常。");
        }

        if (snapshot.SerialDevices.Count == 0)
        {
            blockers.Add("PnP 未发现 DDS 串口候选。");
        }

        if (osSerialPorts.Count == 0)
        {
            blockers.Add("OS 未发现串口。");
        }

        if (sdkDevices.Count == 0)
        {
            blockers.Add("USB2070 SDK 未发现可打开设备。");
        }

        return new HardwareSmokeReadiness(blockers.Count == 0, blockers);
    }

    private IReadOnlyList<string> CaptureSerialPorts(List<string> warnings)
    {
        try
        {
            return serialPortProvider()
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .Select(port => port.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"读取 OS 串口失败：{ex.Message}");
            return [];
        }
    }

    private IReadOnlyList<HardwareSmokeUsb2070Device> CaptureUsb2070SdkDevices(List<string> warnings)
    {
        try
        {
            return new Usb2070Service(usb2070NativeApi)
                .Scan()
                .Select(HardwareSmokeUsb2070Device.FromDevice)
                .ToArray();
        }
        catch (Exception ex)
        {
            warnings.Add($"USB2070 SDK 扫描失败：{ex.Message}");
            return [];
        }
    }

    private Usb2070DriverPreflight CaptureDriverPreflight(List<string> warnings)
    {
        if (!hasDriverPreflightProvider)
        {
            return Usb2070DriverPreflight.Unknown;
        }

        try
        {
            var preflight = driverPreflightProvider();
            if (!preflight.IsAdministrator)
            {
                warnings.Add("当前进程不是管理员；USB2070 驱动安装需要 UAC/管理员权限。");
            }

            if (!string.IsNullOrWhiteSpace(preflight.InfPath) && !preflight.InfExists)
            {
                warnings.Add($"USB2070 INF 不存在：{preflight.InfPath}");
            }

            if (preflight.DriverStoreMatches.Count == 0)
            {
                warnings.Add("Driver Store 未发现 USB2070/FCCTEC 相关驱动包。");
            }

            return preflight;
        }
        catch (Exception ex)
        {
            warnings.Add($"USB2070 驱动预检失败：{ex.Message}");
            return Usb2070DriverPreflight.Unknown;
        }
    }
}
