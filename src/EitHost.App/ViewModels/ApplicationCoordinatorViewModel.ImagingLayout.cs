using System.Reflection;
using System.Windows;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    private bool showRealtimeSettings;
    private bool showRealtimeAuxiliary = true;
    private bool showRealtimeHistory = true;
    private bool showRealtimeDiagnostics;

    public string ApplicationVersionLabel => "· EitHost v" +
        (typeof(ApplicationCoordinatorViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown");

    public bool ShowRealtimeSettings
    {
        get => showRealtimeSettings;
        set
        {
            if (!SetProperty(ref showRealtimeSettings, value)) return;
            OnPropertyChanged(nameof(RealtimeSettingsWidth));
            OnPropertyChanged(nameof(RealtimeSettingsVisibility));
        }
    }
    public bool ShowRealtimeAuxiliary
    {
        get => showRealtimeAuxiliary;
        set
        {
            if (!SetProperty(ref showRealtimeAuxiliary, value)) return;
            OnPropertyChanged(nameof(RealtimeAuxiliaryWidth));
            OnPropertyChanged(nameof(RealtimeAuxiliaryVisibility));
        }
    }
    public bool ShowRealtimeHistory
    {
        get => showRealtimeHistory;
        set
        {
            if (!SetProperty(ref showRealtimeHistory, value)) return;
            OnPropertyChanged(nameof(RealtimeHistoryWidth));
            OnPropertyChanged(nameof(RealtimeHistoryVisibility));
        }
    }
    public bool ShowRealtimeDiagnostics
    {
        get => showRealtimeDiagnostics;
        set
        {
            if (!SetProperty(ref showRealtimeDiagnostics, value)) return;
            OnPropertyChanged(nameof(RealtimeDiagnosticsWidth));
            OnPropertyChanged(nameof(RealtimeDiagnosticsVisibility));
        }
    }
    public GridLength RealtimeSettingsWidth => new(ShowRealtimeSettings ? 320 : 0);
    public GridLength RealtimeHistoryWidth => new(ShowRealtimeHistory ? 288 : 0);
    public GridLength RealtimeDiagnosticsWidth => new(ShowRealtimeDiagnostics ? 230 : 0);
    public GridLength RealtimeAuxiliaryWidth => ShowRealtimeAuxiliary ? new(1, GridUnitType.Star) : new(0);
    public Visibility RealtimeAuxiliaryVisibility => ShowRealtimeAuxiliary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RealtimeSettingsVisibility => ShowRealtimeSettings ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RealtimeHistoryVisibility => ShowRealtimeHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RealtimeDiagnosticsVisibility => ShowRealtimeDiagnostics ? Visibility.Visible : Visibility.Collapsed;

    internal void UpdatePseudo3dRenderSize(int pixelSize) => pseudo3dVisualization.UpdateRenderSize(pixelSize);

    private async Task DrainImagingPersistenceAsync()
    {
        await pseudo3dVisualization.DisposeAsync().ConfigureAwait(false);
        await derivedPersistence.DisposeAsync().ConfigureAwait(false);
    }

    private void RequestStorageStop(Guid runId) => PostToUi(() =>
    {
        foreach (var run in realtimeSessions.States.Where(run => run.Config?.ImagingRunId == runId)) run.RequestStop();
    });
}
