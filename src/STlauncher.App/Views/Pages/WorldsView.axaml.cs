using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The worlds tab of the builds page. The view owns the two system dialogs - where to
/// save an exported world, which archive to import - and hands the chosen path to the
/// view model, which does the work.
/// </summary>
public partial class WorldsView : UserControl
{
    public WorldsView()
    {
        InitializeComponent();
    }

    private static FilePickerFileType ZipType() =>
        new(MainWindowViewModel.Localize("Worlds_ZipType", "World archive (zip)"))
        {
            Patterns = new[] { "*.zip" }
        };

    /// <summary>The player chooses where the zip goes; nothing is written before that.</summary>
    private async void OnExportWorldClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel ||
            (sender as Control)?.DataContext is not WorldItem world)
        {
            return;
        }

        // The picker and the export are both fallible; an async void handler with no
        // try/catch turns any of that into an unhandled exception.
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;

            if (storage is null)
            {
                return;
            }

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var suggested = new string(world.Name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = MainWindowViewModel.Localize("Worlds_ExportTitle", "Save the world as an archive"),
                SuggestedFileName = (suggested.Length == 0 ? world.FolderName : suggested) + ".zip",
                DefaultExtension = "zip",
                FileTypeChoices = new[] { ZipType() }
            });

            var path = file?.TryGetLocalPath();

            if (!string.IsNullOrEmpty(path))
            {
                await viewModel.ExportWorldAsync(world, path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
        }
    }

    private async void OnImportWorldClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;

            if (storage is null)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = MainWindowViewModel.Localize("Worlds_ImportTitle", "Choose an archive with a world"),
                AllowMultiple = false,
                FileTypeFilter = new[] { ZipType() }
            });

            var path = files.FirstOrDefault()?.TryGetLocalPath();

            if (!string.IsNullOrEmpty(path))
            {
                await viewModel.ImportWorldAsync(path);
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportUiFailure(ex);
        }
    }
}
