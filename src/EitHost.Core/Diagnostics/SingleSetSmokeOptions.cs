using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed record SingleSetSmokeOptions
{
    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        $"single-set-smoke-{DateTime.Now:yyyyMMdd-HHmmss}");

    public string SetLabel { get; init; } = "EIT-01";

    public int? Usb2070DeviceNumber { get; init; }

    public string? DdsPortName { get; init; }

    public int SampleRows { get; init; } = 1024;

    public int SampleRateHz { get; init; } = 200_000;

    public int ExcitationFrequencyHz { get; init; } = 10_000;

    public int DacChannel { get; init; } = 1;

    public double DacGain { get; init; } = 1.0;

    public int DacPhaseDegrees { get; init; }

    public byte PgaGain { get; init; } = 1;

    public Usb2070AdRange Range { get; init; } = Usb2070AdRange.Bipolar5V;

    public Usb2070TriggerMode TriggerMode { get; init; } = Usb2070TriggerMode.Continue;

    public Usb2070TriggerSource TriggerSource { get; init; } = Usb2070TriggerSource.Software;

    public int TriggerDelay { get; init; }

    public int TriggerLength { get; init; } = 1024;

    public int TriggerLevel { get; init; } = 2048;
}
