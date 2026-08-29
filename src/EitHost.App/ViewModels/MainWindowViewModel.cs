using EitHost.Core.Diagnostics;
using EitHost.Core.Hardware.Pnp;
using EitHost.Core.Hardware.Usb2070;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App.ViewModels;

/// <summary>
/// Root WPF composition shell. Operational compatibility lives in the application coordinator;
/// durable state and module-owned presentation live in the explicit child workspaces.
/// </summary>
public sealed class MainWindowViewModel : ApplicationCoordinatorViewModel
{
    public MainWindowViewModel()
    {
    }

    internal MainWindowViewModel(
        DataRootLayout dataLayout,
        string? realtimeDiagnosticLogPath = null)
        : base(dataLayout, realtimeDiagnosticLogPath)
    {
    }

    public MainWindowViewModel(
        IUsb2070NativeApi usb2070NativeApi,
        string? dataRootPath = null,
        string? catalogPath = null,
        Func<CancellationToken, Task<HardwareSmokeReport>>? hardwareSmokeCapture = null,
        long? autoFlushByteThreshold = null,
        IRealtimeReconstructionBackend? realtimeReconstructionBackend = null,
        Func<bool>? memoryPressureProbe = null,
        IDataRootStorageService? dataRootStorageService = null,
        Action<string>? directoryOpener = null,
        IExperimentDataLifecycleService? experimentDataLifecycleService = null,
        Func<string, string, bool>? lifecycleConfirmation = null,
        string? legacyApplicationDataPath = null,
        string? realtimeDiagnosticLogPath = null)
        : base(
            usb2070NativeApi,
            dataRootPath,
            catalogPath,
            hardwareSmokeCapture,
            autoFlushByteThreshold,
            realtimeReconstructionBackend,
            memoryPressureProbe,
            dataRootStorageService,
            directoryOpener,
            experimentDataLifecycleService,
            lifecycleConfirmation,
            legacyApplicationDataPath,
            realtimeDiagnosticLogPath)
    {
    }

    internal MainWindowViewModel(PnpInsertionMonitor insertionMonitor)
        : base(insertionMonitor)
    {
    }
}
