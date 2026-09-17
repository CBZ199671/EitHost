using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using EitHost.Core.Concurrency;
using EitHost.Core.Reconstruction;
using EitHost.Core.Storage.Catalog;
using EitHost.Core.Storage.Hdf5;

namespace EitHost.App.ViewModels.Workspaces;

public sealed class Pseudo3dReplayViewModel : ObservableObject, IDisposable
{
    private readonly DataRootLayout layout;
    private readonly ExperimentCatalog catalog;
    private readonly Action<Action> postToUi;
    private readonly DispatcherTimer timer;
    private LatestOnlyAsyncWorker<SeekRequest>? worker;
    private Pseudo3dExperimentReplaySource? source;
    private ExperimentRunListItem? selectedItem;
    private long generation;
    private long seekVersion;
    private bool visible;
    private bool loading;
    private bool playing;
    private bool showDeviceReplay;
    private int frameIndex;
    private int committedIndex = -1;
    private string summary = "";
    private string status = "";
    private string lowerLabel = "";
    private string upperLabel = "";
    private string colorScale = "";
    private ImageSource? lowerImage;
    private ImageSource? upperImage;
    private ImageSource? volumeImage;
    private Geometry lowerCurve = Geometry.Empty;
    private Geometry upperCurve = Geometry.Empty;

