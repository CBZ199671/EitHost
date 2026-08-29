using System.Windows.Threading;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed class RealtimePreviewPump(
    Func<Dispatcher?> getDispatcher,
    Func<bool> hasActiveSets,
    Action flush,
    Action<string> diagnostic,
    TimeSpan interval) : IDisposable
{
    private DispatcherTimer? timer;
    private int flushScheduled;
    private int running;

    internal void Start()
    {
        var dispatcher = getDispatcher();
        if (dispatcher is null)
        {
            diagnostic("preview pump skipped: no UI dispatcher");
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Start, DispatcherPriority.Normal);
            return;
        }

        Interlocked.Exchange(ref flushScheduled, 0);
        timer ??= CreateTimer();
        if (!timer.IsEnabled)
        {
            timer.Start();
        }

        Interlocked.Exchange(ref running, 1);
        diagnostic("preview pump ready");
    }

    internal void Stop()
    {
        var dispatcher = getDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Stop, DispatcherPriority.Normal);
            return;
        }

        if (hasActiveSets())
        {
            return;
        }

        StopCore(flushPending: true);
    }

    internal void RequestFlush()
    {
        var dispatcher = getDispatcher();
        if (dispatcher is null)
        {
            return;
        }

        var alreadyPending = Interlocked.Exchange(ref flushScheduled, 1) != 0;
        if (Volatile.Read(ref running) == 0 && !alreadyPending)
        {
            dispatcher.BeginInvoke(() =>
            {
                if (Interlocked.Exchange(ref flushScheduled, 0) != 0)
                {
                    flush();
                }
            }, DispatcherPriority.Render);
        }
    }

    public void Dispose()
    {
        var dispatcher = getDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => StopCore(flushPending: false));
            return;
        }

        StopCore(flushPending: false);
    }

    private DispatcherTimer CreateTimer()
    {
        var created = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = interval
        };
        created.Tick += (_, _) =>
        {
            if (Interlocked.Exchange(ref flushScheduled, 0) != 0)
            {
                flush();
            }
        };
        return created;
    }

    private void StopCore(bool flushPending)
    {
        timer?.Stop();
        Interlocked.Exchange(ref running, 0);
        if (Interlocked.Exchange(ref flushScheduled, 0) != 0 && flushPending)
        {
            flush();
            diagnostic("preview pump stopped with pending flush");
            return;
        }

        diagnostic("preview pump stopped");
    }
}
