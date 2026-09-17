using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views.Pages;

public partial class BuildsPage : UserControl
{
    public BuildsPage()
    {
        InitializeComponent();
    }

    private async void OnImportModpackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a modpack",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Modrinth modpack") { Patterns = new[] { "*.mrpack" } }
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            await viewModel.ImportModpackAsync(path);
        }
    }
}
