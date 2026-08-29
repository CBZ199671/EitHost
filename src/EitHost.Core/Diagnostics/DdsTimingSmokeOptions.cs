namespace EitHost.Core.Diagnostics;

public sealed record DdsTimingSmokeOptions
{
    public bool Execute { get; init; }

    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        $"dds-timing-smoke-{DateTime.Now:yyyyMMdd-HHmmss}");

    public string SetLabel { get; init; } = "EIT-01";

    public int Usb2070DeviceNumber { get; init; }

    public string DdsPortName { get; init; } = string.Empty;

    public int FrequencyHz { get; init; } = 3125;

    public IReadOnlyList<double> ChannelCycles { get; init; } = [10.0, 20.0, 40.0];

    public int SampleRateHz { get; init; } = 200_000;

    public double CurrentUa { get; init; } = 100.0;

    public byte PgaGain { get; init; } = 1;

    public double DiscardLeadingCycles { get; init; } = 3.0;

    public double DiscardTrailingCycles { get; init; } = 2.0;

    public int FramesPerBlock { get; init; } = 3;

    public int TargetBlocks { get; init; } = 100;
}
