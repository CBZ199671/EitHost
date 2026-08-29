using EitHost.Core.Hardware.Dds;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Pairing;

namespace EitHost.App.ViewModels;

public sealed class PairingSummaryItem : ObservableObject
{
    private bool isExciting;
    private bool isAcquiring;
    private DdsScanStatus? scanStatus;

    public PairingSummaryItem(
        EitSetPairing pairing,
        DdsFirmwareCapabilities? firmwareCapabilities = null)
    {
        Pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        FirmwareCapabilities = firmwareCapabilities;
    }

    public EitSetPairing Pairing { get; }

    public DdsFirmwareCapabilities? FirmwareCapabilities { get; }

    public string Title => Pairing.Label;

    public string Usb2070Line => Pairing.Usb2070Candidate.DisplayName;

    public string Usb2070DeviceNumberLine => $"USB2070 设备号 #{Pairing.Usb2070DeviceNumber}";

    public string Usb2070IdentityLine => FormatIdentity("USB2070", Pairing.Usb2070Candidate);

    public string DdsLine => $"{Pairing.DdsSerialCandidate.PortName}  {Pairing.DdsSerialCandidate.DisplayName}";

    public string DdsIdentityLine => FormatIdentity("DDS", Pairing.DdsSerialCandidate);

    public string DdsFirmwareLine => FirmwareCapabilities is null
        ? "DDS FW：未验证"
        : $"DDS FW {FirmwareCapabilities.FirmwareVersion} · v{DdsProtocolConstants.ProtocolVersion} · 0x{FirmwareCapabilities.FeatureFlags:X4}";

    public DdsScanStatus? ScanStatus => scanStatus;

    // Live state, pushed by the view model on every excitation/acquisition
    // transition so the dashboard card lights up instead of showing static IDLE.
    public bool IsExciting
    {
        get => isExciting;
        set
        {
            if (SetProperty(ref isExciting, value))
            {
                OnPropertyChanged(nameof(DdsStateText));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public bool IsAcquiring
    {
        get => isAcquiring;
        set
        {
            if (SetProperty(ref isAcquiring, value))
            {
                OnPropertyChanged(nameof(AcquireStateText));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    // A bound set is online by definition; the indicator only flips to the
    // "active" tone once excitation or acquisition is actually running.
    public bool IsActive => IsExciting || IsAcquiring;

    public string DdsStateText => scanStatus?.State switch
    {
        DdsScanState.Running => $"扫描 {scanStatus.CompletedCycles}/{scanStatus.TargetCycles}",
        DdsScanState.Completed => $"完成 {scanStatus.CompletedCycles}/{scanStatus.TargetCycles}",
        _ => IsExciting ? "激励中" : "就绪"
    };

    public string DdsScanProgressText => scanStatus?.State switch
    {
        DdsScanState.Running =>
            $"有限扫描：{scanStatus.CompletedCycles}/{scanStatus.TargetCycles} 圈 · 当前 CH{scanStatus.CurrentStep + 1}/16",
        DdsScanState.Completed =>
            $"有限扫描完成：{scanStatus.CompletedCycles}/{scanStatus.TargetCycles} 圈 · 末通道 CH{scanStatus.CurrentStep + 1}",
        _ => string.Empty
    };

    public string AcquireStateText => IsAcquiring ? "采集中" : "就绪";

    public void UpdateScanStatus(DdsScanStatus? status)
    {
        if (SetProperty(ref scanStatus, status, nameof(ScanStatus)))
        {
            OnPropertyChanged(nameof(DdsStateText));
            OnPropertyChanged(nameof(DdsScanProgressText));
        }
    }

    private static string FormatIdentity(string prefix, PnpDeviceCandidate candidate)
    {
        return $"{prefix} PnP: {candidate.Vid}/{candidate.Pid} | {candidate.LocationPath}";
    }
}
