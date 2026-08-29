namespace EitHost.Core.Diagnostics;

public sealed record HardwareSmokeMultiSetReadiness(
    int RequiredSetCount,
    bool ReadyForMultiSetSmoke,
    IReadOnlyList<string> Blockers)
{
    public static HardwareSmokeMultiSetReadiness Create(
        int requiredSetCount,
        int pnpUsb2070Count,
        int pnpDdsSerialCount,
        int osSerialPortCount,
        int usb2070SdkDeviceCount,
        IReadOnlyList<string> baseBlockers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredSetCount, 2);
        ArgumentNullException.ThrowIfNull(baseBlockers);

        var blockers = new List<string>(baseBlockers);
        AddCountBlocker(blockers, "PnP USB2070", pnpUsb2070Count, requiredSetCount);
        AddCountBlocker(blockers, "PnP DDS 串口", pnpDdsSerialCount, requiredSetCount);
        AddCountBlocker(blockers, "OS 串口", osSerialPortCount, requiredSetCount);
        AddCountBlocker(blockers, "USB2070 SDK 可打开设备", usb2070SdkDeviceCount, requiredSetCount);

        return new HardwareSmokeMultiSetReadiness(
            requiredSetCount,
            blockers.Count == 0,
            blockers.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void AddCountBlocker(
        ICollection<string> blockers,
        string name,
        int actual,
        int required)
    {
        if (actual < required)
        {
            blockers.Add($"{name} 数量不足：{actual}/{required}。");
        }
    }
}
