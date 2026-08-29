namespace EitHost.Core.Deployment;

public sealed class FirstRunCheckService
{
    public FirstRunCheckResult Check(string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var usb2070DllPath = Path.Combine(Path.GetFullPath(appBaseDirectory), "USB2070.dll");
        var issues = new List<string>();
        var usb2070DllPresent = File.Exists(usb2070DllPath);
        if (!usb2070DllPresent)
        {
            issues.Add("未找到 USB2070.dll，请确认采集卡 SDK DLL 已随程序一起发布。");
        }

        return new FirstRunCheckResult(usb2070DllPresent, usb2070DllPath, issues);
    }
}
