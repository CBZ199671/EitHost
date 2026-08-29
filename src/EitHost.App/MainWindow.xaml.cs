using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using EitHost.App.Localization;
using EitHost.App.ViewModels;

namespace EitHost.App;

public partial class MainWindow : Window
{
    private const int DwmWindowAttributeBorderColor = 34;
    private const int DwmWindowAttributeWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private static readonly TimeSpan MainWindowShutdownWait = TimeSpan.FromMilliseconds(1000);

    private readonly MainWindowViewModel viewModel;
    private readonly WindowLanguageController languageController;
    private IDisposable? windowIdentityRegistration;
    private Canvas? roiCapturedCanvas;
    private bool closeAfterShutdown;
    private bool shutdownStarted;

    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (Application.Current is null)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/EitHost.App;component/Themes/EitHostTheme.xaml",
                    UriKind.Absolute)
            });
        }

        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        languageController = new WindowLanguageController(this);
        UpdateLanguageMenuSelection();
        ShowPage(NavRealtime);
        Closing += MainWindow_Closing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DisableWindows11AccentBorder();
        EnableRoundedCorners();

        var hwnd = new WindowInteropHelper(this).Handle;
        windowIdentityRegistration = WindowsAppIdentity.ApplyToWindow(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        FitInitialBoundsToWorkArea(hwnd);
    }

    internal static Size ConstrainInitialWindowSize(Size requested, Size minimum, Size workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea), "Work area must be positive.");
        }

        var minimumWidth = Math.Min(minimum.Width, workArea.Width);
        var minimumHeight = Math.Min(minimum.Height, workArea.Height);
        return new Size(
            Math.Clamp(requested.Width, minimumWidth, workArea.Width),
            Math.Clamp(requested.Height, minimumHeight, workArea.Height));
    }

    private void FitInitialBoundsToWorkArea(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)
            || HwndSource.FromHwnd(hwnd)?.CompositionTarget is not { } compositionTarget)
        {
            return;
        }

        var work = monitorInfo.rcWork;
        var topLeft = compositionTarget.TransformFromDevice.Transform(
            new System.Windows.Point(work.left, work.top));
        var bottomRight = compositionTarget.TransformFromDevice.Transform(
            new System.Windows.Point(work.right, work.bottom));
        var workArea = new Size(
            Math.Max(1.0, bottomRight.X - topLeft.X),
            Math.Max(1.0, bottomRight.Y - topLeft.Y));
        var constrained = ConstrainInitialWindowSize(
            new Size(Width, Height),
            new Size(MinWidth, MinHeight),
            workArea);

        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = constrained.Width;
        Height = constrained.Height;
        Left = topLeft.X + ((workArea.Width - Width) / 2.0);
        Top = topLeft.Y + ((workArea.Height - Height) / 2.0);
    }

    // Ask the DWM to round the window corners (Windows 11 22000+). Maximized
    // windows fall back to square automatically.
    private void EnableRoundedCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var preference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowAttributeWindowCornerPreference, ref preference, Marshal.SizeOf<int>());
    }

    // Keep a borderless (WindowStyle=None) maximized window inside the monitor
    // work area so it never covers the taskbar or clips content off-screen.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg == WmGetMinMaxInfo)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                    var work = monitorInfo.rcWork;
                    var area = monitorInfo.rcMonitor;
                    minMax.ptMaxPosition.X = work.left - area.left;
                    minMax.ptMaxPosition.Y = work.top - area.top;
                    minMax.ptMaxSize.X = work.right - work.left;
                    minMax.ptMaxSize.Y = work.bottom - work.top;
                    var compositionTarget = HwndSource.FromHwnd(hwnd)?.CompositionTarget;
                    var scaleX = compositionTarget?.TransformToDevice.M11 ?? 1.0;
                    var scaleY = compositionTarget?.TransformToDevice.M22 ?? 1.0;
                    minMax.ptMinTrackSize.X = Math.Min(
                        (int)Math.Ceiling(MinWidth * scaleX),
                        work.right - work.left);
                    minMax.ptMinTrackSize.Y = Math.Min(
                        (int)Math.Ceiling(MinHeight * scaleY),
                        work.bottom - work.top);
                    Marshal.StructureToPtr(minMax, lParam, true);
                }
            }
        }

        return IntPtr.Zero;
    }

    private void DisableWindows11AccentBorder()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowAttributeBorderColor, ref borderColor, Marshal.SizeOf<int>());
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closeAfterShutdown)
        {
            return;
        }

        e.Cancel = true;
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        IsEnabled = false;
        windowIdentityRegistration?.Dispose();
        windowIdentityRegistration = null;
        Hide();
        _ = Task.Run(ShutdownAndExitProcessAsync);
        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ShutdownAndExitProcessAsync()
    {
        try
        {
            await viewModel.ShutdownAsync(MainWindowShutdownWait)
                .WaitAsync(MainWindowShutdownWait + TimeSpan.FromMilliseconds(250))
                .ConfigureAwait(false);
        }
        catch
        {
            viewModel.Dispose();
        }
        finally
        {
            closeAfterShutdown = true;
            Environment.Exit(0);
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!ArePagesInitialized())
        {
            return;
        }

        if (sender is RadioButton radioButton)
        {
            ShowPage(radioButton);
        }
    }

    private void GuideToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton activeToggle || activeToggle.IsChecked != true)
        {
            return;
        }

        foreach (var toggle in new[] { GuideConnectToggle, GuideSettingsToggle, GuideAcquireToggle })
        {
            if (!ReferenceEquals(toggle, activeToggle))
            {
                toggle.IsChecked = false;
            }
        }
    }

    /// <summary>
    /// Reports the space the conductivity surface actually occupies so the renderer rasterises at
    /// that resolution instead of upscaling a fixed square. The Viewbox sizes itself from its
    /// parent, so feeding its measurement back cannot drive a layout loop.
    /// </summary>
    private void ImageSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement surface)
        {
            return;
        }

        viewModel.VisualizationWorkspace.ApplyImageSurfaceSize(
            Math.Min(surface.ActualWidth, surface.ActualHeight));
    }

    private void RoiCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas)
        {
            return;
        }

        roiCapturedCanvas = canvas;
        canvas.CaptureMouse();
        UpdateRoiCenterFromCanvas(canvas, e);
        e.Handled = true;
    }

    private void RoiCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (roiCapturedCanvas is not { } canvas || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateRoiCenterFromCanvas(canvas, e);
        e.Handled = true;
    }

    private void RoiCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (roiCapturedCanvas is not { } canvas)
        {
            return;
        }

        UpdateRoiCenterFromCanvas(canvas, e);
        canvas.ReleaseMouseCapture();
        roiCapturedCanvas = null;
        e.Handled = true;
    }

    private void UpdateRoiCenterFromCanvas(Canvas canvas, MouseEventArgs e)
    {
        var point = e.GetPosition(canvas);
        viewModel.SetRoiCenterFromImagePoint(point.X, point.Y, canvas.ActualWidth, canvas.ActualHeight);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal UiLanguage CurrentUiLanguage => languageController.CurrentLanguage;

    internal void SetUiLanguage(UiLanguage language)
    {
        languageController.SetLanguage(language);
        Language = XmlLanguage.GetLanguage(languageController.CurrentCulture.IetfLanguageTag);
        UpdateLanguageMenuSelection();
    }

    private void LanguageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetUiLanguage(ReferenceEquals(sender, EnglishMenuItem)
            ? UiLanguage.English
            : UiLanguage.SimplifiedChinese);
        e.Handled = true;
    }

    private void UpdateLanguageMenuSelection()
    {
        SimplifiedChineseMenuItem.IsChecked = CurrentUiLanguage == UiLanguage.SimplifiedChinese;
        EnglishMenuItem.IsChecked = CurrentUiLanguage == UiLanguage.English;
    }

    private void ShowDashboardMenu_Click(object sender, RoutedEventArgs e)
    {
        NavDashboard.IsChecked = true;
        ShowPage(NavDashboard);
    }

    private void ShowPairingMenu_Click(object sender, RoutedEventArgs e)
    {
        NavPairing.IsChecked = true;
        ShowPage(NavPairing);
    }

    private void ShowControlMenu_Click(object sender, RoutedEventArgs e)
    {
        NavControl.IsChecked = true;
        ShowPage(NavControl);
    }

    private void ShowRealtimeMenu_Click(object sender, RoutedEventArgs e)
    {
        NavRealtime.IsChecked = true;
        ShowPage(NavRealtime);
    }

    private void ShowDataMenu_Click(object sender, RoutedEventArgs e)
    {
        NavData.IsChecked = true;
        ShowPage(NavData);
    }

    private void ShowLogMenu_Click(object sender, RoutedEventArgs e)
    {
        NavLog.IsChecked = true;
        ShowPage(NavLog);
    }

    private void ShowPage(RadioButton selected)
    {
        if (!ArePagesInitialized())
        {
            return;
        }

        ViewDashboard.Visibility = selected == NavDashboard ? Visibility.Visible : Visibility.Collapsed;
        ViewPairing.Visibility = selected == NavPairing ? Visibility.Visible : Visibility.Collapsed;
        ViewControl.Visibility = selected == NavControl ? Visibility.Visible : Visibility.Collapsed;
        ViewRealtime.Visibility = selected == NavRealtime ? Visibility.Visible : Visibility.Collapsed;
        ViewData.Visibility = selected == NavData ? Visibility.Visible : Visibility.Collapsed;
        ViewLog.Visibility = selected == NavLog ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ArePagesInitialized()
    {
        return ViewDashboard is not null
            && ViewPairing is not null
            && ViewControl is not null
            && ViewRealtime is not null
            && ViewData is not null
            && ViewLog is not null;
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    private const int MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }
}
