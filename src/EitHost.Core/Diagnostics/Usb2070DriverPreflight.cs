namespace EitHost.Core.Diagnostics;

public sealed record Usb2070DriverPreflight(
    bool IsAdministrator,
    string InfPath,
    bool InfExists,
    IReadOnlyList<string> DriverStoreMatches)
{
    public static Usb2070DriverPreflight Unknown { get; } = new(false, string.Empty, false, []);
}
