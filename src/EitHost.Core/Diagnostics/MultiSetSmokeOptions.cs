using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Usb2070;

namespace EitHost.Core.Diagnostics;

public sealed class MultiSetSmokeOptions
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Path.GetFullPath("artifacts"),
        $"multi-set-smoke-{DateTime.Now:yyyyMMdd-HHmmss}");

    public int RequiredSetCount { get; set; } = 2;

    public IReadOnlyList<MultiSetSmokeRequestedPair> RequestedPairs { get; set; } = [];

    public bool Execute { get; set; }

    public string LabelPrefix { get; set; } = "EIT";

    public int LabelStartIndex { get; set; } = 1;

    public int SampleRows { get; set; } = 1024;

    public int SampleRateHz { get; set; } = 200_000;

    public Usb2070AdRange Range { get; set; } = Usb2070AdRange.Bipolar5V;

    public Usb2070TriggerMode TriggerMode { get; set; } = Usb2070TriggerMode.Continue;

    public Usb2070TriggerSource TriggerSource { get; set; } = Usb2070TriggerSource.Software;

    public int ExcitationFrequencyHz { get; set; } = 10_000;

    public int DacChannel { get; set; } = 1;

    public double DacGain { get; set; } = 1.0;

    public int DacPhaseDegrees { get; set; }

    public byte PgaGain { get; set; } = 1;

    public string CreateSetLabel(int zeroBasedIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedIndex);
        return $"{LabelPrefix}-{LabelStartIndex + zeroBasedIndex:00}";
    }

    public SingleSetSmokeOptions ToSingleSetOptions()
    {
        return new SingleSetSmokeOptions
        {
            OutputDirectory = OutputDirectory,
            SampleRows = SampleRows,
            SampleRateHz = SampleRateHz,
            Range = Range,
            TriggerMode = TriggerMode,
            TriggerSource = TriggerSource,
            ExcitationFrequencyHz = ExcitationFrequencyHz,
            DacChannel = DacChannel,
            DacGain = DacGain,
            DacPhaseDegrees = DacPhaseDegrees,
            PgaGain = PgaGain
        };
    }

    public DdsExcitationSettings CreateExcitationSettings()
    {
        return new DdsExcitationSettings(DdsExcitationMode.Adjacent, ExcitationFrequencyHz);
    }

    public int EffectiveSetCount => RequestedPairs.Count > 0 ? RequestedPairs.Count : RequiredSetCount;
}

public sealed record MultiSetSmokeRequestedPair
{
    public MultiSetSmokeRequestedPair(
        string label,
        int usb2070DeviceNumber,
        string ddsPortName,
        string? usb2070PnpIdentityFragment = null)
    {
        Label = RequireText(label, nameof(label));
        ArgumentOutOfRangeException.ThrowIfNegative(usb2070DeviceNumber);
        Usb2070DeviceNumber = usb2070DeviceNumber;
        DdsPortName = RequireText(ddsPortName, nameof(ddsPortName)).ToUpperInvariant();
        Usb2070PnpIdentityFragment = string.IsNullOrWhiteSpace(usb2070PnpIdentityFragment)
            ? null
            : usb2070PnpIdentityFragment.Trim();
    }

    public string Label { get; }

    public int Usb2070DeviceNumber { get; }

    public string DdsPortName { get; }

    public string? Usb2070PnpIdentityFragment { get; }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
