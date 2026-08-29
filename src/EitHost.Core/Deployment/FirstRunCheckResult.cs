namespace EitHost.Core.Deployment;

public sealed record FirstRunCheckResult(
    bool Usb2070DllPresent,
    string Usb2070DllPath,
    IReadOnlyList<string> Issues)
{
    public bool IsReady => Issues.Count == 0;
}