    public Pseudo3dReplayViewModel(DataRootLayout layout, ExperimentCatalog catalog, Action<Action> postToUi)
    {
        this.layout = layout;
        this.catalog = catalog;
        this.postToUi = postToUi;
        PlayCommand = new RelayCommand(TogglePlayback, () => FrameCount > 0);
        ReloadCommand = new RelayCommand(() => PendingLoad = LoadAsync(selectedItem), () => selectedItem?.Run is not null);
        PreviousCommand = new RelayCommand(() => { Stop(); FrameIndex--; }, () => FrameIndex > 0);
        NextCommand = new RelayCommand(() => { Stop(); FrameIndex++; }, () => FrameIndex < MaxFrameIndex);
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            if (loading) return;
            if (FrameIndex >= MaxFrameIndex) { Stop(); return; }
            FrameIndex++;
        };
    }

    public bool IsVisible { get => visible; private set { if (SetProperty(ref visible, value)) { OnPropertyChanged(nameof(Visibility)); OnPropertyChanged(nameof(ShowDeviceReplay)); } } }
    public bool ShowDeviceReplay { get => !IsVisible || showDeviceReplay; set => SetProperty(ref showDeviceReplay, value); }
    public Visibility Visibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;
    public bool IsLoading { get => loading; private set => SetProperty(ref loading, value); }
    public string Summary { get => summary; private set => SetProperty(ref summary, value); }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string LowerLabel { get => lowerLabel; private set => SetProperty(ref lowerLabel, value); }
    public string UpperLabel { get => upperLabel; private set => SetProperty(ref upperLabel, value); }
    public string ColorScale { get => colorScale; private set => SetProperty(ref colorScale, value); }
    public ImageSource? LowerImage { get => lowerImage; private set => SetProperty(ref lowerImage, value); }
    public ImageSource? UpperImage { get => upperImage; private set => SetProperty(ref upperImage, value); }
    public ImageSource? VolumeImage { get => volumeImage; private set => SetProperty(ref volumeImage, value); }
    public Geometry LowerCurve { get => lowerCurve; private set => SetProperty(ref lowerCurve, value); }
    public Geometry UpperCurve { get => upperCurve; private set => SetProperty(ref upperCurve, value); }
    public int FrameCount => source?.Rounds.Count ?? 0;
    public int MaxFrameIndex => Math.Max(0, FrameCount - 1);
    public string PlayLabel => playing ? "暂停" : "伪三维实时回放";
    public int FrameIndex
    {
        get => frameIndex;
        set
        {
            var next = Math.Clamp(value, 0, MaxFrameIndex);
            if (SetProperty(ref frameIndex, next)) QueueFrame();
        }
    }
    public RelayCommand PlayCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    public void SelectExperiment(ExperimentRunListItem? item) => PendingLoad = LoadAsync(item);

    internal async Task LoadAsync(ExperimentRunListItem? item)
    {
        selectedItem = item;
        var version = Interlocked.Increment(ref generation);
        Stop();
        var oldWorker = worker;
        worker = null;
        oldWorker?.Cancel();
        source = null;
        IsVisible = item?.IsPseudo3d == true;
        IsLoading = false;
        ClearImages();
        UpdateCommands();
        if (oldWorker is not null) await oldWorker.DisposeAsync().ConfigureAwait(true);
        if (version != Volatile.Read(ref generation) || item?.Run is null) return;
        try
        {
            var member = item.Pseudo3dMember ?? await Task.Run(() =>
                catalog.ListPseudo3dMembers().SingleOrDefault(entry => entry.RunId == item.ExperimentRunId)).ConfigureAwait(true);
            if (version != Volatile.Read(ref generation) || member is null) return;
            IsVisible = true;
            Summary = $"伪三维采集组 {member.GroupId:D}";
            Status = "正在加载双设备采集轮次…";
            var loaded = await Task.Run(() => new Pseudo3dExperimentReplaySource(layout, catalog, member.GroupId)).ConfigureAwait(true);
            if (version != Volatile.Read(ref generation)) return;
            source = loaded;
            var lower = loaded.Members.SingleOrDefault(entry => entry.Slot == 0);
            var upper = loaded.Members.SingleOrDefault(entry => entry.Slot == 1);
            Summary = $"伪三维采集组 {member.GroupId:D} · 采集轮次 {loaded.Rounds.Count} · 已保存三维 {loaded.ArchivedFrameCount} · " +
                $"下层 {lower?.SetLabel ?? "缺失"} {loaded.Rounds.Count(round => round.Lower is not null)} 块 / 上层 {upper?.SetLabel ?? "缺失"} {loaded.Rounds.Count(round => round.Upper is not null)} 块";
            worker = new LatestOnlyAsyncWorker<SeekRequest>(RenderAsync);
            frameIndex = 0;
            committedIndex = -1;
            OnPropertyChanged(nameof(FrameIndex));
            UpdateCommands();
            if (FrameCount > 0) QueueFrame();
            else Status = "该伪三维组尚无可回放采集块。";
        }
        catch (Exception ex)
        {
            if (version == Volatile.Read(ref generation))
            {
                IsLoading = false;
                Status = $"伪三维回放不可用：{ex.Message}";
            }
        }
    }

    private void QueueFrame()
    {
        if (source is null || FrameCount == 0 || worker is null) return;
        IsLoading = true;
        Status = $"正在读取第 {FrameIndex + 1}/{FrameCount} 轮…";
        worker.TryPost(new(source, FrameIndex, Volatile.Read(ref generation), Interlocked.Increment(ref seekVersion)));
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    private ValueTask RenderAsync(SeekRequest request, CancellationToken token)
    {
        try
        {
            var frame = request.Source.ReadFrame(request.Index, token);
            if (!IsCurrent(request) || token.IsCancellationRequested) return ValueTask.CompletedTask;
            var scale = frame.Volume?.Metadata is { ColorScaleCenter: { } center, ColorScaleRange: > 0 } metadata
                ? (Center: center, Range: metadata.ColorScaleRange.Value)
                : frame.Volume is { } volume ? ReadScale(volume.Metadata.ColorScale) : null;
            var lower = RenderLayer(frame.Lower, scale);
            var upper = RenderLayer(frame.Upper, scale);
            var three = frame.Volume is { } archived ? Pseudo3dVisualizationRenderer.Render(archived.ToVolume(), 512, scale) : null;
            var lowerVoltage = VoltageCurve(frame.Lower?.Voltage);
            var upperVoltage = VoltageCurve(frame.Upper?.Voltage);
            postToUi(() =>
            {
                if (!IsCurrent(request)) return;
                LowerImage = lower;
                UpperImage = upper;
                VolumeImage = three;
                LowerCurve = lowerVoltage;
                UpperCurve = upperVoltage;
                LowerLabel = DescribeLayer(frame.Lower, "下层");
                UpperLabel = DescribeLayer(frame.Upper, "上层");
                ColorScale = frame.Volume?.Metadata.ColorScale ?? "本轮无三维色标";
                Status = $"轮次 {request.Index + 1}/{request.Source.Rounds.Count} · round {frame.Round} · {frame.Status}";
                IsLoading = false;
                committedIndex = request.Index;
                if (request.Index < request.Source.Rounds.Count - 1)
                {
                    var current = request.Source.Rounds[request.Index];
                    var next = request.Source.Rounds[request.Index + 1];
                    var interval = ((next.Lower?.AcquiredAt ?? next.Upper?.AcquiredAt) -
                        (current.Lower?.AcquiredAt ?? current.Upper?.AcquiredAt))?.TotalMilliseconds ?? 150;
                    timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(interval, 20, 5000));
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            postToUi(() =>
            {
                if (!IsCurrent(request)) return;
                Stop();
                IsLoading = false;
                if (committedIndex >= 0) { frameIndex = committedIndex; OnPropertyChanged(nameof(FrameIndex)); }
                Status = $"读取失败，保留上一完整轮次：{ex.Message}";
            });
        }
        return ValueTask.CompletedTask;
    }

    private bool IsCurrent(SeekRequest request) => request.Generation == Volatile.Read(ref generation) && request.Version == Volatile.Read(ref seekVersion);

    private static ImageSource? RenderLayer(Pseudo3dReplayLayer? layer, (double Center, double Range)? scale)
    {
        if (layer?.Reconstruction is not { } result) return null;
        var raster = new VisualizationRenderer.RealtimeImageRasterCache();
        return scale is { } locked
            ? raster.RenderWithPersistedPresentation(result, "normal", 1, locked.Center, locked.Range, null, 256)
            : raster.Render(result, "normal", 1, null, 256);
    }

    internal static (double Center, double Range)? ReadScale(string label)
    {
        // v1 archives store the locked numeric scale in their invariant-culture label.
        var match = Regex.Match(label, @"^\s*([-+\d.eE]+)\s*←\s*([-+\d.eE]+)\s*→\s*([-+\d.eE]+)");
        if (!match.Success) return null;
        var values = match.Groups.Cast<Group>().Skip(1).Select(group =>
            double.TryParse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        var range = Math.Max(Math.Abs(values[1] - values[0]), Math.Abs(values[2] - values[1]));
        return values.All(double.IsFinite) && range > 0 ? (values[1], range) : null;
    }

    private static Geometry VoltageCurve(double[]? values)
    {
        if (values is not { Length: > 1 } || values.Any(value => !double.IsFinite(value))) return Geometry.Empty;
        var low = values.Min();
        var range = Math.Max(values.Max() - low, 1e-12);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            Point At(int i) => new(5 + i * 490.0 / (values.Length - 1), 95 - (values[i] - low) * 90 / range);
            context.BeginFigure(At(0), false, false);
            for (var i = 1; i < values.Length; i++) context.LineTo(At(i), true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static string DescribeLayer(Pseudo3dReplayLayer? layer, string label) => layer is null
        ? $"{label}成员缺失"
        : $"{label} · {layer.Member.SetLabel} · run {layer.Member.RunId:D}\n{layer.Stamp?.SampleMidpoint.ToLocalTime():HH:mm:ss.fff} · {layer.Status}";

    private void TogglePlayback()
    {
        if (playing) { Stop(); return; }
        if (FrameIndex >= MaxFrameIndex) FrameIndex = 0;
        playing = true;
        OnPropertyChanged(nameof(PlayLabel));
        timer.Start();
    }

    private void Stop() { timer.Stop(); playing = false; OnPropertyChanged(nameof(PlayLabel)); }
    private void ClearImages()
    {
        LowerImage = UpperImage = VolumeImage = null;
        LowerCurve = UpperCurve = Geometry.Empty;
        LowerLabel = UpperLabel = ColorScale = Status = Summary = "";
    }
    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(MaxFrameIndex));
        PlayCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        PreviousCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    public async Task ReleaseExperimentAsync(Guid runId)
    {
        if (source?.Members.Any(member => member.RunId == runId) == true || !PendingLoad.IsCompleted)
            await LoadAsync(null).ConfigureAwait(true);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref generation);
        Stop();
        worker?.Cancel();
        if (worker is { } active) _ = active.DisposeAsync();
    }

    private sealed record SeekRequest(Pseudo3dExperimentReplaySource Source, int Index, long Generation, long Version);
}
