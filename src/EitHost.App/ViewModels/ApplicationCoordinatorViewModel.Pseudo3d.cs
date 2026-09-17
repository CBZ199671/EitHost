using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using EitHost.App.ViewModels.Workspaces;
using EitHost.Core.Acquisition;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    private readonly Pseudo3dVisualizationController pseudo3dVisualization;
    private PairingSummaryItem? selectedPseudo3dLowerPairing;
    private PairingSummaryItem? selectedPseudo3dUpperPairing;
    private bool pseudo3dEnabled;
    private int pseudo3dDisplayLayers = 5;
    private double pseudo3dNormalizedHeight = 2.0;
    private double pseudo3dAxialRangeFactor = 1.0;
    private int pseudo3dMaximumPairSkewMilliseconds = (int)Pseudo3dAcquisitionProfile.MaximumPairSkew.TotalMilliseconds;
    private bool pseudo3dGroupWasRunning;
    private ImageSource? realtimePseudo3dImageSource;
    private string realtimePseudo3dStatus = "伪三维：未启用。";
    private string realtimePseudo3dProvenance = "默认由两个独立二维逆问题的成像结果进行质量感知 Kriging 伪三维拟合；不填造跨层观测，非真实 3D CEM 反演。";

    private string realtimePseudo3dWarning = "";
    private string realtimePseudo3dColorScale = "";
    public string RealtimePseudo3dWarning => realtimePseudo3dWarning;
    public string RealtimePseudo3dColorScale => realtimePseudo3dColorScale;
    private ImageSource? pseudo3dLowerImage;
    private ImageSource? pseudo3dUpperImage;
    private string pseudo3dLowerLabel = "";
    private string pseudo3dUpperLabel = "";
    public ImageSource? Pseudo3dLowerImage => pseudo3dLowerImage;
    public ImageSource? Pseudo3dUpperImage => pseudo3dUpperImage;
    public string Pseudo3dLowerLabel => pseudo3dLowerLabel;
    public string Pseudo3dUpperLabel => pseudo3dUpperLabel;
    public Visibility RealtimePseudo3dWarningVisibility =>
        string.IsNullOrWhiteSpace(realtimePseudo3dWarning) ? Visibility.Collapsed : Visibility.Visible;

    public PairingSummaryItem? SelectedPseudo3dLowerPairing
    {
        get => selectedPseudo3dLowerPairing;
        set
        {
            if (Pseudo3dEnabled && realtimeSessions.IsAnyActive) return;
            if (SetProperty(ref selectedPseudo3dLowerPairing, value))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public PairingSummaryItem? SelectedPseudo3dUpperPairing
    {
        get => selectedPseudo3dUpperPairing;
        set
        {
            if (Pseudo3dEnabled && realtimeSessions.IsAnyActive) return;
            if (SetProperty(ref selectedPseudo3dUpperPairing, value))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public bool Pseudo3dEnabled
    {
        get => pseudo3dEnabled;
        set
        {
            if (value && !pseudo3dEnabled && !CanEnablePseudo3d)
            {
                StatusMessage = "伪三维需要至少两套已绑定设备，并在采集停止后启用。";
                OnPropertyChanged(nameof(Pseudo3dEnabled));
                return;
            }
            if (!value && pseudo3dEnabled)
                realtimeRunCommands.RequestStop(false, SelectedPseudo3dLowerPairing?.Title);
            if (!SetProperty(ref pseudo3dEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Realtime2dImageVisibility));
            OnPropertyChanged(nameof(RealtimePseudo3dVisibility));
            UpdatePseudo3dConfiguration();
            RaiseRealtimeCanExecuteChanged();
        }
    }

    public bool CanEnablePseudo3d => pseudo3dEnabled || (BoundPairings.Count >= 2 && !realtimeSessions.IsAnyActive &&
        selectedPseudo3dLowerPairing is { } lower && selectedPseudo3dUpperPairing is { } upper &&
        lower.Pairing.Usb2070DeviceNumber != upper.Pairing.Usb2070DeviceNumber &&
        !string.IsNullOrWhiteSpace(lower.Pairing.DdsSerialCandidate.PortName) &&
        !string.IsNullOrWhiteSpace(upper.Pairing.DdsSerialCandidate.PortName) &&
        !string.Equals(lower.Pairing.DdsSerialCandidate.PortName, upper.Pairing.DdsSerialCandidate.PortName, StringComparison.OrdinalIgnoreCase));
    public bool CanEditPseudo3dDevices => Pseudo3dEnabled && !realtimeSessions.IsAnyActive;
    public string Pseudo3dAcquisitionPolicy => "双设备固定方案：10 kHz · 10 µA · 20 周期 · 前 8 后 4 · 每轮每层仅扫描 3 帧；目标每秒 3 幅，实际速度取决于采集与重构。两层网格和算法采用下层设置。启动或停止任一套均作用于整组。";

    public int Pseudo3dDisplayLayers
    {
        get => pseudo3dDisplayLayers;
        set
        {
            if (SetProperty(ref pseudo3dDisplayLayers, Math.Clamp(value, 2, 9)))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public double Pseudo3dNormalizedHeight
    {
        get => pseudo3dNormalizedHeight;
        set
        {
            var normalized = double.IsFinite(value) ? Math.Clamp(value, 0.1, 10.0) : 2.0;
            if (SetProperty(ref pseudo3dNormalizedHeight, normalized))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public double Pseudo3dAxialRangeFactor
    {
        get => pseudo3dAxialRangeFactor;
        set
        {
            var normalized = double.IsFinite(value) ? Math.Clamp(value, 0.1, 10.0) : 1.0;
            if (SetProperty(ref pseudo3dAxialRangeFactor, normalized))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public int Pseudo3dMaximumPairSkewMilliseconds
    {
        get => pseudo3dMaximumPairSkewMilliseconds;
        set
        {
            if (SetProperty(ref pseudo3dMaximumPairSkewMilliseconds, (int)Pseudo3dAcquisitionProfile.MaximumPairSkew.TotalMilliseconds))
            {
                UpdatePseudo3dConfiguration();
            }
        }
    }

    public ImageSource? RealtimePseudo3dImageSource
    {
        get => realtimePseudo3dImageSource;
        private set => SetProperty(ref realtimePseudo3dImageSource, value);
    }

    public string RealtimePseudo3dStatus
    {
        get => realtimePseudo3dStatus;
        private set => SetProperty(ref realtimePseudo3dStatus, value);
    }

    public string RealtimePseudo3dProvenance
    {
        get => realtimePseudo3dProvenance;
        private set => SetProperty(ref realtimePseudo3dProvenance, value);
    }

    public Visibility Realtime2dImageVisibility =>
        Pseudo3dEnabled ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RealtimePseudo3dVisibility =>
        Pseudo3dEnabled ? Visibility.Visible : Visibility.Collapsed;

    private void OnPairingInputChanged()
    {
        BindSelectedDevicesCommand?.RaiseCanExecuteChanged();
        SynchronizePseudo3dSelections();
    }

    private void OnPseudo3dBoundPairingsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (Pseudo3dEnabled && (BoundPairings.Count < 2 ||
            !BoundPairings.Contains(selectedPseudo3dLowerPairing!) || !BoundPairings.Contains(selectedPseudo3dUpperPairing!)))
            Pseudo3dEnabled = false;
        SynchronizePseudo3dSelections();
        RefreshPseudo3dAvailability();
    }

    private void SynchronizePseudo3dSelections()
    {
        var pairings = BoundPairings.ToArray();
        if (selectedPseudo3dLowerPairing is null || !pairings.Contains(selectedPseudo3dLowerPairing))
        {
            SelectedPseudo3dLowerPairing = pairings.FirstOrDefault();
        }

        if (selectedPseudo3dUpperPairing is null ||
            !pairings.Contains(selectedPseudo3dUpperPairing) ||
            ReferenceEquals(selectedPseudo3dLowerPairing, selectedPseudo3dUpperPairing))
        {
            SelectedPseudo3dUpperPairing = pairings.FirstOrDefault(pairing =>
                !ReferenceEquals(pairing, selectedPseudo3dLowerPairing));
        }
    }

    private void UpdatePseudo3dConfiguration()
    {
        pseudo3dVisualization.UpdateOptions(new Pseudo3dVisualizationOptions(
            Pseudo3dEnabled,
            SelectedPseudo3dLowerPairing?.Title,
            SelectedPseudo3dUpperPairing?.Title,
            Pseudo3dDisplayLayers,
            Pseudo3dNormalizedHeight,
            TimeSpan.FromMilliseconds(Pseudo3dMaximumPairSkewMilliseconds),
            Pseudo3dAxialRangeFactor,
            RequireTimeDivision: true));
        OnPropertyChanged(nameof(CanEditPseudo3dDevices));
        OnPropertyChanged(nameof(CanEnablePseudo3d));
    }

    private void RefreshPseudo3dAvailability()
    {
        OnPropertyChanged(nameof(CanEnablePseudo3d));
        OnPropertyChanged(nameof(CanEditPseudo3dDevices));
        var running = realtimeSessions.GetStatesToStop(null).Any(state => state.Config?.TimeDivisionGroup is not null && !state.IsStopRequested);
        if (running == pseudo3dGroupWasRunning) return;
        pseudo3dGroupWasRunning = running;
        pseudo3dVisualization.ClearSources();
    }

    private void ApplyPseudo3dPresentation(Pseudo3dVisualizationPresentation presentation)
    {
        _ = presentation.TryCommitIfCurrent(() =>
        {
            RealtimePseudo3dImageSource = presentation.Image;
            RealtimePseudo3dStatus = presentation.Status;
            RealtimePseudo3dProvenance = presentation.Provenance;
            SetProperty(ref realtimePseudo3dWarning, presentation.Warning, nameof(RealtimePseudo3dWarning));
            SetProperty(ref realtimePseudo3dColorScale, presentation.ColorScale, nameof(RealtimePseudo3dColorScale));
            OnPropertyChanged(nameof(RealtimePseudo3dWarningVisibility));
            SetProperty(ref pseudo3dLowerImage, presentation.LowerImage, nameof(Pseudo3dLowerImage));
            SetProperty(ref pseudo3dUpperImage, presentation.UpperImage, nameof(Pseudo3dUpperImage));
            SetProperty(ref pseudo3dLowerLabel, presentation.LowerSource is { } lower ? $"{lower.SetLabel} · #{lower.Result.BlockNumber}" : "", nameof(Pseudo3dLowerLabel));
            SetProperty(ref pseudo3dUpperLabel, presentation.UpperSource is { } upper ? $"{upper.SetLabel} · #{upper.Result.BlockNumber}" : "", nameof(Pseudo3dUpperLabel));
        });
    }
}
