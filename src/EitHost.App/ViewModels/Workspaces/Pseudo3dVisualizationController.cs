using System.IO;
using System.Text.Json;
using System.Windows.Media;
using EitHost.Core.Application.Realtime;
using EitHost.Core.Concurrency;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record Pseudo3dVisualizationOptions(
    bool Enabled,
    string? LowerSetLabel,
    string? UpperSetLabel,
    int DisplayLayers,
    double NormalizedHeight,
    TimeSpan MaximumPairSkew,
    double AxialRangeFactor = 1.0,
    int RenderPixelSize = 512,
    bool RequireTimeDivision = false);

internal sealed record Pseudo3dReconstructionContext(
    ReferenceReconstructionCoordinator State,
    int DynamicGeneration,
    int ReferenceEpoch)
{
    internal bool IsCurrent() =>
        State.IsReconstructionContextCurrent(DynamicGeneration, ReferenceEpoch);
}

internal sealed record Pseudo3dVisualizationPresentation(
    ImageSource? Image,
    string Status,
    string Provenance,
    Pseudo3dReconstructionContext? LowerContext = null,
    Pseudo3dReconstructionContext? UpperContext = null,
    Func<Action, bool>? ConfigurationCommit = null,
    LayeredPseudo3dVolume? Volume = null,
    string Warning = "",
    string ColorScale = "",
    LayeredPseudo3dSource? LowerSource = null,
    LayeredPseudo3dSource? UpperSource = null,
    long ConfigurationGeneration = 0,
    ImageSource? LowerImage = null,
    ImageSource? UpperImage = null,
    (double Center, double Range)? LockedColorScale = null)
{
    internal bool TryCommitIfCurrent(Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (ConfigurationCommit is not null)
        {
            // Source state locks precede the configuration lock throughout this path.
            var accepted = false;
            return (this with { ConfigurationCommit = null }).TryCommitIfCurrent(
                () => accepted = ConfigurationCommit(commit)) && accepted;
        }
        if (LowerContext is null && UpperContext is null)
        {
            commit();
            return true;
        }

        if (LowerContext is null || UpperContext is null)
        {
            var context = LowerContext ?? UpperContext!;
            return context.State.TryCommitReconstructionState(
                context.DynamicGeneration,
                context.ReferenceEpoch,
                commit);
        }

        return ReferenceReconstructionCoordinator.TryCommitReconstructionPair(
            LowerContext.State,
            LowerContext.DynamicGeneration,
            LowerContext.ReferenceEpoch,
            UpperContext.State,
            UpperContext.DynamicGeneration,
            UpperContext.ReferenceEpoch,
            commit);
    }

}

