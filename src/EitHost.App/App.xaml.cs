using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using EitHost.App.ViewModels;
using EitHost.Core.Storage.Catalog;

namespace EitHost.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private DataRootSingleInstanceLease? singleInstanceLease;
    private int unhandledDialogActive;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            WindowsAppIdentity.Apply();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法初始化 Windows 应用身份，程序将退出。\n\n{exception.Message}",
                "EIT 工作站启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        DataRootLayout dataLayout;
        try
        {
            dataLayout = DataRootLayout.Create();
            singleInstanceLease = DataRootSingleInstanceLease.TryAcquire(dataLayout.RootPath);
            if (singleInstanceLease is null)
            {
                MessageBox.Show(
                    $"数据目录已有一个 EIT 工作站实例正在运行。\n\n{dataLayout.RootPath}",
                    "EIT 工作站已在运行",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(-1);
                return;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"无法锁定统一数据目录，程序将退出。\n\n{exception.Message}",
                "EIT 工作站启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);

        MainWindowViewModel? viewModel = null;
        try
        {
            viewModel = new MainWindowViewModel(dataLayout);
            singleInstanceLease.ValidateDataRoot(viewModel.DataRootPath);
            var window = new MainWindow(viewModel);
            EventHandler? firstRenderHandler = null;
            firstRenderHandler = async (_, _) =>
            {
                window.ContentRendered -= firstRenderHandler;
                await viewModel.InitializeAfterFirstRenderAsync().ConfigureAwait(true);
            };
            window.ContentRendered += firstRenderHandler;
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            viewModel?.Dispose();
            singleInstanceLease?.Dispose();
            singleInstanceLease = null;
            MessageBox.Show(
                $"无法创建工作站主窗口，程序将退出。\n\n{exception.Message}",
                "EIT 工作站启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
        singleInstanceLease?.Dispose();
        singleInstanceLease = null;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        GlobalExceptionDiagnostics.Record("dispatcher", e.Exception);
        if (GlobalExceptionDiagnostics.IsFatal(e.Exception))
        {
            return;
        }

        e.Handled = true;
        if (Interlocked.Exchange(ref unhandledDialogActive, 1) != 0)
        {
            return;
        }

        try
        {
            MessageBox.Show(
                $"工作站拦截到未处理异常，本次操作可能未完成。请先停止实验并核查当前数据。\n\n{e.Exception.Message}",
                "EIT 工作站运行异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // The persistent diagnostic is the final fallback if UI reporting fails.
        }
        finally
        {
            Interlocked.Exchange(ref unhandledDialogActive, 0);
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        GlobalExceptionDiagnostics.Record("unobserved-task", e.Exception);
        e.SetObserved();
    }

    private static void OnAppDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        GlobalExceptionDiagnostics.Record(
            "app-domain",
            e.ExceptionObject,
            terminating: e.IsTerminating);
    }
}

