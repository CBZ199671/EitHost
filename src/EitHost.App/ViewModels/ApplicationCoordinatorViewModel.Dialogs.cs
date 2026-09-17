using System.IO;
using System.Windows;
using EitHost.App.Localization;

namespace EitHost.App.ViewModels;

public partial class ApplicationCoordinatorViewModel
{
    // Native dialogs sit outside the visual tree, so the language controller never
    // reaches their text; each one is localized explicitly through UiLanguageContext.

    private static bool ConfirmExperimentLifecycle(string title, string message)
    {
        return MessageBox.Show(
                   UiLanguageContext.Localize(message),
                   UiLanguageContext.Localize(title),
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private string? PromptOpenRealtimeBackendDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = UiLanguageContext.Localize("选择 WSL2 中的 PyEIDORS 安装根目录（推荐 ~/apps/PyEIDORS）")
        };
        var initialDirectory = realtimeBackend.InitialDirectory;
        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private string? PromptOpenHdf5(string currentPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = UiLanguageContext.Localize("HDF5 文件 (*.h5;*.hdf5)|*.h5;*.hdf5|所有文件 (*.*)|*.*"),
            CheckFileExists = true,
        };
        ApplyDialogStartLocation(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? PromptSaveFile(string currentPath, string filter, string defaultExtension)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = UiLanguageContext.Localize(filter),
            DefaultExt = defaultExtension,
            OverwritePrompt = true,
        };
        ApplyDialogStartLocation(dialog, currentPath);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ApplyDialogStartLocation(Microsoft.Win32.FileDialog dialog, string currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var directory = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }

            dialog.FileName = Path.GetFileName(currentPath);
        }
        else if (Directory.Exists(DataRootPath))
        {
            dialog.InitialDirectory = DataRootPath;
        }
    }
}
