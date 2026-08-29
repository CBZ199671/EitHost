using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using EitHost.App.ViewModels.Workspaces;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    private readonly Pseudo3dVisualizationController pseudo3dVisualization;
    private PairingSummaryItem? selectedPseudo3dLowerPairing;
    private PairingSummaryItem? selectedPseudo3dUpperPairing;
    private bool pseudo3dEnabled;
    private int pseudo3dDisplayLayers = 5;
    private double pseudo3dNormalizedHeight = 2.0;
    private int pseudo3dMaximumPairSkewMilliseconds = 1000;
    private ImageSource? realtimePseudo3dImageSource;
    private string realtimePseudo3dStatus = "2.5D：未启用。";
    private string realtimePseudo3dProvenance = "显示层由两套独立二维重建沿 z 线性插值；不是真实 3D CEM 反演。";

    public PairingSummaryItem? SelectedPseudo3dLowerPairing
    {
        get => selectedPseudo3dLowerPairing;
        set
        {
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
            if (!SetProperty(ref pseudo3dEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Realtime2dImageVisibility));
            OnPropertyChanged(nameof(RealtimePseudo3dVisibility));
            UpdatePseudo3dConfiguration();
        }
    }

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

    public int Pseudo3dMaximumPairSkewMilliseconds
    {
        get => pseudo3dMaximumPairSkewMilliseconds;
        set
        {
            if (SetProperty(ref pseudo3dMaximumPairSkewMilliseconds, Math.Clamp(value, 1, 60000)))
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
        SynchronizePseudo3dSelections();
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
            TimeSpan.FromMilliseconds(Pseudo3dMaximumPairSkewMilliseconds)));
    }

    private void ApplyPseudo3dPresentation(Pseudo3dVisualizationPresentation presentation)
    {
        RealtimePseudo3dImageSource = presentation.Image;
        RealtimePseudo3dStatus = presentation.Status;
        RealtimePseudo3dProvenance = presentation.Provenance;
    }
}
