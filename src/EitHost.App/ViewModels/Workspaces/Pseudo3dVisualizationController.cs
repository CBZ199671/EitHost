using System.IO;
using System.Windows.Media;
using EitHost.Core.Concurrency;
using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record Pseudo3dVisualizationOptions(
    bool Enabled,
    string? LowerSetLabel,
    string? UpperSetLabel,
    int DisplayLayers,
    double NormalizedHeight,
    TimeSpan MaximumPairSkew);

internal sealed record Pseudo3dVisualizationPresentation(
    ImageSource? Image,
    string Status,
    string Provenance);

internal sealed class Pseudo3dVisualizationController : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, LayeredPseudo3dSource> latestBySet =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LatestOnlyAsyncWorker<Pseudo3dComposeWorkItem> worker;
    private readonly Action<Pseudo3dVisualizationPresentation> publish;
    private readonly Action<string> diagnostic;
    private Pseudo3dVisualizationOptions options = new(false, null, null, 5, 2.0, TimeSpan.FromSeconds(1));
    private string lastUnavailableStatus = string.Empty;
    private bool disposed;

    internal Pseudo3dVisualizationController(
        Action<Pseudo3dVisualizationPresentation> publish,
        Action<string> diagnostic)
    {
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        worker = new LatestOnlyAsyncWorker<Pseudo3dComposeWorkItem>(
            ProcessAsync,
            ex => this.diagnostic($"2.5D visualization worker failed: {ex}"));
    }

    internal long ReplacedWorkCount => worker.ReplacedCount;

    internal void UpdateOptions(Pseudo3dVisualizationOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            ThrowIfDisposed();
            options = value;
            lastUnavailableStatus = string.Empty;
        }

        if (!value.Enabled)
        {
            publish(new Pseudo3dVisualizationPresentation(
                null,
                "2.5D：未启用。",
                "显示层由两套独立二维重建沿 z 线性插值；不是真实 3D CEM 反演。"));
            return;
        }

        TrySchedule();
    }

    internal void PublishLayer(
        string setLabel,
        RealtimeReconstructionResult result,
        DateTimeOffset acquiredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentNullException.ThrowIfNull(result);
        var snapshot = new LayeredPseudo3dSource(setLabel, acquiredAt, result);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            latestBySet[setLabel] = snapshot;
        }

        TrySchedule();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            latestBySet.Clear();
        }

        worker.Cancel();
    }

    private void TrySchedule()
    {
        Pseudo3dVisualizationOptions current;
        LayeredPseudo3dSource? lower = null;
        LayeredPseudo3dSource? upper = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            current = options;
            if (!current.Enabled)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(current.LowerSetLabel))
            {
                latestBySet.TryGetValue(current.LowerSetLabel, out lower);
            }

            if (!string.IsNullOrWhiteSpace(current.UpperSetLabel))
            {
                latestBySet.TryGetValue(current.UpperSetLabel, out upper);
            }
        }

        if (string.IsNullOrWhiteSpace(current.LowerSetLabel) ||
            string.IsNullOrWhiteSpace(current.UpperSetLabel))
        {
            PublishUnavailable("2.5D：请选择下层和上层设备。");
            return;
        }

        if (string.Equals(current.LowerSetLabel, current.UpperSetLabel, StringComparison.OrdinalIgnoreCase))
        {
            PublishUnavailable("2.5D：下层和上层必须选择不同设备。");
            return;
        }

        if (lower is null || upper is null)
        {
            var missing = lower is null ? current.LowerSetLabel : current.UpperSetLabel;
            PublishUnavailable($"2.5D：等待有效二维重建帧 · 设备={missing}。");
            return;
        }

        var skew = (upper.AcquiredAt - lower.AcquiredAt).Duration();
        if (skew > current.MaximumPairSkew)
        {
            PublishUnavailable(
                $"2.5D：等待同步帧 · Δt={skew.TotalMilliseconds:F0} ms > {current.MaximumPairSkew.TotalMilliseconds:F0} ms。");
            return;
        }

        if (!worker.TryPost(new Pseudo3dComposeWorkItem(lower, upper, current)))
        {
            diagnostic("2.5D visualization rejected a frame after worker shutdown.");
        }
    }

    private ValueTask ProcessAsync(Pseudo3dComposeWorkItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var lower = CloneSource(item.Lower);
            var upper = CloneSource(item.Upper);
            cancellationToken.ThrowIfCancellationRequested();
            var volume = LayeredPseudo3dInterpolator.Interpolate(
                lower,
                upper,
                item.Options.DisplayLayers,
                item.Options.NormalizedHeight);
            cancellationToken.ThrowIfCancellationRequested();
            var image = Pseudo3dVisualizationRenderer.Render(volume);
            var status =
                $"2.5D：{volume.LowerSetLabel} → {volume.UpperSetLabel} · {volume.DisplayLayerCount} 层 · " +
                $"Δt={volume.PairSkew.TotalMilliseconds:F0} ms · h={volume.NormalizedHeight:G4} 相对单位";
            var provenance =
                $"{volume.Algorithm} · {volume.ReconstructionScaleStatus} · " +
                $"{volume.ReconstructionScaleProvenance} · 显示插值，非真实 3D CEM 反演";
            publish(new Pseudo3dVisualizationPresentation(image, status, provenance));
            lock (gate)
            {
                lastUnavailableStatus = string.Empty;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or OverflowException)
        {
            diagnostic($"2.5D composition unavailable: {ex.Message}");

            // The interpolator reports its reason in English. Keep that exact
            // detail, but lead with a localized category so the status line
            // still reads in the operator's own language.
            publish(new Pseudo3dVisualizationPresentation(
                null,
                $"2.5D：不可用 · {DescribeFailureCategory(ex)} · {ex.Message}",
                "未生成插值体；两套二维重建结果保持不变。"));
        }

        return ValueTask.CompletedTask;
    }

    private static string DescribeFailureCategory(Exception ex) => ex switch
    {
        ArgumentOutOfRangeException => "显示参数超出范围",
        ArgumentException => "上下层选择无效",
        OverflowException => "网格规模溢出",
        _ => "两层输入不匹配"
    };

    private static LayeredPseudo3dSource CloneSource(LayeredPseudo3dSource source) =>
        source with
        {
            Result = source.Result with
            {
                Conductivity = (double[])source.Result.Conductivity.Clone(),
                NodeCoords = (double[,])source.Result.NodeCoords.Clone(),
                CellConnectivity = (int[,])source.Result.CellConnectivity.Clone()
            }
        };

    private void PublishUnavailable(string status)
    {
        lock (gate)
        {
            if (string.Equals(lastUnavailableStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            lastUnavailableStatus = status;
        }

        publish(new Pseudo3dVisualizationPresentation(
            null,
            status,
            "显示层由两套独立二维重建沿 z 线性插值；不填造跨层观测，不是真实 3D CEM 反演。"));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record Pseudo3dComposeWorkItem(
        LayeredPseudo3dSource Lower,
        LayeredPseudo3dSource Upper,
        Pseudo3dVisualizationOptions Options);
}
