using System.Text.RegularExpressions;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Hardware.Pnp;

public static partial class PnpDeviceCandidateFactory
{
    private const string UnknownVid = "VID_UNKNOWN";
    private const string UnknownPid = "PID_UNKNOWN";

    public static PnpDeviceCandidate? TryCreate(
        string? pnpDeviceId,
        string? displayName,
        string? pnpClass = null,
        string? service = null,
        string? status = null,
        int? problemCode = null,
        string? problemDescription = null)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var deviceId = pnpDeviceId.Trim();
        var name = displayName.Trim();
        var vid = ExtractHardwareId(deviceId, VidRegex(), UnknownVid);
        var pid = ExtractHardwareId(deviceId, PidRegex(), UnknownPid);
        var portName = ExtractPortName(name);

        if (IsUsb2070(deviceId, name))
        {
            return new PnpDeviceCandidate(
                PnpDeviceKind.Usb2070,
                deviceId,
                name,
                vid,
                pid,
                deviceId,
                status: status,
                problemCode: problemCode,
                problemDescription: problemDescription);
        }

        if (IsSerialCandidate(portName, pnpClass, service))
        {
            return new PnpDeviceCandidate(
                PnpDeviceKind.SerialPort,
                deviceId,
                name,
                vid,
                pid,
                deviceId,
                portName,
                status,
                problemCode,
                problemDescription);
        }

        return null;
    }

    private static bool IsUsb2070(string deviceId, string displayName)
    {
        return Contains(deviceId, Usb2070Constants.VendorId)
            && Contains(deviceId, Usb2070Constants.ProductId)
            || Contains(displayName, "USB2070")
            || Contains(displayName, "FCFR-USB2070");
    }

    private static bool IsSerialCandidate(string? portName, string? pnpClass, string? service)
    {
        return portName is not null
            || Contains(pnpClass, "Ports")
            || Contains(service, "Serial");
    }

    private static string ExtractHardwareId(string deviceId, Regex regex, string fallback)
    {
        var match = regex.Match(deviceId);
        return match.Success ? match.Value.ToUpperInvariant() : fallback;
    }

    private static string? ExtractPortName(string displayName)
    {
        var match = ComPortRegex().Match(displayName);
        return match.Success ? match.Groups["port"].Value.ToUpperInvariant() : null;
    }

    private static bool Contains(string? value, string expected)
    {
        return value?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
    }

    [GeneratedRegex(@"VID_[0-9A-Fa-f]{4}")]
    private static partial Regex VidRegex();

    [GeneratedRegex(@"PID_[0-9A-Fa-f]{4}")]
    private static partial Regex PidRegex();

    [GeneratedRegex(@"\((?<port>COM\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortRegex();
}
