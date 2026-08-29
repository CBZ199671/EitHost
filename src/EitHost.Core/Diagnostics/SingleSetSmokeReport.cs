using EitHost.Core.Storage.Catalog;

namespace EitHost.Core.Diagnostics;

public sealed record SingleSetSmokeReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Ready,
    bool Passed,
    string Status,
    SingleSetSmokeHardwareSummary Hardware,
    SingleSetSmokePairing? Pairing,
    SingleSetSmokeDdsCommand? SetDacCommand,
    SingleSetSmokeDdsCommand? SetPgaCommand,
    SingleSetSmokeDdsCommand? StartExcitationCommand,
    SingleSetSmokeDdsCommand? StopExcitationCommand,
    SingleSetSmokeAcquisition? Acquisition,
    SingleSetSmokeArtifacts? Artifacts,
    IReadOnlyList<string> Warnings);

public sealed record SingleSetSmokeHardwareSummary(
    int PnpUsb2070Count,
    int PnpDdsSerialCount,
    int OsSerialPortCount,
    int Usb2070SdkDeviceCount,
    bool ReadyForSingleSetSmoke,
    IReadOnlyList<string> Blockers);

public sealed record SingleSetSmokePairing(
    string SetLabel,
    int Usb2070DeviceNumber,
    string Usb2070DeviceId,
    string Usb2070DisplayName,
    string Usb2070Vid,
    string Usb2070Pid,
    string Usb2070LocationPath,
    string DdsPortName,
    string DdsDeviceId,
    string DdsDisplayName,
    string DdsVid,
    string DdsPid,
    string DdsLocationPath);

public sealed record SingleSetSmokeDdsCommand(
    string Command,
    string PacketHex,
    DateTimeOffset SentAt);

public sealed record SingleSetSmokeAcquisition(
    int SampleRows,
    int ChannelCount,
    int RawValueCount,
    int SampleRateHz,
    string Range,
    int AdBit);

public sealed record SingleSetSmokeArtifacts(
    string RawHdf5Path,
    string DemodHdf5Path,
    string RawCsvPath,
    string CatalogPath,
    int DemodFrameCount,
    int DemodPeakCount,
    int CsvRowCount,
    int CsvColumnCount)
{
    public EitCatalogSummary? CatalogSummary { get; init; }
}
