using EitHost.Core.Domain;
using EitHost.Core.Hardware.Dds;

namespace EitHost.Core.Diagnostics;

public interface ISingleSetSmokeHardware
{
    Task<HardwareSmokeReport> CaptureHardwareReportAsync(CancellationToken cancellationToken = default);

    Task<SingleSetDdsStartupResult> SendDdsStartupSequenceAsync(
        string portName,
        DdsDacSettings dacSettings,
        byte pgaGain,
        DdsExcitationSettings excitationSettings,
        CancellationToken cancellationToken = default);

    Task<DdsCommandResult> SendDdsSetDacAsync(
        string portName,
        DdsDacSettings settings,
        CancellationToken cancellationToken = default);

    Task<DdsCommandResult> SendDdsSetPgaAsync(
        string portName,
        byte gain,
        CancellationToken cancellationToken = default);

    Task<DdsCommandResult> SendDdsStartExcitationAsync(
        string portName,
        DdsExcitationSettings settings,
        CancellationToken cancellationToken = default);

    Task<DdsCommandResult> SendDdsStopExcitationAsync(string portName, CancellationToken cancellationToken = default);

    SingleSetAdCapture CaptureAdBlock(
        Usb2070Device device,
        SingleSetSmokeOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record SingleSetDdsStartupResult(
    DdsCommandResult SetDac,
    DdsCommandResult SetPga,
    DdsCommandResult StartExcitation);
