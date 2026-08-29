using System.Windows.Threading;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class BufferedAcquisitionPreviewPump : IDisposable
{
    private readonly AcquisitionSessionController acquisitionController;
    private readonly Func<Dispatcher?> dispatcherProvider;
    private readonly Func<string?> preferredSetLabelProvider;
    private readonly Func<int> maxValueCountProvider;
    private readonly Func<BufferedAcquisitionPreviewData, RealtimeRawPreviewSnapshot> snapshotFactory;
    private readonly Action<string, RealtimeRawPreviewSnapshot> publish;
    private readonly TimeSpan interval;
    private DispatcherTimer? timer;
    private bool disposed;

    internal BufferedAcquisitionPreviewPump(
        AcquisitionSessionController acquisitionController,
        Func<Dispatcher?> dispatcherProvider,
        Func<string?> preferredSetLabelProvider,
        Func<int> maxValueCountProvider,
        Func<BufferedAcquisitionPreviewData, RealtimeRawPreviewSnapshot> snapshotFactory,
        Action<string, RealtimeRawPreviewSnapshot> publish,
        TimeSpan interval)
    {
        this.acquisitionController = acquisitionController ?? throw new ArgumentNullException(nameof(acquisitionController));
        this.dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));
        this.preferredSetLabelProvider = preferredSetLabelProvider ?? throw new ArgumentNullException(nameof(preferredSetLabelProvider));
        this.maxValueCountProvider = maxValueCountProvider ?? throw new ArgumentNullException(nameof(maxValueCountProvider));
        this.snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.interval = interval;
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var dispatcher = dispatcherProvider();
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Start, DispatcherPriority.Normal);
            return;
        }

        timer ??= CreateTimer();
        if (!timer.IsEnabled)
        {
            timer.Start();
        }
    }

    internal void StopIfIdle()
    {
        var dispatcher = dispatcherProvider();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(StopIfIdle, DispatcherPriority.Normal);
            return;
        }

        if (acquisitionController.ActiveCount == 0)
        {
            timer?.Stop();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer?.Stop();
        timer = null;
    }

    private DispatcherTimer CreateTimer()
    {
        var next = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = interval
        };
        next.Tick += (_, _) => Refresh();
        return next;
    }

    private void Refresh()
    {
        if (acquisitionController.ActiveCount == 0)
        {
            timer?.Stop();
            return;
        }

        if (!acquisitionController.TryGetRecentPreview(
                preferredSetLabelProvider(),
                maxValueCountProvider(),
                out var preview))
        {
            return;
        }

        publish(preview.SetLabel, snapshotFactory(preview));
    }
}