internal sealed class Pseudo3dVisualizationController : IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ContextualPseudo3dSource> latestBySet =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ContextualPseudo3dSource>> historyBySet = new(StringComparer.OrdinalIgnoreCase);
    private readonly LatestOnlyAsyncWorker<Pseudo3dComposeWorkItem> worker;
    private readonly Action<Pseudo3dVisualizationPresentation> publish;
    private readonly Action<string> diagnostic;
    private readonly IPseudo3dKrigingBackend? krigingBackend;
    private readonly Func<Pseudo3dVisualizationPresentation, Task>? archive;
    private readonly Timer validityTimer;
    private readonly Pseudo3dRenderSession renderSession = new();
    private readonly VisualizationRenderer.RealtimeImageRasterCache lowerPreview = new();
    private readonly VisualizationRenderer.RealtimeImageRasterCache upperPreview = new();
    private long configurationGeneration;
    private long sourceSequence;
    private long scheduledLower;
    private long scheduledUpper;
    private long scheduledRound;
    private Guid scheduledSession;
    private long presentationSequence;
    private int? viewportPixelSize;
    private Pseudo3dVisualizationOptions options = new(false, null, null, 5, 2.0, TimeSpan.FromSeconds(1));
    private string lastUnavailableStatus = string.Empty;
    private bool disposed;

    internal Pseudo3dVisualizationController(
        Action<Pseudo3dVisualizationPresentation> publish,
        Action<string> diagnostic,
        IPseudo3dKrigingBackend? krigingBackend = null,
        Func<Pseudo3dVisualizationPresentation, Task>? archive = null)
    {
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        this.krigingBackend = krigingBackend;
        this.archive = archive;
        worker = new LatestOnlyAsyncWorker<Pseudo3dComposeWorkItem>(
            ProcessAsync,
            ex => this.diagnostic($"Pseudo-3D visualization worker failed: {ex}"));
        validityTimer = new Timer(_ => RefreshValidity(), null, 250, 250);
    }

    internal long ReplacedWorkCount => worker.ReplacedCount;
    internal Task CompleteAsync(CancellationToken cancellationToken = default) => worker.CompleteAsync(cancellationToken);

    internal void UpdateRenderSize(int pixelSize)
    {
        lock (gate)
        {
            viewportPixelSize = Math.Clamp(pixelSize, 192, 1024);
            options = options with { RenderPixelSize = viewportPixelSize.Value };
        }
    }

    internal void UpdateOptions(Pseudo3dVisualizationOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            ThrowIfDisposed();
            options = value with { RenderPixelSize = viewportPixelSize ?? value.RenderPixelSize };
            configurationGeneration++;
            scheduledLower = scheduledUpper = 0;
            scheduledRound = 0;
            scheduledSession = Guid.Empty;
            lastUnavailableStatus = string.Empty;
        }

        if (!value.Enabled)
        {
            PublishUnavailable("伪三维：未启用。");
            return;
        }

        PublishUnavailable("伪三维：等待新配置的有效结果。");
        TrySchedule();
    }

    internal void PublishLayer(
        string setLabel,
        RealtimeReconstructionResult result,
        DateTimeOffset acquiredAt) =>
        PublishLayerCore(setLabel, result, acquiredAt, context: null);

    internal void ClearSources()
    {
        lock (gate)
        {
            latestBySet.Clear();
            historyBySet.Clear();
            scheduledLower = scheduledUpper = scheduledRound = 0;
            scheduledSession = Guid.Empty;
            configurationGeneration++;
            lastUnavailableStatus = string.Empty;
        }
        PublishUnavailable("伪三维：等待同频分时组的同轮结果。");
    }

    internal void PublishLayer(
        string setLabel,
        RealtimeReconstructionResult result,
        DateTimeOffset acquiredAt,
        ReferenceReconstructionCoordinator state,
        int dynamicGeneration,
        int referenceEpoch)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = new Pseudo3dReconstructionContext(state, dynamicGeneration, referenceEpoch);
        if (!context.IsCurrent())
        {
            return;
        }

        PublishLayerCore(setLabel, result, acquiredAt, context);
    }

    private void PublishLayerCore(
        string setLabel,
        RealtimeReconstructionResult result,
        DateTimeOffset acquiredAt,
        Pseudo3dReconstructionContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setLabel);
        ArgumentNullException.ThrowIfNull(result);
        var source = new LayeredPseudo3dSource(setLabel, acquiredAt, result);
        var eligible = false;
        string? orderedMesh = null;
        if (result.TimeDivision is { Slot: 0 or 1 } stamp)
        {
            try
            {
                Pseudo3dTimeDivisionContract.ValidateSource(source, stamp.Slot);
                orderedMesh = ReconstructionMeshFingerprint.Compute(result.NodeCoords, result.CellConnectivity);
                eligible = true;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            {
                diagnostic($"Pseudo-3D source rejected: {exception.Message}");
            }
        }
        var snapshot = new ContextualPseudo3dSource(
            source,
            context,
            Interlocked.Increment(ref sourceSequence),
            Environment.TickCount64, eligible, orderedMesh);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            if (latestBySet.TryGetValue(setLabel, out var previous) &&
                previous.Source.Result.BlockNumber == result.BlockNumber && previous.Context == context)
                return;
            latestBySet[setLabel] = snapshot;
            if (!historyBySet.TryGetValue(setLabel, out var history))
                historyBySet[setLabel] = history = [];
            if (previous?.Source.Result.TimeDivision?.SessionId != result.TimeDivision?.SessionId) history.Clear();
            history.Add(snapshot);
            if (history.Count > 16) history.RemoveAt(0);
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
            historyBySet.Clear();
        }

        worker.Cancel();
        validityTimer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private void TrySchedule()
    {
        Pseudo3dVisualizationOptions current;
        long generation;
        ContextualPseudo3dSource? lower = null;
        ContextualPseudo3dSource? upper = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            current = options;
            generation = configurationGeneration;
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
            PublishUnavailable("伪三维：请选择下层和上层设备。", generation);
            return;
        }

        if (string.Equals(current.LowerSetLabel, current.UpperSetLabel, StringComparison.OrdinalIgnoreCase))
        {
            PublishUnavailable("伪三维：下层和上层必须选择不同设备。", generation);
            return;
        }

        if (lower is null || upper is null)
        {
            var missing = lower is null ? current.LowerSetLabel : current.UpperSetLabel;
            PublishUnavailable($"伪三维：等待有效二维重建帧 · 设备={missing}。", generation);
            return;
        }

        if (!IsCurrent(lower) || !IsCurrent(upper))
        {
            PublishUnavailable("伪三维：来源参考已失效，请等待有效二维重建帧。", generation);
            return;
        }

        if (current.RequireTimeDivision)
        {
            try
            {
                if (!lower.TimeDivisionEligible || !upper.TimeDivisionEligible)
                    throw new InvalidDataException("分时来源网格或采样标记未通过校验。");
                Pseudo3dTimeDivisionContract.ValidateSource(lower.Source, 0);
                Pseudo3dTimeDivisionContract.ValidateSource(upper.Source, 1);
                lock (gate)
                {
                    // Retain bounded history: independent inverse workers may
                    // finish adjacent rounds in a different order.
                    var match = (from a in historyBySet[current.LowerSetLabel]
                                 from b in historyBySet[current.UpperSetLabel]
                                 where a.Sequence > scheduledLower && b.Sequence > scheduledUpper &&
                                     a.Context == lower.Context && b.Context == upper.Context &&
                                     Pseudo3dTimeDivisionContract.IsSameRound(a.Source.Result.TimeDivision, b.Source.Result.TimeDivision) &&
                                     (a.Source.Result.TimeDivision!.SessionId != scheduledSession || a.Source.Result.TimeDivision.Round > scheduledRound)
                                 orderby a.Source.Result.TimeDivision!.Round descending
                                 select (Lower: a, Upper: b)).FirstOrDefault();
                    if (match.Lower is null) return;
                    lower = match.Lower;
                    upper = match.Upper;
                }
                Pseudo3dTimeDivisionContract.ValidatePair(lower.Source, upper.Source);
            }
            catch (InvalidDataException exception)
            {
                PublishUnavailable($"伪三维：不可用 · 两层输入不匹配 · {exception.Message}", generation);
                return;
            }
        }

        var skew = (upper.Source.AcquiredAt - lower.Source.AcquiredAt).Duration();
        if (skew > current.MaximumPairSkew)
        {
            PublishUnavailable(
                $"伪三维：等待同步帧 · Δt={skew.TotalMilliseconds:F0} ms > {current.MaximumPairSkew.TotalMilliseconds:F0} ms。", generation);
            return;
        }

        lock (gate)
        {
            if (disposed || generation != configurationGeneration ||
                lower.Sequence <= scheduledLower || upper.Sequence <= scheduledUpper)
                return;
            scheduledLower = lower.Sequence;
            scheduledUpper = upper.Sequence;
            if (current.RequireTimeDivision)
            {
                scheduledSession = lower.Source.Result.TimeDivision!.SessionId;
                scheduledRound = lower.Source.Result.TimeDivision.Round;
            }
            if (!worker.TryPost(new Pseudo3dComposeWorkItem(lower, upper, current, generation)))
                diagnostic("Pseudo-3D visualization rejected a frame after worker shutdown.");
        }
    }

    private async ValueTask ProcessAsync(Pseudo3dComposeWorkItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWorkCurrent(item))
        {
            return;
        }

        try
        {
            // Accepted reconstruction results are publish-once snapshots. Readers retain them;
            // no array is pooled, mutated or returned while a consumer still owns the frame.
            var lower = item.Lower.Source;
            var upper = item.Upper.Source;
            var request = LayeredPseudo3dInterpolator.CreateKrigingRequest(
                lower, upper, item.Options.DisplayLayers, item.Options.NormalizedHeight,
                item.Options.AxialRangeFactor);
            if (krigingBackend is null)
                throw new NotSupportedException("Pseudo-3D Kriging backend is unavailable.");
            var result = await krigingBackend.InterpolatePseudo3dAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!IsWorkCurrent(item)) return;
            var volume = LayeredPseudo3dInterpolator.ComposeKriging(
                lower, upper, request, result, includeExportVolume: false);
            cancellationToken.ThrowIfCancellationRequested();
            var scaleKey = (item.Generation, item.Lower.Context, item.Upper.Context,
                volume.ReconstructionScaleStatus, volume.ReconstructionScaleProvenance);
            var image = renderSession.Render(volume, scaleKey, item.Options.RenderPixelSize);
            if (!IsWorkCurrent(item)) return;
            var uncertainty = volume.MeanRelativeVariance is { } meanVariance
                ? $" · 相对不确定度均值={meanVariance:F3}" : string.Empty;
            var status =
                $"伪三维：{volume.LowerSetLabel} → {volume.UpperSetLabel} · {volume.DisplayLayerCount} 层 · " +
                $"Δt={volume.PairSkew.TotalMilliseconds:F0} ms · h={volume.NormalizedHeight:G4} 相对单位" + uncertainty;
            if (item.Options.RequireTimeDivision)
                status += $" · 同频分时轮次={lower.Result.TimeDivision!.Round}";
            var provenance =
                $"{volume.Algorithm} · {volume.ReconstructionScaleStatus} · " +
                $"{volume.ReconstructionScaleProvenance} · 仅使用独立二维参数场，不填造跨层电压，非真实 3D CEM 反演" +
                CreateAcceleratorProvenance(volume.AlgorithmProvenance);
            if (item.Options.RequireTimeDivision &&
                (lower.Result.TimeDivision!.TrustedZeroDifference || upper.Result.TimeDivision!.TrustedZeroDifference))
                provenance += $" · 可信零变化层={string.Join(",", new[] { lower, upper }.Where(source => source.Result.TimeDivision!.TrustedZeroDifference).Select(source => source.SetLabel))}";
            var lowConfidence = lower.Result.ImageQualityScore is < 0.65 || upper.Result.ImageQualityScore is < 0.65;
            var presentation = new Pseudo3dVisualizationPresentation(
                image, status, provenance, item.Lower.Context, item.Upper.Context,
                action => TryCommitConfiguration(item.Generation, action), volume,
                lowConfidence ? "低置信度重构 · 图像仅供参考" : string.Empty,
                renderSession.ScaleLabel(volume), lower, upper, item.Generation,
                lowerPreview.Render(lower.Result, "normal", 1.0, null, 192),
                upperPreview.Render(upper.Result, "normal", 1.0, null, 192), renderSession.ColorScale);
            publish(StampPresentation(presentation, item.Generation, item));
            if (archive is not null && IsWorkCurrent(item))
                await archive(presentation).ConfigureAwait(false);
            lock (gate)
            {
                if (item.Generation == configurationGeneration) lastUnavailableStatus = string.Empty;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            diagnostic($"Pseudo-3D composition unavailable: {ex}");
            if (IsWorkCurrent(item))
                PublishUnavailable($"伪三维：不可用 · {DescribeFailureCategory(ex)} · {ex.Message}", item.Generation);
        }
    }
    private static string DescribeFailureCategory(Exception ex) => ex switch
    {
        ArgumentOutOfRangeException => "显示参数超出范围",
        ArgumentException => "上下层选择无效",
        OverflowException => "网格规模溢出",
        InvalidDataException => "两层输入不匹配",
        _ => "后端计算失败"
    };

    internal static string CreateAcceleratorProvenance(string? algorithmProvenance)
    {
        if (string.IsNullOrWhiteSpace(algorithmProvenance))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(algorithmProvenance);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "compute_backend", out var backend))
            {
                return string.Empty;
            }

            _ = TryGetString(root, "accelerator_selection_reason", out var reason);
            var detail = reason switch
            {
                "workload_below_cuda_threshold" => "CPU（小规模任务更快）",
                "accelerator_policy_cpu" => "CPU（策略固定）",
                "cuda_benchmark_speedup_below_threshold" => "CPU（GPU 实测未达门限）",
                "cuda_candidate_unavailable" => "CPU（CUDA 不可用）",
                "cuda_weight_budget_exceeded" => "CPU（设备权重超出缓存预算）",
                "cuda_candidate_failed_cpu_fallback" or
                "cuda_numerical_mismatch_cpu_fallback" or
                "cuda_decision_cache_rejected_cpu_fallback" or
                "cuda_runtime_failure_cpu_fallback" => "CPU（CUDA 已回退）",
                "cuda_benchmark_promoted" => CreateCudaPromotionDetail(root),
                _ when string.Equals(backend, "cuda_torch_batched", StringComparison.Ordinal) => "CUDA",
                _ when string.Equals(backend, "cpu_numpy_vectorized", StringComparison.Ordinal) => "CPU",
                _ => backend
            };

            var fallback = TryGetString(root, "accelerator_fallback_reason", out var fallbackReason)
                ? $" · GPU 原因={Truncate(fallbackReason, 160)}"
                : string.Empty;
            return $" · 插值计算={detail}{fallback}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string CreateCudaPromotionDetail(JsonElement root)
    {
        if (!root.TryGetProperty("accelerator_measured_speedup", out var speedupElement) ||
            speedupElement.ValueKind != JsonValueKind.Number ||
            !speedupElement.TryGetDouble(out var speedup) ||
            !double.IsFinite(speedup) ||
            speedup <= 0.0)
        {
            return "CUDA";
        }

        return $"CUDA（实测速率提升={speedup:F2}×）";
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";

    private static bool IsCurrent(ContextualPseudo3dSource source) =>
        source.Context?.IsCurrent() ?? true;

    private bool IsWorkCurrent(Pseudo3dComposeWorkItem item)
    {
        if (!IsCurrent(item.Lower) || !IsCurrent(item.Upper)) return false;
        lock (gate) return IsPairAvailable(item);
    }

    // Called with gate held. Source-state locks are acquired by the caller first;
    // comparing captured contexts here avoids reversing that lock order at UI commit.
    private bool IsPairAvailable(Pseudo3dComposeWorkItem item)
    {
        if (disposed || !options.Enabled || item.Generation != configurationGeneration ||
            !latestBySet.TryGetValue(item.Lower.Source.SetLabel, out var lower) ||
            !latestBySet.TryGetValue(item.Upper.Source.SetLabel, out var upper) ||
            lower.Context != item.Lower.Context || upper.Context != item.Upper.Context)
            return false;
        var oldest = Math.Min(Math.Min(lower.ReceivedAt, upper.ReceivedAt),
            Math.Min(item.Lower.ReceivedAt, item.Upper.ReceivedAt));
        if (options.RequireTimeDivision)
            return lower.TimeDivisionEligible && upper.TimeDivisionEligible &&
                lower.OrderedMesh == item.Lower.OrderedMesh && upper.OrderedMesh == item.Upper.OrderedMesh &&
                lower.Source.Result.GetMeshIndexMetadata() == item.Lower.Source.Result.GetMeshIndexMetadata() &&
                upper.Source.Result.GetMeshIndexMetadata() == item.Upper.Source.Result.GetMeshIndexMetadata() &&
                Environment.TickCount64 - oldest <= Math.Max(5000, options.MaximumPairSkew.TotalMilliseconds * 3) &&
                lower.Source.Result.TimeDivision?.SessionId == item.Lower.Source.Result.TimeDivision?.SessionId &&
                upper.Source.Result.TimeDivision?.SessionId == item.Upper.Source.Result.TimeDivision?.SessionId &&
                Pseudo3dTimeDivisionContract.IsSameRound(item.Lower.Source.Result.TimeDivision, item.Upper.Source.Result.TimeDivision) &&
                (item.Upper.Source.AcquiredAt - item.Lower.Source.AcquiredAt).Duration() <= options.MaximumPairSkew;
        return Environment.TickCount64 - oldest <= Math.Max(2000, options.MaximumPairSkew.TotalMilliseconds * 2) &&
            (upper.Source.AcquiredAt - lower.Source.AcquiredAt).Duration() <= options.MaximumPairSkew;
    }

    private bool TryCommitConfiguration(long generation, Action commit)
    {
        lock (gate)
        {
            if (disposed || generation != configurationGeneration) return false;
            commit();
            return true;
        }
    }

    private Pseudo3dVisualizationPresentation StampPresentation(Pseudo3dVisualizationPresentation presentation, long generation,
        Pseudo3dComposeWorkItem? item = null)
    {
        long sequence;
        lock (gate)
        {
            if (disposed || generation != configurationGeneration || (item is not null && !IsPairAvailable(item)))
                return presentation with { ConfigurationCommit = _ => false };
            sequence = ++presentationSequence;
        }
        return presentation with
        {
            ConfigurationCommit = action =>
            {
                lock (gate)
                {
                    if (disposed || generation != configurationGeneration || sequence != presentationSequence ||
                        (item is not null && !IsPairAvailable(item))) return false;
                    action();
                    return true;
                }
            }
        };
    }

    internal void RefreshValidity()
    {
        ContextualPseudo3dSource? lower, upper;
        long generation;
        double maxAge;
        lock (gate)
        {
            if (disposed || !options.Enabled) return;
            generation = configurationGeneration;
            latestBySet.TryGetValue(options.LowerSetLabel ?? "", out lower);
            latestBySet.TryGetValue(options.UpperSetLabel ?? "", out upper);
            maxAge = options.RequireTimeDivision ? Math.Max(5000, options.MaximumPairSkew.TotalMilliseconds * 3)
                : Math.Max(2000, options.MaximumPairSkew.TotalMilliseconds * 2);
        }
        if (lower is null || upper is null) return;
        if (!IsCurrent(lower) || !IsCurrent(upper))
            PublishUnavailable("伪三维：来源参考已失效，请等待有效二维重建帧。", generation);
        else if (Environment.TickCount64 - Math.Min(lower.ReceivedAt, upper.ReceivedAt) > maxAge)
            PublishUnavailable("伪三维：来源帧已过期，等待两层更新。", generation);
    }

    private void PublishUnavailable(string status, long? expectedGeneration = null)
    {
        long generation;
        lock (gate)
        {
            if (disposed || (expectedGeneration is { } expected && expected != configurationGeneration)) return;
            generation = configurationGeneration;
            if (string.Equals(lastUnavailableStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            lastUnavailableStatus = status;
        }

        publish(StampPresentation(new Pseudo3dVisualizationPresentation(
            null,
            status,
            "默认由两个独立二维逆问题的成像结果进行质量感知 Kriging 伪三维拟合；不填造跨层观测，非真实 3D CEM 反演。",
            ConfigurationCommit: action => TryCommitConfiguration(generation, action),
            Warning: status), generation));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private sealed record Pseudo3dComposeWorkItem(
        ContextualPseudo3dSource Lower,
        ContextualPseudo3dSource Upper,
        Pseudo3dVisualizationOptions Options,
        long Generation);

    private sealed record ContextualPseudo3dSource(
        LayeredPseudo3dSource Source,
        Pseudo3dReconstructionContext? Context,
        long Sequence,
        long ReceivedAt,
        bool TimeDivisionEligible,
        string? OrderedMesh);
}
